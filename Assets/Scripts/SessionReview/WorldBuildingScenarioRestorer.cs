using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SessionReview
{
    /// <summary>
    /// Rebuilds a saved World Building scenario in a freshly loaded scene.
    ///
    /// Driven by a static <see cref="SceneManager.sceneLoaded"/> hook rather than by
    /// SessionReviewManager.Start(), so restoring does not silently do nothing when the target
    /// scene has no review manager, its object starts inactive, or its Start() ordering changes.
    /// The runner destroys itself when finished.
    /// </summary>
    public class WorldBuildingScenarioRestorer : MonoBehaviour
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void InstallHook()
        {
            SceneManager.sceneLoaded -= HandleSceneLoaded;
            SceneManager.sceneLoaded += HandleSceneLoaded;
        }

        private static void HandleSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            string folder = WorldBuildingScenarioStore.TakePendingLoad();
            if (string.IsNullOrEmpty(folder))
                return;

            Debug.Log($"[WorldBuildingScenario] Scene '{scene.name}' loaded with a pending scenario; restoring '{folder}'.");
            var runner = new GameObject("WorldBuildingScenarioRestorer");
            runner.AddComponent<WorldBuildingScenarioRestorer>().Begin(folder);
        }

        private void Begin(string folder)
        {
            StartCoroutine(RestoreCoroutine(folder));
        }

        private IEnumerator RestoreCoroutine(string folder)
        {
            // Let scene Awake/Start (including the deferred PWD player spawn) settle first.
            yield return null;
            yield return null;

            try
            {
                Restore(folder);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[WorldBuildingScenario] Restore of '{folder}' failed: {ex}");
            }

            Destroy(gameObject);
        }

        private void Restore(string folder)
        {
            WorldBuildingScenarioData data = WorldBuildingScenarioStore.LoadScenarioData(folder);
            int spawnCount = data?.objects != null ? data.objects.Count : 0;
            int deltaCount = data?.sceneObjectDeltas != null ? data.sceneObjectDeltas.Count : 0;
            bool hasWeather = !string.IsNullOrEmpty(data?.weather);
            if (data == null || (spawnCount == 0 && deltaCount == 0 && !hasWeather))
            {
                Debug.LogWarning($"[WorldBuildingScenario] Saved scenario at '{folder}' is empty or unreadable; nothing restored.");
                return;
            }

            RuntimeEditorManager editor = FindRuntimeEditorManager();
            if (editor == null)
            {
                var runtimeEditorObject = new GameObject("RuntimeEditorManager");
                editor = runtimeEditorObject.AddComponent<RuntimeEditorManager>();
            }
            RuntimeEditorManager.DisableStrayEditorComponents(editor.gameObject);
            if (!editor.gameObject.activeInHierarchy)
                editor.gameObject.SetActive(true);

            int restored = 0;
            if (data.objects != null)
            {
                foreach (WorldBuildingScenarioObject entry in data.objects)
                {
                    if (entry == null)
                        continue;

                    GameObject obj = null;
                    if (!string.IsNullOrEmpty(entry.importGlbPath))
                    {
                        // Prefer the copy in the scenario's assets/ library; fall back to the
                        // original import location for scenarios saved before the library existed.
                        string glbPath = WorldBuildingScenarioStore.ResolveImportPath(folder, entry);
                        GameObject instance = MeshyGlbSceneImporter.LoadGlbInstance(
                            glbPath, entry.displayName,
                            error => Debug.LogWarning($"[WorldBuildingScenario] Import '{entry.displayName}': {error}"));
                        if (instance != null)
                        {
                            obj = editor.RestoreImportedObject(instance, glbPath, entry.displayName,
                                entry.position, Quaternion.Euler(entry.rotationEuler), entry.localScale);
                        }
                    }
                    else
                    {
                        obj = editor.RestorePaletteObject(entry.prefabName,
                            entry.position, Quaternion.Euler(entry.rotationEuler), entry.localScale,
                            entry.dynamicAgent);
                    }

                    if (obj != null)
                        restored++;
                }
            }

            // Replay the recorded changes to pre-existing scene objects (moved furniture,
            // deleted props, dragged robot/task markers) on top of the fresh base scene.
            List<GameObject> affectedSceneObjects = WorldBuildingScenarioStore.ApplySceneObjectDeltas(data);
            SyncRestoredRobotMarkers(affectedSceneObjects);
            SyncPwdSpawnerWaypoints(teleportToStart: true);

            ApplyWeather(data);

            // An untouched restored world is already on disk: don't re-prompt to save it.
            WorldBuildingScenarioStore.RestoredSignature = WorldBuildingScenarioStore.ComputeCurrentSignature();

            SessionReviewLog.Log($"[WorldBuildingScenario] Restored '{data.name}': "
                + $"{restored}/{spawnCount} placed object(s), {affectedSceneObjects.Count}/{deltaCount} scene change(s)"
                + (hasWeather ? $", weather: {data.weather}." : "."));
        }

        /// <summary>
        /// Applies the scenario's saved weather. Fog snaps on immediately (no fade) so a
        /// foggy world starts foggy instead of forming in front of the participant. The
        /// scene load already reset any previous fog, so clear weather needs no work.
        /// </summary>
        private static void ApplyWeather(WorldBuildingScenarioData data)
        {
            if (data.weather == "fog")
            {
                Weather.FogController fog = Weather.FogController.EnsureInstance();
                fog.SetPreset(data.fogPreset);
                fog.SetFog(true, immediate: true);
            }
            else if (Weather.FogController.Instance != null)
            {
                Weather.FogController.Instance.SetFog(false, immediate: true);
            }
        }

        /// <summary>Finds a scene RuntimeEditorManager even if its GameObject is inactive.</summary>
        private static RuntimeEditorManager FindRuntimeEditorManager()
        {
            RuntimeEditorManager singleton = RuntimeEditorManager.Instance;
            if (singleton != null)
                return singleton;

            foreach (RuntimeEditorManager candidate in Resources.FindObjectsOfTypeAll<RuntimeEditorManager>())
            {
                if (candidate != null && candidate.gameObject.scene.IsValid())
                    return candidate;
            }

            return null;
        }

        /// <summary>
        /// If a restored delta moved the robot goal marker or the robot itself, write the new
        /// poses into the CustomStartGoal scene Locations: NewTask re-copies robotStart/robotGoal
        /// from those markers on every (re)start, which would otherwise revert the restored
        /// layout and send ROS to the old goal.
        /// </summary>
        private static void SyncRestoredRobotMarkers(List<GameObject> affectedSceneObjects)
        {
            if (affectedSceneObjects == null || affectedSceneObjects.Count == 0)
                return;

            var sean = SEAN.SEAN.instance;
            if (sean == null)
                return;

            SEAN.Tasks.Base task;
            try { task = sean.robotTask; }
            catch (Exception) { return; }

            var custom = task as SEAN.Tasks.CustomStartGoal;
            if (custom == null)
                return;

            bool Affects(GameObject markerObj)
            {
                if (markerObj == null)
                    return false;
                foreach (GameObject affected in affectedSceneObjects)
                {
                    if (affected == null)
                        continue;
                    if (affected == markerObj || markerObj.transform.IsChildOf(affected.transform))
                        return true;
                }
                return false;
            }

            if (task.robotGoal != null && custom.RobotGoalLocation != null && Affects(task.robotGoal))
            {
                Transform goal = task.robotGoal.transform;
                custom.RobotGoalLocation.transform.SetPositionAndRotation(goal.position, goal.rotation);
                Debug.Log($"[WorldBuildingScenario] Restore moved the robot goal to {goal.position}; RobotGoalLocation updated so ROS receives the new destination.");
            }

            if (sean.robot != null && sean.robot.base_link != null && custom.RobotStartLocation != null &&
                Affects(sean.robot.base_link))
            {
                Transform baseLink = sean.robot.base_link.transform;
                custom.RobotStartLocation.transform.SetPositionAndRotation(baseLink.position, baseLink.rotation);
                Debug.Log($"[WorldBuildingScenario] Restore moved the robot to {baseLink.position}; RobotStartLocation updated to match.");
            }

            // After the robot body, so a save that moved BOTH resolves to the explicit start flag.
            if (task.robotStart != null && custom.RobotStartLocation != null && Affects(task.robotStart))
            {
                Transform start = task.robotStart.transform;
                custom.RobotStartLocation.transform.SetPositionAndRotation(start.position, start.rotation);
                Debug.Log($"[WorldBuildingScenario] Restore moved the start flag to {start.position}; RobotStartLocation updated so the robot starts there.");
            }
        }

        /// <summary>
        /// RandomAvatar bakes the spawner markers' positions into the PWD at spawn time
        /// (waypointStart/waypointGoal, spawn pose), so a marker moved AFTER spawning — by a
        /// scenario restore or live in World Building — must be re-baked into the live agent
        /// or the pedestrian keeps navigating to (and being scored against) the old goal.
        /// Restore passes teleportToStart=true (fresh scene: the agent belongs at the restored
        /// start); live World Building exit passes false (only the goal/anchor updates, the
        /// participant is not yanked around mid-session).
        /// </summary>
        public static void SyncPwdSpawnerWaypoints(bool teleportToStart)
        {
            // The player spawner wins; otherwise any spawner tells us the marker names.
            SEAN.Scenario.Agents.RandomAvatar spawner = null;
            foreach (SEAN.Scenario.Agents.RandomAvatar candidate in
                     UnityEngine.Object.FindObjectsOfType<SEAN.Scenario.Agents.RandomAvatar>(true))
            {
                if (candidate == null)
                    continue;
                if (candidate.isPwdPlayer)
                {
                    spawner = candidate;
                    break;
                }
                if (spawner == null)
                    spawner = candidate;
            }
            if (spawner == null)
                return;

            GameObject startObj = SEAN.Scenario.Agents.RandomAvatar.FindSceneObjectByName(spawner.startObjectName);
            GameObject goalObj = SEAN.Scenario.Agents.RandomAvatar.FindSceneObjectByName(spawner.goalObjectName);
            if (startObj == null && goalObj == null)
                return;

            foreach (string agentName in new[] { "PWDPlayer", "PWDAutonomous" })
            {
                GameObject agentObj = GameObject.Find(agentName);
                IVI.SFPWDAgent sfpwd = agentObj != null ? agentObj.GetComponent<IVI.SFPWDAgent>() : null;
                if (sfpwd == null || !sfpwd.useWaypoints)
                    continue;

                bool changed = false;

                if (startObj != null)
                {
                    Vector3 start = SampleNavmesh(startObj.transform.position);
                    if ((start - sfpwd.waypointStart).sqrMagnitude > 1e-4f)
                    {
                        sfpwd.waypointStart = start;
                        Quaternion startRot = Quaternion.Euler(0f, startObj.transform.eulerAngles.y, 0f);
                        // Trial restarts return the agent to the wheelchair controller's
                        // remembered spawn pose (ResetToSpawn), captured at scene load — a
                        // moved start marker must replace it, or "Run Again" after a World
                        // Building edit restarts the pedestrian at the OLD start.
                        IVI.ManualWheelchairController wheelchair =
                            agentObj.GetComponent<IVI.ManualWheelchairController>();
                        if (wheelchair != null)
                            wheelchair.SetSpawnPose(start, startRot);
                        if (teleportToStart)
                        {
                            // The agent spawned at the ORIGINAL marker; move it to the restored one.
                            agentObj.transform.position = start;
                            agentObj.transform.rotation = startRot;
                            foreach (Rigidbody rb in agentObj.GetComponentsInChildren<Rigidbody>(true))
                            {
                                if (rb == null) continue;
                                rb.velocity = Vector3.zero;
                                rb.angularVelocity = Vector3.zero;
                            }
                        }
                        changed = true;
                        Debug.Log($"[WorldBuildingScenario] {agentName} start re-synced to moved marker at {start}"
                            + (teleportToStart ? " (agent teleported)." : "."));
                    }
                }

                if (goalObj != null)
                {
                    Vector3 goal = SampleNavmesh(goalObj.transform.position);
                    if ((goal - sfpwd.waypointGoal).sqrMagnitude > 1e-4f)
                    {
                        sfpwd.waypointGoal = goal;
                        changed = true;
                        Debug.Log($"[WorldBuildingScenario] {agentName} goal re-synced to restored marker at {goal}.");
                    }
                }

                if (!changed)
                    continue;

                // Re-target a live auto-navigation. A manually driven agent killed its
                // coroutine — it just keeps the updated waypoints for arrival detection
                // and the next auto switch.
                IVI.ManualWheelchairController controller = agentObj.GetComponent<IVI.ManualWheelchairController>();
                bool manual = controller != null && controller.isManualMode;
                if (!manual && sfpwd.enabled && sfpwd.gameObject.activeInHierarchy)
                    sfpwd.RestartNavigationCoroutine();
            }
        }

        /// <summary>Same NavMesh snap the spawner applies to marker positions.</summary>
        private static Vector3 SampleNavmesh(Vector3 raw)
        {
            UnityEngine.AI.NavMeshHit hit;
            if (UnityEngine.AI.NavMesh.SamplePosition(raw, out hit, 5f, UnityEngine.AI.NavMesh.AllAreas)
                && Mathf.Abs(hit.position.y - raw.y) < 1.5f)
                return hit.position;
            return raw;
        }
    }
}
