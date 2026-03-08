using System.Collections.Generic;
using UnityEngine;
using Rerun;

namespace SessionReview
{
    public class MultiAgentTrajectoryRenderer : MonoBehaviour
    {
        [Header("Line Settings")]
        [SerializeField] private float lineWidth = 0.08f;
        [SerializeField] private Material lineMaterial;

        [Header("Direction Arrows")]
        [SerializeField] private bool showDirectionArrows = true;
        [SerializeField] private int arrowSpacing = 8;
        [SerializeField] private float arrowLength = 0.6f;

        [Header("Markers")]
        [SerializeField] private float markerSize = 0.3f;

        [Header("Color Palette")]
        [SerializeField] private Color robotColor = new Color(0.9f, 0.2f, 0.2f);
        [SerializeField] private Color robotManualColor = new Color(0.2f, 0.4f, 0.9f);
        [SerializeField] private Color robotAutoColor = new Color(0.2f, 0.9f, 0.8f);
        [SerializeField] private Color pwdColor = new Color(0.7f, 0.2f, 0.8f);
        [SerializeField] private Color pwdManualColor = new Color(0.6f, 0.1f, 0.7f);
        [SerializeField] private Color pwdAutoColor = new Color(0.9f, 0.5f, 0.8f);
        [SerializeField] private Color pwdStaticColor = new Color(0.5f, 0.5f, 0.5f, 0.4f);
        [SerializeField] private Color[] pedestrianPalette = new Color[]
        {
            new Color(0.2f, 0.5f, 0.9f),
            new Color(0.2f, 0.8f, 0.3f),
            new Color(0.9f, 0.6f, 0.1f),
            new Color(0.1f, 0.8f, 0.8f),
            new Color(0.9f, 0.9f, 0.2f),
            new Color(0.5f, 0.3f, 0.1f),
            new Color(0.8f, 0.3f, 0.5f),
            new Color(0.4f, 0.7f, 0.5f)
        };

        private GameObject overlayParent;
        private List<LineRenderer> trajectoryLines = new List<LineRenderer>();
        private List<LineRenderer> arrowLines = new List<LineRenderer>();
        private List<GameObject> markers = new List<GameObject>();
        private bool isShowing;

        private struct LegendEntry
        {
            public string label;
            public Color color;
        }
        private List<LegendEntry> legendEntries = new List<LegendEntry>();

        public bool IsShowing => isShowing;

        /// <param name="timeOffset">recordingStartTime -- subtract from trial timestamps to get recording timestamps</param>
        public void ShowTrajectories(TrialRecord trial, StateRecording recording, ControlModeLog modeLog, float timeOffset = 0f)
        {
            ClearAll();
            if (trial == null || recording == null) return;

            overlayParent = new GameObject("TrajectoryOverlay");
            overlayParent.transform.SetParent(transform);

            if (recording.timelineDict == null)
                recording.BuildCache();

            float recStart = trial.startTime - timeOffset;
            float recEnd = trial.endTime - timeOffset;

            int pedColorIndex = 0;
            int agentsFound = 0;

            Debug.Log($"[SessionReview] Rendering trajectories. Trial has {trial.agentRoles.Count} agents. " +
                      $"Recording has {recording.timelineDict.Count} timelines. " +
                      $"Window: rec[{recStart:F1}, {recEnd:F1}]");

            foreach (var roleEntry in trial.agentRoles)
            {
                string objectId = roleEntry.objectId;
                AgentRole role = roleEntry.role;

                if (!recording.timelineDict.TryGetValue(objectId, out ObjectStateTimeline timeline))
                {
                    Debug.LogWarning($"[SessionReview] Agent \"{objectId}\" ({role}) NOT FOUND in recording. " +
                                     $"Available keys: {string.Join(", ", recording.timelineDict.Keys)}");
                    continue;
                }

                var positions = new List<Vector3>();
                var rotations = new List<Quaternion>();
                var timestamps = new List<float>();

                foreach (var state in timeline.states)
                {
                    if (state.timestamp < recStart || state.timestamp > recEnd)
                        continue;
                    positions.Add(state.position);
                    rotations.Add(state.rotation);
                    timestamps.Add(state.timestamp);
                }

                if (positions.Count < 2) continue;
                agentsFound++;

                Color baseColor;
                switch (role)
                {
                    case AgentRole.Robot:
                        baseColor = robotColor;
                        break;
                    case AgentRole.PWDPlayer:
                        baseColor = pwdColor;
                        break;
                    case AgentRole.BackgroundPWD:
                        baseColor = pedestrianPalette[pedColorIndex % pedestrianPalette.Length];
                        pedColorIndex++;
                        break;
                    default:
                        baseColor = pedestrianPalette[pedColorIndex % pedestrianPalette.Length];
                        pedColorIndex++;
                        break;
                }

                bool hasControlModes = (role == AgentRole.Robot || role == AgentRole.PWDPlayer) && modeLog != null;
                Gradient gradient = null;
                if (hasControlModes)
                    gradient = BuildControlModeGradient(objectId, trial, modeLog, role);

                CreateTrajectoryLine(objectId, positions, rotations, timestamps, baseColor, gradient, role);

                if (showDirectionArrows && (role == AgentRole.Robot || role == AgentRole.PWDPlayer))
                    CreateDirectionArrows(objectId, positions, rotations, baseColor);

                CreateMarker(positions[0], baseColor, objectId + "_start", 1f);
                CreateMarker(positions[positions.Count - 1], baseColor, objectId + "_end", 0.6f);

                string shortName = objectId;
                int lastUnderscore = objectId.LastIndexOf('_');
                if (lastUnderscore > 0 && lastUnderscore < objectId.Length - 1)
                    shortName = objectId.Substring(0, lastUnderscore);
                legendEntries.Add(new LegendEntry { label = $"{role}: {shortName}", color = baseColor });
            }

            if (agentsFound == 0)
                Debug.LogWarning($"[MultiAgentTrajectoryRenderer] No agent trajectories found. " +
                    $"Trial window: [{trial.startTime:F1}, {trial.endTime:F1}], " +
                    $"Recording window: [{recStart:F1}, {recEnd:F1}], " +
                    $"Agents in trial: {trial.agentRoles.Count}, " +
                    $"Timelines in recording: {recording.timelineDict.Count}");

            isShowing = true;
        }

        private Gradient BuildControlModeGradient(string agentId, TrialRecord trial, ControlModeLog modeLog, AgentRole role)
        {
            var entries = trial.controlModeEntries.FindAll(e => e.agentId == agentId);
            if (entries.Count == 0) return null;

            float duration = trial.Duration;
            if (duration <= 0f) return null;

            var gradient = new Gradient();
            var colorKeys = new List<GradientColorKey>();
            var alphaKeys = new GradientAlphaKey[]
            {
                new GradientAlphaKey(1f, 0f),
                new GradientAlphaKey(1f, 1f)
            };

            foreach (var entry in entries)
            {
                float t = Mathf.Clamp01((entry.timestamp - trial.startTime) / duration);
                Color c = GetModeColor(entry.mode, role);
                colorKeys.Add(new GradientColorKey(c, t));
            }

            if (colorKeys.Count == 0)
                return null;
            if (colorKeys.Count == 1)
                colorKeys.Add(new GradientColorKey(colorKeys[0].color, 1f));

            while (colorKeys.Count > 8)
                colorKeys.RemoveAt(colorKeys.Count / 2);

            gradient.SetKeys(colorKeys.ToArray(), alphaKeys);
            return gradient;
        }

        private Color GetModeColor(ControlMode mode, AgentRole role)
        {
            if (role == AgentRole.Robot)
                return mode == ControlMode.Manual ? robotManualColor : robotAutoColor;

            switch (mode)
            {
                case ControlMode.Manual: return pwdManualColor;
                case ControlMode.SFPWDAuto: return pwdAutoColor;
                case ControlMode.Static: return pwdStaticColor;
                default: return pwdAutoColor;
            }
        }

        private void CreateTrajectoryLine(string id, List<Vector3> positions, List<Quaternion> rotations,
            List<float> timestamps, Color color, Gradient gradient, AgentRole role)
        {
            var lineObj = new GameObject("Traj_" + id);
            lineObj.transform.SetParent(overlayParent.transform);
            var lr = lineObj.AddComponent<LineRenderer>();

            lr.positionCount = positions.Count;
            for (int i = 0; i < positions.Count; i++)
            {
                Vector3 p = positions[i];
                p.y += 0.05f;
                lr.SetPosition(i, p);
            }

            float width = lineWidth;
            if (role == AgentRole.Robot) width = lineWidth * 1.5f;
            else if (role == AgentRole.PWDPlayer) width = lineWidth * 1.3f;
            lr.startWidth = width;
            lr.endWidth = width;
            lr.useWorldSpace = true;

            if (lineMaterial != null)
                lr.material = new Material(lineMaterial);
            else
                lr.material = new Material(Shader.Find("Sprites/Default"));

            // Material color must be white so vertex colors show through correctly
            lr.material.color = Color.white;

            if (gradient != null)
            {
                lr.colorGradient = gradient;
            }
            else
            {
                lr.startColor = color;
                lr.endColor = color;
            }

            if (role == AgentRole.BackgroundPWD)
            {
                lr.textureMode = LineTextureMode.Tile;
                lr.material.mainTextureScale = new Vector2(1f / 0.3f, 1f);
            }

            Debug.Log($"[SessionReview] Line for \"{id}\" ({role}): color={color}, gradient={gradient != null}, points={positions.Count}");
            trajectoryLines.Add(lr);
        }

        private void CreateDirectionArrows(string id, List<Vector3> positions, List<Quaternion> rotations, Color color)
        {
            for (int i = 0; i < positions.Count; i += arrowSpacing)
            {
                var arrowObj = new GameObject("Arrow_" + id + "_" + i);
                arrowObj.transform.SetParent(overlayParent.transform);
                var arrow = arrowObj.AddComponent<LineRenderer>();

                arrow.positionCount = 2;
                arrow.startWidth = lineWidth * 0.4f;
                arrow.endWidth = 0f;
                arrow.useWorldSpace = true;

                if (lineMaterial != null)
                    arrow.material = new Material(lineMaterial);
                else
                    arrow.material = new Material(Shader.Find("Sprites/Default"));

                arrow.material.color = Color.white;
                arrow.startColor = color;
                arrow.endColor = color;

                Vector3 pos = positions[i];
                pos.y += 0.05f;
                Vector3 forward = rotations[i] * Vector3.forward;
                arrow.SetPosition(0, pos);
                arrow.SetPosition(1, pos + forward * arrowLength);

                arrowLines.Add(arrow);
            }
        }

        private void CreateMarker(Vector3 position, Color color, string name, float scale)
        {
            var marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            marker.name = "Marker_" + name;
            marker.transform.SetParent(overlayParent.transform);
            marker.transform.position = position + Vector3.up * 0.3f;
            marker.transform.localScale = Vector3.one * markerSize * scale;

            var collider = marker.GetComponent<Collider>();
            if (collider != null) Destroy(collider);

            var renderer = marker.GetComponent<Renderer>();
            if (renderer != null)
            {
                renderer.material = new Material(Shader.Find("Sprites/Default"));
                renderer.material.color = color;
            }

            markers.Add(marker);
        }

        public void ClearAll()
        {
            if (overlayParent != null)
                Destroy(overlayParent);

            trajectoryLines.Clear();
            arrowLines.Clear();
            markers.Clear();
            legendEntries.Clear();
            isShowing = false;
        }

        public void SetVisible(bool visible)
        {
            if (overlayParent != null)
                overlayParent.SetActive(visible);
            isShowing = visible;
        }

        void OnDestroy()
        {
            ClearAll();
        }

        void OnGUI()
        {
            if (!isShowing || legendEntries.Count == 0) return;

            float boxW = 220f;
            float lineH = 22f;
            float pad = 8f;
            float boxH = pad * 2 + legendEntries.Count * lineH + 20f;
            float x = 15f;
            float y = Screen.height - boxH - 15f;

            GUI.backgroundColor = new Color(0f, 0f, 0f, 0.75f);
            GUI.Box(new Rect(x, y, boxW, boxH), "");

            var headerStyle = new GUIStyle(GUI.skin.label)
            {
                fontStyle = FontStyle.Bold,
                fontSize = 13,
                normal = { textColor = Color.white }
            };
            GUI.Label(new Rect(x + pad, y + pad, boxW - pad * 2, 18f), "Agents", headerStyle);

            var labelStyle = new GUIStyle(GUI.skin.label) { fontSize = 12 };
            for (int i = 0; i < legendEntries.Count; i++)
            {
                var entry = legendEntries[i];
                float ly = y + pad + 20f + i * lineH;

                Color prev = GUI.backgroundColor;
                GUI.backgroundColor = entry.color;
                GUI.Box(new Rect(x + pad, ly + 3f, 14f, 14f), "", GUI.skin.button);
                GUI.backgroundColor = prev;

                labelStyle.normal.textColor = entry.color;
                GUI.Label(new Rect(x + pad + 20f, ly, boxW - pad * 2 - 20f, lineH), entry.label, labelStyle);
            }

            GUI.backgroundColor = Color.white;
        }
    }
}
