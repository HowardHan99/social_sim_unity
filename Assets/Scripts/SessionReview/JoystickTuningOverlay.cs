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
    /// One shared set of joystick response values applied to BOTH the player character
    /// (ManualWheelchairController -- wheelchair or walking avatar) and the robot
    /// (VelocityController). Persisted as a PER-SESSION config file next to that
    /// session's trial logs: SessionLogs/&lt;sessionId&gt;/joystick_config.json.
    ///
    /// Rules: values only take over once a slider is touched (enabled flag); a session
    /// without a config file inherits the current live values ("defaults to the previous
    /// session") and gets its own file on the first change; switching the session id
    /// (onboarding page) hot-loads that session's saved tuning.
    /// </summary>
    public static class JoystickTuning
    {
        // Defaults mirror the shared controller defaults (ManualWheelchairController and
        // VelocityController ship with identical joystick response values).
        public const float DefaultLinearSensitivity = 1.0f;
        public const float DefaultAngularSensitivity = 1.0f;
        public const float DefaultLinearFullThrow = 0.1f;
        public const float DefaultAngularFullThrow = 1.0f;
        public const float DefaultDeadzone = 0.03f;
        public const float DefaultMaxSpeed = 0.8f;

        private const string ConfigFileName = "joystick_config.json";

        [Serializable]
        private class TuningData
        {
            public bool enabled;
            public float linearSensitivity = DefaultLinearSensitivity;
            public float angularSensitivity = DefaultAngularSensitivity;
            public float linearFullThrow = DefaultLinearFullThrow;
            public float angularFullThrow = DefaultAngularFullThrow;
            public float deadzone = DefaultDeadzone;
            public float maxSpeed = DefaultMaxSpeed;
            // Input device profile (0 = Auto). Auto follows the connected device names,
            // so it matches whatever controller the participant is actually using.
            public int profile = (int)JoystickProfileType.Auto;
        }

        private static TuningData data = new TuningData();
        private static string loadedForSession; // null until the first load

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
            // session restores the input profile that participant confirmed while practicing.
            if (!loadedFromFile)
                data.profile = (int)JoystickProfileType.Auto;

            JoystickProfiles.SelectedProfile = (JoystickProfileType)data.profile;
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
                Debug.LogWarning($"[JoystickTuning] Could not save session config: {e.Message}");
            }
        }

        /// <summary>True once the user has adjusted any slider; the overlay then owns the
        /// joystick fields on every live controller (re-applied continuously).</summary>
        public static bool Enabled
        {
            get { EnsureLoaded(); return data.enabled; }
        }

        public static float LinearSensitivity
        {
            get { EnsureLoaded(); return data.linearSensitivity; }
            set { EnsureLoaded(); data.linearSensitivity = value; data.enabled = true; Save(); }
        }

        public static float AngularSensitivity
        {
            get { EnsureLoaded(); return data.angularSensitivity; }
            set { EnsureLoaded(); data.angularSensitivity = value; data.enabled = true; Save(); }
        }

        /// <summary>Stick travel fraction that already commands full speed (smaller = reach max sooner).</summary>
        public static float LinearFullThrow
        {
            get { EnsureLoaded(); return data.linearFullThrow; }
            set { EnsureLoaded(); data.linearFullThrow = value; data.enabled = true; Save(); }
        }

        public static float AngularFullThrow
        {
            get { EnsureLoaded(); return data.angularFullThrow; }
            set { EnsureLoaded(); data.angularFullThrow = value; data.enabled = true; Save(); }
        }

        public static float Deadzone
        {
            get { EnsureLoaded(); return data.deadzone; }
            set { EnsureLoaded(); data.deadzone = value; data.enabled = true; Save(); }
        }

        /// <summary>Max manual speed (m/s) for the player character and the robot.</summary>
        public static float MaxSpeed
        {
            get { EnsureLoaded(); return data.maxSpeed; }
            set { EnsureLoaded(); data.maxSpeed = value; data.enabled = true; Save(); }
        }

        /// <summary>
        /// Input device profile for this session. Auto follows the connected controller;
        /// an explicit choice is remembered and re-applied when the session's real scene
        /// loads, so the input method confirmed in practice carries into the study.
        /// Independent of the slider-override <see cref="Enabled"/> flag.
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

            controller.joystickLinearSensitivity = LinearSensitivity;
            controller.joystickAngularSensitivity = AngularSensitivity;
            controller.joystickLinearFullThrow = LinearFullThrow;
            controller.joystickAngularFullThrow = AngularFullThrow;
            // EffectiveJoystickDeadzone() takes max(joystickDeadzone, joystickStartupDeadzone),
            // so both must be written or the slider is a no-op below the startup value.
            controller.joystickDeadzone = Deadzone;
            controller.joystickStartupDeadzone = Deadzone;
            controller.moveSpeed = MaxSpeed;
        }

        public static void ApplyTo(VelocityController controller)
        {
            if (controller == null || !Enabled)
                return;

            controller.joystickLinearSensitivity = LinearSensitivity;
            controller.joystickAngularSensitivity = AngularSensitivity;
            controller.joystickLinearFullThrow = LinearFullThrow;
            controller.joystickAngularFullThrow = AngularFullThrow;
            controller.joystickLinearDeadzone = Deadzone;
            controller.joystickAngularDeadzone = Deadzone;
            controller.manualLinearSpeed = MaxSpeed;
            controller.manualMaxPlanarSpeed = MaxSpeed;
        }

        /// <summary>Resets THIS session's tuning to defaults and hands the joystick fields
        /// back to the controllers' own Inspector/Start defaults (until a slider is touched
        /// again). The input profile returns to Auto (follow the connected device). The
        /// session's config file records the disabled state.</summary>
        public static void ResetToDefaults()
        {
            EnsureLoaded();
            data = new TuningData(); // enabled = false, all defaults, profile = Auto
            JoystickProfiles.SelectedProfile = JoystickProfileType.Auto;
            Save();
        }
    }

    /// <summary>
    /// Runtime IMGUI panel ([U] toggles) with sliders for joystick sensitivity, full-throw
    /// (how far the stick must be pushed for max speed) and deadzone. One set of values is
    /// applied to the player character AND the robot together.
    ///
    /// Values are re-applied to every live controller once per second: this survives
    /// ApplyJoystickResponseDefaults() rewriting fields in each controller's Start(),
    /// scene loads, and player respawns. Self-bootstraps; no scene wiring needed. Works in
    /// scenes without a SessionReviewManager (e.g. TestScene).
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

        // Slider ranges stay inside the clamps in ApplyJoystickResponseDefaults()
        // (linear sensitivity > 2 and angular sensitivity > 1.5 get reset to 1 there).
        private const float MinSensitivity = 0.1f;
        private const float MaxLinearSensitivity = 2.0f;
        private const float MaxAngularSensitivity = 1.5f;
        private const float MinLinearFullThrow = 0.02f;
        private const float MinAngularFullThrow = 0.05f;
        private const float MaxFullThrow = 1.0f;
        private const float MaxDeadzone = 0.3f;

        private readonly List<ManualWheelchairController> playerControllers = new List<ManualWheelchairController>();
        private VelocityController robotController;
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
            bool typing = GUIUtility.keyboardControl != 0 && IsSessionTextEntryOpen();
            if (Input.GetKeyDown(toggleKey) && !typing)
                visible = !visible;

            if (Time.unscaledTime >= nextRescanTime)
            {
                Rescan();
                nextRescanTime = Time.unscaledTime + RescanInterval;

                // Continuous ownership: newly spawned controllers and Start()-time
                // ApplyJoystickResponseDefaults() rewrites are corrected within a second.
                ApplyToAll();
            }
        }

        private void Rescan()
        {
            robotController = FindObjectOfType<VelocityController>();
            playerControllers.Clear();
            playerControllers.AddRange(FindObjectsOfType<ManualWheelchairController>(true));
        }

        private void ApplyToAll()
        {
            if (!JoystickTuning.Enabled)
                return;

            JoystickTuning.ApplyTo(robotController);
            foreach (var pwd in playerControllers)
                JoystickTuning.ApplyTo(pwd);
        }

        // After Reset Defaults the live controllers must get the default feel back
        // immediately (they would otherwise keep the last tuned values until respawn).
        private void ApplyDefaultsToAll()
        {
            if (robotController != null)
            {
                robotController.joystickLinearSensitivity = JoystickTuning.DefaultLinearSensitivity;
                robotController.joystickAngularSensitivity = JoystickTuning.DefaultAngularSensitivity;
                robotController.joystickLinearFullThrow = JoystickTuning.DefaultLinearFullThrow;
                robotController.joystickAngularFullThrow = JoystickTuning.DefaultAngularFullThrow;
                robotController.joystickLinearDeadzone = JoystickTuning.DefaultDeadzone;
                robotController.joystickAngularDeadzone = JoystickTuning.DefaultDeadzone;
                robotController.manualLinearSpeed = JoystickTuning.DefaultMaxSpeed;
                robotController.manualMaxPlanarSpeed = JoystickTuning.DefaultMaxSpeed;
            }

            foreach (var pwd in playerControllers)
            {
                if (pwd == null) continue;
                pwd.joystickLinearSensitivity = JoystickTuning.DefaultLinearSensitivity;
                pwd.joystickAngularSensitivity = JoystickTuning.DefaultAngularSensitivity;
                pwd.joystickLinearFullThrow = JoystickTuning.DefaultLinearFullThrow;
                pwd.joystickAngularFullThrow = JoystickTuning.DefaultAngularFullThrow;
                pwd.joystickDeadzone = JoystickTuning.DefaultDeadzone;
                pwd.joystickStartupDeadzone = JoystickTuning.DefaultDeadzone;
                pwd.moveSpeed = JoystickTuning.DefaultMaxSpeed;
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
            if (!visible || ShouldHide()) return;

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
            const float rowH = 34f;
            const float headerH = 28f;
            const float hintH = 34f;
            const float buttonH = 30f;
            const float labelW = 170f;
            const float valueW = 56f;

            const int rowCount = 6;
            const float profileRowH = 34f;
            const float deviceRowH = 20f;
            float barW = Mathf.Min(430f, ReviewUiScale.Width - 2f * margin);
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
                $"Joystick Tuning -- session {sessionLabel}   ([{toggleKey}] hide)", titleStyle);

            float sliderW = rw - labelW - gap - valueW - gap;
            float rowY = y + pad + headerH;

            // Input-device profile: Auto follows the connected controller; an explicit pick
            // is remembered for this session and carried into the real study scene.
            DrawProfileRow(new Rect(rx, rowY, rw, profileRowH), labelW, gap);
            rowY += profileRowH;
            string device = SEAN.Input.JoystickProfiles.DetectedDeviceName;
            GUI.Label(new Rect(rx, rowY, rw, deviceRowH),
                string.IsNullOrEmpty(device) ? "No controller detected" : $"Detected: {device}", hintStyle);
            rowY += deviceRowH;

            float linSens = DrawSliderRow(new Rect(rx, rowY, rw, rowH), "Linear sensitivity",
                JoystickTuning.LinearSensitivity, MinSensitivity, MaxLinearSensitivity,
                labelW, sliderW, valueW, gap);
            rowY += rowH;
            float angSens = DrawSliderRow(new Rect(rx, rowY, rw, rowH), "Turn sensitivity",
                JoystickTuning.AngularSensitivity, MinSensitivity, MaxAngularSensitivity,
                labelW, sliderW, valueW, gap);
            rowY += rowH;
            float linThrow = DrawSliderRow(new Rect(rx, rowY, rw, rowH), "Full speed at throw",
                JoystickTuning.LinearFullThrow, MinLinearFullThrow, MaxFullThrow,
                labelW, sliderW, valueW, gap);
            rowY += rowH;
            float angThrow = DrawSliderRow(new Rect(rx, rowY, rw, rowH), "Full turn at throw",
                JoystickTuning.AngularFullThrow, MinAngularFullThrow, MaxFullThrow,
                labelW, sliderW, valueW, gap);
            rowY += rowH;
            float dz = DrawSliderRow(new Rect(rx, rowY, rw, rowH), "Deadzone",
                JoystickTuning.Deadzone, 0f, MaxDeadzone,
                labelW, sliderW, valueW, gap);
            rowY += rowH;
            float maxSpd = DrawSliderRow(new Rect(rx, rowY, rw, rowH), "Max speed (m/s)",
                JoystickTuning.MaxSpeed, 0.2f, 2.0f,
                labelW, sliderW, valueW, gap);
            rowY += rowH;

            bool changed =
                ApplyIfChanged(linSens, JoystickTuning.LinearSensitivity, v => JoystickTuning.LinearSensitivity = v) |
                ApplyIfChanged(angSens, JoystickTuning.AngularSensitivity, v => JoystickTuning.AngularSensitivity = v) |
                ApplyIfChanged(linThrow, JoystickTuning.LinearFullThrow, v => JoystickTuning.LinearFullThrow = v) |
                ApplyIfChanged(angThrow, JoystickTuning.AngularFullThrow, v => JoystickTuning.AngularFullThrow = v) |
                ApplyIfChanged(dz, JoystickTuning.Deadzone, v => JoystickTuning.Deadzone = v) |
                ApplyIfChanged(maxSpd, JoystickTuning.MaxSpeed, v => JoystickTuning.MaxSpeed = v);

            if (changed)
                ApplyToAll();

            string status = JoystickTuning.Enabled
                ? "Tuning active: applied to player + robot, saved to this session's config file."
                : "Move a slider to take over; controllers keep their Inspector values until then.";
            GUI.Label(new Rect(rx, rowY, rw, hintH), status, hintStyle);
            rowY += hintH;

            if (GUI.Button(new Rect(rx, rowY + 4f, 150f, buttonH), "Reset Defaults"))
            {
                JoystickTuning.ResetToDefaults();
                ApplyDefaultsToAll();
            }
        }

        // Input-device profile selector: Auto (follow the connected device) / Stick / Gamepad.
        private void DrawProfileRow(Rect rect, float labelW, float gap)
        {
            GUI.Label(new Rect(rect.x, rect.y, labelW, rect.height), "Input device", rowStyle);

            float bx = rect.x + labelW + gap;
            float bw = (rect.xMax - bx - 2f * 6f) / 3f;
            var current = JoystickTuning.Profile;

            if (DrawProfileButton(new Rect(bx, rect.y + 2f, bw, rect.height - 4f), "Auto",
                    current == JoystickProfileType.Auto))
                JoystickTuning.Profile = JoystickProfileType.Auto;
            bx += bw + 6f;
            if (DrawProfileButton(new Rect(bx, rect.y + 2f, bw, rect.height - 4f), "Stick",
                    current == JoystickProfileType.LogitechExtreme3D))
                JoystickTuning.Profile = JoystickProfileType.LogitechExtreme3D;
            bx += bw + 6f;
            if (DrawProfileButton(new Rect(bx, rect.y + 2f, bw, rect.height - 4f), "Gamepad",
                    current == JoystickProfileType.XInputGamepad))
                JoystickTuning.Profile = JoystickProfileType.XInputGamepad;
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
            float labelW, float sliderW, float valueW, float gap)
        {
            GUI.Label(new Rect(rowRect.x, rowRect.y, labelW, rowRect.height), label, rowStyle);
            float sliderX = rowRect.x + labelW + gap;
            float slider = GUI.HorizontalSlider(
                new Rect(sliderX, rowRect.y + rowRect.height * 0.5f - 4f, sliderW, 18f),
                value, min, max);
            GUI.Label(new Rect(sliderX + sliderW + gap, rowRect.y, valueW, rowRect.height),
                slider.ToString("F2"), valueStyle);
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
