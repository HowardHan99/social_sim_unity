#if UNITY_EDITOR
using System.IO;
using SEAN.Scenario.Obstacles;
using Siccity.GLTFUtility;
using UnityEditor;
using UnityEngine;

namespace SessionReview.Editor
{
    /// <summary>
    /// Builds World Building spawn prefabs from BikeModel.glb / ScooterModel.fbx plus palette thumbnails.
    /// Menu: SessionReview → Build Bike & Scooter Spawn Prefabs
    /// </summary>
    public static class WorldBuildingModelPrefabBuilder
    {
        const string SpawnsDir = "Assets/Resources/WorldBuildingSpawns";
        const string ModelSourcesDir = "Assets/Art/WorldBuildingSpawnSources";

        [MenuItem("SessionReview/Build Bike & Scooter Spawn Prefabs")]
        public static void BuildAllFromMenu()
        {
            BuildAll();
        }

        public static void BuildAll()
        {
            BuildOne("Bike", "BikeModel.glb");
            BuildOne("Scooter", "ScooterModel.fbx");
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[WorldBuildingModelPrefabBuilder] Bike and Scooter spawn prefabs updated.");
        }

        static void BuildOne(string prefabName, string modelFileName)
        {
            string modelPath = Path.Combine(ModelSourcesDir, modelFileName).Replace('\\', '/');
            string prefabPath = Path.Combine(SpawnsDir, prefabName + ".prefab").Replace('\\', '/');

            GameObject modelRoot = LoadModelRoot(modelPath);
            if (modelRoot == null)
            {
                Debug.LogError("[WorldBuildingModelPrefabBuilder] Could not load model: " + modelPath);
                return;
            }

            GameObject instance = PrefabUtility.IsPartOfPrefabAsset(modelRoot)
                ? (GameObject)PrefabUtility.InstantiatePrefab(modelRoot)
                : Object.Instantiate(modelRoot);

            if (instance == null)
            {
                Debug.LogError("[WorldBuildingModelPrefabBuilder] Failed to instantiate: " + modelPath);
                return;
            }

            instance.name = prefabName;
            instance.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            instance.transform.localScale = Vector3.one;
            instance.layer = 3;

            FitToGround(instance);

            var box = instance.GetComponent<BoxCollider>();
            if (box == null)
                box = instance.AddComponent<BoxCollider>();
            FitBoxCollider(instance, box);

            var obstacle = instance.GetComponent<TrackedObstacle>();
            if (obstacle == null)
                obstacle = instance.AddComponent<TrackedObstacle>();
            obstacle.type = prefabName.ToLowerInvariant();

            var tracked = instance.GetComponent<Rerun.TrackedObject>();
            if (tracked == null)
                instance.AddComponent<Rerun.TrackedObject>();

            PrefabUtility.SaveAsPrefabAsset(instance, prefabPath);
            Object.DestroyImmediate(instance);
            Debug.Log("[WorldBuildingModelPrefabBuilder] Saved " + prefabPath);
        }

        static GameObject LoadModelRoot(string assetPath)
        {
            var main = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (main != null)
                return main;

            if (assetPath.EndsWith(".glb", System.StringComparison.OrdinalIgnoreCase)
                && File.Exists(assetPath))
            {
                try
                {
                    return Importer.LoadFromFile(assetPath, new ImportSettings(), out _);
                }
                catch (System.Exception ex)
                {
                    Debug.LogWarning("[WorldBuildingModelPrefabBuilder] GLBUtility load failed: " + ex.Message);
                }
            }

            Object[] subAssets = AssetDatabase.LoadAllAssetsAtPath(assetPath);
            if (subAssets == null)
                return null;

            foreach (Object o in subAssets)
            {
                if (o is GameObject go)
                    return go;
            }

            return null;
        }

        static void FitToGround(GameObject root)
        {
            if (!TryWorldBounds(root, out Bounds bounds))
                return;

            Vector3 delta = new Vector3(0f, -bounds.min.y, 0f);
            root.transform.position += delta;
        }

        static void FitBoxCollider(GameObject root, BoxCollider box)
        {
            if (!TryLocalBounds(root, out Bounds bounds))
            {
                box.center = new Vector3(0f, 0.5f, 0f);
                box.size = new Vector3(1.2f, 1f, 0.5f);
                return;
            }

            box.center = bounds.center;
            box.size = bounds.size;
        }

        static bool TryWorldBounds(GameObject root, out Bounds bounds)
        {
            bounds = default;
            Renderer[] renderers = root.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0)
                return false;

            bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++)
                bounds.Encapsulate(renderers[i].bounds);
            return true;
        }

        static bool TryLocalBounds(GameObject root, out Bounds bounds)
        {
            bounds = default;
            if (!TryWorldBounds(root, out Bounds world))
                return false;

            Vector3 localCenter = root.transform.InverseTransformPoint(world.center);
            Vector3 lossy = root.transform.lossyScale;
            bounds = new Bounds(
                localCenter,
                new Vector3(
                    world.size.x / Mathf.Max(Mathf.Abs(lossy.x), 0.001f),
                    world.size.y / Mathf.Max(Mathf.Abs(lossy.y), 0.001f),
                    world.size.z / Mathf.Max(Mathf.Abs(lossy.z), 0.001f)));
            return true;
        }
    }

    [InitializeOnLoad]
    static class WorldBuildingModelPrefabBuilderAuto
    {
        static WorldBuildingModelPrefabBuilderAuto()
        {
            EditorApplication.delayCall += TryBuildMissing;
        }

        static void TryBuildMissing()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                return;

            string bike = SpawnsDir + "/Bike.prefab";
            string scooter = SpawnsDir + "/Scooter.prefab";
            if (File.Exists(bike) && File.Exists(scooter))
            {
                if (PrefabHasRequiredComponents(bike) && PrefabHasRequiredComponents(scooter))
                    return;
            }

            if (!File.Exists(SpawnsDir + "/BikeModel.glb") && !File.Exists(ModelSourcesDir + "/BikeModel.glb")
                && !File.Exists(SpawnsDir + "/ScooterModel.fbx") && !File.Exists(ModelSourcesDir + "/ScooterModel.fbx"))
                return;

            WorldBuildingModelPrefabBuilder.BuildAll();
        }

        const string SpawnsDir = "Assets/Resources/WorldBuildingSpawns";
        const string ModelSourcesDir = "Assets/Art/WorldBuildingSpawnSources";

        static bool PrefabHasRequiredComponents(string prefabPath)
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null)
                return false;

            return prefab.GetComponent<BoxCollider>() != null
                   && prefab.GetComponent<TrackedObstacle>() != null;
        }
    }
}
#endif
