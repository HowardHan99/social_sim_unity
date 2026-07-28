using UnityEngine;

namespace SEAN.Input
{
    /// <summary>
    /// Exposes the ACTIVE gamepad's D-pad as a simple four-way digital input, so it can act
    /// as a second set of WASD keys, plus one shoulder button for the view swap:
    ///
    ///   D-pad Up / Down / Left / Right  ==  W / S / A / D
    ///   RB (right bumper)               ==  Tab -- swap the main view between the
    ///                                       with-avatar (third person) and without-avatar
    ///                                       (first person) camera; the other goes to the mini panel.
    ///
    /// Almost nothing new to learn -- the D-pad does exactly what the keyboard already does.
    /// Unity's legacy Input cannot synthesize key events, so the driving controllers
    /// OR these flags in next to their own GetKey checks.
    ///
    /// Edge (…Pressed) state is computed once per frame here (the component runs early), so
    /// several consumers can read the same press without stealing it from each other.
    /// Only the active gamepad is read, so a second controller (e.g. the flight stick) can
    /// never drive through here.
    /// </summary>
    [DefaultExecutionOrder(-500)]
    public class GamepadHotkeys : MonoBehaviour
    {
        private static GamepadHotkeys instance;

        /// <summary>D-pad deflection that counts as pressed (the axes are digital: -1/0/+1).</summary>
        private const float PressThreshold = 0.5f;

        [Tooltip("Flip if the pad reports D-pad up as negative.")]
        public bool invertDpadY = false;
        [Tooltip("Flip if the pad reports D-pad right as negative.")]
        public bool invertDpadX = false;
        [Tooltip("Gamepad button index that swaps the main view (XInput: 4 = LB, 5 = RB, 0 = A, 1 = B).")]
        public int viewToggleButtonIndex = 5;

        private bool up, down, left, right;
        private bool upEdge, downEdge, leftEdge, rightEdge;
        private bool wasUp, wasDown, wasLeft, wasRight;
        private bool viewToggleEdge;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (instance != null)
                return;

            var go = new GameObject("GamepadHotkeys");
            DontDestroyOnLoad(go);
            go.AddComponent<GamepadHotkeys>();
        }

        public static bool DpadUpHeld => instance != null && instance.up;
        public static bool DpadDownHeld => instance != null && instance.down;
        public static bool DpadLeftHeld => instance != null && instance.left;
        public static bool DpadRightHeld => instance != null && instance.right;

        public static bool DpadUpPressed => instance != null && instance.upEdge;
        public static bool DpadDownPressed => instance != null && instance.downEdge;
        public static bool DpadLeftPressed => instance != null && instance.leftEdge;
        public static bool DpadRightPressed => instance != null && instance.rightEdge;

        /// <summary>RB went down this frame: swap the main view (same action as Tab).</summary>
        public static bool ViewTogglePressed => instance != null && instance.viewToggleEdge;

        private void Awake()
        {
            if (instance != null && instance != this)
            {
                Destroy(gameObject);
                return;
            }
            instance = this;
        }

        private void Update()
        {
            float x = JoystickProfiles.DpadX() * (invertDpadX ? -1f : 1f);
            float y = JoystickProfiles.DpadY() * (invertDpadY ? -1f : 1f);

            bool nowUp = y > PressThreshold;
            bool nowDown = y < -PressThreshold;
            bool nowLeft = x < -PressThreshold;
            bool nowRight = x > PressThreshold;

            upEdge = nowUp && !wasUp;
            downEdge = nowDown && !wasDown;
            leftEdge = nowLeft && !wasLeft;
            rightEdge = nowRight && !wasRight;

            up = nowUp;
            down = nowDown;
            left = nowLeft;
            right = nowRight;

            wasUp = nowUp;
            wasDown = nowDown;
            wasLeft = nowLeft;
            wasRight = nowRight;

            viewToggleEdge = GamepadButtonDown(viewToggleButtonIndex);
        }

        /// <summary>
        /// Reads a button on the ACTIVE gamepad only. Buttons are KeyCodes, not axes, and
        /// the per-device ones are laid out as Joystick1Button0 + (slot-1)*20 + index.
        /// Falls back to the any-joystick KeyCode when the slot is beyond that range.
        /// </summary>
        private static bool GamepadButtonDown(int buttonIndex)
        {
            if (!JoystickProfiles.GamepadActive || buttonIndex < 0 || buttonIndex > 19)
                return false;

            int slot = JoystickProfiles.ActiveJoystickNumber;
            KeyCode code = (slot >= 1 && slot <= 4)
                ? KeyCode.Joystick1Button0 + (slot - 1) * 20 + buttonIndex
                : KeyCode.JoystickButton0 + buttonIndex;

            return UnityEngine.Input.GetKeyDown(code);
        }
    }
}
