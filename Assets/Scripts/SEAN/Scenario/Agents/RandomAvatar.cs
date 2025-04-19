using System.Collections.Generic;
using UnityEngine;

namespace SEAN.Scenario.Agents
{
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

        private GameObject avatarPrefab;
        private GameObject avatarObject;
        private LowLevelControl assignedController;

        void Awake()
        {
            if (SEAN.instance)
            {
                controller = SEAN.instance.AgentController;
            }

            if (isPlayer)
            {
                avatarObject = Instantiate(avatars[0], transform.position, transform.rotation);
                Animator animator = avatarObject.GetComponent<Animator>();
                animator.runtimeAnimatorController = animationController;
                avatarObject.AddComponent<PlayerAgent>();
            }
            else
            {
                int numPWDSFAgentsToSpawn = SEAN.instance ? SEAN.instance.numPwDSFAgents : 0;
                assignedController = controller;

                if (numPWDSFAgentsInstantiated < numPWDSFAgentsToSpawn && pwdAvatarPrefab != null)
                {
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

            if (avatarObject != null)
            {
                avatarObject.transform.parent = transform;
            }
        }
    }
}