using System.Collections.Generic;
using UnityEngine;

namespace SessionReview
{
    /// <summary>
    /// One switch for the two ROS-driven overlays that give away where the robot is headed:
    ///   - the CONTROL TRAJECTORY: the nav-plan line PlanVisualizer draws from the global plan,
    ///   - the ROBOT GOAL: its flag cube/arrow, plus the "ROBOT GOAL" label and orange highlight
    ///     a RobotGoalObjectBinding adds when a scene object is bound as the goal.
    ///
    /// Both start hidden on every trial so a participant never sees the robot's preset path;
    /// the hotkey flips them mid-run, and entering review turns them back on — review is where
    /// they are actually wanted. The PLAYER goal is deliberately untouched: the human participant
    /// needs it to know where to walk.
    ///
    /// Hiding cannot be one-shot. PlanVisualizer redraws whenever ROS republishes the plan
    /// (message delivery runs in Update, so review's frozen timescale does not stop it) and
    /// RobotGoalObjectBinding rebuilds its highlight pieces on every rebind, so the goal
    /// renderers are re-applied each LateUpdate from a periodically refreshed cache.
    ///
    /// Neither overlay is a data source: PlanVisualizer keeps computing the plan while hidden
    /// (LiveTrajectoryRecorder still records it, and the trial-start readiness check still sees
    /// it), and the goal marker's transform — what feeds ROS goal publishing, completion checks
    /// and metrics — is never touched, only its renderers.
    /// </summary>
    public class RosOverlayVisibility : MonoBehaviour
    {
        [Tooltip("Toggles both the ROS control trajectory and the robot goal marker on/off.")]
        [SerializeField] private KeyCode toggleKey = KeyCode.V;

        /// <summary>How often the cached plan visualizers / goal renderers are re-discovered.</summary>
        private const float RediscoverInterval = 0.5f;

        private static bool planVisible;
        private static bool robotGoalVisible;

        private static readonly List<SEAN.Display.PlanVisualizer> planVisualizers =
            new List<SEAN.Display.PlanVisualizer>();
        private static readonly List<Renderer> goalRenderers = new List<Renderer>();
        private static float lastRediscoverTime = float.NegativeInfinity;

        /// <summary>Is the ROS control trajectory (nav plan line) currently shown?</summary>
        public static bool PlanVisible => planVisible;

        /// <summary>Is the robot goal marker (and its goal-binding label/highlight) currently shown?</summary>
        public static bool RobotGoalVisible => robotGoalVisible;

        /// <summary>
        /// True when the scene actually has a ROS plan visualizer — i.e. there is a control
        /// trajectory to toggle at all. Without ROS only the goal button is worth showing.
        /// </summary>
        public static bool HasControlTrajectory { get; private set; }

        // Footprint of the button strip for the frame it was last drawn, so scene-input handlers
        // (draw-mode strokes, top-down pan/zoom) can ignore clicks that land on it.
        private static Rect controlRect;
        private static int controlFrame = -1;

        /// <summary>True when the scaled-GUI-space point is over the overlay toggle strip.</summary>
        public static bool ControlContains(Vector2 guiPoint)
        {
            return Time.frameCount - controlFrame <= 1 && controlRect.Contains(guiPoint);
        }

        // ── Public API ───────────────────────────────────────────────────────────

        public static void SetPlanVisible(bool visible)
        {
            planVisible = visible;
            Rediscover();
            Apply();
        }

        public static void SetRobotGoalVisible(bool visible)
        {
            robotGoalVisible = visible;
            Rediscover();
            Apply();
        }

        /// <summary>Show/hide both overlays at once (trial start hides, review entry shows).</summary>
        public static void SetAllVisible(bool visible)
        {
            planVisible = visible;
            robotGoalVisible = visible;
            Rediscover();
            Apply();
        }

        /// <summary>Hotkey behaviour: anything still hidden turns everything on, else all off.</summary>
        public static void ToggleAll()
        {
            SetAllVisible(!(planVisible && robotGoalVisible));
        }

        // ── Unity lifecycle ──────────────────────────────────────────────────────

        void Update()
        {
            // Onboarding owns the keyboard (Session ID text field), and any focused IMGUI
            // field would otherwise swallow the letter into the text AND toggle the overlays.
            var manager = SessionReviewManager.Instance;
            if (manager != null && manager.IsOnboardingActive)
                return;
            if (GUIUtility.keyboardControl != 0)
                return;

            if (Input.GetKeyDown(toggleKey))
                ToggleAll();
        }

        void LateUpdate()
        {
            // Unscaled: review freezes Time.timeScale, and the overlays must keep being
            // re-hidden there too.
            if (Time.unscaledTime - lastRediscoverTime >= RediscoverInterval)
            {
                Rediscover();
                ApplyPlanSuppression();
            }

            ApplyGoalRenderers();
        }

        void OnGUI()
        {
            var manager = SessionReviewManager.Instance;
            if (manager != null && (manager.IsOnboardingActive || manager.IsWorldBuildingModeActive))
                return;

            ReviewUiScale.Apply();

            const float x = 8f;
            const float y = 44f;   // directly under the UiScaleController "Aa 100%" badge
            const float bw = 148f;
            const float bh = 28f;
            const float gap = 6f;
            const float pad = 6f;
            const float labelH = 16f;

            int buttons = HasControlTrajectory ? 2 : 1;
            float stripW = pad * 2f + buttons * bw + (buttons - 1) * gap;
            Rect strip = new Rect(x, y, stripW, labelH + bh + pad * 2f);
            GUI.Box(strip, GUIContent.none);

            GUI.Label(new Rect(strip.x + pad, strip.y + 2f, stripW - pad * 2f, labelH),
                $"Robot intent  [{toggleKey}]", HintStyle);

            float bx = strip.x + pad;
            float by = strip.y + labelH + pad;

            if (HasControlTrajectory)
            {
                if (ToggleButton(new Rect(bx, by, bw, bh), "Control Traj", planVisible))
                    SetPlanVisible(!planVisible);
                bx += bw + gap;
            }

            if (ToggleButton(new Rect(bx, by, bw, bh), "Robot Goal", robotGoalVisible))
                SetRobotGoalVisible(!robotGoalVisible);

            controlRect = strip;
            controlFrame = Time.frameCount;
        }

        private static bool ToggleButton(Rect rect, string label, bool on)
        {
            GUI.backgroundColor = on
                ? new Color(0.22f, 0.6f, 0.34f)
                : new Color(0.34f, 0.34f, 0.38f);
            bool clicked = GUI.Button(rect, $"{label}: {(on ? "ON" : "OFF")}");
            GUI.backgroundColor = Color.white;
            return clicked;
        }

        private static GUIStyle hintStyle;
        private static GUIStyle HintStyle
        {
            get
            {
                if (hintStyle == null)
                {
                    hintStyle = new GUIStyle(GUI.skin.label)
                    {
                        fontSize = 11,
                        normal = { textColor = new Color(0.78f, 0.81f, 0.86f) }
                    };
                }
                return hintStyle;
            }
        }

        // ── Apply / discover ─────────────────────────────────────────────────────

        private static void Apply()
        {
            ApplyPlanSuppression();
            ApplyGoalRenderers();
            PushPlanToReviewLegend();
        }

        private static void ApplyPlanSuppression()
        {
            for (int i = 0; i < planVisualizers.Count; i++)
            {
                if (planVisualizers[i] != null)
                {
                    planVisualizers[i].SetRenderingSuppressed(
                        SEAN.Display.PlanVisualizer.SuppressionReason.UserToggle, !planVisible);
                }
            }
        }

        private static void ApplyGoalRenderers()
        {
            for (int i = 0; i < goalRenderers.Count; i++)
            {
                if (goalRenderers[i] != null)
                    goalRenderers[i].enabled = robotGoalVisible;
            }
        }

        /// <summary>
        /// In review the live plan line is force-suppressed for the whole session and the plan
        /// is instead shown as a recorded snapshot owned by the Review Legend, so the switch has
        /// to drive that legend group. Pushed only on an explicit toggle — doing it every frame
        /// would fight a reviewer who turns the "ROS Nav Plan" row off from the legend itself.
        /// </summary>
        private static void PushPlanToReviewLegend()
        {
            var manager = SessionReviewManager.Instance;
            if (manager == null || !manager.IsReviewUiActive)
                return;

            var trajectoryRenderer = manager.GetComponent<MultiAgentTrajectoryRenderer>();
            if (trajectoryRenderer != null)
                trajectoryRenderer.SetExternalGroupVisible(RewindController.ActivePlanLegendKey, planVisible);
        }

        private static void Rediscover()
        {
            lastRediscoverTime = Time.unscaledTime;

            planVisualizers.Clear();
            planVisualizers.AddRange(FindObjectsOfType<SEAN.Display.PlanVisualizer>());
            HasControlTrajectory = planVisualizers.Count > 0;

            goalRenderers.Clear();

            // A bound goal object is ordinary world geometry (a door, a bench) that merely
            // stands in for the goal — hiding the marker must never hide it. RobotGoalObjectBinding
            // refuses to bind anything under the marker, but the check is cheap next to deleting
            // a building from the participant's view if that ever changes.
            Transform boundObject = RobotGoalObjectBinding.BoundObject != null
                ? RobotGoalObjectBinding.BoundObject.transform
                : null;

            SEAN.Tasks.Base task = FindRobotTask();
            if (task != null && task.robotGoal != null)
            {
                foreach (Renderer renderer in task.robotGoal.GetComponentsInChildren<Renderer>(true))
                {
                    if (renderer != null && (boundObject == null || !renderer.transform.IsChildOf(boundObject)))
                        goalRenderers.Add(renderer);
                }
            }

            // The floating "ROBOT GOAL" text and the orange object highlight are goal UI too.
            // GoalUiRenderers deliberately excludes the bound object itself.
            if (RobotGoalObjectBinding.Instance != null)
                goalRenderers.AddRange(RobotGoalObjectBinding.Instance.GoalUiRenderers);
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
    }
}
