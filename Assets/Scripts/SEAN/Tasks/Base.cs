// Copyright (c) 2021, Members of Yale Interactive Machines Group, Yale University,
// Nathan Tsoi
// All rights reserved.
// This source code is licensed under the BSD-style license found in the
// LICENSE file in the root directory of this source tree. 

using UnityEngine;
using Unity.Robotics.ROSTCPConnector;
using SessionReview;

namespace SEAN.Tasks
{
    /// <summary>
    /// Delegates to call when a new task is started
    /// </summary>
    public delegate void OnNewTask();

    public abstract class Base : MonoBehaviour
    {

        public GameObject robotStart { get; protected set; }
        public GameObject robotGoal { get; protected set; }

        public GameObject playerStart { get; protected set; }
        public GameObject playerGoal { get; protected set; }

        public float timeoutTaskSeconds = 120f;
        private float taskStartTime = 0f;

        private float timer = 0.0f;
        private int publishInterval = 10;

        private GameObject cube;
        private GameObject arrow;

        public OnNewTask onNewTask;

        public bool PublishGoal = true;
        public static string Topic = "/move_base_simple/goal";
        public string FrameID = "map";

        protected SEAN sean;
        private static ROSConnection ros;

        private float debounceTime = 0f;
        public float debounceTimeoutSec = 5f;
        public float postTrialPromptDelaySec = 0.15f;
        private float debounceStartupTime = 0f;
        protected float debounceStartupTimeoutSec = 3f;
        private static RosMessageTypes.Geometry.MPoseStamped nextGoal;

        public Transform robotStartTransform
        {
            get { return robotStart.transform; }
            set
            {
                robotStart.transform.position = value.position;
                robotStart.transform.rotation = value.rotation;
            }
        }

        public Transform robotGoalTransform
        {
            get { return robotGoal.transform; }
            set
            {
                robotGoal.transform.position = value.position;
                robotGoal.transform.rotation = value.rotation;
            }
        }

        public Transform personStartTransform
        {
            get { return playerStart.transform; }
            set
            {
                playerStart.transform.position = value.position;
                playerStart.transform.rotation = value.rotation;
            }
        }

        public Transform personGoalTransform
        {
            get { return playerGoal.transform; }
            set
            {
                playerGoal.transform.position = value.position;
                robotGoal.transform.rotation = value.rotation;
            }
        }

        public GameObject interactiveStart
        {
            get
            {
                if (sean.PlayerControl)
                {
                    return playerStart;
                }
                else
                {
                    return robotStart;
                }
            }
        }

        protected GameObject interactiveGoal
        {
            get
            {
                if (sean.PlayerControl)
                {
                    return playerGoal;
                }
                else
                {
                    return robotGoal;
                }
            }
        }

        private Transform controlledAvatar
        {
            get
            {
                if (sean.PlayerControl)
                {
                    // get the avatar
                    return sean.player.transform.GetChild(0);
                }
                else
                {
                    return sean.robot.transform;
                }
            }

        }

        [System.NonSerialized] public float completionDistance = 0.3f;

        public int maximumNumberOfTasks = 0;
        public int taskID;

        public bool isRunning { get; private set; }
        public ushort number { get; private set; }
        public bool isAwaitingPostTrialAction { get; private set; }
        public bool hasPreparedTaskPreview { get; private set; }

        // implementers should override this method
        protected abstract bool NewTask();

        private void OnNewTask()
        {
            isAwaitingPostTrialAction = false;
            if (!hasPreparedTaskPreview)
            {
                UpdatePositions();
                UpdateCameras();
            }

            hasPreparedTaskPreview = false;
            isRunning = true;
            number++;
            taskStartTime = Time.time;
        }

        public void SetTargetFlags(GameObject goal)
        {
            foreach (Transform child in goal.transform)
            {
                if (child.name == "TargetFlagCube")
                {
                    cube = child.gameObject;
                }
                else if (child.name == "TargetFlagArrow")
                {
                    arrow = child.gameObject;
                }
            }
        }
        public void Awake()
        {
            isRunning = false;
            // Needs to happen before any callbacks are assigned
            onNewTask = OnNewTask;
        }

        public virtual void Start()
        {
            ros = ROSConnection.instance;
            sean = SEAN.instance;
            initStartAndGoal();
            number = 0;
            debounceStartupTime = Time.time;
        }

        public void Update()
        {
            CheckNewTask();
            // rotate the target
            timer += Time.deltaTime;
            if (PublishGoal && (timer >= publishInterval))
            {
                timer = 0;
                Publish(interactiveGoal);
            }
            if (cube)
            {
                cube.transform.Rotate(0.0f, 2.0f, 0.0f, Space.World);
            }
        }

        protected void Publish(GameObject goal)
        {
            nextGoal = new RosMessageTypes.Geometry.MPoseStamped();
            nextGoal.header.frame_id = FrameID;
            sean.clock.UpdateMHeader(nextGoal.header);
            nextGoal.pose = Util.Geometry.GetMPose(goal.transform);
            ros.Send(Topic, nextGoal);
        }

        private void initStartAndGoal()
        {
            robotStart = sean.GetStartOrGoal(Scenario.Agents.ControlledAgent.Robot, true);
            robotGoal = sean.GetStartOrGoal(Scenario.Agents.ControlledAgent.Robot, false);
            playerStart = sean.GetStartOrGoal(Scenario.Agents.ControlledAgent.Player, true);
            playerGoal = sean.GetStartOrGoal(Scenario.Agents.ControlledAgent.Player, false);
            if (sean.ControlledAgent == Scenario.Agents.ControlledAgent.Robot)
            {
                robotGoal.SetActive(true);
                playerGoal.SetActive(false);
            }
            if (sean.ControlledAgent == Scenario.Agents.ControlledAgent.Player)
            {
                robotGoal.SetActive(false);
                playerGoal.SetActive(true);
            }
            robotStart.SetActive(false);
            playerStart.SetActive(false);
        }

        public void StartNewTask() {
            if (SessionReviewManager.Instance != null && SessionReviewManager.Instance.BlocksAutomaticTrialStart)
            {
                return;
            }

            if (hasPreparedTaskPreview)
            {
                // The preview was prepared when the trial-start prompt appeared, which locked
                // in start/goal at that moment. Let tasks whose goals come from scene markers
                // (CustomStartGoal) re-read them, so edits made while the prompt was up count.
                RefreshPreparedTask();
                UpdatePositions();
                UpdateCameras();
                if (PublishGoal)
                {
                    Publish(interactiveGoal);
                }
                onNewTask.Invoke();
                return;
            }

            if (NewTask())
            {
                if (PublishGoal)
                {
                    Publish(interactiveGoal);
                }
                onNewTask.Invoke();
            }
        }

        /// <summary>
        /// Called before a previously-prepared task preview is started or republished, so the
        /// task can re-sync its start/goal from live scene state. Default: keep the prepared
        /// preview as-is (random tasks must not re-roll between preview and start).
        /// </summary>
        protected virtual void RefreshPreparedTask() { }

        public bool PrepareTaskPreview()
        {
            if (hasPreparedTaskPreview)
            {
                return true;
            }

            if (!NewTask())
            {
                return false;
            }

            if (PublishGoal)
            {
                Publish(interactiveGoal);
            }

            UpdatePositions();
            UpdateCameras();
            hasPreparedTaskPreview = true;
            debounceTime = 0f;
            debounceStartupTime = Time.time;
            return true;
        }

        public void RepublishPreviewGoal()
        {
            if (!hasPreparedTaskPreview || !PublishGoal)
                return;

            RefreshPreparedTask();
            Publish(interactiveGoal);
        }

        protected virtual void CheckNewTask()
        {
            if (SessionReviewManager.Instance != null && SessionReviewManager.Instance.BlocksAutomaticTrialStart)
            {
                return;
            }

            if (isAwaitingPostTrialAction)
            {
                return;
            }

            bool wasRunning = isRunning;
            if (debouce())
            {
                if (maximumNumberOfTasks > 0 && number >= maximumNumberOfTasks)
                {
                    Debug.Log("Completed " + number + " of  " + maximumNumberOfTasks + " tasks, exiting");
#if UNITY_EDITOR
                    UnityEditor.EditorApplication.isPlaying = false;
#else
                    Application.Quit();
#endif
                }
                if (wasRunning && ShouldPauseBeforeStartingNextTask())
                {
                    PauseAfterTaskEnd();
                }
                else
                {
                    StartNewTask();
                }
            }
        }

        public void StartPendingOrNewTask()
        {
            isAwaitingPostTrialAction = false;
            StartNewTask();
        }

        private bool debouce()
        {
            if (!isRunning)
            {
                return debounceStartup();
            } else
            {
                return debounceCompletion() || timeout();
            }
        }

        private bool debounceStartup()
        {
            return (Time.time - debounceStartupTime > debounceStartupTimeoutSec);
        }
        private bool debounceCompletion()
        {
            // Wait until the robot and goal are not at the origin
            if (controlledAvatar.position == Pose.identity.position && controlledAvatar.rotation == Pose.identity.rotation &&
                interactiveGoal.transform.position == Pose.identity.position && interactiveGoal.transform.rotation == Pose.identity.rotation)
            {
                //print("at the origin");
                return false;
            }
            float distToGoal = Util.Geometry.GroundPlaneDist(controlledAvatar.position, interactiveGoal.transform.position);
            //print(debounceTime + " > " + completionDistance + ", debouceCompletion distToGoal: " + distToGoal);

            if (distToGoal > completionDistance)
            {
                debounceTime = 0;
                return false;
            }
            if (debounceTime == 0)
            {
                debounceTime = Time.time;
                return false;
            }

            float requiredDelay = ShouldPauseBeforeStartingNextTask()
                ? Mathf.Max(0f, postTrialPromptDelaySec)
                : debounceTimeoutSec;

            if (Time.time - debounceTime > requiredDelay)
            {
                debounceTime = 0;
                return true;
            }
            return false;
        }

        private void UpdatePositions()
        {
            if (sean.PlayerControl && playerStart)
            {
                // if the avatar hasn't been created yet; this will spawn avatar at start position
                if (sean.player.transform.GetChild(0).gameObject.name == "RocketboxRandomAnimatedPlayer")
                {
                    sean.player.transform.rotation = playerStart.transform.rotation;
                    sean.player.transform.position = playerStart.transform.position;
                }
                else
                {
                    sean.player.transform.GetChild(0).rotation = playerStart.transform.rotation;
                    sean.player.transform.GetChild(0).position = playerStart.transform.position;
                }
            }

            SyncPwdPlayerToTaskStartGoal();

            if (robotStart)
            {
                sean.robot.base_link.transform.rotation = robotStart.transform.rotation;
                sean.robot.base_link.transform.position = robotStart.transform.position;
            }
        }

        private void SyncPwdPlayerToTaskStartGoal()
        {
            if (!sean.PlayerControl || playerStart == null)
                return;

            IVI.ManualWheelchairController pwdController = FindPwdPlayerController();
            if (pwdController == null)
                return;

            Transform pwdTransform = pwdController.transform;
            pwdTransform.rotation = playerStart.transform.rotation;
            pwdTransform.position = playerStart.transform.position;

            foreach (Rigidbody rb in pwdTransform.GetComponentsInChildren<Rigidbody>(true))
            {
                if (rb == null)
                    continue;

                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }

            IVI.SFPWDAgent sfpwd = pwdController.GetComponent<IVI.SFPWDAgent>();
            if (sfpwd != null)
            {
                sfpwd.useWaypoints = true;
                sfpwd.waypointStart = playerStart.transform.position;
                if (playerGoal != null)
                    sfpwd.waypointGoal = playerGoal.transform.position;
            }
        }

        private static IVI.ManualWheelchairController FindPwdPlayerController()
        {
            IVI.ManualWheelchairController fallback = null;
            foreach (IVI.ManualWheelchairController controller in FindObjectsOfType<IVI.ManualWheelchairController>(true))
            {
                if (controller == null)
                    continue;

                if (controller.gameObject != null && controller.gameObject.name == "PWDPlayer")
                    return controller;

                if (fallback == null)
                    fallback = controller;
            }

            return fallback;
        }

        private bool timeout()
        {
            if (taskStartTime == 0f) { return false; }
            if (taskStartTime + timeoutTaskSeconds < Time.time)
            {
                taskStartTime = 0f;
                return true;
            }
            return false;
        }

        private bool ShouldPauseBeforeStartingNextTask()
        {
            return SessionReviewManager.Instance != null &&
                   SessionReviewManager.Instance.UsePostTrialPrompt;
        }

        private void PauseAfterTaskEnd()
        {
            isRunning = false;
            isAwaitingPostTrialAction = true;
            debounceTime = 0f;
            taskStartTime = 0f;
        }

        private void UpdateCameras()
        {
            if (SEAN.instance.TopDownViewOnly) { return; }
            SEAN.instance.environment.topViewCamera.enabled = false;

            bool pwdManualDrivesMainCamera = SessionOnboardingSettings.HasCompletedOnboarding &&
                SessionOnboardingSettings.PlayerMode == OnboardingPlayerMode.Human &&
                SessionOnboardingSettings.PwdStartupControl == StartupControlMode.Manual;
            if (pwdManualDrivesMainCamera)
                return;

            if (!sean.PlayerControl)
            {
                SEAN.instance.robot.camera_first.enabled = true;
            }
        }

        #region helpers
        /// <summary>
        ///  Sample a random group (from ground truth, if available)
        /// </summary>
        /// <returns></returns>
        protected bool GetRandomGroup(out Vector3 center)
        {
            center = Vector3.zero;
            if (sean.pedestrianBehavior.groups.Length < 1)
            {
                return false;
            }
            center = sean.pedestrianBehavior.groups[Random.Range(0, sean.pedestrianBehavior.groups.Length)].center;
            return true;
        }

        /// <summary>
        ///  Sample a random and unoccupied group member position (from ground truth, if available)
        /// </summary>
        /// <returns>true if a group center is found and returned via the output argument</returns>
        protected bool GetRandomGroupMembershipTransform(out Vector3 position, out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            if (sean.pedestrianBehavior.groups.Length < 1)
            {
                return false;
            }
            Scenario.Trajectory.TrackedGroup[] groups = sean.pedestrianBehavior.groups;
            if (groups[Random.Range(0, groups.Length)].GroupMemberLocationGenerator(out position, out rotation))
            {
                // may not be necessary:
                UnityEngine.AI.NavMeshHit hit = Util.Navmesh.RandomHit(position, 0.5f);
                position = hit.position;
                return true;
            }
            return false;
        }
        #endregion helpers

        void OnGUI()
        {
            if (!isRunning)
            {
                return;
            }

            int w = Screen.width, h = Screen.height;
            GUIStyle style = new GUIStyle();
            Rect rect = new Rect(45, 0, w, h * 2 / 100);
            style.alignment = TextAnchor.UpperCenter;
            style.fontSize = h * 2 / 100;
            style.normal.textColor = new Color(0.75f, 0.75f, 0.75f, 1.0f);
            string text = string.Format("Interaction ends in: {0:0.00}", timeoutTaskSeconds - (Time.time - taskStartTime));
            GUI.Label(rect, text, style);
        }


    }
}
