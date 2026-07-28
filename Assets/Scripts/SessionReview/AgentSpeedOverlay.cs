using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using IVI;
using SEAN.Control;

namespace SessionReview
{
    /// <summary>
    /// Live agent-speed control panel, rendered on the secondary display (Display 2) so the
    /// operator can retune each agent's speed without the controls showing up on the
    /// participant's main view. Lists the robot and each PWD/human pedestrian with their
    /// current speed and gives every agent its own slider that sets that agent's target
    /// speed in absolute m/s (converted internally to each controller's speed multiplier)
    /// live so different scenarios can be set up from the start.
    ///
    /// F8 toggles it (shared with the status badge via <see cref="HudVisible"/>); Ctrl+F8
    /// moves it between Display 1 and Display 2. Available from the trial-start prompt
    /// onward so speeds can be staged before the participant starts; hidden during review,
    /// world-building and onboarding.
    ///
    /// Editor note: like ResearcherDisplay, the Editor never reports a second display, so
    /// the panel still targets Display 2 there and only shows in a second Game view window
    /// set to "Display 2" — press Ctrl+F8 to pull it onto the main Game view instead.
    ///
    /// Uses uGUI (a Canvas with <c>targetDisplay</c>) rather than IMGUI because Unity's
    /// OnGUI can only render on Display 1 — there is no OnGUI equivalent of
    /// Canvas.targetDisplay. Self-bootstraps at runtime so no scene wiring is needed and
    /// ensures an EventSystem exists so the sliders are clickable on the second display.
    /// </summary>
    public class AgentSpeedOverlay : MonoBehaviour
    {
        private static AgentSpeedOverlay instance;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (instance != null) return;
            var go = new GameObject("AgentSpeedOverlay");
            go.AddComponent<AgentSpeedOverlay>();
            DontDestroyOnLoad(go);
        }

        public KeyCode toggleKey = KeyCode.F8;

        [Tooltip("Display the panel renders on. 0 = main display, 1 = Display 2 (default).")]
        public int targetDisplay = 1;

        /// <summary>
        /// Shared visibility for the live-trial HUD (this speed panel plus the
        /// SessionReviewManager status badge). Hidden by default; F8 toggles it.
        /// </summary>
        public static bool HudVisible => instance != null && instance.visible;

        [Tooltip("Lowest target speed (m/s) the sliders allow.")]
        public float minSpeed = 0.1f;
        [Tooltip("Highest target speed (m/s) the sliders allow.")]
        public float maxSpeed = 2.0f;

        [Header("Keyboard control (works on any display, no mouse needed)")]
        [Tooltip("Cycles which agent (highlighted row) the speed keys affect.")]
        public KeyCode selectAgentKey = KeyCode.Backslash;
        [Tooltip("Slows the selected agent down by one step.")]
        public KeyCode speedDownKey = KeyCode.Comma;
        [Tooltip("Speeds the selected agent up by one step.")]
        public KeyCode speedUpKey = KeyCode.Period;
        [Tooltip("Target-speed change (m/s) per key press.")]
        public float speedStep = 0.1f;

        private const float RescanInterval = 1.0f;
        private const float SpeedSmoothing = 6.0f;

        // Layout, in the canvas' reference-resolution units.
        private const float Margin = 24f;   // gap from the display edges
        private const float Pad = 16f;      // inner padding
        private const float Gap = 14f;      // gap between columns
        private const float RowH = 42f;
        private const float HeaderH = 34f;
        private const float HintH = 24f;
        private const float LabelW = 210f;  // "Robot: 0.00 m/s"
        private const float SliderW = 240f;
        private const float ScaleW = 100f;  // "0.80 m/s"
        private const float SliderH = 26f;
        private const float PanelW = Pad * 2f + LabelW + Gap + SliderW + Gap + ScaleW;

        private List<Tracked> tracked = new List<Tracked>();
        private readonly List<GameObject> rowObjects = new List<GameObject>();
        private float nextRescanTime;
        private bool visible = false;
        private int selected;   // row the speed keys act on

        private static readonly Color SelectedColor = new Color(0.55f, 1f, 0.7f);

        private Canvas canvas;
        private RectTransform panel;
        private Text titleText;
        private Text hintText;
        private Font uiFont;

        private class Tracked
        {
            public string label;
            public Transform tform;
            public VelocityController robot;              // set for the robot
            public SFPWDAgent pwdAgent;                   // set for a PWD (auto speed)
            public ManualWheelchairController pwdManual;  // set for a PWD (manual speed)
            public Vector3 lastPos;
            public float speed;   // smoothed planar speed (m/s)
            public float target;  // slider value (target speed, m/s)

            public Text nameText;   // "<label>: 0.00 m/s"
            public Text valueText;  // "0.80 m/s" (target)
            public Slider slider;
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
            EnsureUi();

            if (Input.GetKeyDown(toggleKey))
            {
                bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl) ||
                            Input.GetKey(KeyCode.LeftCommand) || Input.GetKey(KeyCode.RightCommand);
                if (ctrl)
                {
                    // Ctrl+F8: pull the panel onto the other display (and show it) — the
                    // escape hatch when nothing is previewing or connected as Display 2.
                    // Ctrl rather than Shift so it can't trip the Shift manual-control
                    // toggles while the panel is still hidden.
                    targetDisplay = targetDisplay == 0 ? 1 : 0;
                    visible = true;
                }
                else
                {
                    visible = !visible;
                }
            }

            // Keep following display availability in case Display 2 is activated late.
            int desired = ResolveTargetDisplay();
            if (canvas.targetDisplay != desired) canvas.targetDisplay = desired;

            HandleSpeedHotkeys();

            if (Time.unscaledTime >= nextRescanTime || AnyTrackedMissing())
            {
                Rescan();
                nextRescanTime = Time.unscaledTime + RescanInterval;
            }

            float dt = Time.deltaTime;
            if (dt > 1e-5f)
            {
                float lerp = 1f - Mathf.Exp(-SpeedSmoothing * dt);
                foreach (var t in tracked)
                {
                    if (t.tform == null) continue;
                    Vector3 p = t.tform.position;
                    Vector3 delta = p - t.lastPos;
                    delta.y = 0f;
                    t.speed = Mathf.Lerp(t.speed, delta.magnitude / dt, lerp);
                    t.lastPos = p;
                }
            }

            RefreshUi();
        }

        private bool AnyTrackedMissing()
        {
            if (tracked.Count == 0) return true;
            foreach (var t in tracked)
                if (t.tform == null) return true;
            return false;
        }

        // Keyboard control of agent speed. Works regardless of which display the panel is on
        // (and needs no mouse), which is the whole point on the second monitor. Only live
        // while the panel is actually shown, so it never surprises the participant.
        // Shift combos: Shift+Minus / Shift+Equals(+) step the selected agent, Shift+0 resets
        // it to the agent's default speed. Ctrl+-/= is taken by UiScaleController; while this
        // panel is up VelocityController skips its bare-LeftShift manual-control toggle.
        private void HandleSpeedHotkeys()
        {
            if (!visible || ShouldHide() || tracked.Count == 0) return;

            selected = Mathf.Clamp(selected, 0, tracked.Count - 1);

            if (Input.GetKeyDown(selectAgentKey))
                selected = (selected + 1) % tracked.Count;

            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            var t = tracked[selected];

            if (shift && (Input.GetKeyDown(KeyCode.Alpha0) || Input.GetKeyDown(KeyCode.Keypad0)))
            {
                t.target = DefaultSpeed(t);
                ApplyTarget(t);
                if (t.slider != null) t.slider.SetValueWithoutNotify(t.target);
                return;
            }

            float dir = 0f;
            if (Input.GetKeyDown(speedUpKey)) dir += 1f;
            if (Input.GetKeyDown(speedDownKey)) dir -= 1f;
            if (shift && (Input.GetKeyDown(KeyCode.Equals) || Input.GetKeyDown(KeyCode.KeypadPlus))) dir += 1f;
            if (shift && (Input.GetKeyDown(KeyCode.Minus) || Input.GetKeyDown(KeyCode.KeypadMinus))) dir -= 1f;
            if (dir != 0f)
            {
                t.target = Mathf.Clamp(t.target + dir * speedStep, minSpeed, maxSpeed);
                ApplyTarget(t);
                if (t.slider != null) t.slider.SetValueWithoutNotify(t.target);
            }
        }

        // ---- Data --------------------------------------------------------------

        private List<Tracked> BuildTrackedList()
        {
            // Carry slider values across rescans (and scene reloads) by transform.
            var priorTarget = new Dictionary<Transform, float>();
            foreach (var t in tracked)
                if (t.tform != null) priorTarget[t.tform] = t.target;

            var list = new List<Tracked>();

            var robot = FindObjectOfType<VelocityController>();
            if (robot != null)
            {
                // base_link is the moving rigidbody transform; fall back to the
                // controller's own transform if the robot rig isn't resolved yet.
                var sean = SEAN.SEAN.instance;
                Transform robotTform = (sean != null && sean.robot != null && sean.robot.base_link != null)
                    ? sean.robot.base_link.transform
                    : robot.transform;
                var tr = new Tracked
                {
                    label = "Robot",
                    tform = robotTform,
                    robot = robot,
                    lastPos = robotTform.position,
                    target = priorTarget.TryGetValue(robotTform, out var s)
                        ? s
                        : robot.speedScale * RobotBaseSpeed(robot)
                };
                ApplyTarget(tr);
                list.Add(tr);
            }

            var pwds = FindObjectsOfType<ManualWheelchairController>();
            // Stable order so the rows (and any in-progress slider drag) don't shuffle
            // between rescans.
            System.Array.Sort(pwds, (a, b) => a.GetInstanceID().CompareTo(b.GetInstanceID()));
            foreach (var pwd in pwds)
            {
                if (pwd == null) continue;
                var agent = pwd.GetComponent<SFPWDAgent>();
                float init = priorTarget.TryGetValue(pwd.transform, out var s)
                    ? s
                    : (agent != null
                        ? agent.autoSpeedScale * Parameters.DESIRED_SPEED
                        : pwd.speedScale * ManualBaseSpeed(pwd));
                var tr = new Tracked
                {
                    label = pwds.Length > 1 ? pwd.gameObject.name : "Human",
                    tform = pwd.transform,
                    pwdAgent = agent,
                    pwdManual = pwd,
                    lastPos = pwd.transform.position,
                    target = init
                };
                ApplyTarget(tr);
                list.Add(tr);
            }

            return list;
        }

        private void Rescan()
        {
            var newList = BuildTrackedList();
            // Only rebuild the UI when the set of agents actually changed, so a slider the
            // operator is dragging isn't destroyed out from under them every second.
            if (SameComposition(newList, tracked)) return;
            tracked = newList;
            RebuildRows();
        }

        private static bool SameComposition(List<Tracked> a, List<Tracked> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (a[i].tform != b[i].tform) return false;
            return true;
        }

        // The slider is an absolute target speed (m/s); each controller still exposes a
        // multiplier, so convert against that controller's own base speed. A PWD row gets
        // its target applied to BOTH modes: auto walking (social-force DESIRED_SPEED) and
        // manual driving (moveSpeed), so the pedestrian moves at the set speed either way.
        // For the robot the base is maxLinearCommand — the commanded-velocity cap — so the
        // slider reads as the robot's cruise/max speed.
        private static float RobotBaseSpeed(VelocityController robot)
            => Mathf.Max(0.05f, robot.maxLinearCommand);

        private static float ManualBaseSpeed(ManualWheelchairController pwd)
            => Mathf.Max(0.05f, pwd.moveSpeed);

        // Speed an agent returns to on Shift+0: its controller's unscaled default.
        private static float DefaultSpeed(Tracked t)
        {
            if (t.robot != null) return RobotBaseSpeed(t.robot);
            if (t.pwdAgent != null) return Parameters.DESIRED_SPEED;
            if (t.pwdManual != null) return ManualBaseSpeed(t.pwdManual);
            return 1f;
        }

        private void ApplyTarget(Tracked t)
        {
            t.target = Mathf.Clamp(t.target, minSpeed, maxSpeed);
            if (t.robot != null) t.robot.speedScale = t.target / RobotBaseSpeed(t.robot);
            if (t.pwdAgent != null) t.pwdAgent.autoSpeedScale = t.target / Parameters.DESIRED_SPEED;
            if (t.pwdManual != null) t.pwdManual.speedScale = t.target / ManualBaseSpeed(t.pwdManual);
        }

        private bool ShouldHide()
        {
            // Deliberately not gated on BlocksAutomaticTrialStart: the panel must be usable
            // on the trial-start prompt (and during warmup) so speeds can be staged before
            // the trial begins. The Display-1 status badge hides itself there separately.
            var srm = SessionReviewManager.Instance;
            return srm != null && (srm.IsReviewModeActive
                                   || srm.IsWorldBuildingModeActive
                                   || srm.IsOnboardingActive);
        }

        // ---- UI ----------------------------------------------------------------

        private void EnsureUi()
        {
            if (canvas != null) return;

            EnsureEventSystem();
            uiFont = LoadUiFont();

            var canvasGo = new GameObject("AgentSpeedCanvas",
                typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGo.transform.SetParent(transform, false);

            canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.targetDisplay = ResolveTargetDisplay();   // Display 2, or Display 1 if it's absent
            canvas.sortingOrder = 500;

            Debug.Log($"[AgentSpeedOverlay] {Display.displays.Length} display(s) connected; " +
                      $"speed panel on Display {canvas.targetDisplay + 1}. {toggleKey} shows/hides it, " +
                      $"Ctrl+{toggleKey} moves it between displays; Shift+-/= steps the selected " +
                      "agent's target speed (m/s), Shift+0 resets it to the agent's default. " +
                      "In the Editor it appears in a Game view " +
                      $"set to \"Display 2\" — press Ctrl+{toggleKey} to pull it onto the main view.");

            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            // Panel docked to the bottom-right corner of Display 2.
            panel = NewRect("Panel", canvasGo.transform);
            panel.anchorMin = panel.anchorMax = new Vector2(1f, 0f);
            panel.pivot = new Vector2(1f, 0f);
            panel.anchoredPosition = new Vector2(-Margin, Margin);
            panel.sizeDelta = new Vector2(PanelW, Pad * 2f + HeaderH + HintH);
            var panelImg = panel.gameObject.AddComponent<Image>();
            panelImg.color = new Color(0f, 0f, 0f, 0.8f);

            titleText = NewText(panel, "Title", 22, TextAnchor.MiddleLeft, FontStyle.Bold);
            PlaceTopLeft(titleText.rectTransform, Pad, Pad, PanelW - 2f * Pad, HeaderH);
            titleText.text = "Agent Speed";

            hintText = NewText(panel, "Hint", 15, TextAnchor.MiddleLeft, FontStyle.Normal);
            PlaceTopLeft(hintText.rectTransform, Pad, Pad + HeaderH, PanelW - 2f * Pad, HintH);
            hintText.color = new Color(1f, 1f, 1f, 0.75f);
        }

        private void RebuildRows()
        {
            foreach (var go in rowObjects)
                if (go != null) Destroy(go);
            rowObjects.Clear();

            panel.sizeDelta = new Vector2(PanelW, Pad * 2f + HeaderH + HintH + tracked.Count * RowH);
            titleText.text = $"Agent Speed   [{toggleKey}] hide · [Ctrl+{toggleKey}] display";
            hintText.text = $"[{KeyLabel(selectAgentKey)}] pick agent  ·  " +
                            $"[{KeyLabel(speedDownKey)}] / Shift+- slower  ·  " +
                            $"[{KeyLabel(speedUpKey)}] / Shift+= faster  ·  Shift+0 reset";

            for (int i = 0; i < tracked.Count; i++)
            {
                var t = tracked[i];
                float y = Pad + HeaderH + HintH + i * RowH;

                var name = NewText(panel, "Name", 20, TextAnchor.MiddleLeft, FontStyle.Normal);
                PlaceTopLeft(name.rectTransform, Pad, y, LabelW, RowH);
                rowObjects.Add(name.gameObject);
                t.nameText = name;

                var slider = NewSlider(panel);
                PlaceTopLeft((RectTransform)slider.transform,
                    Pad + LabelW + Gap, y + (RowH - SliderH) * 0.5f, SliderW, SliderH);
                slider.minValue = minSpeed;
                slider.maxValue = maxSpeed;
                slider.SetValueWithoutNotify(t.target);
                var captured = t;
                slider.onValueChanged.AddListener(v =>
                {
                    captured.target = v;
                    ApplyTarget(captured);
                });
                rowObjects.Add(slider.gameObject);
                t.slider = slider;

                var value = NewText(panel, "Value", 20, TextAnchor.MiddleRight, FontStyle.Bold);
                PlaceTopLeft(value.rectTransform,
                    Pad + LabelW + Gap + SliderW + Gap, y, ScaleW, RowH);
                rowObjects.Add(value.gameObject);
                t.valueText = value;
            }
        }

        private void RefreshUi()
        {
            bool show = visible && !ShouldHide() && tracked.Count > 0;
            if (canvas.gameObject.activeSelf != show)
                canvas.gameObject.SetActive(show);
            if (!show) return;

            selected = Mathf.Clamp(selected, 0, tracked.Count - 1);
            for (int i = 0; i < tracked.Count; i++)
            {
                var t = tracked[i];
                bool sel = i == selected;
                if (t.nameText != null)
                {
                    t.nameText.text = (sel ? "> " : "") + $"{t.label}: {t.speed:F2} m/s";
                    t.nameText.color = sel ? SelectedColor : Color.white;
                }
                if (t.valueText != null)
                {
                    t.valueText.text = $"{t.target:F2} m/s";
                    t.valueText.color = sel ? SelectedColor : Color.white;
                }
            }
        }

        // In a standalone build, falls back to Display 1 (index 0) when the requested
        // display isn't connected, so the panel is never invisible. In the Editor,
        // Display.displays.Length is ALWAYS 1 even with a second Game view open, so no
        // fallback there: keep targeting Display 2 and let a Game view window set to
        // "Display 2" show it (the ResearcherDisplay convention). Ctrl+F8 pulls the
        // panel onto the main view when no such Game view exists.
        private int ResolveTargetDisplay()
        {
#if UNITY_EDITOR
            return targetDisplay;
#else
            if (targetDisplay > 0 && Display.displays.Length <= targetDisplay)
                return 0;
            return targetDisplay;
#endif
        }

        // Friendly glyph for the punctuation keys so the header reads "[,] slower [.] faster"
        // instead of "[Comma] slower [Period] faster".
        private static string KeyLabel(KeyCode k)
        {
            switch (k)
            {
                case KeyCode.Comma: return ",";
                case KeyCode.Period: return ".";
                case KeyCode.Backslash: return "\\";
                case KeyCode.Slash: return "/";
                default: return k.ToString();
            }
        }

        // ---- uGUI construction helpers -----------------------------------------

        private static void EnsureEventSystem()
        {
            if (EventSystem.current != null) return;
            if (FindObjectOfType<EventSystem>() != null) return;
            var es = new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
            DontDestroyOnLoad(es);
        }

        private static Font LoadUiFont()
        {
            Font f = Resources.GetBuiltinResource<Font>("Arial.ttf");
            if (f == null) f = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (f == null) f = Font.CreateDynamicFontFromOSFont("Arial", 14);
            return f;
        }

        private static RectTransform NewRect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        // Places a rect against the panel's top-left corner: (x, y) is the offset from that
        // corner with y growing downward, matching the old IMGUI layout math.
        private static void PlaceTopLeft(RectTransform rt, float x, float y, float w, float h)
        {
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(x, -y);
            rt.sizeDelta = new Vector2(w, h);
        }

        private Text NewText(Transform parent, string name, int fontSize, TextAnchor anchor, FontStyle style)
        {
            var rt = NewRect(name, parent);
            var t = rt.gameObject.AddComponent<Text>();
            t.font = uiFont;
            t.fontSize = fontSize;
            t.fontStyle = style;
            t.alignment = anchor;
            t.color = Color.white;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            t.raycastTarget = false;   // never steal drags from the sliders
            return t;
        }

        // Builds a standard Unity slider hierarchy (Background / Fill Area>Fill /
        // Handle Slide Area>Handle) by hand, since the project has no UI prefabs.
        private Slider NewSlider(Transform parent)
        {
            var rt = NewRect("Slider", parent);
            var slider = rt.gameObject.AddComponent<Slider>();

            var bg = NewRect("Background", rt);
            Stretch(bg, new Vector2(0f, 0.3f), new Vector2(1f, 0.7f));
            bg.gameObject.AddComponent<Image>().color = new Color(0.25f, 0.25f, 0.25f, 1f);

            var fillArea = NewRect("Fill Area", rt);
            Stretch(fillArea, new Vector2(0f, 0.3f), new Vector2(1f, 0.7f));
            fillArea.offsetMin = new Vector2(8f, 0f);
            fillArea.offsetMax = new Vector2(-8f, 0f);
            var fill = NewRect("Fill", fillArea);
            Stretch(fill, Vector2.zero, Vector2.one);  // Slider drives the anchors from value
            var fillImg = fill.gameObject.AddComponent<Image>();
            fillImg.color = new Color(0.35f, 0.7f, 1f, 1f);

            var handleArea = NewRect("Handle Slide Area", rt);
            Stretch(handleArea, Vector2.zero, Vector2.one);
            handleArea.offsetMin = new Vector2(8f, 0f);
            handleArea.offsetMax = new Vector2(-8f, 0f);
            var handle = NewRect("Handle", handleArea);
            handle.sizeDelta = new Vector2(16f, 0f);   // Slider drives the anchors from value
            var handleImg = handle.gameObject.AddComponent<Image>();
            handleImg.color = Color.white;

            slider.fillRect = fill;
            slider.handleRect = handle;
            slider.targetGraphic = handleImg;
            slider.direction = Slider.Direction.LeftToRight;
            slider.wholeNumbers = false;
            return slider;
        }

        private static void Stretch(RectTransform rt, Vector2 anchorMin, Vector2 anchorMax)
        {
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }
    }
}
