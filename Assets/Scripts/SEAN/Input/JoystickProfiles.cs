using UnityEngine;

namespace SEAN.Input
{
    public enum JoystickProfileType
    {
        /// <summary>Pick a device automatically: a gamepad when one is connected, else the flight stick.</summary>
        Auto = 0,
        /// <summary>Logitech Extreme 3D flight stick: steer = twist (axis 2), throttle = stick Y (axis 1).</summary>
        LogitechExtreme3D = 1,
        /// <summary>Xbox-layout gamepad in XInput mode (GameSir, Xbox, 8BitDo, ...): steer = right stick X, throttle = right stick Y (the left stick looks around).</summary>
        XInputGamepad = 2,
    }

    /// <summary>
    /// Routes the manual controllers' joystick reads to ONE chosen physical device, so a
    /// gamepad and a flight stick can stay plugged in at the same time.
    ///
    /// The project's original axes (LogitechTwist, joystickVerticalAxis, ...) are all
    /// joyNum 0, i.e. "any joystick": with two devices connected their axes collide, and a
    /// resting flight-stick throttle would drive the camera while its stick tilt steered the
    /// agent. InputManager therefore also defines per-slot axes JoyNAxisK (N = 1..4 joystick
    /// slot, K = 0..4 physical axis), and this service resolves every request to the axis of
    /// the ACTIVE device only. The other device is never read.
    ///
    /// Consumers (ManualWheelchairController, VelocityController, InputPublisher) keep their
    /// serialized axis-name fields and route them through ResolveAxis()/AxisSign(); the names
    /// are interpreted as roles (steer / throttle / look) rather than literal axes.
    /// The active device is chosen by the user (or Auto) via the joystick tuning panel or
    /// JoystickProfileSwitcher; the choice persists per session.
    /// </summary>
    public static class JoystickProfiles
    {
        private const string ProfilePrefKey = "SEAN.JoystickProfiles.Selected";
        private const string LinearSignPrefKey = "SEAN.JoystickProfiles.GamepadLinearSign";
        private const string SteerSignPrefKey = "SEAN.JoystickProfiles.GamepadSteerSign";
        private const float DetectionRefreshSec = 2f;

        /// <summary>Joystick slots that InputManager defines JoyNAxisK entries for.</summary>
        private const int MaxJoySlots = 4;

        public const float CameraLookDeadzone = 0.15f;

        /// <summary>What a requested axis name means, independent of the device wiring.</summary>
        private enum AxisRole { None, Steer, Throttle, LookX, LookY, DpadX, DpadY }

        private static JoystickProfileType? selectedCache;
        private static float? linearSignCache;
        private static float? steerSignCache;

        // Which slot (1-based) holds each device; 0 = not connected.
        private static int gamepadJoyNum;
        private static int logitechJoyNum;
        private static string gamepadDeviceName = string.Empty;
        private static string logitechDeviceName = string.Empty;
        private static float lastDetectionTime = float.NegativeInfinity;

        // Rest position of the look axes, so a controller that idles off-center can never
        // drive the camera on its own.
        private static float lookCenterX;
        private static float lookCenterY;
        private static bool lookCentersCaptured;
        private static string lookCenterKey;

        /// <summary>The user's explicit choice (Auto by default). Persisted in PlayerPrefs.</summary>
        public static JoystickProfileType SelectedProfile
        {
            get
            {
                if (!selectedCache.HasValue)
                    selectedCache = (JoystickProfileType)PlayerPrefs.GetInt(ProfilePrefKey, (int)JoystickProfileType.Auto);
                return selectedCache.Value;
            }
            set
            {
                selectedCache = value;
                PlayerPrefs.SetInt(ProfilePrefKey, (int)value);
                PlayerPrefs.Save();
            }
        }

        /// <summary>
        /// SelectedProfile with Auto resolved. Auto prefers a connected gamepad -- that is the
        /// primary study controller -- and falls back to the flight stick, which stays
        /// available by connecting it alone or by choosing it explicitly.
        /// </summary>
        public static JoystickProfileType EffectiveProfile
        {
            get
            {
                RefreshDetection();

                if (SelectedProfile == JoystickProfileType.XInputGamepad)
                    return JoystickProfileType.XInputGamepad;
                if (SelectedProfile == JoystickProfileType.LogitechExtreme3D)
                    return JoystickProfileType.LogitechExtreme3D;

                if (gamepadJoyNum > 0)
                    return JoystickProfileType.XInputGamepad;
                if (logitechJoyNum > 0)
                    return JoystickProfileType.LogitechExtreme3D;
                return JoystickProfileType.XInputGamepad;
            }
        }

        /// <summary>Joystick slot (1-based) of the active device; 0 when it is not connected.</summary>
        private static int ActiveJoyNum
        {
            get
            {
                RefreshDetection();
                return EffectiveProfile == JoystickProfileType.XInputGamepad ? gamepadJoyNum : logitechJoyNum;
            }
        }

        /// <summary>
        /// Joystick slot (1-based) of the active device, 0 when not connected. Needed to read
        /// that device's BUTTONS, which are KeyCode.JoystickNButtonK rather than axes.
        /// </summary>
        public static int ActiveJoystickNumber => ActiveJoyNum;

        /// <summary>Name of the device the active mapping drives. Empty when it is not connected.</summary>
        public static string DetectedDeviceName
        {
            get
            {
                RefreshDetection();
                return EffectiveProfile == JoystickProfileType.XInputGamepad ? gamepadDeviceName : logitechDeviceName;
            }
        }

        /// <summary>True when a gamepad is connected and is the active device.</summary>
        public static bool GamepadActive =>
            EffectiveProfile == JoystickProfileType.XInputGamepad && gamepadJoyNum > 0;

        /// <summary>
        /// Flips forward/back on the gamepad profile only, in case the pad reports stick-up with
        /// the opposite sign to the Logitech stick. Leaves the Logitech profile untouched.
        /// </summary>
        public static float GamepadLinearSign
        {
            get
            {
                if (!linearSignCache.HasValue)
                    linearSignCache = PlayerPrefs.GetFloat(LinearSignPrefKey, 1f);
                return linearSignCache.Value;
            }
            set
            {
                linearSignCache = value < 0f ? -1f : 1f;
                PlayerPrefs.SetFloat(LinearSignPrefKey, linearSignCache.Value);
                PlayerPrefs.Save();
            }
        }

        /// <summary>Flips left/right steering on the gamepad profile only.</summary>
        public static float GamepadSteerSign
        {
            get
            {
                if (!steerSignCache.HasValue)
                    steerSignCache = PlayerPrefs.GetFloat(SteerSignPrefKey, 1f);
                return steerSignCache.Value;
            }
            set
            {
                steerSignCache = value < 0f ? -1f : 1f;
                PlayerPrefs.SetFloat(SteerSignPrefKey, steerSignCache.Value);
                PlayerPrefs.Save();
            }
        }

        /// <summary>
        /// Translates a legacy/logical axis name into the concrete InputManager axis of the
        /// ACTIVE device. Names that carry no driving role (buttons such as "L1") pass through.
        /// </summary>
        public static string ResolveAxis(string axisName)
        {
            if (string.IsNullOrWhiteSpace(axisName))
                return axisName;

            AxisRole role = RoleOf(axisName);
            if (role == AxisRole.None)
                return axisName;

            return AxisNameFor(role);
        }

        /// <summary>
        /// Per-profile polarity correction, keyed by the resolved axis name. Multiply the raw
        /// axis value by this before the consumer's own invert flags so one physical device
        /// never requires re-tuning the per-agent inversion settings.
        /// </summary>
        public static float AxisSign(string resolvedAxisName)
        {
            if (EffectiveProfile != JoystickProfileType.XInputGamepad)
                return 1f;

            switch (AxisIndexOfResolvedName(resolvedAxisName))
            {
                case 3: return GamepadSteerSign;    // right stick X (steer)
                case 4: return GamepadLinearSign;   // right stick Y (throttle)
                default: return 1f;
            }
        }

        /// <summary>Right-stick X for camera look, centered + deadzoned. 0 unless a gamepad is active.</summary>
        public static float CameraLookX()
        {
            return ReadCameraLookAxis(AxisRole.LookX, true);
        }

        /// <summary>Right-stick Y for camera look, centered + deadzoned. 0 unless a gamepad is active.</summary>
        public static float CameraLookY()
        {
            return ReadCameraLookAxis(AxisRole.LookY, false);
        }

        /// <summary>D-pad horizontal on the active gamepad: -1 left, +1 right, 0 otherwise.</summary>
        public static float DpadX()
        {
            return GamepadActive ? RawAxisOrZero(AxisNameFor(AxisRole.DpadX)) : 0f;
        }

        /// <summary>D-pad vertical on the active gamepad: +1 up, -1 down, 0 otherwise.</summary>
        public static float DpadY()
        {
            return GamepadActive ? RawAxisOrZero(AxisNameFor(AxisRole.DpadY)) : 0f;
        }

        /// <summary>
        /// True on the frame the gamepad's Y button (button 3 on the Xbox layout) was
        /// pressed. Only fires on the gamepad profile, so flight-stick buttons keep their
        /// legacy meanings. Used as the UI confirm/start button.
        /// </summary>
        public static bool UiStartPressedThisFrame()
        {
            return GamepadButtonDown(3);
        }

        /// <summary>
        /// KeyDown for a button on the active gamepad only (Xbox layout: A=0, B=1, X=2, Y=3),
        /// read via the per-slot KeyCode.JoystickNButtonK so other devices' buttons never alias.
        /// </summary>
        public static bool GamepadButtonDown(int buttonIndex)
        {
            if (!GamepadActive || buttonIndex < 0 || buttonIndex > 19)
                return false;

            int joy = ActiveJoystickNumber;
            if (joy < 1 || joy > MaxJoySlots)
                return UnityEngine.Input.GetKeyDown((KeyCode)((int)KeyCode.JoystickButton0 + buttonIndex));

            return UnityEngine.Input.GetKeyDown(
                (KeyCode)((int)KeyCode.Joystick1Button0 + (joy - 1) * 20 + buttonIndex));
        }

        public static void CycleSelectedProfile()
        {
            switch (SelectedProfile)
            {
                case JoystickProfileType.Auto:
                    SelectedProfile = JoystickProfileType.LogitechExtreme3D;
                    break;
                case JoystickProfileType.LogitechExtreme3D:
                    SelectedProfile = JoystickProfileType.XInputGamepad;
                    break;
                default:
                    SelectedProfile = JoystickProfileType.Auto;
                    break;
            }
        }

        /// <summary>Short label for on-screen hints, e.g. "Gamepad (auto)".</summary>
        public static string DescribeShort()
        {
            string label = EffectiveProfile == JoystickProfileType.XInputGamepad ? "Gamepad" : "Logitech stick";
            return SelectedProfile == JoystickProfileType.Auto ? label + " (auto)" : label;
        }

        public static string Describe()
        {
            string device = DetectedDeviceName;
            return string.IsNullOrEmpty(device)
                ? $"{DescribeShort()} — not connected"
                : $"{DescribeShort()} — {device}";
        }

        /// <summary>True when both a gamepad and the flight stick are plugged in.</summary>
        public static bool BothDevicesConnected
        {
            get
            {
                RefreshDetection();
                return gamepadJoyNum > 0 && logitechJoyNum > 0;
            }
        }

        #region Axis wiring

        private static AxisRole RoleOf(string axisName)
        {
            switch (axisName)
            {
                // Steering: the flight-stick era named it after the twist axis.
                case "LogitechTwist":
                case "RHorizontal":
                case "joystickHorizontalAxis":
                case "joystickAngularAxis":
                    return AxisRole.Steer;

                case "joystickVerticalAxis":
                case "joystickLinearAxis":
                    return AxisRole.Throttle;

                case "LogitechThrottle":
                    return AxisRole.LookX;
                case "LogitechAuxAxis":
                case "RVertical":
                    return AxisRole.LookY;

                default:
                    return AxisRole.None;
            }
        }

        /// <summary>Physical axis index of a role on the active device; -1 when it has none.</summary>
        private static int AxisIndexFor(AxisRole role)
        {
            if (EffectiveProfile == JoystickProfileType.XInputGamepad)
            {
                switch (role)
                {
                    // Hands swapped: the RIGHT stick drives the agent (forward/back + turn) and
                    // the LEFT stick looks around, instead of the usual left-drives/right-looks.
                    case AxisRole.Steer: return 3;      // right stick X
                    case AxisRole.Throttle: return 4;   // right stick Y
                    case AxisRole.LookX: return 0;      // left stick X
                    case AxisRole.LookY: return 1;      // left stick Y
                    case AxisRole.DpadX: return 5;      // D-pad horizontal
                    case AxisRole.DpadY: return 6;      // D-pad vertical
                }
                return -1;
            }

            switch (role)
            {
                case AxisRole.Steer: return 2;          // twist
                case AxisRole.Throttle: return 1;       // stick Y
                default: return -1;                     // no second stick to look with
            }
        }

        private static string AxisNameFor(AxisRole role)
        {
            int index = AxisIndexFor(role);
            if (index < 0)
                return string.Empty;

            int joy = ActiveJoyNum;
            if (joy >= 1 && joy <= MaxJoySlots)
                return $"Joy{joy}Axis{index}";

            // The device is not connected (or sits beyond the configured slots): fall back to
            // the shared any-joystick axes so a single-controller setup still works.
            return SharedAxisName(index);
        }

        private static string SharedAxisName(int axisIndex)
        {
            switch (axisIndex)
            {
                case 0: return "joystickHorizontalAxis";
                case 1: return "joystickVerticalAxis";
                case 2: return "LogitechTwist";
                case 3: return "LogitechThrottle";
                case 4: return "LogitechAuxAxis";
                default: return string.Empty;
            }
        }

        /// <summary>Physical axis index behind a name produced by ResolveAxis; -1 if unknown.</summary>
        private static int AxisIndexOfResolvedName(string resolvedAxisName)
        {
            if (string.IsNullOrEmpty(resolvedAxisName))
                return -1;

            // "JoyNAxisK"
            if (resolvedAxisName.Length >= 9 && resolvedAxisName.StartsWith("Joy"))
            {
                char last = resolvedAxisName[resolvedAxisName.Length - 1];
                if (last >= '0' && last <= '9')
                    return last - '0';
            }

            switch (resolvedAxisName)
            {
                case "joystickHorizontalAxis":
                case "joystickAngularAxis":
                    return 0;
                case "joystickVerticalAxis":
                case "joystickLinearAxis":
                    return 1;
                case "LogitechTwist": return 2;
                case "LogitechThrottle": return 3;
                case "LogitechAuxAxis": return 4;
                default: return -1;
            }
        }

        #endregion

        #region Camera look

        private static float ReadCameraLookAxis(AxisRole role, bool isX)
        {
            if (!GamepadActive)
                return 0f;

            string axisName = AxisNameFor(role);
            if (string.IsNullOrEmpty(axisName))
                return 0f;

            EnsureLookCentersCaptured();
            float value = RawAxisOrZero(axisName) - (isX ? lookCenterX : lookCenterY);
            return Mathf.Abs(value) >= CameraLookDeadzone ? value : 0f;
        }

        /// <summary>
        /// Records where the look axes sit at rest and treats that as zero, so a controller
        /// that idles off-center (or a mis-picked device whose axis 3 is a throttle lever)
        /// can never feed the camera a permanent input. Re-captured when the device changes.
        /// </summary>
        private static void EnsureLookCentersCaptured()
        {
            string key = $"{ActiveJoyNum}:{gamepadDeviceName}";
            if (lookCentersCaptured && lookCenterKey == key)
                return;

            lookCenterKey = key;
            lookCenterX = RawAxisOrZero(AxisNameFor(AxisRole.LookX));
            lookCenterY = RawAxisOrZero(AxisNameFor(AxisRole.LookY));
            lookCentersCaptured = true;
        }

        private static float RawAxisOrZero(string axisName)
        {
            if (string.IsNullOrEmpty(axisName))
                return 0f;

            try
            {
                return UnityEngine.Input.GetAxisRaw(axisName);
            }
            catch (System.ArgumentException)
            {
                return 0f;
            }
        }

        #endregion

        private static void RefreshDetection()
        {
            if (Time.unscaledTime - lastDetectionTime < DetectionRefreshSec)
                return;
            lastDetectionTime = Time.unscaledTime;

            int previousGamepadSlot = gamepadJoyNum;
            gamepadJoyNum = 0;
            logitechJoyNum = 0;
            gamepadDeviceName = string.Empty;
            logitechDeviceName = string.Empty;

            // Index i of GetJoystickNames() is joystick slot i + 1, which is what the
            // JoyNAxisK entries in InputManager are bound to.
            string[] names = UnityEngine.Input.GetJoystickNames();
            for (int i = 0; i < names.Length && i < MaxJoySlots; i++)
            {
                string rawName = names[i];
                if (string.IsNullOrWhiteSpace(rawName))
                    continue;

                string name = rawName.ToLowerInvariant();
                bool isLogitech = name.Contains("logitech") || name.Contains("extreme 3d");

                if (isLogitech)
                {
                    if (logitechJoyNum == 0)
                    {
                        logitechJoyNum = i + 1;
                        logitechDeviceName = rawName;
                    }
                }
                else if (gamepadJoyNum == 0)
                {
                    // Anything that is not the flight stick is an Xbox-layout pad, including
                    // devices that report an unrecognized name (GameSir over BT/HID).
                    gamepadJoyNum = i + 1;
                    gamepadDeviceName = rawName;
                }
            }

            if (gamepadJoyNum != previousGamepadSlot)
                lookCentersCaptured = false;
        }
    }
}
