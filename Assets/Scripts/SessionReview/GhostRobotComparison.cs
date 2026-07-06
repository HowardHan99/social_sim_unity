using UnityEngine;
using Rerun;

namespace SessionReview
{
    /// <summary>
    /// Review-only overlay that shows up to three translucent "ghost robots" so the
    /// robot's proxemics can be compared across different trajectory sources at the
    /// same replay moment:
    ///   • Drawn    – the human-drawn trajectory (DrawTraj / TrajectoryManager)
    ///   • Planned  – the recorded ROS nav plan for this trial
    ///   • Past     – where the robot actually was in the previous trial of this session
    ///
    /// Each ghost is paced "normalized to trial time": at replay progress t (0..1) the
    /// ghost sits at t along its own path, so all ghosts and the real (recorded) robot
    /// reach their goal together and can be compared against the same pedestrians.
    ///
    /// Driven from RewindController: it calls Begin() on review entry, End() on exit,
    /// and ToggleGhosts() from the review hotkey. The component pulls the live
    /// normalized time from the RewindController each frame in LateUpdate.
    /// </summary>
    public class GhostRobotComparison : MonoBehaviour
    {
        private static readonly Color DrawnColor = new Color(0.20f, 0.85f, 1.00f, 0.55f);
        private static readonly Color PlannedColor = new Color(0.25f, 1.00f, 0.35f, 0.55f);
        private static readonly Color PastColor = new Color(1.00f, 0.60f, 0.15f, 0.55f);

        // Fraction of the path swept per second while drawing (replay is paused there),
        // so ~6.7 s per full loop. The three ghosts sweep their paths in sync.
        private const float DrawPreviewLoopSpeed = 0.15f;

        private RewindController rewind;
        private LiveTrajectoryRecorder recorder;
        private TrajectoryManager drawManager;
        private string robotId;
        private Vector3[] plannedRoute;
        private bool hasPrevious;
        private float previousRecStart;
        private float previousRecDur;

        private bool showGhosts;
        private bool active;

        private GameObject container;
        private Ghost drawnGhost;
        private Ghost plannedGhost;
        private Ghost pastGhost;

        public bool ShowGhosts => showGhosts;

        /// <summary>Configure the ghosts for a review session. Called by RewindController.EnterRewind.</summary>
        public void Begin(RewindController rewindController, LiveTrajectoryRecorder liveRecorder,
            TrajectoryManager trajectoryManager, string robotObjectId, Vector3[] plannedPath,
            bool previousTrialAvailable, float prevRecStart, float prevRecDur)
        {
            rewind = rewindController;
            recorder = liveRecorder;
            drawManager = trajectoryManager;
            robotId = robotObjectId;
            plannedRoute = plannedPath;
            hasPrevious = previousTrialAvailable;
            previousRecStart = prevRecStart;
            previousRecDur = prevRecDur;
            active = true;
            RefreshVisibility();
        }

        /// <summary>Tear down when leaving review.</summary>
        public void End()
        {
            active = false;
            DestroyGhosts();
        }

        public void ToggleGhosts()
        {
            showGhosts = !showGhosts;
            RefreshVisibility();
        }

        private void RefreshVisibility()
        {
            // LateUpdate owns per-frame visibility; just react immediately to a toggle.
            if (!active && container != null)
                container.SetActive(false);
        }

        private void LateUpdate()
        {
            if (!active || rewind == null)
                return;

            // Ghosts are shown during replay when toggled on (F6), and ALWAYS while
            // drawing a trajectory so the drawn robot can be compared live against the
            // planned + last-run robots.
            bool drawing = drawManager != null && drawManager.IsDrawMode;
            bool inReplay = rewind.IsRewinding;
            bool shouldShow = drawing || (inReplay && showGhosts);

            if (!shouldShow)
            {
                if (container != null && container.activeSelf)
                    container.SetActive(false);
                return;
            }

            if (container == null)
                BuildGhosts();
            if (!container.activeSelf)
                container.SetActive(true);

            // While drawing, replay is paused, so sweep a looping clock instead so all
            // three robots continuously traverse their paths for comparison. During normal
            // replay, follow the scrubbed replay time.
            float t = drawing
                ? Mathf.Repeat(Time.unscaledTime * DrawPreviewLoopSpeed, 1f)
                : Mathf.Clamp01(rewind.NormalizedTime);

            // Drawn path: the live in-progress stroke while drawing, else the saved path.
            if (TryGetDrawnPose(t, drawing, out Vector3 dPos, out Quaternion dRot))
                drawnGhost.SetPose(dPos, dRot);
            else
                drawnGhost.SetVisible(false);

            // Planned (ROS nav) path from the reviewed run.
            if (EvaluatePoseAlongPath(plannedRoute, t, out Vector3 pPos, out Quaternion pRot))
                plannedGhost.SetPose(pPos, pRot);
            else
                plannedGhost.SetVisible(false);

            // Previous trial's actual robot pose at the same normalized progress.
            if (hasPrevious && recorder != null && !string.IsNullOrEmpty(robotId) && previousRecDur > 0f)
            {
                float sampleTime = previousRecStart + t * previousRecDur;
                var states = recorder.GetStateAtTime(sampleTime);
                if (states != null && states.TryGetValue(robotId, out ObjectState st))
                    pastGhost.SetPose(st.position, st.rotation);
                else
                    pastGhost.SetVisible(false);
            }
            else
            {
                pastGhost.SetVisible(false);
            }
        }

        /// <summary>
        /// Drawn-ghost pose: while drawing, evaluate along the live in-progress stroke so
        /// the ghost updates as you draw; otherwise (and as a fallback before anything is
        /// drawn) use the saved follow-trajectory.
        /// </summary>
        private bool TryGetDrawnPose(float t, bool drawing, out Vector3 position, out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            if (drawManager == null)
                return false;

            if (drawing &&
                drawManager.TryGetLiveDrawnPath(out Vector3[] livePath) &&
                EvaluatePoseAlongPath(livePath, t, out position, out rotation))
                return true;

            return drawManager.TryEvaluateFollowPoseAtNormalized(t, out position, out rotation);
        }

        private void BuildGhosts()
        {
            container = new GameObject("GhostRobotComparison");
            container.transform.SetParent(null, worldPositionStays: false);
            container.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);

            drawnGhost = Ghost.Create(container.transform, "Ghost_Drawn", DrawnColor);
            plannedGhost = Ghost.Create(container.transform, "Ghost_Planned", PlannedColor);
            pastGhost = Ghost.Create(container.transform, "Ghost_Past", PastColor);
        }

        private void DestroyGhosts()
        {
            if (container != null)
                Destroy(container);
            container = null;
            drawnGhost = null;
            plannedGhost = null;
            pastGhost = null;
        }

        void OnDestroy()
        {
            DestroyGhosts();
        }

        /// <summary>Interpolates a pose (position + yaw) at normalized arc-length along a polyline.</summary>
        private static bool EvaluatePoseAlongPath(Vector3[] pts, float normalized, out Vector3 position, out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            if (pts == null || pts.Length < 2)
                return false;

            float total = 0f;
            for (int i = 1; i < pts.Length; i++)
                total += Vector3.Distance(pts[i - 1], pts[i]);
            if (total <= 0.0001f)
                return false;

            float target = Mathf.Clamp01(normalized) * total;
            float traversed = 0f;
            for (int i = 1; i < pts.Length; i++)
            {
                Vector3 from = pts[i - 1];
                Vector3 to = pts[i];
                float seg = Vector3.Distance(from, to);
                if (seg <= 0.0001f)
                    continue;

                if (traversed + seg >= target)
                {
                    float segT = (target - traversed) / seg;
                    position = Vector3.Lerp(from, to, segT);
                    Vector3 fwd = to - from;
                    fwd.y = 0f;
                    if (fwd.sqrMagnitude > 0.0001f)
                        rotation = Quaternion.LookRotation(fwd.normalized, Vector3.up);
                    return true;
                }
                traversed += seg;
            }

            position = pts[pts.Length - 1];
            Vector3 endFwd = pts[pts.Length - 1] - pts[pts.Length - 2];
            endFwd.y = 0f;
            if (endFwd.sqrMagnitude > 0.0001f)
                rotation = Quaternion.LookRotation(endFwd.normalized, Vector3.up);
            return true;
        }

        /// <summary>A single translucent capsule + facing nub that marks a robot pose.</summary>
        private class Ghost
        {
            private readonly GameObject root;
            private const float BodyHalfHeight = 0.45f;

            private Ghost(GameObject root) { this.root = root; }

            public static Ghost Create(Transform parent, string name, Color color)
            {
                var root = new GameObject(name);
                root.transform.SetParent(parent, worldPositionStays: false);

                Material mat = MakeTransparentMaterial(color);

                var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                body.name = "Body";
                StripCollider(body);
                body.transform.SetParent(root.transform, worldPositionStays: false);
                body.transform.localPosition = new Vector3(0f, BodyHalfHeight, 0f);
                body.transform.localScale = new Vector3(0.4f, BodyHalfHeight, 0.4f);
                body.GetComponent<Renderer>().sharedMaterial = mat;

                // Facing indicator so the ghost's heading reads at a glance.
                var nose = GameObject.CreatePrimitive(PrimitiveType.Cube);
                nose.name = "Nose";
                StripCollider(nose);
                nose.transform.SetParent(root.transform, worldPositionStays: false);
                nose.transform.localPosition = new Vector3(0f, BodyHalfHeight, 0.28f);
                nose.transform.localScale = new Vector3(0.12f, 0.12f, 0.3f);
                nose.GetComponent<Renderer>().sharedMaterial = mat;

                return new Ghost(root);
            }

            public void SetPose(Vector3 position, Quaternion rotation)
            {
                if (root == null) return;
                if (!root.activeSelf) root.SetActive(true);
                root.transform.SetPositionAndRotation(position, rotation);
            }

            public void SetVisible(bool visible)
            {
                if (root != null && root.activeSelf != visible)
                    root.SetActive(visible);
            }

            private static void StripCollider(GameObject go)
            {
                var col = go.GetComponent<Collider>();
                if (col != null) Destroy(col);
            }

            private static Material MakeTransparentMaterial(Color color)
            {
                var m = new Material(Shader.Find("Standard"));
                m.SetFloat("_Mode", 3f); // Transparent
                m.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                m.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                m.SetInt("_ZWrite", 0);
                m.DisableKeyword("_ALPHATEST_ON");
                m.EnableKeyword("_ALPHABLEND_ON");
                m.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                m.renderQueue = 3000;
                m.color = color;
                m.SetColor("_EmissionColor", color * 0.4f);
                m.EnableKeyword("_EMISSION");
                return m;
            }
        }
    }
}
