using System.Collections.Generic;
using UnityEngine;
using Rerun;

namespace SessionReview
{
    /// <summary>
    /// Review-only overlay that shows translucent copies of the REAL robot on the
    /// alternative trajectories so its proxemics can be compared at the current
    /// replay moment:
    ///   • Drawn   – the human-drawn trajectory (DrawTraj / TrajectoryManager)
    ///   • Planned – the recorded ROS nav plan for this trial
    /// The trajectory the robot actually drove gets NO ghost: the real (replayed)
    /// robot is already sitting on it, so a ghost there would just duplicate it.
    ///
    /// Pacing: ghosts are placed by the real robot's odometer, not by trial time.
    /// At the current replay moment the real robot has driven some distance along
    /// its own path; each ghost sits at that same traveled distance along its own
    /// path (clamped at its end). When the robot stops or reaches its goal the
    /// ghosts stop with it — they never run ahead on the whole-trial clock.
    ///
    /// Interaction: with the replay paused, a ghost can be picked up with the
    /// pointer (left mouse / single touch) and dragged along its own trajectory to
    /// try out a different proximity. Scrubbing or playing the replay snaps the
    /// ghosts back to odometer pacing. Dragging is disabled while the trajectory
    /// draw mode owns the pointer.
    ///
    /// OFF by default: nothing is shown until the toggle is turned on (F6 in
    /// review, the draw-panel Ghosts button, or enableGhosts in the inspector).
    /// Ghost visuals are cloned from the real robot's renderers (SEAN.instance.robot
    /// base_link hierarchy), tinted per source. No hand-built placeholder geometry.
    ///
    /// Driven from RewindController: it calls Begin() on review entry, End() on exit,
    /// and ToggleGhosts() from the review hotkey.
    /// </summary>
    public class GhostRobotComparison : MonoBehaviour
    {
        [Header("Toggle")]
        [Tooltip("Master switch. Ghosts stay hidden until this is enabled (F6 toggles it in review). Off by default.")]
        [SerializeField] private bool enableGhosts = false;

        [Header("Dragging")]
        [Tooltip("Screen-space pick radius (pixels) for grabbing a ghost with the pointer.")]
        [SerializeField] private float ghostPickRadiusPx = 60f;

        private static readonly Color DrawnColor = new Color(0.20f, 0.85f, 1.00f, 0.45f);
        private static readonly Color PlannedColor = new Color(0.25f, 1.00f, 0.35f, 0.45f);

        private RewindController rewind;
        private TrajectoryManager drawManager;
        private string robotId;
        private Vector3[] plannedRoute;

        // Real-robot odometer over the reviewed trial: timestamp -> cumulative XZ
        // distance driven. Sampled from the recording in Begin().
        private readonly List<float> odoTimes = new List<float>();
        private readonly List<float> odoDists = new List<float>();

        private bool active;
        private bool warnedNoRobot;

        private GameObject container;
        private Ghost drawnGhost;
        private Ghost plannedGhost;

        // Path polylines the ghosts currently follow (index 0 = drawn, 1 = planned).
        private Vector3[] drawnPath;
        private bool wasDrawing;

        // Manual drag overrides (meters along the ghost's own path; < 0 = follow odometer).
        private float drawnManualDist = -1f;
        private float plannedManualDist = -1f;
        private int draggedGhostIndex = -1; // 0 = drawn, 1 = planned
        private float overrideRefTime;      // replay time the overrides belong to

        public bool ShowGhosts => enableGhosts;

        /// <summary>Configure the ghosts for a review session. Called by RewindController.EnterRewind.</summary>
        public void Begin(RewindController rewindController, TrajectoryManager trajectoryManager,
            string robotObjectId, Vector3[] plannedPath,
            StateRecording recording, float recStart, float recEnd)
        {
            rewind = rewindController;
            drawManager = trajectoryManager;
            robotId = robotObjectId;
            plannedRoute = plannedPath;
            active = true;
            warnedNoRobot = false;
            wasDrawing = false;
            ClearManualOverrides();
            RefreshSavedDrawnPath();
            BuildOdometer(recording, recStart, recEnd);
        }

        /// <summary>Tear down when leaving review.</summary>
        public void End()
        {
            active = false;
            ClearManualOverrides();
            DestroyGhosts();
        }

        public void ToggleGhosts()
        {
            enableGhosts = !enableGhosts;
            if (!enableGhosts)
                ClearManualOverrides();
            SessionReviewLog.Log($"[SessionReview] Ghost robot comparison {(enableGhosts ? "enabled" : "disabled")}.");
        }

        private void LateUpdate()
        {
            if (!active || rewind == null)
                return;

            // Only when explicitly enabled and reviewing. The ghosts follow the real
            // robot's odometer (or a manual drag) — they never sweep on their own.
            bool shouldShow = enableGhosts && rewind.IsRewinding;

            if (!shouldShow)
            {
                draggedGhostIndex = -1;
                if (container != null && container.activeSelf)
                    container.SetActive(false);
                return;
            }

            if (container == null)
                BuildGhosts();
            if (container == null)
                return; // real robot not found; nothing to clone
            if (!container.activeSelf)
                container.SetActive(true);

            // Drawn path: the live in-progress stroke while drawing, else the saved path
            // (re-captured once when a drawing session ends).
            bool drawing = drawManager != null && drawManager.IsDrawMode;
            if (drawing)
            {
                if (!(drawManager.TryGetLiveDrawnPath(out drawnPath)))
                    RefreshSavedDrawnPath();
            }
            else if (wasDrawing || drawnPath == null)
            {
                RefreshSavedDrawnPath();
            }
            wasDrawing = drawing;

            HandleDragInput(drawing);

            // Scrubbing / playing moves the replay clock: return dragged ghosts to
            // odometer pacing so they stay comparable to the recorded robot again.
            if (draggedGhostIndex < 0 && HasManualOverride() &&
                Mathf.Abs(rewind.CurrentTime - overrideRefTime) > 0.01f)
            {
                ClearManualOverrides();
            }

            float odometer = EvaluateOdometer(rewind.CurrentTime);

            PlaceGhost(drawnGhost, drawnPath, drawnManualDist, odometer);
            PlaceGhost(plannedGhost, plannedRoute, plannedManualDist, odometer);
        }

        private void PlaceGhost(Ghost ghost, Vector3[] path, float manualDist, float odometer)
        {
            if (ghost == null)
                return;

            float length = PathLength(path);
            if (length <= 0.0001f)
            {
                ghost.SetVisible(false);
                return;
            }

            float dist;
            if (manualDist >= 0f)
                dist = manualDist;
            else if (odometer >= 0f)
                dist = odometer;
            else
                dist = Mathf.Clamp01(rewind.NormalizedTime) * length; // no odometer data: degrade to trial time

            if (EvaluatePoseAtDistance(path, dist, out Vector3 pos, out Quaternion rot))
                ghost.SetPose(pos, rot);
            else
                ghost.SetVisible(false);
        }

        // ── Odometer (real robot's traveled distance over replay time) ────────────

        private void BuildOdometer(StateRecording recording, float recStart, float recEnd)
        {
            odoTimes.Clear();
            odoDists.Clear();

            if (recording == null || string.IsNullOrEmpty(robotId))
                return;
            if (recording.timelineDict == null)
                recording.BuildCache();
            if (recording.timelineDict == null ||
                !recording.timelineDict.TryGetValue(robotId, out ObjectStateTimeline timeline))
                return;

            float cumulative = 0f;
            Vector3 prev = Vector3.zero;
            bool first = true;
            foreach (var state in timeline.states)
            {
                if (state.timestamp < recStart || state.timestamp > recEnd)
                    continue;

                if (!first)
                {
                    Vector3 step = state.position - prev;
                    step.y = 0f; // ground-plane odometry; ignores the initial spawn fall
                    float len = step.magnitude;
                    if (len > 0.001f) // don't integrate stationary jitter
                        cumulative += len;
                }

                odoTimes.Add(state.timestamp);
                odoDists.Add(cumulative);
                prev = state.position;
                first = false;
            }
        }

        /// <summary>Distance (m) the real robot had driven at the given recording time; -1 when unavailable.</summary>
        private float EvaluateOdometer(float time)
        {
            int count = odoTimes.Count;
            if (count == 0)
                return -1f;
            if (time <= odoTimes[0])
                return odoDists[0];
            if (time >= odoTimes[count - 1])
                return odoDists[count - 1];

            int lo = 0, hi = count - 1;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) / 2;
                if (odoTimes[mid] <= time) lo = mid;
                else hi = mid;
            }

            float span = odoTimes[hi] - odoTimes[lo];
            float t = span > 0.0001f ? (time - odoTimes[lo]) / span : 0f;
            return Mathf.Lerp(odoDists[lo], odoDists[hi], t);
        }

        // ── Dragging (try a different proximity along a ghost's own path) ─────────

        private void HandleDragInput(bool drawing)
        {
            // While drawing, the pointer paints strokes — never also drags ghosts.
            if (drawing)
            {
                draggedGhostIndex = -1;
                return;
            }

            Camera cam = rewind.GetActiveReviewCamera();
            if (cam == null)
            {
                draggedGhostIndex = -1;
                return;
            }

            Vector3 pointer = Input.mousePosition;

            if (Input.GetMouseButtonDown(0) && !PointerBlockedByUi(pointer))
            {
                draggedGhostIndex = PickGhost(cam, pointer);
                if (draggedGhostIndex >= 0)
                {
                    overrideRefTime = rewind.CurrentTime;
                    SessionReviewLog.Log($"[SessionReview] Dragging {(draggedGhostIndex == 0 ? "drawn" : "planned")} ghost.");
                }
            }

            if (draggedGhostIndex >= 0 && Input.GetMouseButton(0))
            {
                Vector3[] path = draggedGhostIndex == 0 ? drawnPath : plannedRoute;
                Ghost ghost = draggedGhostIndex == 0 ? drawnGhost : plannedGhost;
                if (path != null && path.Length >= 2 && ghost != null &&
                    RaycastGround(cam, pointer, ghost.Position.y, out Vector3 ground))
                {
                    float dist = ClosestDistanceAlongPath(path, ground);
                    if (draggedGhostIndex == 0) drawnManualDist = dist;
                    else plannedManualDist = dist;
                }
            }

            if (!Input.GetMouseButton(0))
                draggedGhostIndex = -1;
        }

        private int PickGhost(Camera cam, Vector3 pointer)
        {
            int best = -1;
            float bestPx = ghostPickRadiusPx;

            TryPick(cam, pointer, drawnGhost, 0, ref best, ref bestPx);
            TryPick(cam, pointer, plannedGhost, 1, ref best, ref bestPx);
            return best;
        }

        private static void TryPick(Camera cam, Vector3 pointer, Ghost ghost, int index,
            ref int best, ref float bestPx)
        {
            if (ghost == null || !ghost.IsVisible)
                return;

            Vector3 screen = cam.WorldToScreenPoint(ghost.Position);
            if (screen.z <= 0f)
                return;

            float px = Vector2.Distance(new Vector2(screen.x, screen.y), new Vector2(pointer.x, pointer.y));
            if (px < bestPx)
            {
                bestPx = px;
                best = index;
            }
        }

        private bool PointerBlockedByUi(Vector3 pointer)
        {
            Vector2 guiPoint = new Vector2(pointer.x, Screen.height - pointer.y);
            if (ReviewPanels.AnyPanelContains(guiPoint))
                return true;
            return rewind != null && rewind.ProgressBarContains(guiPoint);
        }

        private static bool RaycastGround(Camera cam, Vector3 pointer, float groundY, out Vector3 point)
        {
            point = Vector3.zero;
            Ray ray = cam.ScreenPointToRay(pointer);
            Plane plane = new Plane(Vector3.up, new Vector3(0f, groundY, 0f));
            if (!plane.Raycast(ray, out float enter))
                return false;
            point = ray.GetPoint(enter);
            return true;
        }

        /// <summary>Arc-length (m, XZ) of the path point closest to the given world point.</summary>
        private static float ClosestDistanceAlongPath(Vector3[] pts, Vector3 worldPoint)
        {
            float bestSqr = float.MaxValue;
            float bestDist = 0f;
            float traversed = 0f;
            Vector2 p = new Vector2(worldPoint.x, worldPoint.z);

            for (int i = 1; i < pts.Length; i++)
            {
                Vector2 a = new Vector2(pts[i - 1].x, pts[i - 1].z);
                Vector2 b = new Vector2(pts[i].x, pts[i].z);
                Vector2 ab = b - a;
                float segLen = ab.magnitude;

                float t = 0f;
                if (segLen > 0.0001f)
                    t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / (segLen * segLen));

                Vector2 closest = a + ab * t;
                float sqr = (p - closest).sqrMagnitude;
                if (sqr < bestSqr)
                {
                    bestSqr = sqr;
                    bestDist = traversed + segLen * t;
                }

                traversed += segLen;
            }

            return bestDist;
        }

        private bool HasManualOverride()
        {
            return drawnManualDist >= 0f || plannedManualDist >= 0f;
        }

        private void ClearManualOverrides()
        {
            drawnManualDist = -1f;
            plannedManualDist = -1f;
            draggedGhostIndex = -1;
        }

        // ── Paths ─────────────────────────────────────────────────────────────────

        private void RefreshSavedDrawnPath()
        {
            if (drawManager == null || !drawManager.TryGetFollowPathPoints(out drawnPath))
                drawnPath = null;
        }

        private static float PathLength(Vector3[] pts)
        {
            if (pts == null || pts.Length < 2)
                return 0f;

            float total = 0f;
            for (int i = 1; i < pts.Length; i++)
            {
                Vector3 step = pts[i] - pts[i - 1];
                step.y = 0f;
                total += step.magnitude;
            }
            return total;
        }

        /// <summary>Pose (position + yaw) at the given XZ arc-length along a polyline (clamped to its ends).</summary>
        private static bool EvaluatePoseAtDistance(Vector3[] pts, float distance, out Vector3 position, out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            if (pts == null || pts.Length < 2)
                return false;

            distance = Mathf.Max(0f, distance);
            float traversed = 0f;
            for (int i = 1; i < pts.Length; i++)
            {
                Vector3 from = pts[i - 1];
                Vector3 to = pts[i];
                Vector3 step = to - from;
                Vector3 flat = new Vector3(step.x, 0f, step.z);
                float seg = flat.magnitude;
                if (seg <= 0.0001f)
                    continue;

                if (traversed + seg >= distance)
                {
                    float segT = (distance - traversed) / seg;
                    position = Vector3.Lerp(from, to, segT);
                    if (flat.sqrMagnitude > 0.0001f)
                        rotation = Quaternion.LookRotation(flat.normalized, Vector3.up);
                    return true;
                }
                traversed += seg;
            }

            // Past the end: clamp to the final pose.
            position = pts[pts.Length - 1];
            Vector3 endFwd = pts[pts.Length - 1] - pts[pts.Length - 2];
            endFwd.y = 0f;
            if (endFwd.sqrMagnitude > 0.0001f)
                rotation = Quaternion.LookRotation(endFwd.normalized, Vector3.up);
            return true;
        }

        // ── Ghost construction ────────────────────────────────────────────────────

        private void BuildGhosts()
        {
            Transform robotSource = FindRobotVisualSource();
            if (robotSource == null)
            {
                if (!warnedNoRobot)
                {
                    Debug.LogWarning("[GhostRobotComparison] Real robot (SEAN.instance.robot.base_link) not found; ghost comparison unavailable.");
                    warnedNoRobot = true;
                }
                return;
            }

            container = new GameObject("GhostRobotComparison");
            container.transform.SetParent(null, worldPositionStays: false);
            container.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);

            drawnGhost = Ghost.CreateFromRobot(container.transform, "Ghost_Drawn", DrawnColor, robotSource);
            plannedGhost = Ghost.CreateFromRobot(container.transform, "Ghost_Planned", PlannedColor, robotSource);
        }

        private static Transform FindRobotVisualSource()
        {
            var sean = SEAN.SEAN.instance;
            if (sean != null && sean.robot != null && sean.robot.base_link != null)
                return sean.robot.base_link.transform;
            return null;
        }

        private void DestroyGhosts()
        {
            if (container != null)
                Destroy(container);
            container = null;
            drawnGhost = null;
            plannedGhost = null;
        }

        void OnDestroy()
        {
            DestroyGhosts();
        }

        /// <summary>
        /// A translucent render-only copy of the real robot marking one candidate pose.
        /// Built by cloning the robot's mesh renderers (no scripts, cameras, colliders or
        /// physics come along), preserving each part's pose relative to base_link.
        /// </summary>
        private class Ghost
        {
            private readonly GameObject root;

            private Ghost(GameObject root) { this.root = root; }

            public Vector3 Position => root != null ? root.transform.position : Vector3.zero;
            public bool IsVisible => root != null && root.activeInHierarchy;

            public static Ghost CreateFromRobot(Transform parent, string name, Color color, Transform robotBaseLink)
            {
                var root = new GameObject(name);
                root.transform.SetParent(parent, worldPositionStays: false);

                Material mat = MakeTransparentMaterial(color);

                foreach (Renderer src in robotBaseLink.GetComponentsInChildren<Renderer>())
                {
                    if (src == null || !src.enabled || !src.gameObject.activeInHierarchy)
                        continue;

                    Mesh mesh = null;
                    if (src is SkinnedMeshRenderer skinned)
                    {
                        mesh = skinned.sharedMesh;
                    }
                    else if (src is MeshRenderer)
                    {
                        var mf = src.GetComponent<MeshFilter>();
                        if (mf != null)
                            mesh = mf.sharedMesh;
                    }
                    // Line/trail/particle renderers are overlay artifacts, not robot geometry.
                    if (mesh == null)
                        continue;

                    var part = new GameObject(src.name);
                    part.transform.SetParent(root.transform, worldPositionStays: false);

                    // Preserve the part's pose relative to base_link so the assembled
                    // ghost matches the real robot when the root is set to a path pose.
                    Matrix4x4 rel = robotBaseLink.worldToLocalMatrix * src.transform.localToWorldMatrix;
                    part.transform.localPosition = rel.GetColumn(3);
                    part.transform.localRotation = rel.rotation;
                    part.transform.localScale = rel.lossyScale;

                    part.AddComponent<MeshFilter>().sharedMesh = mesh;
                    var mr = part.AddComponent<MeshRenderer>();
                    var mats = new Material[Mathf.Max(1, mesh.subMeshCount)];
                    for (int i = 0; i < mats.Length; i++)
                        mats[i] = mat;
                    mr.sharedMaterials = mats;
                    mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    mr.receiveShadows = false;
                }

                if (root.transform.childCount == 0)
                    Debug.LogWarning($"[GhostRobotComparison] No mesh renderers found under \"{robotBaseLink.name}\"; ghost \"{name}\" will be invisible.");

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
