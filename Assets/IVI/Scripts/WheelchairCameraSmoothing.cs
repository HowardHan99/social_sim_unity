using UnityEngine;

namespace IVI
{
    /// <summary>
    /// Third-person follow camera for the wheelchair avatar.
    /// Sits behind and above the avatar, smoothly following position and yaw
    /// while always looking at a point near the avatar's shoulders.
    /// Attach to a camera that is a child of the wheelchair avatar root.
    /// </summary>
    public class WheelchairCameraSmoothing : MonoBehaviour
    {
        [Tooltip("Offset behind the avatar in its local space (x=right, y=up, z=forward).")]
        public Vector3 thirdPersonOffset = new Vector3(0f, 1.9f, -1.5f);

        [Tooltip("Point on the avatar to look at, relative to avatar root (y = height above ground).")]
        public float lookAtHeight = 1.0f;

        [Tooltip("How quickly the camera catches up to the avatar position.")]
        public float positionSmoothTime = 0.15f;

        [Tooltip("How quickly the camera catches up to the avatar's yaw (seconds).")]
        public float yawSmoothTime = 0.25f;

        [Tooltip("Hard cap on camera yaw rotation speed (degrees/second).")]
        public float maxYawSpeed = 120f;

        [Tooltip("Maximum yaw offset (degrees) the camera is allowed to lag behind the body.")]
        public float maxYawOffset = 30f;

        [Tooltip("If the camera is further than this (metres) from where it should be, cut to it " +
                 "instead of flying there. Catches avatar teleports. 0 disables the cut.")]
        public float snapDistance = 3f;

        private float smoothedYaw;
        private float yawVelocity;
        private Vector3 positionVelocity;
        private bool initialized;
        private Transform followTarget;
        private ComfortMotionBlur transitionBlur;

        /// <summary>Avatar root this camera follows (null before <see cref="Start"/>).</summary>
        public Transform FollowAvatarRoot => followTarget;

        void Start()
        {
            followTarget = transform.parent;
            if (followTarget != null)
                smoothedYaw = followTarget.eulerAngles.y;
            else
                smoothedYaw = transform.eulerAngles.y;

            transform.SetParent(null, true);
            initialized = true;
        }

        void LateUpdate()
        {
            if (followTarget == null) return;

            Vector3 targetPosition = followTarget.position;
            Vector3 targetEuler = followTarget.eulerAngles;
            if (!IsFinite(targetPosition) || !IsFinite(targetEuler))
                return;

            if (!initialized)
            {
                smoothedYaw = targetEuler.y;
                initialized = true;
            }

            float targetYaw = targetEuler.y;
            if (!IsFinite(smoothedYaw) || !IsFinite(yawVelocity))
            {
                smoothedYaw = targetYaw;
                yawVelocity = 0f;
            }

            float safeYawSmoothTime = Mathf.Max(0.001f, yawSmoothTime);
            float safeMaxYawSpeed = Mathf.Max(0.001f, maxYawSpeed);
            smoothedYaw = Mathf.SmoothDampAngle(
                smoothedYaw, targetYaw, ref yawVelocity, safeYawSmoothTime, safeMaxYawSpeed);
            if (!IsFinite(smoothedYaw))
            {
                smoothedYaw = targetYaw;
                yawVelocity = 0f;
            }

            float offset = Mathf.DeltaAngle(smoothedYaw, targetYaw);
            if (Mathf.Abs(offset) > maxYawOffset)
            {
                smoothedYaw = targetYaw - Mathf.Sign(offset) * maxYawOffset;
                yawVelocity = 0f;
            }

            Quaternion yawRot = Quaternion.Euler(0f, smoothedYaw, 0f);
            Vector3 safeOffset = IsFinite(thirdPersonOffset)
                ? thirdPersonOffset
                : new Vector3(0f, 1.4f, -2.2f);
            Vector3 desiredPos = targetPosition + yawRot * safeOffset;
            if (!IsFinite(desiredPos))
                return;

            if (!IsFinite(transform.position) || !IsFinite(positionVelocity))
            {
                transform.position = desiredPos;
                positionVelocity = Vector3.zero;
            }

            // The avatar teleports on task change, trial reset and respawn. Smoothing across that
            // gap sweeps the camera through the whole world in a fraction of a second, which is a
            // motion-sickness trigger; cut to the new spot instead.
            if (snapDistance > 0f &&
                (desiredPos - transform.position).sqrMagnitude > snapDistance * snapDistance)
            {
                // Re-seed the yaw too, otherwise the camera arrives facing the old direction and
                // slews round afterwards, which is the same problem in the other axis.
                smoothedYaw = targetYaw;
                yawVelocity = 0f;
                yawRot = Quaternion.Euler(0f, smoothedYaw, 0f);
                desiredPos = targetPosition + yawRot * safeOffset;

                transform.position = desiredPos;
                positionVelocity = Vector3.zero;

                if (transitionBlur == null)
                    transitionBlur = GetComponent<ComfortMotionBlur>();
                if (transitionBlur != null)
                    transitionBlur.TriggerTransitionBlur();
            }
            else
            {
                float safePositionSmoothTime = Mathf.Max(0.001f, positionSmoothTime);
                transform.position = Vector3.SmoothDamp(
                    transform.position, desiredPos, ref positionVelocity, safePositionSmoothTime);
            }

            Vector3 lookTarget = targetPosition + Vector3.up * lookAtHeight;
            Vector3 lookDirection = lookTarget - transform.position;
            if (!IsFinite(lookDirection) || lookDirection.sqrMagnitude < 0.0001f)
                lookDirection = yawRot * Vector3.forward;

            transform.rotation = Quaternion.LookRotation(lookDirection, Vector3.up);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static bool IsFinite(Vector3 value)
        {
            return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
        }
    }
}
