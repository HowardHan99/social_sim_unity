using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace SessionReview
{
    public enum OverallControlMode
    {
        PureManual,
        PureAuto,
        PureStatic,
        Combined
    }

    [Serializable]
    public class MetricsSnapshot
    {
        public double minDistToPed;
        public double minDistToTarget;
        public double pathLength;
        public uint robotOnPersonCollisions;
        public uint personOnRobotCollisions;
        public uint robotOnPersonIntimateViolations;
        public uint personOnRobotIntimateViolations;
        public uint robotOnPersonPersonalViolations;
        public uint personOnRobotPersonalViolations;
        public uint objectCollisions;
    }

    [Serializable]
    public class AgentRoleEntry
    {
        public string objectId;
        public AgentRole role;
    }

    [Serializable]
    public class AgentControlSummary
    {
        public string agentId;
        public OverallControlMode overallMode;
        public float manualSeconds;
        public float autoSeconds;
        public float staticSeconds;
    }

    [Serializable]
    public class TrialRecord
    {
        public string trialName;
        public ushort trialNumber;
        public float startTime;
        public float endTime;
        public TrialEndReason endReason;

        public List<AgentRoleEntry> agentRoles = new List<AgentRoleEntry>();
        public List<AgentArrivalInfo> agentArrivals = new List<AgentArrivalInfo>();
        public List<ControlModeEntry> controlModeEntries = new List<ControlModeEntry>();
        public List<AgentControlSummary> controlSummaries = new List<AgentControlSummary>();
        public MetricsSnapshot metrics;

        public string rerunstateFilePath;

        public float Duration => endTime - startTime;
    }

    [Serializable]
    public class TrialArchiveData
    {
        public List<TrialRecord> trials = new List<TrialRecord>();
    }

    public class TrialDataArchive : MonoBehaviour
    {
        public List<TrialRecord> Trials { get; private set; } = new List<TrialRecord>();

        private SessionTracker sessionTracker;
        private ControlModeLog controlModeLog;

        private LiveTrajectoryRecorder trajectoryRecorder;

        void Start()
        {
            sessionTracker = GetComponent<SessionTracker>();
            controlModeLog = GetComponent<ControlModeLog>();
            trajectoryRecorder = GetComponent<LiveTrajectoryRecorder>();

            if (sessionTracker != null)
                sessionTracker.TrialEnded += OnTrialEnded;
        }

        void OnDestroy()
        {
            if (sessionTracker != null)
                sessionTracker.TrialEnded -= OnTrialEnded;
        }

        private void OnTrialEnded(TrialEndInfo info)
        {
            var record = new TrialRecord
            {
                trialName = info.trialName,
                trialNumber = info.trialNumber,
                startTime = info.startTime,
                endTime = info.endTime,
                endReason = info.reason,
                metrics = CaptureMetrics()
            };

            foreach (var kvp in info.agentRoles)
                record.agentRoles.Add(new AgentRoleEntry { objectId = kvp.Key, role = kvp.Value });

            foreach (var kvp in info.agentArrivals)
                record.agentArrivals.Add(kvp.Value);

            if (controlModeLog != null)
            {
                record.controlModeEntries = controlModeLog.GetEntriesInRange(info.startTime, info.endTime);
                record.controlSummaries = ComputeControlSummaries(record);
            }

            Trials.Add(record);

            string trialFolder = CreateTrialFolder(record);
            SaveTrialRecord(record, trialFolder);

            if (trajectoryRecorder != null)
                trajectoryRecorder.SaveTrialTrajectories(trialFolder, record);

            if (controlModeLog != null)
                controlModeLog.SaveToFile(Path.Combine(trialFolder, "control_modes"), record.startTime, record.endTime);

            SaveTrials();
        }

        private MetricsSnapshot CaptureMetrics()
        {
            var sean = SEAN.SEAN.instance;
            if (sean == null || sean.metrics == null)
                return new MetricsSnapshot();

            var m = sean.metrics;
            return new MetricsSnapshot
            {
                minDistToPed = m.MinDistToPed,
                minDistToTarget = m.MinDistToTarget,
                pathLength = m.PathLength,
                robotOnPersonCollisions = m.RobotOnPersonCollisions,
                personOnRobotCollisions = m.PersonOnRobotCollisions,
                robotOnPersonIntimateViolations = m.RobotOnPersonIntimateDistViolations,
                personOnRobotIntimateViolations = m.PersonOnRobotIntimateDistViolations,
                robotOnPersonPersonalViolations = m.RobotOnPersonPersonalDistViolations,
                personOnRobotPersonalViolations = m.PersonOnRobotPersonalDistViolations,
                objectCollisions = m.ObjectCollisions
            };
        }

        private List<AgentControlSummary> ComputeControlSummaries(TrialRecord record)
        {
            var summaries = new List<AgentControlSummary>();
            var agentIds = new HashSet<string>();
            foreach (var entry in record.controlModeEntries)
                agentIds.Add(entry.agentId);

            foreach (string agentId in agentIds)
            {
                var agentEntries = record.controlModeEntries.FindAll(e => e.agentId == agentId);
                float manualSec = 0f, autoSec = 0f, staticSec = 0f;
                var modesUsed = new HashSet<ControlMode>();

                for (int i = 0; i < agentEntries.Count; i++)
                {
                    float start = agentEntries[i].timestamp;
                    float end = (i + 1 < agentEntries.Count)
                        ? agentEntries[i + 1].timestamp
                        : record.endTime;
                    float dur = Mathf.Max(0f, end - start);
                    modesUsed.Add(agentEntries[i].mode);

                    switch (agentEntries[i].mode)
                    {
                        case ControlMode.Manual: manualSec += dur; break;
                        case ControlMode.Auto: autoSec += dur; break;
                        case ControlMode.SFPWDAuto: autoSec += dur; break;
                        case ControlMode.Static: staticSec += dur; break;
                    }
                }

                OverallControlMode overall;
                if (modesUsed.Count > 1)
                    overall = OverallControlMode.Combined;
                else if (modesUsed.Contains(ControlMode.Manual))
                    overall = OverallControlMode.PureManual;
                else if (modesUsed.Contains(ControlMode.Static))
                    overall = OverallControlMode.PureStatic;
                else
                    overall = OverallControlMode.PureAuto;

                summaries.Add(new AgentControlSummary
                {
                    agentId = agentId,
                    overallMode = overall,
                    manualSeconds = manualSec,
                    autoSeconds = autoSec,
                    staticSeconds = staticSec
                });
            }

            return summaries;
        }

        private static string LogFolder
        {
            get
            {
#if UNITY_EDITOR
                return System.IO.Path.GetFullPath("Assets/../SessionLogs/");
#else
                return Application.persistentDataPath + "/SessionLogs/";
#endif
            }
        }

        private string CreateTrialFolder(TrialRecord record)
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string folderName = $"trial_{record.trialNumber:D3}_{stamp}";
            string path = Path.Combine(LogFolder, folderName);
            if (!Directory.Exists(path))
                Directory.CreateDirectory(path);
            Debug.Log($"[SessionReview] Trial folder: {path}");
            return path;
        }

        private void SaveTrialRecord(TrialRecord record, string trialFolder)
        {
            string json = JsonUtility.ToJson(record, true);
            string path = Path.Combine(trialFolder, "trial_info.json");
            File.WriteAllText(path, json);
        }

        private void SaveTrials()
        {
            string folder = LogFolder;
            if (!Directory.Exists(folder))
                Directory.CreateDirectory(folder);

            string path = Path.Combine(folder, "trials.json");
            var data = new TrialArchiveData { trials = Trials };
            string json = JsonUtility.ToJson(data, true);
            File.WriteAllText(path, json);
            Debug.Log($"[SessionReview] All trials index saved to: {path}");
        }

        public TrialRecord GetTrial(int index)
        {
            if (index < 0 || index >= Trials.Count) return null;
            return Trials[index];
        }

        public TrialRecord LatestTrial => Trials.Count > 0 ? Trials[Trials.Count - 1] : null;
        public int TrialCount => Trials.Count;
    }
}
