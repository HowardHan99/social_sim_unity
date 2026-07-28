using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using SEAN.Scenario.Agents;

namespace SessionReview
{
    /// <summary>
    /// Agent Control panel on the secondary display (Display 2): lets the operator pick
    /// any background agent (pedestrian, cyclist, scooter user, PWDAutonomous 鈥?everyone
    /// except the human's "PWDPlayer") and take it over with first-person tank controls,
    /// without touching the participant's Display 1 view.
    ///
    /// Flow: [`] toggles the panel; select an agent by clicking its row, or press
    /// "Pick In Scene" for a world-building-style free-fly camera (WASD/QE fly, RMB look,
    /// wheel dolly) rendered on the same display where clicking an agent selects it 鈥?    /// clicking the already-selected agent (or its row) again takes control immediately,
    /// as does the "Take Control" button. While controlling: W/S drive, A/D turn, Shift
    /// run, RMB free-look, Esc releases the agent back to its own AI.
    ///
    /// Possession recipe follows ManualWheelchairController.SetManualMode: disable the
    /// Base/SFPWDAgent component (its INavigable replanning coroutine is left running so
    /// the path stays fresh), force root motion off, drive the transform directly, then
    /// restore everything on release (SFPWDAgents get RestartNavigationCoroutine).
    /// While picking or possessing, ManualWheelchairController and VelocityController
    /// suppress their keyboard input (see <see cref="KeyboardCaptured"/>) so WASD/Shift
    /// don't also drive the participant's avatar or the robot; joystick input is
    /// untouched and stays with the participant.
    ///
    /// Uses uGUI (Canvas.targetDisplay) like AgentSpeedOverlay because IMGUI can only
    /// render on Display 1. Self-bootstraps at runtime; no scene wiring needed.
    /// </summary>
    public class AgentPossessOverlay : MonoBehaviour
    {
        private static AgentPossessOverlay instance;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (instance != null) return;
            var go = new GameObject("AgentPossessOverlay");
            go.AddComponent<AgentPossessOverlay>();
            DontDestroyOnLoad(go);
        }

        [Tooltip("Shows/hides the panel. Ctrl+key moves it between displays.")]
        public KeyCode toggleKey = KeyCode.BackQuote;

        [Tooltip("Display the panel renders on. 0 = main display, 1 = Display 2 (default).")]
        public int targetDisplay = 1;

        [Header("Pick-mode fly camera")]
        public float flySpeed = 8f;
        public float flyBoost = 3f;
        public float flyLookSensitivity = 3f;
        public float flyStartHeight = 16f;

        /// <summary>
        /// True while the operator is scene-picking or driving a possessed agent.
        /// ManualWheelchairController and VelocityController skip their keyboard input
        /// (WASD/arrows and the Shift mode toggles) while this is set, so the operator's
        /// driving keys don't leak into the participant's avatar or the robot.
        /// </summary>
        public static bool KeyboardCaptured =>
            instance != null && (instance.pickModeActive || instance.possessedController != null);

        public static bool PanelVisible => instance != null && instance.visible;

        private const float RescanInterval = 1.0f;

        // Layout, in the canvas' reference-resolution units.
        private const float Margin = 24f;
        private const float Pad = 16f;
        private const float Gap = 8f;
        private const float HeaderH = 30f;
        private const float HintH = 40f;
        private const float BtnH = 36f;
        private const float StatusH = 28f;
        private const float RowH = 32f;
        private const float PanelW = 470f;

        private class Row
        {
            public Base agent;
            public string label;
            public Button button;
            public Text text;
        }

        private readonly List<Row> rows = new List<Row>();
        private readonly List<GameObject> rowObjects = new List<GameObject>();
        private float nextRescanTime;
        private bool visible = false;

        // Selection / pick mode
        private Base selectedAgent;
        private bool pickModeActive;
        private GameObject pickCamGo;
        private Camera pickCam;
        private float pickYaw;
        private float pickPitch;

        // Possession state (owned here; the controller only drives)
        private Base possessed;
        private PossessedAgentController possessedController;
        private IVI.ManualWheelchairController possessedMwc;
        private bool mwcWasEnabled;
        private Animator possessedAnimator;
        private bool prevRootMotion;

        // UI
        private Canvas canvas;
        private RectTransform panel;
        private Text titleText;
        private Text hintText;
        private Text statusText;
        private Button pickButton;
        private Text pickButtonText;
        private Button controlButton;
        private Button releaseButton;
        private Text markerText;   // "name + 鈻? tag over the selected agent in pick mode
        private Font uiFont;

        private static readonly Color SelectedColor = new Color(0.55f, 1f, 0.7f);
        private static readonly Color PossessedColor = new Color(1f, 0.75f, 0.4f);

        private bool PossessionActive => possessedController != null;

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
                    targetDisplay = targetDisplay == 0 ? 1 : 0;
                    visible = true;
                    ApplyDisplayToCameras();
                }
                else
                {
                    visible = !visible;
                    // Hiding the panel also drops the pick camera (a possession, if any,
                    // keeps running 鈥?Esc or Release ends it).
                    if (!visible && pickModeActive) ExitPickMode();
                }
            }

            int desired = ResolveTargetDisplay();
            if (canvas.targetDisplay != desired) canvas.targetDisplay = desired;

            // Review / world building / onboarding own the screen and the input 鈥?drop
            // everything so we never fight RuntimeEditorManager for clicks or Esc.
            if (ShouldHide())
            {
                if (pickModeActive) ExitPickMode();
                if (PossessionActive || possessed != null) Release();
                RefreshUi();
                return;
            }

            // Possessed agent died externally (trial reset, scene reload): the controller
            // and its camera died with it; just drop the stale references.
            if (possessed == null && (possessedController != null || possessedMwc != null || possessedAnimator != null))
                ClearPossessionRefs();

            if (Time.unscaledTime >= nextRescanTime || AnyRowDead())
            {
                Rescan();
                nextRescanTime = Time.unscaledTime + RescanInterval;
            }

            if (pickModeActive)
                UpdatePickMode();

            if (Input.GetKeyDown(KeyCode.Escape))
            {
                if (PossessionActive) Release();
                else if (pickModeActive) ExitPickMode();
            }

            RefreshUi();
        }

        // ---- Agent list --------------------------------------------------------

        private bool AnyRowDead()
        {
            foreach (var r in rows)
                if (r.agent == null) return true;
            return false;
        }

        private static bool IsSelectable(Base agent)
        {
            if (agent == null) return false;
            if (agent is PlayerAgent || agent.GetType().FullName == "SEAN.Scenario.Agents.Playback.Agent") return false;   // replay ghosts
            return !string.Equals(agent.gameObject.name, "PWDPlayer", System.StringComparison.Ordinal);
        }

        private void Rescan()
        {
            var agents = new List<Base>();
            foreach (Base a in FindObjectsOfType<Base>())
                if (IsSelectable(a)) agents.Add(a);
            // FindObjectsOfType includes disabled components on active objects, so the
            // possessed agent (Base disabled) stays listed; merge defensively anyway.
            if (possessed != null && !agents.Contains(possessed))
                agents.Add(possessed);

            agents.Sort((x, y) =>
            {
                int c = string.CompareOrdinal(x.gameObject.name, y.gameObject.name);
                return c != 0 ? c : x.GetInstanceID().CompareTo(y.GetInstanceID());
            });

            bool same = agents.Count == rows.Count;
            if (same)
                for (int i = 0; i < agents.Count; i++)
                    if (!ReferenceEquals(agents[i], rows[i].agent)) { same = false; break; }
            if (same) return;

            rows.Clear();
            foreach (Base a in agents)
            {
                string tag = a is IVI.SFPWDAgent ? "[PWD] " : "[Ped] ";
                rows.Add(new Row { agent = a, label = tag + a.gameObject.name });
            }
            RebuildRows();
        }

        // ---- Pick mode (world-building-style free camera + click select) -------

        private void EnterPickMode()
        {
            if (pickModeActive) return;

            pickCamGo = new GameObject("AgentPossessPickCamera");
            pickCam = pickCamGo.AddComponent<Camera>();
            pickCam.targetDisplay = ResolveTargetDisplay();
            pickCam.depth = 90f;
            pickCam.fieldOfView = 60f;

            Vector3 focus = selectedAgent != null ? selectedAgent.transform.position : AgentsCentroid();
            pickPitch = 55f;
            pickYaw = 0f;
            pickCamGo.transform.position = focus + new Vector3(0f, flyStartHeight, -flyStartHeight * 0.6f);
            pickCamGo.transform.rotation = Quaternion.Euler(pickPitch, pickYaw, 0f);

            pickModeActive = true;
        }

        private void ExitPickMode()
        {
            if (pickCamGo != null) Destroy(pickCamGo);
            pickCamGo = null;
            pickCam = null;
            pickModeActive = false;
        }

        private Vector3 AgentsCentroid()
        {
            Vector3 sum = Vector3.zero;
            int n = 0;
            foreach (var r in rows)
                if (r.agent != null) { sum += r.agent.transform.position; n++; }
            return n > 0 ? sum / n : Vector3.zero;
        }

        private void UpdatePickMode()
        {
            if (pickCamGo == null) { pickModeActive = false; return; }

            // Free-look with the right mouse button, like the world-building camera.
            if (Input.GetMouseButton(1))
            {
                pickYaw += Input.GetAxis("Mouse X") * flyLookSensitivity;
                pickPitch = Mathf.Clamp(pickPitch - Input.GetAxis("Mouse Y") * flyLookSensitivity, -85f, 85f);
                pickCamGo.transform.rotation = Quaternion.Euler(pickPitch, pickYaw, 0f);
            }

            Vector3 dir = Vector3.zero;
            if (Input.GetKey(KeyCode.W)) dir += Vector3.forward;
            if (Input.GetKey(KeyCode.S)) dir += Vector3.back;
            if (Input.GetKey(KeyCode.A)) dir += Vector3.left;
            if (Input.GetKey(KeyCode.D)) dir += Vector3.right;
            if (Input.GetKey(KeyCode.E)) dir += Vector3.up;
            if (Input.GetKey(KeyCode.Q)) dir += Vector3.down;
            bool boost = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            // Unscaled time so the camera still flies if the game is ever paused.
            pickCamGo.transform.position += pickCamGo.transform.rotation * dir
                * flySpeed * (boost ? flyBoost : 1f) * Time.unscaledDeltaTime;

            float scroll = Input.GetAxis("Mouse ScrollWheel");
            if (Mathf.Abs(scroll) > 0.001f)
                pickCamGo.transform.position += pickCamGo.transform.forward * scroll * 10f;

            if (Input.GetMouseButtonDown(0) && !IsPointerOverUi())
                TrySelectUnderCursor();
        }

        private bool IsPointerOverUi()
        {
            return EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();
        }

        private void TrySelectUnderCursor()
        {
            Vector3 mouse = Input.mousePosition;
            // Map the mouse onto the pick camera's display. In the Editor (and on
            // platforms without display mapping) RelativeMouseAt returns zero 鈥?use the
            // raw position, matching the single-Game-view workflow there.
            Vector3 rel = Display.RelativeMouseAt(mouse);
            if (rel != Vector3.zero)
            {
                if ((int)rel.z != pickCam.targetDisplay) return;
                mouse = new Vector3(rel.x, rel.y, 0f);
            }

            Ray ray = pickCam.ScreenPointToRay(mouse);
            if (!Physics.Raycast(ray, out RaycastHit hit, 500f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                return;

            Base agent = hit.collider.GetComponentInParent<Base>();
            if (!IsSelectable(agent)) return;

            // Second click on the already-selected agent takes control immediately.
            if (ReferenceEquals(agent, selectedAgent) && !PossessionActive)
                Possess(agent);
            else
                selectedAgent = agent;
        }

        // ---- Possession --------------------------------------------------------

        private void Possess(Base agent)
        {
            if (agent == null) return;
            Release();
            if (pickModeActive) ExitPickMode();

            possessed = agent;
            selectedAgent = agent;

            possessedMwc = agent.GetComponent<IVI.ManualWheelchairController>();
            mwcWasEnabled = possessedMwc != null && possessedMwc.enabled;
            if (possessedMwc != null) possessedMwc.enabled = false;

            // Same lookup as Base.Start: the Animator sits on the root, or one level
            // down for avatar prefabs that keep the rig in a nested model instance.
            possessedAnimator = agent.GetComponent<Animator>();
            if (possessedAnimator == null)
                possessedAnimator = agent.GetComponentInChildren<Animator>(true);
            prevRootMotion = possessedAnimator != null && possessedAnimator.applyRootMotion;
            if (possessedAnimator != null) possessedAnimator.applyRootMotion = false;

            Rigidbody rb = agent.GetComponent<Rigidbody>();
            if (rb != null) rb.velocity = Vector3.zero;

            // Stops Base.Update (velocity + rotation + animator params). The INavigable
            // replanning coroutine keeps running on purpose: the path stays fresh so the
            // agent resumes cleanly on release.
            agent.enabled = false;

            possessedController = agent.gameObject.AddComponent<PossessedAgentController>();
            possessedController.Configure(possessedAnimator, ResolveTargetDisplay());

            Debug.Log($"[AgentPossess] Took control of '{agent.gameObject.name}'. WASD drive, Shift run, RMB look, Esc release.");
        }

        private void Release()
        {
            if (possessedController != null) Destroy(possessedController);

            if (possessed != null)
            {
                if (possessedAnimator != null)
                {
                    possessedAnimator.applyRootMotion = prevRootMotion;
                    possessedAnimator.speed = 1f;
                }

                Rigidbody rb = possessed.GetComponent<Rigidbody>();
                if (rb != null) rb.velocity = Vector3.zero;

                possessed.enabled = true;
                // StartCoroutine throws on an inactive GameObject (e.g. an agent a scenario
                // restore deactivated mid-possession); it restarts on its own re-activation.
                if (possessed is IVI.SFPWDAgent sfpwd && sfpwd.gameObject.activeInHierarchy)
                    sfpwd.RestartNavigationCoroutine();
                if (possessedMwc != null && mwcWasEnabled)
                    possessedMwc.enabled = true;

                Debug.Log($"[AgentPossess] Released '{possessed.gameObject.name}' back to its own control.");
            }

            ClearPossessionRefs();
        }

        private void ClearPossessionRefs()
        {
            possessed = null;
            possessedController = null;
            possessedMwc = null;
            possessedAnimator = null;
            mwcWasEnabled = false;
        }

        private bool ShouldHide()
        {
            var srm = SessionReviewManager.Instance;
            return srm != null && (srm.IsReviewModeActive
                                   || srm.IsWorldBuildingModeActive
                                   || srm.IsOnboardingActive);
        }

        private void ApplyDisplayToCameras()
        {
            int display = ResolveTargetDisplay();
            if (pickCam != null) pickCam.targetDisplay = display;
            // The possession camera lives on its own GameObject owned by the controller;
            // cheapest correct move is to re-create it on the new display.
            if (PossessionActive)
            {
                Base agent = possessed;
                Release();
                Possess(agent);
            }
        }

        // ---- UI ----------------------------------------------------------------

        private void EnsureUi()
        {
            if (canvas != null) return;

            EnsureEventSystem();
            uiFont = LoadUiFont();

            var canvasGo = new GameObject("AgentPossessCanvas",
                typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGo.transform.SetParent(transform, false);

            canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.targetDisplay = ResolveTargetDisplay();
            canvas.sortingOrder = 510;   // above the Agent Speed panel, never overlaps anyway

            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            Debug.Log($"[AgentPossessOverlay] Agent Control panel on Display {canvas.targetDisplay + 1}. " +
                      $"[{toggleKey}] shows/hides it, Ctrl+[{toggleKey}] moves it between displays. " +
                      "Pick an agent (list row or Pick In Scene), Take Control drives it first-person; " +
                      "Esc releases. In the Editor use a Game view set to \"Display 2\".");

            // Panel docked to the bottom-LEFT corner (the Agent Speed panel owns bottom-right).
            panel = NewRect("Panel", canvasGo.transform);
            panel.anchorMin = panel.anchorMax = new Vector2(0f, 0f);
            panel.pivot = new Vector2(0f, 0f);
            panel.anchoredPosition = new Vector2(Margin, Margin);
            panel.sizeDelta = new Vector2(PanelW, Pad * 2f + HeaderH + HintH + BtnH + StatusH + 10f);
            var panelImg = panel.gameObject.AddComponent<Image>();
            panelImg.color = new Color(0f, 0f, 0f, 0.8f);

            titleText = NewText(panel, "Title", 22, TextAnchor.MiddleLeft, FontStyle.Bold);
            PlaceTopLeft(titleText.rectTransform, Pad, Pad, PanelW - 2f * Pad, HeaderH);
            titleText.text = "Agent Control";

            hintText = NewText(panel, "Hint", 13, TextAnchor.UpperLeft, FontStyle.Normal);
            PlaceTopLeft(hintText.rectTransform, Pad, Pad + HeaderH, PanelW - 2f * Pad, HintH);
            hintText.color = new Color(1f, 1f, 1f, 0.75f);

            float yBtns = Pad + HeaderH + HintH + 4f;
            pickButton = NewButton(panel, "Pick In Scene");
            PlaceTopLeft((RectTransform)pickButton.transform, Pad, yBtns, 150f, BtnH);
            pickButtonText = pickButton.GetComponentInChildren<Text>();
            pickButton.onClick.AddListener(() =>
            {
                if (pickModeActive) ExitPickMode();
                else EnterPickMode();
            });

            controlButton = NewButton(panel, "Take Control");
            PlaceTopLeft((RectTransform)controlButton.transform, Pad + 150f + Gap, yBtns, 152f, BtnH);
            controlButton.onClick.AddListener(() =>
            {
                if (selectedAgent != null && !PossessionActive) Possess(selectedAgent);
            });

            releaseButton = NewButton(panel, "Release");
            PlaceTopLeft((RectTransform)releaseButton.transform, Pad + 150f + Gap + 152f + Gap, yBtns, 120f, BtnH);
            releaseButton.onClick.AddListener(Release);

            statusText = NewText(panel, "Status", 16, TextAnchor.MiddleLeft, FontStyle.Bold);
            PlaceTopLeft(statusText.rectTransform, Pad, yBtns + BtnH + 6f, PanelW - 2f * Pad, StatusH);

            // Floating "name 鈻? tag over the selected agent while scene-picking; lives on
            // the canvas root and is positioned by projecting through the pick camera.
            markerText = NewText(canvasGo.transform, "PickMarker", 20, TextAnchor.LowerCenter, FontStyle.Bold);
            var markerRt = markerText.rectTransform;
            markerRt.anchorMin = markerRt.anchorMax = new Vector2(0f, 0f);
            markerRt.pivot = new Vector2(0.5f, 0f);
            markerRt.sizeDelta = new Vector2(400f, 56f);
            markerText.color = new Color(1f, 0.95f, 0.4f);
            markerText.gameObject.SetActive(false);
        }

        private void RebuildRows()
        {
            foreach (var go in rowObjects)
                if (go != null) Destroy(go);
            rowObjects.Clear();

            float yRows = Pad + HeaderH + HintH + 4f + BtnH + 6f + StatusH + 4f;
            panel.sizeDelta = new Vector2(PanelW, yRows + rows.Count * RowH + Pad);

            for (int i = 0; i < rows.Count; i++)
            {
                Row row = rows[i];
                Button b = NewButton(panel, row.label, 15);
                var rt = (RectTransform)b.transform;
                PlaceTopLeft(rt, Pad, yRows + i * RowH, PanelW - 2f * Pad, RowH - 4f);
                var img = b.GetComponent<Image>();
                img.color = new Color(1f, 1f, 1f, 0.07f);
                row.button = b;
                row.text = b.GetComponentInChildren<Text>();
                row.text.alignment = TextAnchor.MiddleLeft;
                var textRt = row.text.rectTransform;
                textRt.offsetMin = new Vector2(10f, 0f);

                Row captured = row;
                b.onClick.AddListener(() =>
                {
                    if (captured.agent == null) return;
                    // Second click on the selected row takes control, like the scene pick.
                    if (ReferenceEquals(captured.agent, selectedAgent) && !PossessionActive)
                        Possess(captured.agent);
                    else
                        selectedAgent = captured.agent;
                });
                rowObjects.Add(b.gameObject);
            }
        }

        private void RefreshUi()
        {
            bool show = visible && !ShouldHide();
            if (canvas.gameObject.activeSelf != show)
                canvas.gameObject.SetActive(show);
            if (!show) return;

            if (selectedAgent != null && !IsSelectable(selectedAgent))
                selectedAgent = null;

            titleText.text = $"Agent Control   [{KeyLabel(toggleKey)}] hide 路 [Ctrl+{KeyLabel(toggleKey)}] display";

            if (PossessionActive)
                hintText.text = "DRIVING: [W/S] forward/back 路 [A/D] turn 路 [Shift] run\n[RMB] look around 路 [Esc] or Release button to let go";
            else if (pickModeActive)
                hintText.text = "PICKING: [WASD/QE] fly 路 [RMB] look 路 [wheel] dolly 路 [Shift] boost\nclick an agent to select 鈥?click it again to take control 路 [Esc] exit";
            else
                hintText.text = "Click a row to select (click again to take control),\nor Pick In Scene for a free camera on this display.";

            if (PossessionActive && possessed != null)
            {
                statusText.text = $"Controlling: {possessed.gameObject.name}   ({possessedController.CurrentSpeed:F2} m/s)";
                statusText.color = PossessedColor;
            }
            else if (selectedAgent != null)
            {
                statusText.text = $"Selected: {selectedAgent.gameObject.name}";
                statusText.color = SelectedColor;
            }
            else
            {
                statusText.text = rows.Count > 0 ? "Selected: -" : "No controllable agents in the scene.";
                statusText.color = Color.white;
            }

            if (pickButtonText != null)
                pickButtonText.text = pickModeActive ? "Exit Pick" : "Pick In Scene";
            controlButton.interactable = selectedAgent != null && !PossessionActive;
            releaseButton.interactable = PossessionActive;

            foreach (var row in rows)
            {
                if (row.text == null) continue;
                if (row.agent == null)
                {
                    row.text.text = "  (gone)";
                    row.text.color = Color.gray;
                    continue;
                }
                bool isPossessed = ReferenceEquals(row.agent, possessed);
                bool isSelected = ReferenceEquals(row.agent, selectedAgent);
                row.text.text = (isPossessed ? "[*] " : isSelected ? "[>] " : "    ") + row.label
                                + (isPossessed ? "  - controlling" : "");
                row.text.color = isPossessed ? PossessedColor : isSelected ? SelectedColor : Color.white;
            }

            UpdatePickMarker();
        }

        private void UpdatePickMarker()
        {
            bool show = pickModeActive && pickCam != null && selectedAgent != null;
            if (markerText.gameObject.activeSelf != show)
                markerText.gameObject.SetActive(show);
            if (!show) return;

            Vector3 world = selectedAgent.transform.position + Vector3.up * 2.0f;
            Vector3 sp = pickCam.WorldToScreenPoint(world);
            if (sp.z <= 0f)
            {
                markerText.gameObject.SetActive(false);
                return;
            }
            float scale = Mathf.Max(canvas.scaleFactor, 0.0001f);
            markerText.rectTransform.anchoredPosition = new Vector2(sp.x / scale, sp.y / scale);
            markerText.text = $"{selectedAgent.gameObject.name}\n[>]";
        }

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

        private static string KeyLabel(KeyCode k)
        {
            switch (k)
            {
                case KeyCode.BackQuote: return "`";
                case KeyCode.Comma: return ",";
                case KeyCode.Period: return ".";
                case KeyCode.Backslash: return "\\";
                default: return k.ToString();
            }
        }

        // ---- uGUI construction helpers (same idiom as AgentSpeedOverlay) -------

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
            t.raycastTarget = false;
            return t;
        }

        private Button NewButton(Transform parent, string label, int fontSize = 16)
        {
            var rt = NewRect("Button", parent);
            var img = rt.gameObject.AddComponent<Image>();
            img.color = new Color(0.22f, 0.24f, 0.30f, 1f);
            var b = rt.gameObject.AddComponent<Button>();
            b.targetGraphic = img;
            var t = NewText(rt, "Label", fontSize, TextAnchor.MiddleCenter, FontStyle.Bold);
            var textRt = t.rectTransform;
            textRt.anchorMin = Vector2.zero;
            textRt.anchorMax = Vector2.one;
            textRt.offsetMin = Vector2.zero;
            textRt.offsetMax = Vector2.zero;
            return b;
        }
    }
}

