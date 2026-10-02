using System.Collections.Generic;
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
        [Header("Drive (arrow keys — WASD stays with the robot/avatar controllers)")]
        public float maxSpeed = 1.4f;
        public float runMultiplier = 1.8f;
        [Tooltip("Reverse speed as a fraction of maxSpeed.")]
        public float reverseFactor = 0.5f;
        public float acceleration = 3.0f;
        public float deceleration = 5.0f;
        public float rotationSpeed = 120f;

        [Header("Steering feel")]
        [Tooltip("Deg/s² the turn rate ramps up/down — no more instant snap steering.")]
        public float angularAcceleration = 240f;
        [Tooltip("Drive-frame targets (cars): fraction of the turn rate available at standstill.")]
        [Range(0f, 1f)] public float minTurnFactor = 0.35f;
        [Tooltip("Drive-frame targets (cars): speed (m/s) at which the full turn rate is reached.")]
        public float speedForFullTurn = 1.2f;

        [Header("Step climb (matches ManualWheelchairController)")]
        public float maxStepHeight = 0.35f;
        public float stepClimbSpeed = 2.0f;

        [Header("First-person camera")]
        public float lookSensitivity = 3.0f;
        [Tooltip("Degrees/sec the free-look offset eases back to straight-ahead while driving.")]
        public float lookRecenterSpeed = 180f;
        public float cameraForwardOffset = 0.18f;

        [Header("Drive frame (generic objects / GLB imports)")]
        [Tooltip("Flips the drive direction 180° when the estimated forward faces the object's rear.")]
        public KeyCode flipForwardKey = KeyCode.F;
        [Tooltip("Rotates the drive direction 90° for objects whose long axis was mis-detected.")]
        public KeyCode cycleForwardKey = KeyCode.R;

        private Animator animator;
        private Rigidbody rb;
        private GameObject camGo;
        private Camera cam;
        private float eyeHeight = 1.6f;
        private float currentSpeed;
        private float lookYaw;
        private float lookPitch;
        private float defaultMaxSpeed;
        private RigidbodyConstraints originalConstraints;
        private bool hasOriginalConstraints;
        private bool originalIsKinematic;
        private bool originalUseGravity;
        private bool hasOriginalBodyMode;
        private bool keepKinematicOnDestroy;
        private bool useDriveFrame;
        private bool rotateDriveFrameAroundBounds;
        private float originalAnimatorSpeed;
        private bool hasOriginalAnimatorSpeed;
        // The drive direction expressed in the ROOT'S LOCAL SPACE. World-rotation
        // independent: however the object gets turned (or however crooked it already
        // was when possessed), the visual heading and the drive heading stay locked
        // together, so diagonal "crab" driving is impossible by construction.
        private Vector3 driveLocalAxis = Vector3.forward;
        private float lastDerivedYaw;
        // Drive-axis estimation and the F/R calibration cache live in AgentControlTuning
        // so possession, ManualWheelchairController (player-ridden bikes), the replay
        // animator and the follow cameras all share ONE calibration — a correction made
        // while possessing must not vanish when the same rig is ridden or replayed.
        private float groundOffset;
        private bool hasGroundOffset;
        private float nextDebugLogTime;
        private float currentTurnSpeed;       // deg/s, eased toward the steering target
        private Renderer[] boundsRenderers;   // drive-frame targets: for the rotation pivot

        private static bool DriveForwardHeld => Input.GetKey(KeyCode.UpArrow);
        private static bool DriveBackHeld => Input.GetKey(KeyCode.DownArrow);
        private static float DriveSteer =>
            (Input.GetKey(KeyCode.LeftArrow) ? -1f : 0f) + (Input.GetKey(KeyCode.RightArrow) ? 1f : 0f);
        private static bool AnyDriveKeyHeld =>
            Input.GetKey(KeyCode.UpArrow) || Input.GetKey(KeyCode.DownArrow) ||
            Input.GetKey(KeyCode.LeftArrow) || Input.GetKey(KeyCode.RightArrow);

        public float CurrentSpeed => currentSpeed;

        /// <summary>
        /// World-space yaw (deg) the object is being driven along. For agents this is the
        /// transform's own yaw; for generic objects it is derived every frame from the
        /// local drive axis, so it follows the visual heading exactly.
        /// </summary>
        public float DriveYaw
        {
            get
            {
                if (!useDriveFrame) return transform.eulerAngles.y;
                Vector3 world = transform.TransformDirection(driveLocalAxis);
                world.y = 0f;
                if (world.sqrMagnitude > 1e-8f)
                    lastDerivedYaw = Mathf.Atan2(world.x, world.z) * Mathf.Rad2Deg;
                return lastDerivedYaw;
            }
        }

        public bool UsesDriveFrame => useDriveFrame;

        /// <summary>
        /// Yaw the object would be driven along right now (used by the follow camera for
        /// selected-but-not-possessed objects). Always axis-based — unlike
        /// AgentControlTuning.EstimateDriveYaw this must work for GLB cars too, whose
        /// transform yaw is meaningless.
        /// </summary>
        public static float EstimateDriveYaw(Transform t)
        {
            if (t == null) return 0f;
            Vector3 world = t.TransformDirection(AgentControlTuning.ResolveDriveLocalAxis(t));
            world.y = 0f;
            if (world.sqrMagnitude < 1e-8f) return 0f;
            return Mathf.Atan2(world.x, world.z) * Mathf.Rad2Deg;
        }

        public float MaxSpeed
        {
            get { return maxSpeed; }
            set { maxSpeed = Mathf.Max(0.05f, value); }
        }

        public float DefaultMaxSpeed => defaultMaxSpeed;

        /// <summary>Called by the overlay right after AddComponent.</summary>
        public void Configure(Animator agentAnimator, int display, bool createFirstPersonCamera = true, bool forceKinematic = false)
        {
            animator = agentAnimator;
            if (animator != null)
            {
                originalAnimatorSpeed = animator.speed;
                hasOriginalAnimatorSpeed = true;
            }
            rb = GetComponent<Rigidbody>();
            defaultMaxSpeed = maxSpeed;
            keepKinematicOnDestroy = forceKinematic;
            if (rb != null)
            {
                originalConstraints = rb.constraints;
                hasOriginalConstraints = true;
                rb.constraints = (rb.constraints & ~(RigidbodyConstraints.FreezePositionX |
                                                     RigidbodyConstraints.FreezePositionY |
                                                     RigidbodyConstraints.FreezePositionZ |
                                                     RigidbodyConstraints.FreezeRotationY)) |
                                 RigidbodyConstraints.FreezeRotationX |
                                 RigidbodyConstraints.FreezeRotationZ;
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
                originalIsKinematic = rb.isKinematic;
                originalUseGravity = rb.useGravity;
                hasOriginalBodyMode = true;
                if (forceKinematic)
                {
                    rb.isKinematic = true;
                    rb.useGravity = false;
                }
            }

            // Generic objects (cars, props — often GLB imports with a ±90° axis-fix on the
            // root) are driven in an independent world-yaw frame: rotation is applied
            // around world up so the object can never tilt, and translation follows the
            // frame's forward instead of the root's (possibly vertical) transform.forward.
            useDriveFrame = forceKinematic || AgentControlTuning.ShouldUseVisualDriveFrame(gameObject, animator);
            rotateDriveFrameAroundBounds = forceKinematic;
            if (useDriveFrame)
            {
                boundsRenderers = GetComponentsInChildren<Renderer>(true);
                driveLocalAxis = AgentControlTuning.ResolveDriveLocalAxis(transform);

                // Repair a cached axis that drifted out of the mesh's horizontal plane
                // (e.g. R was pressed in an earlier possession while the body was tilted):
                // project it back onto the plane perpendicular to the mesh's vertical.
                Vector3 upMost = ComputeUpMostLocalAxis();
                Vector3 planar = driveLocalAxis - Vector3.Dot(driveLocalAxis, upMost) * upMost;
                if (planar.sqrMagnitude > 0.25f)
                    driveLocalAxis = planar.normalized;
                AgentControlTuning.CacheDriveLocalAxis(gameObject, driveLocalAxis);

                if (forceKinematic)
                    LevelBody(upMost);

                Debug.Log($"[AgentPossess] '{name}' drive frame: localAxis={driveLocalAxis} yaw={DriveYaw:F0} " +
                          $"(scale={transform.lossyScale}). " +
                          $"[{flipForwardKey}] flips 180, [{cycleForwardKey}] rotates 90 if it drives sideways/backwards.");
            }

            // Remember how far the pivot sits above (or below — common for GLB cars) the
            // ground, so FollowGround preserves that relationship instead of snapping the
            // pivot onto the road surface.
            if (TryProbeGround(transform.position + Vector3.up * 1.0f, 5f, out float initialGround))
            {
                groundOffset = transform.position.y - initialGround;
                hasGroundOffset = true;
            }

            // Eye height from the Base-sized capsule (full avatar height), so seated
            // wheelchair rigs and standing characters both get a sensible viewpoint.
            CapsuleCollider capsule = GetComponent<CapsuleCollider>();
            if (capsule != null)
                eyeHeight = Mathf.Clamp(capsule.height * 0.93f, 0.9f, 1.85f);

            if (!createFirstPersonCamera)
                return;

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
            float dt = Time.deltaTime > 1e-5f ? Time.deltaTime : Time.unscaledDeltaTime;

            if (SessionReviewInputFocus.IsTextEntryActive())
            {
                currentSpeed = Mathf.MoveTowards(currentSpeed, 0f, deceleration * dt);
                UpdateAnimator();
                return;
            }

            // Arrow keys only: WASD belongs to the robot/PWD manual controllers, so the
            // operator can drive a possessed target even while the robot is in manual.
            bool forward = DriveForwardHeld;
            bool back = DriveBackHeld;
            bool run = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);

            // The Logitech flight stick doubles the arrow keys for the operator. Under
            // Auto it always belongs to the researcher (participant = gamepad or
            // keyboard-only); it reads 0 only when the Logitech profile was explicitly
            // selected for the participant. The participant's controllers never read
            // this stick's axes, so it cannot move the robot/PWD either.
            float stickThrottle = SEAN.Input.JoystickProfiles.ResearcherStickThrottle();
            float stickSteer = SEAN.Input.JoystickProfiles.ResearcherStickSteer();

            float target = 0f;
            if (forward) target = maxSpeed * (run ? runMultiplier : 1f);
            else if (back) target = -maxSpeed * reverseFactor;
            else if (stickThrottle > 0.001f) target = maxSpeed * stickThrottle * (run ? runMultiplier : 1f);
            else if (stickThrottle < -0.001f) target = maxSpeed * reverseFactor * stickThrottle;

            float rate = Mathf.Abs(target) > Mathf.Abs(currentSpeed) ? acceleration : deceleration;
            currentSpeed = Mathf.MoveTowards(currentSpeed, target, rate * dt);

            if (useDriveFrame)
            {
                if (Input.GetKeyDown(flipForwardKey))
                {
                    driveLocalAxis = -driveLocalAxis;
                    AgentControlTuning.CacheDriveLocalAxis(gameObject, driveLocalAxis, true);
                    Debug.Log($"[AgentPossess] '{name}' drive forward flipped 180 (yaw {DriveYaw:F0}).");
                }
                if (Input.GetKeyDown(cycleForwardKey))
                {
                    // Rotate the drive axis 90 degrees around the local axis that points
                    // along world up, so the correction stays valid at any orientation.
                    Vector3 upLocal = transform.InverseTransformDirection(Vector3.up);
                    if (upLocal.sqrMagnitude > 1e-8f)
                        driveLocalAxis = Quaternion.AngleAxis(90f, upLocal.normalized) * driveLocalAxis;
                    AgentControlTuning.CacheDriveLocalAxis(gameObject, driveLocalAxis, true);
                    Debug.Log($"[AgentPossess] '{name}' drive forward rotated 90 (yaw {DriveYaw:F0}).");
                }
            }

            float steer = Mathf.Clamp(DriveSteer + stickSteer, -1f, 1f);
            float turnFactor = 1f;
            if (useDriveFrame)
            {
                // Car-like steering: the turn rate scales with speed (a rolling car
                // turns, a parked one barely pivots) and inverts while reversing.
                turnFactor = minTurnFactor + (1f - minTurnFactor)
                    * Mathf.Clamp01(Mathf.Abs(currentSpeed) / Mathf.Max(0.1f, speedForFullTurn));
                if (currentSpeed < -0.05f) steer = -steer;
            }

            // Ease the turn rate instead of snapping it — this is what makes steering
            // feel progressive rather than twitchy.
            float targetTurn = steer * rotationSpeed * turnFactor;
            currentTurnSpeed = Mathf.MoveTowards(currentTurnSpeed, targetTurn, angularAcceleration * dt);

            if (Mathf.Abs(currentTurnSpeed) > 0.01f)
            {
                float delta = currentTurnSpeed * dt;
                if (useDriveFrame)
                {
                    // World-up rotation so a GLB axis-fix root yaws instead of rolling
                    // over, and around the visual bounds center — GLB pivots often sit
                    // at a bumper, and rotating around such a pivot swings the whole
                    // car sideways instead of turning it in place. DriveYaw follows
                    // automatically because it is derived from the rotated transform.
                    // Always about WORLD up: identical to a local-Y rotation for the
                    // upright character rigs, but the only correct choice for a root
                    // carrying an axis-fix tilt (where local Y is a world-space roll).
                    // Props pivot about their bounds center (GLB pivots sit at a bumper,
                    // so a pivot rotation swings the body); characters stand on their
                    // pivot and must turn in place.
                    transform.RotateAround(
                        rotateDriveFrameAroundBounds ? DriveBoundsCenter() : transform.position,
                        Vector3.up, delta);
                }
                else
                {
                    transform.Rotate(0f, delta, 0f);
                }
            }

            Vector3 fwd;
            if (useDriveFrame)
            {
                fwd = Quaternion.Euler(0f, DriveYaw, 0f) * Vector3.forward;
            }
            else
            {
                fwd = transform.forward;
                fwd.y = 0f;
                if (fwd.sqrMagnitude > 0.001f) fwd.Normalize();
            }
            transform.position += fwd * currentSpeed * dt;

            if ((forward || back || steer != 0f || stickThrottle != 0f) && Time.unscaledTime >= nextDebugLogTime)
            {
                nextDebugLogTime = Time.unscaledTime + 2f;
                Debug.Log($"[AgentPossess] '{name}' Up={forward} Down={back} steer={steer} " +
                          $"speed={currentSpeed:F2} turn={currentTurnSpeed:F0}deg/s yaw={DriveYaw:F0} pos={transform.position}");
            }

            FollowGround(dt);
            UpdateAnimator();
        }

        // The local axis that currently points along world up, snapped to ±X/±Y/±Z.
        private Vector3 ComputeUpMostLocalAxis()
        {
            Vector3 upLocal = transform.InverseTransformDirection(Vector3.up);
            int idx = 0;
            for (int i = 1; i < 3; i++)
                if (Mathf.Abs(upLocal[i]) > Mathf.Abs(upLocal[idx])) idx = i;
            Vector3 axis = Vector3.zero;
            axis[idx] = Mathf.Sign(upLocal[idx]);
            return axis;
        }

        // Straightens inherited roll/pitch (left over from physics accidents before the
        // possession system forced objects kinematic): keeps the current drive yaw and
        // forces the mesh's vertical axis back to world up, rotating about the bounds
        // center so the body doesn't swing sideways. Assumes the body is tilted less
        // than 45° — beyond that the "up-most" axis itself is ambiguous.
        private void LevelBody(Vector3 upMostLocal)
        {
            Vector3 worldDrive = transform.TransformDirection(driveLocalAxis);
            worldDrive.y = 0f;
            if (worldDrive.sqrMagnitude < 1e-6f) return;

            Quaternion localFrame = Quaternion.LookRotation(driveLocalAxis, upMostLocal);
            Quaternion target = Quaternion.LookRotation(worldDrive.normalized, Vector3.up)
                                * Quaternion.Inverse(localFrame);
            float tilt = Quaternion.Angle(transform.rotation, target);
            if (tilt < 0.5f) return;

            Vector3 center = DriveBoundsCenter();
            Quaternion delta = target * Quaternion.Inverse(transform.rotation);
            transform.rotation = target;
            transform.position = center + delta * (transform.position - center);
            if (tilt > 2f)
                Debug.Log($"[AgentPossess] '{name}' body auto-leveled: {tilt:F1} deg of inherited tilt removed.");
        }

        private Vector3 DriveBoundsCenter()
        {
            Bounds bounds = default;
            bool hasBounds = false;
            if (boundsRenderers != null)
            {
                foreach (Renderer r in boundsRenderers)
                {
                    if (r == null || !r.enabled) continue;
                    if (!hasBounds) { bounds = r.bounds; hasBounds = true; }
                    else bounds.Encapsulate(r.bounds);
                }
            }
            return hasBounds ? bounds.center : transform.position;
        }

        // Position-driven movement cannot climb steps: probe the ground at the body and
        // slightly ahead and ease the transform up onto climbable steps. Descending is
        // left to rigidbody gravity, like ManualWheelchairController.FollowGround.
        private void FollowGround(float deltaTime)
        {
            if (maxStepHeight <= 0f) return;
            // A drive-frame target without a measured pivot-to-ground offset (probe failed
            // at possession time) must not be snapped: its pivot may sit far below the
            // road (GLB cars) and offset-0 logic would yank it upward.
            if (useDriveFrame && !hasGroundOffset) return;

            // Raise the probe start when the pivot sits below the ground surface.
            float pivotLift = Mathf.Max(0f, -groundOffset);
            float probeUp = maxStepHeight + 0.3f + pivotLift;
            float probeLength = probeUp + maxStepHeight + 0.6f;
            Vector3 fwd = Quaternion.Euler(0f, DriveYaw, 0f) * Vector3.forward;

            float ground = float.NegativeInfinity;
            if (TryProbeGround(transform.position + Vector3.up * probeUp, probeLength, out float hCenter))
                ground = Mathf.Max(ground, hCenter);
            if (TryProbeGround(transform.position + fwd * 0.35f + Vector3.up * probeUp, probeLength, out float hFront))
                ground = Mathf.Max(ground, hFront);
            if (float.IsNegativeInfinity(ground))
                return;

            float diff = ground + groundOffset - transform.position.y;
            bool climbUp = diff > 0.01f && diff <= maxStepHeight;
            bool easeDown = diff < -0.01f && (rb == null || rb.isKinematic);
            if (!climbUp && !easeDown)
                return;

            Vector3 p = transform.position;
            p.y = Mathf.MoveTowards(p.y, ground + groundOffset, stepClimbSpeed * deltaTime);
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
            Vector3 fwd = useDriveFrame
                ? Quaternion.Euler(0f, DriveYaw, 0f) * Vector3.forward
                : transform.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude > 0.001f) fwd.Normalize();
            AgentControlTuning.UpdateLocomotionAnimator(
                animator,
                transform,
                fwd * currentSpeed,
                currentSpeed,
                true,
                useDriveFrame ? DriveYaw : transform.eulerAngles.y);
        }

        void LateUpdate()
        {
            if (cam == null) return;

            if (SessionReviewInputFocus.IsTextEntryActive())
            {
                SyncCamera();
                return;
            }

            if (Input.GetMouseButton(1))
            {
                lookYaw += Input.GetAxis("Mouse X") * lookSensitivity;
                lookPitch = Mathf.Clamp(lookPitch - Input.GetAxis("Mouse Y") * lookSensitivity, -75f, 75f);
            }
            else if (AnyDriveKeyHeld ||
                     SEAN.Input.JoystickProfiles.ResearcherStickThrottle() != 0f ||
                     SEAN.Input.JoystickProfiles.ResearcherStickSteer() != 0f)
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
            Quaternion rot = Quaternion.Euler(lookPitch, (useDriveFrame ? DriveYaw : transform.eulerAngles.y) + lookYaw, 0f);
            camGo.transform.position = transform.position + Vector3.up * eyeHeight
                                       + rot * Vector3.forward * cameraForwardOffset;
            camGo.transform.rotation = rot;
        }

        void OnDestroy()
        {
            if (rb != null && hasOriginalConstraints && !keepKinematicOnDestroy)
                rb.constraints = originalConstraints;
            if (rb != null && hasOriginalBodyMode && !keepKinematicOnDestroy)
            {
                rb.isKinematic = originalIsKinematic;
                rb.useGravity = originalUseGravity;
            }
            if (animator != null && hasOriginalAnimatorSpeed)
                animator.speed = originalAnimatorSpeed;
            if (camGo != null)
                Destroy(camGo);
        }
    }
}
