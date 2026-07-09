using System;
using System.Collections.Generic;
using UnityEngine;
using IVI;

namespace SessionReview
{
    public enum TrialEndReason
    {
        Completion,
        Timeout
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

        /// <summary>Raised once every primary agent (robot + PWD) has reached its goal,
        /// so the review UI can stop the session and surface the post-trial menu.</summary>
        public event Action SessionFullyComplete;

        private SEAN.SEAN sean;
        private bool subscribedToRobotTask;
        private bool tracking;
        private bool trialArchived;
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
            trialArchived = false;
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

            SessionReview.SessionReviewLog.Log($"[SessionReview] Registered {agentRoles.Count} agents with trajectory recorder.");
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
                    // Freeze this agent's trail once it arrives; the rest of the
                    // roster keeps being logged and keeps navigating.
                    trajectoryRecorder?.UntrackAgent(id);
                }
            }

            // Detect goal arrival for the two primary agents by distance to their
            // actual goals. This is robust even when a waypoint PWD immediately turns
            // back toward its start (which makes the agent's own CloseEnough() unreliable)
            // or when the PWD is being driven manually.
            TrackPwdArrival();
            TrackRobotArrival();

            EvaluateTrialProgress();
        }

        // Archive the trial for review the first time any primary agent finishes (or the
        // controlled task times out), keeping the session running; then, once EVERY primary
        // agent has arrived, stop the session and raise SessionFullyComplete.
        private void EvaluateTrialProgress()
        {
            if (!tracking) return;

            CountPrimaryArrivals(out int total, out int arrived);
            bool anyArrived = arrived > 0;
            bool allArrived = total > 0 && arrived >= total;
            bool taskEnded = sean != null && sean.robotTask != null && !sean.robotTask.isRunning;

            if (!trialArchived && (anyArrived || taskEnded))
                ArchiveTrial(anyArrived ? TrialEndReason.Completion : TrialEndReason.Timeout);

            if (trialArchived && allArrived)
            {
                tracking = false;
                SessionReview.SessionReviewLog.Log(
                    "[SessionReview] All primary agents reached their goals -- stopping session and showing the post-trial menu.");
                SessionFullyComplete?.Invoke();
            }
        }

        private void CountPrimaryArrivals(out int total, out int arrived)
        {
            total = 0;
            arrived = 0;
            foreach (var kv in agentRoles)
            {
                if (kv.Value != AgentRole.Robot && kv.Value != AgentRole.PWDPlayer)
                    continue;
                total++;
                if (agentArrivals.TryGetValue(kv.Key, out AgentArrivalInfo info) && info.arrived)
                    arrived++;
            }
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

        /// <summary>Ends the current trial outright (archive + stop tracking). Used when a
        /// brand-new task starts while a trial is still active.</summary>
        private void FinishCurrentTrial(TrialEndReason reason)
        {
            if (!tracking) return;
            ArchiveTrial(reason);
            tracking = false;
        }

        /// <summary>Snapshots the trial and raises <see cref="TrialEnded"/> so it is archived
        /// and reviewable. Fires at most once per trial; does not stop tracking, so remaining
        /// agents keep navigating and being logged.</summary>
        private void ArchiveTrial(TrialEndReason reason)
        {
            if (trialArchived) return;
            trialArchived = true;

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

        private void MarkArrived(string id)
        {
            if (string.IsNullOrEmpty(id) || !agentArrivals.ContainsKey(id) || agentArrivals[id].arrived)
                return;
            agentArrivals[id].arrived = true;
            agentArrivals[id].arrivalTime = Time.time;
            // Freeze this agent's trail once it arrives; the rest of the roster keeps moving.
            trajectoryRecorder?.UntrackAgent(id);
        }

        private void TrackRobotArrival()
        {
            if (sean == null || sean.robotTask == null || sean.robot == null || sean.robot.base_link == null)
                return;

            GameObject robotGoal = sean.robotTask.robotGoal;
            if (robotGoal == null)
                return;

            string robotId = GetRobotObjectId();
            if (string.IsNullOrEmpty(robotId) || !agentArrivals.ContainsKey(robotId) || agentArrivals[robotId].arrived)
                return;

            float dist = SEAN.Util.Geometry.GroundPlaneDist(
                sean.robot.base_link.transform.position, robotGoal.transform.position);
            if (dist <= sean.robotTask.completionDistance)
                MarkArrived(robotId);
        }

        // The PWD/pedestrian is not reliably classified by its own CloseEnough() (a waypoint
        // PWD flips its destination back to the start the instant it touches the goal, and a
        // manually-driven PWD may not update destPos at all). Detect arrival by distance to
        // its real goal instead, latched once reached.
        private void TrackPwdArrival()
        {
            if (pwdController == null)
                return;

            string pwdId = GetObjectId(pwdController.gameObject);
            if (string.IsNullOrEmpty(pwdId) || !agentArrivals.ContainsKey(pwdId) || agentArrivals[pwdId].arrived)
                return;

            if (TryGetPwdGoal(out Vector3 goalPos))
            {
                float dist = SEAN.Util.Geometry.GroundPlaneDist(pwdController.transform.position, goalPos);
                if (dist <= Parameters.CLOSE_ENOUGH_MIN_DIST)
                    MarkArrived(pwdId);
            }
            else if (pwdNavigable != null && pwdNavigable.CloseEnough())
            {
                // No resolvable goal (e.g. graph-nav PWD): fall back to the agent's own notion.
                MarkArrived(pwdId);
            }
        }

        private bool TryGetPwdGoal(out Vector3 goal)
        {
            goal = Vector3.zero;
            if (pwdController == null)
                return false;

            var sfpwd = pwdController.GetComponent<IVI.SFPWDAgent>();
            if (sfpwd != null && sfpwd.useWaypoints)
            {
                goal = sfpwd.waypointGoal;
                return true;
            }

            if (sean != null && sean.robotTask != null && sean.robotTask.playerGoal != null)
            {
                goal = sean.robotTask.playerGoal.transform.position;
                return true;
            }

            return false;
        }

        private string GetRobotObjectId()
        {
            if (sean.robot == null || sean.robot.base_link == null) return null;
            return GetObjectId(sean.robot.base_link);
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
