using System.Collections.Generic;
using UnityEngine;

namespace SessionReview
{
    /// <summary>
    /// Reusable IMGUI "window chrome" for the session-review overlays. Wrapping a
    /// panel with <see cref="Begin"/>/<see cref="End"/> makes it:
    ///   - draggable by its title bar,
    ///   - resizable via the bottom-right grip,
    ///   - collapsible (title-bar [-]/[+]) and closable (title-bar [x]).
    /// A static registry of every panel that has drawn at least once lets
    /// <see cref="DrawToggleBar"/> render a single row of buttons to re-open any
    /// panel that was closed, so "toggle off" is never a dead end.
    ///
    /// Each panel owns one <see cref="State"/> (a serialized/plain field) and calls:
    ///   if (ReviewPanels.Begin(state, this, "Title", defaultRect, out Rect content)) {
    ///       // draw into `content` (GUI space)
    ///   }
    ///   ReviewPanels.End(state);
    /// every OnGUI, even while hidden (so it stays registered / re-openable).
    /// </summary>
    public static class ReviewPanels
    {
        public const float TitleH = 28f;
        public const float Grip = 18f;
        public const float MinW = 200f;
        public const float MinContentH = 52f;

        public class State
        {
            public Rect rect;
            public bool collapsed;
            public bool hidden;
            public bool inited;
            public Vector2 scroll;
            public string title = "Panel";

            // Responsive text: 1.0 at the panel's default size, growing as the panel is
            // enlarged (and shrinking a little when made smaller). Panels multiply their
            // base font sizes by this so text scales with the panel. Updated each frame.
            public float FontScale = 1f;

            internal float refWidth = 1f;
            internal float refHeight = 1f;
            internal float titleH = TitleH;
            internal MonoBehaviour owner;
            internal int mode;            // 0 none, 1 drag, 2 resize
            internal Vector2 grabMouse;
            internal Rect grabRect;

            // Render decision is snapshotted on the Layout event so that toggling
            // collapsed/hidden mid-frame (title-bar buttons, toggle bar) never changes
            // the IMGUI layout structure between the Layout and Repaint passes.
            internal bool drawChromeThisFrame = true;
            internal bool drawContentThisFrame = true;
        }

        // Base font sizes for the chrome itself (scaled by FontScale, gently capped).
        private const int BaseTitleFont = 20;
        private const int BaseChromeButtonFont = 18;
        public const float MinFontScale = 0.85f;
        public const float MaxFontScale = 3f;

        private static readonly List<State> registry = new List<State>();

        private static GUIStyle boxStyle;
        private static GUIStyle titleStyle;
        private static GUIStyle chromeButtonStyle;
        private static GUIStyle gripStyle;
        private static GUIStyle toggleOnStyle;
        private static GUIStyle toggleOffStyle;
        private static bool stylesBuilt;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            registry.Clear();
            stylesBuilt = false;
        }

        private static Texture2D MakeTex(Color c)
        {
            var t = new Texture2D(1, 1);
            t.SetPixel(0, 0, c);
            t.Apply();
            return t;
        }

        private static void EnsureStyles()
        {
            if (stylesBuilt)
                return;
            stylesBuilt = true;

            boxStyle = new GUIStyle(GUI.skin.box)
            {
                padding = new RectOffset(0, 0, 0, 0),
                border = new RectOffset(0, 0, 0, 0),
                normal = { background = MakeTex(new Color(0.06f, 0.07f, 0.09f, 0.9f)) }
            };
            titleStyle = new GUIStyle(GUI.skin.label)
            {
                fontStyle = FontStyle.Bold,
                fontSize = 16,
                alignment = TextAnchor.MiddleLeft,
                clipping = TextClipping.Clip,
                normal = { textColor = new Color(0.92f, 0.94f, 0.98f) }
            };
            chromeButtonStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = 15,
                fontStyle = FontStyle.Bold,
                padding = new RectOffset(0, 0, 0, 0),
                alignment = TextAnchor.MiddleCenter
            };
            gripStyle = new GUIStyle(GUI.skin.box)
            {
                padding = new RectOffset(0, 0, 0, 0),
                normal =
                {
                    background = MakeTex(new Color(0.55f, 0.6f, 0.7f, 0.85f)),
                    textColor = new Color(0.1f, 0.12f, 0.15f)
                },
                fontSize = 11,
                alignment = TextAnchor.MiddleCenter
            };
            toggleOffStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = 16,
                alignment = TextAnchor.MiddleCenter
            };
            toggleOnStyle = new GUIStyle(toggleOffStyle)
            {
                fontStyle = FontStyle.Bold,
                normal =
                {
                    background = MakeTex(new Color(0.2f, 0.45f, 0.32f, 0.95f)),
                    textColor = Color.white
                },
                hover =
                {
                    background = MakeTex(new Color(0.25f, 0.54f, 0.38f, 0.98f)),
                    textColor = Color.white
                }
            };
        }

        /// <summary>
        /// Draw the panel chrome. Returns true (with <paramref name="content"/> set to
        /// the inner GUI-space rect) when the caller should draw its body; returns false
        /// when the panel is hidden or collapsed. Always call <see cref="End"/> afterwards.
        /// </summary>
        public static bool Begin(State s, MonoBehaviour owner, string title, Rect defaultRect, out Rect content)
        {
            EnsureStyles();

            s.title = title;
            s.owner = owner;
            if (!registry.Contains(s))
                registry.Add(s);

            if (!s.inited)
            {
                s.rect = defaultRect;
                s.refWidth = Mathf.Max(1f, defaultRect.width);
                s.refHeight = Mathf.Max(1f, defaultRect.height);
                s.inited = true;
            }

            if (Event.current != null && Event.current.type == EventType.Layout)
            {
                s.drawChromeThisFrame = !s.hidden;
                s.drawContentThisFrame = !s.hidden && !s.collapsed;
            }

            content = default;
            if (!s.drawChromeThisFrame)
                return false;

            HandleEvents(s);
            ClampToScreen(s);

            // Responsive text: scale with how large the panel is relative to its default,
            // driven by whichever axis grew least so tall text never overflows a short panel.
            s.FontScale = Mathf.Clamp(
                Mathf.Min(s.rect.width / s.refWidth, s.rect.height / s.refHeight),
                MinFontScale, MaxFontScale);

            // The chrome grows more gently than the body so the title bar stays compact.
            float chromeScale = Mathf.Clamp(s.FontScale, 1f, 1.9f);
            titleStyle.fontSize = Mathf.RoundToInt(BaseTitleFont * chromeScale);
            chromeButtonStyle.fontSize = Mathf.RoundToInt(BaseChromeButtonFont * chromeScale);
            s.titleH = Mathf.Max(TitleH, titleStyle.fontSize + 12f);
            float titleH = s.titleH;
            float btn = titleH - 8f;

            float fullH = s.drawContentThisFrame ? s.rect.height : titleH;
            Rect full = new Rect(s.rect.x, s.rect.y, s.rect.width, fullH);

            GUI.Box(full, GUIContent.none, boxStyle);

            if (GUI.Button(new Rect(full.x + 5f, full.y + 4f, btn, btn), s.collapsed ? "+" : "-", chromeButtonStyle))
                s.collapsed = !s.collapsed;
            GUI.Label(new Rect(full.x + btn + 12f, full.y + 4f, full.width - btn * 2f - 24f, btn), title, titleStyle);
            if (GUI.Button(new Rect(full.xMax - btn - 5f, full.y + 4f, btn, btn), "x", chromeButtonStyle))
                s.hidden = true;

            if (!s.drawContentThisFrame)
                return false;

            content = new Rect(full.x + 8f, full.y + titleH + 2f, full.width - 16f, full.height - titleH - 12f);
            return true;
        }

        public static void End(State s)
        {
            if (!s.drawContentThisFrame)
                return;

            Rect grip = new Rect(s.rect.xMax - Grip, s.rect.yMax - Grip, Grip, Grip);
            GUI.Box(grip, "//", gripStyle);
        }

        /// <summary>
        /// True when the given GUI-space point is over any visible (non-hidden) panel's
        /// footprint (the full rect, or just the title bar when collapsed). Lets input
        /// handlers such as the top-down scene zoom ignore scroll/clicks over a panel so
        /// scrolling a panel doesn't also zoom the scene behind it.
        /// </summary>
        public static bool AnyPanelContains(Vector2 guiPoint)
        {
            for (int i = 0; i < registry.Count; i++)
            {
                State s = registry[i];
                if (s == null || s.owner == null || s.hidden)
                    continue;

                float h = s.collapsed ? s.titleH : s.rect.height;
                Rect occupied = new Rect(s.rect.x, s.rect.y, s.rect.width, h);
                if (occupied.Contains(guiPoint))
                    return true;
            }
            return false;
        }

        private static void HandleEvents(State s)
        {
            Event e = Event.current;
            if (e == null)
                return;

            // Title-bar drag region excludes the [-]/[x] hit areas so those buttons work.
            float sideInset = s.titleH + 8f;
            Rect titleDrag = new Rect(s.rect.x + sideInset, s.rect.y, s.rect.width - sideInset * 2f, s.titleH);
            Rect grip = new Rect(s.rect.xMax - Grip, s.rect.yMax - Grip, Grip, Grip);

            switch (e.type)
            {
                case EventType.MouseDown:
                    if (!s.collapsed && grip.Contains(e.mousePosition))
                    {
                        s.mode = 2;
                        s.grabMouse = e.mousePosition;
                        s.grabRect = s.rect;
                        e.Use();
                    }
                    else if (titleDrag.Contains(e.mousePosition))
                    {
                        s.mode = 1;
                        s.grabMouse = e.mousePosition;
                        s.grabRect = s.rect;
                        e.Use();
                    }
                    break;

                case EventType.MouseDrag:
                    if (s.mode == 1)
                    {
                        s.rect.position = s.grabRect.position + (e.mousePosition - s.grabMouse);
                        e.Use();
                    }
                    else if (s.mode == 2)
                    {
                        Vector2 d = e.mousePosition - s.grabMouse;
                        s.rect.width = Mathf.Max(MinW, s.grabRect.width + d.x);
                        s.rect.height = Mathf.Max(TitleH + MinContentH, s.grabRect.height + d.y);
                        e.Use();
                    }
                    break;

                case EventType.MouseUp:
                    if (s.mode != 0)
                    {
                        s.mode = 0;
                        e.Use();
                    }
                    break;
            }
        }

        private static void ClampToScreen(State s)
        {
            s.rect.width = Mathf.Clamp(s.rect.width, MinW, Mathf.Max(MinW, Screen.width));
            s.rect.height = Mathf.Clamp(s.rect.height, TitleH + MinContentH, Mathf.Max(TitleH + MinContentH, Screen.height));
            // Keep at least a corner of the title bar on-screen so it can always be grabbed.
            s.rect.x = Mathf.Clamp(s.rect.x, -s.rect.width + 48f, Screen.width - 48f);
            s.rect.y = Mathf.Clamp(s.rect.y, 0f, Screen.height - TitleH);
        }

        /// <summary>
        /// Compact row of buttons (one per registered panel) that toggles each panel
        /// open/closed. Green = visible, grey = hidden. Call once per OnGUI from a
        /// host that is always active during review.
        /// </summary>
        public static void DrawToggleBar()
        {
            EnsureStyles();

            registry.RemoveAll(s => s == null || s.owner == null);
            if (registry.Count == 0)
                return;

            const float bw = 134f;
            const float bh = 30f;
            const float gap = 6f;
            const float pad = 6f;
            float totalW = registry.Count * (bw + gap) - gap + pad * 2f;
            float x = Mathf.Max(4f, (Screen.width - totalW) * 0.5f);
            float y = 4f;

            GUI.Box(new Rect(x, y, totalW, bh + pad * 2f), GUIContent.none, boxStyle);

            float bx = x + pad;
            for (int i = 0; i < registry.Count; i++)
            {
                State s = registry[i];
                GUIStyle style = s.hidden ? toggleOffStyle : toggleOnStyle;
                if (GUI.Button(new Rect(bx, y + pad, bw, bh), s.title, style))
                {
                    s.hidden = !s.hidden;
                    if (!s.hidden)
                        s.collapsed = false;
                }
                bx += bw + gap;
            }
        }
    }
}
