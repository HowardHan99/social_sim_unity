using UnityEngine;

namespace SEAN.Control
{
    public enum ExecutionHealthState
    {
        Nominal,
        Degraded,
        Stalled,
        Recovery
    }

    public struct VelocityFallbackSettings
    {
        public bool UsePlanAwareExecutionMonitor;
        public bool EnablePathReacquisition;
        public float StuckMinCommandedSpeed;
        public float StuckMaxActualPlanarSpeed;
        public float StuckHoldSeconds;
        public float TurnBlockMinAngularCommand;
        public float TurnBlockMaxActualYawSpeed;
        public float TurnBlockMaxPlanarSpeed;
        public float TurnBlockHoldSeconds;
        public float DegradedDistanceToPlan;
        public float StalledDistanceToPlan;
        public float DegradedHeadingErrorDeg;
        public float StalledHeadingErrorDeg;
        public float MinPlanProgressRate;
        public float ExecutionStateWindowSec;
        public float DegradedLinearCommandThreshold;
        public float StalledLinearCommandThreshold;
        public float DegradedTrackingErrorThreshold;
        public float StalledTrackingErrorThreshold;
        public float PathReacquireDistanceThreshold;
        public float PathReacquireHeadingThresholdDeg;
        public float PathReacquireProgressThreshold;
        public float PathReacquireLookaheadDistance;
        public float PathReacquireLinearSpeed;
        public float PathReacquireAngularSpeed;
        public float PathReacquireAngularGain;
        public float PathReacquireMaxDurationSec;
        public float PathReacquireExitDistance;
        public float PathReacquireExitHeadingDeg;
        public float PathReacquireMinForwardScale;
        public float MaxLinearCommand;
    }

    public struct VelocityFallbackContext
    {
        public bool ManualControlActive;
        public float RealtimeSinceStartup;
        public float FixedDeltaTime;
        public float CommandedLinSpeed;
        public float CommandedAngSpeed;
        public float ActualPlanarForwardSpeed;
        public float ActualYawSpeed;
        public Vector3 PlanarVelocityBefore;
        public Rigidbody Rigidbody;
        public Vector3[] Plan;
    }

    public sealed class VelocityFallbackState
    {
        public float DebugDistanceToPlan;
        public float DebugHeadingErrorToPlanDeg;
        public float DebugPlanProgressRate;
        public float DebugTrackingError;
        public bool DebugIsLikelyStuck;
        public bool DebugIsTurnBlocked;
        public string DebugUnityRecoveryState = "idle";
        public string DebugUnityRecoveryReason = "none";
        public bool DebugPathReacquireActive;
        public float DebugPathReacquireTargetDistance;
        public float DebugPathReacquireTargetHeadingDeg;
        public ExecutionHealthState ExecutionHealthState = ExecutionHealthState.Nominal;

        public float StuckAccumulatedSeconds;
        public float TurnBlockedAccumulatedSeconds;
        public float DegradedAccumulatedSeconds;
        public float StalledAccumulatedSeconds;
        public float PreviousDistanceToPlan = -1f;
        public bool PathReacquireActive;
        public float PathReacquireUntilRealtime = -1f;
    }

    public sealed class VelocityFallbackController
    {
        public float GetRecoveryCooldownRemaining(VelocityFallbackState state, float now)
        {
            return 0f;
        }

        public void CancelActiveRecovery(VelocityFallbackState state)
        {
            state.PathReacquireActive = false;
            state.PathReacquireUntilRealtime = -1f;
        }

        public void ResetState(VelocityFallbackState state)
        {
            state.StuckAccumulatedSeconds = 0f;
            state.TurnBlockedAccumulatedSeconds = 0f;
            state.DegradedAccumulatedSeconds = 0f;
            state.StalledAccumulatedSeconds = 0f;
            state.PreviousDistanceToPlan = -1f;
            state.PathReacquireActive = false;
            state.PathReacquireUntilRealtime = -1f;
            state.ExecutionHealthState = ExecutionHealthState.Nominal;
            state.DebugDistanceToPlan = 0f;
            state.DebugHeadingErrorToPlanDeg = 0f;
            state.DebugPlanProgressRate = 0f;
            state.DebugTrackingError = 0f;
            state.DebugIsLikelyStuck = false;
            state.DebugIsTurnBlocked = false;
            state.DebugUnityRecoveryState = "idle";
            state.DebugUnityRecoveryReason = "none";
            state.DebugPathReacquireActive = false;
            state.DebugPathReacquireTargetDistance = 0f;
            state.DebugPathReacquireTargetHeadingDeg = 0f;
        }

        public void UpdateExecutionHealth(VelocityFallbackState state, VelocityFallbackSettings settings, VelocityFallbackContext context)
        {
            if (context.ManualControlActive || !settings.UsePlanAwareExecutionMonitor)
            {
                state.DebugDistanceToPlan = 0f;
                state.DebugHeadingErrorToPlanDeg = 0f;
                state.DebugPlanProgressRate = 0f;
                state.DebugTrackingError = 0f;
                state.ExecutionHealthState = state.PathReacquireActive
                    ? ExecutionHealthState.Recovery
                    : ExecutionHealthState.Nominal;
                return;
            }

            Vector3[] plan = context.Plan;
            if (plan == null || plan.Length < 2 || context.Rigidbody == null)
            {
                state.DebugDistanceToPlan = 0f;
                state.DebugHeadingErrorToPlanDeg = 0f;
                state.DebugPlanProgressRate = 0f;
                state.DebugTrackingError =
                    Mathf.Abs(context.CommandedLinSpeed - context.ActualPlanarForwardSpeed) +
                    Mathf.Abs(context.CommandedAngSpeed - context.ActualYawSpeed);
                state.ExecutionHealthState = state.PathReacquireActive
                    ? ExecutionHealthState.Recovery
                    : ExecutionHealthState.Nominal;
                return;
            }

            Vector3 robotPos = context.Rigidbody.position;
            Vector3 tangent;
            float distanceToPlan = DistanceToPlan(robotPos, plan, out tangent);
            tangent = Vector3.ProjectOnPlane(tangent, Vector3.up);
            if (tangent.sqrMagnitude < 1e-4f)
                tangent = Vector3.ProjectOnPlane(context.Rigidbody.transform.forward, Vector3.up);
            tangent.Normalize();

            Vector3 planarForward = Vector3.ProjectOnPlane(context.Rigidbody.transform.forward, Vector3.up).normalized;
            float headingErrorDeg = tangent.sqrMagnitude > 1e-4f
                ? Mathf.Abs(Vector3.SignedAngle(planarForward, tangent, Vector3.up))
                : 0f;

            float progressRate = 0f;
            if (state.PreviousDistanceToPlan >= 0f && context.FixedDeltaTime > 1e-5f)
                progressRate = (state.PreviousDistanceToPlan - distanceToPlan) / context.FixedDeltaTime;
            state.PreviousDistanceToPlan = distanceToPlan;

            float trackingError =
                Mathf.Abs(context.CommandedLinSpeed - context.ActualPlanarForwardSpeed) +
                Mathf.Abs(context.CommandedAngSpeed - context.ActualYawSpeed);

            state.DebugDistanceToPlan = distanceToPlan;
            state.DebugHeadingErrorToPlanDeg = headingErrorDeg;
            state.DebugPlanProgressRate = progressRate;
            state.DebugTrackingError = trackingError;

            bool forwardProgressPoor =
                Mathf.Abs(context.CommandedLinSpeed) > settings.DegradedLinearCommandThreshold &&
                progressRate < settings.MinPlanProgressRate &&
                distanceToPlan > settings.DegradedDistanceToPlan;
            bool turnExecutionPoor =
                Mathf.Abs(context.CommandedAngSpeed) > settings.TurnBlockMinAngularCommand &&
                headingErrorDeg >= settings.DegradedHeadingErrorDeg &&
                Mathf.Abs(context.ActualYawSpeed) <= settings.TurnBlockMaxActualYawSpeed &&
                distanceToPlan > settings.DegradedDistanceToPlan;

            bool degradedCandidate =
                (distanceToPlan >= settings.DegradedDistanceToPlan &&
                 trackingError >= settings.DegradedTrackingErrorThreshold &&
                 progressRate <= settings.MinPlanProgressRate) ||
                forwardProgressPoor ||
                turnExecutionPoor;
            bool stalledCandidate =
                (distanceToPlan >= settings.StalledDistanceToPlan &&
                 trackingError >= settings.StalledTrackingErrorThreshold &&
                 progressRate <= 0f) ||
                (Mathf.Abs(context.CommandedLinSpeed) > settings.StalledLinearCommandThreshold &&
                 progressRate <= 0f &&
                 context.PlanarVelocityBefore.magnitude < 0.05f &&
                 distanceToPlan > settings.DegradedDistanceToPlan) ||
                (Mathf.Abs(context.CommandedAngSpeed) > settings.TurnBlockMinAngularCommand &&
                 headingErrorDeg >= settings.StalledHeadingErrorDeg &&
                 Mathf.Abs(context.ActualYawSpeed) <= settings.TurnBlockMaxActualYawSpeed &&
                 distanceToPlan > settings.DegradedDistanceToPlan);

            if (degradedCandidate)
                state.DegradedAccumulatedSeconds += context.FixedDeltaTime;
            else
                state.DegradedAccumulatedSeconds = Mathf.Max(0f, state.DegradedAccumulatedSeconds - context.FixedDeltaTime * 2f);

            if (stalledCandidate)
                state.StalledAccumulatedSeconds += context.FixedDeltaTime;
            else
                state.StalledAccumulatedSeconds = Mathf.Max(0f, state.StalledAccumulatedSeconds - context.FixedDeltaTime * 2f);

            if (state.PathReacquireActive)
                state.ExecutionHealthState = ExecutionHealthState.Recovery;
            else if (state.StalledAccumulatedSeconds >= settings.ExecutionStateWindowSec)
                state.ExecutionHealthState = ExecutionHealthState.Stalled;
            else if (state.DegradedAccumulatedSeconds >= settings.ExecutionStateWindowSec * 0.5f)
                state.ExecutionHealthState = ExecutionHealthState.Degraded;
            else
                state.ExecutionHealthState = ExecutionHealthState.Nominal;
        }

        public void ApplyRecovery(
            VelocityFallbackState state,
            VelocityFallbackSettings settings,
            VelocityFallbackContext context,
            ref float appliedLinVelocity,
            ref float appliedAngVelocity)
        {
            bool planAvailableForRecovery = context.Plan != null && context.Plan.Length >= 2;
            bool shouldStartPathReacquire =
                settings.EnablePathReacquisition &&
                !context.ManualControlActive &&
                !state.PathReacquireActive &&
                planAvailableForRecovery &&
                ShouldStartPathReacquire(state, settings);

            if (shouldStartPathReacquire)
            {
                state.PathReacquireActive = true;
                state.PathReacquireUntilRealtime = context.RealtimeSinceStartup + Mathf.Max(0.25f, settings.PathReacquireMaxDurationSec);
                state.ExecutionHealthState = ExecutionHealthState.Recovery;
            }

            if (!context.ManualControlActive && state.PathReacquireActive)
            {
                if (!ApplyPathReacquisitionControl(state, settings, context, ref appliedLinVelocity, ref appliedAngVelocity))
                {
                    state.PathReacquireActive = false;
                    state.PathReacquireUntilRealtime = -1f;
                }
                else
                {
                    state.ExecutionHealthState = ExecutionHealthState.Recovery;
                }
            }
        }

        public void UpdatePostApplyState(
            VelocityFallbackState state,
            VelocityFallbackSettings settings,
            VelocityFallbackContext context,
            float appliedLinVelocity,
            float appliedAngVelocity)
        {
            bool stuckCandidate =
                Mathf.Abs(appliedLinVelocity) >= settings.StuckMinCommandedSpeed &&
                Mathf.Abs(context.ActualPlanarForwardSpeed) <= settings.StuckMaxActualPlanarSpeed;
            bool turnBlockedCandidate =
                Mathf.Abs(appliedAngVelocity) >= settings.TurnBlockMinAngularCommand &&
                Mathf.Abs(context.ActualPlanarForwardSpeed) <= settings.TurnBlockMaxPlanarSpeed &&
                Mathf.Abs(context.ActualYawSpeed) <= settings.TurnBlockMaxActualYawSpeed;

            if (stuckCandidate)
                state.StuckAccumulatedSeconds += context.FixedDeltaTime;
            else
                state.StuckAccumulatedSeconds = 0f;

            if (turnBlockedCandidate)
                state.TurnBlockedAccumulatedSeconds += context.FixedDeltaTime;
            else
                state.TurnBlockedAccumulatedSeconds = 0f;

            state.DebugIsTurnBlocked = state.TurnBlockedAccumulatedSeconds >= settings.TurnBlockHoldSeconds;
            state.DebugIsLikelyStuck = state.StuckAccumulatedSeconds >= settings.StuckHoldSeconds || state.DebugIsTurnBlocked;
        }

        public void UpdateUnityRecoveryDebugState(VelocityFallbackState state, bool manualControlActive)
        {
            state.DebugPathReacquireActive = state.PathReacquireActive;

            if (manualControlActive)
            {
                state.DebugUnityRecoveryState = "manual";
                state.DebugUnityRecoveryReason = "manual_control";
                return;
            }

            if (state.PathReacquireActive)
            {
                state.DebugUnityRecoveryState = "path_reacquire";
                state.DebugUnityRecoveryReason = "force_follow_global_path";
                return;
            }

            if (state.ExecutionHealthState == ExecutionHealthState.Stalled)
            {
                state.DebugUnityRecoveryState = "stalled";
                state.DebugUnityRecoveryReason = state.DebugIsTurnBlocked ? "turn_blocked" : "tracking_stalled";
                return;
            }

            if (state.ExecutionHealthState == ExecutionHealthState.Degraded)
            {
                state.DebugUnityRecoveryState = "degraded";
                state.DebugUnityRecoveryReason = "tracking_degraded";
                return;
            }

            state.DebugUnityRecoveryState = "nominal";
            state.DebugUnityRecoveryReason = "none";
        }

        public string GetUnityStateLabel(VelocityFallbackState state)
        {
            switch (state.DebugUnityRecoveryState)
            {
                case "manual": return "MANUAL";
                case "path_reacquire": return "PATH REACQUIRE";
                case "stalled": return "STALLED";
                case "degraded": return "DEGRADED";
                case "nominal": return "NOMINAL";
                default: return state.DebugUnityRecoveryState.ToUpperInvariant();
            }
        }

        public string GetUnityDetailLabel(VelocityFallbackState state)
        {
            return
                $"{state.DebugUnityRecoveryState} / reason={state.DebugUnityRecoveryReason} / likely_stuck={state.DebugIsLikelyStuck} / turn_blocked={state.DebugIsTurnBlocked} / path_target_dist={state.DebugPathReacquireTargetDistance:F2} / path_target_head={state.DebugPathReacquireTargetHeadingDeg:F0}";
        }

        private static float DistanceToPlan(Vector3 point, Vector3[] plan, out Vector3 tangent)
        {
            float bestDistance = float.MaxValue;
            tangent = Vector3.forward;

            for (int i = 0; i < plan.Length - 1; i++)
            {
                Vector3 a = plan[i];
                Vector3 b = plan[i + 1];
                a.y = point.y;
                b.y = point.y;
                Vector3 ab = b - a;
                float denom = ab.sqrMagnitude;
                if (denom < 1e-5f)
                    continue;

                float t = Mathf.Clamp01(Vector3.Dot(point - a, ab) / denom);
                Vector3 closest = a + ab * t;
                float d = Vector3.Distance(point, closest);
                if (d < bestDistance)
                {
                    bestDistance = d;
                    tangent = ab.normalized;
                }
            }

            return bestDistance == float.MaxValue ? 0f : bestDistance;
        }

        private static bool ShouldStartPathReacquire(VelocityFallbackState state, VelocityFallbackSettings settings)
        {
            bool badlyOffPath =
                state.DebugDistanceToPlan >= settings.PathReacquireDistanceThreshold ||
                state.DebugHeadingErrorToPlanDeg >= settings.PathReacquireHeadingThresholdDeg;
            bool progressBad =
                state.DebugPlanProgressRate <= settings.PathReacquireProgressThreshold &&
                (state.ExecutionHealthState == ExecutionHealthState.Degraded ||
                 state.ExecutionHealthState == ExecutionHealthState.Stalled ||
                 state.DebugIsLikelyStuck);

            return badlyOffPath || progressBad;
        }

        private bool ApplyPathReacquisitionControl(
            VelocityFallbackState state,
            VelocityFallbackSettings settings,
            VelocityFallbackContext context,
            ref float appliedLinVelocity,
            ref float appliedAngVelocity)
        {
            state.DebugPathReacquireTargetDistance = 0f;
            state.DebugPathReacquireTargetHeadingDeg = 0f;

            if (!settings.EnablePathReacquisition || context.Plan == null || context.Plan.Length < 2 || context.Rigidbody == null)
                return false;

            if (state.PathReacquireUntilRealtime > 0f && context.RealtimeSinceStartup >= state.PathReacquireUntilRealtime)
                return false;

            int nearestIndex;
            Vector3 nearestPoint;
            Vector3 tangent;
            float distanceToPath = GetNearestPlanPoint(context.Rigidbody.position, context.Plan, out nearestIndex, out nearestPoint, out tangent);
            if (nearestIndex < 0)
                return false;

            Vector3 targetPoint = GetPlanLookaheadPoint(context.Plan, nearestIndex, nearestPoint, settings.PathReacquireLookaheadDistance);
            Vector3 toTarget = Vector3.ProjectOnPlane(targetPoint - context.Rigidbody.position, Vector3.up);
            float targetDistance = toTarget.magnitude;

            Vector3 desiredDirection = toTarget.sqrMagnitude > 1e-4f
                ? toTarget.normalized
                : Vector3.ProjectOnPlane(tangent, Vector3.up).normalized;
            Vector3 planarForward = Vector3.ProjectOnPlane(context.Rigidbody.transform.forward, Vector3.up).normalized;
            if (planarForward.sqrMagnitude < 1e-4f)
                planarForward = Vector3.forward;

            float headingErrorDeg = desiredDirection.sqrMagnitude > 1e-4f
                ? Vector3.SignedAngle(planarForward, desiredDirection, Vector3.up)
                : 0f;

            state.DebugPathReacquireTargetDistance = targetDistance;
            state.DebugPathReacquireTargetHeadingDeg = headingErrorDeg;

            bool recovered =
                distanceToPath <= settings.PathReacquireExitDistance &&
                Mathf.Abs(headingErrorDeg) <= settings.PathReacquireExitHeadingDeg &&
                !state.DebugIsLikelyStuck;
            if (recovered)
                return false;

            float absHeading = Mathf.Abs(headingErrorDeg);
            float angular = Mathf.Clamp(
                headingErrorDeg * settings.PathReacquireAngularGain,
                -settings.PathReacquireAngularSpeed,
                settings.PathReacquireAngularSpeed);
            float forwardScale = Mathf.Clamp01(1f - (absHeading / 120f));
            forwardScale = Mathf.Max(forwardScale, settings.PathReacquireMinForwardScale);
            if (absHeading > 100f)
                forwardScale = 0f;

            appliedAngVelocity = angular;
            appliedLinVelocity = Mathf.Min(settings.PathReacquireLinearSpeed * forwardScale, settings.MaxLinearCommand);
            if (targetDistance < settings.PathReacquireExitDistance * 0.75f)
            {
                appliedLinVelocity *= Mathf.Clamp01(
                    targetDistance / Mathf.Max(0.01f, settings.PathReacquireExitDistance * 0.75f));
            }

            return true;
        }

        private static float GetNearestPlanPoint(Vector3 point, Vector3[] plan, out int nearestIndex, out Vector3 nearestPoint, out Vector3 tangent)
        {
            nearestIndex = -1;
            nearestPoint = point;
            tangent = Vector3.forward;
            float bestDistance = float.MaxValue;

            for (int i = 0; i < plan.Length - 1; i++)
            {
                Vector3 a = plan[i];
                Vector3 b = plan[i + 1];
                a.y = point.y;
                b.y = point.y;
                Vector3 ab = b - a;
                float denom = ab.sqrMagnitude;
                if (denom < 1e-5f)
                    continue;

                float t = Mathf.Clamp01(Vector3.Dot(point - a, ab) / denom);
                Vector3 closest = a + ab * t;
                float d = Vector3.Distance(point, closest);
                if (d < bestDistance)
                {
                    bestDistance = d;
                    nearestIndex = i;
                    nearestPoint = closest;
                    tangent = ab.normalized;
                }
            }

            return bestDistance == float.MaxValue ? 0f : bestDistance;
        }

        private static Vector3 GetPlanLookaheadPoint(Vector3[] plan, int nearestIndex, Vector3 nearestPoint, float lookaheadDistance)
        {
            Vector3 target = nearestPoint;
            float remaining = Mathf.Max(0.05f, lookaheadDistance);

            for (int i = Mathf.Max(0, nearestIndex); i < plan.Length - 1; i++)
            {
                Vector3 segmentStart = i == nearestIndex ? nearestPoint : plan[i];
                Vector3 segmentEnd = plan[i + 1];
                float segLen = PlanarDistance(segmentStart, segmentEnd);
                if (segLen < 1e-4f)
                    continue;

                if (remaining <= segLen)
                {
                    float t = remaining / segLen;
                    target = Vector3.Lerp(segmentStart, segmentEnd, t);
                    return target;
                }

                remaining -= segLen;
                target = segmentEnd;
            }

            return target;
        }

        private static float PlanarDistance(Vector3 a, Vector3 b)
        {
            a.y = 0f;
            b.y = 0f;
            return Vector3.Distance(a, b);
        }
    }
}
