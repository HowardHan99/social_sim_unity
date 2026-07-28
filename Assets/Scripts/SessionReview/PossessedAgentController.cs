using UnityEngine;

namespace SessionReview
{
    /// <summary>
    /// Drives an agent while the operator has taken it over from the Agent Control panel
    /// (<see cref="AgentPossessOverlay"/>). Added to the agent's GameObject for the
    /// duration of the possession and destroyed on release; the overlay owns disabling
    /// and restoring the agent's own AI (Base/SFPWDAgent, ManualWheelchairController).
    ///
    /// Movement mirrors ManualWheelchairController's position-driven tank drive so a
    /// possessed pedestrian feels like the PWD player: W/S accelerate/reverse, A/D turn,
    /// Shift = faster, with the same environment-only step-climb ground probe. The
    /// first-person camera renders on the operator's display (Display 2 by default) so
    /// the participant's Display 1 view is untouched; hold the right mouse button to
    /// look around and the view eases back to straight-ahead while driving.
    /// </summary>
    public class PossessedAgentController : MonoBehaviour
    {
        [Header("Drive")]
        public float maxSpeed = 1.4f;
        public float runMultiplier = 1.8f;
        [Tooltip("Reverse speed as a fraction of maxSpeed.")]
        public float reverseFactor = 0.5f;
        public float acceleration = 3.0f;
        public float deceleration = 5.0f;
        public float rotationSpeed = 120f;

        [Header("Step climb (matches ManualWheelchairController)")]
        public float maxStepHeight = 0.35f;
        public float stepClimbSpeed = 2.0f;

        [Header("First-person camera")]
        public float lookSensitivity = 3.0f;
        [Tooltip("Degrees/sec the free-look offset eases back to straight-ahead while driving.")]
        public float lookRecenterSpeed = 180f;
        public float cameraForwardOffset = 0.18f;

        private Animator animator;
        private Rigidbody rb;
        private GameObject camGo;
        private Camera cam;
        private float eyeHeight = 1.6f;
        private float currentSpeed;
        private float lookYaw;
        private float lookPitch;

        public float CurrentSpeed => currentSpeed;

        /// <summary>Called by the overlay right after AddComponent.</summary>
        public void Configure(Animator agentAnimator, int display)
        {
            animator = agentAnimator;
            rb = GetComponent<Rigidbody>();

            // Eye height from the Base-sized capsule (full avatar height), so seated
            // wheelchair rigs and standing characters both get a sensible viewpoint.
            CapsuleCollider capsule = GetComponent<CapsuleCollider>();
            if (capsule != null)
                eyeHeight = Mathf.Clamp(capsule.height * 0.93f, 0.9f, 1.85f);

            camGo = new GameObject("PossessFirstPersonCamera");
            cam = camGo.AddComponent<Camera>();
            cam.targetDisplay = display;
            cam.depth = 90f;   // above the ResearcherDisplay mirror cameras on Display 2
            cam.fieldOfView = 65f;
            cam.nearClipPlane = 0.05f;
            SyncCamera();
        }

        void Update()
        {
            float dt = Time.deltaTime;

            bool forward = Input.GetKey(KeyCode.W);
            bool back = Input.GetKey(KeyCode.S);
            bool run = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);

            float target = 0f;
            if (forward) target = maxSpeed * (run ? runMultiplier : 1f);
            else if (back) target = -maxSpeed * reverseFactor;

            float rate = Mathf.Abs(target) > Mathf.Abs(currentSpeed) ? acceleration : deceleration;
            currentSpeed = Mathf.MoveTowards(currentSpeed, target, rate * dt);

            float turn = 0f;
            if (Input.GetKey(KeyCode.A)) turn -= 1f;
            if (Input.GetKey(KeyCode.D)) turn += 1f;
            if (turn != 0f)
                transform.Rotate(0f, turn * rotationSpeed * dt, 0f);

            Vector3 fwd = transform.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude > 0.001f) fwd.Normalize();
            transform.position += fwd * currentSpeed * dt;

            FollowGround(dt);
            UpdateAnimator();
        }

        // Position-driven movement cannot climb steps: probe the ground at the body and
        // slightly ahead and ease the transform up onto climbable steps. Descending is
        // left to rigidbody gravity, like ManualWheelchairController.FollowGround.
        private void FollowGround(float deltaTime)
        {
            if (maxStepHeight <= 0f) return;

            float probeUp = maxStepHeight + 0.3f;
            float probeLength = probeUp + maxStepHeight + 0.6f;
            Vector3 fwd = transform.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude > 0.001f) fwd.Normalize();

            float ground = float.NegativeInfinity;
            if (TryProbeGround(transform.position + Vector3.up * probeUp, probeLength, out float hCenter))
                ground = Mathf.Max(ground, hCenter);
            if (TryProbeGround(transform.position + fwd * 0.35f + Vector3.up * probeUp, probeLength, out float hFront))
                ground = Mathf.Max(ground, hFront);
            if (float.IsNegativeInfinity(ground))
                return;

            float diff = ground - transform.position.y;
            bool climbUp = diff > 0.01f && diff <= maxStepHeight;
            bool easeDown = diff < -0.01f && (rb == null || rb.isKinematic);
            if (!climbUp && !easeDown)
                return;

            Vector3 p = transform.position;
            p.y = Mathf.MoveTowards(p.y, ground, stepClimbSpeed * deltaTime);
            transform.position = p;

            if (climbUp && rb != null && !rb.isKinematic)
            {
                Vector3 v = rb.velocity;
                if (v.y < 0f) { v.y = 0f; rb.velocity = v; }
            }
        }

        private bool TryProbeGround(Vector3 origin, float length, out float groundY)
        {
            groundY = 0f;
            float best = float.NegativeInfinity;
            foreach (var hit in Physics.RaycastAll(origin, Vector3.down, length, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            {
                if (hit.collider == null) continue;
                // Environment only: skip our own body and anything with a rigidbody
                // (pedestrians, robots) so we never "climb" onto another agent.
                if (hit.collider.attachedRigidbody != null) continue;
                if (hit.collider.transform.IsChildOf(transform)) continue;
                best = Mathf.Max(best, hit.point.y);
            }

            if (float.IsNegativeInfinity(best))
                return false;
            groundY = best;
            return true;
        }

        private void UpdateAnimator()
        {
            if (animator == null) return;
            Vector3 vel = transform.forward * currentSpeed;
            float speed = Mathf.Abs(currentSpeed);
            Vector3 local = Quaternion.Euler(0f, -transform.eulerAngles.y, 0f) * vel;
            animator.SetBool("Idling", speed < 0.1f);
            animator.SetFloat("Forward", local.z / 0.6f);
            animator.SetFloat("Strafe", local.x / 0.6f);
            animator.speed = speed > 0.1f ? speed : 1f;
        }

        void LateUpdate()
        {
            if (cam == null) return;

            if (Input.GetMouseButton(1))
            {
                lookYaw += Input.GetAxis("Mouse X") * lookSensitivity;
                lookPitch = Mathf.Clamp(lookPitch - Input.GetAxis("Mouse Y") * lookSensitivity, -75f, 75f);
            }
            else if (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.S) ||
                     Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.D))
            {
                float step = lookRecenterSpeed * Time.deltaTime;
                lookYaw = Mathf.MoveTowards(lookYaw, 0f, step);
                lookPitch = Mathf.MoveTowards(lookPitch, 0f, step);
            }

            SyncCamera();
        }

        private void SyncCamera()
        {
            if (camGo == null) return;
            Quaternion rot = Quaternion.Euler(lookPitch, transform.eulerAngles.y + lookYaw, 0f);
            camGo.transform.position = transform.position + Vector3.up * eyeHeight
                                       + rot * Vector3.forward * cameraForwardOffset;
            camGo.transform.rotation = rot;
        }

        void OnDestroy()
        {
            if (camGo != null)
                Destroy(camGo);
        }
    }
}
