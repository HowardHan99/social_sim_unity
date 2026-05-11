using System.Collections;
using UnityEngine;
using SessionReview;

namespace IVI
{
    public class ManualWheelchairController : MonoBehaviour
    {
        [Header("Control Settings")]
        public float moveSpeed = 0.8f;
        public float rotationSpeed = 180f;
        public KeyCode toggleModeKey = KeyCode.RightShift;
        public bool useWASD = true;
        public bool startInManualMode = false;
        public bool enableJoystick = true;
        public string joystickHorizontalAxis = "LogitechTwist";
        public string joystickVerticalAxis = "joystickVerticalAxis";
        public string joystickStartAxis = "L1";
        public string joystickToggleModeAxis = string.Empty;
        public float joystickDeadzone = 0.03f;
        public float joystickStartupDeadzone = 0.03f;
        public float joystickLinearFullThrow = 0.1f;
        public float joystickAngularFullThrow = 1.0f;
        public float joystickLinearSensitivity = 1.0f;
        public float joystickAngularSensitivity = 1.0f;
        public float joystickLinearResponseExponent = 1.6f;
        public float joystickAngularResponseExponent = 1.2f;
        public bool invertJoystickHorizontal = false;
        public bool invertJoystickVertical = true;

        [Header("Manual Brake/Reverse Behavior")]
        public float brakeStopThreshold = 0.02f;
        public int sPressesToEnableReverse = 2;
        public float sPressWindowSec = 0.6f;

        [Header("Manual Smoothing")]
        public float manualAcceleration = 4.0f;
        public float manualDeceleration = 3.0f;
        public float manualAngularAcceleration = 1200f;

        [Header("Debug Manual Brake (read-only)")]
        public int debugSBrakePressCount;

        [Header("Status (read-only)")]
        public bool isManualMode = false;
        public float debugJoystickRawHorizontal;
        public float debugJoystickRawVertical;
        public float debugJoystickHorizontalCenter;
        public float debugJoystickVerticalCenter;
        public float debugJoystickProcessedHorizontal;
        public float debugJoystickProcessedVertical;

        private SFPWDAgent sfpwdAgent;
        private Rigidbody rb;
        private Animator animator;
        private Vector3 manualVelocity;
        private bool initialized = false;
        private bool waitingForStart = false;
        public bool WaitingForStart => waitingForStart;
        private Quaternion spawnRotation;
        private WheelchairCameraSmoothing camSmoothing;
        private bool JoystickPresent => enableJoystick && Input.GetJoystickNames().Length > 0;
        private bool lastJoystickStartPressed;
        private bool lastJoystickTogglePressed;
        private float joystickHorizontalCenter;
        private float joystickVerticalCenter;
        private bool joystickCenterCaptured;
        private bool warnedMissingJoystickAxis;
        private float lastSBrakePressRealtime = -1f;
        private float currentManualLinearSpeed;
        private float currentManualAngularSpeed;

        void Start()
        {
            spawnRotation = transform.rotation;
            ApplyJoystickResponseDefaults();
            sfpwdAgent = GetComponent<SFPWDAgent>();
            rb = GetComponent<Rigidbody>();
            animator = GetComponent<Animator>();
            StartCoroutine(InitAfterBase());
        }

        IEnumerator InitAfterBase()
        {
            yield return null;

            if (animator != null)
                animator.applyRootMotion = false;

            if (rb != null)
                rb.constraints = RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ;

            if (rb != null)
                rb.velocity = Vector3.zero;

            // Restore the exact spawn rotation (the one-frame agent tick may have changed it).
            transform.rotation = spawnRotation;

            camSmoothing = GetComponentInChildren<WheelchairCameraSmoothing>(true);
            if (camSmoothing != null)
                camSmoothing.enabled = true;

            waitingForStart = false;
            initialized = true;
            if (startInManualMode)
                SetManualMode();
            else
                SetAutomaticMode();

            Debug.Log($"[PWD] ManualWheelchairController ready. Starting in {(startInManualMode ? "MANUAL" : "AUTO")} mode.");
        }

        void Update()
        {
            if (!initialized) return;

            if (Input.GetKeyDown(toggleModeKey) || ReadJoystickButtonDown(joystickToggleModeAxis))
            {
                if (isManualMode)
                    SetAutomaticMode();
                else
                    SetManualMode();
            }

            if (isManualMode)
            {
                HandleInput();
                transform.position += manualVelocity * Time.deltaTime;
                UpdateAnimator();
            }
            else if (sfpwdAgent != null && sfpwdAgent.enabled)
            {
                // SFPWDAgent (Base.Update) computes velocity, handles rotation
                // and animator params, but doesn't drive position when root motion
                // is off. Apply the computed velocity as a direct position update.
                Vector3 vel = sfpwdAgent.velocity;
                vel.y = 0f;
                if (vel.sqrMagnitude > 0.001f)
                    transform.position += vel * Time.deltaTime;
            }
        }

        void HandleInput()
        {
            float manualDesiredLin = currentManualLinearSpeed;
            float manualDesiredAng = 0f;

            if (JoystickPresent)
            {
                if (!joystickCenterCaptured)
                    CaptureJoystickCenter();

                float h = ProcessJoystickInput(ReadJoystickAxis(joystickHorizontalAxis, invertJoystickHorizontal, joystickHorizontalCenter), EffectiveJoystickDeadzone(), joystickAngularFullThrow, joystickAngularSensitivity, joystickAngularResponseExponent);
                float v = ProcessJoystickInput(ReadJoystickAxis(joystickVerticalAxis, invertJoystickVertical, joystickVerticalCenter), EffectiveJoystickDeadzone(), joystickLinearFullThrow, joystickLinearSensitivity, joystickLinearResponseExponent);
                debugJoystickProcessedHorizontal = h;
                debugJoystickProcessedVertical = v;
                manualDesiredLin = moveSpeed * v;
                manualDesiredAng = rotationSpeed * h;
            }
            else
            {
                joystickCenterCaptured = false;
                debugJoystickRawHorizontal = 0f;
                debugJoystickRawVertical = 0f;
                debugJoystickHorizontalCenter = 0f;
                debugJoystickVerticalCenter = 0f;
                debugJoystickProcessedHorizontal = 0f;
                debugJoystickProcessedVertical = 0f;
            }

            bool wHeld = Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow);
            bool sHeld = Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow);
            bool sPressed = Input.GetKeyDown(KeyCode.S) || Input.GetKeyDown(KeyCode.DownArrow);
            float nowRealtime = Time.realtimeSinceStartup;

            if (!sHeld &&
                lastSBrakePressRealtime > 0f &&
                nowRealtime - lastSBrakePressRealtime > sPressWindowSec)
            {
                debugSBrakePressCount = 0;
                lastSBrakePressRealtime = -1f;
            }

            if (sPressed)
            {
                if (lastSBrakePressRealtime < 0f ||
                    nowRealtime - lastSBrakePressRealtime > sPressWindowSec)
                {
                    debugSBrakePressCount = 0;
                }

                debugSBrakePressCount++;
                lastSBrakePressRealtime = nowRealtime;
            }

            if (wHeld)
            {
                manualDesiredLin = moveSpeed;
                debugSBrakePressCount = 0;
                lastSBrakePressRealtime = -1f;
            }
            else if (sHeld)
            {
                bool movingForward = currentManualLinearSpeed > brakeStopThreshold;
                bool nearStop = Mathf.Abs(currentManualLinearSpeed) <= brakeStopThreshold;
                bool reverseArmed = debugSBrakePressCount >= Mathf.Max(1, sPressesToEnableReverse);

                if (movingForward)
                {
                    manualDesiredLin = 0f;
                }
                else if (nearStop)
                {
                    manualDesiredLin = reverseArmed ? -moveSpeed : 0f;
                }
                else
                {
                    manualDesiredLin = -moveSpeed;
                }
            }

            if (useWASD)
            {
                if (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow))
                {
                    manualDesiredAng = -rotationSpeed;
                }
                else if (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow))
                {
                    manualDesiredAng = rotationSpeed;
                }
            }

            if (Input.GetKey(KeyCode.H))
            {
                manualDesiredLin = 0f;
                manualDesiredAng = 0f;
                debugSBrakePressCount = 0;
                lastSBrakePressRealtime = -1f;
            }

            float linearStep = Mathf.Abs(manualDesiredLin) > Mathf.Abs(currentManualLinearSpeed)
                ? manualAcceleration
                : manualDeceleration;
            currentManualLinearSpeed = Mathf.MoveTowards(
                currentManualLinearSpeed,
                manualDesiredLin,
                linearStep * Time.deltaTime);
            currentManualAngularSpeed = Mathf.MoveTowards(
                currentManualAngularSpeed,
                manualDesiredAng,
                manualAngularAcceleration * Time.deltaTime);

            float rot = currentManualAngularSpeed * Time.deltaTime;
            if (Mathf.Abs(rot) > 0.001f)
                transform.Rotate(0f, rot, 0f);

            Vector3 fwd = transform.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude > 0.001f) fwd.Normalize();
            manualVelocity = fwd * currentManualLinearSpeed;
        }

        void UpdateAnimator()
        {
            if (animator == null) return;
            float speed = manualVelocity.magnitude;
            Vector3 local = Quaternion.Euler(0, -transform.eulerAngles.y, 0) * manualVelocity;
            animator.SetBool("Idling", speed < 0.1f);
            animator.SetFloat("Forward", local.z / 0.6f);
            animator.SetFloat("Strafe", local.x / 0.6f);
            animator.speed = speed > 0.1f ? speed : 1f;
        }


        void SetManualMode()
        {
            isManualMode = true;
            manualVelocity = Vector3.zero;
            currentManualLinearSpeed = 0f;
            currentManualAngularSpeed = 0f;
            debugSBrakePressCount = 0;
            lastSBrakePressRealtime = -1f;
            CaptureJoystickCenter();

            if (sfpwdAgent != null)
            {
                sfpwdAgent.KillNavigationCoroutine();
                sfpwdAgent.enabled = false;
            }

            if (animator != null)
                animator.applyRootMotion = false;

            if (rb != null)
                rb.velocity = Vector3.zero;

            Debug.Log("[PWD] MANUAL mode");
        }

        void SetAutomaticMode()
        {
            isManualMode = false;
            manualVelocity = Vector3.zero;
            currentManualLinearSpeed = 0f;
            currentManualAngularSpeed = 0f;
            debugSBrakePressCount = 0;
            lastSBrakePressRealtime = -1f;
            joystickCenterCaptured = false;

            // Keep root motion OFF -- ManualWheelchairController drives position
            // directly using the SFPWDAgent's computed velocity.
            if (animator != null)
                animator.applyRootMotion = false;

            if (sfpwdAgent != null)
            {
                sfpwdAgent.enabled = true;
                sfpwdAgent.RestartNavigationCoroutine();
            }

            if (rb != null)
                rb.velocity = Vector3.zero;

            Debug.Log("[PWD] AUTO mode");
        }

        public void ApplyStartupControlMode(bool startManual)
        {
            startInManualMode = startManual;

            if (!initialized)
                return;

            if (waitingForStart)
                return;

            if (startManual)
                SetManualMode();
            else
                SetAutomaticMode();
        }

        void OnGUI()
        {
            if (!initialized) return;

            if (SessionReviewManager.Instance != null && SessionReviewManager.Instance.IsWorldBuildingModeActive)
                return;

            string mode = isManualMode ? "MANUAL" : "AUTO";
            string pos = $"({transform.position.x:F1}, {transform.position.z:F1})";
            string vel = isManualMode
                ? $"{manualVelocity.magnitude:F1}"
                : (sfpwdAgent != null ? $"{sfpwdAgent.velocity.magnitude:F1}" : "--");
            string controlHint = JoystickPresent
                ? "Joystick + keyboard active"
                : "RShift: toggle | W/S brake+reverse | A/D turn | H stop";
            GUI.Box(new Rect(10, 10, 300, 60),
                $"[{mode}] Pos:{pos} Vel:{vel}\n{controlHint}");
        }

        private float ReadJoystickAxis(string axisName, bool invert, float center)
        {
            if (!JoystickPresent || string.IsNullOrWhiteSpace(axisName))
                return 0f;

            float value = ReadJoystickRawAxis(axisName, invert);

            value -= center;
            return Mathf.Abs(value) >= EffectiveJoystickDeadzone() ? value : 0f;
        }

        private void CaptureJoystickCenter()
        {
            if (!JoystickPresent)
            {
                joystickHorizontalCenter = 0f;
                joystickVerticalCenter = 0f;
                joystickCenterCaptured = false;
                return;
            }

            joystickHorizontalCenter = ReadJoystickRawAxis(joystickHorizontalAxis, invertJoystickHorizontal);
            joystickVerticalCenter = ReadJoystickRawAxis(joystickVerticalAxis, invertJoystickVertical);
            debugJoystickHorizontalCenter = joystickHorizontalCenter;
            debugJoystickVerticalCenter = joystickVerticalCenter;
            joystickCenterCaptured = true;
        }

        private float ReadJoystickRawAxis(string axisName, bool invert)
        {
            if (!JoystickPresent || string.IsNullOrWhiteSpace(axisName))
                return 0f;

            axisName = ResolveJoystickAxisName(axisName);
            float value = GetAxisSafely(axisName);
            value = invert ? -value : value;
            if (axisName == ResolveJoystickAxisName(joystickHorizontalAxis))
                debugJoystickRawHorizontal = value;
            if (axisName == ResolveJoystickAxisName(joystickVerticalAxis))
                debugJoystickRawVertical = value;
            return value;
        }

        private string ResolveJoystickAxisName(string axisName)
        {
            if (axisName == "RHorizontal")
                return "LogitechTwist";
            if (axisName == "RVertical")
                return "LogitechThrottle";
            return axisName;
        }

        private float GetAxisSafely(string axisName)
        {
            if (string.IsNullOrWhiteSpace(axisName))
                return 0f;

            try
            {
                return Input.GetAxisRaw(axisName);
            }
            catch (System.ArgumentException)
            {
                if (!warnedMissingJoystickAxis)
                {
                    Debug.LogWarning($"[PWD] Input axis '{axisName}' is not configured. Joystick value forced to 0.", this);
                    warnedMissingJoystickAxis = true;
                }
                return 0f;
            }
        }

        private float EffectiveJoystickDeadzone()
        {
            return Mathf.Max(joystickDeadzone, joystickStartupDeadzone);
        }

        private float ScaleJoystickInput(float value, float sensitivity)
        {
            return Mathf.Clamp(value * Mathf.Max(0f, sensitivity), -1f, 1f);
        }

        private float ProcessJoystickInput(float value, float deadzone, float fullThrow, float sensitivity, float responseExponent)
        {
            float normalized = NormalizeJoystickThrow(ApplyJoystickDeadzone(value, deadzone), fullThrow);
            float curved = ApplyJoystickResponseCurve(normalized, responseExponent);
            return ScaleJoystickInput(curved, sensitivity);
        }

        private float NormalizeJoystickThrow(float value, float fullThrow)
        {
            return Mathf.Clamp(value / Mathf.Max(0.01f, Mathf.Abs(fullThrow)), -1f, 1f);
        }

        private float ApplyJoystickResponseCurve(float value, float responseExponent)
        {
            float exponent = Mathf.Max(0.25f, responseExponent);
            return Mathf.Sign(value) * Mathf.Pow(Mathf.Abs(value), exponent);
        }

        private void ApplyJoystickResponseDefaults()
        {
            if (Mathf.Approximately(joystickLinearFullThrow, 0.25f) && Mathf.Approximately(joystickLinearSensitivity, 1.0f))
                joystickLinearFullThrow = 0.1f;
            if (joystickLinearSensitivity > 2.0f)
                joystickLinearSensitivity = 1.0f;
            if (joystickAngularSensitivity > 1.5f)
                joystickAngularSensitivity = 1.0f;
            if (joystickLinearResponseExponent <= 0f)
                joystickLinearResponseExponent = 1.6f;
            if (joystickAngularResponseExponent <= 0f)
                joystickAngularResponseExponent = 1.2f;
        }

        private float ApplyJoystickDeadzone(float value, float deadzone)
        {
            return Mathf.Abs(value) >= deadzone ? value : 0f;
        }

        private bool ReadJoystickButtonDown(string axisName)
        {
            if (!JoystickPresent || string.IsNullOrWhiteSpace(axisName))
                return false;

            bool isPressed = GetAxisSafely(ResolveJoystickAxisName(axisName)) >= 0.5f;
            if (axisName == joystickStartAxis)
            {
                bool buttonDown = isPressed && !lastJoystickStartPressed;
                lastJoystickStartPressed = isPressed;
                return buttonDown;
            }

            if (axisName == joystickToggleModeAxis)
            {
                bool buttonDown = isPressed && !lastJoystickTogglePressed;
                lastJoystickTogglePressed = isPressed;
                return buttonDown;
            }

            return isPressed;
        }
    }
}
