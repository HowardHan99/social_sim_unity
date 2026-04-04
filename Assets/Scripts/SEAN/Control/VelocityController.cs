// Copyright (c) 2021, Members of Yale Interactive Machines Group, Yale University,
// Nathan Tsoi
// All rights reserved.
// This source code is licensed under the BSD-style license found in the
// LICENSE file in the root directory of this source tree. 

using UnityEngine;

namespace SEAN.Control
{
    public class VelocityController : ControlSubscriber
    {
        private Rigidbody rb;

        private float targetLinVelocity, targetAngVelocity;
        public float maxTimeDeltaSec = 0.25f;
        private float lastMessageTS = 0;

        // PID Controller - Not used in momentum-based transition directly, but kept for potential future use
        public float P = 1, I = 1, D = 1;
        private float integral, lastError;

        // Manual control variables
        private bool manualControlActive = false;
        public bool ManualControlActive => manualControlActive;
        public float manualLinearSpeed = 0.01f;
        public float manualAngularSpeed = 0.01f;

        [Header("Manual Joystick")]
        public bool enableJoystickManualControl = true;
        public string joystickLinearAxis = "Vertical";
        public string joystickAngularAxis = "RHorizontal";
        public float joystickLinearDeadzone = 0.08f;
        public float joystickAngularDeadzone = 0.2f;
        public bool invertJoystickLinear = true;
        public bool invertJoystickAngular = false;
        
        [Header("Manual Brake/Reverse Behavior")]
        public float brakeStopThreshold = 0.02f;
        public int sPressesToEnableReverse = 2;
        public float sPressWindowSec = 0.6f;

        [Header("Debug Manual Brake (Read-Only)")]
        public int DebugSBrakePressCount;

        // Momentum-based Transition variables
        private float prevLinVelocity = 0f;
        private float prevAngVelocity = 0f;
        public float velocityDamping = 0.9f;

        // Debug fields to show last received ROS commands in Inspector
        [Header("Debug ROS Speeds (Read-Only)")]
        public float DebugROSLinSpeed;
        public float DebugROSAngSpeed;
        
        [Header("Debug Motion (Read-Only)")]
        public float DebugCommandedLinSpeed;
        public float DebugCommandedAngSpeed;
        public float DebugAppliedLinSpeed;
        public float DebugAppliedAngSpeed;
        public float DebugActualPlanarForwardSpeed;
        public float DebugActualTotalSpeed;

        [Header("Debug Joystick (Read-Only)")]
        public bool DebugJoystickPresent;
        public float DebugJoystickRawLinear;
        public float DebugJoystickRawAngular;
        public float DebugJoystickProcessedLinear;
        public float DebugJoystickProcessedAngular;
        public float DebugJoystickLinearCenter;
        public float DebugJoystickAngularCenter;
        public float DebugAxisHorizontal;
        public float DebugAxisVertical;
        public float DebugAxisRHorizontal;
        public float DebugAxisRVertical;

        [Header("Debug Stream Health (Read-Only)")]
        public float DebugCmdHz;
        public float DebugExpectedFixedHz;
        public float DebugSecondsSinceLastCmd = -1f;
        public bool DebugCmdIsFresh;
        public bool DebugTimedOut;

        [Header("Debug Stuck (Read-Only)")]
        public bool DebugIsLikelyStuck;

        [Header("Stuck Detection Settings")]
        public float stuckMinCommandedSpeed = 0.05f;
        public float stuckMaxActualPlanarSpeed = 0.01f;
        public float stuckHoldSeconds = 0.5f;

        [Header("Command Shaping")]
        public bool enableCommandShaping = true;
        public float linearCommandDeadband = 0.01f;
        public float angularCommandDeadband = 0.03f;
        public float maxLinearCommand = 0.6f;
        public float maxAngularCommand = 1.2f;

        [Header("Stuck Mitigation")]
        public bool suppressSpinWhenStuck = true;
        [Range(0f, 1f)]
        public float stuckAngularScale = 0.25f;

        [Header("On-Screen Debug Overlay")]
        public bool showOnScreenDebug = true;
        public Vector2 debugOverlayPosition = new Vector2(15f, 15f);
        public Vector2 debugOverlaySize = new Vector2(430f, 90f);

        [Header("Upright Stabilization")]
        public bool keepRobotUpright = true;

        private float lastCmdReceiptRealtime = -1f;
        private float stuckAccumulatedSeconds = 0f;
        private float lastSBrakePressRealtime = -1f;
        private bool JoystickPresent => enableJoystickManualControl && UnityEngine.Input.GetJoystickNames().Length > 0;
        private float joystickLinearCenter;
        private float joystickAngularCenter;
        private bool joystickCenterCaptured;

        protected void Start()
        {
            base.Start();
            rb = sean.robot.base_link.GetComponent<Rigidbody>();
            ApplyUprightConstraints();
            // ROSConnection.instance.Subscribe<RosMessageTypes.Geometry.MTwist>(Topic, CmdVelMessage);
        }

        private void Update()
        {
            DebugJoystickPresent = JoystickPresent;
            UpdateNamedAxisDebug();

            // Toggle manual control with left shift
            if (UnityEngine.Input.GetKeyDown(KeyCode.LeftShift))
            {
                manualControlActive = !manualControlActive;
                if (manualControlActive)
                {
                    Debug.Log("Manual control activated");
                    CaptureJoystickCenter();
                    // When switching to manual, ensure prevVelocities are current target velocities
                    // to avoid momentary hiccup if Update runs before a new manual input processes.
                    // This helps if switching while robot was moving under ROS.
                    prevLinVelocity = targetLinVelocity;
                    prevAngVelocity = targetAngVelocity;
                    DebugSBrakePressCount = 0;
                    lastSBrakePressRealtime = -1f;
                }
                else
                {
                    Debug.Log("ROS control activated");
                    joystickCenterCaptured = false;
                    // When switching to ROS, ensure prevVelocities are current target velocities
                    // to avoid momentary hiccup if Update runs before a new ROS message processes.
                    // This helps if switching while robot was moving under manual control.
                    prevLinVelocity = targetLinVelocity;
                    prevAngVelocity = targetAngVelocity;
                    DebugSBrakePressCount = 0;
                    lastSBrakePressRealtime = -1f;
                }
            }

            // Process manual control input if active
            if (manualControlActive)
            {
                HandleManualInput();
            }
        }

        private void HandleManualInput()
        {
            // Initialize desired linear velocity to the current (previous frame's target) velocity
            // This ensures that if W/S is not pressed, the intention is to maintain current linear momentum.
            float manualDesiredLin = prevLinVelocity;
            
            // Initialize desired angular velocity to 0 by default.
            // This means if A/D is not pressed, the robot will try to stop turning.
            float manualDesiredAng = 0f; 

            float joystickLinearInput = 0f;
            float joystickAngularInput = 0f;
            if (JoystickPresent)
            {
                if (!joystickCenterCaptured)
                    CaptureJoystickCenter();

                float rawLinearInput = ReadJoystickRawAxis(joystickLinearAxis, invertJoystickLinear);
                float rawAngularInput = ReadJoystickRawAxis(joystickAngularAxis, invertJoystickAngular);
                float centeredLinearInput = rawLinearInput - joystickLinearCenter;
                float centeredAngularInput = rawAngularInput - joystickAngularCenter;
                joystickLinearInput = ApplyJoystickDeadzone(centeredLinearInput, joystickLinearDeadzone);
                joystickAngularInput = ApplyJoystickDeadzone(centeredAngularInput, joystickAngularDeadzone);

                DebugJoystickRawLinear = rawLinearInput;
                DebugJoystickRawAngular = rawAngularInput;
                DebugJoystickProcessedLinear = joystickLinearInput;
                DebugJoystickProcessedAngular = joystickAngularInput;
                DebugJoystickLinearCenter = joystickLinearCenter;
                DebugJoystickAngularCenter = joystickAngularCenter;

                manualDesiredLin = manualLinearSpeed * joystickLinearInput;
                manualDesiredAng = manualAngularSpeed * joystickAngularInput;
            }
            else
            {
                DebugJoystickRawLinear = 0f;
                DebugJoystickRawAngular = 0f;
                DebugJoystickProcessedLinear = 0f;
                DebugJoystickProcessedAngular = 0f;
                DebugJoystickLinearCenter = 0f;
                DebugJoystickAngularCenter = 0f;
            }

            bool wHeld = UnityEngine.Input.GetKey(KeyCode.W);
            bool sHeld = UnityEngine.Input.GetKey(KeyCode.S);
            bool sPressed = UnityEngine.Input.GetKeyDown(KeyCode.S);
            float nowRealtime = Time.realtimeSinceStartup;

            if (!sHeld &&
                lastSBrakePressRealtime > 0f &&
                nowRealtime - lastSBrakePressRealtime > sPressWindowSec)
            {
                DebugSBrakePressCount = 0;
                lastSBrakePressRealtime = -1f;
            }

            if (sPressed)
            {
                if (lastSBrakePressRealtime < 0f ||
                    nowRealtime - lastSBrakePressRealtime > sPressWindowSec)
                {
                    DebugSBrakePressCount = 0;
                }
                DebugSBrakePressCount++;
                lastSBrakePressRealtime = nowRealtime;
            }

            // Forward/Backward
            if (wHeld)
            {
                manualDesiredLin = manualLinearSpeed; // Accelerate/set target to forward speed
                DebugSBrakePressCount = 0;
                lastSBrakePressRealtime = -1f;
            }
            else if (sHeld)
            {
                bool movingForward = prevLinVelocity > brakeStopThreshold;
                bool nearStop = Mathf.Abs(prevLinVelocity) <= brakeStopThreshold;
                bool reverseArmed = DebugSBrakePressCount >= Mathf.Max(1, sPressesToEnableReverse);

                // S behaves as brake first while moving forward.
                if (movingForward)
                {
                    manualDesiredLin = 0f;
                }
                else if (nearStop)
                {
                    // Only engage reverse after repeated S presses near stop.
                    manualDesiredLin = reverseArmed ? -manualLinearSpeed : 0f;
                }
                else
                {
                    // Already moving backward: keep backward command while S is held.
                    manualDesiredLin = -manualLinearSpeed;
                }
            }

            // Turning (A for Right, D for Left as per original logic)
            // If A or D is pressed, set a desired angular speed.
            // If not, manualDesiredAng remains 0f, causing the robot to decelerate its turn.
            if (UnityEngine.Input.GetKey(KeyCode.A))
            {
                manualDesiredAng = manualAngularSpeed; 
            }
            else if (UnityEngine.Input.GetKey(KeyCode.D))
            {
                manualDesiredAng = -manualAngularSpeed;
            }

            // Stop with H key - explicitly sets desired to 0 for smooth deceleration
            if (UnityEngine.Input.GetKey(KeyCode.H))
            {
                manualDesiredLin = 0;
                manualDesiredAng = 0;
                DebugSBrakePressCount = 0;
                lastSBrakePressRealtime = -1f;
            }

            float interpolationFactor = 1.0f - Mathf.Clamp(velocityDamping, 0.0f, 0.999f);

            // Smoothly update target velocities
            targetLinVelocity = Mathf.Lerp(prevLinVelocity, manualDesiredLin, interpolationFactor);
            targetAngVelocity = Mathf.Lerp(prevAngVelocity, manualDesiredAng, interpolationFactor);

            // Update previous velocities for next frame's calculation
            prevLinVelocity = targetLinVelocity;
            prevAngVelocity = targetAngVelocity;

            // Update timestamp to prevent ROS timeout while in manual control
            lastMessageTS = Time.time;
        }

        private void FixedUpdate()
        {
            float now = Time.realtimeSinceStartup;
            DebugExpectedFixedHz = Time.fixedDeltaTime > 1e-5f ? (1f / Time.fixedDeltaTime) : 0f;
            DebugSecondsSinceLastCmd = lastCmdReceiptRealtime > 0f ? (now - lastCmdReceiptRealtime) : -1f;
            DebugCmdIsFresh = lastCmdReceiptRealtime > 0f && DebugSecondsSinceLastCmd <= maxTimeDeltaSec;
            DebugTimedOut = !manualControlActive && !DebugCmdIsFresh;
            if (!DebugCmdIsFresh)
            {
                DebugCmdHz = 0f;
            }

            // Only check for ROS message timeout if not in manual control
            if (!manualControlActive && Time.time - lastMessageTS > maxTimeDeltaSec)
            {
                // Hard stop on timeout for safety, could be smoothed too if desired
                targetAngVelocity = 0;
                targetLinVelocity = 0;
                // Also reset prevVelocities to reflect the stop
                prevAngVelocity = 0;
                prevLinVelocity = 0;
            }

            if (rb == null) return;

            if (keepRobotUpright)
            {
                ApplyUprightConstraints();
                ForceUprightPose();
            }

            // Motion diagnostics: command vs actual robot motion on ground plane.
            DebugActualTotalSpeed = rb.velocity.magnitude;
            Vector3 planarVelocityBefore = Vector3.ProjectOnPlane(rb.velocity, Vector3.up);
            Vector3 planarForward = Vector3.ProjectOnPlane(rb.transform.forward, Vector3.up).normalized;
            DebugActualPlanarForwardSpeed = Vector3.Dot(planarVelocityBefore, planarForward);
            DebugCommandedLinSpeed = targetLinVelocity;
            DebugCommandedAngSpeed = targetAngVelocity;

            float appliedLinVelocity = targetLinVelocity;
            float appliedAngVelocity = targetAngVelocity;
            if (enableCommandShaping)
            {
                if (Mathf.Abs(appliedLinVelocity) < linearCommandDeadband) { appliedLinVelocity = 0f; }
                if (Mathf.Abs(appliedAngVelocity) < angularCommandDeadband) { appliedAngVelocity = 0f; }
                appliedLinVelocity = Mathf.Clamp(appliedLinVelocity, -maxLinearCommand, maxLinearCommand);
                appliedAngVelocity = Mathf.Clamp(appliedAngVelocity, -maxAngularCommand, maxAngularCommand);
            }
            if (suppressSpinWhenStuck &&
                DebugIsLikelyStuck &&
                Mathf.Abs(appliedLinVelocity) >= stuckMinCommandedSpeed)
            {
                appliedAngVelocity *= stuckAngularScale;
            }
            DebugAppliedLinSpeed = appliedLinVelocity;
            DebugAppliedAngSpeed = appliedAngVelocity;

            // Apply angular velocity
            if (Mathf.Approximately(appliedAngVelocity, 0.0f) && Mathf.Approximately(rb.angularVelocity.y, 0.0f))
            {
                rb.angularVelocity = Vector3.zero; // Ensure it's truly zero if target is zero
            }
            else
            {
                rb.angularVelocity = new Vector3(0, -1 * appliedAngVelocity, 0);
            }

            // Apply linear velocity
            if (Mathf.Approximately(appliedLinVelocity, 0.0f) && Mathf.Approximately(rb.velocity.magnitude, 0.0f))
            {
                 // If target is 0 and current velocity is already very small (or zero), set rb.velocity to zero.
                 // This helps prevent tiny residual velocities if rb.velocity.y is non-zero from physics.
                rb.velocity = new Vector3(0, rb.velocity.y, 0); // Keep current Y velocity (gravity, etc.)
            }
            else
            {
                // Apply forward/backward velocity, preserving existing Y velocity (for gravity/physics)
                Vector3 currentVelocity = rb.velocity;
                Vector3 targetWorldVelocity = rb.transform.forward * appliedLinVelocity;
                rb.velocity = new Vector3(targetWorldVelocity.x, currentVelocity.y, targetWorldVelocity.z);
            }

            bool stuckCandidate =
                Mathf.Abs(DebugAppliedLinSpeed) >= stuckMinCommandedSpeed &&
                Mathf.Abs(DebugActualPlanarForwardSpeed) <= stuckMaxActualPlanarSpeed;
            if (stuckCandidate)
            {
                stuckAccumulatedSeconds += Time.fixedDeltaTime;
            }
            else
            {
                stuckAccumulatedSeconds = 0f;
            }
            DebugIsLikelyStuck = stuckAccumulatedSeconds >= stuckHoldSeconds;
        }

        override sealed protected void CmdVelMessage(RosMessageTypes.Geometry.MTwist msg)
        {
            if (msg == null) { return; }
            if (rb == null) { return; }

            float now = Time.realtimeSinceStartup;
            if (lastCmdReceiptRealtime > 0f)
            {
                float cmdDt = now - lastCmdReceiptRealtime;
                if (cmdDt > 1e-5f)
                {
                    DebugCmdHz = 1f / cmdDt;
                }
            }
            lastCmdReceiptRealtime = now;

            // Update debug fields with raw ROS values
            DebugROSLinSpeed = (float)msg.linear.x;
            DebugROSAngSpeed = (float)msg.angular.z;

            // Only update velocity from ROS when manual control is not active
            if (!manualControlActive)
            {
                float rosLinVelocity = DebugROSLinSpeed; // Use the stored debug value
                float rosAngVelocity = DebugROSAngSpeed; // Use the stored debug value

                float interpolationFactor = 1.0f - Mathf.Clamp(velocityDamping, 0.0f, 0.999f);

                targetLinVelocity = Mathf.Lerp(prevLinVelocity, rosLinVelocity, interpolationFactor);
                targetAngVelocity = Mathf.Lerp(prevAngVelocity, rosAngVelocity, interpolationFactor);

                prevLinVelocity = targetLinVelocity;
                prevAngVelocity = targetAngVelocity;
                lastMessageTS = Time.time;
            }
        }

        private void OnGUI()
        {
            if (!showOnScreenDebug) { return; }

            string statusText;
            Color statusColor;
            if (DebugTimedOut)
            {
                statusText = "TIMEOUT";
                statusColor = new Color(1f, 0.35f, 0.35f);
            }
            else if (DebugIsLikelyStuck)
            {
                statusText = "LIKELY STUCK";
                statusColor = new Color(1f, 0.75f, 0.25f);
            }
            else
            {
                statusText = "OK";
                statusColor = new Color(0.45f, 1f, 0.45f);
            }

            string overlayText =
                $"[{statusText}] cmd_fresh={DebugCmdIsFresh} cmd_hz={DebugCmdHz:F1} dt_last_cmd={DebugSecondsSinceLastCmd:F2}s\n" +
                $"cmd_lin={DebugCommandedLinSpeed:F3} app_lin={DebugAppliedLinSpeed:F3} app_ang={DebugAppliedAngSpeed:F3} act_planar={DebugActualPlanarForwardSpeed:F3}" +
                (manualControlActive ? $"\nmanual={(JoystickPresent ? "joystick" : "keyboard")}" : string.Empty) +
                $"\njoy_present={DebugJoystickPresent} raw_lin={DebugJoystickRawLinear:F3} raw_ang={DebugJoystickRawAngular:F3} proc_lin={DebugJoystickProcessedLinear:F3} proc_ang={DebugJoystickProcessedAngular:F3}" +
                $"\naxes H={DebugAxisHorizontal:F3} V={DebugAxisVertical:F3} RH={DebugAxisRHorizontal:F3} RV={DebugAxisRVertical:F3}";

            Rect rect = new Rect(debugOverlayPosition.x, debugOverlayPosition.y, debugOverlaySize.x, debugOverlaySize.y);
            Color previousColor = GUI.color;
            GUI.color = statusColor;
            GUI.Box(rect, overlayText);
            GUI.color = previousColor;
        }

        // PID function - not directly used by momentum control but part of the original script
        private float Pid(float setpoint, float actual, float timeFrame)
        {
            float present = setpoint - actual;
            integral += present * timeFrame;
            float deriv = (present - lastError) / timeFrame;
            lastError = present;
            return present * P + integral * I + deriv * D;
        }

        private float ReadJoystickRawAxis(string axisName, bool invert)
        {
            if (!JoystickPresent || string.IsNullOrWhiteSpace(axisName))
                return 0f;

            float value = UnityEngine.Input.GetAxis(axisName);
            if (invert)
                value = -value;
            return value;
        }

        private float ApplyJoystickDeadzone(float value, float deadzone)
        {
            return Mathf.Abs(value) >= deadzone ? value : 0f;
        }

        private void UpdateNamedAxisDebug()
        {
            DebugAxisHorizontal = UnityEngine.Input.GetAxis("Horizontal");
            DebugAxisVertical = UnityEngine.Input.GetAxis("Vertical");
            DebugAxisRHorizontal = UnityEngine.Input.GetAxis("RHorizontal");
            DebugAxisRVertical = UnityEngine.Input.GetAxis("RVertical");
        }

        private void CaptureJoystickCenter()
        {
            if (!JoystickPresent)
            {
                joystickLinearCenter = 0f;
                joystickAngularCenter = 0f;
                joystickCenterCaptured = false;
                return;
            }

            joystickLinearCenter = ReadJoystickRawAxis(joystickLinearAxis, invertJoystickLinear);
            joystickAngularCenter = ReadJoystickRawAxis(joystickAngularAxis, invertJoystickAngular);
            DebugJoystickLinearCenter = joystickLinearCenter;
            DebugJoystickAngularCenter = joystickAngularCenter;
            joystickCenterCaptured = true;
        }

        private void ApplyUprightConstraints()
        {
            if (rb == null || !keepRobotUpright) { return; }

            rb.constraints |= RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ;
        }

        private void ForceUprightPose()
        {
            if (rb == null) { return; }

            Vector3 euler = rb.rotation.eulerAngles;
            Quaternion uprightRotation = Quaternion.Euler(0f, euler.y, 0f);
            if (Quaternion.Angle(rb.rotation, uprightRotation) > 0.01f)
            {
                rb.MoveRotation(uprightRotation);
            }

            Vector3 angularVelocity = rb.angularVelocity;
            if (!Mathf.Approximately(angularVelocity.x, 0f) || !Mathf.Approximately(angularVelocity.z, 0f))
            {
                rb.angularVelocity = new Vector3(0f, angularVelocity.y, 0f);
            }
        }

        public void ResetMotionState()
        {
            targetLinVelocity = 0f;
            targetAngVelocity = 0f;
            prevLinVelocity = 0f;
            prevAngVelocity = 0f;
            integral = 0f;
            lastError = 0f;
            DebugSBrakePressCount = 0;
            lastSBrakePressRealtime = -1f;

            if (rb != null)
            {
                rb.velocity = new Vector3(0f, rb.velocity.y, 0f);
                rb.angularVelocity = Vector3.zero;
            }
        }
    }
}
