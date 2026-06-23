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

        [Header("Ground Sticking")]
        [Tooltip("Each frame, raycast down and place the character's feet on the ground.")]
        public bool stickToGround = true;
        [Tooltip("Layers treated as ground. Exclude the character's own layer if needed.")]
        public LayerMask groundMask = ~0;
        [Tooltip("Vertical offset added on top of the ground hit (if the pivot isn't exactly at the feet).")]
        public float groundOffset = 0f;
        [Tooltip("How far above the pivot to start the ground ray.")]
        public float groundRayStartHeight = 2.5f;
        [Tooltip("How far down to search for ground from the ray start.")]
        public float groundRayDistance = 6f;

        [Header("Step / Curb Collision")]
        [Tooltip("Probe the ground ahead before moving. If the ground steps up or down by more than " +
                 "Max Step Height, the move is blocked so curbs between sidewalk and road act like walls " +
                 "instead of letting the character climb or drop onto the new height.")]
        public bool blockStepHeight = true;
        [Tooltip("Largest vertical change (meters) the character can walk over. Slopes and tiny bumps " +
                 "below this are followed smoothly; curbs/ledges taller than this block movement.")]
        public float maxStepHeight = 0.12f;
        [Tooltip("When moving diagonally into a curb, slide along the edge instead of stopping dead.")]
        public bool slideAlongSteps = true;

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

        [Header("Debug Locomotion (read-only)")]
        public bool debugAnimatorFound;
        public string debugAnimatorTarget;
        public float debugForward;
        public float debugStrafe;
        public float debugTurn;
        public bool debugIsBiking;

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
        private bool manualForwardInputHeld;
        private bool manualReverseInputHeld;
        private bool useIsBikingAnimator;
        private Animator bikeAnimator;

        void Start()
        {
            spawnRotation = transform.rotation;
            ApplyJoystickResponseDefaults();
            sfpwdAgent = GetComponent<SFPWDAgent>();
            rb = GetComponent<Rigidbody>();
            animator = AvatarAnimatorUtility.GetLocomotionAnimator(gameObject);
            debugAnimatorFound = animator != null;
            debugAnimatorTarget = animator != null ? animator.gameObject.name : "(none)";
            if (animator == null)
                Debug.LogWarning("[PWD] No locomotion Animator found on avatar.", this);
            StartCoroutine(InitAfterBase());
        }

        IEnumerator InitAfterBase()
        {
            yield return null;

            if (animator != null)
            {
                animator.applyRootMotion = false;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                useIsBikingAnimator = HasAnimatorParameter("IsBiking");
            }

            bikeAnimator = FindBikeAnimator();
            if (bikeAnimator != null)
                bikeAnimator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

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
                transform.position = ApplyGroundedMove(transform.position, manualVelocity * Time.deltaTime);
                UpdateAnimator();
            }
            else
            {
                // SFPWDAgent (Base.Update) computes velocity and rotation when present.
                // Fall back to rigidbody velocity for prefabs tested without an agent.
                Vector3 vel = Vector3.zero;
                if (sfpwdAgent != null && sfpwdAgent.enabled)
                    vel = sfpwdAgent.velocity;
                else if (rb != null)
                    vel = rb.velocity;

                vel.y = 0f;
                transform.position = ApplyGroundedMove(transform.position, vel * Time.deltaTime);

                manualVelocity = vel;
                UpdateAnimator();
            }

            // When step collision is on, ApplyGroundedMove already keeps the character grounded
            // (and refuses to climb curbs). The free snap is only used in the legacy mode.
            if (!blockStepHeight)
                SnapToGround();
        }

        // Moves the character horizontally while keeping it on the ground, treating curbs/ledges
        // taller than maxStepHeight as walls (the move into them is blocked instead of snapping up).
        Vector3 ApplyGroundedMove(Vector3 pos, Vector3 horizDelta)
        {
            horizDelta.y = 0f;

            if (!stickToGround || !blockStepHeight)
                return pos + horizDelta; // legacy path: free move, SnapToGround() places Y afterwards

            // Ground height directly under where the character currently stands.
            float currentGroundY;
            if (!SampleGroundY(pos, out currentGroundY))
                currentGroundY = pos.y - groundOffset; // no ground found: keep relative feet height

            // Try the full move first.
            Vector3 moved = TryStep(pos, horizDelta, currentGroundY);

            // If blocked, try sliding along each axis so diagonal walks glide along a curb edge.
            if (slideAlongSteps && moved == pos && horizDelta.sqrMagnitude > 1e-8f)
            {
                Vector3 alongX = TryStep(pos, new Vector3(horizDelta.x, 0f, 0f), currentGroundY);
                if (alongX != pos)
                    return alongX;

                Vector3 alongZ = TryStep(pos, new Vector3(0f, 0f, horizDelta.z), currentGroundY);
                if (alongZ != pos)
                    return alongZ;
            }

            return moved;
        }

        // Returns the grounded destination if the step up/down is within maxStepHeight; otherwise
        // returns the original position (movement blocked, i.e. the character "collides" with the curb).
        Vector3 TryStep(Vector3 pos, Vector3 horizDelta, float currentGroundY)
        {
            Vector3 target = pos + horizDelta;

            float targetGroundY;
            if (!SampleGroundY(target, out targetGroundY))
                return pos; // no ground ahead (gap/edge): block so we don't walk into the void

            float dy = targetGroundY - currentGroundY;
            if (dy > maxStepHeight || dy < -maxStepHeight)
                return pos; // curb/ledge taller than a step: block the move

            target.y = targetGroundY + groundOffset;
            return target;
        }

        // Highest non-self ground height under a given XZ position. Returns false if nothing is hit.
        bool SampleGroundY(Vector3 atPos, out float groundY)
        {
            groundY = 0f;

            Vector3 origin = new Vector3(atPos.x, atPos.y + groundRayStartHeight, atPos.z);
            float maxDistance = groundRayStartHeight + groundRayDistance;

            RaycastHit[] hits = Physics.RaycastAll(
                origin, Vector3.down, maxDistance, groundMask, QueryTriggerInteraction.Ignore);

            bool found = false;
            float bestY = float.NegativeInfinity;
            foreach (RaycastHit hit in hits)
            {
                if (hit.collider.transform == transform || hit.collider.transform.IsChildOf(transform))
                    continue;

                if (hit.point.y > bestY)
                {
                    bestY = hit.point.y;
                    found = true;
                }
            }

            if (found)
                groundY = bestY;
            return found;
        }

        // Places the character's feet on the ground via a downward raycast, ignoring its own
        // colliders. Used only in legacy mode (Block Step Height off).
        void SnapToGround()
        {
            if (!stickToGround)
                return;

            float groundY;
            if (!SampleGroundY(transform.position, out groundY))
                return;

            Vector3 p = transform.position;
            p.y = groundY + groundOffset;
            transform.position = p;
        }

        void LateUpdate()
        {
            if (!initialized || isManualMode || animator == null)
                return;

            Vector3 vel = Vector3.zero;
            if (sfpwdAgent != null && sfpwdAgent.enabled)
                vel = sfpwdAgent.velocity;
            else if (rb != null)
                vel = rb.velocity;

            vel.y = 0f;
            manualVelocity = vel;
            UpdateAnimator();
        }

        void HandleInput()
        {
            float manualDesiredLin = 0f;
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

            bool joystickForward = JoystickPresent &&
                debugJoystickProcessedVertical > EffectiveJoystickDeadzone();
            bool joystickReverse = JoystickPresent &&
                debugJoystickProcessedVertical < -EffectiveJoystickDeadzone();
            manualForwardInputHeld = wHeld || joystickForward;
            manualReverseInputHeld = manualDesiredLin < -brakeStopThreshold || joystickReverse;

            if (!manualForwardInputHeld && !manualReverseInputHeld)
            {
                manualDesiredLin = 0f;
                currentManualLinearSpeed = 0f;
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
            const float animSmoothing = 0.6f;
            float forward = local.z / animSmoothing;
            float strafe = local.x / animSmoothing;
            float turn = rotationSpeed > 0.01f
                ? Mathf.Clamp(currentManualAngularSpeed / rotationSpeed, -1f, 1f)
                : 0f;

            if (isManualMode)
            {
                if (!manualForwardInputHeld && !manualReverseInputHeld)
                {
                    forward = 0f;
                    strafe = 0f;
                }
                else if (manualForwardInputHeld)
                {
                    forward = moveSpeed / animSmoothing;
                }
                else if (manualReverseInputHeld)
                {
                    forward = -moveSpeed / animSmoothing;
                }
            }

            bool shouldIdle = isManualMode
                ? !manualForwardInputHeld && !manualReverseInputHeld
                : speed < 0.1f;

            if (bikeAnimator != null)
                bikeAnimator.SetBool("isIdling", shouldIdle);

            if (useIsBikingAnimator)
            {
                bool isBiking = !shouldIdle;
                animator.SetBool("IsBiking", isBiking);
                animator.speed = isBiking ? Mathf.Max(speed / Mathf.Max(moveSpeed, 0.01f), 0.5f) : 1f;
                debugIsBiking = isBiking;
                debugForward = 0f;
                debugStrafe = 0f;
                debugTurn = 0f;
                return;
            }

            if (shouldIdle)
            {
                animator.SetFloat("Forward", 0f);
                animator.SetFloat("Strafe", 0f);
                animator.SetFloat("Turn", 0f);
            }
            else
            {
                animator.SetFloat("Forward", forward);
                animator.SetFloat("Strafe", strafe);
                animator.SetFloat("Turn", turn);
            }
            animator.speed = speed > 0.1f ? speed : 1f;

            debugForward = forward;
            debugStrafe = strafe;
            debugTurn = turn;
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
            GUI.Box(new Rect(10, 10, 340, 78),
                $"[{mode}] Pos:{pos} Vel:{vel} Fwd:{debugForward:F2}\nAnim:{debugAnimatorTarget}\n{controlHint}");
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

        Animator FindBikeAnimator()
        {
            foreach (Animator candidate in GetComponentsInChildren<Animator>(true))
            {
                if (candidate == animator)
                    continue;

                if (candidate.runtimeAnimatorController == null)
                    continue;

                foreach (AnimatorControllerParameter parameter in candidate.parameters)
                {
                    if (parameter.name == "isIdling")
                        return candidate;
                }
            }

            return null;
        }

        bool HasAnimatorParameter(string parameterName)
        {
            if (animator == null)
                return false;

            foreach (AnimatorControllerParameter parameter in animator.parameters)
            {
                if (parameter.name == parameterName)
                    return true;
            }

            return false;
        }
    }
}
