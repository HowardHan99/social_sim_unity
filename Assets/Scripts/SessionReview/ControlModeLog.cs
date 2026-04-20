using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using IVI;

namespace SessionReview
{
    public enum ControlMode
    {
        Manual,
        Auto,
        Static,
        SFPWDAuto
    }

    [Serializable]
    public class ControlModeEntry
    {
        public float timestamp;
        public string agentId;
        public ControlMode mode;
    }

    [Serializable]
    public class ControlModeLogData
    {
        public List<ControlModeEntry> entries = new List<ControlModeEntry>();
    }

    public class ControlModeLog : MonoBehaviour
    {
        public List<ControlModeEntry> Entries { get; private set; } = new List<ControlModeEntry>();

        private SEAN.Control.VelocityController velocityController;
        private ManualWheelchairController pwdController;

        private bool lastRobotManual;
        private bool lastPwdWaiting;
        private bool lastPwdManual;
        private bool robotFound;
        private bool pwdFound;
        private string robotAgentId;
        private string pwdAgentId;

        void Start()
        {
            TryFindControllers();
        }

        private void TryFindControllers()
        {
            if (!robotFound)
            {
                velocityController = FindObjectOfType<SEAN.Control.VelocityController>();
                if (velocityController != null)
                {
                    robotFound = true;
                    var sean = SEAN.SEAN.instance;
                    GameObject robotLink = (sean != null && sean.robot != null)
                        ? sean.robot.base_link : null;
                    robotAgentId = SessionTracker.GetObjectId(robotLink);
                    if (string.IsNullOrEmpty(robotAgentId))
                        robotAgentId = "robot";
                    lastRobotManual = velocityController.ManualControlActive;
                    AppendEntry(robotAgentId, lastRobotManual ? ControlMode.Manual : ControlMode.Auto);
                }
            }

            if (!pwdFound)
            {
                pwdController = FindPwdPlayerController();
                if (pwdController != null)
                {
                    pwdFound = true;
                    pwdAgentId = SessionTracker.GetObjectId(pwdController.gameObject);
                    if (string.IsNullOrEmpty(pwdAgentId))
                        pwdAgentId = "pwd_player";
                    lastPwdWaiting = true;
                    lastPwdManual = pwdController.isManualMode;
                    AppendEntry(pwdAgentId, ControlMode.Static);
                }
            }
        }

        void Update()
        {
            if (!robotFound || !pwdFound)
                TryFindControllers();

            if (robotFound && velocityController != null)
            {
                bool currentManual = velocityController.ManualControlActive;
                if (currentManual != lastRobotManual)
                {
                    lastRobotManual = currentManual;
                    AppendEntry(robotAgentId, currentManual ? ControlMode.Manual : ControlMode.Auto);
                }
            }

            if (pwdFound && pwdController != null)
            {
                bool currentManual = pwdController.isManualMode;
                bool currentWaiting = pwdController.WaitingForStart;

                if (currentWaiting && !lastPwdWaiting)
                {
                    lastPwdWaiting = true;
                    AppendEntry(pwdAgentId, ControlMode.Static);
                }
                else if (!currentWaiting && lastPwdWaiting)
                {
                    lastPwdWaiting = false;
                    AppendEntry(pwdAgentId, currentManual ? ControlMode.Manual : ControlMode.SFPWDAuto);
                    lastPwdManual = currentManual;
                }
                else if (!currentWaiting && currentManual != lastPwdManual)
                {
                    lastPwdManual = currentManual;
                    AppendEntry(pwdAgentId, currentManual ? ControlMode.Manual : ControlMode.SFPWDAuto);
                }
            }
        }

        private void AppendEntry(string agentId, ControlMode mode)
        {
            Entries.Add(new ControlModeEntry
            {
                timestamp = Time.time,
                agentId = agentId,
                mode = mode
            });
        }

        private static ManualWheelchairController FindPwdPlayerController()
        {
            ManualWheelchairController fallback = null;
            foreach (var controller in FindObjectsOfType<ManualWheelchairController>())
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

        public List<ControlModeEntry> GetEntriesInRange(float startTime, float endTime)
        {
            var result = new List<ControlModeEntry>();
            foreach (var entry in Entries)
            {
                if (entry.timestamp >= startTime && entry.timestamp <= endTime)
                    result.Add(entry);
            }
            return result;
        }

        public ControlMode GetModeAtTime(string agentId, float time)
        {
            ControlMode last = ControlMode.Auto;
            foreach (var entry in Entries)
            {
                if (entry.agentId != agentId) continue;
                if (entry.timestamp > time) break;
                last = entry.mode;
            }
            return last;
        }

        public void SaveToFile(string basePath)
        {
            SaveToFile(basePath, Entries);
        }

        public void SaveToFile(string basePath, float startTime, float endTime)
        {
            SaveToFile(basePath, GetEntriesInRange(startTime, endTime));
        }

        private void SaveToFile(string basePath, List<ControlModeEntry> entries)
        {
            string path = Path.ChangeExtension(basePath, ".ctrlmode");
            var data = new ControlModeLogData { entries = entries };
            string json = JsonUtility.ToJson(data, true);
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(path, json);
        }
    }
}
