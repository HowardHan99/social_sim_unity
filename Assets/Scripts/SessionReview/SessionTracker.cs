using System;
using System.Collections.Generic;
using UnityEngine;
using IVI;

namespace SessionReview
{
    public enum TrialEndReason
    {
        Completion,
        Timeout,
        Manual
    }

    [Serializable]
    public class AgentArrivalInfo
    {
        public string objectId;
        public string agentName;
        public AgentRole role;
        public bool arrived;
        public float arrivalTime;
    }

    public enum AgentRole
    {
        Robot,
        PWDPlayer,
        BackgroundPed,
        BackgroundPWD
    }

    public class TrialEndInfo
    {
        public string trialName;
        public ushort trialNumber;
        public float startTime;
        public float endTime;
        public TrialEndReason reason;
        public Vector3 robotGoalPosition;
        public bool hasRobotGoalPosition;
        public Vector3 playerGoalPosition;
        public bool hasPlayerGoalPosition;
        public Dictionary<string, AgentArrivalInfo> agentArrivals = new Dictionary<string, AgentArrivalInfo>();
        public Dictionary<string, AgentRole> agentRoles = new Dictionary<string, AgentRole>();
    }

    public delegate void OnTrialEnded(TrialEndInfo info);

    public class SessionTracker : MonoBehaviour
    {
        public event OnTrialEnded TrialEnded;

        private SEAN.SEAN sean;
        private bool subscribedToRobotTask;
        private bool tracking;
        private float trialStartTime;
        private ushort trialNumber;
        private string trialName;

        private Dictionary<string, AgentArrivalInfo> agentArrivals = new Dictionary<string, AgentArrivalInfo>();
        private Dictionary<string, AgentRole> agentRoles = new Dictionary<string, AgentRole>();

        private List<SEAN.Scenario.Trajectory.TrackedAgent> trackedPedestrians = new List<SEAN.Scenario.Trajectory.TrackedAgent>();
        private ManualWheelchairController pwdController;
        private LiveTrajectoryRecorder trajectoryRecorder;
        private IVI.INavigable pwdNavigable;
        private float rosterRefreshUntilTime;
        private bool pendingRosterRefresh;

        public bool IsTracking => tracking;

        void Awake()
        {
            trajectoryRecorder = GetComponent<LiveTrajectoryRecorder>();
        }

        void Start()
        {
            TryInitialize();
        }

        void OnDestroy()
        {
            if (subscribedToRobotTask && sean != null && sean.robotTask != null)
                sean.robotTask.onNewTask -= HandleNewTask;
        }

        private bool TryInitialize()
        {
            if (trajectoryRecorder == null)
                trajectoryRecorder = GetComponent<LiveTrajectoryRecorder>();

            if (sean == null)
                sean = SEAN.SEAN.instance;

            if (sean == null || sean.robotTask == null)
                return false;

            if (!subscribedToRobotTask)
            {
                sean.robotTask.onNewTask += HandleNewTask;
                subscribedToRobotTask = true;
            }

            return true;
        }

        private void HandleNewTask()
        {
            if (tracking)
                FinishCurrentTrial(TrialEndReason.Completion);

            BeginTracking();
        }

        public void BeginTrackingForCurrentTask()
        {
            if (tracking)
                return;

            if (!TryInitialize() || !sean.robotTask.isRunning)
                return;

            BeginTracking();
        }

        private void BeginTracking()
        {
            trialStartTime = Time.time;
            trialNumber = sean.robotTask.number;
            trialName = sean.pedestrianBehavior.name + "_" + sean.robotTask.name;

            agentArrivals.Clear();
            agentRoles.Clear();
            trackedPedestrians.Clear();

            string robotId = GetRobotObjectId();
            if (!string.IsNullOrEmpty(robotId))
            {
                agentRoles[robotId] = AgentRole.Robot;
                agentArrivals[robotId] = new AgentArrivalInfo
                {
                    objectId = robotId,
                    agentName = "Robot",
                    role = AgentRole.Robot,
                    arrived = false,
                    arrivalTime = -1f
                };
            }

            pwdController = FindPwdPlayerController();
            if (pwdController != null)
            {
                pwdNavigable = pwdController.GetComponent<IVI.INavigable>();
                string pwdId = GetObjectId(pwdController.gameObject);
                if (!string.IsNullOrEmpty(pwdId))
                {
                    agentRoles[pwdId] = AgentRole.PWDPlayer;
                    agentArrivals[pwdId] = new AgentArrivalInfo
                    {
                        objectId = pwdId,
                        agentName = pwdController.gameObject.name,
                        role = AgentRole.PWDPlayer,
                        arrived = false,
                        arrivalTime = -1f
                    };
                }
            }

            if (sean.pedestrianBehavior != null && sean.pedestrianBehavior.agents != null)
            {
                foreach (var agent in sean.pedestrianBehavior.agents)
                {
                    if (agent == null) continue;
                    string agentId = GetObjectId(agent.gameObject);
                    if (string.IsNullOrEmpty(agentId)) continue;

                    // Skip if already registered (e.g. the PWD player)
                    if (agentRoles.ContainsKey(agentId)) continue;

                    trackedPedestrians.Add(agent);
                    bool isPWD = agent.GetComponent<SFPWDAgent>() != null;
                    AgentRole role = isPWD ? AgentRole.BackgroundPWD : AgentRole.BackgroundPed;
                    agentRoles[agentId] = role;
                    agentArrivals[agentId] = new AgentArrivalInfo
                    {
                        objectId = agentId,
                        agentName = agent.gameObject.name,
                        role = role,
                        arrived = false,
                        arrivalTime = -1f
                    };
                }
            }

            tracking = true;
            RegisterAllWithRecorder();
            pendingRosterRefresh = true;
            rosterRefreshUntilTime = Time.time + 1.0f;
        }

        private void RegisterAllWithRecorder()
        {
            if (trajectoryRecorder == null) return;

            // Robot
            if (sean.robot != null && sean.robot.base_link != null)
            {
                string id = GetObjectId(sean.robot.base_link);
                if (!string.IsNullOrEmpty(id))
                    trajectoryRecorder.TrackAgent(id, sean.robot.base_link.transform);
            }

            // PWD player
            if (pwdController != null)
            {
                string id = GetObjectId(pwdController.gameObject);
                if (!string.IsNullOrEmpty(id))
                    trajectoryRecorder.TrackAgent(id, ResolveTrackingTransform(pwdController.gameObject));
            }

            // Pedestrians
            foreach (var agent in trackedPedestrians)
            {
                if (agent == null) continue;
                string id = GetObjectId(agent.gameObject);
                if (!string.IsNullOrEmpty(id))
                    trajectoryRecorder.TrackAgent(id, agent.transform);
            }

            Debug.Log($"[SessionReview] Registered {agentRoles.Count} agents with trajectory recorder.");
        }

        void Update()
        {
            TryInitialize();

            if (!tracking) return;

            if (pendingRosterRefresh)
            {
                RefreshTrackingRosterIfNeeded();
            }

            foreach (var agent in trackedPedestrians)
            {
                if (agent == null) continue;
                string id = GetObjectId(agent.gameObject);
                if (string.IsNullOrEmpty(id)) continue;
                if (!agentArrivals.ContainsKey(id)) continue;
                if (agentArrivals[id].arrived) continue;

                var nav = agent.GetComponent<IVI.INavigable>();
                if (nav != null && nav.CloseEnough())
                {
                    agentArrivals[id].arrived = true;
                    agentArrivals[id].arrivalTime = Time.time;
                }
            }

            if (pwdNavigable != null)
            {
                string pwdId = GetObjectId(pwdController.gameObject);
                if (!string.IsNullOrEmpty(pwdId) && agentArrivals.ContainsKey(pwdId) && !agentArrivals[pwdId].arrived)
                {
                    if (pwdNavigable.CloseEnough())
                    {
                        agentArrivals[pwdId].arrived = true;
                        agentArrivals[pwdId].arrivalTime = Time.time;

                        if (ShouldFinishTrialOnPwdArrival())
                        {
                            FinishCurrentTrial(TrialEndReason.Completion);
                            return;
                        }
                    }
                }
            }

            if (!sean.robotTask.isRunning && tracking)
                FinishCurrentTrial(TrialEndReason.Timeout);
        }

        private void RefreshTrackingRosterIfNeeded()
        {
            if (Time.time > rosterRefreshUntilTime)
            {
                pendingRosterRefresh = false;
                return;
            }

            bool addedAny = false;

            if (pwdController == null || !IsPwdPlayerController(pwdController))
            {
                pwdController = FindPwdPlayerController();
                if (pwdController != null)
                {
                    pwdNavigable = pwdController.GetComponent<IVI.INavigable>();
                    string pwdId = GetObjectId(pwdController.gameObject);
                    if (!string.IsNullOrEmpty(pwdId) && !agentRoles.ContainsKey(pwdId))
                    {
                        agentRoles[pwdId] = AgentRole.PWDPlayer;
                        agentArrivals[pwdId] = new AgentArrivalInfo
                        {
                            objectId = pwdId,
                            agentName = pwdController.gameObject.name,
                            role = AgentRole.PWDPlayer,
                            arrived = false,
                            arrivalTime = -1f
                        };
                        addedAny = true;
                    }
                }
            }

            if (sean != null && sean.pedestrianBehavior != null && sean.pedestrianBehavior.agents != null)
            {
                foreach (var agent in sean.pedestrianBehavior.agents)
                {
                    if (agent == null) continue;

                    string agentId = GetObjectId(agent.gameObject);
                    if (string.IsNullOrEmpty(agentId) || agentRoles.ContainsKey(agentId))
                        continue;

                    trackedPedestrians.Add(agent);
                    bool isPWD = agent.GetComponent<SFPWDAgent>() != null;
                    AgentRole role = isPWD ? AgentRole.BackgroundPWD : AgentRole.BackgroundPed;
                    agentRoles[agentId] = role;
                    agentArrivals[agentId] = new AgentArrivalInfo
                    {
                        objectId = agentId,
                        agentName = agent.gameObject.name,
                        role = role,
                        arrived = false,
                        arrivalTime = -1f
                    };
                    addedAny = true;
                }
            }

            if (addedAny)
                RegisterAllWithRecorder();
        }

        /// <summary>
        /// Ends the current interaction on demand (e.g. from a UI button) and lets the normal
        /// post-trial flow take over. No-op if no trial is currently tracked.
        /// </summary>
        public void EndCurrentTrialManually()
        {
            if (!tracking) return;
            FinishCurrentTrial(TrialEndReason.Manual);
        }

        private void FinishCurrentTrial(TrialEndReason reason)
        {
            if (!tracking) return;
            tracking = false;

            Vector3 robotGoalPosition = GetGoalPosition(sean != null && sean.robotTask != null ? sean.robotTask.robotGoal : null, out bool hasRobotGoalPosition);
            Vector3 playerGoalPosition = GetGoalPosition(sean != null && sean.robotTask != null ? sean.robotTask.playerGoal : null, out bool hasPlayerGoalPosition);

            var info = new TrialEndInfo
            {
                trialName = trialName,
                trialNumber = trialNumber,
                startTime = trialStartTime,
                endTime = Time.time,
                reason = reason,
                robotGoalPosition = robotGoalPosition,
                hasRobotGoalPosition = hasRobotGoalPosition,
                playerGoalPosition = playerGoalPosition,
                hasPlayerGoalPosition = hasPlayerGoalPosition,
                agentArrivals = new Dictionary<string, AgentArrivalInfo>(agentArrivals),
                agentRoles = new Dictionary<string, AgentRole>(agentRoles)
            };

            TrialEnded?.Invoke(info);
        }

        private string GetRobotObjectId()
        {
            if (sean.robot == null || sean.robot.base_link == null) return null;
            return GetObjectId(sean.robot.base_link);
        }

        private static bool ShouldFinishTrialOnPwdArrival()
        {
            return SessionOnboardingSettings.PlayerMode == OnboardingPlayerMode.Human;
        }

        private static bool IsPwdPlayerController(ManualWheelchairController controller)
        {
            return controller != null &&
                   controller.gameObject != null &&
                   string.Equals(controller.gameObject.name, "PWDPlayer", StringComparison.Ordinal);
        }

        private static ManualWheelchairController FindPwdPlayerController()
        {
            ManualWheelchairController fallback = null;
            foreach (var controller in FindObjectsOfType<ManualWheelchairController>())
            {
                if (controller == null)
                    continue;

                if (IsPwdPlayerController(controller))
                    return controller;

                if (fallback == null)
                    fallback = controller;
            }

            return fallback;
        }

        public static string GetObjectId(GameObject go)
        {
            if (go == null) return null;
            // Use Rerun TrackedObject ID if available, otherwise generate from name + instance
            var tracked = go.GetComponent<Rerun.TrackedObject>();
            if (tracked != null && !string.IsNullOrEmpty(tracked.objectId))
                return tracked.objectId;
            return go.name;
        }

        public static Transform ResolveTrackingTransform(GameObject go)
        {
            if (go == null)
                return null;

            // Some runtime-spawned agents, especially PWDPlayer, keep their controller
            // on the root while motion is driven by a child Rigidbody. Prefer the actual
            // physics-driven transform so recorded review paths match what moved on-screen.
            Rigidbody[] rigidbodies = go.GetComponentsInChildren<Rigidbody>(true);
            if (rigidbodies != null && rigidbodies.Length > 0)
            {
                Rigidbody best = null;
                foreach (Rigidbody rb in rigidbodies)
                {
                    if (rb == null)
                        continue;

                    if (best == null)
                        best = rb;

                    if (!rb.isKinematic)
                    {
                        best = rb;
                        break;
                    }
                }

                if (best != null)
                    return best.transform;
            }

            var trajectory = go.GetComponent<SEAN.Scenario.Trajectory.TrackedTrajectory>();
            if (trajectory != null && trajectory.mainGameObject != null)
                return trajectory.mainGameObject.transform;

            return go.transform;
        }

        private static Vector3 GetGoalPosition(GameObject goal, out bool hasGoal)
        {
            if (goal != null)
            {
                hasGoal = true;
                return goal.transform.position;
            }

            hasGoal = false;
            return Vector3.zero;
        }
    }
}
