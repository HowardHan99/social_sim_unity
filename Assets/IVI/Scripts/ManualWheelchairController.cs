using System.Collections;
using UnityEngine;
using UnityEngine.Serialization;
using SessionReview;

namespace IVI
{
    public class ManualWheelchairController : MonoBehaviour
    {
        [Header("Control Settings")]
        public float moveSpeed = 0.8f;
        [Tooltip("Live speed multiplier for manual driving. Scales moveSpeed so the Agent Speed overlay can retune this pedestrian live. 1 = unchanged.")]
        public float speedScale = 1.0f;
        public float rotationSpeed = 240f;
        public KeyCode toggleModeKey = KeyCode.RightShift;
        public bool useWASD = true;

        [Tooltip("Read Arrow keys instead of WASD for manual driving. Set automatically so the human's role uses WASD and the other manual role uses arrows.")]
        public bool manualUseArrowKeys = false;
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
        public float joystickAngularResponseExponent = 1.0f;
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

        [Header("Manual Inertia Drive")]
        [Tooltip("Manual driving commands acceleration instead of target velocity, for every input device: stick/W accelerates, releasing all input coasts, S/H brakes. Off = original direct-velocity behavior.")]
        [FormerlySerializedAs("gamepadAccelerationDrive")]
        public bool manualInertiaDrive = true;
        [Tooltip("m/s^2 at full stick deflection or held W.")]
        [FormerlySerializedAs("gamepadLinearAcceleration")]
        public float inertiaLinearAcceleration = 1.2f;
        [Tooltip("m/s^2 of passive coasting decay while no input is held. Lower = more inertia.")]
        [FormerlySerializedAs("gamepadCoastDeceleration")]
        public float inertiaCoastDeceleration = 0.35f;
        [Tooltip("deg/s^2 at full steering deflection or held A/D.")]
        [FormerlySerializedAs("gamepadAngularAcceleration")]
        public float inertiaAngularAcceleration = 420f;
        [Tooltip("deg/s^2 of passive turn decay while no steering input is held.")]
        [FormerlySerializedAs("gamepadAngularCoastDeceleration")]
        public float inertiaAngularCoastDeceleration = 320f;

        [Header("Ground Follow / Step Climb")]
        [Tooltip("Ease the character up onto small steps/curbs it drives into (position-driven movement is otherwise blocked by their colliders). No effect on flat ground.")]
        public bool stepClimb = true;
        [Tooltip("Tallest step (m) that can be climbed.")]
        public float maxStepHeight = 0.35f;
        [Tooltip("Vertical speed (m/s) used to ease onto the detected ground height.")]
        public float stepClimbSpeed = 3f;

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
        private Vector3 spawnPosition;
        private Quaternion spawnRotation;
        private WheelchairCameraSmoothing camSmoothing;
        private bool JoystickPresent => enableJoystick && Input.GetJoystickNames().Length > 0;
        // Only the human's active role drives with the joystick. The other manual role
        // is remapped to the arrow keys (manualUseArrowKeys) and must never read the
        // shared joystick, otherwise both agents move together.
        private bool ManualUsesJoystick => JoystickPresent && !manualUseArrowKeys;
        // moveSpeed after applying the live scenario speed multiplier.
        private float EffectiveMoveSpeed => moveSpeed * Mathf.Max(0f, speedScale);
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
            spawnPosition = transform.position;
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

            // Session review may have disabled this component (FreezePwdController) between
            // Start() and this coroutine resuming. Don't override that freeze.
            if (!enabled)
            {
                Debug.Log("[PWD] ManualWheelchairController disabled by session review before init completed; staying frozen.");
                yield break;
            }

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
                FollowGround(Time.deltaTime);
            }
            else if (sfpwdAgent != null && sfpwdAgent.enabled)
            {
                // SFPWDAgent (Base.Update) computes velocity, handles rotation
                // and animator params, but doesn't drive position when root motion
                // is off. Apply the computed velocity as a direct position update.
                Vector3 vel = sfpwdAgent.velocity;
                vel.y = 0f;
                if (vel.sqrMagnitude > 0.001f)
                {
                    transform.position += vel * Time.deltaTime;
                    FollowGround(Time.deltaTime);
                }
            }
        }

        // Position-driven movement cannot climb: driving into a curb/step just gets the
        // capsule depenetrated sideways. Probe the ground at the body and slightly ahead;
        // when it is a climbable step above the feet, ease the transform up onto it.
        // Descending is left to rigidbody gravity, except for kinematic/rigidbody-less
        // bodies (e.g. the TestScene practice robot) which are eased down too.
        private void FollowGround(float deltaTime)
        {
            if (!stepClimb || maxStepHeight <= 0f)
                return;

            float probeUp = maxStepHeight + 0.3f;
            float probeLength = probeUp + maxStepHeight + 0.6f;
            Vector3 fwd = transform.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude > 0.001f) fwd.Normalize();

            float ground = float.NegativeInfinity;
            if (TryProbeGround(transform.position + Vector3.up * probeUp, probeLength, out float hCenter))
                ground = Mathf.Max(ground, hCenter);
            if (TryProbeGround(transform.position + fwd * 0.35f + Vector3.up * probeUp, probeLength, out float hFront))
                ground = Mathf.Max(ground, hFront);
            if (float.IsNegativeInfinity(ground))
                return;

            float diff = ground - transform.position.y;
            bool climbUp = diff > 0.01f && diff <= maxStepHeight;
            bool easeDown = diff < -0.01f && (rb == null || rb.isKinematic);
            if (!climbUp && !easeDown)
                return;

            Vector3 p = transform.position;
            p.y = Mathf.MoveTowards(p.y, ground, stepClimbSpeed * deltaTime);
            transform.position = p;

            if (climbUp && rb != null && !rb.isKinematic)
            {
                Vector3 v = rb.velocity;
                if (v.y < 0f) { v.y = 0f; rb.velocity = v; }
            }
        }

        private bool TryProbeGround(Vector3 origin, float length, out float groundY)
        {
            groundY = 0f;
            float best = float.NegativeInfinity;
            foreach (var hit in Physics.RaycastAll(origin, Vector3.down, length, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            {
                if (hit.collider == null) continue;
                // Environment only: skip our own body and anything with a rigidbody
                // (pedestrians, robots) so we never "climb" onto another agent.
                if (hit.collider.attachedRigidbody != null) continue;
                if (hit.collider.transform.IsChildOf(transform)) continue;
                best = Mathf.Max(best, hit.point.y);
            }

            if (float.IsNegativeInfinity(best))
                return false;
            groundY = best;
            return true;
        }

        void HandleInput()
        {
            float manualDesiredLin = currentManualLinearSpeed;
            float manualDesiredAng = 0f;
            float joystickSteer = 0f;
            float joystickThrottle = 0f;

            if (ManualUsesJoystick)
            {
                if (!joystickCenterCaptured)
                    CaptureJoystickCenter();

                joystickSteer = ProcessJoystickInput(ReadJoystickAxis(joystickHorizontalAxis, invertJoystickHorizontal, joystickHorizontalCenter), EffectiveJoystickDeadzone(), joystickAngularFullThrow, joystickAngularSensitivity, joystickAngularResponseExponent);
                joystickThrottle = ProcessJoystickInput(ReadJoystickAxis(joystickVerticalAxis, invertJoystickVertical, joystickVerticalCenter), EffectiveJoystickDeadzone(), joystickLinearFullThrow, joystickLinearSensitivity, joystickLinearResponseExponent);
                debugJoystickProcessedHorizontal = joystickSteer;
                debugJoystickProcessedVertical = joystickThrottle;
                manualDesiredLin = EffectiveMoveSpeed * joystickThrottle;
                manualDesiredAng = rotationSpeed * joystickSteer;
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

            bool wHeld = ManualKeyHeld(KeyCode.W, KeyCode.UpArrow);
            bool sHeld = ManualKeyHeld(KeyCode.S, KeyCode.DownArrow);
            bool sPressed = ManualKeyDown(KeyCode.S, KeyCode.DownArrow);
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
                manualDesiredLin = EffectiveMoveSpeed;
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
                    manualDesiredLin = reverseArmed ? -EffectiveMoveSpeed : 0f;
                }
                else
                {
                    manualDesiredLin = -EffectiveMoveSpeed;
                }
            }

            if (useWASD)
            {
                if (ManualKeyHeld(KeyCode.A, KeyCode.LeftArrow))
                {
                    manualDesiredAng = -rotationSpeed;
                }
                else if (ManualKeyHeld(KeyCode.D, KeyCode.RightArrow))
                {
                    manualDesiredAng = rotationSpeed;
                }
            }

            bool hardStopHeld = Input.GetKey(KeyCode.H);
            if (hardStopHeld)
            {
                manualDesiredLin = 0f;
                manualDesiredAng = 0f;
                debugSBrakePressCount = 0;
                lastSBrakePressRealtime = -1f;
            }

            if (manualInertiaDrive)
            {
                // Manual driving models a mass, for every input device: stick/W commands
                // acceleration, releasing all input coasts, S (once armed for reverse it
                // accelerates backward instead) and H brake at the firm legacy rate.
                float linearInput = joystickThrottle;
                bool linearBrake = hardStopHeld;
                if (wHeld)
                {
                    linearInput = 1f;
                }
                else if (sHeld)
                {
                    // Reuse the S state machine's verdict: negative desired = reverse intent.
                    if (manualDesiredLin < -brakeStopThreshold)
                        linearInput = -1f;
                    else
                    {
                        linearInput = 0f;
                        linearBrake = true;
                    }
                }

                if (linearBrake)
                    currentManualLinearSpeed = Mathf.MoveTowards(
                        currentManualLinearSpeed, 0f, manualDeceleration * Time.deltaTime);
                else if (Mathf.Abs(linearInput) > 0.001f)
                    currentManualLinearSpeed = Mathf.Clamp(
                        currentManualLinearSpeed + linearInput * inertiaLinearAcceleration * Time.deltaTime,
                        -EffectiveMoveSpeed, EffectiveMoveSpeed);
                else
                    currentManualLinearSpeed = Mathf.MoveTowards(
                        currentManualLinearSpeed, 0f, inertiaCoastDeceleration * Time.deltaTime);

                float angularInput = joystickSteer;
                if (useWASD)
                {
                    if (ManualKeyHeld(KeyCode.A, KeyCode.LeftArrow))
                        angularInput = -1f;
                    else if (ManualKeyHeld(KeyCode.D, KeyCode.RightArrow))
                        angularInput = 1f;
                }

                if (hardStopHeld)
                    currentManualAngularSpeed = Mathf.MoveTowards(
                        currentManualAngularSpeed, 0f, manualAngularAcceleration * Time.deltaTime);
                else if (Mathf.Abs(angularInput) > 0.001f)
                    currentManualAngularSpeed = Mathf.Clamp(
                        currentManualAngularSpeed + angularInput * inertiaAngularAcceleration * Time.deltaTime,
                        -rotationSpeed, rotationSpeed);
                else
                    currentManualAngularSpeed = Mathf.MoveTowards(
                        currentManualAngularSpeed, 0f, inertiaAngularCoastDeceleration * Time.deltaTime);
            }
            else
            {
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
            }

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

        public void ResetToSpawn()
        {
            transform.position = spawnPosition;
            transform.rotation = spawnRotation;
            manualVelocity = Vector3.zero;
            currentManualLinearSpeed = 0f;
            currentManualAngularSpeed = 0f;
            if (rb != null)
            {
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }
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

            // Live driving readout; hide it during review / world-building so it doesn't
            // overlap those overlays (it sits top-left, under the review Metrics panel).
            if (SessionReviewManager.Instance != null &&
                (SessionReviewManager.Instance.IsReviewModeActive || SessionReviewManager.Instance.IsWorldBuildingModeActive))
                return;

            string mode = isManualMode ? "MANUAL" : "AUTO";
            string pos = $"({transform.position.x:F1}, {transform.position.z:F1})";
            string vel = isManualMode
                ? $"{manualVelocity.magnitude:F1}"
                : (sfpwdAgent != null ? $"{sfpwdAgent.velocity.magnitude:F1}" : "--");
            string controlHint = ManualUsesJoystick
                ? $"Joystick ({SEAN.Input.JoystickProfiles.DescribeShort()}) + keyboard | F10 switch" +
                  (SEAN.Input.JoystickProfiles.GamepadActive ? " | RS: look" : "")
                : (manualUseArrowKeys
                    ? "RShift: toggle | Up/Down brake+reverse | Left/Right turn | H stop"
                    : "RShift: toggle | W/S brake+reverse | A/D turn | H stop");
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
            float value = GetAxisSafely(axisName) * SEAN.Input.JoystickProfiles.AxisSign(axisName);
            value = invert ? -value : value;
            if (axisName == ResolveJoystickAxisName(joystickHorizontalAxis))
                debugJoystickRawHorizontal = value;
            if (axisName == ResolveJoystickAxisName(joystickVerticalAxis))
                debugJoystickRawVertical = value;
            return value;
        }

        // Legacy aliases and per-device remapping (Logitech stick vs. gamepad) both live in
        // JoystickProfiles so every manual controller reads the same physical axes.
        private string ResolveJoystickAxisName(string axisName)
        {
            return SEAN.Input.JoystickProfiles.ResolveAxis(axisName);
        }

        // Manual driving reads WASD normally, or the arrow keys when the PWD is not the
        // human's active role (so a robot + PWD both in manual don't move together).
        private bool ManualKeyHeld(KeyCode wasdKey, KeyCode arrowKey)
        {
            return Input.GetKey(manualUseArrowKeys ? arrowKey : wasdKey);
        }

        private bool ManualKeyDown(KeyCode wasdKey, KeyCode arrowKey)
        {
            return Input.GetKeyDown(manualUseArrowKeys ? arrowKey : wasdKey);
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
