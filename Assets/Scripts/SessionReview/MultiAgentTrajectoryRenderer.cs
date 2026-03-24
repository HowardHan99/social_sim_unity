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

        [Header("Stop Circles")]
        [SerializeField] private bool showRobotStopCircles = true;
        [SerializeField] private float stopSpeedThreshold = 0.05f;
        [SerializeField] private float minStopDuration = 0.6f;
        [SerializeField] private float stopCircleBaseRadius = 0.1f;
        [SerializeField] private float stopCircleRadiusPerSecond = 0.1f;
        [SerializeField] private float stopCircleMaxRadius = 2.5f;
        [SerializeField] private int stopCircleSegments = 24;
        [SerializeField] private float stopCircleYOffset = 0.16f;
        [SerializeField] private Color stopCircleColor = new Color(1f, 0.95f, 0.2f, 0.95f);

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

        [Header("VLM Capture Annotations")]
        [SerializeField] private bool showVLMAnnotations = true;
        [SerializeField] private Color vlmAnnotationColor = new Color(0.2f, 0.6f, 1f, 0.95f);
        [SerializeField] private float vlmMarkerSize = 0.4f;
        [SerializeField] private float vlmMarkerYOffset = 0.25f;
        [SerializeField] private GameObject vlmAnnotationPrefab;

        [Header("Auto Plan Path")]
        [SerializeField] private Color planPathColor = new Color(0.2f, 1f, 0.3f, 0.85f);
        [SerializeField] private float planPathLineWidth = 0.12f;
        [SerializeField] private bool showPlanPaths = true;

        private GameObject overlayParent;
        private List<LineRenderer> trajectoryLines = new List<LineRenderer>();
        private List<LineRenderer> arrowLines = new List<LineRenderer>();
        private List<LineRenderer> stopCircleLines = new List<LineRenderer>();
        private List<LineRenderer> planPathLines = new List<LineRenderer>();
        private List<GameObject> vlmMarkers = new List<GameObject>();
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
            ShowTrajectories(trial, recording, modeLog, null, null, timeOffset);
        }

        public void ShowTrajectories(TrialRecord trial, StateRecording recording, ControlModeLog modeLog,
            List<PlanPathSnapshot> planSnapshots, float timeOffset = 0f)
        {
            ShowTrajectories(trial, recording, modeLog, planSnapshots, null, timeOffset);
        }

        public void ShowTrajectories(TrialRecord trial, StateRecording recording, ControlModeLog modeLog,
            List<PlanPathSnapshot> planSnapshots, List<VLMCaptureEvent> vlmCaptures, float timeOffset = 0f)
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
            bool stopLegendAdded = false;

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

                if (showRobotStopCircles && role == AgentRole.Robot)
                {
                    int stops = CreateStopCircles(objectId, positions, timestamps, baseColor);
                    if (stops > 0 && !stopLegendAdded)
                    {
                        legendEntries.Add(new LegendEntry { label = "Robot Stop (radius=duration)", color = stopCircleColor });
                        stopLegendAdded = true;
                    }
                }

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

            if (showPlanPaths && planSnapshots != null && planSnapshots.Count > 0)
            {
                CreatePlanPathLines(planSnapshots);
                legendEntries.Add(new LegendEntry { label = "ROS Nav Plan", color = planPathColor });
            }

            var vlmEvents = vlmCaptures ?? trial.vlmCaptures;
            if (showVLMAnnotations && vlmEvents != null && vlmEvents.Count > 0)
            {
                CreateVLMAnnotations(vlmEvents);
                legendEntries.Add(new LegendEntry { label = $"VLM Capture ({vlmEvents.Count})", color = vlmAnnotationColor });
            }

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

        private int CreateStopCircles(string id, List<Vector3> positions, List<float> timestamps, Color baseColor)
        {
            if (positions == null || timestamps == null || positions.Count < 2 || timestamps.Count != positions.Count)
                return 0;

            int circlesCreated = 0;
            int stopStartIndex = -1;

            for (int i = 1; i < positions.Count; i++)
            {
                float dt = timestamps[i] - timestamps[i - 1];
                if (dt <= 0f) continue;

                float speed = Vector3.Distance(positions[i], positions[i - 1]) / dt;
                bool isStopped = speed <= stopSpeedThreshold;

                if (isStopped)
                {
                    if (stopStartIndex < 0)
                        stopStartIndex = i - 1;
                }
                else if (stopStartIndex >= 0)
                {
                    if (TryCreateStopCircle(id, positions, timestamps, stopStartIndex, i - 1, baseColor))
                        circlesCreated++;
                    stopStartIndex = -1;
                }
            }

            if (stopStartIndex >= 0)
            {
                if (TryCreateStopCircle(id, positions, timestamps, stopStartIndex, positions.Count - 1, baseColor))
                    circlesCreated++;
            }

            if (circlesCreated > 0)
                Debug.Log($"[SessionReview] Stop circles for \"{id}\": {circlesCreated}");

            return circlesCreated;
        }

        private bool TryCreateStopCircle(string id, List<Vector3> positions, List<float> timestamps,
            int startIndex, int endIndex, Color baseColor)
        {
            if (startIndex < 0 || endIndex <= startIndex || endIndex >= positions.Count)
                return false;

            float duration = timestamps[endIndex] - timestamps[startIndex];
            if (duration < minStopDuration)
                return false;

            Vector3 center = Vector3.zero;
            int count = 0;
            for (int i = startIndex; i <= endIndex; i++)
            {
                center += positions[i];
                count++;
            }
            if (count == 0) return false;
            center /= count;
            center.y += stopCircleYOffset;

            float radius = stopCircleBaseRadius + duration * stopCircleRadiusPerSecond;
            radius = Mathf.Clamp(radius, stopCircleBaseRadius, stopCircleMaxRadius);

            var circleObj = new GameObject($"StopCircle_{id}_{startIndex}_{endIndex}");
            circleObj.transform.SetParent(overlayParent.transform);
            var lr = circleObj.AddComponent<LineRenderer>();

            lr.useWorldSpace = true;
            lr.loop = false;
            lr.startWidth = lineWidth * 0.5f;
            lr.endWidth = lineWidth * 0.5f;

            if (lineMaterial != null)
                lr.material = new Material(lineMaterial);
            else
                lr.material = new Material(Shader.Find("Sprites/Default"));
            lr.material.color = Color.white;

            Color c = Color.Lerp(baseColor, stopCircleColor, 0.8f);
            c.a = stopCircleColor.a;
            lr.startColor = c;
            lr.endColor = c;

            int segments = Mathf.Max(8, stopCircleSegments);
            lr.positionCount = segments + 1;
            for (int i = 0; i <= segments; i++)
            {
                float angle = (i / (float)segments) * Mathf.PI * 2f;
                Vector3 offset = new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
                lr.SetPosition(i, center + offset);
            }

            stopCircleLines.Add(lr);

            CreateStopCircleCross(circleObj.transform, center, radius, c);

            return true;
        }

        private void CreateStopCircleCross(Transform circleParent, Vector3 center, float radius, Color color)
        {
            float crossWidth = lineWidth * 0.35f;

            for (int arm = 0; arm < 2; arm++)
            {
                var crossObj = new GameObject(arm == 0 ? "Cross_H" : "Cross_V");
                crossObj.transform.SetParent(circleParent);

                var lr = crossObj.AddComponent<LineRenderer>();
                lr.useWorldSpace = true;
                lr.loop = false;
                lr.positionCount = 2;
                lr.startWidth = crossWidth;
                lr.endWidth = crossWidth;

                if (lineMaterial != null)
                    lr.material = new Material(lineMaterial);
                else
                    lr.material = new Material(Shader.Find("Sprites/Default"));
                lr.material.color = Color.white;
                lr.startColor = color;
                lr.endColor = color;

                if (arm == 0)
                {
                    lr.SetPosition(0, center + new Vector3(-radius, 0f, 0f));
                    lr.SetPosition(1, center + new Vector3(radius, 0f, 0f));
                }
                else
                {
                    lr.SetPosition(0, center + new Vector3(0f, 0f, -radius));
                    lr.SetPosition(1, center + new Vector3(0f, 0f, radius));
                }

                stopCircleLines.Add(lr);
            }
        }

        private void CreateVLMAnnotations(List<VLMCaptureEvent> events)
        {
            for (int i = 0; i < events.Count; i++)
            {
                var evt = events[i];
                Vector3 pos = evt.position;
                pos.y += vlmMarkerYOffset;

                if (vlmAnnotationPrefab != null)
                {
                    var instance = Instantiate(vlmAnnotationPrefab, pos, Quaternion.identity, overlayParent.transform);
                    instance.name = $"VLMAnnotation_{i}";
                    vlmMarkers.Add(instance);
                }
                else
                {
                    CreateVLMDiamond(i, pos, evt.rotation);
                }
            }

            Debug.Log($"[SessionReview] VLM annotations: {events.Count}");
        }

        private void CreateVLMDiamond(int index, Vector3 center, Quaternion rotation)
        {
            float s = vlmMarkerSize;
            var parent = new GameObject($"VLMDiamond_{index}");
            parent.transform.SetParent(overlayParent.transform);

            Vector3 top = center + new Vector3(0f, 0f, s);
            Vector3 right = center + new Vector3(s, 0f, 0f);
            Vector3 bottom = center + new Vector3(0f, 0f, -s);
            Vector3 left = center + new Vector3(-s, 0f, 0f);

            var outlineObj = new GameObject("Outline");
            outlineObj.transform.SetParent(parent.transform);
            var outline = outlineObj.AddComponent<LineRenderer>();
            outline.useWorldSpace = true;
            outline.loop = true;
            outline.positionCount = 4;
            outline.startWidth = lineWidth * 0.6f;
            outline.endWidth = lineWidth * 0.6f;
            outline.SetPosition(0, top);
            outline.SetPosition(1, right);
            outline.SetPosition(2, bottom);
            outline.SetPosition(3, left);
            ApplyLineMaterial(outline, vlmAnnotationColor);
            stopCircleLines.Add(outline);

            var crossH = new GameObject("Cross_H");
            crossH.transform.SetParent(parent.transform);
            var lrH = crossH.AddComponent<LineRenderer>();
            lrH.useWorldSpace = true;
            lrH.positionCount = 2;
            lrH.startWidth = lineWidth * 0.3f;
            lrH.endWidth = lineWidth * 0.3f;
            lrH.SetPosition(0, left);
            lrH.SetPosition(1, right);
            ApplyLineMaterial(lrH, vlmAnnotationColor);
            stopCircleLines.Add(lrH);

            var crossV = new GameObject("Cross_V");
            crossV.transform.SetParent(parent.transform);
            var lrV = crossV.AddComponent<LineRenderer>();
            lrV.useWorldSpace = true;
            lrV.positionCount = 2;
            lrV.startWidth = lineWidth * 0.3f;
            lrV.endWidth = lineWidth * 0.3f;
            lrV.SetPosition(0, bottom);
            lrV.SetPosition(1, top);
            ApplyLineMaterial(lrV, vlmAnnotationColor);
            stopCircleLines.Add(lrV);

            Vector3 fwd = rotation * Vector3.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude > 0.001f)
            {
                fwd = fwd.normalized;
                var dirObj = new GameObject("ViewDir");
                dirObj.transform.SetParent(parent.transform);
                var dirLr = dirObj.AddComponent<LineRenderer>();
                dirLr.useWorldSpace = true;
                dirLr.positionCount = 2;
                dirLr.startWidth = lineWidth * 0.5f;
                dirLr.endWidth = 0f;
                dirLr.SetPosition(0, center);
                dirLr.SetPosition(1, center + fwd * s * 1.6f);
                ApplyLineMaterial(dirLr, vlmAnnotationColor);
                stopCircleLines.Add(dirLr);
            }

            vlmMarkers.Add(parent);
        }

        private void ApplyLineMaterial(LineRenderer lr, Color color)
        {
            if (lineMaterial != null)
                lr.material = new Material(lineMaterial);
            else
                lr.material = new Material(Shader.Find("Sprites/Default"));
            lr.material.color = Color.white;
            lr.startColor = color;
            lr.endColor = color;
        }

        private void CreatePlanPathLines(List<PlanPathSnapshot> snapshots)
        {
            // Only render the last (final) planned path -- the intermediate
            // replans are noise; what matters is where the planner intended
            // the robot to go at the end of the trial.
            PlanPathSnapshot lastSnap = null;
            for (int s = snapshots.Count - 1; s >= 0; s--)
            {
                if (snapshots[s].positions != null && snapshots[s].positions.Length >= 2)
                { lastSnap = snapshots[s]; break; }
            }
            if (lastSnap == null) return;

            var lineObj = new GameObject("PlanPath_final");
            lineObj.transform.SetParent(overlayParent.transform);
            var lr = lineObj.AddComponent<LineRenderer>();

            lr.positionCount = lastSnap.positions.Length;
            for (int i = 0; i < lastSnap.positions.Length; i++)
            {
                Vector3 p = lastSnap.positions[i];
                p.y += 0.08f;
                lr.SetPosition(i, p);
            }

            lr.startWidth = planPathLineWidth;
            lr.endWidth = planPathLineWidth;
            lr.useWorldSpace = true;
            lr.numCornerVertices = 4;

            if (lineMaterial != null)
                lr.material = new Material(lineMaterial);
            else
                lr.material = new Material(Shader.Find("Sprites/Default"));
            lr.material.color = Color.white;
            lr.startColor = planPathColor;
            lr.endColor = planPathColor;

            planPathLines.Add(lr);

            Debug.Log($"[SessionReview] Rendered final plan path ({lastSnap.positions.Length} pts, " +
                      $"from {snapshots.Count} total snapshots)");
        }

        public void ClearAll()
        {
            if (overlayParent != null)
                Destroy(overlayParent);

            trajectoryLines.Clear();
            arrowLines.Clear();
            stopCircleLines.Clear();
            planPathLines.Clear();
            vlmMarkers.Clear();
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

