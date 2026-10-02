using System;
using System.IO;
using UnityEngine;
using IVI;
using SEAN.Control;

namespace SessionReview
{
    /// <summary>Which driven agent a remembered speed belongs to.</summary>
    public enum AgentSpeedRole
    {
        Pedestrian,
        Robot
    }

    /// <summary>
    /// The one place that decides how fast the two driven agents start out, and the memory
    /// of any speed change the operator makes during a run.
    ///
    /// Two things made start speeds unpredictable before this existed. First, each agent's
    /// speed fell out of whichever controller happened to own it: a pedestrian read the
    /// social-force <c>Parameters.DESIRED_SPEED</c> (0.6 m/s) even while the participant was
    /// driving it by hand, so a character that practiced at 1 m/s in the TestScene suddenly
    /// crawled the moment the study scene loaded. Second, nothing survived a scene load --
    /// the Agent Speed panel keys its rows by Transform, and every scene brings new ones.
    ///
    /// So speeds live here as absolute m/s per ROLE (pedestrian / robot), the speed panel
    /// writes back whichever role the operator just changed, and the values are persisted
    /// per session beside that session's trial logs
    /// (SessionLogs/&lt;sessionId&gt;/agent_speed_config.json) -- the same rule
    /// <see cref="JoystickTuning"/> follows, so the driving feel AND the speeds staged
    /// during practice both carry into the study scene.
    ///
    /// Speeds are written as the controllers' BASE values (moveSpeed / maxLinearCommand)
    /// with the live multipliers left at 1, never as a multiplier over some other base.
    /// That matters for the robot: VelocityController scales its angular command by
    /// speedScale to keep a commanded arc intact, so reaching 0.8 m/s by way of
    /// speedScale = 0.8 would quietly drag the turn rate down to 96 deg/s with it.
    /// </summary>
    public static class AgentSpeedSettings
    {
        /// <summary>Start speed (m/s) of every human/PWD pedestrian, driven or autonomous.</summary>
        public const float DefaultPedestrianSpeed = 1.0f;
        /// <summary>Start speed (m/s) of the robot -- its commanded-velocity cap.</summary>
        public const float DefaultRobotSpeed = 0.8f;
        /// <summary>Turn rate (deg/s) at full stick for a pedestrian.</summary>
        public const float DefaultPedestrianTurnRate = 120f;
        /// <summary>Turn rate (deg/s) at full stick for the robot.</summary>
        public const float DefaultRobotTurnRate = 120f;

        /// <summary>Range the stored speeds are clamped to (matches the panel's sliders).</summary>
        public const float MinSpeed = 0.1f;
        public const float MaxSpeed = 2.0f;

        private const string ConfigFileName = "agent_speed_config.json";

        [Serializable]
        private class SpeedData
        {
            public float pedestrianSpeed = DefaultPedestrianSpeed;
            public float robotSpeed = DefaultRobotSpeed;
            // "Someone chose this" vs "still the default". Scene-authored practice values
            // may seed an untouched role but must never overwrite an operator's choice.
            public bool pedestrianSpeedSet;
            public bool robotSpeedSet;
        }

        private static SpeedData data = new SpeedData();
        private static string loadedForSession; // null until the first load
        private static int changeCount;

        /// <summary>
        /// Bumped on every real change to a stored speed. The Agent Speed panel keeps
        /// per-agent targets of its own, so it watches this to tell "someone else moved the
        /// role's speed" (the practice Controls panel) from its own edits, and only re-seeds
        /// its rows in the former case.
        /// </summary>
        public static int ChangeCount => changeCount;

        /// <summary>Config file path for a session (lives beside its trial folders).</summary>
        public static string ConfigPath(string sessionId)
        {
            return Path.Combine(TrialDataArchive.SessionFolder(sessionId), ConfigFileName);
        }

        // Reload when the active session changes. No config file for the new session =
        // keep the current in-memory values, so speeds staged before the session id was
        // typed (the TestScene character page) are not thrown away by the id change.
        private static void EnsureLoaded()
        {
            string sid = ParticipantSession.Id;
            if (loadedForSession == sid)
                return;
            loadedForSession = sid;

            try
            {
                string path = ConfigPath(sid);
                if (File.Exists(path))
                {
                    var loaded = JsonUtility.FromJson<SpeedData>(File.ReadAllText(path));
                    if (loaded != null)
                    {
                        data = loaded;
                        Debug.Log($"[AgentSpeed] Loaded session speeds: pedestrian={data.pedestrianSpeed:F2} m/s, " +
                                  $"robot={data.robotSpeed:F2} m/s ({path})");
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[AgentSpeed] Could not read session speeds: {e.Message}");
            }
        }

        private static void Save()
        {
            try
            {
                string path = ConfigPath(loadedForSession ?? ParticipantSession.Id);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JsonUtility.ToJson(data, true));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[AgentSpeed] Could not save session speeds: {e.Message}");
            }
        }

        /// <summary>
        /// Target speed (m/s) every human/PWD pedestrian starts at. Setting it is what makes
        /// a speed staged in the practice scene show up in the study scene.
        /// </summary>
        public static float PedestrianSpeed
        {
            get { EnsureLoaded(); return data.pedestrianSpeed; }
            set
            {
                EnsureLoaded();
                float v = Mathf.Clamp(value, MinSpeed, MaxSpeed);
                if (data.pedestrianSpeedSet && Mathf.Approximately(data.pedestrianSpeed, v))
                    return; // re-applied every second by the panel; only real changes hit disk
                data.pedestrianSpeed = v;
                data.pedestrianSpeedSet = true;
                changeCount++;
                Save();
            }
        }

        /// <summary>Target speed (m/s) the robot starts at: its commanded-velocity cap.</summary>
        public static float RobotSpeed
        {
            get { EnsureLoaded(); return data.robotSpeed; }
            set
            {
                EnsureLoaded();
                float v = Mathf.Clamp(value, MinSpeed, MaxSpeed);
                if (data.robotSpeedSet && Mathf.Approximately(data.robotSpeed, v))
                    return;
                data.robotSpeed = v;
                data.robotSpeedSet = true;
                changeCount++;
                Save();
            }
        }

        /// <summary>The remembered speed (m/s) for one role.</summary>
        public static float SpeedFor(AgentSpeedRole role)
        {
            return role == AgentSpeedRole.Robot ? RobotSpeed : PedestrianSpeed;
        }

        /// <summary>Remembers a new speed (m/s) for one role.</summary>
        public static void SetSpeed(AgentSpeedRole role, float speed)
        {
            if (role == AgentSpeedRole.Robot) RobotSpeed = speed;
            else PedestrianSpeed = speed;
        }

        /// <summary>Default start speed (m/s) for one role, ignoring anything stored.</summary>
        public static float DefaultSpeedFor(AgentSpeedRole role)
        {
            return role == AgentSpeedRole.Robot ? DefaultRobotSpeed : DefaultPedestrianSpeed;
        }

        /// <summary>
        /// Puts THIS session's speeds back to the study defaults (pedestrian 1.0 m/s, robot
        /// 0.8 m/s). Called by the practice panel's Reset Defaults button.
        /// </summary>
        public static void ResetToDefaults()
        {
            EnsureLoaded();
            data = new SpeedData();
            changeCount++;
            Save();
        }

        /// <summary>
        /// Adopts a scene-authored robot speed for a session that has none of its own, so a
        /// TestScene tuned to a different practice speed still wins on a fresh session while
        /// an operator's live change is never undone by a scene load.
        /// </summary>
        public static void SeedRobotSpeed(float speed)
        {
            EnsureLoaded();
            if (data.robotSpeedSet || speed <= 0f)
                return;
            data.robotSpeed = Mathf.Clamp(speed, MinSpeed, MaxSpeed);
        }

        // Speed and turn rate are applied separately because they have different owners once
        // a run is live: speed is this class's, while the turn rate may have been retuned by
        // the participant in the practice Controls panel. The Apply* pair below is
        // initialization (spawn / controller Start); Apply*Speed is what a live speed change
        // calls, so it never undoes a tuned turn rate.

        /// <summary>Start speed and turn rate for the participant's pedestrian.</summary>
        public static void ApplyPedestrian(ManualWheelchairController manual, SFPWDAgent auto)
        {
            ApplyPedestrianSpeed(manual, auto);
            ApplyTurnRate(manual, DefaultPedestrianTurnRate);
        }

        /// <summary>
        /// Pedestrian speed in both control modes: manual driving reads moveSpeed, while
        /// autonomous walking scales the shared social-force DESIRED_SPEED -- the background
        /// crowd reads that same constant, hence a per-agent multiplier rather than a change
        /// to the constant itself.
        /// </summary>
        public static void ApplyPedestrianSpeed(ManualWheelchairController manual, SFPWDAgent auto)
        {
            float speed = PedestrianSpeed;

            if (manual != null)
            {
                manual.moveSpeed = speed;
                manual.speedScale = 1f;
            }

            if (auto != null)
                auto.autoSpeedScale = speed / Parameters.DESIRED_SPEED;
        }

        /// <summary>Start speed and turn rate for the study robot.</summary>
        public static void ApplyRobot(VelocityController robot)
        {
            if (robot == null) return;

            ApplyRobotSpeed(robot);
            robot.manualAngularSpeed = DefaultRobotTurnRate * Mathf.Deg2Rad;
            robot.maxAngularCommand = DefaultRobotTurnRate * Mathf.Deg2Rad;
        }

        /// <summary>
        /// Robot speed, written to the command cap and the manual limits with speedScale left
        /// at 1 rather than scaled in: VelocityController scales the angular command by
        /// speedScale too, so scaling the speed in would drag the turn rate down with it.
        /// </summary>
        public static void ApplyRobotSpeed(VelocityController robot)
        {
            if (robot == null) return;

            float speed = RobotSpeed;
            robot.maxLinearCommand = speed;
            robot.manualLinearSpeed = speed;
            robot.manualMaxPlanarSpeed = speed;
            robot.speedScale = 1f;
        }

        /// <summary>
        /// The TestScene robot is a prop driven by the same controller as the player, so it
        /// gets the robot's speed and turn rate rather than the pedestrian's.
        /// </summary>
        public static void ApplyPracticeRobot(ManualWheelchairController manual)
        {
            ApplyPracticeRobotSpeed(manual);
            ApplyTurnRate(manual, DefaultRobotTurnRate);
        }

        /// <summary>Robot speed for the TestScene practice prop.</summary>
        public static void ApplyPracticeRobotSpeed(ManualWheelchairController manual)
        {
            if (manual == null) return;

            manual.moveSpeed = RobotSpeed;
            manual.speedScale = 1f;
        }

        /// <summary>
        /// Sets a manual controller's turn rate (deg/s), scaling its angular accelerations by
        /// the same ratio so time-to-full-turn feels unchanged. Idempotent: re-applying the
        /// rate a controller already has leaves the accelerations alone.
        /// </summary>
        public static void ApplyTurnRate(ManualWheelchairController manual, float turnRateDegPerSec)
        {
            if (manual == null || turnRateDegPerSec <= 0f)
                return;

            if (manual.rotationSpeed > 0f && !Mathf.Approximately(manual.rotationSpeed, turnRateDegPerSec))
            {
                float turnScale = turnRateDegPerSec / manual.rotationSpeed;
                manual.inertiaAngularAcceleration *= turnScale;
                manual.inertiaAngularCoastDeceleration *= turnScale;
                manual.manualAngularAcceleration *= turnScale;
            }

            manual.rotationSpeed = turnRateDegPerSec;
        }
    }
}
