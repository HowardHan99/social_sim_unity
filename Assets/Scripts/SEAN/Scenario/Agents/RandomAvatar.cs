using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SEAN.Scenario.Agents
{
    public enum PwdGender { Male, Female, Random }

    public class RandomAvatar : MonoBehaviour
    {
        public RuntimeAnimatorController animationController;
        public RuntimeAnimatorController pwdAnimationController;
        public GameObject[] avatars;
        public GameObject pwdAvatarPrefab;
        public LowLevelControl controller = LowLevelControl.SF;
        public bool isPlayer = false;
        static private List<GameObject> avatarsList;
        static private int numPWDSFAgentsInstantiated = 0;
        static private bool pwdPlayerSpawned = false;
        static private bool autonomousPwdSpawned = false;
        static private int lastSceneHandle = int.MinValue;

        /// <summary>Character id of the most recently spawned player ("" = wheelchair). Lets
        /// onboarding detect that the live player no longer matches the selection.</summary>
        public static string LastSpawnedCharacterId { get; private set; } = string.Empty;
        public static PwdGender LastSpawnedGender { get; private set; } = PwdGender.Male;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            avatarsList = null;
            numPWDSFAgentsInstantiated = 0;
            pwdPlayerSpawned = false;
            autonomousPwdSpawned = false;
            lastSceneHandle = int.MinValue;
            LastSpawnedCharacterId = string.Empty;
            LastSpawnedGender = PwdGender.Male;
        }

        [Header("PWD Player")]
        public bool isPwdPlayer = false;
        [Tooltip("Spawn the player during Awake (default). Untick in lightweight scenes (TestScene) where a flow manager calls SpawnPwdPlayerNow() after the user picks a character.")]
        public bool spawnPlayerOnAwake = true;
        public PwdGender pwdGender = PwdGender.Male;
        public GameObject pwdAvatarPrefabMale;
        public GameObject pwdAvatarPrefabFemale;

        [Header("PWD Start / Goal")]
        [Tooltip("Scene object name for spawn point. Searches entire hierarchy by name.")]
        public string startObjectName = "start";
        [Tooltip("Scene object name for goal point. Searches entire hierarchy by name.")]
        public string goalObjectName = "end";

        [Header("PWD Robot-Trial Route (hidden second start/end)")]
        [Tooltip("In a ROBOT trial the pedestrian spawns/goes here instead of start/end, so a player who drove the primary route while playing the human can't predict where it goes. Falls back to startObjectName/goalObjectName if not found in the scene.")]
        public string robotTrialStartObjectName = "start2";
        public string robotTrialGoalObjectName = "end2";

        public enum PwdTrialMode { AutoFromOnboarding, PedestrianTrial, RobotTrial }
        [Tooltip("Decides if THIS run is a ROBOT trial or a PEDESTRIAN trial. RobotTrial / PedestrianTrial force it directly and do NOT need onboarding -- use these when you just press Play / Run Again. AutoFromOnboarding follows the onboarding role (robot trial only when onboarding was completed with PlayerMode == Robot). In a ROBOT trial the pedestrian uses the robot-trial route AND every pedestrian start/end marker is hidden.")]
        public PwdTrialMode pwdTrialMode = PwdTrialMode.AutoFromOnboarding;

        [Header("Background PWD Gender")]
        public PwdGender bgPwdGender = PwdGender.Random;

        [Header("Walking Player Tuning")]
        [Tooltip("Max manual turn rate (deg/s) for walking player characters; the wheelchair keeps the controller default (240). Turn acceleration/coast scale down proportionally so the ramp feel stays the same. <= 0 disables the override.")]
        public float walkerTurnSpeed = 120f;

        private GameObject avatarPrefab;
        private GameObject avatarObject;
        private LowLevelControl assignedController;
        private bool spawnAutonomousPwdFromOnboarding;
        // True while the current SpawnPwdPlayer() call is building a walking (on-foot)
        // character from Resources/PlayerCharacters instead of the wheelchair pair.
        private bool spawnedWalkingCharacter;

        private void RebuildAvatarPoolIfNeeded()
        {
            if (avatarsList != null && avatarsList.Count > 0)
                return;

            avatarsList = new List<GameObject>();
            if (avatars != null)
            {
                foreach (var avatar in avatars)
                {
                    if (avatar != null)
                        avatarsList.Add(avatar);
                }
            }

            if (pwdAvatarPrefab != null)
                avatarsList.Remove(pwdAvatarPrefab);
            if (pwdAvatarPrefabMale != null)
                avatarsList.Remove(pwdAvatarPrefabMale);
            if (pwdAvatarPrefabFemale != null)
                avatarsList.Remove(pwdAvatarPrefabFemale);
        }

        private GameObject GetFallbackAvatarPrefab()
        {
            if (avatars != null)
            {
                foreach (var avatar in avatars)
                {
                    if (avatar != null && avatar != pwdAvatarPrefab && avatar != pwdAvatarPrefabMale && avatar != pwdAvatarPrefabFemale)
                        return avatar;
                }

                foreach (var avatar in avatars)
                {
                    if (avatar != null)
                        return avatar;
                }
            }

            return null;
        }

        private Animator GetAvatarAnimator(GameObject avatarInstance)
        {
            if (avatarInstance == null)
                return null;

            return avatarInstance.GetComponent<Animator>() ?? avatarInstance.GetComponentInChildren<Animator>(true);
        }

        private bool TrySpawnAvatarInstance(GameObject prefab, Vector3 position, Quaternion rotation, out GameObject instance)
        {
            instance = null;
            if (prefab == null)
                return false;

            instance = Instantiate(prefab, position, rotation);
            if (instance == null)
                return false;

            return true;
        }

        void Awake()
        {
            EnsureSceneScopedStatics();

            if (SEAN.instance)
            {
                controller = SEAN.instance.AgentController;
            }

            if (SessionReview.SessionOnboardingSettings.HasCompletedOnboarding)
            {
                ApplyOnboardingOverrides();
            }

            if (isPwdPlayer)
            {
                if (!spawnPlayerOnAwake)
                {
                    // Deferred: a flow manager (e.g. TestSceneFlowManager) calls
                    // SpawnPwdPlayerNow() once the user has picked a character.
                    return;
                }
                if (pwdPlayerSpawned)
                {
                    Debug.LogWarning($"[PWD] Duplicate isPwdPlayer on '{gameObject.name}' -- already spawned. Spawning as normal agent instead.", this);
                    SpawnBackgroundAgent();
                }
                else
                {
                    pwdPlayerSpawned = true;
                    SpawnPwdPlayer();
                    return; // PWDPlayer is a root object, skip parenting below
                }
            }
            else if (isPlayer)
            {
                avatarObject = Instantiate(avatars[0], transform.position, transform.rotation);
                Animator animator = GetAvatarAnimator(avatarObject);
                if (animator != null)
                    animator.runtimeAnimatorController = animationController;
                else
                    Debug.LogWarning($"[RandomAvatar] No Animator found on player avatar prefab '{avatars[0].name}'.", this);
                avatarObject.AddComponent<PlayerAgent>();
            }
            else if (spawnAutonomousPwdFromOnboarding)
            {
                if (autonomousPwdSpawned)
                {
                    Debug.LogWarning($"[PWD] Duplicate autonomous onboarding spawn on '{gameObject.name}' suppressed.", this);
                    gameObject.SetActive(false);
                    return;
                }

                autonomousPwdSpawned = true;
                SpawnAutonomousPwdAgent();
                return;
            }
            else
            {
                SpawnBackgroundAgent();
            }

            if (avatarObject != null)
            {
                avatarObject.transform.parent = transform;
            }
        }

        private static void EnsureSceneScopedStatics()
        {
            int sceneHandle = SceneManager.GetActiveScene().handle;
            if (sceneHandle == lastSceneHandle)
                return;

            avatarsList = null;
            numPWDSFAgentsInstantiated = 0;
            pwdPlayerSpawned = false;
            autonomousPwdSpawned = false;
            lastSceneHandle = sceneHandle;
        }

        private void ApplyOnboardingOverrides()
        {
            if (!isPwdPlayer)
                return;
            pwdGender = SessionReview.SessionOnboardingSettings.SelectedPwdGender;
            spawnAutonomousPwdFromOnboarding = false;
        }

        /// <summary>
        /// Spawns the player immediately using the current SessionOnboardingSettings
        /// character/gender selection. For scenes with spawnPlayerOnAwake unticked.
        /// </summary>
        public void SpawnPwdPlayerNow()
        {
            if (!isPwdPlayer)
            {
                Debug.LogWarning($"[PWD] SpawnPwdPlayerNow called on '{gameObject.name}' which is not an isPwdPlayer spawner.", this);
                return;
            }
            if (pwdPlayerSpawned)
            {
                Debug.LogWarning("[PWD] SpawnPwdPlayerNow: player already spawned.", this);
                return;
            }

            pwdGender = SessionReview.SessionOnboardingSettings.SelectedPwdGender;
            pwdPlayerSpawned = true;
            SpawnPwdPlayer();
        }

        /// <summary>
        /// Selected walking character from Resources/PlayerCharacters, or the built-in
        /// gendered wheelchair pair when no character is selected / it can't be found.
        /// Sets spawnedWalkingCharacter and the LastSpawned* statics as a side effect.
        /// </summary>
        private GameObject ResolvePlayerCharacterPrefab()
        {
            string characterId = SessionReview.SessionOnboardingSettings.SelectedPlayerCharacterId;
            if (!string.IsNullOrEmpty(characterId))
            {
                GameObject prefab = SessionReview.PlayerCharacterLibrary.FindPrefab(characterId);
                if (prefab != null)
                {
                    spawnedWalkingCharacter = true;
                    LastSpawnedCharacterId = characterId;
                    LastSpawnedGender = pwdGender;
                    return prefab;
                }
                Debug.LogWarning($"[PWD] Player character '{characterId}' not found in Resources/PlayerCharacters; falling back to wheelchair.", this);
            }

            spawnedWalkingCharacter = false;
            LastSpawnedCharacterId = string.Empty;
            LastSpawnedGender = pwdGender;
            return ResolvePwdPrefab(pwdGender);
        }

        private void SpawnPwdPlayer()
        {
            // Populate the shared static avatarsList so that other agents
            // (e.g. those spawned later by NavManager) can pick from it.
            // Without this, the early return below would leave the list empty.
            if (avatarsList is null || avatarsList.Count == 0)
            {
                RebuildAvatarPoolIfNeeded();
            }

            avatarPrefab = ResolvePlayerCharacterPrefab();
            if (avatarPrefab == null)
            {
                Debug.LogError("No PWD avatar prefab assigned for gender: " + pwdGender, this);
                return;
            }

            // Two authored routes. HUMAN (pedestrian) trial uses the primary start/end that the
            // player drives; ROBOT trial uses a separate start/end so the robot player -- who may
            // have driven the primary route while playing the human -- can't predict the pedestrian.
            bool useRobotTrialRoute;
            switch (pwdTrialMode)
            {
                case PwdTrialMode.RobotTrial: useRobotTrialRoute = true; break;
                case PwdTrialMode.PedestrianTrial: useRobotTrialRoute = false; break;
                default:
                    useRobotTrialRoute =
                        SessionReview.SessionOnboardingSettings.HasCompletedOnboarding &&
                        SessionReview.SessionOnboardingSettings.PlayerMode == SessionReview.OnboardingPlayerMode.Robot;
                    break;
            }
            Debug.Log($"[PWD] pwdTrialMode={pwdTrialMode} -> useRobotTrialRoute={useRobotTrialRoute} " +
                      $"(onboarding={SessionReview.SessionOnboardingSettings.HasCompletedOnboarding}, playerMode={SessionReview.SessionOnboardingSettings.PlayerMode})");

            GameObject startObj = FindByName(useRobotTrialRoute ? robotTrialStartObjectName : startObjectName, !useRobotTrialRoute);
            GameObject goalObj = FindByName(useRobotTrialRoute ? robotTrialGoalObjectName : goalObjectName, !useRobotTrialRoute);
            if (useRobotTrialRoute && startObj == null)
            {
                Debug.LogWarning($"[PWD] Robot-trial start '{robotTrialStartObjectName}' not found; falling back to '{startObjectName}'.");
                startObj = FindByName(startObjectName);
            }
            if (useRobotTrialRoute && goalObj == null)
            {
                Debug.LogWarning($"[PWD] Robot-trial goal '{robotTrialGoalObjectName}' not found; falling back to '{goalObjectName}'.");
                goalObj = FindByName(goalObjectName);
            }
            Debug.Log($"[PWD] Route selection: {(useRobotTrialRoute ? "ROBOT trial (hidden second route)" : "HUMAN/pedestrian trial (primary route)")}");

            Vector3 rawPos = startObj != null ? startObj.transform.position : transform.position;
            UnityEngine.AI.NavMeshHit navHit;
            Vector3 spawnPos = rawPos;
            if (UnityEngine.AI.NavMesh.SamplePosition(rawPos, out navHit, 5f, UnityEngine.AI.NavMesh.AllAreas)
                && Mathf.Abs(navHit.position.y - rawPos.y) < 1.5f)
                spawnPos = navHit.position;

            float yAngle = startObj != null ? startObj.transform.eulerAngles.y : transform.eulerAngles.y;
            Quaternion spawnRot = Quaternion.Euler(0f, yAngle, 0f);

            // Instantiate as a ROOT object (no parent). This avoids all parent-child
            // Rigidbody issues. Background agents are parented because NavManager needs
            // the hierarchy, but the PWD player has its own controller.
            avatarObject = Instantiate(avatarPrefab, spawnPos, spawnRot);
            avatarObject.name = "PWDPlayer";

            // Root-or-child lookup: prefabs whose rig lives in a nested model instance
            // (Wheelchair_female 1) carry the Animator below the root.
            Animator animator = GetAvatarAnimator(avatarObject);
            RuntimeAnimatorController playerAnimController = spawnedWalkingCharacter ? animationController : pwdAnimationController;
            if (animator == null)
                Debug.LogWarning($"[PWD] Avatar prefab '{avatarPrefab.name}' has no Animator; the player will not animate.", this);
            else if (playerAnimController != null)
                animator.runtimeAnimatorController = playerAnimController;
            else
                Debug.LogWarning($"[PWD] No {(spawnedWalkingCharacter ? "walking" : "wheelchair")} animation controller assigned on '{gameObject.name}'.", this);

            Vector3 goalPos = spawnPos;
            if (goalObj != null)
            {
                Vector3 goalRaw = goalObj.transform.position;
                if (UnityEngine.AI.NavMesh.SamplePosition(goalRaw, out navHit, 5f, UnityEngine.AI.NavMesh.AllAreas)
                    && Mathf.Abs(navHit.position.y - goalRaw.y) < 1.5f)
                    goalPos = navHit.position;
                else
                    goalPos = goalRaw;
            }

            var sfpwd = avatarObject.AddComponent<IVI.SFPWDAgent>();
            sfpwd.useWaypoints = true;
            sfpwd.waypointStart = spawnPos;
            sfpwd.waypointGoal = goalPos;

            // Hide the pedestrian markers at load. They're only shown during the PEDESTRIAN trial
            // (ApplyTrialRoute) and while world-building (RuntimeEditorManager). ApplyTrialRoute sets
            // the authoritative visibility at trial start, once the "You play" role is known.
            SetPedestrianMarkersVisible(false);

            if (spawnedWalkingCharacter)
            {
                // Standing avatar: keep the Base-computed capsule center (seated recenter
                // would sink it) and claim normal pedestrian personal space, not the
                // wheelchair's doubled radius.
                sfpwd.applyWheelchairColliderCenter = false;
                sfpwd.pwdPersonalRadius = Base.RADIUS;
            }

            // Reuse existing ManualWheelchairController from the prefab if present;
            // only add a new one if the prefab doesn't have one.
            var manualCtrl = avatarObject.GetComponent<IVI.ManualWheelchairController>();
            if (manualCtrl == null)
                manualCtrl = avatarObject.AddComponent<IVI.ManualWheelchairController>();
            manualCtrl.enabled = true;
            manualCtrl.startInManualMode = SessionReview.SessionOnboardingSettings.PwdStartupControl == SessionReview.StartupControlMode.Manual;

            if (spawnedWalkingCharacter && walkerTurnSpeed > 0f && manualCtrl.rotationSpeed > 0f)
            {
                // Walking humans turn slower than the wheelchair. Scale the angular
                // accelerations by the same ratio so time-to-full-turn feels unchanged.
                float turnScale = walkerTurnSpeed / manualCtrl.rotationSpeed;
                manualCtrl.inertiaAngularAcceleration *= turnScale;
                manualCtrl.inertiaAngularCoastDeceleration *= turnScale;
                manualCtrl.manualAngularAcceleration *= turnScale;
                manualCtrl.rotationSpeed = walkerTurnSpeed;
            }

            AttachCameraToHead(avatarObject, spawnedWalkingCharacter);
            AttachPlayerMiniScreens(avatarObject, spawnedWalkingCharacter);

            // Disable the Agent_X scene object since PWDPlayer is independent
            gameObject.SetActive(false);

            Debug.Log($"[PWD] Spawned PWDPlayer at ({spawnPos.x:F1},{spawnPos.y:F1},{spawnPos.z:F1}), " +
                      $"goal=({goalPos.x:F1},{goalPos.y:F1},{goalPos.z:F1}), " +
                      $"startObj={(startObj != null ? startObj.name : "null")}, " +
                      $"goalObj={(goalObj != null ? goalObj.name : "null")}");
        }

        private void SpawnAutonomousPwdAgent()
        {
            if (avatarsList is null || avatarsList.Count == 0)
            {
                RebuildAvatarPoolIfNeeded();
            }

            avatarPrefab = ResolvePwdPrefab(bgPwdGender);
            if (avatarPrefab == null)
            {
                Debug.LogError("No autonomous PWD avatar prefab assigned.", this);
                return;
            }

            GameObject startObj = FindByName(startObjectName);
            GameObject goalObj = FindByName(goalObjectName);

            Vector3 rawPos = startObj != null ? startObj.transform.position : transform.position;
            UnityEngine.AI.NavMeshHit navHit;
            Vector3 spawnPos = rawPos;
            if (UnityEngine.AI.NavMesh.SamplePosition(rawPos, out navHit, 5f, UnityEngine.AI.NavMesh.AllAreas)
                && Mathf.Abs(navHit.position.y - rawPos.y) < 1.5f)
                spawnPos = navHit.position;

            float yAngle = startObj != null ? startObj.transform.eulerAngles.y : transform.eulerAngles.y;
            Quaternion spawnRot = Quaternion.Euler(0f, yAngle, 0f);

            avatarObject = Instantiate(avatarPrefab, spawnPos, spawnRot);
            avatarObject.name = "PWDAutonomous";

            Animator animator = avatarObject.GetComponent<Animator>();
            if (animator != null && pwdAnimationController != null)
                animator.runtimeAnimatorController = pwdAnimationController;

            Vector3 goalPos = spawnPos;
            if (goalObj != null)
            {
                Vector3 goalRaw = goalObj.transform.position;
                if (UnityEngine.AI.NavMesh.SamplePosition(goalRaw, out navHit, 5f, UnityEngine.AI.NavMesh.AllAreas)
                    && Mathf.Abs(navHit.position.y - goalRaw.y) < 1.5f)
                    goalPos = navHit.position;
                else
                    goalPos = goalRaw;
            }

            var sfpwd = avatarObject.GetComponent<IVI.SFPWDAgent>();
            if (sfpwd == null)
                sfpwd = avatarObject.AddComponent<IVI.SFPWDAgent>();
            sfpwd.useWaypoints = true;
            sfpwd.waypointStart = spawnPos;
            sfpwd.waypointGoal = goalPos;

            var manualCtrl = avatarObject.GetComponent<IVI.ManualWheelchairController>();
            if (manualCtrl != null)
                manualCtrl.enabled = false;

            gameObject.SetActive(false);

            Debug.Log($"[PWD] Spawned autonomous PWD at ({spawnPos.x:F1},{spawnPos.y:F1},{spawnPos.z:F1}), " +
                      $"goal=({goalPos.x:F1},{goalPos.y:F1},{goalPos.z:F1})");
        }

        /// <summary>
        /// Re-selects and re-applies the pedestrian's route AFTER the role is chosen in the
        /// "You play: Robot/Human" prompt. The PWD is spawned in Awake (before the role exists),
        /// so this must run at trial start to actually move it. Called by SessionReviewManager.
        /// robotTrialFromRole = (selectedPlayerMode == Robot). pwdTrialMode can force it either way.
        /// </summary>
        public void ApplyTrialRoute(bool robotTrialFromRole)
        {
            if (!isPwdPlayer)
                return;

            bool useRobotTrialRoute;
            switch (pwdTrialMode)
            {
                case PwdTrialMode.RobotTrial: useRobotTrialRoute = true; break;
                case PwdTrialMode.PedestrianTrial: useRobotTrialRoute = false; break;
                default: useRobotTrialRoute = robotTrialFromRole; break;
            }

            // Target the ACTUAL player-driven pedestrian by name. When multiple isPwdPlayer
            // RandomAvatars exist (e.g. the NavManager agentPrefab carries isPwdPlayer, so every
            // background agent has it too), avatarObject on the one we were called through may be a
            // BACKGROUND agent -- moving that teleports a random background pedestrian to start2 and
            // leaves the real player behind. The real player is always the root object "PWDPlayer".
            GameObject pwd = GameObject.Find("PWDPlayer");
            if (pwd == null) pwd = avatarObject; // fallback if it's momentarily inactive
            if (pwd == null)
            {
                Debug.LogWarning("[PWD] ApplyTrialRoute: PWDPlayer not found.");
                return;
            }

            GameObject startObj = FindByName(useRobotTrialRoute ? robotTrialStartObjectName : startObjectName, false);
            GameObject goalObj = FindByName(useRobotTrialRoute ? robotTrialGoalObjectName : goalObjectName, false);
            if (useRobotTrialRoute && startObj == null) startObj = FindByName(startObjectName, false);
            if (useRobotTrialRoute && goalObj == null) goalObj = FindByName(goalObjectName, false);

            Vector3 spawnPos = SampleOnNavMesh(startObj, pwd.transform.position);
            Vector3 goalPos = SampleOnNavMesh(goalObj, spawnPos);
            float yAngle = startObj != null ? startObj.transform.eulerAngles.y : pwd.transform.eulerAngles.y;
            Quaternion spawnRot = Quaternion.Euler(0f, yAngle, 0f);

            var sfpwd = pwd.GetComponent<IVI.SFPWDAgent>();
            if (sfpwd != null)
            {
                sfpwd.useWaypoints = true;
                sfpwd.waypointStart = spawnPos;
                sfpwd.waypointGoal = goalPos;
            }
            var mwc = pwd.GetComponent<IVI.ManualWheelchairController>();
            if (mwc != null)
                mwc.SetSpawnPose(spawnPos, spawnRot); // so ResetToSpawn uses the new start
            pwd.transform.SetPositionAndRotation(spawnPos, spawnRot);

            // Robot trial: hide every pedestrian marker so the robot player can't see the route.
            // Pedestrian trial: show them again (the human needs to see the goal). The connecting
            // line (TargetFlagArrow) stays hidden regardless -- it's disabled at the prefab level.
            SetPedestrianMarkersVisible(!useRobotTrialRoute);

            Debug.Log($"[PWD] ApplyTrialRoute: robotTrial={useRobotTrialRoute} (role={robotTrialFromRole}, mode={pwdTrialMode}); " +
                      $"start=({spawnPos.x:F1},{spawnPos.z:F1}) goal=({goalPos.x:F1},{goalPos.z:F1}); markers {(useRobotTrialRoute ? "HIDDEN" : "visible")}.");
        }

        private static Vector3 SampleOnNavMesh(GameObject obj, Vector3 fallback)
        {
            if (obj == null) return fallback;
            Vector3 raw = obj.transform.position;
            UnityEngine.AI.NavMeshHit hit;
            if (UnityEngine.AI.NavMesh.SamplePosition(raw, out hit, 5f, UnityEngine.AI.NavMesh.AllAreas)
                && Mathf.Abs(hit.position.y - raw.y) < 1.5f)
                return hit.position;
            return raw;
        }

        /// <summary>
        /// Show/hide the pedestrian start/end markers on every isPwdPlayer agent. Hidden at load and
        /// during robot trials; shown during the pedestrian trial (ApplyTrialRoute) and while
        /// world-building (RuntimeEditorManager enter/exit).
        /// </summary>
        public static void SetAllPedestrianMarkersVisible(bool visible)
        {
            foreach (var ra in FindObjectsOfType<RandomAvatar>(true))
                if (ra != null && ra.isPwdPlayer)
                    ra.SetPedestrianMarkersVisible(visible);
        }

        /// <summary>
        /// Enables/disables the renderers of every pedestrian start/end marker (both routes).
        /// GameObjects stay active so their positions remain resolvable by name.
        /// </summary>
        private void SetPedestrianMarkersVisible(bool visible)
        {
            string[] markerNames = { startObjectName, goalObjectName, robotTrialStartObjectName, robotTrialGoalObjectName };
            foreach (string markerName in markerNames)
            {
                if (string.IsNullOrEmpty(markerName)) continue;
                GameObject marker = FindByName(markerName, false);
                if (marker == null) continue;
                foreach (Renderer r in marker.GetComponentsInChildren<Renderer>(true))
                    r.enabled = visible;
            }
        }

        private static GameObject FindByName(string objectName)
        {
            return FindByName(objectName, verbose: true);
        }

        /// <summary>
        /// Same name-based scene lookup spawning uses, for World Building (marker registration,
        /// scenario restore) — so "the object the PWD navigates to" resolves identically there.
        /// Quiet: callers probe generic names ("start"/"end") in scenes that may lack them.
        /// </summary>
        public static GameObject FindSceneObjectByName(string objectName)
        {
            return FindByName(objectName, verbose: false);
        }

        private static GameObject FindByName(string objectName, bool verbose)
        {
            if (string.IsNullOrEmpty(objectName)) return null;

            // Try direct Find first (works for root objects and full paths from root)
            GameObject obj = GameObject.Find(objectName);
            if (obj != null)
            {
                if (verbose)
                    Debug.Log($"[PWD] Found '{objectName}' directly at ({obj.transform.position.x:F1},{obj.transform.position.y:F1},{obj.transform.position.z:F1})");
                return obj;
            }

            // Path-based Find failed. Search all objects by the leaf name.
            string leafName = objectName;
            int lastSlash = objectName.LastIndexOf('/');
            if (lastSlash >= 0)
                leafName = objectName.Substring(lastSlash + 1);

            foreach (GameObject root in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
            {
                Transform found = SearchChildrenRecursive(root.transform, leafName);
                if (found != null)
                {
                    if (verbose)
                        Debug.Log($"[PWD] Found '{leafName}' (from '{objectName}') via recursive search at ({found.position.x:F1},{found.position.y:F1},{found.position.z:F1})");
                    return found.gameObject;
                }
            }

            if (verbose)
                Debug.LogError($"[PWD] Object '{objectName}' NOT FOUND anywhere in scene! Check Inspector name.");
            return null;
        }

        private static Transform SearchChildrenRecursive(Transform parent, string name)
        {
            if (parent.name == name) return parent;
            for (int i = 0; i < parent.childCount; i++)
            {
                Transform found = SearchChildrenRecursive(parent.GetChild(i), name);
                if (found != null) return found;
            }
            return null;
        }

        private void AttachCameraToHead(GameObject avatar, bool standing)
        {
            // Third-person camera: temporarily parent to avatar so
            // WheelchairCameraSmoothing.Start() can read the follow target,
            // then the script un-parents itself to orbit freely.
            Transform wheelchairCam = avatar.transform.Find("wheelchairCamera");
            if (wheelchairCam == null)
            {
                foreach (Transform child in avatar.GetComponentsInChildren<Transform>(true))
                {
                    if (child.name.ToLower().Contains("wheelchaircamera") ||
                        child.name.ToLower().Contains("wheelchair_camera"))
                    {
                        wheelchairCam = child;
                        break;
                    }
                }
            }

            // A standing avatar is a head taller than the seated wheelchair user, so the
            // orbit camera sits higher/farther and aims at standing chest height.
            Vector3 thirdPersonOffset = standing ? new Vector3(0f, 2.2f, -2.2f) : new Vector3(0f, 1.9f, -1.5f);
            float lookAtHeight = standing ? 1.5f : 1.0f;
            Vector3 spawnPos = avatar.transform.position + avatar.transform.rotation * thirdPersonOffset;

            if (wheelchairCam != null)
            {
                wheelchairCam.SetParent(avatar.transform, false);
                wheelchairCam.position = spawnPos;
                wheelchairCam.LookAt(avatar.transform.position + Vector3.up * lookAtHeight);

                Camera cam = wheelchairCam.GetComponent<Camera>();
                if (cam != null)
                {
                    cam.targetDisplay = 1;
                    if (cam.GetComponent<ComfortMotionBlur>() == null)
                        cam.gameObject.AddComponent<ComfortMotionBlur>();
                    if (cam.GetComponent<CenterAnchorOverlay>() == null)
                        cam.gameObject.AddComponent<CenterAnchorOverlay>();
                }

                var smoothing = wheelchairCam.GetComponent<IVI.WheelchairCameraSmoothing>();
                if (smoothing == null)
                    smoothing = wheelchairCam.gameObject.AddComponent<IVI.WheelchairCameraSmoothing>();
                smoothing.thirdPersonOffset = thirdPersonOffset;
            }
            else
            {
                GameObject camObj = new GameObject("PWDThirdPersonCamera");
                camObj.transform.SetParent(avatar.transform, false);
                camObj.transform.position = spawnPos;
                camObj.transform.LookAt(avatar.transform.position + Vector3.up * lookAtHeight);

                Camera cam = camObj.AddComponent<Camera>();
                cam.targetDisplay = 1;
                cam.fieldOfView = 60f;
                cam.nearClipPlane = 0.1f;
                if (cam.GetComponent<ComfortMotionBlur>() == null)
                    cam.gameObject.AddComponent<ComfortMotionBlur>();
                if (cam.GetComponent<CenterAnchorOverlay>() == null)
                    cam.gameObject.AddComponent<CenterAnchorOverlay>();

                var smoothing = camObj.AddComponent<IVI.WheelchairCameraSmoothing>();
                smoothing.thirdPersonOffset = thirdPersonOffset;
            }

            Debug.Log($"[PWD] Third-person camera attached to avatar '{avatar.name}'");

            foreach (var camScript in avatar.GetComponentsInChildren<IVI.CameraScript>(true))
                camScript.allowMouseScrollZoom = false;
        }

        // Player-side equivalent of the robot's top-corner mini views
        // (Robot.camera_overhead + Robot.camera_first). Adds a top-down and a
        // first-person panel in the top corners. The robot's bottom-right
        // rear-view mini is deliberately NOT mirrored here — only the robot
        // gets a rear camera. They are
        // created DISABLED on the main display (0); SessionReviewManager owns their
        // lifecycle: ActivatePwdCameraAsMain() enables them (keeping display 0)
        // while a human drives the PWD, and RestoreRobotGameplayCameras() hides
        // them for robot play (both recognize the names below). This avoids them
        // overlapping the robot's own minis during onboarding / robot play.
        // View-only: no ROS publishing and no extra AudioListener.
        private void AttachPlayerMiniScreens(GameObject avatar, bool standing)
        {
            // Match the PWD main view, which SessionReviewManager moves to display 0
            // when the human drives the PWD. Higher depth so the panels draw on top
            // of the full-screen main view within their rects.
            const int playerDisplay = 0;
            const float miniDepth = 20f;

            // Top-left top-down and top-right first-person, matching the robot
            // layout (OverheadCamera.prefab uses the same 0.03/0.72 rect).
            var topDownRect = new Rect(0.03f, 0.72f, 0.25f, 0.25f);
            var firstPersonRect = new Rect(0.72f, 0.72f, 0.25f, 0.25f);

            // Top-down: orthographic, looking straight down, following the avatar.
            // Parented to the avatar so it tracks position like the robot overhead.
            GameObject topDownObj = new GameObject("PWDOverheadCamera");
            topDownObj.transform.SetParent(avatar.transform, false);
            topDownObj.transform.localPosition = new Vector3(0f, 3.5f, 0f);
            topDownObj.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            Camera topDownCam = topDownObj.AddComponent<Camera>();
            topDownCam.orthographic = true;
            topDownCam.orthographicSize = 3f;
            topDownCam.nearClipPlane = 0.3f;
            topDownCam.farClipPlane = 200f;
            topDownCam.clearFlags = CameraClearFlags.Skybox;
            topDownCam.targetDisplay = playerDisplay;
            topDownCam.rect = topDownRect;
            topDownCam.depth = miniDepth;
            topDownCam.enabled = false; // SessionReviewManager enables during PWD play

            // First-person: forward-facing from roughly eye height, offset slightly
            // ahead so the avatar's own head does not fill the panel. Kept level by
            // FirstPersonCameraLevel so wheelchair pitch/roll never tilts the view
            // (it un-parents and drives its own transform, so no static local pose).
            GameObject firstPersonObj = new GameObject("PWDFirstPersonCamera");
            firstPersonObj.transform.SetParent(avatar.transform, false);
            Camera firstPersonCam = firstPersonObj.AddComponent<Camera>();
            firstPersonCam.fieldOfView = 60f;
            firstPersonCam.nearClipPlane = 0.1f;
            firstPersonCam.farClipPlane = 200f;
            firstPersonCam.clearFlags = CameraClearFlags.Skybox;
            firstPersonCam.targetDisplay = playerDisplay;
            firstPersonCam.rect = firstPersonRect;
            firstPersonCam.depth = miniDepth;
            firstPersonCam.enabled = false; // SessionReviewManager enables during PWD play
            var firstPersonLevel = firstPersonObj.AddComponent<IVI.FirstPersonCameraLevel>();
            // Standing eye height vs seated wheelchair eye height.
            firstPersonLevel.eyeOffset = standing ? new Vector3(0f, 1.65f, 0.35f) : new Vector3(0f, 1.15f, 0.35f);

            Debug.Log($"[PWD] Created top-down + first-person mini screens on '{avatar.name}' (enabled during PWD play)");
        }

        private Transform FindBoneRecursive(Transform root, string boneName)
        {
            foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
            {
                if (child.name.EndsWith(boneName) && !child.name.EndsWith("Top_End"))
                {
                    return child;
                }
            }
            return null;
        }

        private GameObject ResolvePwdPrefab(PwdGender gender)
        {
            switch (gender)
            {
                case PwdGender.Male:
                    return pwdAvatarPrefabMale != null ? pwdAvatarPrefabMale : pwdAvatarPrefab;
                case PwdGender.Female:
                    return pwdAvatarPrefabFemale != null ? pwdAvatarPrefabFemale : pwdAvatarPrefab;
                case PwdGender.Random:
                    bool pickMale = Random.value > 0.5f;
                    if (pickMale)
                        return pwdAvatarPrefabMale != null ? pwdAvatarPrefabMale : pwdAvatarPrefab;
                    else
                        return pwdAvatarPrefabFemale != null ? pwdAvatarPrefabFemale : pwdAvatarPrefab;
                default:
                    return pwdAvatarPrefab;
            }
        }

        private void SpawnBackgroundAgent()
        {
            int numPWDSFAgentsToSpawn = SEAN.instance ? SEAN.instance.numPwDSFAgents : 0;
            assignedController = controller;

            if (numPWDSFAgentsInstantiated < numPWDSFAgentsToSpawn)
            {
                avatarPrefab = ResolvePwdPrefab(bgPwdGender);
                if (avatarPrefab == null)
                    avatarPrefab = pwdAvatarPrefab;

                assignedController = LowLevelControl.PWDSF;
                numPWDSFAgentsInstantiated++;
            }
            else
            {
                if (avatarsList is null || avatarsList.Count == 0)
                {
                    RebuildAvatarPoolIfNeeded();
                }

                if (avatarPrefab is null && avatarsList.Count > 0)
                {
                    int randomIndex = Random.Range(0, avatarsList.Count);
                    avatarPrefab = avatarsList[randomIndex];
                    avatarsList.RemoveAt(randomIndex);
                }

                if (avatarPrefab == null)
                {
                    avatarPrefab = GetFallbackAvatarPrefab();
                    if (avatarPrefab != null)
                    {
                        Debug.LogWarning($"[RandomAvatar] Avatar pool was empty on '{name}'. Falling back to '{avatarPrefab.name}'.", this);
                    }
                }
            }

            if (avatarPrefab != null)
            {
                if (!TrySpawnAvatarInstance(avatarPrefab, transform.position, transform.rotation, out avatarObject))
                {
                    Debug.LogError($"[RandomAvatar] Failed to instantiate avatar prefab '{avatarPrefab.name}' on '{name}'.", this);
                    return;
                }

                Animator animator = GetAvatarAnimator(avatarObject);

                if (animator == null)
                {
                    Debug.LogWarning($"[RandomAvatar] No Animator found on spawned avatar '{avatarPrefab.name}' for '{name}'.", this);
                }
                else if (assignedController == LowLevelControl.PWDSF && pwdAnimationController != null)
                {
                    animator.runtimeAnimatorController = pwdAnimationController;
                }
                else
                {
                    animator.runtimeAnimatorController = animationController;
                }

                if (assignedController == LowLevelControl.PWDSF)
                {
                    avatarObject.AddComponent<IVI.SFPWDAgent>();
                }
                else if (assignedController == LowLevelControl.SF)
                {
                    avatarObject.AddComponent<IVI.SFAgent>();
                }
                else if (assignedController == LowLevelControl.ORCA)
                {
                    avatarObject.AddComponent<ORCA.Agent>();
                }
            }
            else
            {
                Debug.LogError("Could not determine avatar prefab to instantiate.", this);
            }
        }
    }
}
