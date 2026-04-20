using UnityEngine;

namespace SessionReview
{
    public class MetricsOverlayUI : MonoBehaviour
    {
        [Header("Display Settings")]
        [SerializeField] private float panelWidth = 420f;
        [SerializeField] private float panelX = 10f;
        [SerializeField] private float panelY = 80f;

        private bool visible;
        private TrialRecord currentTrial;
        private Vector2 scrollPosition;

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

            var t = currentTrial;
            float lineH = 22f;
            float sectionGap = 8f;

            int lineCount = 8
                + t.agentArrivals.Count
                + t.controlSummaries.Count
                + (t.metrics != null ? 8 : 0);
            float contentHeight = lineCount * lineH + sectionGap * 5;
            float panelHeight = Mathf.Min(contentHeight + 60f, Screen.height - panelY - 20f);

            GUI.Box(new Rect(panelX, panelY, panelWidth, panelHeight), "");

            GUILayout.BeginArea(new Rect(panelX + 8, panelY + 8, panelWidth - 16, panelHeight - 16));
            scrollPosition = GUILayout.BeginScrollView(scrollPosition);

            GUIStyle headerStyle = new GUIStyle(GUI.skin.label)
            {
                fontStyle = FontStyle.Bold,
                fontSize = 14
            };
            GUIStyle sectionStyle = new GUIStyle(GUI.skin.label)
            {
                fontStyle = FontStyle.Bold,
                fontSize = 12
            };

            string status = t.endReason == TrialEndReason.Completion ? "[COMPLETED]" : "[TIMEOUT]";
            GUILayout.Label($"{status} {t.trialName} #{t.trialNumber}", headerStyle);
            GUILayout.Label($"Duration: {t.Duration:F1}s  ({t.startTime:F1}s - {t.endTime:F1}s)");
            GUILayout.Space(sectionGap);

            GUILayout.Label("Agent Roster", sectionStyle);
            foreach (var arrival in t.agentArrivals)
            {
                string arrivalStr = arrival.arrived
                    ? $"arrived @ {arrival.arrivalTime:F1}s"
                    : "not arrived";
                GUILayout.Label($"  {arrival.agentName} ({arrival.role}): {arrivalStr}");
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
                    GUILayout.Label($"  {cs.agentId}: {cs.overallMode}");
                    GUILayout.Label($"    {breakdown}");
                }
                GUILayout.Space(sectionGap);
            }

            if (t.metrics != null)
            {
                var m = t.metrics;
                GUILayout.Label("Robot Metrics", sectionStyle);
                GUILayout.Label($"  Path Length: {m.pathLength:F2}m");
                GUILayout.Label($"  Min Dist to Target: {FormatDist(m.minDistToTarget)}");
                GUILayout.Label($"  Min Dist to Pedestrian: {FormatDist(m.minDistToPed)}");

                GUILayout.Space(sectionGap);
                GUILayout.Label("Safety Metrics", sectionStyle);
                GUILayout.Label($"  Robot->Person Collisions: {m.robotOnPersonCollisions}  Person->Robot: {m.personOnRobotCollisions}");
                GUILayout.Label($"  Object Collisions: {m.objectCollisions}");
                GUILayout.Label($"  Intimate Violations: R->P:{m.robotOnPersonIntimateViolations} P->R:{m.personOnRobotIntimateViolations}");
                GUILayout.Label($"  Personal Violations: R->P:{m.robotOnPersonPersonalViolations} P->R:{m.personOnRobotPersonalViolations}");
            }

            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }
    }
}
