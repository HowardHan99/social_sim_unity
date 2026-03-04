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

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            avatarsList = null;
            numPWDSFAgentsInstantiated = 0;
            pwdPlayerSpawned = false;
        }

        [Header("PWD Player")]
        public bool isPwdPlayer = false;
        public PwdGender pwdGender = PwdGender.Male;
        public GameObject pwdAvatarPrefabMale;
        public GameObject pwdAvatarPrefabFemale;

        [Header("PWD Start / Goal")]
        [Tooltip("Scene object name for spawn point. Searches entire hierarchy by name.")]
        public string startObjectName = "start";
        [Tooltip("Scene object name for goal point. Searches entire hierarchy by name.")]
        public string goalObjectName = "end";

        [Header("Background PWD Gender")]
        public PwdGender bgPwdGender = PwdGender.Random;

        private GameObject avatarPrefab;
        private GameObject avatarObject;
        private LowLevelControl assignedController;

        void Awake()
        {
            if (SEAN.instance)
            {
                controller = SEAN.instance.AgentController;
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
                Animator animator = avatarObject.GetComponent<Animator>();
                animator.runtimeAnimatorController = animationController;
                avatarObject.AddComponent<PlayerAgent>();
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

        private void SpawnPwdPlayer()
        {
            // Populate the shared static avatarsList so that other agents
            // (e.g. those spawned later by NavManager) can pick from it.
            // Without this, the early return below would leave the list empty.
            if (avatarsList is null || avatarsList.Count == 0)
            {
                avatarsList = new List<GameObject>(avatars);
                if (pwdAvatarPrefab != null) avatarsList.Remove(pwdAvatarPrefab);
                if (pwdAvatarPrefabMale != null) avatarsList.Remove(pwdAvatarPrefabMale);
                if (pwdAvatarPrefabFemale != null) avatarsList.Remove(pwdAvatarPrefabFemale);
            }

            avatarPrefab = ResolvePwdPrefab(pwdGender);
            if (avatarPrefab == null)
            {
                Debug.LogError("No PWD avatar prefab assigned for gender: " + pwdGender, this);
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

            // Instantiate as a ROOT object (no parent). This avoids all parent-child
            // Rigidbody issues. Background agents are parented because NavManager needs
            // the hierarchy, but the PWD player has its own controller.
            avatarObject = Instantiate(avatarPrefab, spawnPos, spawnRot);
            avatarObject.name = "PWDPlayer";

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

            var sfpwd = avatarObject.AddComponent<IVI.SFPWDAgent>();
            sfpwd.useWaypoints = true;
            sfpwd.waypointStart = spawnPos;
            sfpwd.waypointGoal = goalPos;

            // Reuse existing ManualWheelchairController from the prefab if present;
            // only add a new one if the prefab doesn't have one.
            var manualCtrl = avatarObject.GetComponent<IVI.ManualWheelchairController>();
            if (manualCtrl == null)
                manualCtrl = avatarObject.AddComponent<IVI.ManualWheelchairController>();
            manualCtrl.enabled = true;
            manualCtrl.startInManualMode = false;

            AttachCameraToHead(avatarObject);

            // Lock position on the camera's CameraScript so mouse/keyboard input
            // cannot move the camera away from the avatar. Rotation (free-look) still works.
            foreach (var camScript in avatarObject.GetComponentsInChildren<IVI.CameraScript>(true))
                camScript.lockPosition = true;

            // Disable the Agent_X scene object since PWDPlayer is independent
            gameObject.SetActive(false);

            Debug.Log($"[PWD] Spawned PWDPlayer at ({spawnPos.x:F1},{spawnPos.y:F1},{spawnPos.z:F1}), " +
                      $"goal=({goalPos.x:F1},{goalPos.y:F1},{goalPos.z:F1}), " +
                      $"startObj={(startObj != null ? startObj.name : "null")}, " +
                      $"goalObj={(goalObj != null ? goalObj.name : "null")}");
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
            // Parent camera to the AVATAR ROOT (not a bone) so it follows position
            // and Y-rotation but is unaffected by animation bone movement.
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

            // Use head bone only to measure height, not as parent
            Transform headBone = FindBoneRecursive(avatar.transform, "Head");
            float camHeight = 1.2f;
            if (headBone != null)
                camHeight = headBone.position.y - avatar.transform.position.y + 0.05f;

            Vector3 camOffset = new Vector3(0f, camHeight, 0.1f);

            if (wheelchairCam != null)
            {
                wheelchairCam.SetParent(avatar.transform, false);
                wheelchairCam.localPosition = camOffset;
                wheelchairCam.localRotation = Quaternion.identity;

                Camera cam = wheelchairCam.GetComponent<Camera>();
                if (cam != null)
                    cam.targetDisplay = 1;

                if (wheelchairCam.GetComponent<IVI.WheelchairCameraSmoothing>() == null)
                    wheelchairCam.gameObject.AddComponent<IVI.WheelchairCameraSmoothing>();
            }
            else
            {
                GameObject camObj = new GameObject("PWDFirstPersonCamera");
                camObj.transform.SetParent(avatar.transform, false);
                camObj.transform.localPosition = camOffset;
                camObj.transform.localRotation = Quaternion.identity;

                Camera cam = camObj.AddComponent<Camera>();
                cam.targetDisplay = 1;
                cam.fieldOfView = 70f;
                cam.nearClipPlane = 0.1f;

                camObj.AddComponent<IVI.WheelchairCameraSmoothing>();
            }

            Debug.Log($"[PWD] Camera attached to avatar root at offset {camOffset}");
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
                    avatarsList = new List<GameObject>(avatars);
                    if (pwdAvatarPrefab != null && avatarsList.Contains(pwdAvatarPrefab))
                    {
                        avatarsList.Remove(pwdAvatarPrefab);
                    }
                    if (pwdAvatarPrefabMale != null && avatarsList.Contains(pwdAvatarPrefabMale))
                    {
                        avatarsList.Remove(pwdAvatarPrefabMale);
                    }
                    if (pwdAvatarPrefabFemale != null && avatarsList.Contains(pwdAvatarPrefabFemale))
                    {
                        avatarsList.Remove(pwdAvatarPrefabFemale);
                    }
                }

                if (avatarPrefab is null && avatarsList.Count > 0)
                {
                    int randomIndex = Random.Range(0, avatarsList.Count);
                    avatarPrefab = avatarsList[randomIndex];
                    avatarsList.RemoveAt(randomIndex);
                }
            }

            if (avatarPrefab != null)
            {
                avatarObject = Instantiate(avatarPrefab, transform.position, transform.rotation);
                Animator animator = avatarObject.GetComponent<Animator>();

                if (assignedController == LowLevelControl.PWDSF && pwdAnimationController != null)
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