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
        public float paddingX = 3f;
        public float paddingZ = 3f;
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

                foreach (var state in timeline.states)
                {
                    if (state.timestamp < recStart || state.timestamp > recEnd)
                        continue;

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
            if (trial == null || recording == null)
                throw new ArgumentNullException("trial/recording");

            if (recording.timelineDict == null)
                recording.BuildCache();

            if (!TryComputeTrajectoryEnvelope(trial, recording, recordingTimeOffset, out Bounds envelope))
                throw new InvalidOperationException("Could not compute trajectory envelope for the selected trial.");

            Bounds roi = ApplySettings(envelope, settings);
            string exportFolder = CreateExportFolder(trial);

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
                string imageFileName = "roi_topdown.png";
                string imagePath = Path.Combine(exportFolder, imageFileName);
                ExportTopDownImage(roi, settings.imageMaxResolution, imagePath);
                data.image = BuildImageInfo(roi, settings.imageMaxResolution, imageFileName);
            }

            string jsonPath = Path.Combine(exportFolder, "review_roi_export.json");
            File.WriteAllText(jsonPath, JsonUtility.ToJson(data, true));
            Debug.Log($"[SessionReview] Review ROI export saved to: {exportFolder}");
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

                foreach (var state in timeline.states)
                {
                    if (state.timestamp < recStart || state.timestamp > recEnd)
                        continue;

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

        private static ReviewExportImageInfo BuildImageInfo(Bounds roi, int maxResolution, string fileName)
        {
            float width = Mathf.Max(roi.size.x, 0.1f);
            float height = Mathf.Max(roi.size.z, 0.1f);
            float aspect = width / height;

            int pixelWidth;
            int pixelHeight;
            if (aspect >= 1f)
            {
                pixelWidth = maxResolution;
                pixelHeight = Mathf.Max(1, Mathf.RoundToInt(maxResolution / aspect));
            }
            else
            {
                pixelHeight = maxResolution;
                pixelWidth = Mathf.Max(1, Mathf.RoundToInt(maxResolution * aspect));
            }

            return new ReviewExportImageInfo
            {
                fileName = fileName,
                width = pixelWidth,
                height = pixelHeight
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

        private static void ExportTopDownImage(Bounds roi, int maxResolution, string outputPath)
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

            var cameraGO = new GameObject("ReviewRoiExportCamera");
            var exportCamera = cameraGO.AddComponent<Camera>();
            exportCamera.orthographic = true;
            exportCamera.transform.position = new Vector3(roi.center.x, roi.center.y + 100f, roi.center.z);
            exportCamera.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            exportCamera.orthographicSize = Mathf.Max(heightWorld * 0.5f, widthWorld * 0.5f / Mathf.Max(0.01f, widthPx / (float)heightPx));
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
                File.WriteAllBytes(outputPath, tex.EncodeToPNG());
                UnityEngine.Object.Destroy(tex);
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
