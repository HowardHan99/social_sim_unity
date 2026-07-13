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
        public float yawSpeed = 150f;
        public float pitchSpeed = 100f;
        public float maxYawOffset = 160f;
        public float maxPitchOffset = 55f;
        public float recenterSpeed = 220f;
        public bool invertPitch = false;

        private Camera trackedCamera;
        private Quaternion baseLocalRotation = Quaternion.identity;
        private bool bareOffsetApplied;
        private float yawOffset;
        private float pitchOffset;

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
            Camera cam = FindActiveCamera();
            if (cam != trackedCamera)
            {
                trackedCamera = cam;
                yawOffset = 0f;
                pitchOffset = 0f;
                bareOffsetApplied = false;
                baseLocalRotation = cam != null ? cam.transform.localRotation : Quaternion.identity;
            }

            if (cam == null)
                return;

            if (IsLookBlocked(cam))
            {
                yawOffset = 0f;
                pitchOffset = 0f;
                bareOffsetApplied = false;
                return;
            }

            float lookX = JoystickProfiles.CameraLookX();
            float lookY = JoystickProfiles.CameraLookY();
            float dt = Time.unscaledDeltaTime;

            if (lookX != 0f || lookY != 0f)
            {
                float pitchSign = invertPitch ? -1f : 1f;
                yawOffset = Mathf.Clamp(yawOffset + lookX * yawSpeed * dt, -maxYawOffset, maxYawOffset);
                pitchOffset = Mathf.Clamp(pitchOffset + lookY * pitchSpeed * pitchSign * dt, -maxPitchOffset, maxPitchOffset);
            }
            else
            {
                yawOffset = Mathf.MoveTowards(yawOffset, 0f, recenterSpeed * dt);
                pitchOffset = Mathf.MoveTowards(pitchOffset, 0f, recenterSpeed * dt);
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

        private static Camera FindActiveCamera()
        {
            Camera main = Camera.main;
            if (main != null && main.isActiveAndEnabled)
                return main;

            Camera[] cams = Camera.allCameras;
            return cams.Length > 0 ? cams[0] : null;
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
