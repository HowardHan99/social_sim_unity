using UnityEngine;

namespace SessionReview
{
    public class SessionReviewManager : MonoBehaviour
    {
        public static SessionReviewManager Instance { get; private set; }

        [Header("Keyboard Shortcuts")]
        [SerializeField] private KeyCode reviewToggleKey = KeyCode.Tab;
        [SerializeField] private KeyCode prevTrialKey = KeyCode.LeftBracket;
        [SerializeField] private KeyCode nextTrialKey = KeyCode.RightBracket;
        [SerializeField] private KeyCode playPauseKey = KeyCode.Space;
        [SerializeField] private KeyCode ghostTrailKey = KeyCode.G;

        [Header("Perspective Keys")]
        [SerializeField] private KeyCode robotFPKey = KeyCode.F1;
        [SerializeField] private KeyCode pwdFPKey = KeyCode.F2;
        [SerializeField] private KeyCode pedViewKey = KeyCode.F3;
        [SerializeField] private KeyCode topDownKey = KeyCode.F4;
        [SerializeField] private KeyCode freeCamKey = KeyCode.F5;

        [Header("Speed")]
        [SerializeField] private KeyCode speedUpKey = KeyCode.Equals;
        [SerializeField] private KeyCode speedDownKey = KeyCode.Minus;

        [Header("VLM Capture Annotation")]
        [Tooltip("Transform whose position is recorded when a VLM capture occurs (e.g. the robot base_link). If null, falls back to SEAN robot.")]
        public Transform vlmCaptureSource;
        [Tooltip("Optional: assign the VLM capture UI Button here to auto-wire the onClick event.")]
        public UnityEngine.UI.Button vlmCaptureButton;

        private SessionTracker sessionTracker;
        private ControlModeLog controlModeLog;
        private TrialDataArchive trialArchive;
        private MultiAgentTrajectoryRenderer trajectoryRenderer;
        private MetricsOverlayUI metricsOverlay;
        private RewindController rewindController;
        private LiveTrajectoryRecorder trajectoryRecorder;

        private int reviewTrialIndex = -1;
        private bool inRewindMode;

        private static readonly float[] speedSteps = { 0.25f, 0.5f, 1f, 2f, 4f };
        private int currentSpeedIndex = 2;
        private float savedTimeScale = 1f;

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            EnsureComponents();
        }

        private void EnsureComponents()
        {
            sessionTracker = GetComponent<SessionTracker>();
            if (sessionTracker == null)
                sessionTracker = gameObject.AddComponent<SessionTracker>();

            controlModeLog = GetComponent<ControlModeLog>();
            if (controlModeLog == null)
                controlModeLog = gameObject.AddComponent<ControlModeLog>();

            trialArchive = GetComponent<TrialDataArchive>();
            if (trialArchive == null)
                trialArchive = gameObject.AddComponent<TrialDataArchive>();

            trajectoryRenderer = GetComponent<MultiAgentTrajectoryRenderer>();
            if (trajectoryRenderer == null)
                trajectoryRenderer = gameObject.AddComponent<MultiAgentTrajectoryRenderer>();

            metricsOverlay = GetComponent<MetricsOverlayUI>();
            if (metricsOverlay == null)
                metricsOverlay = gameObject.AddComponent<MetricsOverlayUI>();

            rewindController = GetComponent<RewindController>();
            if (rewindController == null)
                rewindController = gameObject.AddComponent<RewindController>();

            trajectoryRecorder = GetComponent<LiveTrajectoryRecorder>();
            if (trajectoryRecorder == null)
                trajectoryRecorder = gameObject.AddComponent<LiveTrajectoryRecorder>();
        }

        void Start()
        {
            if (sessionTracker != null)
                sessionTracker.TrialEnded += OnTrialEnded;

            if (vlmCaptureButton != null)
                vlmCaptureButton.onClick.AddListener(RecordVLMCapture);
        }

        void OnDestroy()
        {
            if (vlmCaptureButton != null)
                vlmCaptureButton.onClick.RemoveListener(RecordVLMCapture);

            if (sessionTracker != null)
                sessionTracker.TrialEnded -= OnTrialEnded;
            if (Instance == this)
                Instance = null;
        }

        private void OnTrialEnded(TrialEndInfo info)
        {
            reviewTrialIndex = trialArchive.TrialCount - 1;
            Debug.Log($"[SessionReview] Trial #{info.trialNumber} ended ({info.reason}). " +
                      $"Press [{reviewToggleKey}] to review.");
        }

        void Update()
        {
            HandleInput();
        }

        private void HandleInput()
        {
            if (inRewindMode)
            {
                HandleRewindInput();
                return;
            }

            // Tab only works when there are completed trials
            if (Input.GetKeyDown(reviewToggleKey) && trialArchive.TrialCount > 0)
                EnterRewindMode(trialArchive.TrialCount - 1);
        }

        private void HandleRewindInput()
        {
            if (Input.GetKeyDown(KeyCode.Escape) || Input.GetKeyDown(reviewToggleKey))
            {
                ExitReviewMode();
                return;
            }

            if (Input.GetKeyDown(playPauseKey))
                rewindController.TogglePlayPause();

            if (Input.GetKeyDown(KeyCode.LeftArrow))
                rewindController.StepBackward();
            if (Input.GetKeyDown(KeyCode.RightArrow))
                rewindController.StepForward();

            if (Input.GetKeyDown(KeyCode.Home))
                rewindController.JumpToStart();
            if (Input.GetKeyDown(KeyCode.End))
                rewindController.JumpToEnd();

            if (Input.GetKeyDown(speedUpKey))
            {
                currentSpeedIndex = Mathf.Min(currentSpeedIndex + 1, speedSteps.Length - 1);
                rewindController.SetPlaybackSpeed(speedSteps[currentSpeedIndex]);
            }
            if (Input.GetKeyDown(speedDownKey))
            {
                currentSpeedIndex = Mathf.Max(currentSpeedIndex - 1, 0);
                rewindController.SetPlaybackSpeed(speedSteps[currentSpeedIndex]);
            }

            if (Input.GetKeyDown(robotFPKey))
                rewindController.SetPerspective(PerspectiveMode.RobotFirstPerson);
            if (Input.GetKeyDown(pwdFPKey))
                rewindController.SetPerspective(PerspectiveMode.PWDFirstPerson);
            if (Input.GetKeyDown(pedViewKey))
                rewindController.SetPerspective(PerspectiveMode.PedestrianOverShoulder);
            if (Input.GetKeyDown(topDownKey))
                rewindController.SetPerspective(PerspectiveMode.TopDown);
            if (Input.GetKeyDown(freeCamKey))
                rewindController.SetPerspective(PerspectiveMode.FreeCam);

            if (Input.GetKeyDown(ghostTrailKey))
                rewindController.ToggleTrails();

            if (Input.GetKeyDown(prevTrialKey) && trialArchive.TrialCount > 1)
                EnterRewindMode(Mathf.Max(0, reviewTrialIndex - 1));
            if (Input.GetKeyDown(nextTrialKey) && trialArchive.TrialCount > 1)
                EnterRewindMode(Mathf.Min(trialArchive.TrialCount - 1, reviewTrialIndex + 1));
        }

        public void EnterRewindMode(int trialIndex)
        {
            var trial = trialArchive.GetTrial(trialIndex);
            if (trial == null) return;

            if (inRewindMode)
                rewindController.ExitRewind();

            reviewTrialIndex = trialIndex;
            inRewindMode = true;
            currentSpeedIndex = 2;

            var recording = trajectoryRecorder.BuildSnapshot();
            float timeOffset = trajectoryRecorder.RecordingStartTime;

            float recStart = trial.startTime - timeOffset;
            float recEnd = trial.endTime - timeOffset;
            var planSnapshots = trajectoryRecorder.GetPlanSnapshots(recStart, recEnd);
            var vlmCaptures = trajectoryRecorder.GetVLMCaptures(recStart, recEnd);

            // Freeze the simulation
            savedTimeScale = Time.timeScale;
            Time.timeScale = 0f;

            trajectoryRenderer.ShowTrajectories(trial, recording, controlModeLog, planSnapshots, vlmCaptures, timeOffset);
            metricsOverlay.ShowTrial(trial);
            rewindController.EnterRewind(trial, recording, controlModeLog, trajectoryRenderer, timeOffset);
        }

        public void ExitReviewMode()
        {
            inRewindMode = false;
            trajectoryRenderer.ClearAll();
            metricsOverlay.Hide();
            rewindController.ExitRewind();

            // Restore simulation
            Time.timeScale = savedTimeScale;
        }

        /// <summary>
        /// Call this from VLM capture button onClick (or any script) to record a VLM annotation.
        /// Can also be called via SessionReviewManager.Instance.RecordVLMCapture() from code.
        /// </summary>
        public void RecordVLMCapture()
        {
            if (trajectoryRecorder == null) return;

            Transform source = ResolveVLMSource();
            if (source == null)
            {
                Debug.LogWarning("[SessionReview] RecordVLMCapture: no source transform available.");
                return;
            }

            string agentId = SessionTracker.GetObjectId(source.gameObject);
            trajectoryRecorder.RecordVLMCapture(agentId, source.position, source.rotation);
        }

        private Transform ResolveVLMSource()
        {
            if (vlmCaptureSource != null)
                return vlmCaptureSource;

            var sean = SEAN.SEAN.instance;
            if (sean != null && sean.robot != null && sean.robot.base_link != null)
                return sean.robot.base_link.transform;

            return null;
        }

        public void CyclePerspective()
        {
            if (inRewindMode)
                rewindController.CyclePerspective();
        }

        public void SelectAgent(string objectId)
        {
            if (inRewindMode)
                rewindController.SelectPedestrian(objectId);
        }

        void OnGUI()
        {
            DrawStatusBadge();

            if (inRewindMode)
            {
                string perspective = rewindController.CurrentPerspective.ToString();
                string playing = rewindController.IsPlaying ? "PLAYING" : "PAUSED";
                GUI.Box(new Rect(Screen.width - 340, 10, 330, 50), "");
                GUI.Label(new Rect(Screen.width - 335, 15, 320, 20),
                    $"REWIND [{playing}] Trial {reviewTrialIndex + 1}/{trialArchive.TrialCount}");
                GUI.Label(new Rect(Screen.width - 335, 35, 320, 20),
                    $"{perspective} | F1-F5:View  Tab/Esc:Exit");
            }
        }

        private void DrawStatusBadge()
        {
            if (inRewindMode) return;

            var sean = SEAN.SEAN.instance;
            bool running = sean != null && sean.robotTask != null && sean.robotTask.isRunning;
            int trials = trialArchive != null ? trialArchive.TrialCount : 0;
            int tracked = trajectoryRecorder != null ? trajectoryRecorder.TrackedCount : 0;

            string text;
            Color bgColor;

            if (running)
            {
                text = $"TRIAL RUNNING | Tracking {tracked} agents";
                bgColor = new Color(0.1f, 0.3f, 0.1f, 0.85f);
            }
            else if (trials > 0)
            {
                text = $"TRIAL ENDED | {trials} trial(s) ready | [Tab] Review";
                bgColor = new Color(0.3f, 0.15f, 0f, 0.85f);
            }
            else
            {
                text = $"Tracking {tracked} agents | Waiting for trial to end...";
                bgColor = new Color(0.1f, 0.1f, 0.1f, 0.7f);
            }

            GUIStyle style = new GUIStyle(GUI.skin.box)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 14,
                fontStyle = FontStyle.Bold,
                normal = { textColor = Color.white }
            };

            float w = Mathf.Max(360, text.Length * 9.5f);
            GUI.backgroundColor = bgColor;
            GUI.Box(new Rect(Screen.width - w - 15, 10, w, 30), text, style);
            GUI.backgroundColor = Color.white;
        }
    }
}
