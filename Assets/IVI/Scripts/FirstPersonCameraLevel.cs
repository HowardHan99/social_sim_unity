using UnityEngine;

namespace IVI
{
    /// <summary>
    /// Keeps a first-person camera level (horizon parallel to the ground) while
    /// following the avatar's position and horizontal heading, so wheelchair body
    /// pitch/roll never tilts the view. Attach to a camera that starts as a child
    /// of the avatar root; it caches that root, un-parents itself, and drives its
    /// own world transform in LateUpdate (same pattern as WheelchairCameraSmoothing).
    /// </summary>
    public class FirstPersonCameraLevel : MonoBehaviour
    {
        [Tooltip("Eye offset from the avatar root in its yaw-only frame (x=right, y=up, z=forward).")]
        public Vector3 eyeOffset = new Vector3(0f, 1.15f, 0.35f);

        [Tooltip("Optional downward look (degrees). 0 keeps the view perfectly horizontal.")]
        public float pitchDegrees = 0f;

        private Transform followTarget;

        void Start()
        {
            followTarget = transform.parent;
            transform.SetParent(null, true);
        }

        void LateUpdate()
        {
            if (followTarget == null) return;

            // Heading from the body's forward projected onto the ground plane, so a
            // rolled/pitched body still yields a level, forward-facing view.
            Vector3 forward = followTarget.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 1e-6f)
                forward = Vector3.forward;
            forward.Normalize();

            Quaternion yawRot = Quaternion.LookRotation(forward, Vector3.up);

            transform.position = followTarget.position + yawRot * eyeOffset;
            // Pitch is applied around the (horizontal) local right axis, so it never
            // reintroduces roll -- the horizon stays level.
            transform.rotation = yawRot * Quaternion.Euler(pitchDegrees, 0f, 0f);
        }
    }
}
