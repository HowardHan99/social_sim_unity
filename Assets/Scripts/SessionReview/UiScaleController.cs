using UnityEngine;

namespace SessionReview
{
    /// <summary>
    /// Global user-controlled zoom for the session IMGUI overlays so text stays readable
    /// on large displays (projectors, wall screens) without giving up the existing
    /// responsive layout.
    ///
    /// Overlays opt in by calling <see cref="Apply"/> first thing in OnGUI (scales fonts
    /// and layout uniformly through GUI.matrix) and laying out against <see cref="Width"/>/
    /// <see cref="Height"/> — the "virtual screen" size in GUI units — instead of
    /// Screen.width/height, so responsive clamps and edge-docking keep working at any zoom.
    ///
    /// IMGUI hit-testing follows GUI.matrix automatically (Event.current.mousePosition is
    /// already in scaled GUI space). Only code that builds GUI-space points by hand from
    /// Input.mousePosition / Touch.position / Camera.WorldToScreenPoint must convert
    /// through <see cref="ScreenToGui"/>.
    /// </summary>
    public static class ReviewUiScale
    {
        public const float Min = 0.5f;
        public const float Max = 2.5f;
        private const string PrefKey = "SessionReview.UiScale";

        private static float userScale = -1f;

        /// <summary>User zoom factor (1 = default), persisted across sessions.</summary>
        public static float Value
        {
            get
            {
                if (userScale <= 0f)
                    userScale = Mathf.Clamp(PlayerPrefs.GetFloat(PrefKey, 1f), Min, Max);
                return userScale;
            }
            set
            {
                float clamped = Mathf.Clamp(value, Min, Max);
                if (Mathf.Approximately(clamped, userScale))
                    return;
                userScale = clamped;
                PlayerPrefs.SetFloat(PrefKey, clamped);
            }
        }

        /// <summary>Virtual screen width in GUI units; use instead of Screen.width in scaled OnGUI code.</summary>
        public static float Width => Screen.width / Value;

        /// <summary>Virtual screen height in GUI units; use instead of Screen.height in scaled OnGUI code.</summary>
        public static float Height => Screen.height / Value;

        /// <summary>Call first thing in OnGUI so the whole overlay (fonts + layout) zooms uniformly.</summary>
        public static void Apply()
        {
            float v = Value;
            GUI.matrix = Matrix4x4.Scale(new Vector3(v, v, 1f));
        }

        /// <summary>Screen point (Input/Touch/WorldToScreenPoint, origin bottom-left, real pixels) to scaled GUI point.</summary>
        public static Vector2 ScreenToGui(Vector2 screenPos)
        {
            float v = Value;
            return new Vector2(screenPos.x / v, (Screen.height - screenPos.y) / v);
        }

        /// <summary>Current mouse position as a scaled GUI point (top-left origin).</summary>
        public static Vector2 GuiMousePosition()
        {
            return ScreenToGui(Input.mousePosition);
        }
    }

    /// <summary>
    /// On-screen controller for <see cref="ReviewUiScale"/>: a small "Aa" badge in the
    /// top-left corner showing the current zoom; clicking it expands a strip with a
    /// slider, +/- steppers and presets. Hotkeys work anywhere: Ctrl +/- zooms,
    /// Ctrl 0 resets (Cmd on macOS).
    ///
    /// Self-bootstraps at runtime so no scene wiring is needed (duplicates destroy
    /// themselves, mirroring AgentSpeedOverlay).
    /// </summary>
    public class UiScaleController : MonoBehaviour
    {
        private static UiScaleController instance;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (instance != null) return;
            var go = new GameObject("UiScaleController");
            go.AddComponent<UiScaleController>();
            DontDestroyOnLoad(go);
        }

        private const float Step = 0.1f;

        private bool expanded;
        private GUIStyle badgeStyle;
        private GUIStyle stepperStyle;
        private GUIStyle presetStyle;
        private GUIStyle presetActiveStyle;
        private GUIStyle valueStyle;
        private GUIStyle hintStyle;
        private GUIStyle stripStyle;

        // Footprint of the whole control for the frame it was last drawn, so scene-input
        // handlers (top-down zoom/pan, draw strokes) can ignore pointer activity over it.
        private static Rect controlRect;
        private static int controlFrame = -1;

        /// <summary>True when the scaled-GUI-space point is over the zoom control.</summary>
        public static bool ControlContains(Vector2 guiPoint)
        {
            return Time.frameCount - controlFrame <= 1 && controlRect.Contains(guiPoint);
        }

        void Awake()
        {
            if (instance != null && instance != this)
            {
                Destroy(gameObject);
                return;
            }
            instance = this;
        }

        void Update()
        {
            bool modifier = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl) ||
                            Input.GetKey(KeyCode.LeftCommand) || Input.GetKey(KeyCode.RightCommand);
            if (!modifier) return;

            if (Input.GetKeyDown(KeyCode.Equals) || Input.GetKeyDown(KeyCode.Plus) || Input.GetKeyDown(KeyCode.KeypadPlus))
                ReviewUiScale.Value += Step;
            else if (Input.GetKeyDown(KeyCode.Minus) || Input.GetKeyDown(KeyCode.KeypadMinus))
                ReviewUiScale.Value -= Step;
            else if (Input.GetKeyDown(KeyCode.Alpha0) || Input.GetKeyDown(KeyCode.Keypad0))
                ReviewUiScale.Value = 1f;
        }

        private void EnsureStyles()
        {
            if (badgeStyle != null)
                return;

            badgeStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = 14,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter
            };
            stepperStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = 16,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                padding = new RectOffset(0, 0, 0, 0)
            };
            presetStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = 12,
                alignment = TextAnchor.MiddleCenter
            };
            presetActiveStyle = new GUIStyle(presetStyle)
            {
                fontStyle = FontStyle.Bold,
                normal = { textColor = new Color(0.55f, 1f, 0.7f) },
                hover = { textColor = new Color(0.55f, 1f, 0.7f) }
            };
            valueStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 14,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter
            };
            hintStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 11,
                alignment = TextAnchor.MiddleLeft,
                normal = { textColor = new Color(0.75f, 0.78f, 0.84f) }
            };
            stripStyle = new GUIStyle(GUI.skin.box)
            {
                padding = new RectOffset(0, 0, 0, 0)
            };
        }

        void OnGUI()
        {
            ReviewUiScale.Apply();
            EnsureStyles();

            const float x = 8f;
            const float y = 8f;
            const float badgeW = 88f;
            const float badgeH = 30f;

            Rect badgeRect = new Rect(x, y, badgeW, badgeH);
            int percent = Mathf.RoundToInt(ReviewUiScale.Value * 100f);
            if (GUI.Button(badgeRect, $"Aa {percent}%", badgeStyle))
                expanded = !expanded;

            Rect footprint = badgeRect;

            if (expanded)
            {
                const float pad = 10f;
                const float rowH = 24f;
                const float stepperW = 28f;
                const float sliderW = 240f;
                const float valueW = 52f;
                const float presetW = 58f;
                const float gap = 6f;

                float stripW = pad * 2f + stepperW + gap + sliderW + gap + stepperW + gap + valueW;
                float stripH = pad * 2f + rowH * 2f + 6f;
                Rect strip = new Rect(badgeRect.xMax + 8f, y, stripW, stripH);
                GUI.Box(strip, GUIContent.none, stripStyle);

                float cx = strip.x + pad;
                float cy = strip.y + pad;

                if (GUI.Button(new Rect(cx, cy, stepperW, rowH), "-", stepperStyle))
                    ReviewUiScale.Value -= Step;
                cx += stepperW + gap;

                float slid = GUI.HorizontalSlider(
                    new Rect(cx, cy + rowH * 0.5f - 6f, sliderW, 14f),
                    ReviewUiScale.Value, ReviewUiScale.Min, ReviewUiScale.Max);
                // Snap to 5% so dragging lands on tidy values.
                slid = Mathf.Round(slid * 20f) / 20f;
                if (!Mathf.Approximately(slid, ReviewUiScale.Value))
                    ReviewUiScale.Value = slid;
                cx += sliderW + gap;

                if (GUI.Button(new Rect(cx, cy, stepperW, rowH), "+", stepperStyle))
                    ReviewUiScale.Value += Step;
                cx += stepperW + gap;

                GUI.Label(new Rect(cx, cy, valueW, rowH), $"{percent}%", valueStyle);

                // Preset row + hotkey hint.
                cy += rowH + 6f;
                cx = strip.x + pad;
                float[] presets = { 1f, 1.25f, 1.5f, 2f };
                foreach (float preset in presets)
                {
                    bool active = Mathf.Abs(ReviewUiScale.Value - preset) < 0.011f;
                    if (GUI.Button(new Rect(cx, cy, presetW, rowH),
                        $"{Mathf.RoundToInt(preset * 100f)}%",
                        active ? presetActiveStyle : presetStyle))
                        ReviewUiScale.Value = preset;
                    cx += presetW + gap;
                }
                GUI.Label(new Rect(cx, cy, strip.xMax - cx - pad, rowH), "Ctrl +/-  Ctrl 0", hintStyle);

                footprint = new Rect(
                    badgeRect.x, badgeRect.y,
                    strip.xMax - badgeRect.x, Mathf.Max(badgeRect.height, strip.height));
            }

            controlRect = footprint;
            controlFrame = Time.frameCount;
        }
    }
}
