using UnityEngine;

namespace IVI
{
    /// <summary>
    /// Smooths the camera's yaw rotation so it follows the parent avatar's
    /// body rotation at a limited speed, preventing jarring snap-turns even
    /// when the body changes direction abruptly.
    /// Attach to a camera that is a child of the wheelchair avatar root.
    /// </summary>
    public class WheelchairCameraSmoothing : MonoBehaviour
    {
        [Tooltip("How quickly the camera catches up to the body's yaw (seconds). Higher = smoother but laggier.")]
        public float yawSmoothTime = 0.25f;

        [Tooltip("Hard cap on camera yaw rotation speed (degrees/second). " +
                 "The camera will never rotate faster than this, no matter how fast the body turns.")]
        public float maxYawSpeed = 10f;

        [Tooltip("Maximum yaw offset (degrees) the camera is allowed to lag behind the body. " +
                 "Prevents the camera from falling too far behind during sustained turns.")]
        public float maxYawOffset = 20f;

        private float smoothedYaw;
        private float yawVelocity;
        private bool initialized;

        void Start()
        {
            if (transform.parent != null)
                smoothedYaw = transform.parent.eulerAngles.y;
            else
                smoothedYaw = transform.eulerAngles.y;
            initialized = true;
        }

        void LateUpdate()
        {
            if (transform.parent == null) return;

            float targetYaw = transform.parent.eulerAngles.y;

            if (!initialized)
            {
                smoothedYaw = targetYaw;
                initialized = true;
            }

            smoothedYaw = Mathf.SmoothDampAngle(
                smoothedYaw, targetYaw, ref yawVelocity, yawSmoothTime, maxYawSpeed);

            float offset = Mathf.DeltaAngle(smoothedYaw, targetYaw);
            if (Mathf.Abs(offset) > maxYawOffset)
            {
                smoothedYaw = targetYaw - Mathf.Sign(offset) * maxYawOffset;
                yawVelocity = 0f;
            }

            transform.rotation = Quaternion.Euler(0f, smoothedYaw, 0f);
        }
    }
}
