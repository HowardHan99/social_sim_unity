// Copyright (c) 2021, Members of Yale Interactive Machines Group, Yale University,
// Nathan Tsoi
// All rights reserved.
// This source code is licensed under the BSD-style license found in the
// LICENSE file in the root directory of this source tree.

using System.Collections.Generic;
using UnityEngine;
using Unity.Robotics.ROSTCPConnector;

namespace SEAN.Control
{
    public class VelocityController : ControlSubscriber
    {
        private Rigidbody rb;
        private global::SEAN.Display.PlanVisualizer planVisualizer;
        private readonly VelocityFallbackController fallbackController = new VelocityFallbackController();
        private readonly VelocityFallbackState fallbackState = new VelocityFallbackState();

        private float targetLinVelocity, targetAngVelocity;
        public float maxTimeDeltaSec = 0.25f;
        private float lastMessageTS = 0;

        // PID Controller - Not used in momentum-based transition directly, but kept for potential future use
        public float P = 1, I = 1, D = 1;
        private float integral, lastError;

        // Manual control variables
        private bool manualControlActive = false;
        public bool ManualControlActive => manualControlActive;
        public float manualLinearSpeed = 1.0f;
        public float manualAngularSpeed = 2.4f;

        [Header("Startup Control")]
        public bool startInManualMode = false;

        [Header("Manual Joystick")]
        public bool enableJoystickManualControl = true;
        public string joystickLinearAxis = "joystickLinearAxis";
        public string joystickAngularAxis = "LogitechTwist";
        public float joystickLinearDeadzone = 0.03f;
        public float joystickAngularDeadzone = 0.03f;
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
        public float velocityDamping = 0.97f;

        [Header("Unity Velocity Post-Processing")]
        public bool bypassUnityVelocityPostProcessing = true;
        public bool preserveManualVelocitySmoothing = false;
        public float manualVelocityDamping = 0.85f;
        public bool enforceManualSpeedLimit = true;
        public float manualMaxPlanarSpeed = 0.7f;

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
        public float DebugActualYawSpeed;
        public float DebugDistanceToPlan;
        public float DebugHeadingErrorToPlanDeg;
        public float DebugPlanProgressRate;
        public float DebugTrackingError;
        public string DebugExecutionState;

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
        public float DebugRecoveryCooldownRemaining;

        [Header("ROS Recovery Monitor")]
        public bool monitorRosRecovery = true;
        public string rosRecoveryActiveTopic = "/social_sim/ros_recovery_active";
        public string rosRecoveryStatusTopic = "/social_sim/ros_recovery_status";
        public string moveBaseStatusTopic = "/move_base/status";
        public float rosMonitorSubscribeRetrySec = 1.0f;
        public float rosMonitorBridgeGraceSec = 1.5f;

        [Header("Debug ROS Recovery (Read-Only)")]
        public bool DebugRosRecoveryActive;
        public string DebugRosRecoveryStatus = "unavailable";
        public float DebugSecondsSinceRosRecoveryUpdate = -1f;
        public bool DebugRosMonitorEverReceived;
        public string DebugRosMonitorLastTopic = "";
        public bool DebugRosConnectionError;

        [Header("Debug Stuck (Read-Only)")]
        public bool DebugIsLikelyStuck;
        public bool DebugIsTurnBlocked;
        public string DebugUnityRecoveryState = "idle";
        public string DebugUnityRecoveryReason = "none";
        public bool DebugPathReacquireActive;
        public float DebugPathReacquireTargetDistance;
        public float DebugPathReacquireTargetHeadingDeg;

        [Header("Stuck Detection Settings")]
        public float stuckMinCommandedSpeed = 0.05f;
        public float stuckMaxActualPlanarSpeed = 0.01f;
        public float stuckHoldSeconds = 0.5f;

        [Header("Arc Preservation")]
        public bool preserveArcWhenUnderspeed = true;
        public float preserveArcMinLinear = 0.05f;

        [Header("Turn Safety")]
        public bool suppressLinearDuringTurns = true;
        public float turnLinearSuppressionStart = 0.6f;
        public float turnLinearSuppressionFull = 1.2f;
        [Range(0f, 1f)] public float turnLinearMinimumScale = 0.5f;

        [Header("Turn Block Detection")]
        public float turnBlockMinAngularCommand = 0.6f;
        public float turnBlockMaxActualYawSpeed = 0.2f;
        public float turnBlockMaxPlanarSpeed = 0.03f;
        public float turnBlockHoldSeconds = 0.8f;

        [Header("Execution Monitor")]
        public bool usePlanAwareExecutionMonitor = true;
        public float degradedDistanceToPlan = 0.3f;
        public float stalledDistanceToPlan = 0.5f;
        public float degradedHeadingErrorDeg = 35f;
        public float stalledHeadingErrorDeg = 65f;
        public float minPlanProgressRate = 0.02f;
        public float executionStateWindowSec = 0.6f;
        public float degradedLinearCommandThreshold = 0.08f;
        public float stalledLinearCommandThreshold = 0.08f;
        public float degradedTrackingErrorThreshold = 0.45f;
        public float stalledTrackingErrorThreshold = 0.8f;

        [Header("Command Shaping")]
        public bool enableCommandShaping = true;
        public float linearCommandDeadband = 0.01f;
        public float angularCommandDeadband = 0.03f;
        public float maxLinearCommand = 0.7f;
        public float maxAngularCommand = 0.7f;

        [Header("Path Reacquisition")]
        public bool enablePathReacquisition = true;
        public float pathReacquireDistanceThreshold = 0.45f;
        public float pathReacquireHeadingThresholdDeg = 45f;
        public float pathReacquireProgressThreshold = -0.01f;
        public float pathReacquireLookaheadDistance = 0.45f;
        public float pathReacquireLinearSpeed = 0.12f;
        public float pathReacquireAngularSpeed = 1.0f;
        public float pathReacquireAngularGain = 0.03f;
        public float pathReacquireMaxDurationSec = 2.0f;
        public float pathReacquireExitDistance = 0.18f;
        public float pathReacquireExitHeadingDeg = 20f;
        public float pathReacquireMinForwardScale = 0.2f;

        [Header("Fallback Control")]
        public bool applyFallbackControl = false;

        [Header("On-Screen Debug Overlay")]
        public bool showOnScreenDebug = true;
        public KeyCode debugOverlayToggleKey = KeyCode.F1;
        public Vector2 debugOverlayPosition = new Vector2(15f, 15f);
        public Vector2 debugOverlaySize = new Vector2(620f, 180f);

        [Header("Upright Stabilization")]
        public bool keepRobotUpright = true;

        [Header("Collider Friction")]
        public bool zeroColliderFriction = true;

        private float lastCmdReceiptRealtime = -1f;
        private ExecutionHealthState executionHealthState = ExecutionHealthState.Nominal;
        private float lastRosRecoveryUpdateRealtime = -1f;
        private float lastRosMonitorSubscribeAttemptRealtime = -1f;
        private readonly HashSet<string> rosMonitorSubscribedTopics = new HashSet<string>();
        private float lastSBrakePressRealtime = -1f;
        private bool JoystickPresent => enableJoystickManualControl && UnityEngine.Input.GetJoystickNames().Length > 0;
        private float joystickLinearCenter;
        private float joystickAngularCenter;
        private bool joystickCenterCaptured;
        private bool warnedMissingJoystickAxis;

        protected void Start()
        {
            base.Start();
            rb = sean.robot.base_link.GetComponent<Rigidbody>();
            planVisualizer = FindObjectOfType<global::SEAN.Display.PlanVisualizer>();
            ApplyUprightConstraints();
            ApplyZeroFrictionMaterial();
            SetManualControlActive(startInManualMode, false);
            RegisterRosMonitorSubscriptions(true);
        }

        private void ApplyZeroFrictionMaterial()
        {
            if (!zeroColliderFriction || sean == null || sean.robot == null || sean.robot.base_link == null)
                return;

            PhysicMaterial mat = new PhysicMaterial("RobotZeroFriction")
            {
                dynamicFriction = 0f,
                staticFriction = 0f,
                bounciness = 0f,
                frictionCombine = PhysicMaterialCombine.Minimum,
                bounceCombine = PhysicMaterialCombine.Minimum
            };

            foreach (Collider col in sean.robot.base_link.GetComponentsInChildren<Collider>())
            {
                if (col.isTrigger) continue;
                col.sharedMaterial = mat;
            }
        }

        private void Update()
        {
            DebugJoystickPresent = JoystickPresent;
            UpdateNamedAxisDebug();

            if (UnityEngine.Input.GetKeyDown(KeyCode.LeftShift))
            {
                SetManualControlActive(!manualControlActive);
            }

            if (debugOverlayToggleKey != KeyCode.None &&
                UnityEngine.Input.GetKeyDown(debugOverlayToggleKey))
            {
                showOnScreenDebug = !showOnScreenDebug;
            }

            if (manualControlActive)
            {
                HandleManualInput();
            }
        }

        private void HandleManualInput()
        {
            float manualDesiredLin = prevLinVelocity;
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

            if (wHeld)
            {
                manualDesiredLin = manualLinearSpeed;
                DebugSBrakePressCount = 0;
                lastSBrakePressRealtime = -1f;
            }
            else if (sHeld)
            {
                bool movingForward = prevLinVelocity > brakeStopThreshold;
                bool nearStop = Mathf.Abs(prevLinVelocity) <= brakeStopThreshold;
                bool reverseArmed = DebugSBrakePressCount >= Mathf.Max(1, sPressesToEnableReverse);

                if (movingForward)
                {
                    manualDesiredLin = 0f;
                }
                else if (nearStop)
                {
                    manualDesiredLin = reverseArmed ? -manualLinearSpeed : 0f;
                }
                else
                {
                    manualDesiredLin = -manualLinearSpeed;
                }
            }

            if (UnityEngine.Input.GetKey(KeyCode.A))
            {
                manualDesiredAng = manualAngularSpeed;
            }
            else if (UnityEngine.Input.GetKey(KeyCode.D))
            {
                manualDesiredAng = -manualAngularSpeed;
            }

            if (UnityEngine.Input.GetKey(KeyCode.H))
            {
                manualDesiredLin = 0f;
                manualDesiredAng = 0f;
                DebugSBrakePressCount = 0;
                lastSBrakePressRealtime = -1f;
            }

            if (bypassUnityVelocityPostProcessing && !preserveManualVelocitySmoothing)
            {
                targetLinVelocity = manualDesiredLin;
                targetAngVelocity = manualDesiredAng;
            }
            else
            {
                float damping = preserveManualVelocitySmoothing
                    ? manualVelocityDamping
                    : velocityDamping;
                float interpolationFactor = 1.0f - Mathf.Clamp(damping, 0.0f, 0.999f);
                targetLinVelocity = Mathf.Lerp(prevLinVelocity, manualDesiredLin, interpolationFactor);
                targetAngVelocity = Mathf.Lerp(prevAngVelocity, manualDesiredAng, interpolationFactor);
            }

            prevLinVelocity = targetLinVelocity;
            prevAngVelocity = targetAngVelocity;
            lastMessageTS = Time.time;
        }

        private void FixedUpdate()
        {
            float now = Time.realtimeSinceStartup;
            DebugExpectedFixedHz = Time.fixedDeltaTime > 1e-5f ? (1f / Time.fixedDeltaTime) : 0f;
            DebugSecondsSinceLastCmd = lastCmdReceiptRealtime > 0f ? (now - lastCmdReceiptRealtime) : -1f;
            DebugCmdIsFresh = lastCmdReceiptRealtime > 0f && DebugSecondsSinceLastCmd <= maxTimeDeltaSec;
            DebugTimedOut = !manualControlActive && !DebugCmdIsFresh;
            DebugSecondsSinceRosRecoveryUpdate = lastRosRecoveryUpdateRealtime > 0f ? (now - lastRosRecoveryUpdateRealtime) : -1f;
            DebugRosConnectionError = ros != null && ros.HasConnectionError;
            UpdateRosMonitorBridgeState(now);
            SyncFallbackDebugFields(now);
            if (!DebugCmdIsFresh)
            {
                DebugCmdHz = 0f;
            }

            if (!manualControlActive && Time.time - lastMessageTS > maxTimeDeltaSec)
            {
                targetAngVelocity = 0f;
                targetLinVelocity = 0f;
                prevAngVelocity = 0f;
                prevLinVelocity = 0f;
            }

            if (rb == null) return;

            if (keepRobotUpright)
            {
                ApplyUprightConstraints();
                ForceUprightPose();
            }

            DebugActualTotalSpeed = rb.velocity.magnitude;
            Vector3 planarVelocityBefore = Vector3.ProjectOnPlane(rb.velocity, Vector3.up);
            Vector3 planarForward = Vector3.ProjectOnPlane(rb.transform.forward, Vector3.up).normalized;
            DebugActualPlanarForwardSpeed = Vector3.Dot(planarVelocityBefore, planarForward);
            DebugActualYawSpeed = -rb.angularVelocity.y;
            DebugCommandedLinSpeed = targetLinVelocity;
            DebugCommandedAngSpeed = targetAngVelocity;

            Vector3[] currentPlan = planVisualizer != null ? planVisualizer.GetCurrentPlanPositions() : null;
            VelocityFallbackSettings fallbackSettings = GetFallbackSettings();
            VelocityFallbackContext fallbackContext = GetFallbackContext(now, planarVelocityBefore, currentPlan);
            fallbackController.UpdateExecutionHealth(fallbackState, fallbackSettings, fallbackContext);
            SyncFallbackDebugFields(now);

            float appliedLinVelocity = targetLinVelocity;
            float appliedAngVelocity = targetAngVelocity;
            if (!bypassUnityVelocityPostProcessing && enableCommandShaping)
            {
                if (Mathf.Abs(appliedLinVelocity) < linearCommandDeadband) { appliedLinVelocity = 0f; }
                if (Mathf.Abs(appliedAngVelocity) < angularCommandDeadband) { appliedAngVelocity = 0f; }
                appliedLinVelocity = Mathf.Clamp(appliedLinVelocity, -maxLinearCommand, maxLinearCommand);
                appliedAngVelocity = Mathf.Clamp(appliedAngVelocity, -maxAngularCommand, maxAngularCommand);
            }

            if (!bypassUnityVelocityPostProcessing && suppressLinearDuringTurns && !manualControlActive)
            {
                float turnMagnitude = Mathf.Abs(appliedAngVelocity);
                if (turnMagnitude > turnLinearSuppressionStart)
                {
                    float t = Mathf.InverseLerp(
                        turnLinearSuppressionStart,
                        Mathf.Max(turnLinearSuppressionStart + 0.001f, turnLinearSuppressionFull),
                        turnMagnitude);
                    float scale = Mathf.Lerp(1f, turnLinearMinimumScale, t);
                    appliedLinVelocity *= scale;
                }
            }

            if (!bypassUnityVelocityPostProcessing && !manualControlActive)
            {
                if (executionHealthState == ExecutionHealthState.Degraded)
                {
                    appliedLinVelocity *= 0.85f;
                }
                else if (executionHealthState == ExecutionHealthState.Stalled ||
                         executionHealthState == ExecutionHealthState.Recovery)
                {
                    appliedLinVelocity = Mathf.Min(appliedLinVelocity, 0f);
                }
            }

            if (!applyFallbackControl)
            {
                fallbackController.CancelActiveRecovery(fallbackState);
                SyncFallbackDebugFields(now);
            }
            else
            {
                fallbackController.ApplyRecovery(fallbackState, fallbackSettings, fallbackContext, ref appliedLinVelocity, ref appliedAngVelocity);
                SyncFallbackDebugFields(now);
            }

            if (!bypassUnityVelocityPostProcessing &&
                preserveArcWhenUnderspeed &&
                !manualControlActive &&
                !fallbackState.PathReacquireActive &&
                Mathf.Abs(appliedLinVelocity) > preserveArcMinLinear &&
                DebugActualPlanarForwardSpeed * appliedLinVelocity > 0f &&
                Mathf.Abs(appliedAngVelocity) < turnBlockMinAngularCommand)
            {
                float ratio = Mathf.Clamp01(Mathf.Abs(DebugActualPlanarForwardSpeed) / Mathf.Abs(appliedLinVelocity));
                ratio = Mathf.Max(ratio, 0.5f);
                appliedAngVelocity *= ratio;
            }

            DebugAppliedLinSpeed = appliedLinVelocity;
            DebugAppliedAngSpeed = appliedAngVelocity;

            if (Mathf.Approximately(appliedAngVelocity, 0.0f) && Mathf.Approximately(rb.angularVelocity.y, 0.0f))
            {
                rb.angularVelocity = Vector3.zero;
            }
            else
            {
                rb.angularVelocity = new Vector3(0f, -1f * appliedAngVelocity, 0f);
            }

            if (Mathf.Approximately(appliedLinVelocity, 0.0f) && Mathf.Approximately(rb.velocity.magnitude, 0.0f))
            {
                rb.velocity = new Vector3(0f, rb.velocity.y, 0f);
            }
            else
            {
                Vector3 currentVelocity = rb.velocity;
                Vector3 targetWorldVelocity = rb.transform.forward * appliedLinVelocity;
                rb.velocity = new Vector3(targetWorldVelocity.x, currentVelocity.y, targetWorldVelocity.z);
            }

            if (manualControlActive && enforceManualSpeedLimit)
            {
                Vector3 planarVelocity = Vector3.ProjectOnPlane(rb.velocity, Vector3.up);
                float planarSpeed = planarVelocity.magnitude;
                if (planarSpeed > manualMaxPlanarSpeed)
                {
                    Vector3 clampedPlanarVelocity = planarVelocity.normalized * manualMaxPlanarSpeed;
                    rb.velocity = new Vector3(clampedPlanarVelocity.x, rb.velocity.y, clampedPlanarVelocity.z);
                }
            }

            fallbackController.UpdatePostApplyState(
                fallbackState,
                fallbackSettings,
                fallbackContext,
                DebugAppliedLinSpeed,
                DebugAppliedAngSpeed);
            fallbackController.UpdateUnityRecoveryDebugState(
                fallbackState,
                manualControlActive);
            SyncFallbackDebugFields(now);
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

            DebugROSLinSpeed = (float)msg.linear.x;
            DebugROSAngSpeed = (float)msg.angular.z;

            if (!manualControlActive)
            {
                float rosLinVelocity = DebugROSLinSpeed;
                float rosAngVelocity = DebugROSAngSpeed;
                if (bypassUnityVelocityPostProcessing)
                {
                    targetLinVelocity = rosLinVelocity;
                    targetAngVelocity = rosAngVelocity;
                }
                else
                {
                    float interpolationFactor = 1.0f - Mathf.Clamp(velocityDamping, 0.0f, 0.999f);
                    targetLinVelocity = Mathf.Lerp(prevLinVelocity, rosLinVelocity, interpolationFactor);
                    targetAngVelocity = Mathf.Lerp(prevAngVelocity, rosAngVelocity, interpolationFactor);
                }

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
            else if (executionHealthState == ExecutionHealthState.Recovery)
            {
                statusText = "RECOVERY";
                statusColor = new Color(1f, 0.6f, 0.2f);
            }
            else if (executionHealthState == ExecutionHealthState.Stalled)
            {
                statusText = DebugRecoveryCooldownRemaining > 0f ? "STALLED_WAIT" : "STALLED";
                statusColor = new Color(1f, 0.78f, 0.2f);
            }
            else if (executionHealthState == ExecutionHealthState.Degraded)
            {
                statusText = "DEGRADED";
                statusColor = new Color(0.95f, 0.9f, 0.35f);
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

            string rosUpdate = DebugSecondsSinceRosRecoveryUpdate >= 0f
                ? $"{DebugSecondsSinceRosRecoveryUpdate:F1}s"
                : "n/a";
            string unityStateLabel = fallbackController.GetUnityStateLabel(fallbackState);
            string unityDetailLabel = fallbackController.GetUnityDetailLabel(fallbackState);
            string rosStateLabel = GetRosStateLabel();
            string rosDetailLabel = GetRosDetailLabel();
            string overlayText =
                $"[{statusText}]  exec={DebugExecutionState}\n" +
                $"cmd fresh={DebugCmdIsFresh}  hz={DebugCmdHz:F1}  dt={DebugSecondsSinceLastCmd:F2}s\n" +
                $"cmd lin/ang: {DebugCommandedLinSpeed:F2} / {DebugCommandedAngSpeed:F2}\n" +
                $"app lin/ang: {DebugAppliedLinSpeed:F2} / {DebugAppliedAngSpeed:F2}\n" +
                $"act lin/yaw: {DebugActualPlanarForwardSpeed:F2} / {DebugActualYawSpeed:F2}  total={DebugActualTotalSpeed:F2}\n" +
                $"plan dist={DebugDistanceToPlan:F2}  head err={DebugHeadingErrorToPlanDeg:F0}  prog={DebugPlanProgressRate:F2}\n" +
                $"UNITY: {unityStateLabel}  cooldown={DebugRecoveryCooldownRemaining:F1}s\n" +
                $"unity raw={unityDetailLabel}\n" +
                $"ROS: {rosStateLabel}  update={rosUpdate}  connErr={DebugRosConnectionError}\n" +
                $"ros raw={rosDetailLabel}  topic={DebugRosMonitorLastTopic}";

            GUIStyle style = new GUIStyle(GUI.skin.label);
            style.alignment = TextAnchor.UpperLeft;
            style.fontSize = 18;
            style.richText = false;
            style.wordWrap = true;
            style.padding = new RectOffset(12, 12, 10, 10);
            style.normal.textColor = statusColor;

            float textHeight = style.CalcHeight(new GUIContent(overlayText), debugOverlaySize.x);
            float panelHeight = Mathf.Max(debugOverlaySize.y, textHeight + 20f);
            Rect rect = new Rect(debugOverlayPosition.x, debugOverlayPosition.y, debugOverlaySize.x, panelHeight);

            Color previousColor = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.85f);
            GUI.DrawTexture(rect, Texture2D.whiteTexture);

            GUI.color = Color.white;
            GUI.Label(rect, overlayText, style);
            GUI.color = previousColor;
        }

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

            float value = GetAxisSafely(ResolveJoystickAxisName(axisName));
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
            DebugAxisHorizontal = GetAxisSafely("Horizontal");
            DebugAxisVertical = GetAxisSafely("Vertical");
            DebugAxisRHorizontal = GetAxisSafely("LogitechTwist");
            DebugAxisRVertical = GetAxisSafely("LogitechThrottle");
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
                return UnityEngine.Input.GetAxis(axisName);
            }
            catch (System.ArgumentException)
            {
                if (!warnedMissingJoystickAxis)
                {
                    Debug.LogWarning($"[VelocityController] Input axis '{axisName}' is not configured. Joystick value forced to 0.", this);
                    warnedMissingJoystickAxis = true;
                }
                return 0f;
            }
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
            fallbackController.ResetState(fallbackState);
            SyncFallbackDebugFields(Time.realtimeSinceStartup);
            executionHealthState = ExecutionHealthState.Nominal;
            DebugExecutionState = executionHealthState.ToString();

            if (rb != null)
            {
                rb.velocity = new Vector3(0f, rb.velocity.y, 0f);
                rb.angularVelocity = Vector3.zero;
            }
        }

        private VelocityFallbackSettings GetFallbackSettings()
        {
            return new VelocityFallbackSettings
            {
                UsePlanAwareExecutionMonitor = usePlanAwareExecutionMonitor,
                EnablePathReacquisition = enablePathReacquisition,
                StuckMinCommandedSpeed = stuckMinCommandedSpeed,
                StuckMaxActualPlanarSpeed = stuckMaxActualPlanarSpeed,
                StuckHoldSeconds = stuckHoldSeconds,
                TurnBlockMinAngularCommand = turnBlockMinAngularCommand,
                TurnBlockMaxActualYawSpeed = turnBlockMaxActualYawSpeed,
                TurnBlockMaxPlanarSpeed = turnBlockMaxPlanarSpeed,
                TurnBlockHoldSeconds = turnBlockHoldSeconds,
                DegradedDistanceToPlan = degradedDistanceToPlan,
                StalledDistanceToPlan = stalledDistanceToPlan,
                DegradedHeadingErrorDeg = degradedHeadingErrorDeg,
                StalledHeadingErrorDeg = stalledHeadingErrorDeg,
                MinPlanProgressRate = minPlanProgressRate,
                ExecutionStateWindowSec = executionStateWindowSec,
                DegradedLinearCommandThreshold = degradedLinearCommandThreshold,
                StalledLinearCommandThreshold = stalledLinearCommandThreshold,
                DegradedTrackingErrorThreshold = degradedTrackingErrorThreshold,
                StalledTrackingErrorThreshold = stalledTrackingErrorThreshold,
                PathReacquireDistanceThreshold = pathReacquireDistanceThreshold,
                PathReacquireHeadingThresholdDeg = pathReacquireHeadingThresholdDeg,
                PathReacquireProgressThreshold = pathReacquireProgressThreshold,
                PathReacquireLookaheadDistance = pathReacquireLookaheadDistance,
                PathReacquireLinearSpeed = pathReacquireLinearSpeed,
                PathReacquireAngularSpeed = pathReacquireAngularSpeed,
                PathReacquireAngularGain = pathReacquireAngularGain,
                PathReacquireMaxDurationSec = pathReacquireMaxDurationSec,
                PathReacquireExitDistance = pathReacquireExitDistance,
                PathReacquireExitHeadingDeg = pathReacquireExitHeadingDeg,
                PathReacquireMinForwardScale = pathReacquireMinForwardScale,
                MaxLinearCommand = maxLinearCommand
            };
        }

        private VelocityFallbackContext GetFallbackContext(float now, Vector3 planarVelocityBefore, Vector3[] currentPlan)
        {
            return new VelocityFallbackContext
            {
                ManualControlActive = manualControlActive,
                RealtimeSinceStartup = now,
                FixedDeltaTime = Time.fixedDeltaTime,
                CommandedLinSpeed = DebugCommandedLinSpeed,
                CommandedAngSpeed = DebugCommandedAngSpeed,
                ActualPlanarForwardSpeed = DebugActualPlanarForwardSpeed,
                ActualYawSpeed = DebugActualYawSpeed,
                PlanarVelocityBefore = planarVelocityBefore,
                Rigidbody = rb,
                Plan = currentPlan
            };
        }

        private void SyncFallbackDebugFields(float now)
        {
            DebugDistanceToPlan = fallbackState.DebugDistanceToPlan;
            DebugHeadingErrorToPlanDeg = fallbackState.DebugHeadingErrorToPlanDeg;
            DebugPlanProgressRate = fallbackState.DebugPlanProgressRate;
            DebugTrackingError = fallbackState.DebugTrackingError;
            DebugIsLikelyStuck = fallbackState.DebugIsLikelyStuck;
            DebugIsTurnBlocked = fallbackState.DebugIsTurnBlocked;
            DebugUnityRecoveryState = fallbackState.DebugUnityRecoveryState;
            DebugUnityRecoveryReason = fallbackState.DebugUnityRecoveryReason;
            DebugPathReacquireActive = fallbackState.DebugPathReacquireActive;
            DebugPathReacquireTargetDistance = fallbackState.DebugPathReacquireTargetDistance;
            DebugPathReacquireTargetHeadingDeg = fallbackState.DebugPathReacquireTargetHeadingDeg;
            executionHealthState = fallbackState.ExecutionHealthState;
            DebugExecutionState = executionHealthState.ToString();
            DebugRecoveryCooldownRemaining = fallbackController.GetRecoveryCooldownRemaining(fallbackState, now);
        }

        private void RosRecoveryActiveMessage(string topic, RosMessageTypes.Std.MBool msg)
        {
            DebugRosMonitorEverReceived = true;
            DebugRosMonitorLastTopic = topic;
            DebugRosRecoveryActive = msg != null && msg.data;
            lastRosRecoveryUpdateRealtime = Time.realtimeSinceStartup;
        }

        private void RosRecoveryStatusMessage(string topic, RosMessageTypes.Std.MString msg)
        {
            DebugRosMonitorEverReceived = true;
            DebugRosMonitorLastTopic = topic;
            DebugRosRecoveryStatus = msg != null && !string.IsNullOrWhiteSpace(msg.data)
                ? msg.data
                : "unknown";
            lastRosRecoveryUpdateRealtime = Time.realtimeSinceStartup;
        }

        private void MoveBaseStatusMessage(string topic, RosMessageTypes.Actionlib.MGoalStatusArray msg)
        {
            if (msg == null || msg.status_list == null || msg.status_list.Length == 0)
                return;

            DebugRosMonitorEverReceived = true;
            DebugRosMonitorLastTopic = topic;
            var latest = msg.status_list[msg.status_list.Length - 1];
            string derivedStatus;
            switch (latest.status)
            {
                case 0: derivedStatus = "pending"; break;
                case 1: derivedStatus = "navigating"; break;
                case 2: derivedStatus = "preempted"; break;
                case 3: derivedStatus = "succeeded"; break;
                case 4: derivedStatus = "aborted"; break;
                case 5: derivedStatus = "rejected"; break;
                case 6: derivedStatus = "preempting"; break;
                case 7: derivedStatus = "recalling"; break;
                case 8: derivedStatus = "recalled"; break;
                case 9: derivedStatus = "lost"; break;
                default: derivedStatus = "unknown"; break;
            }

            if (!DebugRosRecoveryActive)
                DebugRosRecoveryStatus = derivedStatus;

            lastRosRecoveryUpdateRealtime = Time.realtimeSinceStartup;
        }

        private void RegisterRosMonitorSubscriptions(bool force = false)
        {
            if (!monitorRosRecovery)
                return;

            if (ros == null)
                ros = ROSConnection.instance;

            if (ros == null)
                return;

            float now = Time.realtimeSinceStartup;
            if (!force && lastRosRecoveryUpdateRealtime > 0f)
                return;

            if (!force &&
                lastRosMonitorSubscribeAttemptRealtime > 0f &&
                now - lastRosMonitorSubscribeAttemptRealtime < rosMonitorSubscribeRetrySec)
            {
                return;
            }

            foreach (string topic in GetTopicVariants(rosRecoveryActiveTopic))
            {
                string capturedTopic = topic;
                if (rosMonitorSubscribedTopics.Add("bool:" + capturedTopic))
                    ros.Subscribe<RosMessageTypes.Std.MBool>(capturedTopic, msg => RosRecoveryActiveMessage(capturedTopic, msg));
            }

            foreach (string topic in GetTopicVariants(rosRecoveryStatusTopic))
            {
                string capturedTopic = topic;
                if (rosMonitorSubscribedTopics.Add("string:" + capturedTopic))
                    ros.Subscribe<RosMessageTypes.Std.MString>(capturedTopic, msg => RosRecoveryStatusMessage(capturedTopic, msg));
            }

            foreach (string topic in GetTopicVariants(moveBaseStatusTopic))
            {
                string capturedTopic = topic;
                if (rosMonitorSubscribedTopics.Add("goal_status:" + capturedTopic))
                    ros.Subscribe<RosMessageTypes.Actionlib.MGoalStatusArray>(capturedTopic, msg => MoveBaseStatusMessage(capturedTopic, msg));
            }

            lastRosMonitorSubscribeAttemptRealtime = now;
            if (!DebugRosMonitorEverReceived && string.IsNullOrWhiteSpace(DebugRosMonitorLastTopic))
                DebugRosMonitorLastTopic = "waiting";
        }

        private void UpdateRosMonitorBridgeState(float now)
        {
            if (!monitorRosRecovery || lastRosRecoveryUpdateRealtime > 0f)
                return;

            if (ros == null)
            {
                DebugRosRecoveryStatus = "bridge_missing";
                DebugRosMonitorLastTopic = "none";
                return;
            }

            if (lastRosMonitorSubscribeAttemptRealtime <= 0f)
            {
                DebugRosRecoveryStatus = "sub_pending";
                DebugRosMonitorLastTopic = "pending";
                return;
            }

            float sinceSubscribeAttempt = now - lastRosMonitorSubscribeAttemptRealtime;
            if (DebugCmdIsFresh && sinceSubscribeAttempt >= rosMonitorBridgeGraceSec)
            {
                DebugRosRecoveryStatus = "bridge_no_ros_state";
                DebugRosMonitorLastTopic = "no_callback";
                return;
            }

            DebugRosRecoveryStatus = "waiting_ros_state";
            if (string.IsNullOrWhiteSpace(DebugRosMonitorLastTopic))
                DebugRosMonitorLastTopic = "waiting";
        }

        private string GetRosStateLabel()
        {
            if (DebugRosConnectionError)
                return "BRIDGE ERROR";

            if (DebugRosRecoveryActive)
                return "ACTIVE RECOVERY";

            switch (DebugRosRecoveryStatus)
            {
                case "recent_recovery": return "RECENT RECOVERY";
                case "rotate_recovery": return "ACTIVE RECOVERY";
                case "clear_costmap":
                case "clear_costmaps":
                case "recovery_behavior":
                case "oscillation":
                    return "RECOVERY SIGNAL";
                case "navigating":
                case "active":
                    return "NAVIGATING";
                case "pending":
                case "preempting":
                    return "GOAL TRANSITION";
                case "succeeded":
                    return "GOAL REACHED";
                case "aborted":
                case "rejected":
                case "lost":
                    return "NAV FAILED";
                case "preempted":
                case "recalled":
                case "recalling":
                    return "GOAL CHANGED";
                case "idle":
                    return "IDLE";
                case "bridge_missing":
                    return "BRIDGE MISSING";
                case "bridge_no_ros_state":
                    return "NO ROS STATE";
                case "sub_pending":
                case "waiting_ros_state":
                    return "WAITING FOR ROS";
                case "startup":
                    return "ROS STARTING";
                default:
                    return string.IsNullOrWhiteSpace(DebugRosRecoveryStatus)
                        ? "ROS UNKNOWN"
                        : DebugRosRecoveryStatus.ToUpperInvariant();
            }
        }

        private string GetRosDetailLabel()
        {
            string rawStatus = string.IsNullOrWhiteSpace(DebugRosRecoveryStatus)
                ? "unknown"
                : DebugRosRecoveryStatus;
            return $"{rawStatus} / active={DebugRosRecoveryActive}";
        }

        private IEnumerable<string> GetTopicVariants(string topic)
        {
            HashSet<string> variants = new HashSet<string>();
            if (string.IsNullOrWhiteSpace(topic))
                return variants;

            string trimmed = topic.Trim();
            variants.Add(trimmed);
            if (trimmed.StartsWith("/"))
                variants.Add(trimmed.TrimStart('/'));
            else
                variants.Add("/" + trimmed);

            return variants;
        }

        public void SetManualControlActive(bool isManual)
        {
            SetManualControlActive(isManual, true);
        }

        private void SetManualControlActive(bool isManual, bool logChange)
        {
            if (manualControlActive == isManual)
            {
                startInManualMode = isManual;
                return;
            }

            startInManualMode = isManual;
            manualControlActive = isManual;

            if (manualControlActive)
            {
                if (logChange)
                    Debug.Log("Manual control activated");

                fallbackController.CancelActiveRecovery(fallbackState);
                SyncFallbackDebugFields(Time.realtimeSinceStartup);
                CaptureJoystickCenter();
            }
            else
            {
                if (logChange)
                    Debug.Log("ROS control activated");

                joystickCenterCaptured = false;
            }

            prevLinVelocity = targetLinVelocity;
            prevAngVelocity = targetAngVelocity;
            DebugSBrakePressCount = 0;
            lastSBrakePressRealtime = -1f;
        }
    }
}
