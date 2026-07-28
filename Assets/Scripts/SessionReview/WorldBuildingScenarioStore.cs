using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SessionReview
{
    [Serializable]
    public class WorldBuildingScenarioObject
    {
        public string prefabName;    // Resources/WorldBuildingSpawns/<prefabName> (palette spawns)
        public string importGlbPath; // GLB for Meshy imports: "assets/<file>" (scenario asset library) or absolute; empty for palette spawns
        public string sourceGlbPath; // original absolute GLB location, kept as a fallback
        public string displayName;
        public bool dynamicAgent;    // character spawned as a moving agent (SFAgent wander) instead of a static prop; absent in old saves = static
        public Vector3 position;
        public Vector3 rotationEuler;
        public Vector3 localScale;
    }

    /// <summary>
    /// A change made to a PRE-EXISTING object of the base scene (moved/rescaled or deleted).
    /// Together with the spawned-object list this makes scene.json a full snapshot of the
    /// built world, replayed on top of the base .unity scene.
    /// </summary>
    [Serializable]
    public class WorldBuildingSceneObjectDelta
    {
        public string scenePath;         // hierarchy path in the base scene ("Env/Props/Bench")
        public Vector3 originalPosition; // pose in the pristine scene; disambiguates same-named objects on restore
        public bool deleted;             // true: object was deleted (deactivated) in World Building
        public Vector3 position;
        public Vector3 rotationEuler;
        public Vector3 localScale;
    }

    [Serializable]
    public class WorldBuildingScenarioData
    {
        public string name;
        public string sceneName; // preset scene the world was built on top of
        public string sessionId;
        public string createdAt;
        public string weather = "";  // "" = clear, "fog" (rain/snow later); set in the Weather panel
        public int fogPreset = 1;    // FogController density preset (0 light / 1 medium / 2 heavy)
        public List<WorldBuildingScenarioObject> objects = new List<WorldBuildingScenarioObject>();
        public List<WorldBuildingSceneObjectDelta> sceneObjectDeltas = new List<WorldBuildingSceneObjectDelta>();
    }

    /// <summary>One saved scenario as listed on the onboarding page (thumbnail card).</summary>
    public sealed class WorldBuildingScenarioInfo
    {
        public string Folder;
        public string Name;
        public string SceneName;
        public string SessionId;
        public int ObjectCount;
        public Texture2D Thumbnail;
    }

    /// <summary>
    /// Saves and reloads World Building worlds ("scenarios"). Each scenario is one
    /// self-contained folder under SessionLogs/&lt;sessionId&gt;/scenario/&lt;name_stamp&gt;/:
    ///   scene.json     - the scene file: base scene name + spawned objects + deltas of
    ///                    moved/deleted pre-existing scene objects
    ///   assets/        - the asset library: copies of runtime-imported files (Meshy GLBs),
    ///                    so the scenario keeps working if the originals are deleted
    ///   thumbnail.png  - snapshot from the "Do you want to save this scene?" prompt
    /// Reloading loads the base scene and replays the snapshot on top of it. (A real .unity
    /// file cannot be written at runtime, hence base scene + delta.)
    /// </summary>
    public static class WorldBuildingScenarioStore
    {
        public const string ScenarioFolderName = "scenario";
        private const string DataFileName = "scene.json";
        private const string LegacyDataFileName = "scenario.json"; // pre-asset-library saves
        private const string AssetsFolderName = "assets";
        private const string ThumbnailFileName = "thumbnail.png";

        /// <summary>
        /// Scenario folder set just before a scene load; <see cref="WorldBuildingScenarioRestorer"/>
        /// consumes it via <see cref="TakePendingLoad"/> once the scene has loaded and rebuilds
        /// the saved world.
        /// </summary>
        public static string PendingLoadFolder { get; set; }

        /// <summary>
        /// Signature of the world as last restored or saved. A world matching it is already on
        /// disk, so the "Save This Scene?" prompt stays quiet until something changes again.
        /// Static (not a SessionReviewManager field) so it survives the scene load that
        /// restore itself performs.
        /// </summary>
        public static string RestoredSignature { get; set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            PendingLoadFolder = null;
            RestoredSignature = null;
        }

        public static string TakePendingLoad()
        {
            string folder = PendingLoadFolder;
            PendingLoadFolder = null;
            return folder;
        }

        public static string ScenarioRoot(string sessionId)
        {
            return Path.Combine(TrialDataArchive.SessionFolder(sessionId), ScenarioFolderName);
        }

        /// <summary>Every active runtime-placed World Building object in the scene.</summary>
        public static List<WorldBuildingScenarioObject> CollectCurrentObjects()
        {
            var list = new List<WorldBuildingScenarioObject>();
            foreach (WorldBuildingPlacedObject marker in UnityEngine.Object.FindObjectsOfType<WorldBuildingPlacedObject>())
            {
                if (marker == null)
                    continue;

                bool isImport = !string.IsNullOrEmpty(marker.importGlbPath);
                if (!isImport && string.IsNullOrEmpty(marker.paletteName))
                    continue;

                Transform t = marker.transform;
                list.Add(new WorldBuildingScenarioObject
                {
                    prefabName = marker.paletteName ?? string.Empty,
                    importGlbPath = marker.importGlbPath ?? string.Empty,
                    sourceGlbPath = marker.importGlbPath ?? string.Empty,
                    displayName = marker.displayName ?? string.Empty,
                    dynamicAgent = marker.dynamicAgent,
                    position = t.position,
                    rotationEuler = t.rotation.eulerAngles,
                    localScale = t.localScale
                });
            }

            return list;
        }

        /// <summary>
        /// Changes World Building made to PRE-EXISTING scene objects: everything that carries
        /// a pose baseline and was actually moved/rescaled or deleted (deactivated).
        /// </summary>
        public static List<WorldBuildingSceneObjectDelta> CollectSceneObjectDeltas()
        {
            var list = new List<WorldBuildingSceneObjectDelta>();
            foreach (WorldBuildingSceneObjectBaseline baseline in
                     UnityEngine.Object.FindObjectsOfType<WorldBuildingSceneObjectBaseline>(true))
            {
                if (baseline == null || string.IsNullOrEmpty(baseline.scenePath))
                    continue;

                // Task/spawner markers: active state is engine-managed (the unused start/goal
                // is deactivated every scene load), so record their pose only.
                bool deleted = !baseline.gameObject.activeSelf && !baseline.isProtectedMarker;
                if (!deleted && !baseline.HasMoved())
                    continue;

                Transform t = baseline.transform;
                list.Add(new WorldBuildingSceneObjectDelta
                {
                    scenePath = baseline.scenePath,
                    originalPosition = baseline.originalPosition,
                    deleted = deleted,
                    position = t.position,
                    rotationEuler = t.rotation.eulerAngles,
                    localScale = t.localScale
                });
            }

            return list;
        }

        /// <summary>
        /// Order-independent fingerprint of the built world: placed objects AND deltas on
        /// pre-existing scene objects. Empty string when the scene is untouched. Used to skip
        /// the save prompt when a world restored from a scenario is left unmodified.
        /// </summary>
        public static string ComputeCurrentSignature()
        {
            var parts = new List<string>();
            foreach (WorldBuildingScenarioObject obj in CollectCurrentObjects())
            {
                string key = string.IsNullOrEmpty(obj.importGlbPath)
                    ? obj.prefabName
                    : Path.GetFileName(obj.importGlbPath);
                // Same character static vs moving is a different world — keep the save prompt awake.
                if (obj.dynamicAgent)
                    key += "~walk";
                parts.Add(string.Format(CultureInfo.InvariantCulture,
                    "{0}@{1:F2},{2:F2},{3:F2}/{4:F1},{5:F1},{6:F1}",
                    key,
                    obj.position.x, obj.position.y, obj.position.z,
                    obj.rotationEuler.x, obj.rotationEuler.y, obj.rotationEuler.z));
            }

            foreach (WorldBuildingSceneObjectDelta delta in CollectSceneObjectDeltas())
            {
                parts.Add(string.Format(CultureInfo.InvariantCulture,
                    "{0}#{1}@{2:F2},{3:F2},{4:F2}/{5:F1},{6:F1},{7:F1}",
                    delta.scenePath, delta.deleted ? "del" : "mov",
                    delta.position.x, delta.position.y, delta.position.z,
                    delta.rotationEuler.x, delta.rotationEuler.y, delta.rotationEuler.z));
            }

            // Weather set in the Weather panel is part of the built world too: a fog-only
            // change must trigger the save prompt and land in the scenario.
            Weather.FogController fogController = Weather.FogController.Instance;
            if (fogController != null && fogController.FogActive)
                parts.Add("weather:fog@" + fogController.presetIndex.ToString(CultureInfo.InvariantCulture));

            if (parts.Count == 0)
                return string.Empty;

            parts.Sort(StringComparer.Ordinal);
            return string.Join(";", parts);
        }

        /// <summary>
        /// Writes the built world (spawned objects + scene deltas + copied import assets) as a
        /// named scenario and returns the created folder. The thumbnail (may be null) is
        /// stored as thumbnail.png.
        /// </summary>
        public static string SaveCurrentScenario(string displayName, Texture2D thumbnail)
        {
            var data = new WorldBuildingScenarioData
            {
                name = string.IsNullOrWhiteSpace(displayName) ? "Unnamed world" : displayName.Trim(),
                sceneName = SceneManager.GetActiveScene().name,
                sessionId = ParticipantSession.Id,
                createdAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                objects = CollectCurrentObjects(),
                sceneObjectDeltas = CollectSceneObjectDeltas()
            };

            Weather.FogController fog = Weather.FogController.Instance;
            data.weather = fog != null && fog.FogActive ? "fog" : string.Empty;
            data.fogPreset = fog != null ? fog.presetIndex : 1;

            string root = ScenarioRoot(data.sessionId);
            Directory.CreateDirectory(root);

            string baseName = TrialDataArchive.SanitizeFolderName(data.name, "scenario")
                              + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string folder = Path.Combine(root, baseName);
            int suffix = 1;
            while (Directory.Exists(folder))
                folder = Path.Combine(root, baseName + "_" + (suffix++).ToString("D2"));
            Directory.CreateDirectory(folder);

            CopyImportAssetsIntoScenario(folder, data);

            File.WriteAllText(Path.Combine(folder, DataFileName), JsonUtility.ToJson(data, true));

            if (thumbnail != null)
            {
                try
                {
                    File.WriteAllBytes(Path.Combine(folder, ThumbnailFileName), thumbnail.EncodeToPNG());
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[WorldBuildingScenario] Could not write thumbnail: {ex.Message}");
                }
            }

            // The world on screen now matches what is on disk, so don't re-prompt for it.
            RestoredSignature = ComputeCurrentSignature();

            Debug.Log($"[WorldBuildingScenario] Saved '{data.name}' to '{folder}' "
                + $"({data.objects.Count} placed object(s), {data.sceneObjectDeltas.Count} scene change(s), session '{data.sessionId}').");
            return folder;
        }

        /// <summary>
        /// Copies every imported file (Meshy GLBs live in persistentDataPath and can be
        /// deleted/regenerated any time) into the scenario's assets/ library and rewrites the
        /// entries to scenario-relative paths, making the folder self-contained and portable.
        /// Palette prefabs ship with the build (Resources) and need no copy.
        /// </summary>
        private static void CopyImportAssetsIntoScenario(string folder, WorldBuildingScenarioData data)
        {
            if (data.objects == null)
                return;

            string assetsFolder = Path.Combine(folder, AssetsFolderName);
            var usedNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // source path -> stored file name

            foreach (WorldBuildingScenarioObject obj in data.objects)
            {
                if (string.IsNullOrEmpty(obj.importGlbPath) || !Path.IsPathRooted(obj.importGlbPath))
                    continue;

                try
                {
                    if (!File.Exists(obj.importGlbPath))
                        continue; // keep the absolute path; restore will warn if still missing

                    string storedName;
                    if (!usedNames.TryGetValue(obj.importGlbPath, out storedName))
                    {
                        Directory.CreateDirectory(assetsFolder);
                        storedName = Path.GetFileName(obj.importGlbPath);
                        string baseFile = Path.GetFileNameWithoutExtension(storedName);
                        string ext = Path.GetExtension(storedName);
                        int n = 1;
                        while (File.Exists(Path.Combine(assetsFolder, storedName)))
                            storedName = baseFile + "_" + (n++).ToString("D2") + ext;

                        File.Copy(obj.importGlbPath, Path.Combine(assetsFolder, storedName));
                        usedNames[obj.importGlbPath] = storedName;
                    }

                    obj.importGlbPath = AssetsFolderName + "/" + storedName;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[WorldBuildingScenario] Could not copy import '{obj.importGlbPath}' into the scenario asset library: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Absolute path of an import entry's file: scenario-relative asset-library paths are
        /// resolved against the scenario folder, with the original absolute location as fallback.
        /// </summary>
        public static string ResolveImportPath(string scenarioFolder, WorldBuildingScenarioObject obj)
        {
            if (obj == null || string.IsNullOrEmpty(obj.importGlbPath))
                return null;

            string primary = Path.IsPathRooted(obj.importGlbPath)
                ? obj.importGlbPath
                : Path.Combine(scenarioFolder, obj.importGlbPath);
            if (File.Exists(primary))
                return primary;

            if (!string.IsNullOrEmpty(obj.sourceGlbPath) && File.Exists(obj.sourceGlbPath))
                return obj.sourceGlbPath;

            return primary; // caller logs the miss
        }

        public static WorldBuildingScenarioData LoadScenarioData(string folder)
        {
            try
            {
                string path = Path.Combine(folder, DataFileName);
                if (!File.Exists(path))
                    path = Path.Combine(folder, LegacyDataFileName);
                if (!File.Exists(path))
                    return null;
                return JsonUtility.FromJson<WorldBuildingScenarioData>(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[WorldBuildingScenario] Could not read scenario at '{folder}': {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Replays the recorded deltas onto the freshly loaded base scene: moved objects get
        /// their saved pose (plus a baseline so re-saving keeps working), deleted objects are
        /// deactivated. Returns the affected GameObjects (deleted ones included).
        /// </summary>
        public static List<GameObject> ApplySceneObjectDeltas(WorldBuildingScenarioData data)
        {
            var affected = new List<GameObject>();
            if (data?.sceneObjectDeltas == null)
                return affected;

            HashSet<GameObject> engineManagedMarkers = GetEngineManagedMarkers();

            foreach (WorldBuildingSceneObjectDelta delta in data.sceneObjectDeltas)
            {
                if (delta == null || string.IsNullOrEmpty(delta.scenePath))
                    continue;

                GameObject target = ResolveSceneObject(delta);
                if (target == null)
                {
                    Debug.LogWarning($"[WorldBuildingScenario] Scene object '{delta.scenePath}' not found in this scene; delta skipped.");
                    continue;
                }

                WorldBuildingSceneObjectBaseline baseline = target.GetComponent<WorldBuildingSceneObjectBaseline>();
                if (baseline == null)
                {
                    baseline = target.AddComponent<WorldBuildingSceneObjectBaseline>();
                    baseline.Capture(); // pristine pose: deltas apply before any move below
                }
                if (engineManagedMarkers.Contains(target))
                    baseline.isProtectedMarker = true;

                // Never deactivate a task/spawner marker, whatever an (old) save recorded —
                // hiding a live goal breaks the trial. Apply its pose instead.
                if (delta.deleted && !baseline.isProtectedMarker)
                {
                    target.SetActive(false);
                }
                else
                {
                    target.transform.SetPositionAndRotation(delta.position, Quaternion.Euler(delta.rotationEuler));
                    if (delta.localScale != Vector3.zero)
                        target.transform.localScale = delta.localScale;

                    foreach (Rigidbody rb in target.GetComponentsInChildren<Rigidbody>(true))
                    {
                        if (rb == null) continue;
                        rb.velocity = Vector3.zero;
                        rb.angularVelocity = Vector3.zero;
                    }
                }

                affected.Add(target);
            }

            return affected;
        }

        /// <summary>
        /// Finds the delta's object in the freshly loaded scene by hierarchy path; when several
        /// objects share the path (duplicate names), the one sitting closest to the recorded
        /// pristine pose wins — in an unmodified base scene that is an exact match.
        /// </summary>
        private static GameObject ResolveSceneObject(WorldBuildingSceneObjectDelta delta)
        {
            string[] segments = delta.scenePath.Split('/');
            if (segments.Length == 0)
                return null;

            var current = new List<Transform>();
            foreach (GameObject root in SceneManager.GetActiveScene().GetRootGameObjects())
            {
                if (root != null && root.name == segments[0])
                    current.Add(root.transform);
            }

            for (int depth = 1; depth < segments.Length && current.Count > 0; depth++)
            {
                var next = new List<Transform>();
                foreach (Transform parent in current)
                {
                    for (int i = 0; i < parent.childCount; i++)
                    {
                        Transform child = parent.GetChild(i);
                        if (child.name == segments[depth])
                            next.Add(child);
                    }
                }
                current = next;
            }

            Transform best = null;
            float bestSqr = float.MaxValue;
            foreach (Transform candidate in current)
            {
                float sqr = (candidate.position - delta.originalPosition).sqrMagnitude;
                if (sqr < bestSqr)
                {
                    bestSqr = sqr;
                    best = candidate;
                }
            }

            return best != null ? best.gameObject : null;
        }

        /// <summary>
        /// The scene objects whose ACTIVE state the engine manages: the task's four start/goal
        /// markers plus the PWD spawner's named start/goal objects. These may be restored in
        /// pose but never deactivated.
        /// </summary>
        private static HashSet<GameObject> GetEngineManagedMarkers()
        {
            var set = new HashSet<GameObject>();

            var sean = SEAN.SEAN.instance;
            if (sean != null)
            {
                SEAN.Tasks.Base task = null;
                try { task = sean.robotTask; }
                catch (Exception) { }
                if (task != null)
                {
                    if (task.robotStart != null) set.Add(task.robotStart);
                    if (task.robotGoal != null) set.Add(task.robotGoal);
                    if (task.playerStart != null) set.Add(task.playerStart);
                    if (task.playerGoal != null) set.Add(task.playerGoal);
                }
            }

            foreach (SEAN.Scenario.Agents.RandomAvatar spawner in
                     UnityEngine.Object.FindObjectsOfType<SEAN.Scenario.Agents.RandomAvatar>(true))
            {
                if (spawner == null)
                    continue;

                GameObject start = SEAN.Scenario.Agents.RandomAvatar.FindSceneObjectByName(spawner.startObjectName);
                if (start != null) set.Add(start);
                GameObject goal = SEAN.Scenario.Agents.RandomAvatar.FindSceneObjectByName(spawner.goalObjectName);
                if (goal != null) set.Add(goal);
            }

            return set;
        }

        /// <summary>
        /// Saved scenarios, newest first, thumbnails loaded. By default scoped to the CURRENT
        /// session (<see cref="ParticipantSession.Id"/>) so one participant never sees another
        /// participant's (e.g. "test") saved worlds; worlds saved before any session id was
        /// typed live under the "unassigned" folder and are always included, so a save made
        /// before the id was set doesn't look lost. Pass <paramref name="allSessions"/> = true
        /// (the picker's "Show all sessions" toggle) to list every session's worlds instead.
        /// The owning session is shown on each card.
        /// </summary>
        public static List<WorldBuildingScenarioInfo> ListScenarios(bool allSessions = false)
        {
            var result = new List<WorldBuildingScenarioInfo>();
            var dirList = new List<string>();

            try
            {
                if (!Directory.Exists(TrialDataArchive.LogFolder))
                    return result;

                // Current session's folder (+ the pre-id "unassigned" folder) unless the caller
                // asks for every session. SessionLogs/<sessionId>/scenario/<scenario folder>
                string currentSession = TrialDataArchive.SanitizeFolderName(ParticipantSession.Id, "unassigned");
                foreach (string sessionFolder in Directory.GetDirectories(TrialDataArchive.LogFolder))
                {
                    string sessionName = Path.GetFileName(sessionFolder);
                    if (!allSessions && sessionName != currentSession && sessionName != "unassigned")
                        continue;

                    string root = Path.Combine(sessionFolder, ScenarioFolderName);
                    if (Directory.Exists(root))
                        dirList.AddRange(Directory.GetDirectories(root));
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[WorldBuildingScenario] Could not list scenarios under '{TrialDataArchive.LogFolder}': {ex.Message}");
                return result;
            }

            string[] dirs = dirList.ToArray();
            Array.Sort(dirs, (a, b) => Directory.GetLastWriteTime(b).CompareTo(Directory.GetLastWriteTime(a)));

            foreach (string dir in dirs)
            {
                WorldBuildingScenarioData data = LoadScenarioData(dir);
                if (data == null)
                    continue;

                Texture2D thumb = null;
                string thumbPath = Path.Combine(dir, ThumbnailFileName);
                if (File.Exists(thumbPath))
                {
                    try
                    {
                        var tex = new Texture2D(2, 2, TextureFormat.RGB24, false);
                        if (tex.LoadImage(File.ReadAllBytes(thumbPath)))
                            thumb = tex;
                        else
                            UnityEngine.Object.Destroy(tex);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[WorldBuildingScenario] Could not read thumbnail '{thumbPath}': {ex.Message}");
                    }
                }

                result.Add(new WorldBuildingScenarioInfo
                {
                    Folder = dir,
                    Name = string.IsNullOrEmpty(data.name) ? Path.GetFileName(dir) : data.name,
                    SceneName = data.sceneName,
                    SessionId = data.sessionId,
                    ObjectCount = (data.objects != null ? data.objects.Count : 0)
                                  + (data.sceneObjectDeltas != null ? data.sceneObjectDeltas.Count : 0),
                    Thumbnail = thumb
                });
            }

            Debug.Log($"[WorldBuildingScenario] Found {result.Count} saved scenario(s) under '{TrialDataArchive.LogFolder}'.");
            return result;
        }

        /// <summary>
        /// Snapshot of the game view as it looks right now, WITHOUT any IMGUI overlay
        /// (cameras render the world only), so it stays clean while the save panel covers
        /// the screen. The whole display-0 camera stack is replayed in depth order into one
        /// texture, which is what the player actually sees — picking a single camera got this
        /// wrong, because hidden mini panels stay enabled at a LOWER depth than the main view
        /// (see GameDisplay) and their first-person close-up won the "lowest depth" test.
        /// </summary>
        public static Texture2D CaptureThumbnail(int width = 960)
        {
            List<Camera> cameras = CollectScreenCameras();
            if (cameras.Count == 0)
            {
                Debug.LogWarning("[WorldBuildingScenario] No camera available for the scenario thumbnail.");
                return null;
            }

            // Keep the screen's aspect so the snapshot reads as a normal game screenshot.
            float aspect = Screen.height > 0 ? (float)Screen.width / Screen.height : 16f / 9f;
            int height = Mathf.Max(1, Mathf.RoundToInt(width / Mathf.Max(0.1f, aspect)));

            RenderTexture rt = RenderTexture.GetTemporary(width, height, 24);
            RenderTexture prevActive = RenderTexture.active;
            var prevTargets = new RenderTexture[cameras.Count];
            for (int i = 0; i < cameras.Count; i++)
                prevTargets[i] = cameras[i].targetTexture;

            try
            {
                for (int i = 0; i < cameras.Count; i++)
                {
                    cameras[i].targetTexture = rt;
                    cameras[i].Render();
                }

                RenderTexture.active = rt;
                var tex = new Texture2D(width, height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0f, 0f, width, height), 0, 0);
                tex.Apply(false);
                return tex;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[WorldBuildingScenario] Thumbnail capture failed: {ex.Message}");
                return null;
            }
            finally
            {
                for (int i = 0; i < cameras.Count; i++)
                    cameras[i].targetTexture = prevTargets[i];
                RenderTexture.active = prevActive;
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        /// <summary>
        /// Every enabled camera that draws to the main display, sorted the way Unity composites
        /// them (lowest depth first), so replaying them reproduces the on-screen image: the
        /// full-screen view clears over anything below it, overlays on top keep their viewport.
        /// </summary>
        private static List<Camera> CollectScreenCameras()
        {
            var cameras = new List<Camera>();
            foreach (Camera cam in Camera.allCameras)
            {
                if (cam == null || cam.targetTexture != null || cam.targetDisplay != 0)
                    continue;
                if (cam.rect.width <= 0f || cam.rect.height <= 0f)
                    continue;
                cameras.Add(cam);
            }

            if (cameras.Count == 0 && Camera.main != null)
                cameras.Add(Camera.main);

            cameras.Sort((a, b) => a.depth.CompareTo(b.depth));
            return cameras;
        }
    }
}
