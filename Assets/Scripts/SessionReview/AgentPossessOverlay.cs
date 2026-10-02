using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using SEAN.Scenario.Agents;

namespace SessionReview
{
    /// <summary>
    /// Agent Control panel on the secondary display (Display 2): lets the operator pick
    /// any background agent (pedestrian, cyclist, scooter user, PWDAutonomous - everyone
    /// except the human's "PWDPlayer") and take it over with first-person tank controls,
    /// without touching the participant's Display 1 view.
    ///
    /// Flow: [`] toggles the panel; select an agent by clicking its row, or press
    /// "Pick In Scene" for a world-building-style free-fly camera (WASD/QE fly, RMB look,
    /// wheel dolly) rendered on the same display where clicking an agent selects it;
    /// clicking the already-selected agent (or its row) again takes control immediately,
    /// as does the "Take Control" button. While controlling: W/S drive, A/D turn, Shift
    /// run, RMB free-look, Esc releases the agent back to its own AI.
    ///
    /// Possession recipe follows ManualWheelchairController.SetManualMode: disable the
    /// Base/SFPWDAgent component (its INavigable replanning coroutine is left running so
    /// the path stays fresh), force root motion off, drive the transform directly, then
    /// restore everything on release (SFPWDAgents get RestartNavigationCoroutine).
    /// While picking or possessing, ManualWheelchairController and VelocityController
    /// suppress their keyboard input (see <see cref="KeyboardCaptured"/>) so WASD/Shift
    /// don't also drive the participant's avatar or the robot; joystick input is
    /// untouched and stays with the participant.
    ///
    /// Uses uGUI (Canvas.targetDisplay) like AgentSpeedOverlay because IMGUI can only
    /// render on Display 1. Self-bootstraps at runtime; no scene wiring needed.
    /// </summary>
    public class AgentPossessOverlay : MonoBehaviour
    {
        private static AgentPossessOverlay instance;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (instance != null) return;
            var go = new GameObject("AgentPossessOverlay");
            go.AddComponent<AgentPossessOverlay>();
            DontDestroyOnLoad(go);
        }

        [Tooltip("Shows/hides the panel. Ctrl+key moves it between displays.")]
        public KeyCode toggleKey = KeyCode.BackQuote;

        [Tooltip("Display the panel renders on. 0 = main display, 1 = Display 2 (default).")]
        public int targetDisplay = 1;

        [Tooltip("Display used for the selected/possessed agent third-person view. 1 = Display 2 (same as the panel; the pick camera and this view share it, the view yields while picking).")]
        public int selectedAgentDisplay = 1;

        [Header("Pick-mode fly camera")]
        public float flySpeed = 8f;
        public float flyBoost = 3f;
        public float flyLookSensitivity = 3f;
        public float flyStartHeight = 16f;

        /// <summary>
        /// True while the operator is scene-picking or driving a possessed agent.
        /// ManualWheelchairController and VelocityController skip their keyboard input
        /// (WASD/arrows and the Shift mode toggles) while this is set, so the operator's
        /// driving keys don't leak into the participant's avatar or the robot.
        /// </summary>
        public static bool KeyboardCaptured =>
            instance != null && (instance.pickModeActive || instance.possessedController != null);

        public static bool PanelVisible => instance != null && instance.visible;

        /// <summary>True while the scene-pick fly camera owns the mouse (RMB/wheel).</summary>
        public static bool PickModeActive => instance != null && instance.pickModeActive;

        private const float RescanInterval = 1.0f;

        // Layout, in the canvas' reference-resolution units.
        private const float Margin = 24f;
        private const float Pad = 16f;
        private const float Gap = 8f;
        private const float HeaderH = 30f;
        private const float HintH = 40f;
        private const float BtnH = 36f;
        private const float StatusH = 28f;
        private const float SpeedH = 34f;
        private const float RowH = 32f;
        private const float PanelW = 470f;
        private const float PickScreenRadiusPx = 46f;
        private const float ScrollDeadzone = 0.01f;
        private const float SpeedStep = 0.2f;
        private const float MinControlSpeed = 0.2f;
        private const float MaxControlSpeed = 6.0f;
        private const float MaxGenericObjectPickSize = 8.0f;

        private class Row
        {
            public Base agent;
            public GameObject target;
            public string label;
            public Button button;
            public Text text;
        }

        private readonly List<Row> rows = new List<Row>();
        private readonly List<GameObject> rowObjects = new List<GameObject>();
        // Generic objects the operator has picked this session; keeps a released car in
        // the row list (it has no agent component that a rescan would find it by).
        private readonly List<GameObject> recentObjects = new List<GameObject>();

        // Live-world pose of every object we've ever possessed. Review playback drives
        // these objects along their recorded trajectories and leaves them wherever the
        // timeline stopped — robot/pedestrians have reset paths, generic props don't.
        // Poses are snapshotted every live frame (frozen during review) and restored
        // the moment review ends.
        private readonly Dictionary<GameObject, Pose> livePoses = new Dictionary<GameObject, Pose>();
        private bool wasReviewActive;
        private float nextRescanTime;
        private bool visible = true;

        // Selection / pick mode
        private Base selectedAgent;
        private GameObject selectedObject;
        private GameObject selectedViewObject;
        private GameObject selectedViewCamGo;
        private Camera selectedViewCam;
        private bool pickModeActive;
        private GameObject pickCamGo;
        private Camera pickCam;
        private float pickYaw;
        private float pickPitch;

        // Possession state (owned here; the controller only drives)
        private Base possessed;
        private GameObject possessedObject;
        private PossessedAgentController possessedController;
        private IVI.ManualWheelchairController possessedMwc;
        private bool mwcWasEnabled;
        private SEAN.Control.VelocityController possessedVelocity;
        private bool velocityWasEnabled;
        private Animator possessedAnimator;
        private bool prevRootMotion;
        private float possessedDefaultSpeed;
        private Rigidbody possessedAddedRigidbody;

        // UI
        private Canvas canvas;
        private RectTransform panel;
        private Text titleText;
        private Text hintText;
        private Text statusText;
        private Button pickButton;
        private Text pickButtonText;
        private Button controlButton;
        private Button releaseButton;
        private Text speedText;
        private Button speedDownButton;
        private Button speedResetButton;
        private Button speedUpButton;
        private Text markerText;   // name tag over the selected agent in pick mode
        private Canvas viewStatusCanvas;   // SELECTED/DRIVING watermark on the Display-3 view
        private Text viewStatusText;
        private Font uiFont;

        private static readonly Color SelectedColor = new Color(0.55f, 1f, 0.7f);
        private static readonly Color PossessedColor = new Color(1f, 0.75f, 0.4f);

        private bool PossessionActive => possessedController != null;

        void Awake()
        {
            if (instance != null && instance != this)
            {
                Destroy(gameObject);
                return;
            }
            instance = this;
        }

        void Update()
        {
            EnsureUi();

            bool textEntryActive = SessionReviewInputFocus.IsTextEntryActive();
            if (!textEntryActive && Input.GetKeyDown(toggleKey))
            {
                bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl) ||
                            Input.GetKey(KeyCode.LeftCommand) || Input.GetKey(KeyCode.RightCommand);
                if (ctrl)
                {
                    targetDisplay = targetDisplay == 0 ? 1 : 0;
                    visible = true;
                    ApplyDisplayToCameras();
                }
                else
                {
                    visible = !visible;
                    // Hiding the panel also drops the pick camera (a possession, if any,
                    // keeps running - Esc or Release ends it).
                    if (!visible && pickModeActive) ExitPickMode();
                }
            }

            int desired = ResolveTargetDisplay();
            if (canvas.targetDisplay != desired) canvas.targetDisplay = desired;

            // Must run even while hidden: review displaces possessed objects and the
            // restore has to fire on the exact frame review ends.
            TrackReviewPoseRestore();

            // Review / world building / onboarding own the screen and the input - drop
            // everything so we never fight RuntimeEditorManager for clicks or Esc.
            if (ShouldHide())
            {
                if (pickModeActive) ExitPickMode();
                if (PossessionActive || possessed != null) Release();
                DestroySelectedAgentView();
                RefreshUi();
                return;
            }

            // Possessed agent died externally (trial reset, scene reload): the controller
            // and its camera died with it; just drop the stale references.
            if (possessedObject == null && (possessedController != null || possessedMwc != null ||
                                            possessedVelocity != null || possessedAnimator != null))
                ClearPossessionRefs();

            if (Time.unscaledTime >= nextRescanTime || AnyRowDead())
            {
                Rescan();
                nextRescanTime = Time.unscaledTime + RescanInterval;
            }

            if (pickModeActive && !textEntryActive)
                UpdatePickMode();

            UpdateSelectedAgentView();

            if (!textEntryActive && Input.GetKeyDown(KeyCode.Escape))
            {
                if (PossessionActive) Release();
                else if (pickModeActive) ExitPickMode();
            }

            RefreshUi();
        }

        // ---- Agent list --------------------------------------------------------

        private bool AnyRowDead()
        {
            foreach (var r in rows)
                if (r.target == null) return true;
            return false;
        }

        // Climbs to the outermost transform that still belongs to the same character rig.
        // A composite rig (Cyclist = Bicycle + rider siblings) can carry its agent or a
        // rigidbody on a CHILD; possessing that child drives the bike away and leaves the
        // rider standing, so every resolve funnels through here.
        private static GameObject ResolveRigRoot(GameObject candidate)
        {
            if (candidate == null) return null;
            Transform best = candidate.transform;
            for (Transform cur = candidate.transform.parent; cur != null; cur = cur.parent)
            {
                if (cur.GetComponent<IVI.ManualWheelchairController>() != null ||
                    cur.GetComponent<Base>() != null ||
                    cur.GetComponent<WorldBuildingCompositeAgentDriver>() != null ||
                    cur.GetComponent<WorldBuildingWanderPedestrian>() != null ||
                    cur.GetComponent<SEAN.Scenario.Obstacles.TrackedObstacle>() != null ||
                    cur.GetComponent<WorldBuildingPlacedObject>() != null)
                    best = cur;
            }
            return best.gameObject;
        }

        private static bool IsSelectable(Base agent)
        {
            if (agent == null) return false;
            if (agent is PlayerAgent || agent.GetType().FullName == "SEAN.Scenario.Agents.Playback.Agent") return false;   // replay ghosts
            return !string.Equals(agent.gameObject.name, "PWDPlayer", System.StringComparison.Ordinal);
        }

        private static bool IsSelectableTarget(GameObject target)
        {
            if (target == null) return false;
            Base agent = target.GetComponent<Base>();
            if (agent != null) return IsSelectable(agent);
            if (string.Equals(target.name, "PWDPlayer", System.StringComparison.Ordinal))
                return false;
            if (target.GetComponent<SEAN.Control.VelocityController>() != null ||
                target.GetComponentInParent<SEAN.Scenario.Robot>() != null)
                return true;
            if (IsUiOrCameraTarget(target))
                return false;
            return IsSelectableObjectTarget(target);
        }

        private static bool IsUiOrCameraTarget(GameObject target)
        {
            return target.GetComponent<Camera>() != null ||
                   target.GetComponentInChildren<Camera>(true) != null ||
                   target.GetComponentInParent<Canvas>() != null;
        }

        private static bool IsSelectableObjectTarget(GameObject target)
        {
            if (target == null || IsUiOrCameraTarget(target))
                return false;

            if (target.GetComponent<SEAN.Scenario.Obstacles.TrackedObstacle>() != null)
                return target.GetComponent<Rigidbody>() != null || TryGetRendererBounds(target, out _);

            Rigidbody rb = target.GetComponent<Rigidbody>();
            if (rb != null)
                return !TryGetTargetBounds(target, out Bounds rbBounds) ||
                       Mathf.Max(rbBounds.size.x, rbBounds.size.y, rbBounds.size.z) <= MaxGenericObjectPickSize;

            // No rigidbody yet — colliderless props like the GLB cars. Renderer bounds
            // within the prop size cap qualify; statics are excluded because a
            // static-batched mesh cannot be moved at runtime anyway.
            if (HasStaticParent(target))
                return false;
            return TryGetRendererBounds(target, out Bounds bounds) &&
                   Mathf.Max(bounds.size.x, bounds.size.y, bounds.size.z) <= MaxGenericObjectPickSize;
        }

        private static bool HasStaticParent(GameObject target)
        {
            for (Transform current = target != null ? target.transform : null; current != null; current = current.parent)
                if (current.gameObject.isStatic)
                    return true;
            return false;
        }

        private static bool TryGetTargetBounds(GameObject target, out Bounds bounds)
        {
            bounds = default;
            bool hasBounds = false;

            foreach (Renderer renderer in target.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer == null) continue;
                if (!hasBounds)
                {
                    bounds = renderer.bounds;
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }
            }

            foreach (Collider collider in target.GetComponentsInChildren<Collider>(true))
            {
                if (collider == null) continue;
                if (!hasBounds)
                {
                    bounds = collider.bounds;
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(collider.bounds);
                }
            }

            return hasBounds;
        }

        private static GameObject ResolvePickTarget(Collider collider)
        {
            if (collider == null) return null;

            Base agent = collider.GetComponentInParent<Base>();
            if (IsSelectable(agent)) return ResolveRigRoot(agent.gameObject);

            var robot = collider.GetComponentInParent<SEAN.Scenario.Robot>();
            if (robot != null)
                return robot.base_link != null ? robot.base_link : robot.gameObject;

            var velocity = collider.GetComponentInParent<SEAN.Control.VelocityController>();
            if (velocity != null)
                return ResolveVelocityTarget(velocity);

            var obstacle = collider.GetComponentInParent<SEAN.Scenario.Obstacles.TrackedObstacle>();
            if (obstacle != null && IsSelectableObjectTarget(obstacle.gameObject))
                return obstacle.gameObject;

            Rigidbody rb = collider.attachedRigidbody;
            if (rb == null)
                rb = collider.GetComponentInParent<Rigidbody>();
            if (rb != null)
            {
                GameObject root = ResolveRigRoot(OutermostRigidbody(rb).gameObject);
                return IsSelectableTarget(root) ? root : null;
            }
            return null;
        }

        // Earlier iterations sometimes added a Rigidbody to an INNER node of a GLB
        // hierarchy. Selecting that node instead of the true root gives a completely
        // different local frame (intermittent crooked driving), so every rigidbody
        // resolve climbs to the outermost rigidbody ancestor.
        private static Rigidbody OutermostRigidbody(Rigidbody rb)
        {
            while (rb != null && rb.transform.parent != null)
            {
                Rigidbody parentRb = rb.transform.parent.GetComponentInParent<Rigidbody>();
                if (parentRb == null || ReferenceEquals(parentRb, rb)) break;
                rb = parentRb;
            }
            return rb;
        }

        private static GameObject ResolveVelocityTarget(SEAN.Control.VelocityController velocity)
        {
            if (velocity == null) return null;
            var sean = SEAN.SEAN.instance;
            var robot = sean != null ? sean.robot : null;
            if (robot != null && robot.base_link != null)
                return robot.base_link;
            Rigidbody rb = velocity.GetComponent<Rigidbody>();
            if (rb == null) rb = velocity.GetComponentInChildren<Rigidbody>(true);
            return rb != null ? rb.gameObject : velocity.gameObject;
        }

        private void Rescan()
        {
            var targets = new List<GameObject>();
            foreach (Base a in FindObjectsOfType<Base>())
            {
                if (!IsSelectable(a)) continue;
                // Composite rigs keep the agent on a child (the bike); list the rig root
                // so possession moves the vehicle and its rider together.
                GameObject root = ResolveRigRoot(a.gameObject);
                if (!targets.Contains(root)) targets.Add(root);
            }

            foreach (var velocity in FindObjectsOfType<SEAN.Control.VelocityController>())
            {
                GameObject target = ResolveVelocityTarget(velocity);
                if (IsSelectableTarget(target) && !targets.Contains(target))
                    targets.Add(target);
            }

            foreach (var obstacle in FindObjectsOfType<SEAN.Scenario.Obstacles.TrackedObstacle>())
            {
                if (obstacle != null && IsSelectableTarget(obstacle.gameObject) && !targets.Contains(obstacle.gameObject))
                    targets.Add(obstacle.gameObject);
            }

            // Placed characters (Cyclist / Scooter / Phone User ... from the Add Characters
            // panel) carry a ManualWheelchairController but often no Base agent and no
            // rigidbody, so none of the scans above find them — list them directly instead
            // of requiring a scene-pick first. The participant's own avatar is excluded.
            foreach (var mwc in FindObjectsOfType<IVI.ManualWheelchairController>())
            {
                if (mwc == null) continue;
                GameObject go = mwc.gameObject;
                if (go.GetComponent<Base>() != null) continue;   // already listed as an agent
                if (IsSelectableTarget(go) && !targets.Contains(go))
                    targets.Add(go);
            }

            foreach (var rb in FindObjectsOfType<Rigidbody>())
            {
                if (rb == null) continue;
                // Stray inner rigidbody from an earlier possession attempt: the outer
                // root owns the object — never list the same car twice.
                if (rb.transform.parent != null && rb.transform.parent.GetComponentInParent<Rigidbody>() != null)
                    continue;
                GameObject target = rb.gameObject;
                if (target.GetComponentInParent<Base>() != null ||
                    target.GetComponentInParent<SEAN.Scenario.Robot>() != null ||
                    target.GetComponentInParent<SEAN.Scenario.Obstacles.TrackedObstacle>() != null)
                    continue;
                if (IsSelectableTarget(target) && !targets.Contains(target))
                    targets.Add(target);
            }

            // FindObjectsOfType includes disabled components on active objects, so the
            // possessed agent (Base disabled) stays listed; merge defensively anyway.
            if (possessedObject != null && !targets.Contains(possessedObject))
                targets.Add(possessedObject);
            if (selectedObject != null && IsSelectableTarget(selectedObject) && !targets.Contains(selectedObject))
                targets.Add(selectedObject);
            recentObjects.RemoveAll(o => o == null);
            foreach (GameObject recent in recentObjects)
                if (IsSelectableTarget(recent) && !targets.Contains(recent))
                    targets.Add(recent);

            targets.Sort((x, y) =>
            {
                int c = string.CompareOrdinal(TargetLabel(x), TargetLabel(y));
                return c != 0 ? c : x.GetInstanceID().CompareTo(y.GetInstanceID());
            });

            bool same = targets.Count == rows.Count;
            if (same)
                for (int i = 0; i < targets.Count; i++)
                    if (!ReferenceEquals(targets[i], rows[i].target)) { same = false; break; }
            if (same) return;

            rows.Clear();
            foreach (GameObject target in targets)
            {
                Base agent = target.GetComponent<Base>();
                rows.Add(new Row { agent = agent, target = target, label = TargetLabel(target) });
            }
            RebuildRows();
        }

        private static string TargetLabel(GameObject target)
        {
            if (target == null) return "(gone)";
            Base agent = target.GetComponent<Base>();
            if (agent is IVI.SFPWDAgent) return "[PWD] " + target.name;
            if (agent != null) return "[Ped] " + target.name;
            if (target.GetComponentInParent<SEAN.Scenario.Robot>() != null ||
                target.GetComponent<SEAN.Control.VelocityController>() != null)
                return "[Robot] " + target.name;
            if (IsCharacterRig(target))
                return "[Char] " + target.name;
            return "[Obj] " + target.name;
        }

        // ---- Pick mode (world-building-style free camera + click select) -------

        private void EnterPickMode()
        {
            if (pickModeActive) return;
            Rescan();
            nextRescanTime = Time.unscaledTime + RescanInterval;

            pickCamGo = new GameObject("AgentPossessPickCamera");
            pickCam = pickCamGo.AddComponent<Camera>();
            pickCam.targetDisplay = ResolveTargetDisplay();
            pickCam.depth = 90f;
            pickCam.fieldOfView = 60f;

            Vector3 focus = selectedObject != null ? selectedObject.transform.position : AgentsCentroid();
            pickPitch = 55f;
            pickYaw = 0f;
            pickCamGo.transform.position = focus + new Vector3(0f, flyStartHeight, -flyStartHeight * 0.6f);
            pickCamGo.transform.rotation = Quaternion.Euler(pickPitch, pickYaw, 0f);

            pickModeActive = true;
        }

        private void ExitPickMode()
        {
            if (pickCamGo != null) Destroy(pickCamGo);
            pickCamGo = null;
            pickCam = null;
            pickModeActive = false;
        }

        private Vector3 AgentsCentroid()
        {
            Vector3 sum = Vector3.zero;
            int n = 0;
            foreach (var r in rows)
                if (r.target != null) { sum += r.target.transform.position; n++; }
            return n > 0 ? sum / n : Vector3.zero;
        }

        private void UpdatePickMode()
        {
            if (pickCamGo == null) { pickModeActive = false; return; }

            // Free-look with RMB. WASD pans on the ground plane so moving the pick
            // camera from a steep top-down angle does not accidentally zoom.
            if (Input.GetMouseButton(1))
            {
                pickYaw += Input.GetAxis("Mouse X") * flyLookSensitivity;
                pickPitch = Mathf.Clamp(pickPitch - Input.GetAxis("Mouse Y") * flyLookSensitivity, -85f, 85f);
                pickCamGo.transform.rotation = Quaternion.Euler(pickPitch, pickYaw, 0f);
            }

            Quaternion yawOnly = Quaternion.Euler(0f, pickYaw, 0f);
            Vector3 dir = Vector3.zero;
            if (Input.GetKey(KeyCode.W)) dir += yawOnly * Vector3.forward;
            if (Input.GetKey(KeyCode.S)) dir += yawOnly * Vector3.back;
            if (Input.GetKey(KeyCode.A)) dir += yawOnly * Vector3.left;
            if (Input.GetKey(KeyCode.D)) dir += yawOnly * Vector3.right;
            if (Input.GetKey(KeyCode.E)) dir += Vector3.up;
            if (Input.GetKey(KeyCode.Q)) dir += Vector3.down;
            if (dir.sqrMagnitude > 1f) dir.Normalize();

            bool boost = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            pickCamGo.transform.position += dir * flySpeed * (boost ? flyBoost : 1f) * Time.unscaledDeltaTime;

            float scroll = Input.mouseScrollDelta.y;
            if (Mathf.Abs(scroll) > ScrollDeadzone)
                pickCamGo.transform.position += pickCamGo.transform.forward * scroll * 8f;

            if (Input.GetMouseButtonDown(0) && !IsPointerOverUi())
                TrySelectUnderCursor();
        }

        private bool IsPointerOverUi()
        {
            return EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();
        }

        private void TrySelectUnderCursor()
        {
            if (!TryGetPickMousePosition(out Vector3 mouse)) return;

            GameObject target = null;
            Ray ray = pickCam.ScreenPointToRay(mouse);
            RaycastHit[] hits = Physics.RaycastAll(ray, 500f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            float bestHitDistance = float.PositiveInfinity;
            foreach (RaycastHit hit in hits)
            {
                if (hit.collider == null || hit.distance >= bestHitDistance) continue;
                GameObject hitTarget = ResolvePickTarget(hit.collider);
                if (!IsSelectableTarget(hitTarget)) continue;
                bestHitDistance = hit.distance;
                target = hitTarget;
            }

            if (target == null)
                target = FindRendererBoundsTarget(ray);
            if (target == null)
                target = FindSceneRendererTarget(ray);
            if (target == null)
                target = FindNearestProjectedTarget(mouse);
            if (!IsSelectableTarget(target)) return;

            SelectOrPossess(target);
        }

        private bool TryGetPickMousePosition(out Vector3 mouse)
        {
            mouse = Input.mousePosition;
            // Map the mouse onto the pick camera's display. In the Editor (and on
            // platforms without display mapping) RelativeMouseAt returns zero - use the
            // raw position, matching the single-Game-view workflow there.
            Vector3 rel = Display.RelativeMouseAt(mouse);
            if (rel == Vector3.zero) return true;
            if ((int)rel.z != pickCam.targetDisplay) return false;
            mouse = new Vector3(rel.x, rel.y, 0f);
            return true;
        }

        private GameObject FindRendererBoundsTarget(Ray ray)
        {
            GameObject best = null;
            float bestDistance = float.PositiveInfinity;
            foreach (Row row in rows)
            {
                GameObject target = row.target;
                if (!IsSelectableTarget(target) || !TryGetRendererBounds(target, out Bounds bounds))
                    continue;
                if (!bounds.IntersectRay(ray, out float distance) || distance >= bestDistance)
                    continue;
                bestDistance = distance;
                best = target;
            }
            return best;
        }

        private static bool TryGetRendererBounds(GameObject target, out Bounds bounds)
        {
            bounds = default;
            bool hasBounds = false;
            foreach (Renderer renderer in target.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer == null || !renderer.enabled || renderer is LineRenderer)
                    continue;
                if (!hasBounds)
                {
                    bounds = renderer.bounds;
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }
            }
            return hasBounds;
        }

        // Colliderless props (GLB imports like the parked cars) can't be raycast; walk
        // every non-static renderer's world bounds along the ray and resolve the best
        // hit to its controllable root.
        private static GameObject FindSceneRendererTarget(Ray ray)
        {
            GameObject best = null;
            float bestDistance = float.PositiveInfinity;
            foreach (Renderer renderer in FindObjectsOfType<Renderer>())
            {
                if (renderer == null || !renderer.enabled) continue;
                if (renderer is LineRenderer || renderer is TrailRenderer || renderer is ParticleSystemRenderer) continue;
                if (renderer.gameObject.isStatic) continue;   // static-batched meshes can't move anyway
                Bounds b = renderer.bounds;
                if (Mathf.Max(b.size.x, b.size.y, b.size.z) > MaxGenericObjectPickSize) continue;
                if (!b.IntersectRay(ray, out float distance) || distance >= bestDistance) continue;
                GameObject root = ResolveGenericRoot(renderer.transform);
                if (root == null) continue;
                bestDistance = distance;
                best = root;
            }
            return best;
        }

        // Climb from a picked renderer to the outermost ancestor that is still
        // prop-sized, so clicking a car door selects the whole car but never a scene
        // container. Agent/robot/UI hierarchies are owned by the other resolve paths.
        private static GameObject ResolveGenericRoot(Transform leaf)
        {
            if (leaf == null) return null;
            if (leaf.GetComponentInParent<Base>() != null ||
                leaf.GetComponentInParent<SEAN.Scenario.Robot>() != null ||
                leaf.GetComponentInParent<SEAN.Control.VelocityController>() != null ||
                leaf.GetComponentInParent<Canvas>() != null)
                return null;

            Transform best = leaf;
            for (Transform cur = leaf; cur != null; cur = cur.parent)
            {
                if (!TryGetTargetBounds(cur.gameObject, out Bounds bounds)) break;
                if (Mathf.Max(bounds.size.x, bounds.size.y, bounds.size.z) > MaxGenericObjectPickSize) break;
                best = cur;
            }
            GameObject root = best.gameObject;
            return IsSelectableTarget(root) ? root : null;
        }

        private GameObject FindNearestProjectedTarget(Vector3 mouse)
        {
            GameObject best = null;
            float bestDistSq = PickScreenRadiusPx * PickScreenRadiusPx;
            foreach (Row row in rows)
            {
                GameObject target = row.target;
                if (!IsSelectableTarget(target)) continue;

                Vector3 screen = pickCam.WorldToScreenPoint(target.transform.position + Vector3.up * 1.0f);
                if (screen.z <= 0f) continue;
                float distSq = ((Vector2)screen - (Vector2)mouse).sqrMagnitude;
                if (distSq >= bestDistSq) continue;
                bestDistSq = distSq;
                best = target;
            }
            return best;
        }

        private void SelectOrPossess(GameObject target)
        {
            if (!IsSelectableTarget(target)) return;
            Base agent = target.GetComponent<Base>();
            // Second click on the already-selected agent takes control immediately.
            if (ReferenceEquals(target, selectedObject) && !PossessionActive)
                Possess(target);
            else
            {
                selectedObject = target;
                selectedAgent = agent;
                if (IsGenericObjectTarget(target))
                {
                    PrepareGenericObjectForControl(target);
                    RememberRecentObject(target);
                }
                LogSelectedTargetDebug(target);
                EnsureSelectedAgentView(target);
            }
        }

        private void TrackReviewPoseRestore()
        {
            var srm = SessionReviewManager.Instance;
            bool reviewActive = srm != null && srm.IsReviewModeActive;
            if (!reviewActive)
            {
                if (wasReviewActive)
                    RestoreLivePoses();
                CaptureLivePoses();
            }
            wasReviewActive = reviewActive;
        }

        private void CaptureLivePoses()
        {
            recentObjects.RemoveAll(o => o == null);
            foreach (GameObject go in recentObjects)
                livePoses[go] = new Pose(go.transform.position, go.transform.rotation);
        }

        private void RestoreLivePoses()
        {
            int restored = 0;
            foreach (var kvp in livePoses)
            {
                GameObject go = kvp.Key;
                if (go == null) continue;
                go.transform.SetPositionAndRotation(kvp.Value.position, kvp.Value.rotation);
                Rigidbody rb = go.GetComponent<Rigidbody>();
                if (rb != null)
                {
                    rb.velocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                }
                restored++;
            }
            if (restored > 0)
                Debug.Log($"[AgentPossess] Review ended - restored {restored} controlled object(s) to their live pose.");
        }

        private void RememberRecentObject(GameObject target)
        {
            if (target == null || !IsGenericObjectTarget(target)) return;
            recentObjects.RemoveAll(o => o == null || ReferenceEquals(o, target));
            recentObjects.Insert(0, target);
            if (recentObjects.Count > 8)
                recentObjects.RemoveRange(8, recentObjects.Count - 8);
        }

        private static void LogSelectedTargetDebug(GameObject target)
        {
            if (target == null) return;
            string path = target.name;
            for (Transform t = target.transform.parent; t != null; t = t.parent)
                path = t.name + "/" + path;
            TryGetRendererBounds(target, out Bounds bounds);
            Rigidbody rb = target.GetComponent<Rigidbody>();
            string rbInfo = rb != null
                ? $"kinematic={rb.isKinematic} gravity={rb.useGravity} constraints={rb.constraints}"
                : "none";
            Debug.Log($"[AgentPossess] Selected '{path}' pos={target.transform.position} " +
                      $"rot={target.transform.eulerAngles} scale={target.transform.lossyScale} " +
                      $"boundsCenter={bounds.center} boundsSize={bounds.size} rigidbody={rbInfo}");
        }

        // ---- Selected-agent Display 3 view -------------------------------------

        private void UpdateSelectedAgentView()
        {
            if (selectedObject == null || !IsSelectableTarget(selectedObject))
            {
                DestroySelectedAgentView();
                return;
            }

            EnsureSelectedAgentView(selectedObject);
            if (selectedViewCam != null)
            {
                selectedViewCam.targetDisplay = ResolveSelectedAgentDisplay();
                // Both cameras share Display 2: while scene-picking, the pick camera
                // (depth 90) must be visible, so the follow view (depth 120) yields.
                selectedViewCam.enabled = !pickModeActive;
            }
            if (viewStatusCanvas != null)
            {
                viewStatusCanvas.targetDisplay = ResolveSelectedAgentDisplay();
            }
        }

        private void EnsureSelectedAgentView(GameObject target)
        {
            if (target == null) return;
            if (ReferenceEquals(selectedViewObject, target) && selectedViewCam != null)
                return;

            DestroySelectedAgentView();
            selectedViewObject = target;
            Base agent = target.GetComponent<Base>();

            bool visualDriveFrame = AgentControlTuning.ShouldUseVisualDriveFrame(
                target, AgentControlTuning.FindAnimator(target));
            bool agentFollow = UsesAgentFollowCamera(target) && !visualDriveFrame;
            bool seated = agent is IVI.SFPWDAgent;
            Vector3 thirdPersonOffset = seated ? new Vector3(0f, 1.9f, -1.5f) : new Vector3(0f, 2.2f, -2.2f);
            float lookAtHeight = seated ? 1.0f : 1.5f;

            selectedViewCamGo = new GameObject("AgentPossessThirdPersonCamera");
            if (agentFollow)
            {
                selectedViewCamGo.transform.SetParent(target.transform, false);
                selectedViewCamGo.transform.position = target.transform.position + target.transform.rotation * thirdPersonOffset;
                selectedViewCamGo.transform.LookAt(target.transform.position + Vector3.up * lookAtHeight);
            }

            selectedViewCam = selectedViewCamGo.AddComponent<Camera>();
            selectedViewCam.targetDisplay = ResolveSelectedAgentDisplay();
            selectedViewCam.rect = new Rect(0f, 0f, 1f, 1f);
            selectedViewCam.depth = 120f;
            selectedViewCam.fieldOfView = 60f;
            selectedViewCam.nearClipPlane = 0.1f;
            selectedViewCam.farClipPlane = 200f;
            selectedViewCam.clearFlags = (agentFollow || visualDriveFrame) ? CameraClearFlags.Skybox : CameraClearFlags.SolidColor;
            selectedViewCam.backgroundColor = new Color(0.08f, 0.09f, 0.10f, 1f);
            selectedViewCam.cullingMask = ~0;

            if (agentFollow)
            {
                var smoothing = selectedViewCamGo.AddComponent<IVI.WheelchairCameraSmoothing>();
                smoothing.thirdPersonOffset = thirdPersonOffset;
                smoothing.lookAtHeight = lookAtHeight;
            }
            else
            {
                Bounds bounds;
                if (!TryGetRendererBounds(target, out bounds) && !TryGetTargetBounds(target, out bounds))
                    bounds = new Bounds(target.transform.position + Vector3.up * 0.8f, Vector3.one * 1.6f);
                var follow = selectedViewCamGo.AddComponent<AgentPossessObjectFollowCamera>();
                follow.Configure(target.transform, bounds);
            }

            if (agentFollow && selectedViewCam.GetComponent<ComfortMotionBlur>() == null)
                selectedViewCam.gameObject.AddComponent<ComfortMotionBlur>();

            Debug.Log($"[AgentPossess] Showing selected target '{target.name}' on Display {selectedViewCam.targetDisplay + 1}.");
            EnsureViewStatusCanvas();
        }

        // Big watermark at the bottom of the Display-3 view: makes "selected but NOT
        // driving yet" vs "driving" unmistakable — WASD stays with the participant's
        // avatar/robot until control is actually taken.
        private void EnsureViewStatusCanvas()
        {
            if (viewStatusCanvas != null) return;

            var go = new GameObject("AgentPossessViewStatus", typeof(Canvas), typeof(CanvasScaler));
            go.transform.SetParent(transform, false);
            viewStatusCanvas = go.GetComponent<Canvas>();
            viewStatusCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
            viewStatusCanvas.targetDisplay = ResolveSelectedAgentDisplay();
            viewStatusCanvas.sortingOrder = 600;

            var scaler = go.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            viewStatusText = NewText(go.transform, "Status", 26, TextAnchor.LowerCenter, FontStyle.Bold);
            var rt = viewStatusText.rectTransform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0f);
            rt.pivot = new Vector2(0.5f, 0f);
            rt.anchoredPosition = new Vector2(0f, 28f);
            rt.sizeDelta = new Vector2(1700f, 60f);
        }

        private void UpdateViewStatus()
        {
            if (viewStatusText == null) return;
            if (PossessionActive && possessedObject != null)
            {
                bool driveFrame = possessedController != null && possessedController.UsesDriveFrame;
                viewStatusText.text = driveFrame
                    ? $"DRIVING  {possessedObject.name}   [Up/Down] drive  [Left/Right] steer  [F] flip  [R] rotate  [Esc] release"
                    : $"DRIVING  {possessedObject.name}   [Up/Down] walk  [Left/Right] turn  [Shift] run  [Esc] release";
                viewStatusText.color = PossessedColor;
            }
            else if (selectedObject != null)
            {
                viewStatusText.text = $"SELECTED  {selectedObject.name}   NOT driving yet - press Take Control (or click it again)";
                viewStatusText.color = SelectedColor;
            }
            else
            {
                viewStatusText.text = "";
            }
        }

        // Humanoid targets get the same over-the-shoulder rig the PWD player uses.
        // Placed characters (ManualWheelchairController, no Base) belong here too —
        // otherwise they fall through to the prop chase camera, which frames the mesh
        // bounds along the mesh long axis and ends up beside the character.
        private static bool UsesAgentFollowCamera(GameObject target)
        {
            return target != null &&
                   (IsCharacterRig(target) ||
                    target.GetComponentInParent<SEAN.Scenario.Robot>() != null ||
                    target.GetComponent<SEAN.Control.VelocityController>() != null);
        }

        private void DestroySelectedAgentView()
        {
            selectedViewObject = null;
            selectedViewCam = null;
            if (selectedViewCamGo != null)
                Destroy(selectedViewCamGo);
            selectedViewCamGo = null;
            if (viewStatusCanvas != null)
                Destroy(viewStatusCanvas.gameObject);
            viewStatusCanvas = null;
            viewStatusText = null;
        }

        // ---- Possession --------------------------------------------------------

        private void Possess(GameObject target)
        {
            if (!IsSelectableTarget(target)) return;
            GUIUtility.keyboardControl = 0;
            Release();
            if (pickModeActive) ExitPickMode();

            // Composite rigs (and anything spawned before the agent moved to the rig root)
            // keep the agent on a child; it must be silenced too or it keeps driving that
            // child while we move the root.
            Base agent = target.GetComponent<Base>();
            if (agent == null) agent = target.GetComponentInChildren<Base>(true);
            bool genericObject = IsGenericObjectTarget(target);
            if (genericObject)
            {
                PrepareGenericObjectForControl(target);
                RememberRecentObject(target);
            }
            if (NeedsControlledObjectTracking(target))
            {
                // Register with the session tracker so the driven motion is recorded
                // into the trial trajectory log and the target moves in review replay
                // like any agent (robot/Base pedestrians are already tracked).
                var tracker = FindObjectOfType<SessionTracker>();
                if (tracker != null) tracker.RegisterControlledObject(target);
            }
            possessed = agent;
            possessedObject = target;
            selectedObject = target;
            selectedAgent = agent;
            EnsureSelectedAgentView(target);

            possessedMwc = target.GetComponent<IVI.ManualWheelchairController>();
            mwcWasEnabled = possessedMwc != null && possessedMwc.enabled;
            if (possessedMwc != null) possessedMwc.enabled = false;

            possessedVelocity = FindVelocityControllerForTarget(target);
            velocityWasEnabled = possessedVelocity != null && possessedVelocity.enabled;
            if (possessedVelocity != null) possessedVelocity.enabled = false;

            // Same lookup as Base.Start: the Animator sits on the root, or one level
            // down for avatar prefabs that keep the rig in a nested model instance.
            possessedAnimator = target.GetComponent<Animator>();
            if (possessedAnimator == null)
                possessedAnimator = target.GetComponentInChildren<Animator>(true);
            prevRootMotion = possessedAnimator != null && possessedAnimator.applyRootMotion;
            if (possessedAnimator != null) possessedAnimator.applyRootMotion = false;

            Rigidbody rb = target.GetComponent<Rigidbody>();
            if (rb == null)
            {
                rb = target.AddComponent<Rigidbody>();
                rb.mass = 80f;
                rb.constraints = RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ;
                rb.isKinematic = true;
                rb.useGravity = false;
                possessedAddedRigidbody = rb;
            }
            if (rb != null) rb.velocity = Vector3.zero;

            // Stops Base.Update (velocity + rotation + animator params). The INavigable
            // replanning coroutine keeps running on purpose: the path stays fresh so the
            // agent resumes cleanly on release.
            if (agent != null)
                agent.enabled = false;

            possessedController = target.AddComponent<PossessedAgentController>();
            possessedDefaultSpeed = DefaultSpeedForTarget(target);
            possessedController.MaxSpeed = possessedDefaultSpeed;
            possessedController.Configure(possessedAnimator, ResolveSelectedAgentDisplay(), false, genericObject);

            Debug.Log($"[AgentPossess] Took control of '{target.name}'. Arrow keys drive (Up/Down + Left/Right), Shift fast, Esc release.");
        }

        // A character rig — agent, player-style controller, or simply anything built on a
        // humanoid avatar. The humanoid test is what catches statically placed Add-Characters
        // props, whose driving components are stripped at spawn: without it they look like
        // plain props to the panel.
        private static bool IsCharacterRig(GameObject target)
        {
            if (target == null) return false;
            if (target.GetComponent<Base>() != null) return true;
            if (target.GetComponent<IVI.ManualWheelchairController>() != null) return true;
            foreach (Animator animator in target.GetComponentsInChildren<Animator>(true))
            {
                if (animator != null && animator.avatar != null && animator.avatar.isHuman)
                    return true;
            }
            return false;
        }

        // "Generic" means a prop (car, crate): driven kinematically in a mesh-axis drive
        // frame and auto-leveled. A character must NOT take that path: a human's mesh long
        // axis is its shoulder width, so the drive frame would walk it sideways and put the
        // follow camera on its flank, and LevelBody would rotate the body outright.
        private static bool IsGenericObjectTarget(GameObject target)
        {
            if (target == null) return false;
            return !IsCharacterRig(target) &&
                   target.GetComponentInParent<SEAN.Scenario.Robot>() == null &&
                   target.GetComponent<SEAN.Control.VelocityController>() == null;
        }

        // Targets the session tracker doesn't already record (it covers the robot, the
        // PWD player and Base pedestrians). Placed characters and props both need
        // explicit registration so possession shows up in review playback.
        private static bool NeedsControlledObjectTracking(GameObject target)
        {
            // A rig whose agent sits on a child is tracked by SessionTracker under that
            // child's transform, so the root we actually move still needs registering.
            return target != null &&
                   target.GetComponent<Base>() == null &&
                   target.GetComponentInParent<SEAN.Scenario.Robot>() == null &&
                   target.GetComponent<SEAN.Control.VelocityController>() == null;
        }

        private static void PrepareGenericObjectForControl(GameObject target)
        {
            if (!IsGenericObjectTarget(target)) return;

            SetStaticRecursively(target, false);

            Rigidbody rb = target.GetComponent<Rigidbody>();
            if (rb == null)
                rb = target.AddComponent<Rigidbody>();

            rb.velocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
            rb.isKinematic = true;
            rb.useGravity = false;
            rb.constraints = (rb.constraints & ~(RigidbodyConstraints.FreezePositionX |
                                                 RigidbodyConstraints.FreezePositionY |
                                                 RigidbodyConstraints.FreezePositionZ |
                                                 RigidbodyConstraints.FreezeRotationY)) |
                             RigidbodyConstraints.FreezeRotationX |
                             RigidbodyConstraints.FreezeRotationZ;
        }

        private static void SetStaticRecursively(GameObject root, bool isStatic)
        {
            if (root == null) return;
            foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
                child.gameObject.isStatic = isStatic;
        }

        private static SEAN.Control.VelocityController FindVelocityControllerForTarget(GameObject target)
        {
            if (target == null) return null;
            var direct = target.GetComponent<SEAN.Control.VelocityController>();
            if (direct != null) return direct;
            direct = target.GetComponentInParent<SEAN.Control.VelocityController>();
            if (direct != null) return direct;
            direct = target.GetComponentInChildren<SEAN.Control.VelocityController>(true);
            if (direct != null) return direct;

            var sean = SEAN.SEAN.instance;
            var robot = sean != null ? sean.robot : null;
            if (robot != null && robot.base_link != null &&
                (ReferenceEquals(target, robot.base_link) || target.transform.IsChildOf(robot.base_link.transform)))
                return FindObjectOfType<SEAN.Control.VelocityController>();

            return null;
        }

        private static float DefaultSpeedForTarget(GameObject target)
        {
            if (target == null) return 1.4f;
            if (target.GetComponentInParent<SEAN.Scenario.Robot>() != null ||
                FindVelocityControllerForTarget(target) != null)
                return 1.0f;
            // Riding rigs (bike/scooter) cruise faster than a walking pace.
            if (AgentControlTuning.ShouldUseVisualDriveFrame(target, AgentControlTuning.FindAnimator(target)))
                return 3.0f;
            if (target.GetComponent<Base>() is IVI.SFPWDAgent)
                return 1.1f;
            if (target.GetComponent<Base>() != null)
                return 1.4f;
            // Placed walking character (Phone User, Dog Walker, ...): walking pace, not
            // the prop default.
            if (IsCharacterRig(target))
                return 1.4f;
            return 2.0f;
        }

        private void Release()
        {
            if (possessedController != null) Destroy(possessedController);

            if (possessedObject != null)
            {
                if (possessedAnimator != null)
                {
                    possessedAnimator.applyRootMotion = prevRootMotion;
                }

                Rigidbody rb = possessedObject.GetComponent<Rigidbody>();
                if (rb != null) rb.velocity = Vector3.zero;

                if (possessed != null)
                    possessed.enabled = true;
                // StartCoroutine throws on an inactive GameObject (e.g. an agent a scenario
                // restore deactivated mid-possession); it restarts on its own re-activation.
                if (possessed is IVI.SFPWDAgent sfpwd && sfpwd.gameObject.activeInHierarchy)
                    sfpwd.RestartNavigationCoroutine();
                if (possessedMwc != null && mwcWasEnabled)
                    possessedMwc.enabled = true;
                if (possessedVelocity != null && velocityWasEnabled)
                    possessedVelocity.enabled = true;
                if (possessedAddedRigidbody != null)
                    Destroy(possessedAddedRigidbody);

                Debug.Log($"[AgentPossess] Released '{possessedObject.name}' back to its own control.");
            }

            ClearPossessionRefs();
        }

        private void ClearPossessionRefs()
        {
            possessed = null;
            possessedObject = null;
            possessedController = null;
            possessedMwc = null;
            possessedVelocity = null;
            possessedAnimator = null;
            mwcWasEnabled = false;
            velocityWasEnabled = false;
            possessedDefaultSpeed = 0f;
            possessedAddedRigidbody = null;
        }

        private void AdjustControlSpeed(float delta)
        {
            if (possessedController == null) return;
            SetControlSpeed(possessedController.MaxSpeed + delta);
        }

        private void ResetControlSpeed()
        {
            if (possessedController == null) return;
            float reset = possessedDefaultSpeed > 0f ? possessedDefaultSpeed : possessedController.DefaultMaxSpeed;
            SetControlSpeed(reset);
        }

        private void SetControlSpeed(float speed)
        {
            if (possessedController == null) return;
            possessedController.MaxSpeed = Mathf.Clamp(speed, MinControlSpeed, MaxControlSpeed);
        }

        private bool ShouldHide()
        {
            var srm = SessionReviewManager.Instance;
            return srm != null && (srm.IsReviewModeActive
                                   || srm.IsWorldBuildingModeActive
                                   || srm.IsOnboardingActive);
        }

        private void ApplyDisplayToCameras()
        {
            int display = ResolveTargetDisplay();
            if (pickCam != null) pickCam.targetDisplay = display;
            if (selectedViewCam != null) selectedViewCam.targetDisplay = ResolveSelectedAgentDisplay();
            // The possession camera lives on its own GameObject owned by the controller;
            // cheapest correct move is to re-create it on the new display.
            if (PossessionActive)
            {
                GameObject target = possessedObject;
                Release();
                Possess(target);
            }
        }

        // ---- UI ----------------------------------------------------------------

        private void EnsureUi()
        {
            EnsureEventSystem();
            ActivateConnectedDisplays();
            if (canvas != null) return;
            uiFont = LoadUiFont();

            var canvasGo = new GameObject("AgentPossessCanvas",
                typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGo.transform.SetParent(transform, false);

            canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.targetDisplay = ResolveTargetDisplay();
            canvas.sortingOrder = 510;   // above the Agent Speed panel, never overlaps anyway

            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            Debug.Log($"[AgentPossessOverlay] Agent Control panel on Display {canvas.targetDisplay + 1}. " +
                      $"[{toggleKey}] shows/hides it, Ctrl+[{toggleKey}] moves it between displays. " +
                      "Pick an agent (list row or Pick In Scene), Take Control drives it first-person; " +
                      "Esc releases. In the Editor use a Game view set to \"Display 2\".");

            // Panel docked to the bottom-LEFT corner (the Agent Speed panel owns bottom-right).
            panel = NewRect("Panel", canvasGo.transform);
            panel.anchorMin = panel.anchorMax = new Vector2(0f, 0f);
            panel.pivot = new Vector2(0f, 0f);
            panel.anchoredPosition = new Vector2(Margin, Margin);
            panel.sizeDelta = new Vector2(PanelW, Pad * 2f + HeaderH + HintH + BtnH + StatusH + SpeedH + 14f);
            var panelImg = panel.gameObject.AddComponent<Image>();
            panelImg.color = new Color(0f, 0f, 0f, 0.8f);

            titleText = NewText(panel, "Title", 22, TextAnchor.MiddleLeft, FontStyle.Bold);
            PlaceTopLeft(titleText.rectTransform, Pad, Pad, PanelW - 2f * Pad, HeaderH);
            titleText.text = "Agent Control";

            hintText = NewText(panel, "Hint", 13, TextAnchor.UpperLeft, FontStyle.Normal);
            PlaceTopLeft(hintText.rectTransform, Pad, Pad + HeaderH, PanelW - 2f * Pad, HintH);
            hintText.color = new Color(1f, 1f, 1f, 0.75f);

            float yBtns = Pad + HeaderH + HintH + 4f;
            pickButton = NewButton(panel, "Pick In Scene");
            PlaceTopLeft((RectTransform)pickButton.transform, Pad, yBtns, 150f, BtnH);
            pickButtonText = pickButton.GetComponentInChildren<Text>();
            pickButton.onClick.AddListener(() =>
            {
                if (pickModeActive) ExitPickMode();
                else EnterPickMode();
            });

            controlButton = NewButton(panel, "Take Control");
            PlaceTopLeft((RectTransform)controlButton.transform, Pad + 150f + Gap, yBtns, 152f, BtnH);
            controlButton.onClick.AddListener(() =>
            {
                if (selectedObject != null && !PossessionActive) Possess(selectedObject);
            });

            releaseButton = NewButton(panel, "Release");
            PlaceTopLeft((RectTransform)releaseButton.transform, Pad + 150f + Gap + 152f + Gap, yBtns, 120f, BtnH);
            releaseButton.onClick.AddListener(Release);

            statusText = NewText(panel, "Status", 16, TextAnchor.MiddleLeft, FontStyle.Bold);
            PlaceTopLeft(statusText.rectTransform, Pad, yBtns + BtnH + 6f, PanelW - 2f * Pad, StatusH);

            float ySpeed = yBtns + BtnH + 6f + StatusH + 4f;
            speedText = NewText(panel, "ControlSpeed", 14, TextAnchor.MiddleLeft, FontStyle.Bold);
            PlaceTopLeft(speedText.rectTransform, Pad, ySpeed, 180f, SpeedH);
            speedDownButton = NewButton(panel, "-");
            PlaceTopLeft((RectTransform)speedDownButton.transform, Pad + 184f, ySpeed, 42f, SpeedH);
            speedDownButton.onClick.AddListener(() => AdjustControlSpeed(-SpeedStep));
            speedResetButton = NewButton(panel, "Reset", 13);
            PlaceTopLeft((RectTransform)speedResetButton.transform, Pad + 184f + 42f + Gap, ySpeed, 84f, SpeedH);
            speedResetButton.onClick.AddListener(ResetControlSpeed);
            speedUpButton = NewButton(panel, "+");
            PlaceTopLeft((RectTransform)speedUpButton.transform, Pad + 184f + 42f + Gap + 84f + Gap, ySpeed, 42f, SpeedH);
            speedUpButton.onClick.AddListener(() => AdjustControlSpeed(SpeedStep));

            // Floating "name > tag over the selected agent while scene-picking; lives on
            // the canvas root and is positioned by projecting through the pick camera.
            markerText = NewText(canvasGo.transform, "PickMarker", 20, TextAnchor.LowerCenter, FontStyle.Bold);
            var markerRt = markerText.rectTransform;
            markerRt.anchorMin = markerRt.anchorMax = new Vector2(0f, 0f);
            markerRt.pivot = new Vector2(0.5f, 0f);
            markerRt.sizeDelta = new Vector2(400f, 56f);
            markerText.color = new Color(1f, 0.95f, 0.4f);
            markerText.gameObject.SetActive(false);
        }

        private void RebuildRows()
        {
            foreach (var go in rowObjects)
                if (go != null) Destroy(go);
            rowObjects.Clear();

            float yRows = Pad + HeaderH + HintH + 4f + BtnH + 6f + StatusH + 4f + SpeedH + 6f;
            panel.sizeDelta = new Vector2(PanelW, yRows + rows.Count * RowH + Pad);

            for (int i = 0; i < rows.Count; i++)
            {
                Row row = rows[i];
                Button b = NewButton(panel, row.label, 15);
                var rt = (RectTransform)b.transform;
                PlaceTopLeft(rt, Pad, yRows + i * RowH, PanelW - 2f * Pad, RowH - 4f);
                var img = b.GetComponent<Image>();
                img.color = new Color(1f, 1f, 1f, 0.07f);
                row.button = b;
                row.text = b.GetComponentInChildren<Text>();
                row.text.alignment = TextAnchor.MiddleLeft;
                var textRt = row.text.rectTransform;
                textRt.offsetMin = new Vector2(10f, 0f);

                Row captured = row;
                b.onClick.AddListener(() =>
                {
                    if (captured.target == null) return;
                    // Second click on the selected row takes control, like the scene pick.
                    if (ReferenceEquals(captured.target, selectedObject) && !PossessionActive)
                        Possess(captured.target);
                    else
                        SelectOrPossess(captured.target);
                });
                rowObjects.Add(b.gameObject);
            }
        }

        private void RefreshUi()
        {
            bool show = visible && !ShouldHide();
            if (canvas.gameObject.activeSelf != show)
                canvas.gameObject.SetActive(show);
            UpdateViewStatus();   // the Display-3 watermark follows the view, not the panel
            if (!show) return;

            if (selectedObject != null && !IsSelectableTarget(selectedObject))
            {
                selectedObject = null;
                selectedAgent = null;
                DestroySelectedAgentView();
            }

            titleText.text = $"Agent Control   [{KeyLabel(toggleKey)}] hide | [Ctrl+{KeyLabel(toggleKey)}] display";

            // The Logitech flight stick doubles the arrows when it is free (participant
            // on the gamepad); only advertise it when that is actually the case.
            string stick = SEAN.Input.JoystickProfiles.ResearcherStickAvailable ? " or stick" : "";
            if (PossessionActive)
                hintText.text = possessedController != null && possessedController.UsesDriveFrame
                    ? $"DRIVING: [Up/Down]+[Left/Right]{stick} | [Shift] fast | [F] flip / [R] rotate 90\nview on this display | [RMB] orbit | [wheel] zoom | [Esc] release"
                    : $"DRIVING: [Up/Down] walk | [Left/Right] turn{stick} | [Shift] run\nview on this display | [Esc] or Release to let go";
            else if (pickModeActive)
                hintText.text = "PICKING: [WASD] pan | [Q/E] up/down | [RMB] look | [wheel] zoom\nclick an agent to select - click it again to take control | [Esc] exit";
            else
                hintText.text = $"Click a row to select; the view opens on this display.\nClick again or Take Control to drive it (arrow keys{stick}).";

            if (PossessionActive && possessedObject != null)
            {
                statusText.text = $"Controlling: {TargetLabel(possessedObject)}   ({possessedController.CurrentSpeed:F2} m/s)";
                statusText.color = PossessedColor;
            }
            else if (selectedObject != null)
            {
                statusText.text = $"Selected: {TargetLabel(selectedObject)}";
                statusText.color = SelectedColor;
            }
            else
            {
                statusText.text = rows.Count > 0 ? "Selected: -" : "No controllable agents in the scene.";
                statusText.color = Color.white;
            }

            if (pickButtonText != null)
                pickButtonText.text = pickModeActive ? "Exit Pick" : "Pick In Scene";
            controlButton.interactable = selectedObject != null && !PossessionActive;
            releaseButton.interactable = PossessionActive;
            RefreshSpeedControls();

            foreach (var row in rows)
            {
                if (row.text == null) continue;
                if (row.target == null)
                {
                    row.text.text = "  (gone)";
                    row.text.color = Color.gray;
                    continue;
                }
                bool isPossessed = ReferenceEquals(row.target, possessedObject);
                bool isSelected = ReferenceEquals(row.target, selectedObject);
                row.text.text = (isPossessed ? "[*] " : isSelected ? "[>] " : "    ") + row.label
                                + (isPossessed ? "  - controlling" : "");
                row.text.color = isPossessed ? PossessedColor : isSelected ? SelectedColor : Color.white;
            }

            UpdatePickMarker();
        }

        private void RefreshSpeedControls()
        {
            bool speedActive = PossessionActive && possessedController != null;
            if (speedText != null)
            {
                speedText.gameObject.SetActive(speedActive);
                if (speedActive)
                    speedText.text = $"Speed: {possessedController.MaxSpeed:F1} m/s";
            }
            if (speedDownButton != null)
            {
                speedDownButton.gameObject.SetActive(speedActive);
                speedDownButton.interactable = speedActive &&
                    possessedController.MaxSpeed > MinControlSpeed + 0.01f;
            }
            if (speedResetButton != null)
            {
                speedResetButton.gameObject.SetActive(speedActive);
                speedResetButton.interactable = speedActive;
            }
            if (speedUpButton != null)
            {
                speedUpButton.gameObject.SetActive(speedActive);
                speedUpButton.interactable = speedActive &&
                    possessedController.MaxSpeed < MaxControlSpeed - 0.01f;
            }
        }

        private void UpdatePickMarker()
        {
            bool show = pickModeActive && pickCam != null && selectedObject != null;
            if (markerText.gameObject.activeSelf != show)
                markerText.gameObject.SetActive(show);
            if (!show) return;

            Vector3 world = selectedObject.transform.position + Vector3.up * 2.0f;
            Vector3 sp = pickCam.WorldToScreenPoint(world);
            if (sp.z <= 0f)
            {
                markerText.gameObject.SetActive(false);
                return;
            }
            float scale = Mathf.Max(canvas.scaleFactor, 0.0001f);
            markerText.rectTransform.anchoredPosition = new Vector2(sp.x / scale, sp.y / scale);
            markerText.text = $"{selectedObject.name}\n[>]";
        }

        private static void ActivateConnectedDisplays()
        {
#if !UNITY_EDITOR
            for (int i = 1; i < Display.displays.Length; i++)
                Display.displays[i].Activate();
#endif
        }

        private int ResolveTargetDisplay()
        {
#if UNITY_EDITOR
            return targetDisplay;
#else
            if (targetDisplay > 0 && Display.displays.Length <= targetDisplay)
                return 0;
            return targetDisplay;
#endif
        }

        private int ResolveSelectedAgentDisplay()
        {
#if UNITY_EDITOR
            return selectedAgentDisplay;
#else
            if (selectedAgentDisplay > 0 && Display.displays.Length <= selectedAgentDisplay)
                return ResolveTargetDisplay();
            return selectedAgentDisplay;
#endif
        }

        private static string KeyLabel(KeyCode k)
        {
            switch (k)
            {
                case KeyCode.BackQuote: return "`";
                case KeyCode.Comma: return ",";
                case KeyCode.Period: return ".";
                case KeyCode.Backslash: return "\\";
                default: return k.ToString();
            }
        }

        // ---- uGUI construction helpers (same idiom as AgentSpeedOverlay) -------

        private static void EnsureEventSystem()
        {
            const string overlayEventSystemName = "SessionReviewOverlayEventSystem";
            EventSystem[] systems = FindObjectsOfType<EventSystem>();
            EventSystem sceneSystem = null;
            EventSystem fallback = null;

            foreach (EventSystem system in systems)
            {
                if (system == null) continue;
                if (fallback == null) fallback = system;
                if (system.gameObject.scene.name != "DontDestroyOnLoad")
                {
                    sceneSystem = system;
                    break;
                }
            }

            if (sceneSystem != null)
            {
                foreach (EventSystem system in systems)
                {
                    if (system == null || ReferenceEquals(system, sceneSystem)) continue;
                    bool oldOverlaySystem = system.gameObject.scene.name == "DontDestroyOnLoad" &&
                                            (system.gameObject.name == overlayEventSystemName ||
                                             system.gameObject.name == "EventSystem") &&
                                            system.GetComponent<StandaloneInputModule>() != null;
                    if (oldOverlaySystem)
                        Destroy(system.gameObject);
                }
                return;
            }

            if (fallback != null) return;

            var es = new GameObject(overlayEventSystemName, typeof(EventSystem), typeof(StandaloneInputModule));
            DontDestroyOnLoad(es);
        }

        private static Font LoadUiFont()
        {
            Font f = Resources.GetBuiltinResource<Font>("Arial.ttf");
            if (f == null) f = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (f == null) f = Font.CreateDynamicFontFromOSFont("Arial", 14);
            return f;
        }

        private static RectTransform NewRect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        private static void PlaceTopLeft(RectTransform rt, float x, float y, float w, float h)
        {
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(x, -y);
            rt.sizeDelta = new Vector2(w, h);
        }

        private Text NewText(Transform parent, string name, int fontSize, TextAnchor anchor, FontStyle style)
        {
            var rt = NewRect(name, parent);
            var t = rt.gameObject.AddComponent<Text>();
            t.font = uiFont;
            t.fontSize = fontSize;
            t.fontStyle = style;
            t.alignment = anchor;
            t.color = Color.white;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            t.raycastTarget = false;
            return t;
        }

        private Button NewButton(Transform parent, string label, int fontSize = 16)
        {
            var rt = NewRect("Button", parent);
            var img = rt.gameObject.AddComponent<Image>();
            img.color = new Color(0.22f, 0.24f, 0.30f, 1f);
            var b = rt.gameObject.AddComponent<Button>();
            b.targetGraphic = img;
            var t = NewText(rt, "Label", fontSize, TextAnchor.MiddleCenter, FontStyle.Bold);
            t.text = label;
            var textRt = t.rectTransform;
            textRt.anchorMin = Vector2.zero;
            textRt.anchorMax = Vector2.one;
            textRt.offsetMin = Vector2.zero;
            textRt.offsetMax = Vector2.zero;
            return b;
        }
    }
}
