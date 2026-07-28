using System;
using System.Collections.Generic;
using System.IO;
using Rerun;
using UnityEngine;

namespace SessionReview
{
    [Serializable]
    public class ReviewExportSettings
    {
        public float paddingX = 12f;
        public float paddingZ = 12f;
        public float offsetX = 0f;
        public float offsetZ = 0f;
        public bool exportImage = true;
        public int imageMaxResolution = 1024;
    }

    [Serializable]
    public class ReviewExportBounds
    {
        public Vector2 centerXZ;
        public Vector2 sizeXZ;
        public float minX;
        public float maxX;
        public float minZ;
        public float maxZ;
    }

    [Serializable]
    public class ReviewExportImageInfo
    {
        public string fileName;
        public int width;
        public int height;
    }

    [Serializable]
    public class ReviewExportFootprintPoint
    {
        public float x;
        public float z;
    }

    [Serializable]
    public class ReviewExportObject
    {
        public string name;
        public string objectName;
        public string sourceObjectName;
        public string categoryPath;
        public string hierarchyPath;
        public bool isGroup;
        public string semanticType;
        public string colliderType;
        public string shape;
        public Vector3 center;
        public Vector3 size;
        public Vector3 rotationEuler;
        public float radius;
        public float height;
        public Vector3 boundsMin;
        public Vector3 boundsMax;
        public List<ReviewExportFootprintPoint> footprintXZ = new List<ReviewExportFootprintPoint>();
    }

    [Serializable]
    public class ReviewExportTrajectorySample
    {
        public float timestamp;
        public Vector3 position;
        public Quaternion rotation;
    }

    [Serializable]
    public class ReviewExportAgent
    {
        public string objectId;
        public string role;
        public string displayName;
        public Vector3 startPosition;
        public Vector3 endPosition;
        public Vector3 goalPosition;
        public bool goalIsInferred;
        public List<ReviewExportTrajectorySample> samples = new List<ReviewExportTrajectorySample>();
        public List<ReviewExportTrajectorySample> samplesInsideBounds = new List<ReviewExportTrajectorySample>();
    }

    [Serializable]
    public class ReviewExportData
    {
        public string sceneName;
        public string exportTimestamp;
        public string trialName;
        public ushort trialNumber;
        public float trialStartTime;
        public float trialEndTime;
        public ReviewExportBounds bounds;
        public ReviewExportImageInfo image;
        public ReviewExportImageInfo trajectoryImage;
        public List<ReviewExportObject> objects = new List<ReviewExportObject>();
        public List<ReviewExportAgent> agents = new List<ReviewExportAgent>();
    }

    public static class ReviewRoiExporter
    {
        public static bool TryComputeTrajectoryEnvelope(TrialRecord trial, StateRecording recording, float recordingTimeOffset, out Bounds bounds)
        {
            bounds = default;
            if (trial == null || recording == null)
                return false;

            if (recording.timelineDict == null)
                recording.BuildCache();

            float recStart = trial.startTime - recordingTimeOffset;
            float recEnd = trial.endTime - recordingTimeOffset;

            bool hasPoint = false;
            Vector3 min = Vector3.zero;
            Vector3 max = Vector3.zero;

            foreach (var roleEntry in trial.agentRoles)
            {
                if (!recording.timelineDict.TryGetValue(roleEntry.objectId, out ObjectStateTimeline timeline) || timeline.states == null)
                    continue;

                foreach (var state in MultiAgentTrajectoryRenderer.CollectTrialStates(timeline, roleEntry.role, recStart, recEnd))
                {
                    if (!hasPoint)
                    {
                        min = state.position;
                        max = state.position;
                        hasPoint = true;
                    }
                    else
                    {
                        min = Vector3.Min(min, state.position);
                        max = Vector3.Max(max, state.position);
                    }
                }
            }

            if (!hasPoint)
                return false;

            Vector3 center = (min + max) * 0.5f;
            Vector3 size = max - min;
            size.x = Mathf.Max(size.x, 1f);
            size.z = Mathf.Max(size.z, 1f);
            size.y = Mathf.Max(size.y, 1f);
            bounds = new Bounds(center, size);
            return true;
        }

        public static Bounds ApplySettings(Bounds baseBounds, ReviewExportSettings settings)
        {
            Vector3 center = baseBounds.center + new Vector3(settings.offsetX, 0f, settings.offsetZ);
            Vector3 size = baseBounds.size;
            size.x = Mathf.Max(1f, size.x + settings.paddingX * 2f);
            size.z = Mathf.Max(1f, size.z + settings.paddingZ * 2f);
            size.y = Mathf.Max(size.y, 1f);
            return new Bounds(center, size);
        }

        public static string ExportTrialRoi(TrialRecord trial, StateRecording recording, float recordingTimeOffset, ReviewExportSettings settings)
        {
            return ExportTrialRoi(trial, recording, recordingTimeOffset, settings, null);
        }

        /// <summary>
        /// Export the trial ROI into <paramref name="outputFolder"/>. Pass null to create a
        /// fresh timestamped folder under SessionLogs/ReviewExports (manual F7 export);
        /// pass the trial's own save folder to auto-save alongside trial_info.json, where
        /// re-exports (e.g. FinalizeLatestTrial) overwrite the fixed file names in place.
        /// </summary>
        public static string ExportTrialRoi(TrialRecord trial, StateRecording recording, float recordingTimeOffset, ReviewExportSettings settings, string outputFolder)
        {
            if (trial == null || recording == null)
                throw new ArgumentNullException("trial/recording");

            if (recording.timelineDict == null)
                recording.BuildCache();

            if (!TryComputeTrajectoryEnvelope(trial, recording, recordingTimeOffset, out Bounds envelope))
                throw new InvalidOperationException("Could not compute trajectory envelope for the selected trial.");

            Bounds roi = ApplySettings(envelope, settings);
            string exportFolder = string.IsNullOrEmpty(outputFolder) ? CreateExportFolder(trial) : outputFolder;
            if (!Directory.Exists(exportFolder))
                Directory.CreateDirectory(exportFolder);

            var data = new ReviewExportData
            {
                sceneName = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name,
                exportTimestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"),
                trialName = trial.trialName,
                trialNumber = trial.trialNumber,
                trialStartTime = trial.startTime,
                trialEndTime = trial.endTime,
                bounds = ToBoundsData(roi)
            };

            float recStart = trial.startTime - recordingTimeOffset;
            float recEnd = trial.endTime - recordingTimeOffset;

            CollectObjects(data.objects, roi);
            CollectAgents(data.agents, trial, recording, recStart, recEnd, roi);

            if (settings.exportImage)
            {
                ExportTopDownImages(roi, settings.imageMaxResolution, exportFolder, data);
            }

            string jsonPath = Path.Combine(exportFolder, "review_roi_export.json");
            File.WriteAllText(jsonPath, JsonUtility.ToJson(data, true));
            SessionReview.SessionReviewLog.Log($"[SessionReview] Review ROI export saved to: {exportFolder}");
            return exportFolder;
        }

        private static void CollectObjects(List<ReviewExportObject> output, Bounds roi)
        {
            var environment = GameObject.Find("/Environment");
            if (environment == null)
                return;

            CollectEnvironmentGroups(output, environment.transform, roi);

            var colliders = environment.GetComponentsInChildren<Collider>(true);
            foreach (var collider in colliders)
            {
                if (!IsExportableCollider(collider))
                    continue;

                if (!IntersectsRoi(collider.bounds, roi))
                    continue;

                Transform sourceTransform = ResolveSourceTransform(collider);
                if (sourceTransform == null)
                    continue;

                output.Add(BuildObjectExport(collider, sourceTransform));
            }
        }

        private static void CollectEnvironmentGroups(List<ReviewExportObject> output, Transform environmentRoot, Bounds roi)
        {
            foreach (Transform child in environmentRoot)
            {
                if (child == null || !child.gameObject.activeInHierarchy)
                    continue;

                Bounds? aggregateBounds = TryComputeAggregateBounds(child, roi);
                if (!aggregateBounds.HasValue)
                    continue;

                Bounds bounds = aggregateBounds.Value;
                output.Add(new ReviewExportObject
                {
                    name = child.name,
                    objectName = child.name,
                    sourceObjectName = child.name,
                    categoryPath = string.Empty,
                    hierarchyPath = GetHierarchyPath(child),
                    isGroup = true,
                    semanticType = InferSemanticType(child.name),
                    colliderType = "Group",
                    shape = "group_bounds",
                    center = bounds.center,
                    size = bounds.size,
                    rotationEuler = Vector3.zero,
                    boundsMin = bounds.min,
                    boundsMax = bounds.max,
                    footprintXZ = BuildBoundsFootprint(bounds)
                });
            }
        }

        private static void CollectAgents(List<ReviewExportAgent> output, TrialRecord trial, StateRecording recording, float recStart, float recEnd, Bounds roi)
        {
            foreach (var roleEntry in trial.agentRoles)
            {
                if (!recording.timelineDict.TryGetValue(roleEntry.objectId, out ObjectStateTimeline timeline) || timeline.states == null)
                    continue;

                var exportAgent = new ReviewExportAgent
                {
                    objectId = roleEntry.objectId,
                    role = roleEntry.role.ToString(),
                    displayName = ResolveAgentDisplayName(roleEntry.objectId)
                };

                foreach (var state in MultiAgentTrajectoryRenderer.CollectTrialStates(timeline, roleEntry.role, recStart, recEnd))
                {
                    var sample = new ReviewExportTrajectorySample
                    {
                        timestamp = state.timestamp,
                        position = state.position,
                        rotation = state.rotation
                    };

                    exportAgent.samples.Add(sample);
                    if (ContainsPoint(roi, state.position))
                        exportAgent.samplesInsideBounds.Add(sample);
                }

                if (exportAgent.samples.Count == 0)
                    continue;

                exportAgent.startPosition = exportAgent.samples[0].position;
                exportAgent.endPosition = exportAgent.samples[exportAgent.samples.Count - 1].position;
                exportAgent.goalPosition = ResolveGoalPosition(trial, roleEntry.role, exportAgent.endPosition, out bool goalIsInferred);
                exportAgent.goalIsInferred = goalIsInferred;

                output.Add(exportAgent);
            }
        }

        private static ReviewExportBounds ToBoundsData(Bounds roi)
        {
            return new ReviewExportBounds
            {
                centerXZ = new Vector2(roi.center.x, roi.center.z),
                sizeXZ = new Vector2(roi.size.x, roi.size.z),
                minX = roi.min.x,
                maxX = roi.max.x,
                minZ = roi.min.z,
                maxZ = roi.max.z
            };
        }

        private static ReviewExportImageInfo BuildImageInfo(Texture2D texture, string fileName)
        {
            return new ReviewExportImageInfo
            {
                fileName = fileName,
                width = texture.width,
                height = texture.height
            };
        }

        private static bool IsExportableCollider(Collider collider)
        {
            if (collider == null || !collider.enabled || collider.isTrigger)
                return false;

            if (!collider.gameObject.activeInHierarchy)
                return false;

            if (collider.GetComponentInParent<IVI.INavigable>() != null)
                return false;

            if (collider.GetComponentInParent<SEAN.Scenario.Robot>() != null)
                return false;

            string path = GetHierarchyPath(collider.transform);
            if (path.Contains("/PedestrianControl/Graph/Agents"))
                return false;

            if (path.ToLowerInvariant().Contains("disabled"))
                return false;

            Transform sourceTransform = ResolveSourceTransform(collider);
            if (sourceTransform == null || !HasVisibleRendererInHierarchy(sourceTransform))
                return false;

            return true;
        }

        private static bool IntersectsRoi(Bounds objectBounds, Bounds roi)
        {
            return !(objectBounds.max.x < roi.min.x ||
                     objectBounds.min.x > roi.max.x ||
                     objectBounds.max.z < roi.min.z ||
                     objectBounds.min.z > roi.max.z);
        }

        private static bool ContainsPoint(Bounds roi, Vector3 point)
        {
            return point.x >= roi.min.x && point.x <= roi.max.x &&
                   point.z >= roi.min.z && point.z <= roi.max.z;
        }

        private static ReviewExportObject BuildObjectExport(Collider collider, Transform sourceTransform)
        {
            string sourceObjectName = sourceTransform.gameObject.name;
            string categoryPath = GetCategoryPath(sourceTransform);

            var data = new ReviewExportObject
            {
                name = sourceObjectName,
                objectName = sourceObjectName,
                sourceObjectName = sourceObjectName,
                categoryPath = categoryPath,
                hierarchyPath = GetHierarchyPath(sourceTransform),
                isGroup = false,
                semanticType = InferSemanticType(sourceObjectName),
                colliderType = collider.GetType().Name,
                rotationEuler = collider.transform.eulerAngles,
                boundsMin = collider.bounds.min,
                boundsMax = collider.bounds.max
            };

            if (collider is BoxCollider box)
            {
                data.shape = "box";
                data.center = collider.transform.TransformPoint(box.center);
                data.size = ScaleAbs(box.size, collider.transform.lossyScale);
                AddBoxFootprint(data.footprintXZ, box, collider.transform);
            }
            else if (collider is SphereCollider sphere)
            {
                data.shape = "sphere";
                data.center = collider.transform.TransformPoint(sphere.center);
                data.radius = sphere.radius * MaxAbs(collider.transform.lossyScale.x, collider.transform.lossyScale.z);
                data.size = new Vector3(data.radius * 2f, data.radius * 2f, data.radius * 2f);
            }
            else if (collider is CapsuleCollider capsule)
            {
                data.shape = "capsule";
                data.center = collider.transform.TransformPoint(capsule.center);
                data.radius = capsule.radius * MaxAbs(collider.transform.lossyScale.x, collider.transform.lossyScale.z);
                data.height = capsule.height * Mathf.Abs(collider.transform.lossyScale.y);
                data.size = new Vector3(data.radius * 2f, data.height, data.radius * 2f);
            }
            else if (collider is MeshCollider)
            {
                data.shape = "mesh_bounds";
                data.center = collider.bounds.center;
                data.size = collider.bounds.size;
                AddBoundsFootprint(data.footprintXZ, collider.bounds);
            }
            else
            {
                data.shape = "bounds";
                data.center = collider.bounds.center;
                data.size = collider.bounds.size;
                AddBoundsFootprint(data.footprintXZ, collider.bounds);
            }

            if (data.footprintXZ.Count == 0)
                AddBoundsFootprint(data.footprintXZ, collider.bounds);

            return data;
        }

        private static void AddBoxFootprint(List<ReviewExportFootprintPoint> footprint, BoxCollider box, Transform transform)
        {
            Vector3 half = box.size * 0.5f;
            Vector3 center = box.center;
            Vector3[] corners =
            {
                new Vector3(center.x - half.x, center.y, center.z - half.z),
                new Vector3(center.x - half.x, center.y, center.z + half.z),
                new Vector3(center.x + half.x, center.y, center.z + half.z),
                new Vector3(center.x + half.x, center.y, center.z - half.z)
            };

            foreach (var corner in corners)
            {
                Vector3 world = transform.TransformPoint(corner);
                footprint.Add(new ReviewExportFootprintPoint { x = world.x, z = world.z });
            }
        }

        private static void AddBoundsFootprint(List<ReviewExportFootprintPoint> footprint, Bounds bounds)
        {
            footprint.Add(new ReviewExportFootprintPoint { x = bounds.min.x, z = bounds.min.z });
            footprint.Add(new ReviewExportFootprintPoint { x = bounds.min.x, z = bounds.max.z });
            footprint.Add(new ReviewExportFootprintPoint { x = bounds.max.x, z = bounds.max.z });
            footprint.Add(new ReviewExportFootprintPoint { x = bounds.max.x, z = bounds.min.z });
        }

        private static List<ReviewExportFootprintPoint> BuildBoundsFootprint(Bounds bounds)
        {
            var footprint = new List<ReviewExportFootprintPoint>();
            AddBoundsFootprint(footprint, bounds);
            return footprint;
        }

        private static Vector3 ScaleAbs(Vector3 value, Vector3 scale)
        {
            return new Vector3(
                value.x * Mathf.Abs(scale.x),
                value.y * Mathf.Abs(scale.y),
                value.z * Mathf.Abs(scale.z));
        }

        private static float MaxAbs(float a, float b)
        {
            return Mathf.Max(Mathf.Abs(a), Mathf.Abs(b));
        }

        private static string ResolveAgentDisplayName(string objectId)
        {
            Transform transform = SessionReviewManager.Instance != null
                ? SessionReviewManager.Instance.ResolveTransformForObjectId(objectId)
                : null;
            return transform != null ? transform.gameObject.name : objectId;
        }

        private static Vector3 ResolveGoalPosition(TrialRecord trial, AgentRole role, Vector3 fallback, out bool inferred)
        {
            if (trial != null)
            {
                if (role == AgentRole.Robot && trial.hasRobotGoalPosition)
                {
                    inferred = false;
                    return trial.robotGoalPosition;
                }

                if (role == AgentRole.PWDPlayer && trial.hasPlayerGoalPosition)
                {
                    inferred = false;
                    return trial.playerGoalPosition;
                }
            }

            inferred = true;
            return fallback;
        }

        private static string InferSemanticType(string objectName)
        {
            string lower = objectName.ToLowerInvariant();
            if (lower.Contains("bench")) return "bench";
            if (lower.Contains("hydrant")) return "hydrant";
            if (lower.Contains("lamp") || lower.Contains("light") || lower.Contains("pole")) return "pole";
            if (lower.Contains("stairs") || lower.Contains("stair")) return "stairs";
            if (lower.Contains("door") || lower.Contains("entrance")) return "door";
            if (lower.Contains("road") || lower.Contains("street")) return "road";
            if (lower.Contains("sidewalk") || lower.Contains("walk")) return "sidewalk";
            if (lower.Contains("trash") || lower.Contains("garbage") || lower.Contains("bin")) return "trash";
            if (lower.Contains("building") || lower.Contains("wall") || lower.Contains("facade")) return "building";
            if (lower.Contains("tree") || lower.Contains("plant") || lower.Contains("planter")) return "vegetation";
            return "static_object";
        }

        private static string GetHierarchyPath(Transform transform)
        {
            if (transform == null)
                return string.Empty;

            string path = transform.name;
            while (transform.parent != null)
            {
                transform = transform.parent;
                path = transform.name + "/" + path;
            }
            return path;
        }

        private static Transform ResolveSourceTransform(Collider collider)
        {
            if (collider == null)
                return null;

            Transform current = collider.transform;
            Transform nearestMeaningful = null;
            Transform fallback = current;

            while (current != null)
            {
                if (!IsGenericColliderObjectName(current.name))
                {
                    if (nearestMeaningful == null)
                        nearestMeaningful = current;

                    if (HasVisibleRendererInHierarchy(current))
                        return current;
                }

                if (current.parent == null || current.parent.name == "Environment")
                    break;

                fallback = current.parent;
                current = current.parent;
            }

            return nearestMeaningful != null ? nearestMeaningful : fallback;
        }

        private static bool IsGenericColliderObjectName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return true;

            string lower = name.ToLowerInvariant();
            return lower == "collider" ||
                   lower == "meshcollider" ||
                   lower == "boxcollider" ||
                   lower == "spherecollider" ||
                   lower == "capsulecollider" ||
                   lower == "mesh collider" ||
                   lower == "box collider" ||
                   lower == "sphere collider" ||
                   lower == "capsule collider";
        }

        private static bool HasVisibleRendererInHierarchy(Transform sourceTransform)
        {
            if (sourceTransform == null)
                return false;

            Renderer[] renderers = sourceTransform.GetComponentsInChildren<Renderer>(false);
            foreach (var renderer in renderers)
            {
                if (renderer != null && renderer.enabled && renderer.gameObject.activeInHierarchy)
                    return true;
            }

            return false;
        }

        private static Bounds? TryComputeAggregateBounds(Transform root, Bounds roi)
        {
            Collider[] colliders = root.GetComponentsInChildren<Collider>(true);
            bool hasBounds = false;
            Bounds aggregate = default;

            foreach (var collider in colliders)
            {
                if (!IsExportableCollider(collider))
                    continue;

                if (!IntersectsRoi(collider.bounds, roi))
                    continue;

                if (!hasBounds)
                {
                    aggregate = collider.bounds;
                    hasBounds = true;
                }
                else
                {
                    aggregate.Encapsulate(collider.bounds);
                }
            }

            if (hasBounds)
                return aggregate;

            return null;
        }

        private static string GetCategoryPath(Transform transform)
        {
            if (transform == null)
                return string.Empty;

            string fullPath = GetHierarchyPath(transform);
            const string prefix = "Environment/";
            if (!fullPath.StartsWith(prefix))
                return string.Empty;

            string relativePath = fullPath.Substring(prefix.Length);
            int lastSlash = relativePath.LastIndexOf('/');
            if (lastSlash < 0)
                return string.Empty;

            return relativePath.Substring(0, lastSlash);
        }

        private static string CreateExportFolder(TrialRecord trial)
        {
            return TrialDataArchive.CreateReviewExportFolder(trial);
        }

        /// <summary>
        /// Render the ROI top-down once and write two PNGs from it: the clean plate
        /// (`roi_topdown.png`) and the same plate with the trial's trajectories drawn on
        /// top (`roi_topdown_trajectory.png`). Both share one render, so they are
        /// pixel-aligned with each other and with the exported ROI bounds.
        /// </summary>
        private static void ExportTopDownImages(Bounds roi, int maxResolution, string exportFolder, ReviewExportData data)
        {
            Texture2D texture = RenderTopDown(roi, maxResolution, out Rect worldRect);
            if (texture == null)
                return;

            try
            {
                const string plainFileName = "roi_topdown.png";
                File.WriteAllBytes(Path.Combine(exportFolder, plainFileName), texture.EncodeToPNG());
                data.image = BuildImageInfo(texture, plainFileName);

                const string trajectoryFileName = "roi_topdown_trajectory.png";
                DrawAgentTrajectories(texture, worldRect, data.agents);
                File.WriteAllBytes(Path.Combine(exportFolder, trajectoryFileName), texture.EncodeToPNG());
                data.trajectoryImage = BuildImageInfo(texture, trajectoryFileName);
            }
            finally
            {
                UnityEngine.Object.Destroy(texture);
            }
        }

        /// <summary>
        /// Render the ROI from a top-down orthographic camera. <paramref name="worldRect"/>
        /// receives the world XZ area the pixels actually cover (x/y = min X/Z), which can
        /// differ from the ROI by a fraction of a metre because the pixel dimensions are
        /// rounded; trajectory drawing must map through it, not through the raw ROI.
        /// </summary>
        private static Texture2D RenderTopDown(Bounds roi, int maxResolution, out Rect worldRect)
        {
            float widthWorld = Mathf.Max(roi.size.x, 0.1f);
            float heightWorld = Mathf.Max(roi.size.z, 0.1f);
            float aspect = widthWorld / heightWorld;

            int widthPx;
            int heightPx;
            if (aspect >= 1f)
            {
                widthPx = maxResolution;
                heightPx = Mathf.Max(1, Mathf.RoundToInt(maxResolution / aspect));
            }
            else
            {
                heightPx = maxResolution;
                widthPx = Mathf.Max(1, Mathf.RoundToInt(maxResolution * aspect));
            }

            float pixelAspect = widthPx / (float)heightPx;
            float orthoSize = Mathf.Max(heightWorld * 0.5f, widthWorld * 0.5f / Mathf.Max(0.01f, pixelAspect));
            float coveredHeight = orthoSize * 2f;
            float coveredWidth = coveredHeight * pixelAspect;
            worldRect = new Rect(
                roi.center.x - coveredWidth * 0.5f,
                roi.center.z - coveredHeight * 0.5f,
                coveredWidth,
                coveredHeight);

            var cameraGO = new GameObject("ReviewRoiExportCamera");
            var exportCamera = cameraGO.AddComponent<Camera>();
            exportCamera.orthographic = true;
            exportCamera.transform.position = new Vector3(roi.center.x, roi.center.y + 100f, roi.center.z);
            exportCamera.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            exportCamera.orthographicSize = orthoSize;
            exportCamera.clearFlags = CameraClearFlags.Skybox;

            var rt = new RenderTexture(widthPx, heightPx, 24);
            exportCamera.targetTexture = rt;

            var hiddenRenderers = HideDynamicRenderers();
            var activeRT = RenderTexture.active;

            try
            {
                exportCamera.Render();
                RenderTexture.active = rt;
                var tex = new Texture2D(widthPx, heightPx, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, widthPx, heightPx), 0, 0);
                tex.Apply();
                return tex;
            }
            finally
            {
                RestoreRenderers(hiddenRenderers);
                RenderTexture.active = activeRT;
                exportCamera.targetTexture = null;
                rt.Release();
                UnityEngine.Object.Destroy(rt);
                UnityEngine.Object.Destroy(cameraGO);
            }
        }

        // Same order/colours as review_roi_viewer.py so a baked image and the viewer
        // assign the same colour to the same agent.
        private static readonly Color32[] AgentPalette =
        {
            new Color32(0xff, 0x5f, 0x56, 0xff),
            new Color32(0x37, 0xc9, 0x78, 0xff),
            new Color32(0x4d, 0xa3, 0xff, 0xff),
            new Color32(0xf2, 0xc1, 0x4e, 0xff),
            new Color32(0xd2, 0x77, 0xff, 0xff),
            new Color32(0xff, 0x8c, 0x42, 0xff)
        };

        private static readonly Color32 TrajectoryOutlineColor = new Color32(0x10, 0x14, 0x18, 0xff);

        private static void DrawAgentTrajectories(Texture2D texture, Rect worldRect, List<ReviewExportAgent> agents)
        {
            if (agents == null || agents.Count == 0)
                return;

            int width = texture.width;
            int height = texture.height;
            Color32[] pixels = texture.GetPixels32();

            // Scale line weight with resolution so a 2048px export is not hairline-thin.
            int lineRadius = Mathf.Max(1, Mathf.RoundToInt(Mathf.Max(width, height) / 500f));
            int markerRadius = lineRadius * 3;

            for (int i = 0; i < agents.Count; i++)
            {
                ReviewExportAgent agent = agents[i];
                if (agent == null || agent.samples == null || agent.samples.Count == 0)
                    continue;

                Color32 color = AgentPalette[i % AgentPalette.Length];

                // Two passes: a dark casing first, then the colour, so paths stay readable
                // over both bright pavement and dark shadow.
                for (int pass = 0; pass < 2; pass++)
                {
                    Color32 passColor = pass == 0 ? TrajectoryOutlineColor : color;
                    int passRadius = pass == 0 ? lineRadius + 1 : lineRadius;

                    Vector2Int previous = default;
                    bool hasPrevious = false;
                    foreach (var sample in agent.samples)
                    {
                        Vector2Int point = WorldToPixel(sample.position, worldRect, width, height);
                        if (hasPrevious)
                            DrawLine(pixels, width, height, previous, point, passColor, passRadius);
                        previous = point;
                        hasPrevious = true;
                    }

                    DrawDisc(pixels, width, height, WorldToPixel(agent.startPosition, worldRect, width, height), passColor, pass == 0 ? markerRadius + 1 : markerRadius);
                    DrawSquare(pixels, width, height, WorldToPixel(agent.endPosition, worldRect, width, height), passColor, pass == 0 ? markerRadius + 1 : markerRadius);
                    if (!agent.goalIsInferred)
                        DrawCross(pixels, width, height, WorldToPixel(agent.goalPosition, worldRect, width, height), passColor, markerRadius + 2, pass == 0 ? lineRadius + 1 : lineRadius);
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply();
        }

        private static Vector2Int WorldToPixel(Vector3 world, Rect worldRect, int width, int height)
        {
            float u = (world.x - worldRect.xMin) / Mathf.Max(0.0001f, worldRect.width);
            float v = (world.z - worldRect.yMin) / Mathf.Max(0.0001f, worldRect.height);
            // Texture row 0 is the bottom of the render and the export camera's up axis is
            // +Z, so world Z maps straight onto texture Y with no flip.
            return new Vector2Int(
                Mathf.RoundToInt(u * (width - 1)),
                Mathf.RoundToInt(v * (height - 1)));
        }

        private static void DrawLine(Color32[] pixels, int width, int height, Vector2Int a, Vector2Int b, Color32 color, int radius)
        {
            // Trivial reject: samples outside the ROI are common (the trajectory runs past
            // the padded box), and stepping across them pixel by pixel is pure waste.
            if ((a.x < -radius && b.x < -radius) || (a.x >= width + radius && b.x >= width + radius) ||
                (a.y < -radius && b.y < -radius) || (a.y >= height + radius && b.y >= height + radius))
                return;

            int dx = Mathf.Abs(b.x - a.x);
            int sx = a.x < b.x ? 1 : -1;
            int dy = -Mathf.Abs(b.y - a.y);
            int sy = a.y < b.y ? 1 : -1;
            int err = dx + dy;
            int x = a.x;
            int y = a.y;

            int maxSteps = (width + height) * 4;
            for (int step = 0; step <= maxSteps; step++)
            {
                DrawDisc(pixels, width, height, new Vector2Int(x, y), color, radius);
                if (x == b.x && y == b.y)
                    break;

                int e2 = 2 * err;
                if (e2 >= dy)
                {
                    err += dy;
                    x += sx;
                }
                if (e2 <= dx)
                {
                    err += dx;
                    y += sy;
                }
            }
        }

        private static void DrawDisc(Color32[] pixels, int width, int height, Vector2Int center, Color32 color, int radius)
        {
            int radiusSq = radius * radius;
            for (int oy = -radius; oy <= radius; oy++)
            {
                for (int ox = -radius; ox <= radius; ox++)
                {
                    if (ox * ox + oy * oy > radiusSq)
                        continue;
                    SetPixel(pixels, width, height, center.x + ox, center.y + oy, color);
                }
            }
        }

        private static void DrawSquare(Color32[] pixels, int width, int height, Vector2Int center, Color32 color, int halfSize)
        {
            for (int oy = -halfSize; oy <= halfSize; oy++)
            {
                for (int ox = -halfSize; ox <= halfSize; ox++)
                    SetPixel(pixels, width, height, center.x + ox, center.y + oy, color);
            }
        }

        private static void DrawCross(Color32[] pixels, int width, int height, Vector2Int center, Color32 color, int halfSize, int thickness)
        {
            for (int offset = -halfSize; offset <= halfSize; offset++)
            {
                DrawDisc(pixels, width, height, new Vector2Int(center.x + offset, center.y + offset), color, thickness);
                DrawDisc(pixels, width, height, new Vector2Int(center.x + offset, center.y - offset), color, thickness);
            }
        }

        private static void SetPixel(Color32[] pixels, int width, int height, int x, int y, Color32 color)
        {
            if (x < 0 || y < 0 || x >= width || y >= height)
                return;
            pixels[y * width + x] = color;
        }

        private static List<Renderer> HideDynamicRenderers()
        {
            var hidden = new List<Renderer>();

            foreach (var nav in UnityEngine.Object.FindObjectsOfType<IVI.INavigable>())
                AddRenderers(hidden, nav.GetComponentsInChildren<Renderer>(true));

            var sean = SEAN.SEAN.instance;
            if (sean != null)
            {
                if (sean.robot != null)
                    AddRenderers(hidden, sean.robot.GetComponentsInChildren<Renderer>(true));
                if (sean.player != null)
                    AddRenderers(hidden, sean.player.GetComponentsInChildren<Renderer>(true));
                if (sean.robotTask != null)
                {
                    if (sean.robotTask.robotGoal != null)
                        AddRenderers(hidden, sean.robotTask.robotGoal.GetComponentsInChildren<Renderer>(true));
                    if (sean.robotTask.playerGoal != null)
                        AddRenderers(hidden, sean.robotTask.playerGoal.GetComponentsInChildren<Renderer>(true));
                }
            }

            if (SessionReviewManager.Instance != null)
                AddRenderers(hidden, SessionReviewManager.Instance.GetComponentsInChildren<Renderer>(true));

            // The floating "ROBOT GOAL" text and goal outline are goal UI, not scene geometry.
            if (RobotGoalObjectBinding.Instance != null)
                AddRenderers(hidden, RobotGoalObjectBinding.Instance.GoalUiRenderers);

            // Same for GoalBeacon's floating goal labels, which are root objects rather than
            // children of the markers hidden above.
            AddRenderers(hidden, GoalBeacon.AllUiRenderers);

            return hidden;
        }

        private static void AddRenderers(List<Renderer> hidden, Renderer[] renderers)
        {
            foreach (var renderer in renderers)
            {
                if (renderer == null || !renderer.enabled || hidden.Contains(renderer))
                    continue;

                renderer.enabled = false;
                hidden.Add(renderer);
            }
        }

        private static void RestoreRenderers(List<Renderer> hidden)
        {
            foreach (var renderer in hidden)
            {
                if (renderer != null)
                    renderer.enabled = true;
            }
        }
    }
}
