using UnityEngine;

namespace SEAN.Input
{
    /// <summary>
    /// Runtime picker for JoystickProfiles. Self-bootstraps into every scene (no prefab wiring):
    ///   F10 — cycle profile: Auto -> Logitech stick -> Gamepad -> Auto
    ///   F11 — flip gamepad forward/back polarity (gamepad profile only)
    ///   F9  — flip gamepad steering polarity (gamepad profile only)
    /// Shows a transient top-center toast after a change (and once on startup when a joystick
    /// is connected) so users can see which controller mapping is live.
    /// </summary>
    public class JoystickProfileSwitcher : MonoBehaviour
    {
        public KeyCode cycleProfileKey = KeyCode.F10;
        public KeyCode flipLinearKey = KeyCode.F11;
        public KeyCode flipSteerKey = KeyCode.F9;
        public float toastDurationSec = 4f;

        private float toastUntil = -1f;
        private string toastText = string.Empty;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (FindObjectOfType<JoystickProfileSwitcher>() != null)
                return;

            var go = new GameObject("JoystickProfileSwitcher");
            DontDestroyOnLoad(go);
            go.AddComponent<JoystickProfileSwitcher>();
        }

        private void Start()
        {
            if (!string.IsNullOrEmpty(JoystickProfiles.DetectedDeviceName))
                ShowToast($"Controller: {JoystickProfiles.Describe()}");
        }

        private void Update()
        {
            if (UnityEngine.Input.GetKeyDown(cycleProfileKey))
            {
                JoystickProfiles.CycleSelectedProfile();
                ShowToast($"Controller profile: {JoystickProfiles.Describe()}");
                Debug.Log($"[JoystickProfileSwitcher] Profile -> {JoystickProfiles.SelectedProfile} ({JoystickProfiles.Describe()})");
            }

            if (UnityEngine.Input.GetKeyDown(flipLinearKey))
            {
                JoystickProfiles.GamepadLinearSign = -JoystickProfiles.GamepadLinearSign;
                ShowToast($"Gamepad forward/back sign: {JoystickProfiles.GamepadLinearSign:+0;-0}");
            }

            if (UnityEngine.Input.GetKeyDown(flipSteerKey))
            {
                JoystickProfiles.GamepadSteerSign = -JoystickProfiles.GamepadSteerSign;
                ShowToast($"Gamepad steering sign: {JoystickProfiles.GamepadSteerSign:+0;-0}");
            }
        }

        private void ShowToast(string text)
        {
            toastText = $"{text}\nF10 switch profile | F11 flip fwd/back | F9 flip steering";
            toastUntil = Time.unscaledTime + toastDurationSec;
        }

        private void OnGUI()
        {
            if (Time.unscaledTime >= toastUntil)
                return;

            const float width = 440f;
            const float height = 44f;
            // Top-center so it never overlaps the session-review scrubber or the
            // driving HUD boxes that dock top-left / bottom.
            var rect = new Rect((Screen.width - width) * 0.5f, 8f, width, height);
            GUI.Box(rect, toastText);
        }
    }
}
