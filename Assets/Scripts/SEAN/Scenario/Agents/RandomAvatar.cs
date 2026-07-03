using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SEAN.Scenario.Agents
{
    public enum PwdCharacter { MaleWheelchairUser, FemaleWheelchairUser, Random, ScooterUser, DogWalker, Cyclist, WalkerUser, WhiteCaneUser, CaneUser, FemaleChild, MaleChild, PhoneUser }

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

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            avatarsList = null;
            numPWDSFAgentsInstantiated = 0;
            pwdPlayerSpawned = false;
            autonomousPwdSpawned = false;
            lastSceneHandle = int.MinValue;
        }

        [Header("PWD Player")]
        public bool isPwdPlayer = false;
        public PwdCharacter pwdCharacter = PwdCharacter.MaleWheelchairUser;
        public GameObject pwdAvatarPrefabMale;
        public GameObject pwdAvatarPrefabFemale;
        public GameObject pwdAvatarPrefabScooteruser;
        public GameObject pwdAvatarPrefabDogwalker;
        public GameObject pwdAvatarPrefabCyclist;
        public GameObject pwdAvatarPrefabWalker;
        public GameObject pwdAvatarPrefabWhiteCane;
        public GameObject pwdAvatarPrefabCane;
        public GameObject pwdAvatarPrefabFemaleChild;
        public GameObject pwdAvatarPrefabMaleChild;
        public GameObject pwdAvatarPrefabPhoneUser;

        [Header("PWD Start / Goal")]
        [Tooltip("Scene object name for spawn point. Searches entire hierarchy by name.")]
        public string startObjectName = "start";
        [Tooltip("Scene object name for goal point. Searches entire hierarchy by name.")]
        public string goalObjectName = "end";

        [Header("Background PWD Gender")]
        public PwdCharacter bgPwdCharacter = PwdCharacter.Random;

        private GameObject avatarPrefab;
        private GameObject avatarObject;
        private LowLevelControl assignedController;
        private bool spawnAutonomousPwdFromOnboarding;

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
            if (pwdAvatarPrefabScooteruser != null)
                avatarsList.Remove(pwdAvatarPrefabScooteruser);
            if (pwdAvatarPrefabDogwalker != null)
                avatarsList.Remove(pwdAvatarPrefabDogwalker);
        }

        private GameObject GetFallbackAvatarPrefab()
        {
            if (avatars != null)
            {
                foreach (var avatar in avatars)
                {
                    if (avatar != null && avatar != pwdAvatarPrefab && avatar != pwdAvatarPrefabMale && avatar != pwdAvatarPrefabFemale && avatar != pwdAvatarPrefabScooteruser && avatar != pwdAvatarPrefabDogwalker)
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
            return IVI.AvatarAnimatorUtility.GetLocomotionAnimator(avatarInstance);
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

            Debug.Log("Awake: HasCompletedOnboarding " + SessionReview.SessionOnboardingSettings.HasCompletedOnboarding);

            if (SessionReview.SessionOnboardingSettings.HasCompletedOnboarding)
            {
                Debug.Log("ApplyOnboardingOverrides in Awake");
                ApplyOnboardingOverrides();
            }

            if (isPwdPlayer)
            {
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
            Debug.Log("ApplyOnboardingOverrides");
            if (!isPwdPlayer)
                return;
            pwdCharacter = SessionReview.SessionOnboardingSettings.SelectedPwdCharacter;
            Debug.Log("pwdCharacter in ApplyOnboardingOverrides: " + pwdCharacter);
            spawnAutonomousPwdFromOnboarding = false;
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
            Debug.Log("SpawnPwdPlayer: " + pwdCharacter);
            avatarPrefab = ResolvePwdPrefab(pwdCharacter);
            if (avatarPrefab == null)
            {
                Debug.LogError("No PWD avatar prefab assigned for gender: " + pwdCharacter, this);
                return;
            }

            GameObject startObj = FindByName(startObjectName);
            GameObject goalObj = FindByName(goalObjectName);

            Vector3 rawPos = startObj != null ? startObj.transform.position : transform.position;
            float yAngle = startObj != null ? startObj.transform.eulerAngles.y : transform.eulerAngles.y;
            Quaternion spawnRot = Quaternion.Euler(0f, yAngle, 0f);

            UnityEngine.AI.NavMeshHit navHit;
            Vector3 spawnPos = rawPos;
            if (UnityEngine.AI.NavMesh.SamplePosition(rawPos, out navHit, 5f, UnityEngine.AI.NavMesh.AllAreas)
                && Mathf.Abs(navHit.position.y - rawPos.y) < 1.5f)
                spawnPos = navHit.position;

            // Instantiate as a ROOT object (no parent). This avoids all parent-child
            // Rigidbody issues. Background agents are parented because NavManager needs
            // the hierarchy, but the PWD player has its own controller.
            avatarObject = Instantiate(avatarPrefab, spawnPos, spawnRot);
            avatarObject.name = "PWDPlayer";

            Animator animator = GetAvatarAnimator(avatarObject);
            if (animator != null
                && pwdAnimationController != null
                && PwdCharacterUtility.UsesWheelchairPlayerAnimation(pwdCharacter)
                && animator.runtimeAnimatorController == null)
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

            var sfpwd = avatarObject.AddComponent<IVI.SFPWDAgent>();
            sfpwd.useWaypoints = true;
            sfpwd.waypointStart = spawnPos;
            sfpwd.waypointGoal = goalPos;

            // Reuse existing ManualWheelchairController from the prefab if present;
            // only add a new one if the prefab doesn't have one.
            var manualCtrl = avatarObject.GetComponent<IVI.ManualWheelchairController>();
            if (manualCtrl == null)
                manualCtrl = avatarObject.AddComponent<IVI.ManualWheelchairController>();
            manualCtrl.BindNavigationAgent(sfpwd);
            manualCtrl.enabled = true;
            manualCtrl.startInManualMode = SessionReview.SessionOnboardingSettings.PwdStartupControl == SessionReview.StartupControlMode.Manual;

            AttachCameraToHead(avatarObject);

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

            avatarPrefab = ResolvePwdPrefab(bgPwdCharacter);
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

            Animator animator = GetAvatarAnimator(avatarObject);
            if (animator != null
                && pwdAnimationController != null
                && PwdCharacterUtility.UsesWheelchairPlayerAnimation(pwdCharacter)
                && animator.runtimeAnimatorController == null)
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

        private static GameObject FindByName(string objectName)
        {
            if (string.IsNullOrEmpty(objectName)) return null;

            // Try direct Find first (works for root objects and full paths from root)
            GameObject obj = GameObject.Find(objectName);
            if (obj != null)
            {
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
                    Debug.Log($"[PWD] Found '{leafName}' (from '{objectName}') via recursive search at ({found.position.x:F1},{found.position.y:F1},{found.position.z:F1})");
                    return found.gameObject;
                }
            }

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

        private void AttachCameraToHead(GameObject avatar)
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

            Vector3 thirdPersonOffset = new Vector3(0f, 1.4f, -2.2f);
            Vector3 spawnPos = avatar.transform.position + avatar.transform.rotation * thirdPersonOffset;

            if (wheelchairCam != null)
            {
                wheelchairCam.SetParent(avatar.transform, false);
                wheelchairCam.position = spawnPos;
                wheelchairCam.LookAt(avatar.transform.position + Vector3.up * 1.0f);

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
                camObj.transform.LookAt(avatar.transform.position + Vector3.up * 1.0f);

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

        private GameObject ResolvePwdPrefab(PwdCharacter gender)
        {
            switch (gender)
            {
                case PwdCharacter.MaleWheelchairUser:
                    return pwdAvatarPrefabMale != null ? pwdAvatarPrefabMale : pwdAvatarPrefab;
                case PwdCharacter.FemaleWheelchairUser:
                    return pwdAvatarPrefabFemale != null ? pwdAvatarPrefabFemale : pwdAvatarPrefab;
                case PwdCharacter.ScooterUser:
                    return pwdAvatarPrefabScooteruser != null ? pwdAvatarPrefabScooteruser : pwdAvatarPrefab;
                case PwdCharacter.DogWalker:
                    return pwdAvatarPrefabDogwalker != null ? pwdAvatarPrefabDogwalker : pwdAvatarPrefab;
                case PwdCharacter.Cyclist:
                    return pwdAvatarPrefabCyclist != null ? pwdAvatarPrefabCyclist : pwdAvatarPrefab;
                case PwdCharacter.WalkerUser:
                    return pwdAvatarPrefabWalker != null ? pwdAvatarPrefabWalker : pwdAvatarPrefab;
                case PwdCharacter.WhiteCaneUser:
                    return pwdAvatarPrefabWhiteCane != null ? pwdAvatarPrefabWhiteCane : pwdAvatarPrefab;
                case PwdCharacter.CaneUser:
                    return pwdAvatarPrefabCane != null ? pwdAvatarPrefabCane : pwdAvatarPrefab;
                case PwdCharacter.FemaleChild:
                    return pwdAvatarPrefabFemaleChild != null ? pwdAvatarPrefabFemaleChild : pwdAvatarPrefab;
                case PwdCharacter.MaleChild:
                    return pwdAvatarPrefabMaleChild != null ? pwdAvatarPrefabMaleChild : pwdAvatarPrefab;
                case PwdCharacter.PhoneUser:
                    return pwdAvatarPrefabPhoneUser != null ? pwdAvatarPrefabPhoneUser : pwdAvatarPrefab;
                case PwdCharacter.Random:
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
                avatarPrefab = ResolvePwdPrefab(bgPwdCharacter);
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
                else if (assignedController == LowLevelControl.PWDSF
                    && pwdAnimationController != null
                    && PwdCharacterUtility.UsesWheelchairPlayerAnimation(bgPwdCharacter))
                {
                    animator.runtimeAnimatorController = pwdAnimationController;
                }
                else if (assignedController != LowLevelControl.PWDSF)
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
