using System.Collections.Generic;
using UnityEngine;

namespace SessionReview
{
    /// <summary>
    /// Drives pedestrian walk-cycle animation during review playback. Review freezes
    /// Time.timeScale, so Animators stop and replayed avatars slide around in a frozen
    /// stride. For every replayed transform that belongs to an agent avatar, this driver
    /// turns off root motion (the replay owns the transform), silences the live agent
    /// behaviour that normally writes the parameters, feeds Forward/Strafe/Idling from
    /// the velocity of the recorded motion — the same mapping the live game uses for
    /// transform-driven avatars (ManualWheelchairController.UpdateAnimator) — and pumps
    /// the graph manually with Animator.Update(), which advances and evaluates it
    /// regardless of the frozen time scale. (Switching Animator.updateMode to
    /// UnscaledTime at runtime is not reliable: the mode is latched when the playable
    /// graph starts.) Everything is restored when the review exits.
    /// </summary>
    public class ReplayPedestrianAnimator
    {
        // Same divisor SEAN.Scenario.Agents.Base / ManualWheelchairController apply
        // to the Forward/Strafe blend parameters.
        private const float AnimationSmoothing = 0.6f;
        private const float IdleSpeedThreshold = 0.1f;
        // The recording samples at 10 Hz, so finite-difference velocities are stepwise;
        // this exponential smoothing rate (per unscaled second) evens out the walk cycle.
        private const float VelocitySmoothRate = 8f;

        private class Entry
        {
            public Animator animator;
            public Transform transform;
            public Behaviour liveAgent;
            public bool liveAgentWasEnabled;
            public bool savedRootMotion;
            public float savedSpeed;
            public Vector3 targetVelocity;
            public Vector3 smoothedVelocity;
        }

        private readonly Dictionary<Transform, Entry> entries = new Dictionary<Transform, Entry>();
        private readonly HashSet<Transform> rejected = new HashSet<Transform>();

        /// <summary>
        /// Report the world-space velocity of a replayed transform at the current
        /// playback time (in recorded time, unscaled by playback rate). Transforms
        /// without an agent Animator are ignored.
        /// </summary>
        public void SetMotion(Transform t, Vector3 worldVelocity)
        {
            if (t == null || rejected.Contains(t))
                return;

            if (!entries.TryGetValue(t, out Entry entry))
            {
                entry = Capture(t);
                if (entry == null)
                {
                    rejected.Add(t);
                    return;
                }
                entries[t] = entry;
            }

            worldVelocity.y = 0f;
            entry.targetVelocity = worldVelocity;
        }

        /// <summary>Write animator state for this frame. Call every frame while rewinding.</summary>
        public void Tick(bool isPlaying, float playbackSpeed)
        {
            float dt = Time.unscaledDeltaTime;
            float smoothing = 1f - Mathf.Exp(-VelocitySmoothRate * dt);

            foreach (Entry entry in entries.Values)
            {
                if (entry.animator == null || entry.transform == null || !entry.animator.isActiveAndEnabled)
                    continue;

                // Signed by playback direction so reverse playback back-pedals.
                Vector3 target = isPlaying ? entry.targetVelocity * playbackSpeed : Vector3.zero;
                entry.smoothedVelocity = Vector3.Lerp(entry.smoothedVelocity, target, smoothing);

                float speed = entry.smoothedVelocity.magnitude;
                AgentControlTuning.UpdateLocomotionAnimator(
                    entry.animator,
                    entry.transform,
                    entry.smoothedVelocity,
                    speed,
                    false,
                    AgentControlTuning.EstimateDriveYaw(entry.transform));

                // Walk cycle at leg speed while moving, idle animation at the playback
                // rate while stationary; a paused review freezes the pose entirely.
                float rate = !isPlaying
                    ? 0f
                    : (speed > IdleSpeedThreshold ? speed : Mathf.Abs(playbackSpeed));

                entry.animator.speed = 1f;
                if (rate > 0f && dt > 0f)
                    entry.animator.Update(dt * rate);
            }
        }

        /// <summary>Restore every captured animator and re-enable the live agent scripts.</summary>
        public void End()
        {
            foreach (Entry entry in entries.Values)
            {
                if (entry.animator != null)
                {
                    entry.animator.applyRootMotion = entry.savedRootMotion;
                    entry.animator.speed = entry.savedSpeed;
                    AgentControlTuning.UpdateLocomotionAnimator(
                        entry.animator,
                        entry.transform,
                        Vector3.zero,
                        0f,
                        false,
                        entry.transform != null ? AgentControlTuning.EstimateDriveYaw(entry.transform) : 0f);
                }
                if (entry.liveAgent != null)
                    entry.liveAgent.enabled = entry.liveAgentWasEnabled;
            }
            entries.Clear();
            rejected.Clear();
        }

        private Entry Capture(Transform t)
        {
            Animator animator = t.GetComponentInChildren<Animator>();
            if (animator == null)
                animator = t.GetComponentInParent<Animator>();
            if (animator == null || animator.runtimeAnimatorController == null)
                return null;

            // Only animate agent avatars: SEAN pedestrians (Base), the player
            // (ManualWheelchairController), or anything with the shared walk-blend
            // "Forward" parameter. Leaves e.g. robot-mounted animators untouched.
            var liveAgent = animator.GetComponentInParent<SEAN.Scenario.Agents.Base>();
            bool isAvatar = liveAgent != null ||
                animator.GetComponentInParent<IVI.ManualWheelchairController>() != null ||
                HasFloatParam(animator, "Forward");
            if (!isAvatar)
            {
                Debug.Log($"[ReplayAnim] Skipping '{animator.gameObject.name}': not an agent avatar.");
                return null;
            }

            var entry = new Entry
            {
                animator = animator,
                transform = t,
                savedRootMotion = animator.applyRootMotion,
                savedSpeed = animator.speed,
            };

            animator.applyRootMotion = false;

            // Base.Move() writes the same parameters every Update from the (stale)
            // live velocity even while the sim is frozen — disable it so the replay
            // values are the only writer. Restored in End().
            if (liveAgent != null)
            {
                entry.liveAgent = liveAgent;
                entry.liveAgentWasEnabled = liveAgent.enabled;
                liveAgent.enabled = false;
            }

            Debug.Log($"[ReplayAnim] Driving replay animation for '{animator.gameObject.name}' " +
                      $"(controller: {animator.runtimeAnimatorController.name}, " +
                      $"hasForward: {HasFloatParam(animator, "Forward")}, agent: {(liveAgent != null ? liveAgent.GetType().Name : "none")})");
            return entry;
        }

        private static bool HasFloatParam(Animator animator, string name)
        {
            foreach (AnimatorControllerParameter p in animator.parameters)
            {
                if (p.type == AnimatorControllerParameterType.Float && p.name == name)
                    return true;
            }
            return false;
        }
    }
}
