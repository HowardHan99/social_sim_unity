using UnityEngine;

namespace SEAN.Input
{
    /// <summary>
    /// Right-stick free-look for Xbox-layout gamepads. Self-bootstraps like
    /// JoystickProfileSwitcher (no scene wiring) and applies a yaw/pitch offset to
    /// whatever camera is currently rendering, after the follow-camera scripts run:
    ///   - WheelchairCameraSmoothing (third person): orbits around the followed avatar.
    ///   - FirstPersonCameraLevel: rotates the view in place.
    ///   - bare cameras (robot-mounted): offsets a cached mount rotation.
    /// Offsets recenter smoothly when the stick is released. Inactive on the Logitech
    /// profile (no second stick), during session review / world building, and on the
    /// runtime-editor free camera, which has its own look controls.
    /// </summary>
    [DefaultExecutionOrder(5000)]
    public class GamepadCameraLook : MonoBehaviour
    {
        [Tooltip("Right-stick yaw speed (deg/s). Deliberately gentle: fast free-look while driving is a common motion-sickness trigger. Driven live by the joystick tuning panel's Look speed slider.")]
        public float yawSpeed = 45f;
        [Tooltip("Right-stick pitch speed (deg/s). Kept below the yaw speed; vertical swings are the most nauseating.")]
        public float pitchSpeed = 28f;
        public float maxYawOffset = 160f;
        public float maxPitchOffset = 50f;
        [Tooltip("Seconds for the stick input to ramp in/out, so the view never starts or stops abruptly.")]
        public float inputSmoothTime = 0.18f;
        [Tooltip("Seconds for the view to ease back to center after the stick is released. Higher = softer return. Camera motion the user did not ask for is a strong nausea trigger, so this is a slow drift rather than a spring-back.")]
        public float recenterTime = 1.4f;
        public bool invertPitch = false;

        private Camera trackedCamera;
        private Quaternion baseLocalRotation = Quaternion.identity;
        private bool bareOffsetApplied;
        private float yawOffset;
        private float pitchOffset;
        private float smoothedLookX;
        private float smoothedLookY;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (FindObjectOfType<GamepadCameraLook>() != null)
                return;

            var go = new GameObject("GamepadCameraLook");
            DontDestroyOnLoad(go);
            go.AddComponent<GamepadCameraLook>();
        }

        private void LateUpdate()
        {
            Camera cam = ResolveLookCamera();
            if (cam != trackedCamera)
            {
                trackedCamera = cam;
                yawOffset = 0f;
                pitchOffset = 0f;
                smoothedLookX = 0f;
                smoothedLookY = 0f;
                bareOffsetApplied = false;
                baseLocalRotation = cam != null ? cam.transform.localRotation : Quaternion.identity;
            }

            if (cam == null)
                return;

            if (IsLookBlocked(cam))
            {
                yawOffset = 0f;
                pitchOffset = 0f;
                smoothedLookX = 0f;
                smoothedLookY = 0f;
                bareOffsetApplied = false;
                return;
            }

            float dt = Time.unscaledDeltaTime;
            if (dt <= 0f)
                return;

            // Ramp the stick input instead of using it raw: an instant start/stop of camera
            // motion is what makes free-look while driving nauseating.
            float inputK = 1f - Mathf.Exp(-dt / Mathf.Max(0.01f, inputSmoothTime));
            smoothedLookX = Mathf.Lerp(smoothedLookX, JoystickProfiles.CameraLookX(), inputK);
            smoothedLookY = Mathf.Lerp(smoothedLookY, JoystickProfiles.CameraLookY(), inputK);

            if (Mathf.Abs(smoothedLookX) > 0.002f || Mathf.Abs(smoothedLookY) > 0.002f)
            {
                float pitchSign = invertPitch ? -1f : 1f;
                yawOffset = Mathf.Clamp(yawOffset + smoothedLookX * yawSpeed * dt, -maxYawOffset, maxYawOffset);
                pitchOffset = Mathf.Clamp(pitchOffset + smoothedLookY * pitchSpeed * pitchSign * dt, -maxPitchOffset, maxPitchOffset);
            }
            else
            {
                // Exponential ease home rather than a constant-rate snap, so releasing the
                // stick drifts the view back instead of yanking it.
                float recenterK = 1f - Mathf.Exp(-dt / Mathf.Max(0.01f, recenterTime));
                yawOffset = Mathf.Lerp(yawOffset, 0f, recenterK);
                pitchOffset = Mathf.Lerp(pitchOffset, 0f, recenterK);
                if (Mathf.Abs(yawOffset) < 0.05f) yawOffset = 0f;
                if (Mathf.Abs(pitchOffset) < 0.05f) pitchOffset = 0f;
            }

            ApplyOffsets(cam);
        }

        private void ApplyOffsets(Camera cam)
        {
            Transform t = cam.transform;
            bool offsetsActive = Mathf.Abs(yawOffset) > 0.01f || Mathf.Abs(pitchOffset) > 0.01f;

            var thirdPerson = cam.GetComponent<IVI.WheelchairCameraSmoothing>();
            if (thirdPerson != null)
            {
                // The follow script rewrites the camera every LateUpdate, so reapply the
                // full offset each frame by orbiting around the followed avatar.
                if (offsetsActive && thirdPerson.FollowAvatarRoot != null)
                {
                    Vector3 pivot = thirdPerson.FollowAvatarRoot.position;
                    t.RotateAround(pivot, Vector3.up, yawOffset);
                    t.RotateAround(pivot, t.right, pitchOffset);
                }
                return;
            }

            var firstPerson = cam.GetComponent<IVI.FirstPersonCameraLevel>();
            if (firstPerson != null)
            {
                if (offsetsActive)
                {
                    t.rotation = Quaternion.AngleAxis(yawOffset, Vector3.up) * t.rotation;
                    t.rotation = t.rotation * Quaternion.Euler(pitchOffset, 0f, 0f);
                }
                return;
            }

            // Bare (robot-mounted) camera: nothing rewrites it per frame, so compose the
            // offsets over a cached mount rotation instead of accumulating deltas.
            if (offsetsActive)
            {
                bareOffsetApplied = true;
                t.localRotation = baseLocalRotation *
                                  Quaternion.Euler(0f, yawOffset, 0f) *
                                  Quaternion.Euler(pitchOffset, 0f, 0f);
            }
            else if (bareOffsetApplied)
            {
                t.localRotation = baseLocalRotation;
                bareOffsetApplied = false;
            }
            else
            {
                // Track outside changes (scripts, cutscenes) while we're not offsetting.
                baseLocalRotation = t.localRotation;
            }
        }

        // Scanning for the view every frame is wasteful; it only changes when the player
        // respawns or the first/third-person toggle fires.
        private Camera cachedCamera;
        private float nextCameraScanTime;

        private Camera ResolveLookCamera()
        {
            if (cachedCamera != null && cachedCamera.isActiveAndEnabled && Time.unscaledTime < nextCameraScanTime)
                return cachedCamera;

            nextCameraScanTime = Time.unscaledTime + 0.5f;
            cachedCamera = FindActiveCamera();
            return cachedCamera;
        }

        /// <summary>
        /// Right-stick look only ever moves the FIRST-PERSON view -- the small top-right
        /// panel, or the main view once the first/third-person toggle swaps it in. Orbiting
        /// the third-person camera swung the whole scene around the avatar, which is what
        /// made looking around while driving nauseating; a head turn inside a small inset is
        /// far easier to tolerate. Returns null when no first-person view exists, and the
        /// stick then does nothing.
        /// </summary>
        private static Camera FindActiveCamera()
        {
            foreach (var firstPerson in FindObjectsOfType<IVI.FirstPersonCameraLevel>())
            {
                if (firstPerson == null)
                    continue;
                var cam = firstPerson.GetComponent<Camera>();
                if (cam != null && cam.isActiveAndEnabled)
                    return cam;
            }
            return null;
        }

        private static bool IsLookBlocked(Camera cam)
        {
            var review = SessionReview.SessionReviewManager.Instance;
            if (review != null && (review.IsReviewModeActive || review.IsWorldBuildingModeActive))
                return true;

            // The runtime-editor free camera has its own mouse/keyboard look.
            return cam.GetComponent<UnityTemplateProjects.SimpleCameraController>() != null;
        }
    }
}
