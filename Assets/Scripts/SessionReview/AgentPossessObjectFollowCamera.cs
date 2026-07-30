using UnityEngine;

namespace SessionReview
{
    /// <summary>
    /// Third-person chase camera for generic possessed objects (cars and other props,
    /// typically GLB imports). GLB roots often carry a ±90° axis-fix rotation and a
    /// pivot that sits below the ground, so this camera never trusts the target's
    /// transform axes or pivot: it frames the renderer-bounds center (recomputed every
    /// frame from cached renderers) and takes its yaw from the possession controller's
    /// drive frame (<see cref="PossessedAgentController.DriveYaw"/>), falling back to
    /// the same axis estimate when the object is only selected, not driven.
    ///
    /// A sphere-cast from the bounds center pulls the camera in front of walls so it
    /// can't end up inside a building. RMB orbits, the wheel zooms; both are ignored
    /// while the pick camera is up (it owns the mouse then), and the orbit eases back
    /// behind the object while driving.
    /// </summary>
    public class AgentPossessObjectFollowCamera : MonoBehaviour
    {
        public Transform target;
        [Tooltip("Camera pitch (deg) above the horizontal when not orbiting.")]
        public float basePitch = 16f;
        public float minDistance = 2.0f;
        public float maxDistance = 14f;
        public float positionSmoothTime = 0.10f;
        public float orbitSensitivity = 3f;
        [Tooltip("Degrees/sec the RMB orbit eases back behind the target while driving.")]
        public float orbitRecenterSpeed = 120f;
        public float obstructionRadius = 0.3f;

        private Renderer[] renderers;
        private float distance = 6f;
        private float fallbackYaw;
        private float orbitYaw;
        private float orbitPitch;
        private Vector3 posVelocity;
        private Bounds lastBounds;
        private Vector3 lastCenterOffset;   // bounds center relative to the pivot
        private bool hasBounds;

        public void Configure(Transform followTarget, Bounds bounds)
        {
            target = followTarget;
            renderers = followTarget != null
                ? followTarget.GetComponentsInChildren<Renderer>(true)
                : new Renderer[0];

            float footprint = Mathf.Max(bounds.size.x, bounds.size.z, 1f);
            distance = Mathf.Clamp(footprint * 1.6f + 1.5f, 3.5f, 10f);
            fallbackYaw = followTarget != null
                ? PossessedAgentController.EstimateDriveYaw(followTarget)
                : 0f;
            lastBounds = bounds;
            lastCenterOffset = followTarget != null ? bounds.center - followTarget.position : Vector3.zero;
            hasBounds = true;

            transform.position = DesiredPosition(bounds.center, ResolveYaw());
            transform.rotation = Quaternion.LookRotation(bounds.center - transform.position, Vector3.up);
            posVelocity = Vector3.zero;
        }

        private void LateUpdate()
        {
            if (target == null) return;

            Bounds bounds = CurrentBounds();
            bool mouseFree = !AgentPossessOverlay.PickModeActive;

            if (mouseFree && Input.GetMouseButton(1))
            {
                orbitYaw += Input.GetAxis("Mouse X") * orbitSensitivity;
                orbitPitch = Mathf.Clamp(orbitPitch - Input.GetAxis("Mouse Y") * orbitSensitivity, -12f, 55f);
            }
            else if (Input.GetKey(KeyCode.UpArrow) || Input.GetKey(KeyCode.DownArrow) ||
                     Input.GetKey(KeyCode.LeftArrow) || Input.GetKey(KeyCode.RightArrow))
            {
                float step = orbitRecenterSpeed * Time.deltaTime;
                orbitYaw = Mathf.MoveTowards(orbitYaw, 0f, step);
                orbitPitch = Mathf.MoveTowards(orbitPitch, 0f, step);
            }

            if (mouseFree)
            {
                float scroll = Input.mouseScrollDelta.y;
                if (Mathf.Abs(scroll) > 0.01f)
                    distance = Mathf.Clamp(distance * (1f - scroll * 0.1f), minDistance, maxDistance);
            }

            Vector3 desired = DesiredPosition(bounds.center, ResolveYaw());
            transform.position = Vector3.SmoothDamp(
                transform.position, desired, ref posVelocity, Mathf.Max(0.001f, positionSmoothTime));

            Vector3 lookDir = bounds.center - transform.position;
            if (lookDir.sqrMagnitude < 0.0001f)
                lookDir = Vector3.forward;
            transform.rotation = Quaternion.LookRotation(lookDir, Vector3.up);
        }

        private float ResolveYaw()
        {
            var controller = target != null ? target.GetComponent<PossessedAgentController>() : null;
            return controller != null ? controller.DriveYaw : fallbackYaw;
        }

        private Vector3 DesiredPosition(Vector3 center, float yaw)
        {
            Quaternion rot = Quaternion.Euler(basePitch + orbitPitch, yaw + orbitYaw, 0f);
            Vector3 dir = rot * Vector3.back;
            float dist = ClampToObstruction(center, dir, distance);
            return center + dir * dist;
        }

        // Pull the camera in front of walls: sphere-cast outward from the bounds center
        // against environment colliders only (skip the target itself and anything with a
        // rigidbody, i.e. agents), and stop short of the first hit.
        private float ClampToObstruction(Vector3 center, Vector3 dir, float wantedDist)
        {
            float best = wantedDist;
            foreach (var hit in Physics.SphereCastAll(center, obstructionRadius, dir, wantedDist,
                         Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            {
                if (hit.collider == null) continue;
                if (hit.collider.attachedRigidbody != null) continue;
                if (target != null && hit.collider.transform.IsChildOf(target)) continue;
                if (hit.distance <= 0.01f) continue;   // cast started inside this collider
                best = Mathf.Min(best, Mathf.Max(hit.distance - 0.25f, minDistance * 0.5f));
            }
            return best;
        }

        private Bounds CurrentBounds()
        {
            Bounds bounds = default;
            bool has = false;
            if (renderers != null)
            {
                foreach (Renderer r in renderers)
                {
                    if (r == null || !r.enabled) continue;
                    if (!has) { bounds = r.bounds; has = true; }
                    else bounds.Encapsulate(r.bounds);
                }
            }
            if (!has)
            {
                // Renderers vanished (LOD swap, disable): keep the last size and the last
                // center-to-pivot offset so the frame still follows the moving pivot.
                return hasBounds
                    ? new Bounds(target.position + lastCenterOffset, lastBounds.size)
                    : new Bounds(target.position + Vector3.up * 0.8f, Vector3.one * 1.6f);
            }
            lastBounds = bounds;
            lastCenterOffset = bounds.center - target.position;
            hasBounds = true;
            return bounds;
        }
    }
}
