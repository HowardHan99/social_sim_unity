using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace SessionReview
{
    /// <summary>
    /// Lets a reviewer step into the robot's seat DURING a replay and send a signal from
    /// there — a turn-indicator flash or a spoken message — at the exact moment on the
    /// scrubber where they think the robot should have signalled.
    ///
    /// It deliberately does NOT reuse the live Send Signal flow (SignalUIManager): that one
    /// pauses the game through PauseManager, takes over the canvas and drives the VLM
    /// capture camera, all of which fight the review's frozen timescale and its own camera
    /// stack. This panel talks straight to the two output devices the replay already uses
    /// (RobotSignalLightController's review flashes and TTSManager) so nothing about the
    /// review state has to be unwound afterwards.
    ///
    /// The way back is the point of the design, so it is spelled out three ways and none of
    /// them is a dead end:
    ///   - "Back to &lt;view&gt;" restores the perspective the reviewer came from,
    ///   - Esc backs out one rung at a time (typing -> panel -> previous view -> top-down
    ///     -> next-step menu; see SessionReviewManager's Escape ladder),
    ///   - closing it with [x] leaves a button on the ReviewPanels toggle bar to re-open it.
    ///
    /// Everything sent here is post-hoc annotation, never trial data: it is saved to
    /// review_signals.json beside the trial (see <see cref="ReviewSignalRecording"/>) and
    /// kept out of the recorded signal_annotations.json.
    /// </summary>
    public class ReplaySignalOverlay : MonoBehaviour
    {
        private const string MessageFieldControl = "ReplaySignalCustomMessage";
        private const string SaveFileName = "review_signals.json";
        private const float ToastSeconds = 2.5f;

        // Used when the scene has no UIManager to borrow the robot player's presets from.
        private static readonly string[] FallbackPresetMessages =
        {
            "Please, give way.",
            "Excuse me.",
            "Attention, robot here."
        };

        /// <summary>
        /// True while the custom-message field owns the keyboard. Review hotkeys (Space,
        /// E, G, F1-F5...) must stand down while it is set, or typing a message would
        /// toggle playback and swap cameras instead of entering text.
        /// </summary>
        public static bool IsTypingMessage { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            IsTypingMessage = false;
        }

        [Header("Panel")]
        [SerializeField] private float panelWidth = 400f;
        [SerializeField] private float panelHeight = 500f;
        [Tooltip("Pause the replay when a signal is sent, so the annotation lands on the exact "
                 + "frame the reviewer was looking at.")]
        [SerializeField] private bool pauseOnSend = true;

        private readonly ReviewPanels.State panel = new ReviewPanels.State();
        private readonly List<ReviewSignalEvent> sentThisReview = new List<ReviewSignalEvent>();

        private RewindController rewind;
        private TrialRecord trial;
        private RobotSignalLightController lights;
        private TTSManager tts;
        private UIManager voiceUi;
        private List<string> presetMessages;

        private bool active;
        private bool autoOpenedForRobotPov;
        private string customMessage = string.Empty;
        private bool clearFocusRequested;
        private PerspectiveMode lastPerspective = PerspectiveMode.TopDown;
        private string toast = string.Empty;
        private float toastUntil;
        private string savePath;

        /// <summary>Panel is on screen (not closed via [x] and not hidden).</summary>
        public bool IsVisible => active && !panel.hidden;

        public void BeginReview(RewindController rewindController, TrialRecord reviewTrial)
        {
            rewind = rewindController;
            trial = reviewTrial;
            active = rewind != null && trial != null;

            sentThisReview.Clear();
            customMessage = string.Empty;
            savePath = null;
            // Re-resolve per review: a restarted trial reloads the scene, and the cached
            // preset list would otherwise outlive the UIManager it was read from.
            presetMessages = null;
            toast = string.Empty;
            autoOpenedForRobotPov = false;
            IsTypingMessage = false;

            // Starts closed: the reviewer opens it by stepping into the robot's view (F1 or
            // the toggle-bar button), which is the only context where it means anything.
            panel.hidden = true;
            panel.collapsed = false;
            lastPerspective = rewind != null ? rewind.CurrentPerspective : PerspectiveMode.TopDown;

            ResolveSignalDevices();
        }

        public void EndReview()
        {
            if (active && sentThisReview.Count > 0)
                SaveReviewSignals();

            active = false;
            rewind = null;
            trial = null;
            panel.hidden = true;
            IsTypingMessage = false;
        }

        private void OnDisable()
        {
            IsTypingMessage = false;
        }

        /// <summary>
        /// One rung of the review's Escape ladder. Returns true when this panel consumed the
        /// press: first it drops keyboard focus (so Esc leaves a half-typed message instead
        /// of the whole panel), then it closes the panel and puts the camera back where the
        /// reviewer was before they stepped into the robot.
        /// </summary>
        public bool TryHandleEscape()
        {
            if (!active)
                return false;

            if (IsTypingMessage)
            {
                clearFocusRequested = true;
                IsTypingMessage = false;
                return true;
            }

            if (!panel.hidden)
            {
                panel.hidden = true;
                if (rewind != null && IsRobotView(rewind.CurrentPerspective))
                    LeaveRobotPov();
                return true;
            }

            return false;
        }

        /// <summary>
        /// Open the panel and put the review camera on the robot. Third person, not first:
        /// the robot's own indicators are out of frame from its eyes, so a reviewer sending
        /// a lighting signal could not see what they just sent.
        /// </summary>
        public void EnterRobotPov()
        {
            if (!active || rewind == null)
                return;

            panel.hidden = false;
            panel.collapsed = false;
            if (!IsRobotView(rewind.CurrentPerspective))
                rewind.SetPerspective(PerspectiveMode.RobotThirdPerson);
        }

        /// <summary>Return to the view the reviewer was in before the robot POV.</summary>
        public void LeaveRobotPov()
        {
            if (!active || rewind == null)
                return;

            rewind.SetPerspective(BackTarget());
        }

        private PerspectiveMode BackTarget()
        {
            if (rewind == null)
                return PerspectiveMode.TopDown;

            PerspectiveMode previous = rewind.PreviousPerspective;
            // Never "back" into a robot view — that is the one being left. Top-down is home.
            return IsRobotView(previous) ? PerspectiveMode.TopDown : previous;
        }

        private static bool IsRobotView(PerspectiveMode mode)
        {
            return mode == PerspectiveMode.RobotThirdPerson || mode == PerspectiveMode.RobotFirstPerson;
        }

        private void Update()
        {
            if (!active || rewind == null)
                return;

            // Stepping into the robot's view IS the entry point for this panel, however the
            // reviewer got there (F1, the perspective cycle, or the button below).
            PerspectiveMode current = rewind.CurrentPerspective;
            if (current != lastPerspective)
            {
                if (IsRobotView(current) && !autoOpenedForRobotPov)
                {
                    panel.hidden = false;
                    panel.collapsed = false;
                    autoOpenedForRobotPov = true;
                }
                else if (!IsRobotView(current))
                {
                    // Re-arm, so the next trip into the robot's eyes opens it again even if
                    // the reviewer closed it with [x] in between.
                    autoOpenedForRobotPov = false;
                }
                lastPerspective = current;
            }
        }

        // ---- Sending -----------------------------------------------------------

        private void SendLighting(SignalAnnotationType type)
        {
            ResolveSignalDevices();
            if (lights == null)
            {
                Toast("No RobotSignalLightController in the scene.");
                Debug.LogWarning("[SessionReview] Replay signal: lighting skipped, no RobotSignalLightController found.");
                return;
            }

            FreezeForSend();

            switch (type)
            {
                case SignalAnnotationType.LightingLeft:
                    lights.PlayReviewFlashLeft();
                    break;
                case SignalAnnotationType.LightingRight:
                    lights.PlayReviewFlashRight();
                    break;
                default:
                    type = SignalAnnotationType.LightingBoth;
                    lights.PlayReviewFlashBoth();
                    break;
            }

            RecordReviewSignal(type, "Lighting", "ReviewLighting", string.Empty);
            Toast($"Sent {LightingName(type)} flash.");
        }

        private void SendVoice(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return;

            message = message.Trim();
            ResolveSignalDevices();
            if (tts == null)
            {
                Toast("No TTSManager in the scene.");
                Debug.LogWarning("[SessionReview] Replay signal: voice skipped, no TTSManager found.");
                return;
            }

            FreezeForSend();
            tts.PlaySpeech(message);

            // VlmCapture is the replay pipeline's "spoken message" type: recorded as one it
            // speaks again (and shows in the signal status box) whenever the scrubber passes
            // over it. The label is what tells it apart from a live capture.
            RecordReviewSignal(SignalAnnotationType.VlmCapture, "Voice", "ReviewVoice", message);
            Toast($"Sent voice: \"{Shorten(message, 32)}\"");
        }

        private void FreezeForSend()
        {
            if (pauseOnSend && rewind != null)
                rewind.SetPlaying(false);
        }

        private void RecordReviewSignal(SignalAnnotationType type, string channel, string label, string message)
        {
            Transform robot = rewind != null ? rewind.ResolveRobotTransform() : null;
            string agentId = rewind != null ? rewind.ResolveRobotObjectId() : null;
            float replayTime = rewind != null ? rewind.CurrentTime : 0f;

            var annotation = new SignalAnnotation
            {
                timestamp = replayTime,
                agentId = string.IsNullOrEmpty(agentId) ? "robot" : agentId,
                type = type,
                position = robot != null ? robot.position : Vector3.zero,
                rotation = robot != null ? robot.rotation : Quaternion.identity,
                label = label,
                metadata = message ?? string.Empty
            };

            if (rewind != null)
                rewind.AddReviewSignal(annotation);

            sentThisReview.Add(new ReviewSignalEvent
            {
                replayTime = replayTime,
                trialElapsed = ElapsedSeconds(),
                channel = channel,
                type = type,
                message = annotation.metadata,
                agentId = annotation.agentId,
                position = annotation.position,
                rotation = annotation.rotation,
                perspective = rewind != null ? rewind.CurrentPerspective.ToString() : string.Empty,
                sentAtLocal = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            });

            SaveReviewSignals();
        }

        // ---- Persistence -------------------------------------------------------

        /// <summary>
        /// Written after every send, not just on review exit: a review that ends by quitting
        /// play mode would otherwise lose the annotations the reviewer just made.
        /// </summary>
        private void SaveReviewSignals()
        {
            if (trial == null || sentThisReview.Count == 0)
                return;

            var data = new ReviewSignalRecording
            {
                trialName = trial.trialName,
                trialNumber = trial.trialNumber,
                sessionId = trial.sessionId,
                savedAtLocal = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                events = sentThisReview
            };

            try
            {
                string path = ResolveSavePath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JsonUtility.ToJson(data, true));
                savePath = path;
            }
            catch (Exception exception)
            {
                Debug.LogError($"[SessionReview] Failed to save review signals: {exception}");
            }
        }

        private string ResolveSavePath()
        {
            string trialFolder = SessionReviewManager.CurrentReviewTrialFolder;
            if (!string.IsNullOrEmpty(trialFolder))
                return Path.Combine(trialFolder, SaveFileName);

            // Reviews of a trial with no archive folder (nothing was written to disk for it)
            // still keep their annotations, grouped under the session instead.
            string fallbackFolder = Path.Combine(TrialDataArchive.LogFolder, "ReviewSignals",
                TrialDataArchive.SanitizeFolderName(trial != null ? trial.sessionId : null, "unassigned"));
            string fileName = trial != null
                ? $"trial_{trial.trialNumber:D3}_{SaveFileName}"
                : SaveFileName;
            return Path.Combine(fallbackFolder, fileName);
        }

        // ---- UI ----------------------------------------------------------------

        private void OnGUI()
        {
            if (!active || rewind == null || trial == null)
                return;

            if (SessionReviewManager.Instance != null && SessionReviewManager.Instance.IsWorldBuildingModeActive)
                return;

            ReviewUiScale.Apply();

            if (clearFocusRequested)
            {
                GUI.FocusControl(null);
                GUIUtility.keyboardControl = 0;
                clearFocusRequested = false;
            }

            float w = Mathf.Min(panelWidth, Mathf.Max(ReviewPanels.MinW, ReviewUiScale.Width - 40f));
            float h = Mathf.Min(panelHeight, Mathf.Max(220f, ReviewUiScale.Height - 200f));
            // Left edge: the right side already carries the rewind header, top-down controls
            // and the Legend, and the bottom is the scrubber.
            Rect defaultRect = new Rect(24f, Mathf.Max(56f, (ReviewUiScale.Height - h) * 0.4f), w, h);

            if (ReviewPanels.Begin(panel, this, "Robot Signal", defaultRect, out Rect content))
            {
                GUILayout.BeginArea(content);
                panel.scroll = GUILayout.BeginScrollView(panel.scroll);
                DrawBody();
                GUILayout.EndScrollView();
                GUILayout.EndArea();
            }
            ReviewPanels.End(panel);

            // Focus is only knowable inside OnGUI, and only while the body actually drew.
            IsTypingMessage = !panel.hidden && !panel.collapsed &&
                              GUI.GetNameOfFocusedControl() == MessageFieldControl;
        }

        private void DrawBody()
        {
            float scale = panel.FontScale;

            var body = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.RoundToInt(17f * scale),
                wordWrap = true
            };
            var section = new GUIStyle(GUI.skin.label)
            {
                fontStyle = FontStyle.Bold,
                fontSize = Mathf.RoundToInt(19f * scale)
            };
            var hint = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.RoundToInt(14f * scale),
                wordWrap = true,
                normal = { textColor = new Color(0.66f, 0.72f, 0.8f) }
            };
            var button = new GUIStyle(GUI.skin.button) { fontSize = Mathf.RoundToInt(17f * scale) };
            float buttonHeight = 30f * scale;
            float gap = 8f * scale;

            bool robotView = IsRobotView(rewind.CurrentPerspective);
            bool firstPerson = rewind.CurrentPerspective == PerspectiveMode.RobotFirstPerson;

            // --- Where you are, and how to get in / out --------------------------
            string viewLabel = robotView
                ? (firstPerson ? "View: ROBOT first person" : "View: ROBOT third person")
                : $"View: {rewind.CurrentPerspective}";
            GUILayout.Label(viewLabel, section);
            GUILayout.Label($"Replay t = {ElapsedSeconds():F1}s / {Mathf.Max(0f, trial.Duration):F1}s   " +
                            $"({(rewind.IsPlaying ? "playing" : "paused")})", body);

            if (!robotView)
            {
                if (GUILayout.Button("Enter robot view, 3rd person   (Shift+F1)", button, GUILayout.Height(buttonHeight)))
                    EnterRobotPov();
            }
            else if (GUILayout.Button($"Back to {BackTarget()}   (Esc)", button, GUILayout.Height(buttonHeight)))
            {
                LeaveRobotPov();
            }

            GUILayout.Space(gap);

            // --- Lighting --------------------------------------------------------
            GUILayout.Label("Lighting", section);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Left", button, GUILayout.Height(buttonHeight)))
                SendLighting(SignalAnnotationType.LightingLeft);
            if (GUILayout.Button("Right", button, GUILayout.Height(buttonHeight)))
                SendLighting(SignalAnnotationType.LightingRight);
            if (GUILayout.Button("Both", button, GUILayout.Height(buttonHeight)))
                SendLighting(SignalAnnotationType.LightingBoth);
            GUILayout.EndHorizontal();
            if (firstPerson)
                GUILayout.Label("The robot's indicators are out of frame from its own eyes — switch to third person (Shift+F1) to watch the flash.", hint);

            GUILayout.Space(gap);

            // --- Voice -----------------------------------------------------------
            GUILayout.Label("Voice", section);
            foreach (string preset in ResolvePresetMessages())
            {
                if (GUILayout.Button(preset, button, GUILayout.Height(buttonHeight)))
                    SendVoice(preset);
            }

            GUILayout.Space(gap * 0.5f);
            GUI.SetNextControlName(MessageFieldControl);
            var field = new GUIStyle(GUI.skin.textField) { fontSize = Mathf.RoundToInt(17f * scale) };
            string typed = GUILayout.TextField(customMessage, field, GUILayout.Height(buttonHeight));
            if (typed != customMessage)
                customMessage = typed;

            bool submitPressed = Event.current.type == EventType.KeyDown &&
                                 (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter) &&
                                 GUI.GetNameOfFocusedControl() == MessageFieldControl;

            bool hasMessage = !string.IsNullOrWhiteSpace(customMessage);
            GUI.enabled = hasMessage;
            bool sendPressed = GUILayout.Button("Send custom message   (Enter)", button, GUILayout.Height(buttonHeight));
            GUI.enabled = true;

            if ((sendPressed || submitPressed) && hasMessage)
            {
                if (submitPressed)
                    Event.current.Use();
                SendVoice(customMessage);
                customMessage = string.Empty;
                clearFocusRequested = true;
            }

            GUILayout.Space(gap);

            // --- What has been sent ---------------------------------------------
            var toggle = new GUIStyle(GUI.skin.toggle) { fontSize = Mathf.RoundToInt(16f * scale) };
            pauseOnSend = GUILayout.Toggle(pauseOnSend, " Pause replay on send", toggle);
            GUILayout.Label($"Sent this review: {sentThisReview.Count}", section);
            for (int i = sentThisReview.Count - 1; i >= 0; i--)
            {
                ReviewSignalEvent evt = sentThisReview[i];
                string what = evt.channel == "Voice"
                    ? $"Voice  \"{Shorten(evt.message, 30)}\""
                    : $"Lighting {LightingName(evt.type)}";
                GUILayout.Label($"  {evt.trialElapsed:F1}s   {what}", body);
            }

            if (sentThisReview.Count > 0)
            {
                GUILayout.Label("Saved as review annotations (review_signals.json), separate from the trial's live signals. " +
                                "Scrub back over one and it plays again.", hint);
                if (!string.IsNullOrEmpty(savePath))
                    GUILayout.Label(savePath, hint);
            }

            if (Time.unscaledTime < toastUntil && !string.IsNullOrEmpty(toast))
            {
                GUILayout.Space(gap * 0.5f);
                GUILayout.Label(toast, section);
            }

            GUILayout.Space(gap);
            GUILayout.Label("Esc backs out one step at a time: typing -> this panel -> the view you came from -> top-down -> exit review. " +
                            "Closing with [x] leaves a button on the panel bar up top.", hint);
        }

        // ---- Helpers -----------------------------------------------------------

        private void ResolveSignalDevices()
        {
            // Include inactive: the live signal UI switches its own objects off for the whole
            // review (SignalUIManager), and the robot's light rig can sit on a disabled root.
            if (lights == null)
                lights = FindIncludingInactive<RobotSignalLightController>();
            if (tts == null)
                tts = FindIncludingInactive<TTSManager>();
            if (voiceUi == null)
                voiceUi = FindIncludingInactive<UIManager>();
        }

        private static T FindIncludingInactive<T>() where T : Component
        {
            T[] found = FindObjectsOfType<T>(true);
            return found != null && found.Length > 0 ? found[0] : null;
        }

        /// <summary>
        /// The robot player's own quick messages, so a signal sent in review is worded exactly
        /// like one sent live. Falls back to the same defaults UIManager ships with. Cached:
        /// IMGUI needs the same button count in the layout and repaint passes of a frame.
        /// </summary>
        private List<string> ResolvePresetMessages()
        {
            if (presetMessages != null)
                return presetMessages;

            presetMessages = new List<string>();
            ResolveSignalDevices();
            if (voiceUi != null && voiceUi.presetVoiceMessages != null)
            {
                foreach (string message in voiceUi.presetVoiceMessages)
                {
                    if (!string.IsNullOrWhiteSpace(message))
                        presetMessages.Add(message);
                }
            }

            if (presetMessages.Count == 0)
                presetMessages.AddRange(FallbackPresetMessages);

            return presetMessages;
        }

        private float ElapsedSeconds()
        {
            if (rewind == null || trial == null)
                return 0f;
            return Mathf.Max(0f, rewind.NormalizedTime * trial.Duration);
        }

        private void Toast(string message)
        {
            toast = message;
            toastUntil = Time.unscaledTime + ToastSeconds;
        }

        private static string LightingName(SignalAnnotationType type)
        {
            switch (type)
            {
                case SignalAnnotationType.LightingLeft: return "LEFT";
                case SignalAnnotationType.LightingRight: return "RIGHT";
                default: return "BOTH";
            }
        }

        private static string Shorten(string value, int max)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= max)
                return value ?? string.Empty;
            return value.Substring(0, max - 1) + "…";
        }
    }
}
