using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using TMPro;

namespace SessionReview
{
    /// <summary>
    /// Floats a label in the air above a goal marker that always turns to face the camera, with
    /// a dashed line dropping from it to the marker so it is obvious which spot it names. The
    /// label, its backing plate and the connector all render with ZTest Always (overlay queue),
    /// so a building between the camera and the goal never hides where the goal is.
    ///
    /// The pedestrian marker prefab (Resources/PedTrajctory/end) carries its own TextMeshPro
    /// child with a rotation baked into the prefab, so from half the approach angles the
    /// participant reads it mirrored, and at eye level it is small and easy to miss. This hides
    /// that label and owns a billboarded one instead. The other goal markers -- the practice
    /// flow's, and the player/robot markers the scenario scenes build at runtime -- have no
    /// label at all and get one from here too.
    ///
    /// The robot-goal label (<see cref="followsRobotGoalVisibility"/>) is NOT shown on its own
    /// terms: its renderers are handed to <see cref="RosOverlayVisibility"/>, which shows the
    /// robot goal only while a human drives the robot or the "Robot Goal" switch is on. That
    /// keeps the label from giving the robot's destination away to a participant. The player
    /// goal is always shown, exactly like the marker it labels.
    ///
    /// Nothing has to be wired in the scene -- <see cref="GoalBeaconBootstrap"/> polls for goal
    /// markers. To pin down your own size/height/color, add the component to a marker in the
    /// editor and configure it: markers that already carry a GoalBeacon are left alone.
    /// </summary>
    public class GoalBeacon : MonoBehaviour
    {
        public enum ConnectorStyle { None, Solid, Dashed }

        /// <summary>Whose goal this label names -- only the driven agent's goal is shown.</summary>
        public enum Audience { Pedestrian, Robot }

        private enum DrivenAgent { Pedestrian, Robot }

        /// <summary>Marker name the scenario scenes and RandomAvatar.goalObjectName use.</summary>
        private const string DefaultGoalName = "end";

        /// <summary>How often goal markers are re-discovered; matches RosOverlayVisibility.</summary>
        internal const float DiscoverInterval = 0.5f;

        /// <summary>ZTest-Always shader (in a Resources folder so builds keep it) for the
        /// backing plate and connector; the text uses TMP's own Overlay shader variant.</summary>
        private const string OverlayShaderName = "SessionReview/GoalBeaconOverlay";

        // Overlay-range render queues. Everything here draws with ZTest Always, so queue order
        // is the only thing keeping the text on top of its own plate and connector.
        private const int ConnectorQueue = 3997;
        private const int BackingQueue = 3998;
        private const int TextQueue = 4000;

        // The practice flow calls these the WHITE and the YELLOW goal, so the labels match.
        private static readonly Color PedestrianColor = Color.white;
        private static readonly Color RobotColor = new Color(1f, 0.85f, 0.2f);

        private static readonly List<GoalBeacon> live = new List<GoalBeacon>();

        public string label = "Pedestrian Goal";
        public Color color = PedestrianColor;
        [Tooltip("Whose goal this is. Only the currently-driven agent's goal is shown: driving " +
                 "the robot hides the pedestrian goal, and walking as the pedestrian hides the robot's.")]
        public Audience audience = Audience.Pedestrian;
        [Tooltip("Height (m) above the ground the floating label sits at.")]
        public float labelHeight = 2.0f;
        public float labelFontSize = 3.5f;
        [Tooltip("Beyond this distance (m) the label grows so it stays legible from far away.")]
        public float labelReferenceDistance = 12f;
        public float maxLabelScale = 2f;
        [Tooltip("Line dropped from the label down to the marker it names.")]
        public ConnectorStyle connectorStyle = ConnectorStyle.Dashed;
        [Tooltip("Dashes per metre of connector line.")]
        public float dashesPerMeter = 3f;
        [Tooltip("Hide the marker prefab's own TextMeshPro child (the one that reads mirrored).")]
        public bool hideMarkerLabel = true;
        [Tooltip("Let RosOverlayVisibility own whether this label is drawn -- the robot goal is " +
                 "hidden from participants except while a human drives the robot.")]
        public bool followsRobotGoalVisibility;

        private GameObject root;
        private Transform labelRoot;
        private LineRenderer connector;
        private float labelHalfHeight;
        private Renderer[] labelRenderers = new Renderer[0];
        private Camera cachedCamera;
        private Vector3 lastMarkerPosition;

        private static Texture2D dashTexture;
        private static TestSceneFlowManager cachedFlow;

        /// <summary>
        /// Renderers of every beacon label, so ROI export can hide them like it hides the goal
        /// markers themselves.
        /// </summary>
        public static Renderer[] AllUiRenderers => Collect(false);

        /// <summary>
        /// Renderers of the robot-goal labels, for RosOverlayVisibility to switch. They live
        /// outside the marker's hierarchy (its 0.3 scale would distort them), so walking the
        /// marker's children does not find them.
        /// </summary>
        public static Renderer[] RobotGoalUiRenderers => Collect(true);

        private static Renderer[] Collect(bool robotGoalOnly)
        {
            var result = new List<Renderer>();
            foreach (GoalBeacon beacon in live)
            {
                if (beacon == null || (robotGoalOnly && !beacon.followsRobotGoalVisibility))
                    continue;
                foreach (Renderer renderer in beacon.labelRenderers)
                {
                    if (renderer != null) result.Add(renderer);
                }
            }
            return result.ToArray();
        }

        void Awake()
        {
            live.Add(this);
        }

        void Start()
        {
            lastMarkerPosition = transform.position;
            BuildLabel();
            if (hideMarkerLabel) HideMarkerLabel();
        }

        // The label is a root object, so it would keep hovering over a marker that was switched
        // off (the task deactivates the robot goal on player-controlled trials).
        void OnEnable()
        {
            if (root != null) root.SetActive(true);
        }

        void OnDisable()
        {
            if (root != null) root.SetActive(false);
        }

        void OnDestroy()
        {
            live.Remove(this);
            if (root != null) Destroy(root);
        }

        void LateUpdate()
        {
            if (root == null) return;

            // Only the driven agent's goal is shown. Three cases decide whether this label is up:
            //   - when an object is bound as the robot goal, that object carries its OWN beacon
            //     (RobotGoalObjectBinding attaches one), so the goal MARKER's beacon must step
            //     aside -- its ground ray would stop on the object and misplace the label;
            //   - a robot goal wired to RosOverlayVisibility (scenario scenes) leaves its
            //     renderers to that switch, which already shows it only while driving the robot;
            //   - everything else self-manages: the pedestrian goal hides while the robot is
            //     driven, the (practice) robot goal hides while the pedestrian is.
            bool isMarkerSupersededByBinding = followsRobotGoalVisibility
                && RobotGoalObjectBinding.BoundObject != null
                && RobotGoalObjectBinding.BoundObject != gameObject;
            bool wantActive;
            if (isMarkerSupersededByBinding)
                wantActive = false;
            else if (followsRobotGoalVisibility)
                wantActive = true;
            else
                wantActive = ShouldShowForDrivenAgent();

            if (root.activeSelf != wantActive) root.SetActive(wantActive);
            if (!wantActive) return;

            // World Building can move scene objects, and RobotGoalObjectBinding drives the robot
            // marker every frame, so keep the label over the marker.
            if (transform.position != lastMarkerPosition)
            {
                lastMarkerPosition = transform.position;
                root.transform.position = ResolveGroundPosition();
            }

            Camera cam = ResolveCamera();
            if (cam == null) return;

            // Face the camera but stay upright: the label is never mirrored, tilted or upside
            // down, however the participant approaches it.
            Vector3 away = labelRoot.position - cam.transform.position;
            away.y = 0f;
            if (away.sqrMagnitude > 0.0001f)
                labelRoot.rotation = Quaternion.LookRotation(away, Vector3.up);

            float distance = Vector3.Distance(cam.transform.position, labelRoot.position);
            float scale = Mathf.Clamp(distance / Mathf.Max(labelReferenceDistance, 0.01f), 1f, maxLabelScale);
            labelRoot.localScale = Vector3.one * scale;

            if (connector != null)
            {
                // The label grows with distance, so the line has to stop lower and thicken with
                // it, otherwise it either pokes through the text or thins away to nothing.
                connector.widthMultiplier = 0.035f * scale;
                connector.SetPosition(1, new Vector3(0f, labelHeight - labelHalfHeight * scale, 0f));
            }
        }

        #region Build

        private void BuildLabel()
        {
            // Kept out of the marker's hierarchy on purpose: the pedestrian marker prefab is
            // scaled to 0.3 and carries a baked child rotation, both of which would distort the
            // label if it were parented under it.
            root = new GameObject("GoalBeacon (" + label + ")");
            root.transform.position = ResolveGroundPosition();

            var labelObj = new GameObject("Label");
            labelRoot = labelObj.transform;
            labelRoot.SetParent(root.transform, false);
            labelRoot.localPosition = new Vector3(0f, labelHeight, 0f);

            var textObj = new GameObject("Text");
            textObj.transform.SetParent(labelRoot, false);
            var text = textObj.AddComponent<TextMeshPro>();
            text.text = label;
            text.fontSize = labelFontSize;
            text.color = color;
            text.alignment = TextAlignmentOptions.Center;
            text.enableWordWrapping = false;
            text.rectTransform.sizeDelta = new Vector2(16f, 4f);
            var textRenderer = text.GetComponent<Renderer>();
            textRenderer.shadowCastingMode = ShadowCastingMode.Off;
            ApplyOverlayFontMaterial(text);

            // Dark plate behind the text so it stays legible against sky and pavement alike.
            // Sits behind the text so back-to-front transparent sorting draws it first.
            Vector2 size = text.GetPreferredValues(label);
            var backing = GameObject.CreatePrimitive(PrimitiveType.Quad);
            backing.name = "Backing";
            backing.transform.SetParent(labelRoot, false);
            backing.transform.localPosition = new Vector3(0f, 0f, 0.05f);
            backing.transform.localScale = new Vector3(
                size.x + labelFontSize * 0.12f, size.y + labelFontSize * 0.07f, 1f);
            labelHalfHeight = backing.transform.localScale.y * 0.5f;

            var backingCollider = backing.GetComponent<Collider>();
            if (backingCollider != null) Destroy(backingCollider);

            var backingRenderer = backing.GetComponent<Renderer>();
            backingRenderer.material = CreateOverlayMaterial(BackingQueue, new Color(0f, 0f, 0f, 0.55f));
            backingRenderer.shadowCastingMode = ShadowCastingMode.Off;
            backingRenderer.receiveShadows = false;

            BuildConnector();

            var renderers = new List<Renderer> { textRenderer, backingRenderer };
            if (connector != null) renderers.Add(connector);
            labelRenderers = renderers.ToArray();

            // The robot goal starts hidden: RosOverlayVisibility decides, and it only applies
            // its state on a toggle or its own LateUpdate, which may be a frame away.
            if (followsRobotGoalVisibility)
            {
                foreach (Renderer renderer in labelRenderers) renderer.enabled = false;
            }
        }

        /// <summary>Line from the ground under the marker up to the bottom of the label.</summary>
        private void BuildConnector()
        {
            if (connectorStyle == ConnectorStyle.None) return;

            var connectorObj = new GameObject("Connector");
            connectorObj.transform.SetParent(root.transform, false);

            connector = connectorObj.AddComponent<LineRenderer>();
            connector.useWorldSpace = false;
            connector.positionCount = 2;
            connector.SetPosition(0, new Vector3(0f, 0.05f, 0f));
            connector.SetPosition(1, new Vector3(0f, labelHeight - labelHalfHeight, 0f));
            connector.widthMultiplier = 0.035f;
            connector.startColor = connector.endColor = color;
            connector.shadowCastingMode = ShadowCastingMode.Off;
            connector.receiveShadows = false;

            var material = CreateOverlayMaterial(ConnectorQueue, Color.white);
            if (connectorStyle == ConnectorStyle.Dashed)
            {
                // Tile mode repeats the texture per world unit of line, so the dashes stay the
                // same physical size whatever the label height works out to.
                connector.textureMode = LineTextureMode.Tile;
                material.mainTexture = DashTexture;
                material.mainTextureScale = new Vector2(Mathf.Max(0.1f, dashesPerMeter), 1f);
            }
            connector.material = material;
        }

        /// <summary>
        /// Swaps the label's font material to TMP's Overlay shader variant (ZTest Always), so
        /// the text shows through buildings. fontMaterial is a per-renderer instance, so the
        /// shared font asset every other TMP text uses is untouched. Missing shader (stripped
        /// from a build) just leaves the text depth-tested like before.
        /// </summary>
        private static void ApplyOverlayFontMaterial(TextMeshPro text)
        {
            Shader overlay = Shader.Find("TextMeshPro/Distance Field Overlay");
            if (overlay == null) return;

            Material fontMaterial = text.fontMaterial;
            fontMaterial.shader = overlay;
            fontMaterial.renderQueue = TextQueue;
        }

        /// <summary>
        /// Material for the backing plate / connector that draws through world geometry. Falls
        /// back to Sprites/Default (depth-tested, the old behaviour) if the overlay shader has
        /// not been imported.
        /// </summary>
        private static Material CreateOverlayMaterial(int renderQueue, Color color)
        {
            Shader shader = Shader.Find(OverlayShaderName);
            if (shader == null)
                return new Material(Shader.Find("Sprites/Default")) { color = color };

            return new Material(shader) { color = color, renderQueue = renderQueue };
        }

        /// <summary>Repeating opaque/transparent strip that turns the line into dashes.</summary>
        private static Texture2D DashTexture
        {
            get
            {
                if (dashTexture == null)
                {
                    const int width = 8;
                    dashTexture = new Texture2D(width, 1, TextureFormat.RGBA32, false)
                    {
                        filterMode = FilterMode.Point,
                        wrapMode = TextureWrapMode.Repeat
                    };
                    for (int x = 0; x < width; x++)
                        dashTexture.SetPixel(x, 0, new Color(1f, 1f, 1f, x < 5 ? 1f : 0f));
                    dashTexture.Apply();
                }
                return dashTexture;
            }
        }

        /// <summary>
        /// Finds the ground under the marker, so the label hovers at a predictable height even
        /// when the marker itself sits above or below the pavement. The marker's own collider is
        /// skipped, otherwise the ray would stop on the goal cube itself.
        /// </summary>
        private Vector3 ResolveGroundPosition()
        {
            Vector3 origin = transform.position + Vector3.up * 3f;
            RaycastHit[] hits = Physics.RaycastAll(origin, Vector3.down, 12f, ~0, QueryTriggerInteraction.Ignore);

            float best = float.MaxValue;
            Vector3 ground = transform.position;
            foreach (RaycastHit hit in hits)
            {
                if (hit.transform == transform || hit.transform.IsChildOf(transform)) continue;
                if (hit.distance < best)
                {
                    best = hit.distance;
                    ground = hit.point;
                }
            }
            return ground;
        }

        private void HideMarkerLabel()
        {
            // The beacon's own label is not part of this hierarchy, so everything found here is
            // the prefab's baked text.
            foreach (TMP_Text text in GetComponentsInChildren<TMP_Text>(true))
            {
                if (text != null) text.gameObject.SetActive(false);
            }
        }

        private Camera ResolveCamera()
        {
            if (cachedCamera != null && cachedCamera.isActiveAndEnabled) return cachedCamera;

            cachedCamera = Camera.main;
            if (cachedCamera == null || !cachedCamera.isActiveAndEnabled)
            {
                // AgentViewToggle and the review cameras can leave no MainCamera tagged.
                cachedCamera = Camera.allCameras.Length > 0 ? Camera.allCameras[0] : null;
            }
            return cachedCamera;
        }

        /// <summary>
        /// True when this label's agent is the one currently being driven. Only reached by the
        /// self-managed beacons -- the scenario robot goal defers to RosOverlayVisibility instead.
        /// </summary>
        private bool ShouldShowForDrivenAgent()
        {
            // Review is where every goal is wanted at once (RosOverlayVisibility switches the
            // robot overlays back on there too), so no label hides for role while it is up.
            var manager = SessionReviewManager.Instance;
            if (manager != null && manager.IsReviewUiActive)
                return true;

            DrivenAgent driven = CurrentDrivenAgent();
            return audience == Audience.Robot
                ? driven == DrivenAgent.Robot
                : driven != DrivenAgent.Robot;
        }

        /// <summary>
        /// Which agent the participant is driving right now. In the practice flow that is the
        /// phase (walk, then robot); in a scenario scene it is the SEAN controlled agent. A
        /// mid-trial operator nudge of the robot (VelocityController manual control) is
        /// deliberately NOT counted -- it must not strip a walking participant of their own goal.
        /// </summary>
        private static DrivenAgent CurrentDrivenAgent()
        {
            if (cachedFlow != null)
                return cachedFlow.IsDrivingRobot ? DrivenAgent.Robot : DrivenAgent.Pedestrian;

            // The scenario scenes are authored robot-controlled (sean.ControlledAgent == Robot)
            // even when the participant's chosen role is the pedestrian, so the onboarding role
            // must win -- otherwise a walking participant never sees their own goal label.
            if (SessionOnboardingSettings.HasCompletedOnboarding)
                return SessionOnboardingSettings.PlayerMode == OnboardingPlayerMode.Human
                    ? DrivenAgent.Pedestrian
                    : DrivenAgent.Robot;

            return IsControlledAgentRobot() ? DrivenAgent.Robot : DrivenAgent.Pedestrian;
        }

        private static bool IsControlledAgentRobot()
        {
            try
            {
                var sean = SEAN.SEAN.instance;
                return sean != null &&
                       sean.ControlledAgent == SEAN.Scenario.Agents.ControlledAgent.Robot;
            }
            catch (System.Exception)
            {
                // Scene without a (valid) SEAN rig -- treat as the pedestrian case.
                return false;
            }
        }

        #endregion

        #region Discovery

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            var host = new GameObject("GoalBeaconBootstrap") { hideFlags = HideFlags.HideAndDontSave };
            DontDestroyOnLoad(host);
            host.AddComponent<GoalBeaconBootstrap>();
        }

        /// <summary>
        /// Finds the goals the same ways the rest of the project does, so no scene has to be
        /// edited: the practice flow's wired markers, whatever RandomAvatar navigates to, the
        /// "end" marker the scenario scenes ship with, and the SEAN task's own player/robot
        /// markers -- which is what a scenario scene actually puts in front of the participant,
        /// since Tasks.Base.initStartAndGoal builds them from the _StartAndGoal rig at runtime.
        ///
        /// Polled rather than run once per scene load, for exactly that reason: those markers do
        /// not exist yet when the scene finishes loading, and are re-activated between trials.
        /// </summary>
        internal static void DiscoverGoals()
        {
            cachedFlow = null;
            foreach (var flow in FindObjectsOfType<TestSceneFlowManager>(true))
            {
                if (flow == null) continue;
                cachedFlow = flow; // read every frame to switch the labels by practice phase
                Attach(flow.pwdEnd, "Pedestrian Goal", PedestrianColor, Audience.Pedestrian, false);
                Attach(flow.robotEnd, "Robot Goal", RobotColor, Audience.Robot, false);
            }

            foreach (var spawner in FindObjectsOfType<SEAN.Scenario.Agents.RandomAvatar>(true))
            {
                if (spawner == null || string.IsNullOrEmpty(spawner.goalObjectName)) continue;
                GameObject goal = GameObject.Find(spawner.goalObjectName);
                if (goal != null) Attach(goal.transform, "Pedestrian Goal", PedestrianColor, Audience.Pedestrian, false);
            }

            GameObject fallback = GameObject.Find(DefaultGoalName);
            if (fallback != null) Attach(fallback.transform, "Pedestrian Goal", PedestrianColor, Audience.Pedestrian, false);

            SEAN.Tasks.Base task = FindRobotTask();
            if (task != null)
            {
                // The participant's goal in a scenario scene. Shown while the pedestrian is the
                // controlled agent (and the marker itself is deactivated otherwise anyway).
                if (task.playerGoal != null)
                    Attach(task.playerGoal.transform, "Pedestrian Goal", PedestrianColor, Audience.Pedestrian, false);
                // The robot's, whose visibility RosOverlayVisibility owns (drive-robot only).
                if (task.robotGoal != null)
                    Attach(task.robotGoal.transform, "Robot Goal", RobotColor, Audience.Robot, true);
            }
        }

        private static void Attach(Transform marker, string label, Color color, Audience audience,
                                   bool followsRobotGoalVisibility)
        {
            if (marker == null) return;
            if (marker.GetComponent<GoalBeacon>() != null) return;

            var beacon = marker.gameObject.AddComponent<GoalBeacon>();
            beacon.label = label;
            beacon.color = color;
            beacon.audience = audience;
            beacon.followsRobotGoalVisibility = followsRobotGoalVisibility;

            // Which marker got labelled is the one thing worth logging: when a label does not
            // show up, this says whether it was never attached or attached somewhere unexpected.
            Debug.Log($"[GoalBeacon] Labelled '{marker.name}' as \"{label}\"" +
                      (followsRobotGoalVisibility ? " (shown only while driving the robot)." : "."), marker);
        }

        private static SEAN.Tasks.Base FindRobotTask()
        {
            try
            {
                var sean = SEAN.SEAN.instance;
                return sean != null ? sean.robotTask : null;
            }
            catch (System.Exception)
            {
                // Scene without a (valid) SEAN rig.
                return null;
            }
        }

        #endregion
    }
}
