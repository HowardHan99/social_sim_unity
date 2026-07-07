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
        [SerializeField] private float trajectoryYOffset = 0.05f;
        [SerializeField] private float pwdTrajectoryExtraYOffset = 0.12f;

        [Header("Direction Arrows")]
        [SerializeField] private bool showDirectionArrows = true;
        [SerializeField] private int arrowSpacing = 8;
        [SerializeField] private float arrowLength = 0.6f;

        [Header("Markers")]
        [SerializeField] private float markerSize = 0.3f;

        [Header("Stop Markers")]
        [SerializeField] private bool showRobotStopCircles = true;
        [SerializeField] private float stopSpeedThreshold = 0.05f;
        [SerializeField] private float minStopDuration = 0.6f;
        [SerializeField] private float stopCircleBaseRadius = 0.1f;
        [SerializeField] private float stopCircleRadiusPerSecond = 0.06f;
        [SerializeField] private float stopCircleMaxRadius = 0.55f;
        [SerializeField] private int stopCircleSegments = 24;
        [SerializeField] private float stopCircleYOffset = 0.05f;
        [SerializeField] private Color stopCircleColor = new Color(1f, 0.95f, 0.2f, 0.85f);

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

        [Header("Signal Annotation Overlay")]
        [SerializeField] private bool showSignalAnnotations = true;
        [SerializeField] private Color lightingLeftAnnotationColor = new Color(1f, 0.72f, 0.2f, 0.98f);
        [SerializeField] private Color lightingRightAnnotationColor = new Color(1f, 0.56f, 0.16f, 0.98f);
        [SerializeField] private Color lightingBothAnnotationColor = new Color(1f, 0.9f, 0.25f, 0.98f);
        [SerializeField] private float annotationStemHeight = 1.1f;
        [SerializeField] private float annotationStemWidth = 0.05f;
        [SerializeField] private float annotationHaloRadius = 0.22f;
        [SerializeField] private float annotationYOffset = 0.12f;

        private GameObject overlayParent;
        private List<LineRenderer> trajectoryLines = new List<LineRenderer>();
        private List<LineRenderer> arrowLines = new List<LineRenderer>();
        private List<LineRenderer> stopCircleLines = new List<LineRenderer>();
        private List<GameObject> stopSpheres = new List<GameObject>();
        private List<LineRenderer> annotationLines = new List<LineRenderer>();
        private List<GameObject> vlmMarkers = new List<GameObject>();
        private List<GameObject> annotationMarkers = new List<GameObject>();
        private List<GameObject> markers = new List<GameObject>();
        private bool isShowing;

        private struct LegendEntry
        {
            public string key;
            public string label;
            public Color color;
            public bool toggleable;
        }
        private List<LegendEntry> legendEntries = new List<LegendEntry>();
        private readonly ReviewPanels.State legendPanel = new ReviewPanels.State();
        private class VisibilityGroup
        {
            public bool visible = true;
            public readonly List<Renderer> renderers = new List<Renderer>();
            public readonly List<GameObject> gameObjects = new List<GameObject>();
        }
        private readonly Dictionary<string, VisibilityGroup> visibilityGroups = new Dictionary<string, VisibilityGroup>();

        public bool IsShowing => isShowing;

        /// <param name="timeOffset">recordingStartTime -- subtract from trial timestamps to get recording timestamps</param>
        public void ShowTrajectories(TrialRecord trial, StateRecording recording, ControlModeLog modeLog, float timeOffset = 0f)
        {
            ShowTrajectories(trial, recording, modeLog, null, null, null, timeOffset);
        }

        public void ShowTrajectories(TrialRecord trial, StateRecording recording, ControlModeLog modeLog,
            List<PlanPathSnapshot> planSnapshots, float timeOffset = 0f)
        {
            ShowTrajectories(trial, recording, modeLog, planSnapshots, null, null, timeOffset);
        }

        public void ShowTrajectories(TrialRecord trial, StateRecording recording, ControlModeLog modeLog,
            List<PlanPathSnapshot> planSnapshots, List<VLMCaptureEvent> vlmCaptures, float timeOffset = 0f)
        {
            ShowTrajectories(trial, recording, modeLog, planSnapshots, vlmCaptures, null, timeOffset);
        }

        public void ShowTrajectories(TrialRecord trial, StateRecording recording, ControlModeLog modeLog,
            List<PlanPathSnapshot> planSnapshots, List<VLMCaptureEvent> vlmCaptures, List<SignalAnnotation> signalAnnotations,
            float timeOffset = 0f)
        {
            ClearAll();
            if (trial == null || recording == null) return;

            // Keep the overlay at world root (NOT parented to this manager). The manager's
            // GameObject can sit under a non-zero / moving parent in some scenes. Trajectory
            // LineRenderers use useWorldSpace=true so they ignore that transform, but parented
            // primitives (stop spheres, start/end markers, annotation markers) would inherit
            // the parent offset and drift away from the real world positions. Rooting the
            // overlay makes every overlay object render in true world space, consistently.
            overlayParent = new GameObject("TrajectoryOverlay");
            overlayParent.transform.SetParent(null, worldPositionStays: false);
            overlayParent.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            overlayParent.transform.localScale = Vector3.one;

            if (recording.timelineDict == null)
                recording.BuildCache();

            float recStart = trial.startTime - timeOffset;
            float recEnd = trial.endTime - timeOffset;

            int pedColorIndex = 0;
            int agentsFound = 0;
            bool stopLegendAdded = false;

            SessionReview.SessionReviewLog.Log($"[SessionReview] Rendering trajectories. Trial has {trial.agentRoles.Count} agents. " +
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

                if (role == AgentRole.Robot)
                    TrimInitialFall(positions, rotations, timestamps);

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
                        legendEntries.Add(new LegendEntry
                        {
                            key = "robot_stops",
                            label = "Robot Stop (radius=duration)",
                            color = stopCircleColor,
                            toggleable = true
                        });
                        stopLegendAdded = true;
                    }
                }

                if (showDirectionArrows && (role == AgentRole.Robot || role == AgentRole.PWDPlayer))
                    CreateDirectionArrows(objectId, positions, rotations, baseColor, role);

                CreateMarker(positions[0], baseColor, objectId + "_start", 1f);
                CreateMarker(positions[positions.Count - 1], baseColor, objectId + "_end", 0.6f);

                string shortName = objectId;
                int lastUnderscore = objectId.LastIndexOf('_');
                if (lastUnderscore > 0 && lastUnderscore < objectId.Length - 1)
                    shortName = objectId.Substring(0, lastUnderscore);
                legendEntries.Add(new LegendEntry
                {
                    key = objectId,
                    label = $"{role}: {shortName}",
                    color = baseColor,
                    toggleable = true
                });
            }

            if (agentsFound == 0)
                Debug.LogWarning($"[MultiAgentTrajectoryRenderer] No agent trajectories found. " +
                    $"Trial window: [{trial.startTime:F1}, {trial.endTime:F1}], " +
                    $"Recording window: [{recStart:F1}, {recEnd:F1}], " +
                    $"Agents in trial: {trial.agentRoles.Count}, " +
                    $"Timelines in recording: {recording.timelineDict.Count}");

            var vlmEvents = vlmCaptures ?? trial.vlmCaptures;
            List<SignalAnnotation> annotationsToRender = signalAnnotations ?? trial.signalAnnotations;
            if ((annotationsToRender == null || annotationsToRender.Count == 0) &&
                vlmEvents != null && vlmEvents.Count > 0)
            {
                annotationsToRender = ConvertLegacyVlmCaptures(vlmEvents);
            }

            if (showSignalAnnotations && annotationsToRender != null && annotationsToRender.Count > 0)
            {
                CreateSignalAnnotations(annotationsToRender);
                AppendAnnotationLegendEntries(annotationsToRender);
            }
            else if (showVLMAnnotations && vlmEvents != null && vlmEvents.Count > 0)
            {
                CreateVLMAnnotations(vlmEvents);
                legendEntries.Add(new LegendEntry
                {
                    key = "vlm_annotations",
                    label = $"VLM Capture ({vlmEvents.Count})",
                    color = vlmAnnotationColor,
                    toggleable = true
                });
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
            RegisterRendererToGroup(id, lr);

            lr.positionCount = positions.Count;
            float yOffset = GetTrajectoryYOffset(role);
            for (int i = 0; i < positions.Count; i++)
            {
                Vector3 p = positions[i];
                p.y += yOffset;
                lr.SetPosition(i, p);
            }

            float width = lineWidth;
            if (role == AgentRole.Robot) width = lineWidth * 1.5f;
            else if (role == AgentRole.PWDPlayer) width = lineWidth * 1.3f;
            lr.startWidth = width;
            lr.endWidth = width;
            lr.useWorldSpace = true;
            lr.numCornerVertices = 5;

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

            SessionReview.SessionReviewLog.Log($"[SessionReview] Line for \"{id}\" ({role}): color={color}, gradient={gradient != null}, points={positions.Count}");
            trajectoryLines.Add(lr);
        }

        private void CreateDirectionArrows(string id, List<Vector3> positions, List<Quaternion> rotations, Color color, AgentRole role)
        {
            float yOffset = GetTrajectoryYOffset(role);
            for (int i = 0; i < positions.Count; i += arrowSpacing)
            {
                var arrowObj = new GameObject("Arrow_" + id + "_" + i);
                arrowObj.transform.SetParent(overlayParent.transform);
                var arrow = arrowObj.AddComponent<LineRenderer>();
                RegisterRendererToGroup(id, arrow);

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
                pos.y += yOffset;
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
            marker.transform.position = position + Vector3.up * (trajectoryYOffset + 0.25f);
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
            string groupKey = GetGroupKeyFromMarkerName(name);
            if (!string.IsNullOrEmpty(groupKey))
                RegisterGameObjectToGroup(groupKey, marker);
        }

        private float GetTrajectoryYOffset(AgentRole role)
        {
            if (role == AgentRole.PWDPlayer)
                return trajectoryYOffset + pwdTrajectoryExtraYOffset;

            return trajectoryYOffset;
        }

        // Removes leading positions where the robot is still falling from its spawn height.
        // We find the lowest Y the robot reaches (settled ground level) and drop all
        // leading frames where it is more than 0.25 m above that level.
        private static void TrimInitialFall(List<Vector3> positions, List<Quaternion> rotations, List<float> timestamps)
        {
            if (positions.Count < 2) return;

            float groundY = float.MaxValue;
            foreach (var p in positions)
                if (p.y < groundY) groundY = p.y;

            const float fallThreshold = 0.25f;
            int firstGrounded = 0;
            for (int i = 0; i < positions.Count; i++)
            {
                if (positions[i].y <= groundY + fallThreshold)
                {
                    firstGrounded = i;
                    break;
                }
            }

            if (firstGrounded <= 0) return;

            positions.RemoveRange(0, firstGrounded);
            rotations.RemoveRange(0, firstGrounded);
            timestamps.RemoveRange(0, firstGrounded);
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
                SessionReview.SessionReviewLog.Log($"[SessionReview] Stop spheres for \"{id}\": {circlesCreated}");

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

            float radius = stopCircleBaseRadius + duration * stopCircleRadiusPerSecond;
            radius = Mathf.Clamp(radius, stopCircleBaseRadius, stopCircleMaxRadius);

            // Sphere sits on the ground surface
            center.y += stopCircleYOffset + radius;

            Color c = Color.Lerp(baseColor, stopCircleColor, 0.8f);
            c.a = 1f;

            var sphereObj = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            sphereObj.name = $"StopSphere_{id}_{startIndex}_{endIndex}";
            sphereObj.transform.SetParent(overlayParent.transform);
            sphereObj.transform.position = center;
            sphereObj.transform.localScale = Vector3.one * radius * 2f;

            var col = sphereObj.GetComponent<Collider>();
            if (col != null) Destroy(col);

            var rend = sphereObj.GetComponent<Renderer>();
            var mat = new Material(Shader.Find("Standard"));
            mat.color = c;
            mat.SetFloat("_Metallic", 0f);
            mat.SetFloat("_Glossiness", 0.25f);
            rend.material = mat;

            RegisterRendererToGroup("robot_stops", rend);
            stopSpheres.Add(sphereObj);

            return true;
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
                RegisterGameObjectToGroup("vlm_annotations", instance);
            }
            else
            {
                CreateVLMDiamond(i, pos, evt.rotation);
            }
            }

            SessionReview.SessionReviewLog.Log($"[SessionReview] VLM annotations: {events.Count}");
        }

        private void CreateSignalAnnotations(List<SignalAnnotation> annotations)
        {
            int vlmCount = 0;
            int lightingCount = 0;
            for (int i = 0; i < annotations.Count; i++)
            {
                SignalAnnotation annotation = annotations[i];
                CreateAnnotationBeacon(i, annotation);
                if (annotation.type == SignalAnnotationType.VlmCapture)
                    vlmCount++;
                else if (IsLightingAnnotation(annotation.type))
                    lightingCount++;
            }

            if (vlmCount > 0)
                SessionReview.SessionReviewLog.Log($"[SessionReview] VLMAnnotation rendered: {vlmCount}");
            if (lightingCount > 0)
                SessionReview.SessionReviewLog.Log($"[SessionReview] LightingAnnotation rendered: {lightingCount}");
        }

        private void CreateAnnotationBeacon(int index, SignalAnnotation annotation)
        {
            Vector3 basePos = annotation.position + Vector3.up * annotationYOffset;
            Color color = GetAnnotationColor(annotation.type);
            float height = annotationStemHeight;
            float haloRadius = annotationHaloRadius;
            float topY = basePos.y + height;

            string objectName = GetAnnotationObjectName(annotation.type, index);
            GameObject parent = new GameObject(objectName);
            parent.transform.SetParent(overlayParent.transform);
            annotationMarkers.Add(parent);
            RegisterGameObjectToGroup(GetAnnotationGroupKey(annotation.type), parent);

            LineRenderer stem = CreateAnnotationLineRenderer(parent.transform, "Stem", color, annotationStemWidth, annotationStemWidth * 0.85f);
            stem.positionCount = 2;
            stem.SetPosition(0, basePos);
            stem.SetPosition(1, new Vector3(basePos.x, topY, basePos.z));

            Vector3 center = new Vector3(basePos.x, topY, basePos.z);
            LineRenderer halo = CreateAnnotationLineRenderer(parent.transform, "Halo", color, annotationStemWidth * 0.8f, annotationStemWidth * 0.8f);
            halo.loop = true;
            halo.positionCount = 4;
            halo.SetPosition(0, center + new Vector3(0f, 0f, haloRadius));
            halo.SetPosition(1, center + new Vector3(haloRadius, 0f, 0f));
            halo.SetPosition(2, center + new Vector3(0f, 0f, -haloRadius));
            halo.SetPosition(3, center + new Vector3(-haloRadius, 0f, 0f));

            LineRenderer cap = CreateAnnotationLineRenderer(parent.transform, "Cap", color, annotationStemWidth * 0.65f, 0f);
            cap.positionCount = 2;
            cap.SetPosition(0, center);
            cap.SetPosition(1, center + GetAnnotationDirection(annotation) * haloRadius * 1.75f);

            CreateTypeAccent(parent.transform, center, annotation, color, haloRadius);
        }

        private void CreateTypeAccent(Transform parent, Vector3 center, SignalAnnotation annotation, Color color, float haloRadius)
        {
            switch (annotation.type)
            {
                case SignalAnnotationType.LightingLeft:
                    CreateAccentWing(parent, center, color, Vector3.left, haloRadius);
                    break;
                case SignalAnnotationType.LightingRight:
                    CreateAccentWing(parent, center, color, Vector3.right, haloRadius);
                    break;
                case SignalAnnotationType.LightingBoth:
                    CreateAccentWing(parent, center, color, Vector3.left, haloRadius);
                    CreateAccentWing(parent, center, color, Vector3.right, haloRadius);
                    break;
                default:
                    CreateAccentCross(parent, center, color, haloRadius);
                    break;
            }
        }

        private void CreateAccentWing(Transform parent, Vector3 center, Color color, Vector3 side, float haloRadius)
        {
            LineRenderer wing = CreateAnnotationLineRenderer(parent, side.x < 0f ? "Wing_L" : "Wing_R",
                color, annotationStemWidth * 0.6f, annotationStemWidth * 0.25f);
            wing.positionCount = 2;
            wing.SetPosition(0, center + side * haloRadius * 0.35f);
            wing.SetPosition(1, center + side * haloRadius * 1.5f + Vector3.up * haloRadius * 0.25f);
        }

        private void CreateAccentCross(Transform parent, Vector3 center, Color color, float haloRadius)
        {
            LineRenderer cross = CreateAnnotationLineRenderer(parent, "Cross", color,
                annotationStemWidth * 0.45f, annotationStemWidth * 0.45f);
            cross.positionCount = 2;
            cross.SetPosition(0, center + new Vector3(-haloRadius * 0.6f, 0f, -haloRadius * 0.6f));
            cross.SetPosition(1, center + new Vector3(haloRadius * 0.6f, 0f, haloRadius * 0.6f));

            LineRenderer cross2 = CreateAnnotationLineRenderer(parent, "Cross_2", color,
                annotationStemWidth * 0.45f, annotationStemWidth * 0.45f);
            cross2.positionCount = 2;
            cross2.SetPosition(0, center + new Vector3(-haloRadius * 0.6f, 0f, haloRadius * 0.6f));
            cross2.SetPosition(1, center + new Vector3(haloRadius * 0.6f, 0f, -haloRadius * 0.6f));
        }

        private LineRenderer CreateAnnotationLineRenderer(Transform parent, string name, Color color, float startWidth, float endWidth)
        {
            GameObject obj = new GameObject(name);
            obj.transform.SetParent(parent);
            LineRenderer lr = obj.AddComponent<LineRenderer>();
            lr.useWorldSpace = true;
            lr.startWidth = startWidth;
            lr.endWidth = endWidth;
            lr.numCornerVertices = 4;
            ApplyLineMaterial(lr, color);
            annotationLines.Add(lr);
            return lr;
        }

        private Vector3 GetAnnotationDirection(SignalAnnotation annotation)
        {
            Vector3 forward = annotation.rotation * Vector3.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.001f)
                forward = Vector3.forward;
            return forward.normalized;
        }

        private Color GetAnnotationColor(SignalAnnotationType type)
        {
            switch (type)
            {
                case SignalAnnotationType.VlmCapture:
                    return vlmAnnotationColor;
                case SignalAnnotationType.LightingLeft:
                    return lightingLeftAnnotationColor;
                case SignalAnnotationType.LightingRight:
                    return lightingRightAnnotationColor;
                case SignalAnnotationType.LightingBoth:
                    return lightingBothAnnotationColor;
                default:
                    return Color.white;
            }
        }

        private bool IsLightingAnnotation(SignalAnnotationType type)
        {
            return type == SignalAnnotationType.LightingLeft ||
                   type == SignalAnnotationType.LightingRight ||
                   type == SignalAnnotationType.LightingBoth;
        }

        private string GetAnnotationObjectName(SignalAnnotationType type, int index)
        {
            if (type == SignalAnnotationType.VlmCapture)
                return $"VLMAnnotation_{index}";

            if (IsLightingAnnotation(type))
                return $"LightingAnnotation_{index}";

            return $"SignalAnnotation_{index}";
        }

        private void AppendAnnotationLegendEntries(List<SignalAnnotation> annotations)
        {
            Dictionary<SignalAnnotationType, int> counts = new Dictionary<SignalAnnotationType, int>();
            foreach (SignalAnnotation annotation in annotations)
            {
                if (!counts.ContainsKey(annotation.type))
                    counts[annotation.type] = 0;
                counts[annotation.type]++;
            }

            foreach (var pair in counts)
            {
                legendEntries.Add(new LegendEntry
                {
                    key = GetAnnotationGroupKey(pair.Key),
                    label = $"{GetAnnotationLegendLabel(pair.Key)} ({pair.Value})",
                    color = GetAnnotationColor(pair.Key),
                    toggleable = true
                });
            }
        }

        private string GetAnnotationLegendLabel(SignalAnnotationType type)
        {
            switch (type)
            {
                case SignalAnnotationType.VlmCapture:
                    return "VLM Annotation";
                case SignalAnnotationType.LightingLeft:
                    return "Light Signal Left";
                case SignalAnnotationType.LightingRight:
                    return "Light Signal Right";
                case SignalAnnotationType.LightingBoth:
                    return "Light Signal Both";
                default:
                    return "Signal Annotation";
            }
        }

        private string GetAnnotationGroupKey(SignalAnnotationType type)
        {
            switch (type)
            {
                case SignalAnnotationType.VlmCapture:
                    return "annotation_vlm";
                case SignalAnnotationType.LightingLeft:
                    return "annotation_light_left";
                case SignalAnnotationType.LightingRight:
                    return "annotation_light_right";
                case SignalAnnotationType.LightingBoth:
                    return "annotation_light_both";
                default:
                    return "annotation_misc";
            }
        }

        private List<SignalAnnotation> ConvertLegacyVlmCaptures(List<VLMCaptureEvent> events)
        {
            var annotations = new List<SignalAnnotation>();
            if (events == null)
                return annotations;

            foreach (VLMCaptureEvent evt in events)
            {
                annotations.Add(new SignalAnnotation
                {
                    timestamp = evt.timestamp,
                    agentId = evt.agentId,
                    type = SignalAnnotationType.VlmCapture,
                    position = evt.position,
                    rotation = evt.rotation,
                    label = "VLM Capture",
                    metadata = string.Empty
                });
            }

            return annotations;
        }

        private void CreateVLMDiamond(int index, Vector3 center, Quaternion rotation)
        {
            float s = vlmMarkerSize;
            var parent = new GameObject($"VLMDiamond_{index}");
            parent.transform.SetParent(overlayParent.transform);
            RegisterGameObjectToGroup("vlm_annotations", parent);

            Vector3 top = center + new Vector3(0f, 0f, s);
            Vector3 right = center + new Vector3(s, 0f, 0f);
            Vector3 bottom = center + new Vector3(0f, 0f, -s);
            Vector3 left = center + new Vector3(-s, 0f, 0f);

            var outlineObj = new GameObject("Outline");
            outlineObj.transform.SetParent(parent.transform);
            var outline = outlineObj.AddComponent<LineRenderer>();
            RegisterRendererToGroup("vlm_annotations", outline);
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
            RegisterRendererToGroup("vlm_annotations", lrH);
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
            RegisterRendererToGroup("vlm_annotations", lrV);
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
                RegisterRendererToGroup("vlm_annotations", dirLr);
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

        public void SetAllTrajectoryVisibility(bool visible)
        {
            foreach (var key in new List<string>(visibilityGroups.Keys))
                SetGroupVisibility(key, visible);
        }

        /// <summary>
        /// Registers a toggleable legend row + visibility group for a line owned by another
        /// system (e.g. RewindController's live "active plan" line), so "Show All"/"Hide All"
        /// and the per-row toggle also govern it. The owner must query IsExternalGroupVisible(key)
        /// before drawing, because it may rebuild/re-enable its renderer every frame.
        /// </summary>
        public void RegisterExternalLegendGroup(string key, string label, Color color, bool initiallyVisible = true)
        {
            if (string.IsNullOrEmpty(key))
                return;

            GetOrCreateVisibilityGroup(key).visible = initiallyVisible;

            for (int i = 0; i < legendEntries.Count; i++)
            {
                if (legendEntries[i].key == key)
                    return; // already registered (e.g. re-entering review)
            }

            legendEntries.Add(new LegendEntry
            {
                key = key,
                label = label,
                color = color,
                toggleable = true
            });
        }

        /// <summary>Visibility of an externally-owned group (see RegisterExternalLegendGroup).</summary>
        public bool IsExternalGroupVisible(string key)
        {
            return IsGroupVisible(key);
        }

        private void RegisterRendererToGroup(string key, Renderer renderer)
        {
            if (renderer == null || string.IsNullOrEmpty(key))
                return;

            VisibilityGroup group = GetOrCreateVisibilityGroup(key);
            group.renderers.Add(renderer);
            renderer.enabled = group.visible;
        }

        private void RegisterGameObjectToGroup(string key, GameObject go)
        {
            if (go == null || string.IsNullOrEmpty(key))
                return;

            VisibilityGroup group = GetOrCreateVisibilityGroup(key);
            group.gameObjects.Add(go);
            go.SetActive(group.visible);
        }

        private VisibilityGroup GetOrCreateVisibilityGroup(string key)
        {
            if (!visibilityGroups.TryGetValue(key, out VisibilityGroup group))
            {
                group = new VisibilityGroup();
                visibilityGroups[key] = group;
            }

            return group;
        }

        private void ToggleGroupVisibility(string key)
        {
            if (string.IsNullOrEmpty(key) || !visibilityGroups.TryGetValue(key, out VisibilityGroup group))
                return;

            SetGroupVisibility(key, !group.visible);
        }

        private void SetGroupVisibility(string key, bool visible)
        {
            if (string.IsNullOrEmpty(key) || !visibilityGroups.TryGetValue(key, out VisibilityGroup group))
                return;

            group.visible = visible;

            foreach (Renderer renderer in group.renderers)
            {
                if (renderer != null)
                    renderer.enabled = visible;
            }

            foreach (GameObject go in group.gameObjects)
            {
                if (go != null)
                    go.SetActive(visible);
            }
        }

        private bool IsGroupVisible(string key)
        {
            return string.IsNullOrEmpty(key) ||
                   !visibilityGroups.TryGetValue(key, out VisibilityGroup group) ||
                   group.visible;
        }

        private static string GetGroupKeyFromMarkerName(string name)
        {
            const string startSuffix = "_start";
            const string endSuffix = "_end";

            if (string.IsNullOrEmpty(name))
                return null;

            if (name.EndsWith(startSuffix))
                return name.Substring(0, name.Length - startSuffix.Length);

            if (name.EndsWith(endSuffix))
                return name.Substring(0, name.Length - endSuffix.Length);

            return null;
        }

        public void ClearAll()
        {
            if (overlayParent != null)
                Destroy(overlayParent);

            trajectoryLines.Clear();
            arrowLines.Clear();
            stopCircleLines.Clear();
            stopSpheres.Clear();
            annotationLines.Clear();
            vlmMarkers.Clear();
            annotationMarkers.Clear();
            markers.Clear();
            legendEntries.Clear();
            visibilityGroups.Clear();
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

            // Nominal row/controls heights just to size the default window; the actual
            // heights are computed responsively in DrawLegendBody from FontScale.
            float lineH = 40f;
            float controlsH = 40f;
            float boxW = 400f;
            float defaultH = ReviewPanels.TitleH + controlsH + 8f
                + Mathf.Min(Mathf.Max(legendEntries.Count, 1), 8) * lineH + 12f;
            float dx = Screen.width - boxW - 20f;
            float dy = Mathf.Max(10f, Screen.height - defaultH - 140f);
            Rect defaultRect = new Rect(dx, dy, boxW, defaultH);

            if (ReviewPanels.Begin(legendPanel, this, "Legend", defaultRect, out Rect content))
                DrawLegendBody(content);
            ReviewPanels.End(legendPanel);
        }

        private void DrawLegendBody(Rect content)
        {
            // Responsive: font, row height and swatch all scale with the panel size.
            float scale = legendPanel.FontScale;
            int fontSize = Mathf.RoundToInt(17f * scale);
            float rowLineH = fontSize + 20f;
            float controlsH = fontSize + 22f;
            float swatch = fontSize + 2f;

            var buttonStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = fontSize,
                alignment = TextAnchor.MiddleCenter
            };
            var rowTextStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = fontSize,
                alignment = TextAnchor.MiddleLeft,
                clipping = TextClipping.Clip,
                padding = new RectOffset(0, 0, 0, 0),
                normal = { textColor = Color.white }
            };

            float controlsWidth = (content.width - 8f) * 0.5f;
            if (GUI.Button(new Rect(content.x, content.y, controlsWidth, controlsH - 6f), "Show All", buttonStyle))
                SetAllTrajectoryVisibility(true);
            if (GUI.Button(new Rect(content.x + controlsWidth + 8f, content.y, controlsWidth, controlsH - 6f), "Hide All", buttonStyle))
                SetAllTrajectoryVisibility(false);

            // Scrollable list so the panel stays usable at any resized height.
            Rect viewport = new Rect(content.x, content.y + controlsH, content.width, Mathf.Max(0f, content.yMax - (content.y + controlsH)));
            float innerW = content.width - 18f;
            float innerH = legendEntries.Count * rowLineH;
            legendPanel.scroll = GUI.BeginScrollView(viewport, legendPanel.scroll, new Rect(0f, 0f, innerW, innerH));

            for (int i = 0; i < legendEntries.Count; i++)
            {
                var entry = legendEntries[i];
                float ly = i * rowLineH;
                bool visible = IsGroupVisible(entry.key);
                Rect rowRect = new Rect(0f, ly, innerW, rowLineH - 4f);

                GUI.backgroundColor = visible
                    ? new Color(0.08f, 0.1f, 0.14f, 0.92f)
                    : new Color(0.05f, 0.05f, 0.06f, 0.75f);
                GUI.Box(rowRect, "");

                Color prev = GUI.backgroundColor;
                GUI.backgroundColor = visible
                    ? entry.color
                    : new Color(entry.color.r * 0.35f, entry.color.g * 0.35f, entry.color.b * 0.35f, 0.9f);
                float swatchY = rowRect.y + (rowRect.height - swatch) * 0.5f;
                GUI.Box(new Rect(rowRect.x + 8f, swatchY, swatch, swatch), "", GUI.skin.button);
                GUI.backgroundColor = prev;

                rowTextStyle.normal.textColor = visible
                    ? Color.white
                    : new Color(0.66f, 0.69f, 0.73f, 1f);
                string label = visible ? entry.label : $"{entry.label}  [hidden]";
                Rect labelRect = new Rect(rowRect.x + swatch + 16f, rowRect.y + 1f, rowRect.width - swatch - 24f, rowRect.height - 2f);
                GUI.Label(labelRect, label, rowTextStyle);

                // Whole-row click toggles visibility (covers the colour swatch too).
                if (entry.toggleable &&
                    Event.current.type == EventType.MouseDown &&
                    rowRect.Contains(Event.current.mousePosition))
                {
                    ToggleGroupVisibility(entry.key);
                    Event.current.Use();
                }
            }

            GUI.EndScrollView();
            GUI.backgroundColor = Color.white;
        }
    }
}
