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
        public Vector3 thirdPersonOffset = new Vector3(0f, 1.4f, -2.2f);

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

        private float smoothedYaw;
        private float yawVelocity;
        private Vector3 positionVelocity;
        private bool initialized;
        private Transform followTarget;

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

            if (!initialized)
            {
                smoothedYaw = followTarget.eulerAngles.y;
                initialized = true;
            }

            float targetYaw = followTarget.eulerAngles.y;
            smoothedYaw = Mathf.SmoothDampAngle(
                smoothedYaw, targetYaw, ref yawVelocity, yawSmoothTime, maxYawSpeed);

            float offset = Mathf.DeltaAngle(smoothedYaw, targetYaw);
            if (Mathf.Abs(offset) > maxYawOffset)
            {
                smoothedYaw = targetYaw - Mathf.Sign(offset) * maxYawOffset;
                yawVelocity = 0f;
            }

            Quaternion yawRot = Quaternion.Euler(0f, smoothedYaw, 0f);
            Vector3 desiredPos = followTarget.position + yawRot * thirdPersonOffset;

            transform.position = Vector3.SmoothDamp(
                transform.position, desiredPos, ref positionVelocity, positionSmoothTime);

            Vector3 lookTarget = followTarget.position + Vector3.up * lookAtHeight;
            transform.rotation = Quaternion.LookRotation(lookTarget - transform.position, Vector3.up);
        }
    }
}
