using System.Collections;
using UnityEngine;

namespace IVI
{
    public class ManualWheelchairController : MonoBehaviour
    {
        [Header("Control Settings")]
        public float moveSpeed = 3f;
        public float rotationSpeed = 90f;
        public KeyCode toggleModeKey = KeyCode.RightShift;
        public bool useWASD = false;
        public bool startInManualMode = false;
        public bool enableJoystick = true;
        public string joystickHorizontalAxis = "RHorizontal";
        public string joystickVerticalAxis = "Vertical";
        public string joystickStartAxis = "L1";
        public string joystickToggleModeAxis = string.Empty;
        public float joystickDeadzone = 0.05f;
        public bool invertJoystickHorizontal = false;
        public bool invertJoystickVertical = true;

        [Header("Status (read-only)")]
        public bool isManualMode = false;

        private SFPWDAgent sfpwdAgent;
        private Rigidbody rb;
        private Animator animator;
        private Vector3 manualVelocity;
        private bool initialized = false;
        private bool waitingForStart = true;
        public bool WaitingForStart => waitingForStart;
        private Quaternion spawnRotation;
        private WheelchairCameraSmoothing camSmoothing;
        private bool JoystickPresent => enableJoystick && Input.GetJoystickNames().Length > 0;
        private bool lastJoystickStartPressed;
        private bool lastJoystickTogglePressed;

        void Start()
        {
            spawnRotation = transform.rotation;
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

            // Freeze the agent on spawn; navigation begins on Space press.
            if (sfpwdAgent != null)
            {
                sfpwdAgent.KillNavigationCoroutine();
                sfpwdAgent.enabled = false;
            }
            if (rb != null)
                rb.velocity = Vector3.zero;

            // Restore the exact spawn rotation (the one-frame agent tick may have changed it).
            transform.rotation = spawnRotation;

            // Disable camera smoothing during wait so the user can free-look.
            camSmoothing = GetComponentInChildren<WheelchairCameraSmoothing>(true);
            if (camSmoothing != null)
                camSmoothing.enabled = false;

            waitingForStart = true;
            initialized = true;
            Debug.Log("[PWD] ManualWheelchairController ready. Waiting for Space to start.");
        }

        void Update()
        {
            if (!initialized) return;

            if (waitingForStart)
            {
                if (Input.GetKeyDown(KeyCode.Space) || ReadJoystickButtonDown(joystickStartAxis))
                {
                    waitingForStart = false;

                    if (camSmoothing != null)
                        camSmoothing.enabled = true;

                    Debug.Log("[PWD] Space pressed -- starting navigation.");
                    if (startInManualMode)
                        SetManualMode();
                    else
                        SetAutomaticMode();
                }
                return;
            }

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
            float h = 0f, v = 0f;

            if (JoystickPresent)
            {
                h = ReadJoystickAxis(joystickHorizontalAxis, invertJoystickHorizontal);
                v = ReadJoystickAxis(joystickVerticalAxis, invertJoystickVertical);
            }

            if (Input.GetKey(KeyCode.UpArrow)) v += 1f;
            if (Input.GetKey(KeyCode.DownArrow)) v -= 1f;
            if (Input.GetKey(KeyCode.LeftArrow)) h -= 1f;
            if (Input.GetKey(KeyCode.RightArrow)) h += 1f;

            if (useWASD)
            {
                if (Input.GetKey(KeyCode.W)) v += 1f;
                if (Input.GetKey(KeyCode.S)) v -= 1f;
                if (Input.GetKey(KeyCode.A)) h -= 1f;
                if (Input.GetKey(KeyCode.D)) h += 1f;
            }

            v = Mathf.Clamp(v, -1f, 1f);
            h = Mathf.Clamp(h, -1f, 1f);

            float rot = h * rotationSpeed * Time.deltaTime;
            if (Mathf.Abs(rot) > 0.001f)
                transform.Rotate(0f, rot, 0f);

            Vector3 fwd = transform.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude > 0.001f) fwd.Normalize();
            manualVelocity = fwd * v * moveSpeed;
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

            if (waitingForStart)
            {
                string startHint = JoystickPresent
                    ? $"Press {joystickStartAxis} to start"
                    : "Press SPACE to start";
                GUI.Box(new Rect(10, 10, 300, 40), startHint);
                return;
            }

            string mode = isManualMode ? "MANUAL" : "AUTO";
            string pos = $"({transform.position.x:F1}, {transform.position.z:F1})";
            string vel = isManualMode
                ? $"{manualVelocity.magnitude:F1}"
                : (sfpwdAgent != null ? $"{sfpwdAgent.velocity.magnitude:F1}" : "--");
            string controlHint = JoystickPresent
                ? "Joystick + keyboard active"
                : "RShift: toggle | WASD/Arrows: move";
            GUI.Box(new Rect(10, 10, 300, 60),
                $"[{mode}] Pos:{pos} Vel:{vel}\n{controlHint}");
        }

        private float ReadJoystickAxis(string axisName, bool invert)
        {
            if (!JoystickPresent || string.IsNullOrWhiteSpace(axisName))
                return 0f;

            float value = Input.GetAxis(axisName);
            if (invert)
                value = -value;
            return Mathf.Abs(value) >= joystickDeadzone ? value : 0f;
        }

        private bool ReadJoystickButtonDown(string axisName)
        {
            if (!JoystickPresent || string.IsNullOrWhiteSpace(axisName))
                return false;

            bool isPressed = Input.GetAxis(axisName) >= 0.5f;
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
