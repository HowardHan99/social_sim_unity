using System.Collections.Generic;
using UnityEngine;

namespace SEAN.Scenario.Agents
{
    public class RandomAvatar : MonoBehaviour
    {
        public RuntimeAnimatorController animationController;
        public RuntimeAnimatorController pwdAnimationController;

        public GameObject[] avatars;
        public LowLevelControl controller = LowLevelControl.SF;
        public bool isPlayer = false;
        static private List<GameObject> avatarsList;

        // Static counters to keep track of instantiated agents
        static private int totalAgentsInstantiated = 0;
        static private int numPWDSFAgentsInstantiated = 0;
        static private int numORCAAgentsInstantiated = 0;
        static private int numSFAgentsInstantiated = 0;

        // Total number of agents to instantiate
        static private int totalAgentsToInstantiate = 0;

        private GameObject avatarPrefab;
        private GameObject avatarObject;
        private LowLevelControl assignedController;

    void Awake()
        {
            // If SEAN instance is available, get the agent controller
            if (SEAN.instance)
            {
                controller = SEAN.instance.AgentController;
            }

            // Check if this avatar is controlled by the player
            if (isPlayer)
            {
                // Instantiate the player avatar using the first model in the avatars array
                avatarObject = Instantiate(avatars[0], transform.position, transform.rotation);

                // Get the Animator component and set the animation controller
                Animator animator = avatarObject.GetComponent<Animator>();
                animator.runtimeAnimatorController = pwdAnimationController;

                // Add the PlayerAgent component to the avatar
                avatarObject.AddComponent<PlayerAgent>();
            }else
            {
                // Initialize the avatars list if it's null or empty
                if (avatarsList == null || avatarsList.Count == 0)
                {
                    avatarsList = new List<GameObject>(avatars);
                    avatarsList.RemoveAt(0); // Remove the PWD avatar at index 0
                }

                // Get the total specified agent numbers from SEAN.instance
                int numPWDSFAgents = SEAN.instance.numPwDSFAgents;
                int numORCAAgents = SEAN.instance.numORCAAgents;
                int numSFAgents = SEAN.instance.numSFAgents;

                // Calculate the total specified agents
                int totalSpecifiedAgents = numPWDSFAgents + numORCAAgents + numSFAgents;

                // Initialize the totalAgentsToInstantiate if not already set
                if (totalAgentsToInstantiate == 0)
                {
                    totalAgentsToInstantiate = totalSpecifiedAgents;
                }

                // Decide which avatar to use based on the instantiated counts
                if (numPWDSFAgentsInstantiated < numPWDSFAgents)
                {
                    // Use PWD model
                    avatarPrefab = avatars[0]; // PWD model at index 0
                    assignedController = LowLevelControl.PWDSF;
                }
                else
                {
                    // Use a random avatar from the avatars list
                    int randomIndex = Random.Range(1, avatarsList.Count);
                    avatarPrefab = avatarsList[randomIndex];
                    avatarsList.RemoveAt(randomIndex);
                    assignedController = controller; // Use default controller
                }

                // Instantiate the avatar
                avatarObject = Instantiate(avatarPrefab, transform.position, transform.rotation);

                // Set the appropriate animator controller
                Animator animator = avatarObject.GetComponent<Animator>();
                if (assignedController == LowLevelControl.PWDSF)
                {
                    animator.runtimeAnimatorController = pwdAnimationController;
                }
                else
                {
                    animator.runtimeAnimatorController = animationController;
                }

                // Assign the controller
                AssignController();

                // Add the appropriate agent component based on the assigned controller
                if (assignedController == LowLevelControl.PWDSF)
                {
                    avatarObject.AddComponent<IVI.SFAgent>();
                    numPWDSFAgentsInstantiated++;
                }
                else if (assignedController == LowLevelControl.ORCA)
                {
                    avatarObject.AddComponent<ORCA.Agent>();
                    numORCAAgentsInstantiated++;
                }
                else if (assignedController == LowLevelControl.SF)
                {
                    avatarObject.AddComponent<IVI.SFAgent>();
                    numSFAgentsInstantiated++;
                }
                else
                {
                    // Default to SF if controller type is unknown
                    avatarObject.AddComponent<IVI.SFAgent>();
                    numSFAgentsInstantiated++;
                }

                // Increase the total agents instantiated
                totalAgentsInstantiated++;

                // Set the avatar's parent to this game object
                avatarObject.transform.parent = transform;
            }
        }

        private void AssignController()
        {
            // The AssignController logic remains unchanged
            // Get the total specified agent numbers from SEAN.instance
            int numPWDSFAgents = SEAN.instance.numPwDSFAgents;
            int numORCAAgents = SEAN.instance.numORCAAgents;
            int numSFAgents = SEAN.instance.numSFAgents;

            // Determine the controller to assign based on the counts and priority
            if (assignedController == LowLevelControl.PWDSF)
            {
                // PWD agents already have their controller assigned
                return;
            }
            else if (numORCAAgentsInstantiated < numORCAAgents && totalAgentsInstantiated < totalAgentsToInstantiate)
            {
                assignedController = LowLevelControl.ORCA;
            }
            else if (numSFAgentsInstantiated < numSFAgents && totalAgentsInstantiated < totalAgentsToInstantiate)
            {
                assignedController = LowLevelControl.SF;
            }
            else
            {
                // Default to SF if no agents left
                assignedController = LowLevelControl.SF;
            }
        }

//  void Awake()
//         {
//             // Copy the avatars list at the beginning of the game
//             // or if avatarsList becomes empty
//             if (avatarsList is null || avatarsList.Count == 0)
//             {
//                 avatarsList = new List<GameObject>(avatars);
//                 // avatarsList.RemoveAt(0); // Remove the PWD avatar
//             }        

//             if (totalAgentsToInstantiate == 0)
//             {
//                 totalAgentsToInstantiate = avatarsList.Count+1;
//             }

//             // Load a random avatar from the remaining avatars in the list
//             // then remove that avatar from the list, and if the list becomes empty
//             // then it copies the avatars array to avatarsList
//             if (avatarPrefab is null)
//             {
//                 int randomIndex = Random.Range(0, avatarsList.Count);
//                 avatarPrefab = avatarsList[0];
//                 avatarsList.RemoveAt(randomIndex);
//             }

//             avatarObject = Instantiate(avatarPrefab, transform.position, transform.rotation);
//             Animator animator = avatarObject.GetComponent<Animator>();
//             animator.runtimeAnimatorController = pwdAnimationController;
//             if (SEAN.instance)
//             {
//                 controller = SEAN.instance.AgentController;
//             }
//             if (isPlayer)
//             {
//                 avatarObject.AddComponent<PlayerAgent>();
//             }
//             else
//             {
//                 AssignController();
//                 //old logic
//                 // if (controller == LowLevelControl.SF)
//                 // {
//                 //     avatarObject.AddComponent<IVI.SFAgent>();
//                 // }
//                 // else if (controller == LowLevelControl.ORCA)
//                 // {
//                 //     avatarObject.AddComponent<ORCA.Agent>();
//                 // }
                
//             }
//             avatarObject.transform.parent = transform;
//         }
       


        // private void AssignController()
        // {
        //     // Get the total specified agent numbers from SEAN.instance
        //     int numPWDSFAgents = SEAN.instance.numPwDSFAgents;
        //     int numORCAAgents = SEAN.instance.numORCAAgents;
        //     int numSFAgents = SEAN.instance.numSFAgents;

        //     // Calculate the total specified agents
        //     int totalSpecifiedAgents = numPWDSFAgents + numORCAAgents + numSFAgents;

        //     // Determine the controller to assign based on the counts and priority
        //     LowLevelControl assignedController = LowLevelControl.SF; // Default controller

        //     if (numPWDSFAgentsInstantiated < numPWDSFAgents && totalAgentsInstantiated < totalAgentsToInstantiate)
        //     {
        //         assignedController = LowLevelControl.PWDSF;
        //         numPWDSFAgentsInstantiated++;
        //     }
        //     else if (numORCAAgentsInstantiated < numORCAAgents && totalAgentsInstantiated < totalAgentsToInstantiate)
        //     {
        //         assignedController = LowLevelControl.ORCA;
        //         numORCAAgentsInstantiated++;
        //     }
        //     else if (numSFAgentsInstantiated < numSFAgents && totalAgentsInstantiated < totalAgentsToInstantiate)
        //     {
        //         assignedController = LowLevelControl.SF;
        //         numSFAgentsInstantiated++;
        //     }
        //     else if (totalAgentsInstantiated < totalAgentsToInstantiate)
        //     {
        //         // Allocate remaining agents with SF controller
        //         assignedController = LowLevelControl.SF;
        //         numSFAgentsInstantiated++;
        //     }
        //     else
        //     {
        //         // Total agents have been instantiated
        //         Debug.LogWarning("All agents have been instantiated.");
        //         return;
        //     }

        //     totalAgentsInstantiated++;

        //     // Add the appropriate component based on the assigned controller
        //     if (assignedController == LowLevelControl.PWDSF)
        //     {
        //         // Here to replace the agent model
        //         avatarObject.AddComponent<IVI.SFAgent>();
        //     }
        //     else if (assignedController == LowLevelControl.ORCA)
        //     {
        //         avatarObject.AddComponent<ORCA.Agent>();
        //     }
        //     else if (assignedController == LowLevelControl.SF)
        //     {
        //         avatarObject.AddComponent<IVI.SFAgent>();
        //     }
        //     else
        //     {
        //         // Default to SF if controller type is unknown
        //         avatarObject.AddComponent<IVI.SFAgent>();
        //     }
        // }
    }
}