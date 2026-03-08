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
        public Dictionary<string, AgentArrivalInfo> agentArrivals = new Dictionary<string, AgentArrivalInfo>();
        public Dictionary<string, AgentRole> agentRoles = new Dictionary<string, AgentRole>();
    }

    public delegate void OnTrialEnded(TrialEndInfo info);

    public class SessionTracker : MonoBehaviour
    {
        public event OnTrialEnded TrialEnded;

        private SEAN.SEAN sean;
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

        void Start()
        {
            sean = SEAN.SEAN.instance;
            if (sean == null || sean.robotTask == null)
            {
                enabled = false;
                return;
            }
            trajectoryRecorder = GetComponent<LiveTrajectoryRecorder>();
            sean.robotTask.onNewTask += HandleNewTask;
        }

        void OnDestroy()
        {
            if (sean != null && sean.robotTask != null)
                sean.robotTask.onNewTask -= HandleNewTask;
        }

        private void HandleNewTask()
        {
            if (tracking)
                FinishCurrentTrial(TrialEndReason.Completion);

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

            pwdController = FindObjectOfType<ManualWheelchairController>();
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

            // Also try finding PWD by SFPWDAgent + ManualWheelchairController on same object
            // in case FindObjectOfType missed it above (e.g. it's inside pedestrianBehavior.agents)
            if (pwdController == null)
            {
                foreach (var mwc in FindObjectsOfType<ManualWheelchairController>())
                {
                    if (mwc == null) continue;
                    pwdController = mwc;
                    pwdNavigable = mwc.GetComponent<IVI.INavigable>();
                    string pwdId2 = GetObjectId(mwc.gameObject);
                    if (!string.IsNullOrEmpty(pwdId2) && !agentRoles.ContainsKey(pwdId2))
                    {
                        agentRoles[pwdId2] = AgentRole.PWDPlayer;
                        agentArrivals[pwdId2] = new AgentArrivalInfo
                        {
                            objectId = pwdId2,
                            agentName = mwc.gameObject.name,
                            role = AgentRole.PWDPlayer,
                            arrived = false,
                            arrivalTime = -1f
                        };
                    }
                    break;
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
                    trajectoryRecorder.TrackAgent(id, pwdController.transform);
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
            if (!tracking) return;

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
                    }
                }
            }

            if (!sean.robotTask.isRunning && tracking)
                FinishCurrentTrial(TrialEndReason.Timeout);
        }

        private void FinishCurrentTrial(TrialEndReason reason)
        {
            if (!tracking) return;
            tracking = false;

            var info = new TrialEndInfo
            {
                trialName = trialName,
                trialNumber = trialNumber,
                startTime = trialStartTime,
                endTime = Time.time,
                reason = reason,
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

        public static string GetObjectId(GameObject go)
        {
            if (go == null) return null;
            // Use Rerun TrackedObject ID if available, otherwise generate from name + instance
            var tracked = go.GetComponent<Rerun.TrackedObject>();
            if (tracked != null && !string.IsNullOrEmpty(tracked.objectId))
                return tracked.objectId;
            return go.name;
        }
    }
}
