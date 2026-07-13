using System.Collections.Generic;
using UnityEngine;

namespace SEAN.Input
{
    public enum JoystickProfileType
    {
        /// <summary>Pick a profile automatically from the connected device names.</summary>
        Auto = 0,
        /// <summary>Logitech Extreme 3D flight stick: steer = twist (axis 2), throttle = stick Y (axis 1).</summary>
        LogitechExtreme3D = 1,
        /// <summary>Xbox-layout gamepad in XInput mode (GameSir, Xbox, 8BitDo, ...): steer = left stick X, throttle = left stick Y.</summary>
        XInputGamepad = 2,
    }

    /// <summary>
    /// Central mapping between the legacy joystick axis names the manual controllers were
    /// written against (Logitech Extreme 3D era: "LogitechTwist", "joystickLinearAxis", ...)
    /// and the concrete InputManager axes of the physically connected device.
    /// Consumers (ManualWheelchairController, VelocityController, InputPublisher) keep their
    /// serialized axis-name fields and route them through ResolveAxis()/AxisSign(), so any
    /// supported controller drives the same code path. The user picks a profile (or Auto)
    /// at runtime via JoystickProfileSwitcher; the choice persists in PlayerPrefs.
    /// </summary>
    public static class JoystickProfiles
    {
        private const string ProfilePrefKey = "SEAN.JoystickProfiles.Selected";
        private const string LinearSignPrefKey = "SEAN.JoystickProfiles.GamepadLinearSign";
        private const string SteerSignPrefKey = "SEAN.JoystickProfiles.GamepadSteerSign";
        private const float DetectionRefreshSec = 2f;

        // XInput right stick = axes 3/4, which InputManager.asset exposes under the
        // legacy Logitech axis names. Only read for camera look on the gamepad profile.
        private const string CameraLookXAxisName = "LogitechThrottle";
        private const string CameraLookYAxisName = "LogitechAuxAxis";
        public const float CameraLookDeadzone = 0.15f;

        // Aliases that predate this service; kept so axis names serialized in scenes keep working.
        private static readonly Dictionary<string, string> LogitechMap = new Dictionary<string, string>
        {
            { "RHorizontal", "LogitechTwist" },
            { "RVertical", "LogitechThrottle" },
        };

        // On an XInput pad the flight stick's twist axis (axis 2) is the shared LT/RT trigger
        // axis, so steering moves to left stick X (axis 0 = joystickHorizontalAxis). The right
        // stick sits on axes 3/4, which InputManager exposes as LogitechThrottle/LogitechAuxAxis.
        private static readonly Dictionary<string, string> XInputMap = new Dictionary<string, string>
        {
            { "LogitechTwist", "joystickHorizontalAxis" },
            { "RHorizontal", "joystickHorizontalAxis" },
            { "LogitechThrottle", "LogitechAuxAxis" },
            { "RVertical", "LogitechAuxAxis" },
        };

        private static JoystickProfileType? selectedCache;
        private static float? linearSignCache;
        private static float? steerSignCache;
        private static JoystickProfileType detectedProfile = JoystickProfileType.LogitechExtreme3D;
        private static string detectedDeviceName = string.Empty;
        private static float lastDetectionTime = float.NegativeInfinity;

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

        /// <summary>SelectedProfile with Auto resolved against the connected device names.</summary>
        public static JoystickProfileType EffectiveProfile
        {
            get
            {
                if (SelectedProfile != JoystickProfileType.Auto)
                    return SelectedProfile;
                RefreshDetection();
                return detectedProfile;
            }
        }

        /// <summary>First connected joystick name, for display. Empty when none.</summary>
        public static string DetectedDeviceName
        {
            get
            {
                RefreshDetection();
                return detectedDeviceName;
            }
        }

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
        /// Translates a legacy/logical axis name into the concrete InputManager axis for the
        /// active profile. Unknown names pass through unchanged.
        /// </summary>
        public static string ResolveAxis(string axisName)
        {
            if (string.IsNullOrWhiteSpace(axisName))
                return axisName;

            var map = EffectiveProfile == JoystickProfileType.XInputGamepad ? XInputMap : LogitechMap;
            return map.TryGetValue(axisName, out string mapped) ? mapped : axisName;
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

            switch (resolvedAxisName)
            {
                case "joystickVerticalAxis":
                case "joystickLinearAxis":
                    return GamepadLinearSign;
                case "joystickHorizontalAxis":
                case "joystickAngularAxis":
                    return GamepadSteerSign;
                default:
                    return 1f;
            }
        }

        /// <summary>True when an Xbox-layout gamepad drives the active profile and is connected.</summary>
        public static bool GamepadActive =>
            EffectiveProfile == JoystickProfileType.XInputGamepad &&
            !string.IsNullOrEmpty(DetectedDeviceName);

        /// <summary>Right-stick X for camera look, deadzoned. 0 unless the gamepad profile is active.</summary>
        public static float CameraLookX()
        {
            return ReadCameraLookAxis(CameraLookXAxisName);
        }

        /// <summary>Right-stick Y for camera look, deadzoned. 0 unless the gamepad profile is active.</summary>
        public static float CameraLookY()
        {
            return ReadCameraLookAxis(CameraLookYAxisName);
        }

        private static float ReadCameraLookAxis(string axisName)
        {
            if (!GamepadActive)
                return 0f;

            float value;
            try
            {
                value = UnityEngine.Input.GetAxisRaw(axisName);
            }
            catch (System.ArgumentException)
            {
                return 0f;
            }
            return Mathf.Abs(value) >= CameraLookDeadzone ? value : 0f;
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
                ? $"{DescribeShort()} — no joystick detected"
                : $"{DescribeShort()} — {device}";
        }

        private static void RefreshDetection()
        {
            if (Time.unscaledTime - lastDetectionTime < DetectionRefreshSec)
                return;
            lastDetectionTime = Time.unscaledTime;

            bool sawLogitech = false;
            bool sawGamepad = false;
            detectedDeviceName = string.Empty;

            string[] names = UnityEngine.Input.GetJoystickNames();
            foreach (string rawName in names)
            {
                if (string.IsNullOrWhiteSpace(rawName))
                    continue;

                if (string.IsNullOrEmpty(detectedDeviceName))
                    detectedDeviceName = rawName;

                string name = rawName.ToLowerInvariant();
                if (name.Contains("logitech") || name.Contains("extreme 3d"))
                    sawLogitech = true;
                if (name.Contains("xbox") || name.Contains("xinput") || name.Contains("gamesir") ||
                    name.Contains("gamepad") || name.Contains("wireless controller") || name.Contains("controller ("))
                    sawGamepad = true;
            }

            // Prefer the lab's Logitech stick when both are plugged in; unknown devices keep the
            // legacy Logitech mapping so pre-existing setups behave exactly as before.
            detectedProfile = sawLogitech || !sawGamepad
                ? JoystickProfileType.LogitechExtreme3D
                : JoystickProfileType.XInputGamepad;
        }
    }
}
