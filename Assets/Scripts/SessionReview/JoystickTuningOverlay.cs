using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using IVI;
using SEAN.Control;
using SEAN.Input;

namespace SessionReview
{
    /// <summary>
    /// The handful of driving-feel values a participant may need to change, applied to BOTH
    /// the player character (ManualWheelchairController -- wheelchair or walking avatar) and
    /// the robot (VelocityController), plus the gamepad camera look. Persisted as a
    /// PER-SESSION config file next to that session's trial logs:
    /// SessionLogs/&lt;sessionId&gt;/joystick_config.json.
    ///
    /// Deliberately a handful of knobs, not a controller-engineering panel. They split into
    /// two groups, and the panel lists them in that order because it is the thing people get
    /// wrong: max speed / max turn rate are the CAPS (felt immediately), while the two
    /// sensitivity values only reshape the stick curve on the way to those caps -- at full
    /// deflection they change nothing at all. A panel of curve-shaping knobs alone reads as
    /// "these sliders do nothing" to anyone who drives with the stick pushed fully over,
    /// which is why the turn-rate cap is exposed alongside the speed cap.
    ///
    /// "Sensitivity" is one number -- how far the stick must travel to command full output --
    /// and drives the underlying full-throw value; the per-axis sensitivity multipliers stay
    /// at 1 so there is a single thing to reason about.
    ///
    /// Rules: values only take over once a slider is touched (enabled flag); a session
    /// without a config file inherits the current live values ("defaults to the previous
    /// session") and gets its own file on the first change; switching the session id
    /// (onboarding page) hot-loads that session's saved tuning.
    ///
    /// The exception is max speed. It is not stored here: speeds are per agent role and have
    /// to survive the load into the study scene, so the "Fastest it can drive" row reads and
    /// writes <see cref="AgentSpeedSettings"/> for whichever agent is being driven. Which
    /// also means dragging that row alone does not flip the enabled flag.
    /// </summary>
    public static class JoystickTuning
    {
        public const float DefaultDriveSensitivity = 0.5f;
        public const float DefaultTurnSensitivity = 0.5f;
        public const float DefaultTurnRate = AgentSpeedSettings.DefaultPedestrianTurnRate;
        public const float DefaultDeadzone = 0.03f;
        public const float DefaultLookSpeed = 45f;

        private const string ConfigFileName = "joystick_config.json";
        // Bumped when a stored field changes meaning. v1: turnRate became the real deg/s
        // rate (both controllers' baseline is 120 deg/s) instead of a 240-based ratio.
        private const int CurrentVersion = 1;
        private const float LegacyDefaultTurnRate = 240f;

        [Serializable]
        private class TuningData
        {
            // Left at 0 so a file written before versioning existed reads as pre-v1; Save()
            // stamps the current version.
            public int version;
            public bool enabled;
            // 0 = must push the stick all the way for full output, 1 = a tenth of the travel
            // is already full output. Drive and turn are separate: they are different
            // motions and rarely feel right at the same setting.
            public float driveSensitivity = DefaultDriveSensitivity;
            public float turnSensitivity = DefaultTurnSensitivity;
            // Turn rate cap (deg/s). The sensitivity knobs above only reshape the stick
            // curve, so at full deflection they change nothing -- this is the knob that
            // actually makes turning faster or slower.
            public float turnRate = DefaultTurnRate;
            public float deadzone = DefaultDeadzone;
            // Gamepad right-stick camera yaw speed, deg/s.
            public float lookSpeed = DefaultLookSpeed;
            // Input device profile (0 = Auto). Auto prefers a connected gamepad.
            public int profile = (int)JoystickProfileType.Auto;
        }

        private static TuningData data = new TuningData();
        private static string loadedForSession; // null until the first load

        /// <summary>Stick travel that already commands full output, from the 0..1 sensitivity.</summary>
        public static float FullThrowFor(float sensitivity)
        {
            return Mathf.Lerp(1.0f, 0.1f, Mathf.Clamp01(sensitivity));
        }

        /// <summary>Config file path for a session (lives beside its trial folders).</summary>
        public static string ConfigPath(string sessionId)
        {
            return Path.Combine(TrialDataArchive.SessionFolder(sessionId), ConfigFileName);
        }

        // Reload when the active session changes. No config file for the new session =
        // keep the current live values (inherit from the previous session).
        private static void EnsureLoaded()
        {
            string sid = ParticipantSession.Id;
            if (loadedForSession == sid)
                return;
            loadedForSession = sid;

            bool loadedFromFile = false;
            try
            {
                string path = ConfigPath(sid);
                if (File.Exists(path))
                {
                    var loaded = JsonUtility.FromJson<TuningData>(File.ReadAllText(path));
                    if (loaded != null)
                    {
                        data = loaded;
                        loadedFromFile = true;
                        MigrateLoadedData();
                        Debug.Log($"[JoystickTuning] Loaded session config: {path}");
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[JoystickTuning] Could not read session config: {e.Message}");
            }

            // A fresh session (no saved file) follows whatever device is connected (Auto),
            // never a stray manual profile left over from a previous participant. A saved
            // session restores the input device that participant confirmed while practicing.
            if (!loadedFromFile)
                data.profile = (int)JoystickProfileType.Auto;

            JoystickProfiles.SelectedProfile = (JoystickProfileType)data.profile;
        }

        // A pre-v1 file stored the turn rate against a 240 baseline that no controller
        // actually had (both are 120 deg/s), so its numbers read ~2x the rate they produced.
        // Rescaling by the baseline ratio keeps an old session driving exactly as it did
        // while the panel now reads true deg/s.
        private static void MigrateLoadedData()
        {
            if (data.version >= CurrentVersion)
                return;

            data.turnRate *= DefaultTurnRate / LegacyDefaultTurnRate;
            data.version = CurrentVersion;
        }

        private static void Save()
        {
            try
            {
                string path = ConfigPath(loadedForSession ?? ParticipantSession.Id);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                data.version = CurrentVersion;
                File.WriteAllText(path, JsonUtility.ToJson(data, true));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[JoystickTuning] Could not save session config: {e.Message}");
            }
        }

        /// <summary>True once the user has adjusted any slider; the overlay then owns the
        /// joystick fields on every live controller (re-applied continuously).</summary>
        public static bool Enabled
        {
            get { EnsureLoaded(); return data.enabled; }
        }

        /// <summary>0..1: how little stick travel is needed for full forward/back speed.</summary>
        public static float DriveSensitivity
        {
            get { EnsureLoaded(); return data.driveSensitivity; }
            set { EnsureLoaded(); data.driveSensitivity = value; data.enabled = true; Save(); }
        }

        /// <summary>0..1: how little stick travel is needed for the full turn rate.</summary>
        public static float TurnSensitivity
        {
            get { EnsureLoaded(); return data.turnSensitivity; }
            set { EnsureLoaded(); data.turnSensitivity = value; data.enabled = true; Save(); }
        }

        // Max speed deliberately does NOT live here. It is per role (pedestrian 1.0 m/s,
        // robot 0.8 m/s) and has to survive the load into the study scene, neither of which
        // one shared number in this file could do -- see AgentSpeedSettings, which the
        // "Fastest it can drive" row reads and writes for whichever agent is being driven.

        /// <summary>Max turn rate (deg/s) at full stick, for the wheelchair. Other
        /// controllers scale from their own baseline by the same ratio.</summary>
        public static float TurnRate
        {
            get { EnsureLoaded(); return data.turnRate; }
            set { EnsureLoaded(); data.turnRate = value; data.enabled = true; Save(); }
        }

        public static float Deadzone
        {
            get { EnsureLoaded(); return data.deadzone; }
            set { EnsureLoaded(); data.deadzone = value; data.enabled = true; Save(); }
        }

        // Turn rate is applied as a RATIO of each controller's own baseline rather than
        // written flat: the walking player deliberately turns slower than the wheelchair
        // (RandomAvatar.walkerTurnSpeed) and the robot's angular scale is an order of
        // magnitude below both. Scaling preserves those relationships; a flat write would
        // erase them the moment any slider was touched.
        private static readonly Dictionary<int, float> turnBaselines = new Dictionary<int, float>();

        private static float TurnBaseline(int instanceId, float current)
        {
            if (!turnBaselines.TryGetValue(instanceId, out float baseline))
            {
                baseline = current;
                turnBaselines[instanceId] = baseline;
            }
            return baseline;
        }

        /// <summary>Restores a controller's authored turn rate (used by Reset Defaults).</summary>
        public static float TurnBaselineFor(int instanceId, float current)
        {
            return TurnBaseline(instanceId, current);
        }

        /// <summary>Gamepad right-stick camera yaw speed (deg/s); pitch follows at ~60%.</summary>
        public static float LookSpeed
        {
            get { EnsureLoaded(); return data.lookSpeed; }
            set { EnsureLoaded(); data.lookSpeed = value; data.enabled = true; Save(); }
        }

        /// <summary>
        /// Input device for this session. Auto prefers a connected gamepad; an explicit
        /// choice is remembered and re-applied when the session's real scene loads, so the
        /// input method confirmed in practice carries into the study. Independent of the
        /// slider-override <see cref="Enabled"/> flag.
        /// </summary>
        public static JoystickProfileType Profile
        {
            get { EnsureLoaded(); return (JoystickProfileType)data.profile; }
            set
            {
                EnsureLoaded();
                data.profile = (int)value;
                JoystickProfiles.SelectedProfile = value;
                Save();
            }
        }

        public static void ApplyTo(ManualWheelchairController controller)
        {
            if (controller == null || !Enabled)
                return;

            controller.joystickLinearFullThrow = FullThrowFor(DriveSensitivity);
            controller.joystickAngularFullThrow = FullThrowFor(TurnSensitivity);
            // The multipliers stay neutral so each sensitivity means exactly "how far do I
            // push for full output", with nothing else scaling on top.
            controller.joystickLinearSensitivity = 1f;
            controller.joystickAngularSensitivity = 1f;
            // Stick POSITION commands speed. Under the acceleration model the stick only set
            // how quickly you reach top speed, so holding it always ended at the same speed
            // and the sensitivity sliders felt like they did nothing.
            controller.manualInertiaDrive = false;
            controller.angularDirectDrive = true;
            // EffectiveJoystickDeadzone() takes max(joystickDeadzone, joystickStartupDeadzone),
            // so both must be written or the slider is a no-op below the startup value.
            controller.joystickDeadzone = Deadzone;
            controller.joystickStartupDeadzone = Deadzone;
            controller.rotationSpeed =
                TurnBaseline(controller.GetInstanceID(), controller.rotationSpeed)
                * (TurnRate / DefaultTurnRate);
        }

        public static void ApplyTo(VelocityController controller)
        {
            if (controller == null || !Enabled)
                return;

            controller.joystickLinearFullThrow = FullThrowFor(DriveSensitivity);
            controller.joystickAngularFullThrow = FullThrowFor(TurnSensitivity);
            controller.joystickLinearSensitivity = 1f;
            controller.joystickAngularSensitivity = 1f;
            controller.manualInertiaDrive = false; // stick position commands speed (see above)
            controller.joystickLinearDeadzone = Deadzone;
            controller.joystickAngularDeadzone = Deadzone;
            controller.manualAngularSpeed =
                TurnBaseline(controller.GetInstanceID(), controller.manualAngularSpeed)
                * (TurnRate / DefaultTurnRate);
        }

        public static void ApplyTo(GamepadCameraLook look)
        {
            if (look == null || !Enabled)
                return;

            look.yawSpeed = LookSpeed;
            look.pitchSpeed = LookSpeed * 0.62f;
        }

        /// <summary>Resets THIS session's tuning to defaults and hands the joystick fields
        /// back to the controllers' own Inspector/Start defaults (until a slider is touched
        /// again). Agent speeds go back to the study defaults too, since the panel's top row
        /// edits those. The input profile returns to Auto (follow the connected device).</summary>
        public static void ResetToDefaults()
        {
            EnsureLoaded();
            data = new TuningData(); // enabled = false, all defaults, profile = Auto
            JoystickProfiles.SelectedProfile = JoystickProfileType.Auto;
            AgentSpeedSettings.ResetToDefaults();
            Save();
        }
    }

    /// <summary>
    /// Runtime IMGUI panel ([U] toggles; the TestScene opens it automatically once driving
    /// starts) with the input-device row and six sliders: max speed, max turn rate, the two
    /// stick-travel sensitivities, deadzone and look speed. Values apply to the player
    /// character AND the robot together.
    ///
    /// They are re-applied to every live controller once per second: this survives
    /// ApplyJoystickResponseDefaults() rewriting fields in each controller's Start(), scene
    /// loads, and player respawns. Self-bootstraps; works without a SessionReviewManager
    /// (e.g. in the TestScene).
    /// </summary>
    public class JoystickTuningOverlay : MonoBehaviour
    {
        private static JoystickTuningOverlay instance;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (instance != null) return;
            var go = new GameObject("JoystickTuningOverlay");
            go.AddComponent<JoystickTuningOverlay>();
            DontDestroyOnLoad(go);
        }

        public KeyCode toggleKey = KeyCode.U;

        private const float RescanInterval = 1.0f;

        private const float MinDeadzone = 0f;
        private const float MaxDeadzone = 0.3f;
        private const float MinSpeed = 0.2f;
        private const float MaxSpeed = 2.0f;
        private const float MinTurnRate = 60f;
        private const float MaxTurnRate = 360f;
        private const float MinLookSpeed = 15f;
        private const float MaxLookSpeed = 120f;

        private readonly List<ManualWheelchairController> playerControllers = new List<ManualWheelchairController>();
        private VelocityController robotController;
        private GamepadCameraLook cameraLook;
        // The panel is a practice-scene tool: it only appears where TestSceneFlowManager
        // lives. The values keep applying everywhere else.
        private TestSceneFlowManager practiceFlow;
        private float nextRescanTime;
        private bool visible;
        private GUIStyle titleStyle;
        private GUIStyle rowStyle;
        private GUIStyle valueStyle;
        private GUIStyle hintStyle;

        void Awake()
        {
            if (instance != null && instance != this)
            {
                Destroy(gameObject);
                return;
            }
            instance = this;
        }

        /// <summary>Force the panel visible (TestScene shows it when driving starts).</summary>
        public static void Show()
        {
            if (instance == null)
                Bootstrap();
            if (instance != null)
                instance.visible = true;
        }

        // Only treat keyboard focus as "typing" while a page with a text field is open.
        // (IMGUI sliders also latch GUIUtility.keyboardControl, so a plain != 0 check
        // would permanently eat the hotkey after any slider was clicked.)
        private static bool IsSessionTextEntryOpen()
        {
            var srm = SessionReviewManager.Instance;
            if (srm != null && srm.IsOnboardingActive)
                return true;

            var flow = FindObjectOfType<TestSceneFlowManager>();
            return flow != null && flow.IsCharacterSelectOpen;
        }

        void Update()
        {
            if (Time.unscaledTime >= nextRescanTime)
            {
                Rescan();
                nextRescanTime = Time.unscaledTime + RescanInterval;

                // Continuous ownership: newly spawned controllers and Start()-time
                // ApplyJoystickResponseDefaults() rewrites are corrected within a second.
                // This runs in every scene -- only the panel is practice-scene-only.
                ApplyToAll();
            }

            // Tuning is done during practice; in the study scene the panel stays away so a
            // participant can neither see it nor open it by accident.
            if (practiceFlow == null)
            {
                visible = false;
                return;
            }

            bool typing = GUIUtility.keyboardControl != 0 && IsSessionTextEntryOpen();
            if (Input.GetKeyDown(toggleKey) && !typing)
                visible = !visible;
        }

        private void Rescan()
        {
            robotController = FindObjectOfType<VelocityController>();
            cameraLook = FindObjectOfType<GamepadCameraLook>();
            practiceFlow = FindObjectOfType<TestSceneFlowManager>();
            playerControllers.Clear();
            playerControllers.AddRange(FindObjectsOfType<ManualWheelchairController>(true));
        }

        private void ApplyToAll()
        {
            if (!JoystickTuning.Enabled)
                return;

            JoystickTuning.ApplyTo(robotController);
            JoystickTuning.ApplyTo(cameraLook);
            foreach (var pwd in playerControllers)
                JoystickTuning.ApplyTo(pwd);
        }

        // Pushes a just-changed role speed onto whatever is driving right now, so the slider
        // is felt on this frame rather than at the next respawn. The Agent Speed panel picks
        // the same change up from AgentSpeedSettings within a frame.
        private void ApplySpeedToLive(AgentSpeedRole role)
        {
            var practiceRobot = practiceFlow != null ? practiceFlow.PracticeRobotController : null;

            if (role == AgentSpeedRole.Robot)
            {
                AgentSpeedSettings.ApplyRobotSpeed(robotController);
                AgentSpeedSettings.ApplyPracticeRobotSpeed(practiceRobot);
                return;
            }

            foreach (var pwd in playerControllers)
            {
                if (pwd == null || pwd == practiceRobot) continue;
                AgentSpeedSettings.ApplyPedestrianSpeed(pwd, pwd.GetComponent<SFPWDAgent>());
            }
        }

        // After Reset Defaults the live controllers must get the default feel back
        // immediately (they would otherwise keep the last tuned values until respawn).
        private void ApplyDefaultsToAll()
        {
            float driveThrow = JoystickTuning.FullThrowFor(JoystickTuning.DefaultDriveSensitivity);
            float turnThrow = JoystickTuning.FullThrowFor(JoystickTuning.DefaultTurnSensitivity);
            var practiceRobot = practiceFlow != null ? practiceFlow.PracticeRobotController : null;

            if (robotController != null)
            {
                robotController.joystickLinearFullThrow = driveThrow;
                robotController.joystickAngularFullThrow = turnThrow;
                robotController.joystickLinearSensitivity = 1f;
                robotController.joystickAngularSensitivity = 1f;
                robotController.joystickLinearDeadzone = JoystickTuning.DefaultDeadzone;
                robotController.joystickAngularDeadzone = JoystickTuning.DefaultDeadzone;
                // Speeds were reset with the tuning, so re-read them rather than restoring a
                // baseline this panel captured.
                AgentSpeedSettings.ApplyRobotSpeed(robotController);
                robotController.manualAngularSpeed = JoystickTuning.TurnBaselineFor(
                    robotController.GetInstanceID(), robotController.manualAngularSpeed);
            }

            foreach (var pwd in playerControllers)
            {
                if (pwd == null) continue;
                pwd.joystickLinearFullThrow = driveThrow;
                pwd.joystickAngularFullThrow = turnThrow;
                pwd.joystickLinearSensitivity = 1f;
                pwd.joystickAngularSensitivity = 1f;
                pwd.joystickDeadzone = JoystickTuning.DefaultDeadzone;
                pwd.joystickStartupDeadzone = JoystickTuning.DefaultDeadzone;
                if (pwd == practiceRobot)
                    AgentSpeedSettings.ApplyPracticeRobotSpeed(pwd);
                else
                    AgentSpeedSettings.ApplyPedestrianSpeed(pwd, pwd.GetComponent<SFPWDAgent>());
                pwd.rotationSpeed = JoystickTuning.TurnBaselineFor(
                    pwd.GetInstanceID(), pwd.rotationSpeed);
            }

            if (cameraLook != null)
            {
                cameraLook.yawSpeed = JoystickTuning.DefaultLookSpeed;
                cameraLook.pitchSpeed = JoystickTuning.DefaultLookSpeed * 0.62f;
            }
        }

        private bool ShouldHide()
        {
            var srm = SessionReviewManager.Instance;
            return srm != null && (srm.IsReviewModeActive
                                   || srm.IsWorldBuildingModeActive
                                   || srm.IsOnboardingActive);
        }

        void OnGUI()
        {
            if (!visible || practiceFlow == null || ShouldHide()) return;

            // Never shown during review, so the RewindController scrubber-docking
            // convention (TryGetProgressBarRect) is satisfied without a dock check.
            ReviewUiScale.Apply();

            if (titleStyle == null)
            {
                titleStyle = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft, fontSize = 15 };
                rowStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleLeft, fontSize = 14 };
                valueStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleRight, fontStyle = FontStyle.Bold, fontSize = 14 };
                hintStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleLeft, fontSize = 12, wordWrap = true };
            }

            const float margin = 16f;
            const float pad = 14f;
            const float gap = 12f;
            const float rowH = 38f;
            const float headerH = 28f;
            const float profileRowH = 40f;
            // Two lines: the device name plus the both-connected note wrap on narrow panels.
            const float deviceRowH = 36f;
            // Four wrapped lines: the hint has to explain why the two "stick travel" rows
            // feel like nothing when you drive with the stick pushed all the way over.
            const float hintH = 76f;
            const float buttonH = 30f;
            const float labelW = 208f;
            const float valueW = 96f;

            const int rowCount = 6;
            float barW = Mathf.Min(520f, ReviewUiScale.Width - 2f * margin);
            float barH = pad * 2f + headerH + profileRowH + deviceRowH + rowCount * rowH + hintH + buttonH + 8f;
            // Docked mid-left so participants can tune while driving: clear of the
            // top-left driving HUD / overhead mini-cam and the bottom-right F8 overlay.
            float x = margin;
            float y = (ReviewUiScale.Height - barH) * 0.5f;

            GUI.Box(new Rect(x, y, barW, barH), GUIContent.none);

            float rx = x + pad;
            float rw = barW - 2f * pad;
            string sessionLabel = string.IsNullOrEmpty(ParticipantSession.Id) ? "unassigned" : ParticipantSession.Id;
            GUI.Label(new Rect(rx, y + pad, rw, headerH),
                $"Controls -- session {sessionLabel}   ([{toggleKey}] hide)", titleStyle);

            float sliderW = rw - labelW - gap - valueW - gap;
            float rowY = y + pad + headerH;

            // Which physical controller drives. Auto prefers a connected gamepad; the
            // flight stick stays available even while both are plugged in.
            DrawProfileRow(new Rect(rx, rowY, rw, profileRowH), labelW, gap);
            rowY += profileRowH;

            string device = JoystickProfiles.DetectedDeviceName;
            string both = JoystickProfiles.BothDevicesConnected ? "  (the other one is ignored)" : string.Empty;
            GUI.Label(new Rect(rx, rowY + 2f, rw, deviceRowH - 4f),
                string.IsNullOrEmpty(device)
                    ? "Using: nothing connected"
                    : $"Using: {device}{both}",
                hintStyle);
            rowY += deviceRowH;

            // Ordered so the two rows you feel immediately come first: these set the CAPS.
            // The two "stick travel" rows below only reshape the curve on the way to those
            // caps, which is why they seem to do nothing if you always shove the stick over.
            // Speed belongs to the agent being driven right now, not to the panel: the robot
            // and the pedestrian run at different speeds, and the value has to survive into
            // the study scene (AgentSpeedSettings owns both of those).
            AgentSpeedRole role = practiceFlow.IsDrivingRobot
                ? AgentSpeedRole.Robot
                : AgentSpeedRole.Pedestrian;
            float roleSpeed = AgentSpeedSettings.SpeedFor(role);
            float maxSpd = DrawSliderRow(new Rect(rx, rowY, rw, rowH), "Fastest it can drive",
                roleSpeed, MinSpeed, MaxSpeed, labelW, sliderW, valueW, gap,
                $"{roleSpeed:F2} m/s");
            rowY += rowH;
            float turnRate = DrawSliderRow(new Rect(rx, rowY, rw, rowH), "Fastest it can turn",
                JoystickTuning.TurnRate, MinTurnRate, MaxTurnRate, labelW, sliderW, valueW, gap,
                $"{JoystickTuning.TurnRate:F0}°/s");
            rowY += rowH;
            float driveSens = DrawSliderRow(new Rect(rx, rowY, rw, rowH), "Push needed: full speed",
                JoystickTuning.DriveSensitivity, 0f, 1f, labelW, sliderW, valueW, gap,
                $"{JoystickTuning.FullThrowFor(JoystickTuning.DriveSensitivity) * 100f:F0}% of the way");
            rowY += rowH;
            float turnSens = DrawSliderRow(new Rect(rx, rowY, rw, rowH), "Push needed: full turn",
                JoystickTuning.TurnSensitivity, 0f, 1f, labelW, sliderW, valueW, gap,
                $"{JoystickTuning.FullThrowFor(JoystickTuning.TurnSensitivity) * 100f:F0}% of the way");
            rowY += rowH;
            float dz = DrawSliderRow(new Rect(rx, rowY, rw, rowH), "Hand-wobble ignored near center",
                JoystickTuning.Deadzone, MinDeadzone, MaxDeadzone, labelW, sliderW, valueW, gap,
                $"under {JoystickTuning.Deadzone * 100f:F0}%");
            rowY += rowH;
            float look = DrawSliderRow(new Rect(rx, rowY, rw, rowH), "Camera look-around speed",
                JoystickTuning.LookSpeed, MinLookSpeed, MaxLookSpeed, labelW, sliderW, valueW, gap,
                $"{JoystickTuning.LookSpeed:F0}°/s");
            rowY += rowH;

            bool changed =
                ApplyIfChanged(driveSens, JoystickTuning.DriveSensitivity, v => JoystickTuning.DriveSensitivity = v) |
                ApplyIfChanged(turnSens, JoystickTuning.TurnSensitivity, v => JoystickTuning.TurnSensitivity = v) |
                ApplyIfChanged(maxSpd, roleSpeed, v =>
                {
                    AgentSpeedSettings.SetSpeed(role, v);
                    ApplySpeedToLive(role);
                }) |
                ApplyIfChanged(turnRate, JoystickTuning.TurnRate, v => JoystickTuning.TurnRate = v) |
                ApplyIfChanged(dz, JoystickTuning.Deadzone, v => JoystickTuning.Deadzone = v) |
                ApplyIfChanged(look, JoystickTuning.LookSpeed, v => JoystickTuning.LookSpeed = v);

            if (changed)
                ApplyToAll();

            string status = JoystickTuning.Enabled
                ? "The top two rows are the LIMITS -- you feel those change straight away. "
                  + "The two \"push needed\" rows only change how the stick feels on the way there: "
                  + "lower = you hit the limit with a smaller push (twitchier). Held all the way over, "
                  + "you always get the limits above, so those two feel like nothing if you never "
                  + "feather the stick.  The D-pad works like W/A/S/D."
                : "Move a slider to take over the defaults.  The D-pad works like W/A/S/D.";
            GUI.Label(new Rect(rx, rowY, rw, hintH), status, hintStyle);
            rowY += hintH;

            if (GUI.Button(new Rect(rx, rowY + 4f, 150f, buttonH), "Reset Defaults"))
            {
                JoystickTuning.ResetToDefaults();
                ApplyDefaultsToAll();
            }
        }

        // Input-device selector: Auto (prefer a connected gamepad) / Gamepad / Stick.
        private void DrawProfileRow(Rect rect, float labelW, float gap)
        {
            GUI.Label(new Rect(rect.x, rect.y, labelW, rect.height), "Controller", rowStyle);

            float bx = rect.x + labelW + gap;
            float bw = (rect.xMax - bx - 2f * 6f) / 3f;
            var current = JoystickTuning.Profile;

            if (DrawProfileButton(new Rect(bx, rect.y + 2f, bw, rect.height - 4f), "Auto",
                    current == JoystickProfileType.Auto))
                JoystickTuning.Profile = JoystickProfileType.Auto;
            bx += bw + 6f;
            if (DrawProfileButton(new Rect(bx, rect.y + 2f, bw, rect.height - 4f), "Gamepad",
                    current == JoystickProfileType.XInputGamepad))
                JoystickTuning.Profile = JoystickProfileType.XInputGamepad;
            bx += bw + 6f;
            if (DrawProfileButton(new Rect(bx, rect.y + 2f, bw, rect.height - 4f), "Stick",
                    current == JoystickProfileType.LogitechExtreme3D))
                JoystickTuning.Profile = JoystickProfileType.LogitechExtreme3D;
        }

        private bool DrawProfileButton(Rect rect, string label, bool active)
        {
            var prev = GUI.backgroundColor;
            if (active) GUI.backgroundColor = new Color(0.3f, 0.55f, 0.85f);
            bool clicked = GUI.Button(rect, label);
            GUI.backgroundColor = prev;
            return clicked && !active;
        }

        private float DrawSliderRow(Rect rowRect, string label, float value, float min, float max,
            float labelW, float sliderW, float valueW, float gap, string readout)
        {
            GUI.Label(new Rect(rowRect.x, rowRect.y, labelW, rowRect.height), label, rowStyle);
            float sliderX = rowRect.x + labelW + gap;
            float slider = GUI.HorizontalSlider(
                new Rect(sliderX, rowRect.y + rowRect.height * 0.5f - 4f, sliderW, 18f),
                value, min, max);
            GUI.Label(new Rect(sliderX + sliderW + gap, rowRect.y, valueW, rowRect.height),
                readout, valueStyle);
            return slider;
        }

        private static bool ApplyIfChanged(float newValue, float current, System.Action<float> setter)
        {
            if (Mathf.Approximately(newValue, current))
                return false;
            setter(newValue);
            return true;
        }
    }
}
