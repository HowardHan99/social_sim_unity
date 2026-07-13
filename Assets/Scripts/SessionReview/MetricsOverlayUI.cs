using UnityEngine;

namespace SessionReview
{
    public class MetricsOverlayUI : MonoBehaviour
    {
        [Header("Display Settings")]
        [SerializeField] private float panelWidth = 420f;
        [SerializeField] private float panelRightMargin = 16f;
        [SerializeField] private float panelY = 80f;
        [SerializeField] private float defaultPanelHeight = 540f;
        [SerializeField] private bool showTrajectoryDebug = false;

        private bool visible;
        private TrialRecord currentTrial;
        private readonly ReviewPanels.State panel = new ReviewPanels.State();

        // Cached so the "Trajectory Follow" diagnostics (previously a separate floating
        // HUD in TrajectoryUI) can be shown inline with the rest of the review data.
        private TrajectoryManager trajectoryManager;

        public bool IsVisible => visible;

        public void ShowTrial(TrialRecord trial)
        {
            currentTrial = trial;
            visible = true;
        }

        public void Hide()
        {
            visible = false;
            currentTrial = null;
        }

        private static string FormatDist(double val)
        {
            if (val >= double.MaxValue * 0.5 || double.IsInfinity(val) || double.IsNaN(val))
                return "N/A";
            return $"{val:F2}m";
        }

        void OnGUI()
        {
            if (!visible || currentTrial == null) return;

            if (SessionReviewManager.Instance != null && SessionReviewManager.Instance.IsWorldBuildingModeActive)
                return;

            // Docked on the right by default so it never covers the draw-mode panel on the left.
            Rect defaultRect = new Rect(
                Mathf.Max(10f, Screen.width - panelWidth - panelRightMargin), panelY, panelWidth,
                Mathf.Min(defaultPanelHeight, Screen.height - panelY - 20f));

            if (ReviewPanels.Begin(panel, this, "Metrics", defaultRect, out Rect content))
            {
                GUILayout.BeginArea(content);
                panel.scroll = GUILayout.BeginScrollView(panel.scroll);
                DrawBody();
                GUILayout.EndScrollView();
                GUILayout.EndArea();
            }
            ReviewPanels.End(panel);
        }

        private void DrawBody()
        {
            var t = currentTrial;
            float scale = panel.FontScale;
            float sectionGap = 8f * scale;

            GUIStyle bodyStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.RoundToInt(18f * scale),
                wordWrap = true
            };
            GUIStyle sectionStyle = new GUIStyle(GUI.skin.label)
            {
                fontStyle = FontStyle.Bold,
                fontSize = Mathf.RoundToInt(20f * scale)
            };
            GUIStyle headerStyle = new GUIStyle(GUI.skin.label)
            {
                fontStyle = FontStyle.Bold,
                fontSize = Mathf.RoundToInt(24f * scale)
            };

            string status;
            switch (t.endReason)
            {
                case TrialEndReason.Completion:
                    status = "[COMPLETED]";
                    break;
                case TrialEndReason.Manual:
                    status = "[ENDED]";
                    break;
                default:
                    status = "[TIMEOUT]";
                    break;
            }
            GUILayout.Label($"{status} {t.trialName} #{t.trialNumber}", headerStyle);
            GUILayout.Label($"Duration: {t.Duration:F1}s  ({t.startTime:F1}s - {t.endTime:F1}s)", bodyStyle);
            GUILayout.Space(sectionGap);

            GUILayout.Label("Agent Roster", sectionStyle);
            foreach (var arrival in t.agentArrivals)
            {
                string arrivalStr = arrival.arrived
                    ? $"arrived @ {arrival.arrivalTime:F1}s"
                    : "not arrived";
                GUILayout.Label($"  {arrival.agentName} ({arrival.role}): {arrivalStr}", bodyStyle);
            }
            GUILayout.Space(sectionGap);

            if (t.controlSummaries.Count > 0)
            {
                GUILayout.Label("Control Modes", sectionStyle);
                foreach (var cs in t.controlSummaries)
                {
                    float total = cs.manualSeconds + cs.autoSeconds + cs.staticSeconds;
                    if (total <= 0f) total = 1f;
                    string breakdown = "";
                    if (cs.manualSeconds > 0)
                        breakdown += $"Manual:{cs.manualSeconds:F1}s({cs.manualSeconds / total * 100:F0}%) ";
                    if (cs.autoSeconds > 0)
                        breakdown += $"Auto:{cs.autoSeconds:F1}s({cs.autoSeconds / total * 100:F0}%) ";
                    if (cs.staticSeconds > 0)
                        breakdown += $"Static:{cs.staticSeconds:F1}s({cs.staticSeconds / total * 100:F0}%) ";
                    GUILayout.Label($"  {cs.agentId}: {cs.overallMode}", bodyStyle);
                    GUILayout.Label($"    {breakdown}", bodyStyle);
                }
                GUILayout.Space(sectionGap);
            }

            if (t.metrics != null)
            {
                var m = t.metrics;
                GUILayout.Label("Robot Metrics", sectionStyle);
                GUILayout.Label($"  Path Length: {m.pathLength:F2}m", bodyStyle);
                GUILayout.Label($"  Min Dist to Target: {FormatDist(m.minDistToTarget)}", bodyStyle);
                GUILayout.Label($"  Min Dist to Pedestrian: {FormatDist(m.minDistToPed)}", bodyStyle);

                GUILayout.Space(sectionGap);
                GUILayout.Label("Safety Metrics", sectionStyle);
                GUILayout.Label($"  Robot->Person Collisions: {m.robotOnPersonCollisions}  Person->Robot: {m.personOnRobotCollisions}", bodyStyle);
                GUILayout.Label($"  Object Collisions: {m.objectCollisions}", bodyStyle);
                GUILayout.Label($"  Intimate Violations: R->P:{m.robotOnPersonIntimateViolations} P->R:{m.personOnRobotIntimateViolations}", bodyStyle);
                GUILayout.Label($"  Personal Violations: R->P:{m.robotOnPersonPersonalViolations} P->R:{m.personOnRobotPersonalViolations}", bodyStyle);
                GUILayout.Space(sectionGap);
            }

            GUIStyle toggleStyle = new GUIStyle(GUI.skin.toggle)
            {
                fontSize = Mathf.RoundToInt(16f * scale)
            };
            showTrajectoryDebug = GUILayout.Toggle(showTrajectoryDebug, " Trajectory debug", toggleStyle);
            if (showTrajectoryDebug)
                DrawTrajectoryFollowSection(sectionStyle, bodyStyle);
        }

        // Formerly a standalone floating HUD in TrajectoryUI ("Play=.. FollowMode=.. follow SKIP..").
        // Merged here so all review readouts live in one panel.
        private void DrawTrajectoryFollowSection(GUIStyle sectionStyle, GUIStyle bodyStyle)
        {
            if (trajectoryManager == null)
                trajectoryManager = FindObjectOfType<TrajectoryManager>();
            if (trajectoryManager == null)
                return;

            var tm = trajectoryManager;
            GUILayout.Label("Trajectory Follow", sectionStyle);
            GUILayout.Label($"  Play: {tm.ReviewIsPlaying}   t: {tm.ReviewNormalizedTime:P0}   rate: {tm.ReviewPlaybackSpeed:F2}x   toggles: {tm.ReviewToggleCount}", bodyStyle);
            GUILayout.Label($"  FollowMode: {tm.IsFollowMode}   HasTraj: {tm.HasFollowTrajectory}", bodyStyle);
            GUILayout.Label($"  dist: {tm.LastFollowDistance:F2}m   speed: {tm.EffectiveFollowSpeed:F2}m/s   elapsed: {tm.LastFollowElapsed:F2}s", bodyStyle);
            string skip = string.IsNullOrEmpty(tm.LastFollowSkipReason) ? "follow OK" : $"follow SKIP: {tm.LastFollowSkipReason}";
            GUILayout.Label($"  {skip}", bodyStyle);
        }
    }
}
