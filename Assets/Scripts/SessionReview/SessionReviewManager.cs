using UnityEngine;
using UnityEngine.SceneManagement;
using System.Collections.Generic;
using System.IO;
using System;
using UnityTemplateProjects;

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
        [SerializeField] private KeyCode speedUpKey = KeyCode.RightBracket;
        [SerializeField] private KeyCode speedDownKey = KeyCode.LeftBracket;

        [Header("VLM Capture Annotation")]
        [Tooltip("Transform whose position is recorded when a VLM capture occurs (e.g. the robot base_link). If null, falls back to SEAN robot.")]
        public Transform vlmCaptureSource;
        [Tooltip("Optional: assign the VLM capture UI Button here to auto-wire the onClick event.")]
        public UnityEngine.UI.Button vlmCaptureButton;

        [Header("Onboarding")]
        [SerializeField] private bool showOnboardingOnStart = true;
        [SerializeField] private KeyCode onboardingToggleKey = KeyCode.O;

        [Header("Post-Trial Prompt")]
        [SerializeField] private bool usePostTrialPrompt = true;
        [SerializeField] private KeyCode replayTrialKey = KeyCode.R;

        [Header("End Interaction Button")]
        [Tooltip("Show an on-screen button during an active interaction that ends it and proceeds to the next phase.")]
        [SerializeField] private bool showEndInteractionButton = true;
        [SerializeField] private string endInteractionButtonLabel = "End Interaction";

        [Header("Pre-Trial Ready Prompt")]
        [SerializeField] private KeyCode startTrialKey = KeyCode.Return;
        [SerializeField] private KeyCode exportReviewKey = KeyCode.E;
        [SerializeField] private bool requirePlanBeforeTrialStart = true;
        [SerializeField] private bool allowStartWithoutRosBackend = true;

        private SessionTracker sessionTracker;
        private ControlModeLog controlModeLog;
        private TrialDataArchive trialArchive;
        private MultiAgentTrajectoryRenderer trajectoryRenderer;
        private MetricsOverlayUI metricsOverlay;
        private RewindController rewindController;
        private LiveTrajectoryRecorder trajectoryRecorder;

        private int reviewTrialIndex = -1;
        private bool inRewindMode;
        private bool showPostTrialPrompt;
        private bool showTrialStartPrompt;
        private bool trialStartReady;
        private bool bypassRosBackendForTrialStart;
        private bool postTrialPromptPausedTime;
        private bool trialStartPromptPausedTime;
        private bool trialWarmupPending;
        private int trialWarmupDelayFrames;
        private int trialWarmupGoalRepublishFrames;
        private TrialEndInfo latestTrialEndInfo;
        private TrialRecord currentReviewTrial;
        private Rerun.StateRecording currentReviewRecording;
        private float currentReviewTimeOffset;
        private bool showReviewExportPanel;
        private ReviewExportSettings reviewExportSettings = new ReviewExportSettings();
        private Bounds reviewExportEnvelope;
        private string lastReviewExportPath;
        private bool isTopDownPanning;
        private Vector2 lastTopDownMousePosition;
        private static Texture2D lineTexture;
        private Vector2 worldBuildingSpawnScroll;
        private bool showReviewCompletionPrompt;
        private bool inWorldBuildingMode;
        private Camera worldBuildingCamera;
        private RuntimeEditorManager runtimeEditorManager;
        private SimpleCameraController worldBuildingCameraController;
        private Vector3 pendingWorldBuildingCameraPosition;
        private Quaternion pendingWorldBuildingCameraRotation;
        private float pendingWorldBuildingOrthoSize;
        private bool hasPendingWorldBuildingCameraPose;
        private Camera worldBuildingPreviousMainCamera;
        private bool worldBuildingPreviousMainCameraEnabled;
        private int worldBuildingPreviousTargetDisplay;
        private bool hasWorldBuildingTargetDisplayOverride;
        private Camera runtimeEditorPreviousRaycastCamera;
        private MonoBehaviour runtimeEditorPreviousCameraController;
        private readonly System.Collections.Generic.Dictionary<Behaviour, bool> worldBuildingDisabledBehaviours =
            new System.Collections.Generic.Dictionary<Behaviour, bool>();

        public bool UsePostTrialPrompt => usePostTrialPrompt;
        public bool IsReviewModeActive => inRewindMode;
        public bool IsWorldBuildingModeActive => inWorldBuildingMode;
        public bool IsPostTrialPromptActive => showPostTrialPrompt;
        public bool IsTrialStartPromptActive => showTrialStartPrompt;
        public bool IsOnboardingActive => showOnboarding;
        public bool IsReviewUiActive => inRewindMode && !inWorldBuildingMode;
        public bool IsLiveTrialRunning
        {
            get
            {
                var sean = SEAN.SEAN.instance;
                return sean != null && sean.robotTask != null && sean.robotTask.isRunning;
            }
        }

        public bool BlocksAutomaticTrialStart => showOnboarding || showTrialStartPrompt || trialWarmupPending;

        /// <summary>True while any session UI is blocking gameplay input (onboarding, prompts, review, warmup).</summary>
        public bool IsMovementInputBlocked =>
            showOnboarding || showTrialStartPrompt || trialWarmupPending ||
            showPostTrialPrompt || inRewindMode || showReviewCompletionPrompt;

        private static readonly float[] speedSteps = { 0.25f, 0.5f, 1f, 2f, 4f };
        private int currentSpeedIndex = 2;
        private float savedTimeScale = 1f;

        private bool showOnboarding;
        private bool onboardingPausedTime;
        private float onboardingSavedTimeScale = 1f;
        private OnboardingPlayerMode selectedPlayerMode = OnboardingPlayerMode.Robot;
        private StartupControlMode selectedRobotStartupControl = StartupControlMode.Manual;
        private StartupControlMode selectedPwdStartupControl = StartupControlMode.Auto;
        private SEAN.Scenario.Agents.PwdGender selectedPwdGender = SEAN.Scenario.Agents.PwdGender.Male;
        private int selectedSceneIndex = -1;
        private Vector2 onboardingSceneScroll;
        private Vector2 onboardingContentScroll;

        private GUIStyle onboardingPanelStyle;
        private GUIStyle onboardingTitleStyle;
        private GUIStyle onboardingSectionStyle;
        private GUIStyle onboardingBodyStyle;
        private GUIStyle onboardingHintStyle;
        private GUIStyle onboardingPrimaryButtonStyle;
        private GUIStyle onboardingSecondaryButtonStyle;
        private GUIStyle onboardingChipStyle;
        private GUIStyle onboardingChipActiveStyle;
        private GUIStyle onboardingSceneButtonStyle;
        private GUIStyle onboardingSceneActiveButtonStyle;
        private GUIStyle onboardingPreviewLabelStyle;
        private bool onboardingStylesBuilt;
        private Texture2D femaleWheelchairPreview;
        private Texture2D maleWheelchairPreview;
        private Texture2D dogwalkerPreview;
        private Texture2D scooterUserPreview;

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            EnsureComponents();
            LoadOnboardingPreviewTextures();
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
            rewindController.PlaybackReachedEnd -= HandleReviewPlaybackReachedEnd;
            rewindController.PlaybackReachedEnd += HandleReviewPlaybackReachedEnd;

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

            InitializeOnboardingSelection();

            if (showOnboardingOnStart && !SessionOnboardingSettings.HasCompletedOnboarding)
                SetOnboardingVisible(true);
            else if (SessionOnboardingSettings.HasCompletedOnboarding && SessionOnboardingSettings.PendingTrialStart)
                ShowTrialStartPrompt();
        }

        void OnDestroy()
        {
            if (vlmCaptureButton != null)
                vlmCaptureButton.onClick.RemoveListener(RecordVLMCapture);

            if (sessionTracker != null)
                sessionTracker.TrialEnded -= OnTrialEnded;
            if (rewindController != null)
                rewindController.PlaybackReachedEnd -= HandleReviewPlaybackReachedEnd;
            if (Instance == this)
                Instance = null;
        }

        private void OnTrialEnded(TrialEndInfo info)
        {
            if (inRewindMode)
                ExitReviewMode();

            ExitWorldBuildingMode(restoreGameplayCameras: inWorldBuildingMode);

            reviewTrialIndex = trialArchive.TrialCount - 1;
            latestTrialEndInfo = info;
            showPostTrialPrompt = usePostTrialPrompt;
            if (showPostTrialPrompt)
                PauseForPostTrialPrompt();
            Debug.Log($"[SessionReview] Trial #{info.trialNumber} ended ({info.reason}). " +
                      $"Press [{reviewToggleKey}] to review.");
        }

        void Update()
        {
            ProcessTrialWarmup();
            HandleInput();
        }

        private void HandleInput()
        {
            if (Input.GetKeyDown(onboardingToggleKey) && (SessionOnboardingSettings.HasCompletedOnboarding || !showOnboarding))
                SetOnboardingVisible(!showOnboarding);

            if (showOnboarding)
                return;

            if (showTrialStartPrompt)
            {
                HandleTrialStartPromptInput();
                return;
            }

            if (showPostTrialPrompt)
            {
                HandlePostTrialPromptInput();
                return;
            }

            if (showReviewCompletionPrompt)
            {
                HandleReviewCompletionPromptInput();
                return;
            }

            if (inWorldBuildingMode)
            {
                HandleWorldBuildingInput();
                return;
            }

            if (inRewindMode)
            {
                HandleRewindInput();
                return;
            }

            // Tab only works when there are completed trials
            if (Input.GetKeyDown(reviewToggleKey) && trialArchive.TrialCount > 0)
                EnterRewindMode(trialArchive.TrialCount - 1);
        }

        private void HandleTrialStartPromptInput()
        {
            if (Input.GetKeyDown(startTrialKey) || Input.GetKeyDown(KeyCode.KeypadEnter))
            {
                if (trialStartReady)
                {
                    StartTrialFromPrompt();
                }
            }
        }

        private void HandlePostTrialPromptInput()
        {
            if (Input.GetKeyDown(reviewToggleKey))
            {
                HidePostTrialPrompt();
                EnterRewindMode(trialArchive.TrialCount - 1);
                return;
            }

            if (Input.GetKeyDown(replayTrialKey))
            {
                StartNextTrialFromPrompt();
            }
        }

        private void HandleReviewCompletionPromptInput()
        {
            // ESC backs out of the next-step menu and returns to the still-active
            // review instead of committing to exiting the scenario. Mirror the
            // "Keep Reviewing" button: restore the speed the menu zeroed so Space
            // advances time again.
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                showReviewCompletionPrompt = false;
                rewindController?.SetPlaybackSpeed(speedSteps[currentSpeedIndex]);
                return;
            }

            if (Input.GetKeyDown(replayTrialKey))
                StartNextTrialFromReviewCompletion();

            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
                StartNextTrialFromReviewCompletion();
        }

        private void EndReviewAndShowNextStepMenu()
        {
            if (!inRewindMode)
                return;

            rewindController?.SetPlaybackSpeed(0f);
            if (rewindController != null && rewindController.IsPlaying)
                rewindController.TogglePlayPause();

            showReviewCompletionPrompt = true;
            showReviewExportPanel = false;
        }

        private void HandleWorldBuildingInput()
        {
            if (runtimeEditorManager == null)
            {
                ExitWorldBuildingMode(true);
                showPostTrialPrompt = true;
                PauseForPostTrialPrompt();
                return;
            }

            bool mouseOverWorldBuildingUi = IsMouseOverWorldBuildingUi();
            if (worldBuildingCameraController != null)
                worldBuildingCameraController.enabled = !mouseOverWorldBuildingUi;

            if (!runtimeEditorManager.isEditorActive)
            {
                ExitWorldBuildingMode(true);
                showPostTrialPrompt = true;
                PauseForPostTrialPrompt();
            }
        }

        private void HandleRewindInput()
        {
            HandleTopDownMouseInput();

            var drawTrajManager = FindObjectOfType<TrajectoryManager>();
            bool isDrawTrajectoryModeActive = drawTrajManager != null && drawTrajManager.IsDrawMode;

            if (isDrawTrajectoryModeActive)
            {
                if (Input.GetKeyDown(reviewToggleKey))
                    return;

                if (Input.GetKeyDown(KeyCode.Escape))
                    return;
            }

            bool lightingTestPressed =
                Input.GetKeyDown(KeyCode.Minus) ||
                Input.GetKeyDown(KeyCode.KeypadMinus);
            bool audioTestPressed =
                Input.GetKeyDown(KeyCode.Equals) ||
                Input.GetKeyDown(KeyCode.KeypadPlus);

            if (Input.GetKeyDown(exportReviewKey))
            {
                showReviewExportPanel = !showReviewExportPanel;
            }

            if (Input.GetKeyDown(KeyCode.Escape))
            {
                // Back out one layer at a time instead of ending the review (and
                // tearing down the gameplay camera) on the very first press:
                //   1) close the export panel,
                //   2) return to the default top-down view,
                //   3) only then surface the next-step menu.
                if (showReviewExportPanel)
                {
                    showReviewExportPanel = false;
                }
                else if (rewindController != null &&
                         rewindController.CurrentPerspective != PerspectiveMode.TopDown)
                {
                    rewindController.SetPerspective(PerspectiveMode.TopDown);
                }
                else
                {
                    EndReviewAndShowNextStepMenu();
                }
                return;
            }

            if (Input.GetKeyDown(reviewToggleKey))
            {
                ExitReviewMode();
                return;
            }

            if (Input.GetKeyDown(playPauseKey))
            {
                // Clear any IMGUI keyboard focus (e.g. progress-bar slider) so
                // Space reliably toggles play even right after scrubbing.
                GUIUtility.keyboardControl = 0;
                bool before = rewindController.IsPlaying;
                rewindController.TogglePlayPause();
                Debug.Log($"[SessionReview] Space pressed. isPlaying {before} -> {rewindController.IsPlaying}  speed={rewindController.PlaybackSpeed:F2}");
            }

            if (Input.GetKeyDown(KeyCode.LeftArrow))
                rewindController.StepBackward();
            if (Input.GetKeyDown(KeyCode.RightArrow))
                rewindController.StepForward();

            if (Input.GetKeyDown(KeyCode.Home))
                rewindController.JumpToStart();
            if (Input.GetKeyDown(KeyCode.End))
                rewindController.JumpToEnd();

            if (lightingTestPressed)
            {
                Debug.Log("[SessionReview] Replay lighting test key detected.");
                rewindController.ToggleLightingReplayTest();
            }

            if (audioTestPressed)
            {
                Debug.Log("[SessionReview] Replay audio test key detected.");
                rewindController.PlayAudioReplayTest();
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

            if (Input.GetKeyDown(KeyCode.PageDown))
                rewindController.AdjustTopDownZoom(1.2f);
            if (Input.GetKeyDown(KeyCode.PageUp))
                rewindController.AdjustTopDownZoom(0.8f);

            if (Input.GetKeyDown(ghostTrailKey))
                rewindController.ToggleTrails();

            // Trial navigation takes priority over speed when the same keys are bound to both.
            // Speed only fires when no trial switch occurred this frame.
            bool trialSwitched = false;
            if (Input.GetKeyDown(prevTrialKey) && trialArchive.TrialCount > 1)
            {
                trialSwitched = true;
                EnterRewindMode(Mathf.Max(0, reviewTrialIndex - 1));
            }
            if (!trialSwitched && Input.GetKeyDown(nextTrialKey) && trialArchive.TrialCount > 1)
            {
                trialSwitched = true;
                EnterRewindMode(Mathf.Min(trialArchive.TrialCount - 1, reviewTrialIndex + 1));
            }

            if (!trialSwitched)
            {
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
            }
        }

        private void HandleTopDownMouseInput()
        {
            if (rewindController == null || rewindController.CurrentPerspective != PerspectiveMode.TopDown)
            {
                isTopDownPanning = false;
                return;
            }

            bool mouseOverReviewUi = IsMouseOverReviewUi();

            float scroll = Input.mouseScrollDelta.y;
            if (!mouseOverReviewUi && Mathf.Abs(scroll) > 0.01f)
            {
                float zoomMultiplier = scroll > 0f ? 0.85f : 1.15f;
                rewindController.ZoomTopDownAtScreenPoint(Input.mousePosition, zoomMultiplier);
            }

            if (mouseOverReviewUi)
            {
                if (!Input.GetMouseButton(2))
                    isTopDownPanning = false;
                return;
            }

            if (Input.GetMouseButtonDown(2))
            {
                isTopDownPanning = true;
                lastTopDownMousePosition = Input.mousePosition;
            }

            if (isTopDownPanning && Input.GetMouseButton(2))
            {
                Vector2 currentMousePosition = Input.mousePosition;
                Vector2 mouseDelta = currentMousePosition - lastTopDownMousePosition;
                rewindController.PanTopDownFromScreenDelta(mouseDelta);
                lastTopDownMousePosition = currentMousePosition;
            }

            if (Input.GetMouseButtonUp(2))
                isTopDownPanning = false;
        }

        private bool IsMouseOverReviewUi()
        {
            Vector2 mouse = Input.mousePosition;
            float guiY = Screen.height - mouse.y;

            Rect topRightStatusRect = new Rect(Screen.width - 340f, 10f, 330f, 50f);
            if (topRightStatusRect.Contains(new Vector2(mouse.x, guiY)))
                return true;

            if (rewindController != null && rewindController.CurrentPerspective == PerspectiveMode.TopDown)
            {
                Rect topDownControlsRect = new Rect(Screen.width - 500f, 66f, 304f, 28f);
                if (topDownControlsRect.Contains(new Vector2(mouse.x, guiY)))
                    return true;
            }

            if (showReviewExportPanel)
            {
                Rect exportButtonRect = new Rect(Screen.width - 170f, 70f, 140f, 32f);
                Rect exportPanelRect = new Rect(Screen.width - 380f, 110f, 360f, 340f);
                if (exportButtonRect.Contains(new Vector2(mouse.x, guiY)) ||
                    exportPanelRect.Contains(new Vector2(mouse.x, guiY)))
                    return true;
            }

            Rect progressBarRect = new Rect(15f, Screen.height - 75f, Screen.width - 30f, 70f);
            return progressBarRect.Contains(new Vector2(mouse.x, guiY));
        }

        public void EnterRewindMode(int trialIndex)
        {
            var trial = trialArchive.GetTrial(trialIndex);
            if (trial == null) return;

            if (inRewindMode)
                rewindController.ExitRewind();

            ExitWorldBuildingMode(true);

            reviewTrialIndex = trialIndex;
            inRewindMode = true;
            showReviewCompletionPrompt = false;
            currentSpeedIndex = 2;

            var recording = trajectoryRecorder.BuildSnapshot();
            float timeOffset = trajectoryRecorder.RecordingStartTime;

            float recStart = trial.startTime - timeOffset;
            float recEnd = trial.endTime - timeOffset;
            var planSnapshots = trajectoryRecorder.GetPlanSnapshots(recStart, recEnd);
            var vlmCaptures = trajectoryRecorder.GetVLMCaptures(recStart, recEnd);
            var signalAnnotations = trajectoryRecorder.GetSignalAnnotations(recStart, recEnd);

            // Freeze the simulation
            savedTimeScale = Time.timeScale;
            Time.timeScale = 0f;

            trajectoryRenderer.ShowTrajectories(trial, recording, controlModeLog, planSnapshots, vlmCaptures, signalAnnotations, timeOffset);
            metricsOverlay.ShowTrial(trial);
            rewindController.EnterRewind(trial, recording, controlModeLog, trajectoryRenderer, timeOffset, signalAnnotations);

            currentReviewTrial = trial;
            currentReviewRecording = recording;
            currentReviewTimeOffset = timeOffset;
            showReviewExportPanel = false;
            lastReviewExportPath = null;
            if (!ReviewRoiExporter.TryComputeTrajectoryEnvelope(trial, recording, timeOffset, out reviewExportEnvelope))
                reviewExportEnvelope = new Bounds(Vector3.zero, new Vector3(10f, 1f, 10f));
        }

        public void ExitReviewMode()
        {
            inRewindMode = false;
            showReviewCompletionPrompt = false;
            trajectoryRenderer.ClearAll();
            metricsOverlay.Hide();
            rewindController.ExitRewind();

            // Restore simulation
            Time.timeScale = savedTimeScale;

            showReviewExportPanel = false;
            currentReviewTrial = null;
            currentReviewRecording = null;
            currentReviewTimeOffset = 0f;
        }

        public void StartNextTrialFromPrompt()
        {
            CaptureReviewCameraForWorldBuilding();
            showReviewCompletionPrompt = false;
            if (inRewindMode)
                ExitReviewMode();
            ExitWorldBuildingMode(true);

            HidePostTrialPrompt();
            latestTrialEndInfo = null;

            var sean = SEAN.SEAN.instance;
            if (sean == null || sean.robotTask == null)
                return;

            ResetControlledMotion();
            ShowTrialStartPrompt();
        }

        public void StartNextTrialFromReviewCompletion()
        {
            showReviewCompletionPrompt = false;
            StartNextTrialFromPrompt();
        }

        public void StartTrialFromPrompt()
        {
            bool canStart = bypassRosBackendForTrialStart
                ? IsTrialPreviewReadyWithoutRosBackend()
                : trialStartReady;

            if (!canStart)
                return;

            var sean = SEAN.SEAN.instance;
            if (sean == null || sean.robotTask == null)
                return;

            SessionOnboardingSettings.UpdateStartupControls(
                selectedPlayerMode,
                selectedRobotStartupControl,
                selectedPwdStartupControl);
            ApplyStartupControlDefaults();
            TrajectoryIO.ClearAllSessions();
            SessionOnboardingSettings.MarkTrialStarted();
            trialWarmupPending = false;
            trialWarmupDelayFrames = 0;
            trialStartReady = false;
            showTrialStartPrompt = false;

            ResetControlledMotion();
            sean.robotTask.StartPendingOrNewTask();
            ApplyStartupControlDefaults();
            sessionTracker?.BeginTrackingForCurrentTask();

            if (trialStartPromptPausedTime)
            {
                Time.timeScale = savedTimeScale;
                trialStartPromptPausedTime = false;
            }
        }

        public void StartTrialWithoutRosBackend()
        {
            if (!IsTrialPreviewReadyWithoutRosBackend())
            {
                trialWarmupPending = true;
                trialWarmupDelayFrames = 0;
                ProcessTrialWarmup();
                return;
            }

            bypassRosBackendForTrialStart = true;
            StartTrialFromPrompt();
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

        public void RecordLightingAnnotation(SignalAnnotationType type = SignalAnnotationType.LightingBoth)
        {
            if (trajectoryRecorder == null)
                return;

            Transform source = ResolveVLMSource();
            if (source == null)
            {
                Debug.LogWarning("[SessionReview] RecordLightingAnnotation: no source transform available.");
                return;
            }

            if (type != SignalAnnotationType.LightingLeft &&
                type != SignalAnnotationType.LightingRight &&
                type != SignalAnnotationType.LightingBoth)
            {
                type = SignalAnnotationType.LightingBoth;
            }

            string agentId = SessionTracker.GetObjectId(source.gameObject);
            trajectoryRecorder.RecordSignalAnnotation(new SignalAnnotation
            {
                timestamp = -1f,
                agentId = agentId,
                type = type,
                position = source.position,
                rotation = source.rotation,
                label = "LightingAnnotation",
                metadata = string.Empty
            });
        }

        public void AttachVLMReplayResponse(string responseText)
        {
            if (trajectoryRecorder == null)
                return;

            Transform source = ResolveVLMSource();
            if (source == null)
            {
                Debug.LogWarning("[SessionReview] AttachVLMReplayResponse: no source transform available.");
                return;
            }

            string agentId = SessionTracker.GetObjectId(source.gameObject);
            trajectoryRecorder.AttachMetadataToLatestVlmAnnotation(agentId, "VLMAnnotation", responseText);
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

        public void OpenOnboardingFromPostTrial()
        {
            showReviewCompletionPrompt = false;
            CaptureReviewCameraForWorldBuilding();
            if (inRewindMode)
                ExitReviewMode();
            ExitWorldBuildingMode(true);

            HidePostTrialPrompt();
            SetOnboardingVisible(true);
        }

        public void EnterWorldBuildingModeFromPostTrial()
        {
            showReviewCompletionPrompt = false;
            CaptureReviewCameraForWorldBuilding();

            if (inRewindMode)
                ExitReviewMode();

            HidePostTrialPrompt();
            ActivateWorldBuildingView();
        }

        void OnGUI()
        {
            if (showOnboarding)
                DrawOnboardingUI();

            DrawStatusBadge();

            DrawEndInteractionButton();

            if (showTrialStartPrompt)
                DrawTrialStartPrompt();

            if (showPostTrialPrompt)
                DrawPostTrialPrompt();

            if (showReviewCompletionPrompt)
                DrawReviewCompletionPrompt();

            if (inRewindMode)
            {
                DrawReviewRoiOverlay();

                string perspective = rewindController.CurrentPerspective.ToString();
                string playing = rewindController.IsPlaying ? "PLAYING" : "PAUSED";
                var drawTrajManager = FindObjectOfType<TrajectoryManager>();
                bool isDrawTrajectoryModeActive = drawTrajManager != null && drawTrajManager.IsDrawMode;
                string controlsLine = isDrawTrajectoryModeActive
                    ? $"{perspective} | Draw Traj: LMB draw  Wheel:Zoom  MMB:Pan  Esc:Finish Draw"
                    : $"{perspective} | F1-F5:View  Wheel:Zoom  MMB:Pan  Tab/Esc:Exit";
                GUI.Box(new Rect(Screen.width - 340, 10, 330, 50), "");
                GUI.Label(new Rect(Screen.width - 335, 15, 320, 20),
                    $"REWIND [{playing}] Trial {reviewTrialIndex + 1}/{trialArchive.TrialCount}");
                GUI.Label(new Rect(Screen.width - 335, 35, 320, 20),
                    controlsLine);
                DrawEndReviewButton();
                DrawTopDownReviewControls();

                DrawReviewExportPanel();
            }

            if (inWorldBuildingMode)
                DrawWorldBuildingOverlay();
        }

        private void DrawEndInteractionButton()
        {
            if (!showEndInteractionButton)
                return;

            // Only available while an interaction is actively running and nothing else
            // (onboarding, prompts, review, world building) is occupying the screen.
            if (sessionTracker == null || !sessionTracker.IsTracking)
                return;
            if (IsMovementInputBlocked || inWorldBuildingMode || showOnboarding)
                return;

            float width = 220f;
            float height = 40f;
            float x = (Screen.width - width) * 0.5f;
            float y = Screen.height - height - 24f;

            if (GUI.Button(new Rect(x, y, width, height), endInteractionButtonLabel))
                EndInteractionAndProceed();
        }

        private void EndInteractionAndProceed()
        {
            // Ending the trial fires SessionTracker.TrialEnded, which OnTrialEnded handles
            // by advancing to the next phase (post-trial prompt / review).
            sessionTracker?.EndCurrentTrialManually();
        }

        private void DrawEndReviewButton()
        {
            float width = 156f;
            float height = 34f;
            float x = Screen.width - width - 18f;
            float y = Screen.height - 126f;

            if (GUI.Button(new Rect(x, y, width, height), "End Review / Menu"))
                EndReviewAndShowNextStepMenu();
        }

        private void DrawTopDownReviewControls()
        {
            if (rewindController == null || rewindController.CurrentPerspective != PerspectiveMode.TopDown)
                return;

            Bounds roi = ReviewRoiExporter.ApplySettings(reviewExportEnvelope, reviewExportSettings);
            float top = 66f;
            float right = Screen.width - 500f;
            GUI.Label(new Rect(right, top - 22f, 304f, 20f), "Top-down review navigation");

            if (GUI.Button(new Rect(right, top, 96f, 28f), "Focus"))
                rewindController.FocusTopDownOnBounds(roi, false);

            if (GUI.Button(new Rect(right + 104f, top, 96f, 28f), "Zoom +"))
                rewindController.AdjustTopDownZoom(0.8f);

            if (GUI.Button(new Rect(right + 208f, top, 96f, 28f), "Zoom -"))
                rewindController.AdjustTopDownZoom(1.2f);
        }

        private void DrawReviewExportPanel()
        {
            float buttonWidth = 140f;
            Rect buttonRect = new Rect(Screen.width - 170f, 70f, buttonWidth, 32f);
            if (GUI.Button(buttonRect, showReviewExportPanel ? $"Hide Export [{exportReviewKey}]" : $"Export ROI [{exportReviewKey}]"))
                showReviewExportPanel = !showReviewExportPanel;

            if (!showReviewExportPanel)
                return;

            float width = 360f;
            float height = 340f;
            Rect rect = new Rect(Screen.width - width - 20f, 110f, width, height);
            GUI.Box(rect, "");

            float x = rect.x + 16f;
            float y = rect.y + 14f;
            float innerWidth = rect.width - 32f;

            GUI.Label(new Rect(x, y, innerWidth, 24f), "Review ROI Export");
            y += 28f;

            Bounds roi = ReviewRoiExporter.ApplySettings(reviewExportEnvelope, reviewExportSettings);
            GUI.Label(new Rect(x, y, innerWidth, 22f), $"Envelope: {reviewExportEnvelope.size.x:F1}m x {reviewExportEnvelope.size.z:F1}m");
            y += 22f;
            GUI.Label(new Rect(x, y, innerWidth, 22f), $"ROI: {roi.size.x:F1}m x {roi.size.z:F1}m");
            y += 26f;

            DrawSliderRow(x, ref y, innerWidth, "Pad X", ref reviewExportSettings.paddingX, 0f, 15f);
            DrawSliderRow(x, ref y, innerWidth, "Pad Z", ref reviewExportSettings.paddingZ, 0f, 15f);
            DrawSliderRow(x, ref y, innerWidth, "Offset X", ref reviewExportSettings.offsetX, -15f, 15f);
            DrawSliderRow(x, ref y, innerWidth, "Offset Z", ref reviewExportSettings.offsetZ, -15f, 15f);

            reviewExportSettings.exportImage = GUI.Toggle(
                new Rect(x, y, innerWidth, 22f),
                reviewExportSettings.exportImage,
                "Export aligned top-down PNG");
            y += 26f;

            float resolution = reviewExportSettings.imageMaxResolution;
            DrawSliderRow(x, ref y, innerWidth, "Image Max", ref resolution, 512f, 2048f);
            reviewExportSettings.imageMaxResolution = Mathf.RoundToInt(resolution / 64f) * 64;

            if (GUI.Button(new Rect(x, rect.yMax - 96f, 150f, 34f), "Focus Top-Down"))
            {
                rewindController?.FocusTopDownOnBounds(roi);
            }

            if (GUI.Button(new Rect(x, rect.yMax - 54f, 150f, 34f), "Export Current ROI"))
            {
                try
                {
                    lastReviewExportPath = ReviewRoiExporter.ExportTrialRoi(
                        currentReviewTrial,
                        currentReviewRecording,
                        currentReviewTimeOffset,
                        reviewExportSettings);
                }
                catch (System.Exception ex)
                {
                    lastReviewExportPath = "Export failed";
                    Debug.LogError($"[SessionReview] Review ROI export failed: {ex}");
                }
            }

            if (!string.IsNullOrEmpty(lastReviewExportPath))
            {
                GUI.Label(new Rect(x, rect.yMax - 126f, innerWidth, 28f), $"Last export: {lastReviewExportPath}");
            }
        }

        private void DrawReviewRoiOverlay()
        {
            if (!showReviewExportPanel || rewindController == null)
                return;

            if (rewindController.CurrentPerspective != PerspectiveMode.TopDown)
                return;

            Camera camera = rewindController.GetActiveReviewCamera();
            if (camera == null)
                return;

            Bounds roi = ReviewRoiExporter.ApplySettings(reviewExportEnvelope, reviewExportSettings);
            float y = roi.center.y + 0.05f;

            if (!TryProjectWorldPoint(camera, new Vector3(roi.min.x, y, roi.min.z), out Vector2 p0) ||
                !TryProjectWorldPoint(camera, new Vector3(roi.min.x, y, roi.max.z), out Vector2 p1) ||
                !TryProjectWorldPoint(camera, new Vector3(roi.max.x, y, roi.max.z), out Vector2 p2) ||
                !TryProjectWorldPoint(camera, new Vector3(roi.max.x, y, roi.min.z), out Vector2 p3))
            {
                return;
            }

            DrawScreenLine(p0, p1, new Color(0.1f, 1f, 1f, 0.95f), 3f);
            DrawScreenLine(p1, p2, new Color(0.1f, 1f, 1f, 0.95f), 3f);
            DrawScreenLine(p2, p3, new Color(0.1f, 1f, 1f, 0.95f), 3f);
            DrawScreenLine(p3, p0, new Color(0.1f, 1f, 1f, 0.95f), 3f);

            Rect labelRect = new Rect(Mathf.Min(p0.x, p1.x, p2.x, p3.x) + 8f, Mathf.Min(p0.y, p1.y, p2.y, p3.y) + 8f, 220f, 24f);
            GUI.Label(labelRect, "ROI export area");
        }

        private void DrawSliderRow(float x, ref float y, float width, string label, ref float value, float min, float max)
        {
            GUI.Label(new Rect(x, y, 90f, 22f), $"{label}: {value:F1}");
            value = GUI.HorizontalSlider(new Rect(x + 96f, y + 4f, width - 96f, 20f), value, min, max);
            y += 28f;
        }

        private static bool TryProjectWorldPoint(Camera camera, Vector3 worldPoint, out Vector2 guiPoint)
        {
            Vector3 screenPoint = camera.WorldToScreenPoint(worldPoint);
            if (screenPoint.z <= 0f)
            {
                guiPoint = default;
                return false;
            }

            guiPoint = new Vector2(screenPoint.x, Screen.height - screenPoint.y);
            return true;
        }

        private static void DrawScreenLine(Vector2 start, Vector2 end, Color color, float thickness)
        {
            if (lineTexture == null)
                lineTexture = Texture2D.whiteTexture;

            Matrix4x4 matrix = GUI.matrix;
            Color previousColor = GUI.color;

            float angle = Vector3.Angle(end - start, Vector2.right);
            if (start.y > end.y)
                angle = -angle;

            float length = (end - start).magnitude;
            GUI.color = color;
            GUIUtility.RotateAroundPivot(angle, start);
            GUI.DrawTexture(new Rect(start.x, start.y - thickness * 0.5f, length, thickness), lineTexture);
            GUI.matrix = matrix;
            GUI.color = previousColor;
        }

        private void DrawStatusBadge()
        {
            if (inRewindMode || inWorldBuildingMode || showOnboarding || showPostTrialPrompt || showTrialStartPrompt) return;

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

        private void DrawPostTrialPrompt()
        {
            string reasonText = latestTrialEndInfo != null ? latestTrialEndInfo.reason.ToString() : "Completion";

            float width = 420f;
            float height = 150f;
            Rect rect = new Rect((Screen.width - width) * 0.5f, 24f, width, height);

            GUI.Box(rect, "");
            GUI.Label(new Rect(rect.x + 18f, rect.y + 16f, rect.width - 36f, 24f),
                $"SESSION ENDED ({reasonText})");
            GUI.Label(new Rect(rect.x + 18f, rect.y + 46f, rect.width - 36f, 40f),
                $"[{replayTrialKey}] Run again    [{reviewToggleKey}] Review panel");

            if (GUI.Button(new Rect(rect.x + 18f, rect.y + 98f, 168f, 34f), $"Run Again [{replayTrialKey}]"))
                StartNextTrialFromPrompt();

            if (GUI.Button(new Rect(rect.x + 234f, rect.y + 98f, 168f, 34f), $"Review [{reviewToggleKey}]"))
            {
                HidePostTrialPrompt();
                EnterRewindMode(trialArchive.TrialCount - 1);
            }
        }

        private void DrawReviewCompletionPrompt()
        {
            float width = 520f;
            float height = 196f;
            Rect rect = new Rect((Screen.width - width) * 0.5f, 24f, width, height);

            GUI.Box(rect, "");
            GUI.Label(new Rect(rect.x + 18f, rect.y + 16f, rect.width - 36f, 24f), "REVIEW COMPLETE");
            GUI.Label(new Rect(rect.x + 18f, rect.y + 46f, rect.width - 36f, 40f),
                "You reached the end of the reviewed trajectory. Choose what to do next, or keep inspecting this replay.");

            if (GUI.Button(new Rect(rect.x + 18f, rect.y + 98f, 148f, 40f), $"Run Again [{replayTrialKey}]"))
                StartNextTrialFromReviewCompletion();

            if (GUI.Button(new Rect(rect.x + 186f, rect.y + 98f, 152f, 40f), "Choose Scenario"))
                OpenOnboardingFromPostTrial();

            if (GUI.Button(new Rect(rect.x + 358f, rect.y + 98f, 144f, 40f), "World Building"))
                EnterWorldBuildingModeFromPostTrial();

            if (GUI.Button(new Rect(rect.x + 176f, rect.y + 148f, 168f, 32f), "Keep Reviewing"))
            {
                showReviewCompletionPrompt = false;
                // EndReviewAndShowNextStepMenu() zeroed playbackSpeed to fully
                // freeze the review while the prompt was up. Restore the
                // previously selected speed so Space actually advances time.
                rewindController?.SetPlaybackSpeed(speedSteps[currentSpeedIndex]);
            }
        }

        private void DrawWorldBuildingOverlay()
        {
            Rect rect = GetWorldBuildingOverlayRect();
            GUI.Box(rect, "");

            GUIStyle bodyStyle = new GUIStyle(GUI.skin.label)
            {
                wordWrap = true,
                richText = false
            };

            float contentX = rect.x + 16f;
            float contentWidth = rect.width - 32f;
            float y = rect.y + 14f;

            GUI.Label(new Rect(contentX, y, contentWidth, 24f), "World Building");
            y += 28f;

            string introText =
                "Session review is now using the runtime editor. It opens in top-down map view so objects are easier to place, and you can switch into free camera while editing.";
            float introHeight = bodyStyle.CalcHeight(new GUIContent(introText), contentWidth);
            GUI.Label(new Rect(contentX, y, contentWidth, introHeight), introText, bodyStyle);
            y += introHeight + 10f;

            string cameraMode = worldBuildingCameraController != null && worldBuildingCameraController.IsTopDownView()
                ? "Top-down"
                : "Free camera";
            string selectionText = runtimeEditorManager != null && runtimeEditorManager.CurrentSelectedObject != null
                ? $"Selected: {runtimeEditorManager.CurrentSelectedObject.name}"
                : "Selected: none";

            GUI.Label(new Rect(contentX, y, contentWidth, 22f), $"Camera: {cameraMode}");
            y += 22f;
            GUI.Label(new Rect(contentX, y, contentWidth, 22f), selectionText);
            y += 26f;

            string controlsText =
                "Controls: Left click select/drag gizmo | Shift+click add moveable | Shift+drag box-select many | T translate | R rotate | Right mouse free cam | Middle mouse pan | Wheel zoom | F4 top-down reset | Esc deselect | \"Back To Menu\" to exit";
            float controlsHeight = bodyStyle.CalcHeight(new GUIContent(controlsText), contentWidth);
            GUI.Label(new Rect(contentX, y, contentWidth, controlsHeight), controlsText, bodyStyle);
            y += controlsHeight + 12f;

            float buttonWidth = 148f;
            float buttonHeight = 30f;
            float buttonGap = 12f;

            if (GUI.Button(new Rect(contentX, y, buttonWidth, buttonHeight), "Back To Menu"))
            {
                ExitWorldBuildingMode(true);
                showPostTrialPrompt = true;
                PauseForPostTrialPrompt();
            }

            if (GUI.Button(new Rect(contentX + buttonWidth + buttonGap, y, buttonWidth, buttonHeight), "Choose Scenario"))
                OpenOnboardingFromPostTrial();

            if (GUI.Button(new Rect(contentX + (buttonWidth + buttonGap) * 2f, y, buttonWidth, buttonHeight), "Run Again"))
                StartNextTrialFromPrompt();

            y += buttonHeight + 8f;

            int undoCount = runtimeEditorManager != null ? runtimeEditorManager.UndoCount : 0;
            int redoCount = runtimeEditorManager != null ? runtimeEditorManager.RedoCount : 0;
            bool hasSelection = runtimeEditorManager?.CurrentSelectedObject != null;

            GUI.enabled = undoCount > 0;
            if (GUI.Button(new Rect(contentX, y, buttonWidth, buttonHeight), $"Undo  [{undoCount}]  Ctrl+Z"))
                runtimeEditorManager?.UndoLastAction();

            GUI.enabled = redoCount > 0;
            if (GUI.Button(new Rect(contentX + buttonWidth + buttonGap, y, buttonWidth, buttonHeight), $"Redo  [{redoCount}]  Ctrl+Y"))
                runtimeEditorManager?.RedoLastAction();

            GUI.enabled = hasSelection;
            if (GUI.Button(new Rect(contentX + (buttonWidth + buttonGap) * 2f, y, buttonWidth, buttonHeight), "Delete  [Del]"))
                runtimeEditorManager?.DeleteSelectedObject();

            GUI.enabled = true;

            DrawWorldBuildingSpawnPalette();
        }

        private void DrawWorldBuildingSpawnPalette()
        {
            IReadOnlyList<WorldBuildingSpawnUiRow> rows = WorldBuildingSpawnLibrary.LastUiRows;
            if (rows == null || rows.Count == 0)
            {
                Rect panelRect = GetWorldBuildingSpawnPaletteRect(72f, 360f);
                GUI.Box(panelRect, "");
                GUI.Label(new Rect(panelRect.x + 12f, panelRect.y + 12f, panelRect.width - 24f, 48f),
                    "No world-building props: each prefab in Resources/WorldBuildingSpawns needs a matching thumbnail in Resources/WorldBuildingUI (see WorldBuildingSpawnLibrary).");
                return;
            }

            float panelWidth2 = Mathf.Min(540f, Screen.width - 32f);
            float headerH = 68f;
            float cardHeight = 96f;
            float gap = 10f;
            int cols = 3;
            int rowCount = (rows.Count + cols - 1) / cols;
            float scrollAreaMin = 120f;
            float scrollAreaMax = Mathf.Min(280f, Screen.height * 0.38f);
            float scrollInnerHeight = rowCount * (cardHeight + gap) + gap;
            float scrollViewportH = Mathf.Clamp(scrollInnerHeight, scrollAreaMin, scrollAreaMax);
            float panelHeight2 = headerH + scrollViewportH + 16f;
            Rect panelRect2 = GetWorldBuildingSpawnPaletteRect(panelHeight2, panelWidth2);

            GUI.Box(panelRect2, "");
            GUI.Label(new Rect(panelRect2.x + 14f, panelRect2.y + 10f, panelRect2.width - 28f, 22f), "Add Objects");
            GUI.Label(new Rect(panelRect2.x + 14f, panelRect2.y + 30f, panelRect2.width - 28f, 34f),
                "Only prefabs with a matching image in Resources/WorldBuildingUI are listed (others in WorldBuildingSpawns are ignored).");

            float innerPad = 12f;
            float scrollBarReserve = 18f;
            Rect viewRect = new Rect(
                panelRect2.x + innerPad,
                panelRect2.y + headerH,
                panelRect2.width - innerPad * 2f,
                scrollViewportH);
            float innerW = viewRect.width - scrollBarReserve;
            float cardW = (innerW - (cols - 1) * gap) / cols;
            float contentW = viewRect.width - scrollBarReserve;
            float contentH = Mathf.Max(scrollInnerHeight, viewRect.height);
            Rect contentRect = new Rect(0f, 0f, contentW, contentH);

            worldBuildingSpawnScroll = GUI.BeginScrollView(viewRect, worldBuildingSpawnScroll, contentRect, false, true);
            for (int i = 0; i < rows.Count; i++)
            {
                WorldBuildingSpawnUiRow row = rows[i];
                int r = i / cols;
                int c = i % cols;
                float x = c * (cardW + gap);
                float y = gap + r * (cardHeight + gap);
                DrawSpawnPreviewCard(new Rect(x, y, cardW, cardHeight), row.DisplayName, row.SpawnId, row.Thumbnail);
            }

            GUI.EndScrollView();
        }

        private bool IsMouseOverWorldBuildingUi()
        {
            Vector2 mouse = Input.mousePosition;
            float guiY = Screen.height - mouse.y;
            Vector2 guiPoint = new Vector2(mouse.x, guiY);

            if (GetWorldBuildingOverlayRect().Contains(guiPoint))
                return true;

            return GetWorldBuildingSpawnPaletteRect().Contains(guiPoint);
        }

        private static Rect GetWorldBuildingOverlayRect()
        {
            return new Rect(24f, 24f, 620f, 228f);
        }

        private static Rect GetWorldBuildingSpawnPaletteRect()
        {
            IReadOnlyList<WorldBuildingSpawnUiRow> rows = WorldBuildingSpawnLibrary.LastUiRows;
            if (rows == null || rows.Count == 0)
                return GetWorldBuildingSpawnPaletteRect(72f, 360f);

            float panelWidth = Mathf.Min(540f, Screen.width - 32f);
            float headerH = 68f;
            float cardHeight = 96f;
            float gap = 10f;
            int cols = 3;
            int rowCount = (rows.Count + cols - 1) / cols;
            float scrollAreaMin = 120f;
            float scrollAreaMax = Mathf.Min(280f, Screen.height * 0.38f);
            float scrollInnerHeight = rowCount * (cardHeight + gap) + gap;
            float scrollViewportH = Mathf.Clamp(scrollInnerHeight, scrollAreaMin, scrollAreaMax);
            float panelHeight = headerH + scrollViewportH + 16f;
            return GetWorldBuildingSpawnPaletteRect(panelHeight, panelWidth);
        }

        private static Rect GetWorldBuildingSpawnPaletteRect(float panelHeight, float panelWidth)
        {
            return new Rect(Screen.width - panelWidth - 24f, Screen.height - panelHeight - 24f, panelWidth, panelHeight);
        }

        private void DrawSpawnPreviewCard(Rect rect, string label, string spawnId, Texture2D thumbnail)
        {
            GUI.Box(rect, "");

            Rect previewRect = new Rect(rect.x + 12f, rect.y + 10f, rect.width - 24f, 44f);
            DrawWorldBuildingSpawnThumbnail(previewRect, thumbnail);

            GUI.Label(new Rect(rect.x + 10f, rect.y + 58f, rect.width - 20f, 18f), label);
            if (GUI.Button(new Rect(rect.x + 10f, rect.y + 74f, rect.width - 20f, 18f), $"Add {label}"))
                runtimeEditorManager?.SpawnObject(spawnId);
        }

        private static void DrawWorldBuildingSpawnThumbnail(Rect rect, Texture2D thumbnail)
        {
            Color previousColor = GUI.color;
            Texture2D backdrop = Texture2D.whiteTexture;

            GUI.color = new Color(0.12f, 0.15f, 0.19f, 0.95f);
            GUI.DrawTexture(rect, backdrop);

            if (thumbnail != null)
            {
                GUI.color = Color.white;
                GUI.DrawTexture(rect, thumbnail, ScaleMode.ScaleToFit, true);
            }

            GUI.color = previousColor;
        }

        private void HandleReviewPlaybackReachedEnd()
        {
            if (!inRewindMode)
                return;

            EndReviewAndShowNextStepMenu();
        }

        private void DrawTrialStartPrompt()
        {
            EnsureOnboardingStyles();

            bool noRosReady = IsTrialPreviewReadyWithoutRosBackend();

            float width = 520f;
            float height = allowStartWithoutRosBackend ? 312f : 260f;
            Rect rect = new Rect((Screen.width - width) * 0.5f, 24f, width, height);

            GUI.Box(rect, "");
            GUI.Label(new Rect(rect.x + 18f, rect.y + 16f, rect.width - 36f, 28f),
                trialStartReady ? "SESSION READY" : "LOADING SESSION");
            GUI.Label(new Rect(rect.x + 18f, rect.y + 50f, rect.width - 36f, 48f),
                trialStartReady
                    ? "Robot, PWD, and cameras are loaded. Start when you are ready."
                    : "Preparing robot, pedestrians, and camera view...");

            float controlsY = rect.y + 104f;
            GUI.Label(new Rect(rect.x + 18f, controlsY, 220f, 24f), "Robot Control", onboardingSectionStyle);
            if (DrawChipButton(new Rect(rect.x + 18f, controlsY + 30f, 110f, 34f), "Manual",
                selectedRobotStartupControl == StartupControlMode.Manual))
            {
                SetRobotStartupControl(StartupControlMode.Manual);
            }
            if (DrawChipButton(new Rect(rect.x + 136f, controlsY + 30f, 110f, 34f), "Auto",
                selectedRobotStartupControl == StartupControlMode.Auto))
            {
                SetRobotStartupControl(StartupControlMode.Auto);
            }

            GUI.Label(new Rect(rect.x + 272f, controlsY, 220f, 24f), "PWD Control", onboardingSectionStyle);
            if (DrawChipButton(new Rect(rect.x + 272f, controlsY + 30f, 110f, 34f), "Manual",
                selectedPwdStartupControl == StartupControlMode.Manual))
            {
                SetPwdStartupControl(StartupControlMode.Manual);
            }
            if (DrawChipButton(new Rect(rect.x + 390f, controlsY + 30f, 110f, 34f), "Auto",
                selectedPwdStartupControl == StartupControlMode.Auto))
            {
                SetPwdStartupControl(StartupControlMode.Auto);
            }

            GUI.Label(new Rect(rect.x + 18f, rect.y + 176f, rect.width - 36f, 36f),
                "Robot and PWD can be set independently. The opposite pairing is only the default recommendation.",
                onboardingHintStyle);

            if (allowStartWithoutRosBackend)
            {
                GUI.Label(new Rect(rect.x + 18f, rect.y + 212f, rect.width - 36f, 28f),
                    trialStartReady
                        ? "ROS-backed start is ready. You can still bypass ROS explicitly if needed."
                        : noRosReady
                            ? "No ROS backend? The scene is ready; only the live planner is missing."
                            : "Preparing non-ROS scene setup before a bypass start is allowed.",
                    onboardingHintStyle);
            }

            GUI.enabled = trialStartReady || bypassRosBackendForTrialStart;
            if (GUI.Button(new Rect(rect.x + 152f, rect.y + 214f, 218f, 36f),
                trialStartReady ? $"Start Trial [{startTrialKey}]" : "Loading..."))
            {
                StartTrialFromPrompt();
            }
            GUI.enabled = true;

            if (allowStartWithoutRosBackend)
            {
                GUI.enabled = trialStartReady || noRosReady;
                if (GUI.Button(new Rect(rect.x + 152f, rect.y + 258f, 218f, 32f), "Start Without ROS Backend"))
                {
                    StartTrialWithoutRosBackend();
                }
                GUI.enabled = true;
            }
        }

        private void PauseForPostTrialPrompt()
        {
            if (postTrialPromptPausedTime)
                return;

            savedTimeScale = Time.timeScale;
            Time.timeScale = 0f;
            postTrialPromptPausedTime = true;
        }

        private void HidePostTrialPrompt()
        {
            showPostTrialPrompt = false;

            if (!postTrialPromptPausedTime)
                return;

            Time.timeScale = savedTimeScale;
            postTrialPromptPausedTime = false;
        }

        private void CaptureReviewCameraForWorldBuilding()
        {
            if (!inRewindMode || rewindController == null)
                return;

            if (rewindController.CurrentPerspective != PerspectiveMode.TopDown)
            {
                hasPendingWorldBuildingCameraPose = false;
                return;
            }

            Camera activeCamera = rewindController.GetActiveReviewCamera();
            if (activeCamera == null || !activeCamera.orthographic)
            {
                hasPendingWorldBuildingCameraPose = false;
                return;
            }

            pendingWorldBuildingCameraPosition = activeCamera.transform.position;
            pendingWorldBuildingCameraRotation = Quaternion.Euler(90f, 0f, 0f);
            pendingWorldBuildingOrthoSize = activeCamera.orthographicSize;
            hasPendingWorldBuildingCameraPose = true;
        }

        private void ActivateWorldBuildingView()
        {
            var sean = SEAN.SEAN.instance;
            if (sean == null || sean.environment == null)
            {
                showPostTrialPrompt = true;
                PauseForPostTrialPrompt();
                return;
            }

            worldBuildingCamera = sean.environment.topViewCamera;
            if (worldBuildingCamera == null)
            {
                showPostTrialPrompt = true;
                PauseForPostTrialPrompt();
                return;
            }
            PrepareTopDownWorldBuildingCamera(worldBuildingCamera);

            if (hasPendingWorldBuildingCameraPose)
            {
                worldBuildingCamera.transform.position = pendingWorldBuildingCameraPosition;
                worldBuildingCamera.transform.rotation = pendingWorldBuildingCameraRotation;
                if (worldBuildingCamera.orthographic)
                    worldBuildingCamera.orthographicSize = pendingWorldBuildingOrthoSize;
            }

            if (!EnsureRuntimeEditorReady())
            {
                showPostTrialPrompt = true;
                PauseForPostTrialPrompt();
                return;
            }

            worldBuildingCamera.enabled = true;
            runtimeEditorPreviousRaycastCamera = runtimeEditorManager.ActiveRaycastCamera;
            runtimeEditorPreviousCameraController = runtimeEditorManager.cameraController;
            runtimeEditorManager.SetEditorCamera(worldBuildingCamera, worldBuildingCameraController);
            if (hasPendingWorldBuildingCameraPose)
            {
                FocusWorldBuildingCameraOnBounds(new Bounds(
                    new Vector3(pendingWorldBuildingCameraPosition.x, 0f, pendingWorldBuildingCameraPosition.z),
                    new Vector3(
                        Mathf.Max(pendingWorldBuildingOrthoSize * 2f, 10f),
                        1f,
                        Mathf.Max(pendingWorldBuildingOrthoSize * 2f, 10f))));
            }
            else
            {
                FocusWorldBuildingCameraOnCurrentScene();
                worldBuildingCameraController.SnapToTopDownView();
            }

            worldBuildingCameraController.SyncToCurrentTransform();

            runtimeEditorManager.suppressSpawnCanvas = true;
            // World building owns the editor lifecycle, so ESC/hotkey must not self-exit and tear down
            // the world-building camera. Exit is via the "Back To Menu" button instead.
            runtimeEditorManager.externalLifecycleControl = true;
            runtimeEditorManager.SetEditorMode(true);

            showPostTrialPrompt = false;
            showReviewCompletionPrompt = false;
            inWorldBuildingMode = true;
        }

        private void ExitWorldBuildingMode(bool restoreGameplayCameras = false)
        {
            inWorldBuildingMode = false;

            if (runtimeEditorManager != null)
            {
                runtimeEditorManager.suppressSpawnCanvas = false;
                runtimeEditorManager.externalLifecycleControl = false;

                if (runtimeEditorManager.isEditorActive)
                    runtimeEditorManager.SetEditorMode(false);

                runtimeEditorManager.SetEditorCamera(
                    runtimeEditorPreviousRaycastCamera != null ? runtimeEditorPreviousRaycastCamera : worldBuildingCamera,
                    runtimeEditorPreviousCameraController);
            }

            if (worldBuildingCamera != null)
            {
                worldBuildingCamera.enabled = false;
                if (hasWorldBuildingTargetDisplayOverride)
                    worldBuildingCamera.targetDisplay = worldBuildingPreviousTargetDisplay;
            }

            if (restoreGameplayCameras)
            {
                RestoreRobotGameplayCameras();
            }
            else if (worldBuildingPreviousMainCamera != null &&
                     worldBuildingPreviousMainCamera != worldBuildingCamera)
            {
                worldBuildingPreviousMainCamera.enabled = worldBuildingPreviousMainCameraEnabled;
            }
            else if (worldBuildingPreviousMainCamera == worldBuildingCamera &&
                     worldBuildingCamera != null)
            {
                worldBuildingCamera.enabled = worldBuildingPreviousMainCameraEnabled;
            }

            runtimeEditorPreviousRaycastCamera = null;
            runtimeEditorPreviousCameraController = null;
            worldBuildingCameraController = null;
            worldBuildingCamera = null;
            hasPendingWorldBuildingCameraPose = false;
            worldBuildingPreviousMainCamera = null;
            hasWorldBuildingTargetDisplayOverride = false;

            RestoreWorldBuildingCameraBehaviours();
        }

        private void ResetControlledMotion()
        {
            var sean = SEAN.SEAN.instance;
            if (sean == null)
                return;

            var velocityController = FindObjectOfType<SEAN.Control.VelocityController>();
            if (velocityController != null)
                velocityController.ResetMotionState();

            if (sean.robot != null && sean.robot.base_link != null)
                ZeroRigidbodies(sean.robot.base_link.transform);

            if (sean.player != null)
                ZeroRigidbodies(sean.player.transform);

            var manualWheelchair = FindPwdPlayerControllerIncludingInactive();
            if (manualWheelchair != null)
                manualWheelchair.ResetToSpawn();
        }

        private static IVI.ManualWheelchairController FindPwdPlayerControllerIncludingInactive()
        {
            IVI.ManualWheelchairController fallback = null;
            foreach (var controller in FindObjectsOfType<IVI.ManualWheelchairController>(true))
            {
                if (controller == null)
                    continue;

                if (controller.gameObject != null && controller.gameObject.name == "PWDPlayer")
                    return controller;

                if (fallback == null)
                    fallback = controller;
            }

            return fallback;
        }

        private static void ZeroRigidbodies(Transform root)
        {
            if (root == null)
                return;

            foreach (var rb in root.GetComponentsInChildren<Rigidbody>(true))
            {
                if (rb == null)
                    continue;

                rb.velocity = new Vector3(0f, rb.velocity.y, 0f);
                rb.angularVelocity = Vector3.zero;
            }
        }

        private void PrepareTopDownWorldBuildingCamera(Camera cameraToUse)
        {
            worldBuildingPreviousMainCamera = Camera.main;
            worldBuildingPreviousMainCameraEnabled = worldBuildingPreviousMainCamera != null &&
                                                   worldBuildingPreviousMainCamera.enabled;
            if (worldBuildingPreviousMainCamera != null &&
                worldBuildingPreviousMainCamera != cameraToUse)
            {
                worldBuildingPreviousMainCamera.enabled = false;
            }

            worldBuildingPreviousTargetDisplay = cameraToUse.targetDisplay;
            hasWorldBuildingTargetDisplayOverride = true;
            cameraToUse.targetDisplay = 0;
            cameraToUse.orthographic = true;
            cameraToUse.nearClipPlane = 0.05f;
            cameraToUse.farClipPlane = Mathf.Max(cameraToUse.farClipPlane, 2000f);
            cameraToUse.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
        }

        private void FocusWorldBuildingCameraOnCurrentScene()
        {
            Bounds focusBounds;
            if (TryGetWorldBuildingFocusBounds(out focusBounds))
            {
                FocusWorldBuildingCameraOnBounds(focusBounds);
                return;
            }

            if (worldBuildingCamera == null)
                return;

            Vector3 fallbackCenter = Vector3.zero;
            float fallbackExtent = 30f;
            worldBuildingCamera.transform.position = new Vector3(fallbackCenter.x, fallbackExtent, fallbackCenter.z);
            worldBuildingCamera.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            worldBuildingCamera.orthographic = true;
            worldBuildingCamera.orthographicSize = fallbackExtent * 0.5f;
        }

        private bool TryGetWorldBuildingFocusBounds(out Bounds focusBounds)
        {
            Bounds roi = ReviewRoiExporter.ApplySettings(reviewExportEnvelope, reviewExportSettings);
            if (roi.size.x > 0.1f && roi.size.z > 0.1f)
            {
                focusBounds = roi;
                return true;
            }

            var sean = SEAN.SEAN.instance;
            if (sean != null && sean.environment != null && sean.environment.environment != null)
            {
                Renderer[] renderers = sean.environment.environment.GetComponentsInChildren<Renderer>(true);
                bool hasBounds = false;
                Bounds combined = default;
                foreach (Renderer renderer in renderers)
                {
                    if (renderer == null || !renderer.enabled)
                        continue;

                    if (!hasBounds)
                    {
                        combined = renderer.bounds;
                        hasBounds = true;
                    }
                    else
                    {
                        combined.Encapsulate(renderer.bounds);
                    }
                }

                if (hasBounds)
                {
                    focusBounds = combined;
                    return true;
                }
            }

            focusBounds = default;
            return false;
        }

        private void FocusWorldBuildingCameraOnBounds(Bounds bounds)
        {
            if (worldBuildingCamera == null)
                return;

            float aspect = worldBuildingCamera.aspect > 0.01f
                ? worldBuildingCamera.aspect
                : Mathf.Max(1f, (float)Screen.width / Mathf.Max(1, Screen.height));
            float paddedWidth = Mathf.Max(bounds.size.x, 8f) + 6f;
            float paddedDepth = Mathf.Max(bounds.size.z, 8f) + 6f;
            float orthographicSize = Mathf.Max(paddedDepth * 0.5f, paddedWidth / Mathf.Max(aspect, 0.01f) * 0.5f);
            float cameraHeight = Mathf.Max(bounds.max.y + 20f, bounds.center.y + orthographicSize + 10f);

            worldBuildingCamera.transform.position = new Vector3(bounds.center.x, cameraHeight, bounds.center.z);
            worldBuildingCamera.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            worldBuildingCamera.orthographic = true;
            worldBuildingCamera.orthographicSize = Mathf.Max(orthographicSize, 8f);
        }

        /// <summary>
        /// Finds a scene RuntimeEditorManager even if its GameObject is inactive (FindObjectOfType skips inactive in 2020).
        /// Avoids creating a duplicate: a second instance is destroyed in Awake, leaving a broken reference and an empty spawn list.
        /// </summary>
        private RuntimeEditorManager FindRuntimeEditorManagerForWorldBuilding()
        {
            RuntimeEditorManager singleton = RuntimeEditorManager.Instance;
            if (singleton != null)
                return singleton;

            foreach (RuntimeEditorManager candidate in Resources.FindObjectsOfTypeAll<RuntimeEditorManager>())
            {
                if (candidate == null)
                    continue;
                GameObject go = candidate.gameObject;
                if (!go.scene.IsValid())
                    continue;
                return candidate;
            }

            return null;
        }

        /// <summary>
        /// Sync spawn list from Resources/WorldBuildingSpawns so RuntimeEditorManager and IMGUI palette stay aligned.
        /// </summary>
        private void EnsureWorldBuildingSpawnPrefabsConfigured()
        {
            if (runtimeEditorManager == null)
                return;

            WorldBuildingSpawnLibrary.RefreshFromResources();
            IReadOnlyList<SpawnableObject> built = WorldBuildingSpawnLibrary.LastSpawnables;
            if (built == null || built.Count == 0)
            {
                Debug.LogWarning("[SessionReview] World building: no prefabs with UI thumbnails (pair WorldBuildingSpawns prefabs + WorldBuildingUI textures).");
                if (runtimeEditorManager.spawnableObjects == null)
                    runtimeEditorManager.spawnableObjects = new List<SpawnableObject>();
                else
                    runtimeEditorManager.spawnableObjects.Clear();
                return;
            }

            if (runtimeEditorManager.spawnableObjects == null)
                runtimeEditorManager.spawnableObjects = new List<SpawnableObject>();
            runtimeEditorManager.spawnableObjects.Clear();
            foreach (SpawnableObject entry in built)
            {
                if (entry == null || entry.prefab == null)
                    continue;
                runtimeEditorManager.spawnableObjects.Add(new SpawnableObject
                {
                    id = entry.id,
                    prefab = entry.prefab,
                    spawnButton = null
                });
            }
        }

        private bool EnsureRuntimeEditorReady()
        {
            runtimeEditorManager = FindRuntimeEditorManagerForWorldBuilding();
            if (runtimeEditorManager == null)
            {
                GameObject runtimeEditorObject = new GameObject("RuntimeEditorManager");
                runtimeEditorManager = runtimeEditorObject.AddComponent<RuntimeEditorManager>();
            }

            if (!runtimeEditorManager.gameObject.activeInHierarchy)
                runtimeEditorManager.gameObject.SetActive(true);

            EnsureWorldBuildingSpawnPrefabsConfigured();

            if (PauseManager.Instance == null)
            {
                GameObject pauseManagerObject = new GameObject("PauseManager");
                pauseManagerObject.AddComponent<PauseManager>();
            }

            if (worldBuildingCamera == null)
                return false;

            worldBuildingCameraController = worldBuildingCamera.GetComponent<SimpleCameraController>();
            if (worldBuildingCameraController == null)
                worldBuildingCameraController = worldBuildingCamera.gameObject.AddComponent<SimpleCameraController>();

            Cameramovement legacyCameraMovement = worldBuildingCamera.GetComponent<Cameramovement>();
            if (legacyCameraMovement != null)
                CacheAndDisableWorldBuildingBehaviour(legacyCameraMovement);

            foreach (IVI.CameraScript cameraScript in FindObjectsOfType<IVI.CameraScript>(true))
                CacheAndDisableWorldBuildingBehaviour(cameraScript);

            foreach (IVI.WheelchairCameraSmoothing smoothing in FindObjectsOfType<IVI.WheelchairCameraSmoothing>(true))
                CacheAndDisableWorldBuildingBehaviour(smoothing);

            worldBuildingCameraController.enabled = true;
            return true;
        }

        private void CacheAndDisableWorldBuildingBehaviour(Behaviour behaviour)
        {
            if (behaviour == null || worldBuildingDisabledBehaviours.ContainsKey(behaviour))
                return;

            worldBuildingDisabledBehaviours[behaviour] = behaviour.enabled;
            behaviour.enabled = false;
        }

        private void RestoreWorldBuildingCameraBehaviours()
        {
            foreach (var kvp in worldBuildingDisabledBehaviours)
            {
                if (kvp.Key != null)
                    kvp.Key.enabled = kvp.Value;
            }

            worldBuildingDisabledBehaviours.Clear();
        }

        private void ShowTrialStartPrompt()
        {
            showTrialStartPrompt = true;
            bypassRosBackendForTrialStart = false;
            var planVisualizer = FindObjectOfType<SEAN.Display.PlanVisualizer>();
            if (planVisualizer != null)
                planVisualizer.ClearCurrentPlan();

            if (IsTrialPreviewReady())
            {
                trialStartReady = true;
                trialWarmupPending = false;

                if (!trialStartPromptPausedTime)
                {
                    savedTimeScale = Time.timeScale;
                    Time.timeScale = 0f;
                    trialStartPromptPausedTime = true;
                }

                return;
            }

            trialStartReady = false;
            trialWarmupPending = true;
            trialWarmupDelayFrames = 0;
            trialWarmupGoalRepublishFrames = 0;
            ProcessTrialWarmup();
        }

        private void ProcessTrialWarmup()
        {
            if ((!showTrialStartPrompt && !showOnboarding) || !trialWarmupPending)
                return;

            if (trialWarmupDelayFrames > 0)
            {
                trialWarmupDelayFrames--;
                return;
            }

            var sean = SEAN.SEAN.instance;
            if (sean == null || sean.robotTask == null)
                return;

            SessionOnboardingSettings.SyncInFlightTrialControls(
                selectedPlayerMode,
                selectedRobotStartupControl,
                selectedPwdStartupControl);

            ApplyStartupControlDefaults();

            if (!sean.robotTask.hasPreparedTaskPreview)
            {
                sean.robotTask.PrepareTaskPreview();
            }

            if (requirePlanBeforeTrialStart && !bypassRosBackendForTrialStart && sean.robotTask.hasPreparedTaskPreview)
            {
                if (trialWarmupGoalRepublishFrames > 0)
                {
                    trialWarmupGoalRepublishFrames--;
                }
                else
                {
                    sean.robotTask.RepublishPreviewGoal();
                    trialWarmupGoalRepublishFrames = 20;
                }
            }

            if (IsTrialPreviewReady())
            {
                trialStartReady = true;
                trialWarmupPending = false;
                trialWarmupGoalRepublishFrames = 0;

                if (showTrialStartPrompt && !trialStartPromptPausedTime)
                {
                    savedTimeScale = Time.timeScale;
                    Time.timeScale = 0f;
                    trialStartPromptPausedTime = true;
                }
            }
        }

        private bool IsTrialPreviewReady()
        {
            return IsTrialPreviewReadyCore(requireRosPlan: true);
        }

        private bool IsTrialPreviewReadyWithoutRosBackend()
        {
            return IsTrialPreviewReadyCore(requireRosPlan: false);
        }

        private bool IsTrialPreviewReadyCore(bool requireRosPlan)
        {
            var sean = SEAN.SEAN.instance;
            if (sean == null || sean.robotTask == null || !sean.robotTask.hasPreparedTaskPreview)
                return false;

            var navManager = FindObjectOfType<IVI.NavManager>();
            if (navManager != null && navManager.gameObject.activeInHierarchy)
            {
                navManager.EnsureInitialized();
                if (navManager.allAgents == null || navManager.allAgents.Length == 0)
                    return false;
            }

            if (requirePlanBeforeTrialStart && requireRosPlan && !bypassRosBackendForTrialStart)
            {
                var planVisualizer = FindObjectOfType<SEAN.Display.PlanVisualizer>();
                if (planVisualizer == null)
                    return false;

                Vector3[] currentPlan = planVisualizer.GetCurrentPlanPositions();
                if (currentPlan == null || currentPlan.Length < 2)
                    return false;
            }

            return true;
        }

        private void ApplyStartupControlDefaults()
        {
            var velocityController = FindObjectOfType<SEAN.Control.VelocityController>();
            if (velocityController != null)
            {
                bool robotManual = selectedRobotStartupControl == StartupControlMode.Manual;
                velocityController.startInManualMode = robotManual;
                velocityController.SetManualControlActive(robotManual);
            }

            var pwdControllers = FindObjectsOfType<IVI.ManualWheelchairController>(true);
            bool pwdManual = selectedPwdStartupControl == StartupControlMode.Manual;
            bool allowPwdMovement = ShouldAllowPwdMovement();
            foreach (var pwdController in pwdControllers)
            {
                if (pwdController == null)
                    continue;

                pwdController.startInManualMode = pwdManual;
                if (allowPwdMovement)
                    UnfreezePwdController(pwdController, pwdManual);
                else
                    FreezePwdController(pwdController);
            }

            bool usePwdAsMainCamera =
                selectedPlayerMode == OnboardingPlayerMode.Human &&
                selectedPwdStartupControl == StartupControlMode.Manual;

            if (usePwdAsMainCamera)
                ActivatePwdCameraAsMain();
            else
                RestoreRobotGameplayCameras();
        }

        private bool ShouldAllowPwdMovement()
        {
            var sean = SEAN.SEAN.instance;
            return !showOnboarding &&
                   !showTrialStartPrompt &&
                   !trialWarmupPending &&
                   sean != null &&
                   sean.robotTask != null &&
                   sean.robotTask.isRunning;
        }

        private static void FreezePwdController(IVI.ManualWheelchairController pwdController)
        {
            if (pwdController == null)
                return;

            var sfpwdAgent = pwdController.GetComponent<IVI.SFPWDAgent>();
            if (sfpwdAgent != null)
            {
                sfpwdAgent.KillNavigationCoroutine();
                sfpwdAgent.enabled = false;
            }

            foreach (var rb in pwdController.GetComponentsInChildren<Rigidbody>(true))
            {
                if (rb == null)
                    continue;

                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }

            pwdController.enabled = false;
        }

        private static void UnfreezePwdController(IVI.ManualWheelchairController pwdController, bool pwdManual)
        {
            if (pwdController == null)
                return;

            if (!pwdController.enabled)
                pwdController.enabled = true;

            pwdController.ApplyStartupControlMode(pwdManual);
        }

        private static readonly HashSet<string> pwdCameraNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "wheelchairCamera", "PWDThirdPersonCamera", "PWDFirstPersonCamera"
        };

        private static bool IsPwdCamera(Camera cam)
        {
            if (cam == null) return false;
            if (pwdCameraNames.Contains(cam.name)) return true;
            if (cam.GetComponent<IVI.WheelchairCameraSmoothing>() != null) return true;
            if (cam.GetComponent<IVI.CameraScript>() != null)
            {
                var pwdPlayer = cam.transform.root;
                if (pwdPlayer != null && pwdPlayer.name == "PWDPlayer")
                    return true;
            }
            return false;
        }

        private Camera FindPwdCamera()
        {
            foreach (var smoothing in FindObjectsOfType<IVI.WheelchairCameraSmoothing>())
            {
                var cam = smoothing != null ? smoothing.GetComponent<Camera>() : null;
                if (cam != null) return cam;
            }

            foreach (Camera cam in Camera.allCameras)
            {
                if (IsPwdCamera(cam))
                    return cam;
            }

            GameObject pwdPlayer = GameObject.Find("PWDPlayer");
            if (pwdPlayer != null)
            {
                foreach (var cam in pwdPlayer.GetComponentsInChildren<Camera>(true))
                    return cam;
            }

            return null;
        }

        private void RestoreRobotGameplayCameras()
        {
            DestroyLegacyStandaloneMainCamera();

            // Disable ALL PWD cameras (including disabled ones that allCameras would miss)
            foreach (Camera cam in FindObjectsOfType<Camera>(true))
            {
                if (IsPwdCamera(cam))
                {
                    cam.enabled = false;
                    cam.targetDisplay = 1;
                }
            }

            foreach (var smoothing in FindObjectsOfType<IVI.WheelchairCameraSmoothing>(true))
            {
                if (smoothing == null) continue;
                var cam = smoothing.GetComponent<Camera>();
                if (cam != null)
                {
                    cam.enabled = false;
                    cam.targetDisplay = 1;
                }
            }

            var sean = SEAN.SEAN.instance;
            if (sean?.robot == null)
            {
                Debug.LogWarning("[SessionReview] RestoreRobotGameplayCameras: SEAN robot is null");
                return;
            }

            void EnableRobotCam(Camera cam, string label)
            {
                if (cam == null)
                {
                    Debug.LogWarning($"[SessionReview] RestoreRobotGameplayCameras: {label} is null");
                    return;
                }
                cam.gameObject.SetActive(true);
                cam.enabled = true;
                cam.targetDisplay = 0;
            }

            EnableRobotCam(sean.robot.camera_first, "camera_first");
            EnableRobotCam(sean.robot.camera_third, "camera_third");
        }

        private void DestroyLegacyStandaloneMainCamera()
        {
            Camera legacyMain = Camera.main;
            if (legacyMain == null)
                return;

            // Never destroy the SessionReviewManager's own GameObject (which also hosts the rewindCamera)
            if (legacyMain.gameObject == gameObject)
                return;

            var sean = SEAN.SEAN.instance;
            if (IsManagedGameplayCamera(legacyMain, sean))
                return;

            Debug.Log($"[SessionReview] Destroying legacy standalone main camera '{legacyMain.name}'");
            Destroy(legacyMain.gameObject);
        }

        private bool IsManagedGameplayCamera(Camera cam, SEAN.SEAN sean)
        {
            if (cam == null)
                return false;

            if (IsPwdCamera(cam))
                return true;

            if (worldBuildingCamera != null && cam == worldBuildingCamera)
                return true;

            if (sean?.environment?.topViewCamera != null && cam == sean.environment.topViewCamera)
                return true;

            if (sean?.robot != null)
            {
                if (cam == sean.robot.camera_first ||
                    cam == sean.robot.camera_third ||
                    cam == sean.robot.camera_overhead)
                {
                    return true;
                }
            }

            return false;
        }

        private void ActivatePwdCameraAsMain()
        {
            Camera pwdCam = FindPwdCamera();
            if (pwdCam == null)
            {
                Debug.LogWarning("[SessionReview] ActivatePwdCameraAsMain: no PWD camera found");
                return;
            }

            // First enable the PWD camera
            pwdCam.targetDisplay = 0;
            pwdCam.enabled = true;
            pwdCam.gameObject.SetActive(true);

            // Disable ALL other cameras (including disabled ones, using FindObjectsOfType)
            foreach (Camera cam in FindObjectsOfType<Camera>(true))
            {
                if (cam == pwdCam)
                    continue;
                cam.enabled = false;
            }

            Debug.Log($"[SessionReview] Activated PWD camera '{pwdCam.name}' as main view");
        }

        private void InitializeOnboardingSelection()
        {
            selectedPlayerMode = SessionOnboardingSettings.PlayerMode;
            selectedRobotStartupControl = SessionOnboardingSettings.RobotStartupControl;
            selectedPwdStartupControl = SessionOnboardingSettings.PwdStartupControl;
            selectedPwdGender = SessionOnboardingSettings.SelectedPwdGender;

            var sceneChange = FindObjectOfType<SceneChange>();
            if (sceneChange != null && sceneChange.SceneCount > 0)
            {
                int preferredIndex = SessionOnboardingSettings.SelectedSceneIndex;
                if (preferredIndex < 0 || preferredIndex >= sceneChange.SceneCount)
                    preferredIndex = sceneChange.CurrentSceneIndex;

                selectedSceneIndex = preferredIndex;
            }
            else
            {
                selectedSceneIndex = 0;
            }

            RefreshOnboardingWarmupState();
        }

        private void SetOnboardingVisible(bool visible)
        {
            if (showOnboarding == visible)
                return;

            showOnboarding = visible;

            if (showOnboarding)
            {
                onboardingSavedTimeScale = Time.timeScale;
                onboardingPausedTime = false;
                bypassRosBackendForTrialStart = false;
                RefreshOnboardingWarmupState();
            }
            else if (onboardingPausedTime)
            {
                Time.timeScale = onboardingSavedTimeScale;
                onboardingPausedTime = false;
            }
        }

        private void DrawOnboardingUI()
        {
            EnsureOnboardingStyles();

            float panelWidth = Mathf.Min(Screen.width * 0.78f, 1100f);
            float panelHeight = Mathf.Min(Screen.height * 0.88f, 860f);
            float panelX = (Screen.width - panelWidth) * 0.5f;
            float panelY = (Screen.height - panelHeight) * 0.5f;
            Rect panelRect = new Rect(panelX, panelY, panelWidth, panelHeight);

            GUI.color = new Color(0f, 0f, 0f, 0.6f);
            GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), Texture2D.whiteTexture);
            GUI.color = Color.white;

            GUI.Box(panelRect, GUIContent.none, onboardingPanelStyle);

            float x = panelRect.x + 36f;
            float y = panelRect.y + 30f;
            float innerWidth = panelRect.width - 72f;

            GUI.Label(new Rect(x, y, innerWidth, 42f), "Session Onboarding", onboardingTitleStyle);
            y += 50f;

            GUI.Label(new Rect(x, y, innerWidth, 56f),
                "Choose who is playing, pick the PWD player gender when human control is enabled, and select the session scene to launch.",
                onboardingBodyStyle);
            y += 72f;
            var sceneChange = FindObjectOfType<SceneChange>();
            float footerTop = panelRect.y + panelRect.height - 86f;
            float scrollTop = y;
            float scrollHeight = Mathf.Max(180f, footerTop - scrollTop - 18f);
            Rect scrollRect = new Rect(x, scrollTop, innerWidth, scrollHeight);
            float contentHeight = GetOnboardingContentHeight(sceneChange);
            Rect viewRect = new Rect(0f, 0f, innerWidth - 18f, contentHeight);

            onboardingContentScroll = GUI.BeginScrollView(scrollRect, onboardingContentScroll, viewRect);
            DrawOnboardingContent(viewRect.width, sceneChange);
            GUI.EndScrollView();

            bool canWarmupNow = CanWarmupCurrentSelectionInActiveScene();
            string actionLabel = SessionOnboardingSettings.HasCompletedOnboarding ? "Apply and Reload" : "Start Session";
            if (GUI.Button(new Rect(panelRect.x + panelRect.width - 264f, panelRect.y + panelRect.height - 74f, 228f, 50f),
                actionLabel, onboardingPrimaryButtonStyle))
            {
                ApplyOnboardingSelection();
            }

            if (canWarmupNow)
            {
                string preloadStatus = trialStartReady
                    ? "Trajectory preload is ready. You can enter the session page now."
                    : "Trajectory preload has started in the background to shorten the wait later.";
                GUI.Label(new Rect(panelRect.x + panelRect.width - 500f, panelRect.y + panelRect.height - 110f, 464f, 28f),
                    preloadStatus,
                    onboardingHintStyle);
            }

            if (SessionOnboardingSettings.HasCompletedOnboarding)
            {
                if (GUI.Button(new Rect(panelRect.x + 36f, panelRect.y + panelRect.height - 74f, 144f, 50f),
                    "Close", onboardingSecondaryButtonStyle))
                {
                    SetOnboardingVisible(false);
                }
            }
            else
            {
                GUI.Label(new Rect(panelRect.x + 36f, panelRect.y + panelRect.height - 60f, 340f, 24f),
                    $"[{onboardingToggleKey}] opens this panel later.",
                    onboardingHintStyle);
            }
        }

        private float GetOnboardingContentHeight(SceneChange sceneChange)
        {
            float height = 0f;
            height += 42f + 46f + 20f;

            if (selectedPwdStartupControl == StartupControlMode.Manual)
            {
                height += 42f + 248f + 18f;
                height += 42f + 150f + 22f;
            }

            height += 40f;

            int sceneCount = sceneChange != null ? sceneChange.SceneCount : 0;
            float sceneHeight = sceneCount > 0
                ? Mathf.Max(160f, sceneCount * 48f + 8f)
                : 160f;

            height += 30f + sceneHeight;
            return height + 8f;
        }

        private void DrawOnboardingContent(float width, SceneChange sceneChange)
        {
            float x = 0f;
            float y = 0f;

            GUI.Label(new Rect(x, y, 260f, 30f), "Who Is Playing?", onboardingSectionStyle);
            y += 42f;

            if (DrawChipButton(new Rect(x, y, 180f, 46f), "Robot", selectedPlayerMode == OnboardingPlayerMode.Robot))
                ApplyRecommendedStartupControlsForPlayerMode(OnboardingPlayerMode.Robot);
            if (DrawChipButton(new Rect(x + 196f, y, 180f, 46f), "Human", selectedPlayerMode == OnboardingPlayerMode.Human))
                ApplyRecommendedStartupControlsForPlayerMode(OnboardingPlayerMode.Human);
            y += 66f;

            if (selectedPwdStartupControl == StartupControlMode.Manual)
            {
                GUI.Label(new Rect(x, y, width, 30f), "PWD Player Gender", onboardingSectionStyle);
                y += 42f;

                DrawGenderPreviewCard(new Rect(x, y, 220f, 196f), "Male", maleWheelchairPreview,
                    selectedPwdGender == SEAN.Scenario.Agents.PwdGender.Male,
                    () => selectedPwdGender = SEAN.Scenario.Agents.PwdGender.Male);

                DrawGenderPreviewCard(new Rect(x + 236f, y, 220f, 196f), "Female", femaleWheelchairPreview,
                    selectedPwdGender == SEAN.Scenario.Agents.PwdGender.Female,
                    () => selectedPwdGender = SEAN.Scenario.Agents.PwdGender.Female);

                if (DrawChipButton(new Rect(x, y + 208f, 160f, 40f), "Male", selectedPwdGender == SEAN.Scenario.Agents.PwdGender.Male))
                    selectedPwdGender = SEAN.Scenario.Agents.PwdGender.Male;
                if (DrawChipButton(new Rect(x + 176f, y + 208f, 160f, 40f), "Female", selectedPwdGender == SEAN.Scenario.Agents.PwdGender.Female))
                    selectedPwdGender = SEAN.Scenario.Agents.PwdGender.Female;
                y += 266f;

                GUI.Label(new Rect(x, y, width, 30f), "Other Community-Informed Characters", onboardingSectionStyle);
                y += 42f;

                DrawPreviewCard(new Rect(x, y, 188f, 150f), "Dogwalker", dogwalkerPreview,
                    "Shown here for UI preview only.");
                DrawPreviewCard(new Rect(x + 204f, y, 188f, 150f), "Scooter User", scooterUserPreview,
                    "Shown here for UI preview only.");
                DrawPreviewCard(new Rect(x + 408f, y, 188f, 150f), "More To Be Built", null,
                    "Additional characters coming soon.");
                y += 172f;
            }

            GUI.Label(new Rect(x, y, width, 30f), "Session To Play", onboardingSectionStyle);
            y += 40f;

            int sceneCount = sceneChange != null ? sceneChange.SceneCount : 0;
            float sceneHeight = sceneCount > 0
                ? Mathf.Max(160f, sceneCount * 48f + 8f)
                : 160f;
            DrawSceneSelection(new Rect(x, y, width, sceneHeight));
        }

        private void DrawSceneSelection(Rect rect)
        {
            var sceneChange = FindObjectOfType<SceneChange>();
            if (sceneChange == null || sceneChange.SceneCount == 0)
            {
                GUI.Box(rect, GUIContent.none, onboardingSceneButtonStyle);
                GUI.Label(new Rect(rect.x + 20f, rect.y + 20f, rect.width - 40f, 32f),
                    "No SceneChange component with configured scenes was found.",
                    onboardingBodyStyle);
                return;
            }

            if (selectedSceneIndex < 0 || selectedSceneIndex >= sceneChange.SceneCount)
                selectedSceneIndex = sceneChange.CurrentSceneIndex;

            float totalHeight = sceneChange.SceneCount * 48f;
            if (totalHeight <= rect.height)
            {
                for (int i = 0; i < sceneChange.SceneCount; i++)
                {
                    Rect rowRect = new Rect(rect.x, rect.y + i * 48f, rect.width, 40f);
                    bool isActive = i == selectedSceneIndex;
                    string label = $"{i + 1}. {sceneChange.SceneNames[i]}";
                    if (GUI.Button(rowRect, label, isActive ? onboardingSceneActiveButtonStyle : onboardingSceneButtonStyle))
                    {
                        selectedSceneIndex = i;
                        RefreshOnboardingWarmupState();
                    }
                }

                return;
            }

            Rect viewRect = new Rect(0f, 0f, rect.width - 18f, totalHeight);
            onboardingSceneScroll = GUI.BeginScrollView(rect, onboardingSceneScroll, viewRect);

            for (int i = 0; i < sceneChange.SceneCount; i++)
            {
                Rect rowRect = new Rect(0f, i * 48f, viewRect.width, 40f);
                bool isActive = i == selectedSceneIndex;
                string label = $"{i + 1}. {sceneChange.SceneNames[i]}";
                if (GUI.Button(rowRect, label, isActive ? onboardingSceneActiveButtonStyle : onboardingSceneButtonStyle))
                {
                    selectedSceneIndex = i;
                    RefreshOnboardingWarmupState();
                }
            }

            GUI.EndScrollView();
        }

        private bool DrawChipButton(Rect rect, string label, bool active)
        {
            return GUI.Button(rect, label, active ? onboardingChipActiveStyle : onboardingChipStyle);
        }

        private void DrawGenderPreviewCard(Rect rect, string label, Texture2D preview, bool active, System.Action onClick)
        {
            GUI.Box(rect, GUIContent.none, active ? onboardingSceneActiveButtonStyle : onboardingSceneButtonStyle);

            Rect imageRect = new Rect(rect.x + 12f, rect.y + 12f, rect.width - 24f, rect.height - 58f);
            if (preview != null)
            {
                GUI.DrawTexture(imageRect, preview, ScaleMode.ScaleToFit, true);
            }
            else
            {
                GUI.Label(imageRect, "Preview not found", onboardingHintStyle);
            }

            GUI.Label(new Rect(rect.x + 12f, rect.yMax - 36f, rect.width - 24f, 24f), label, onboardingPreviewLabelStyle);

            if (GUI.Button(rect, GUIContent.none, GUIStyle.none))
                onClick?.Invoke();
        }

        private void DrawPreviewCard(Rect rect, string label, Texture2D preview, string helperText)
        {
            GUI.Box(rect, GUIContent.none, onboardingSceneButtonStyle);

            Rect imageRect = new Rect(rect.x + 12f, rect.y + 12f, rect.width - 24f, rect.height - 72f);
            if (preview != null)
            {
                GUI.DrawTexture(imageRect, preview, ScaleMode.ScaleToFit, true);
            }
            else
            {
                GUI.Label(imageRect, "Preview not found", onboardingHintStyle);
            }

            GUI.Label(new Rect(rect.x + 12f, rect.yMax - 50f, rect.width - 24f, 22f), label, onboardingPreviewLabelStyle);
            GUI.Label(new Rect(rect.x + 12f, rect.yMax - 30f, rect.width - 24f, 22f), helperText, onboardingHintStyle);
        }

        private void ApplyOnboardingSelection()
        {
            var sceneChange = FindObjectOfType<SceneChange>();
            string currentSceneName = SceneManager.GetActiveScene().name;
            string targetSceneName = SceneManager.GetActiveScene().name;
            int targetSceneIndex = selectedSceneIndex;

            if (sceneChange != null && sceneChange.SceneCount > 0)
            {
                targetSceneIndex = Mathf.Clamp(selectedSceneIndex, 0, sceneChange.SceneCount - 1);
                targetSceneName = sceneChange.SceneNames[targetSceneIndex];
            }
            else
            {
                targetSceneIndex = 0;
            }

            SessionOnboardingSettings.Apply(
                selectedPlayerMode,
                selectedPwdGender,
                targetSceneIndex,
                targetSceneName,
                selectedRobotStartupControl,
                selectedPwdStartupControl);
            SetOnboardingVisible(false);

            if (sceneChange != null && sceneChange.SceneCount > 0)
            {
                if (targetSceneName == currentSceneName)
                {
                    ShowTrialStartPrompt();
                    return;
                }

                sceneChange.LoadSceneAtIndex(targetSceneIndex);
                return;
            }

            if (targetSceneName == currentSceneName)
            {
                ShowTrialStartPrompt();
                return;
            }

            SceneManager.LoadScene(targetSceneName);
        }

        private void SetRobotStartupControl(StartupControlMode mode)
        {
            selectedRobotStartupControl = mode;
            RefreshOnboardingWarmupState();
            ApplyStartupControlsIfTrialPromptVisible();
        }

        private void SetPwdStartupControl(StartupControlMode mode)
        {
            selectedPwdStartupControl = mode;
            RefreshOnboardingWarmupState();
            ApplyStartupControlsIfTrialPromptVisible();
        }

        private void ApplyRecommendedStartupControlsForPlayerMode(OnboardingPlayerMode mode)
        {
            selectedPlayerMode = mode;
            selectedRobotStartupControl = mode == OnboardingPlayerMode.Robot
                ? StartupControlMode.Manual
                : StartupControlMode.Auto;
            selectedPwdStartupControl = mode == OnboardingPlayerMode.Human
                ? StartupControlMode.Manual
                : StartupControlMode.Auto;
            RefreshOnboardingWarmupState();
            ApplyStartupControlsIfTrialPromptVisible();
        }

        private void ApplyStartupControlsIfTrialPromptVisible()
        {
            if (!showTrialStartPrompt)
                return;
            ApplyStartupControlDefaults();
        }

        private bool CanWarmupCurrentSelectionInActiveScene()
        {
            string activeSceneName = SceneManager.GetActiveScene().name;
            string targetSceneName = activeSceneName;

            var sceneChange = FindObjectOfType<SceneChange>();
            if (sceneChange != null && sceneChange.SceneCount > 0)
            {
                if (selectedSceneIndex < 0 || selectedSceneIndex >= sceneChange.SceneCount)
                    return false;

                targetSceneName = sceneChange.SceneNames[selectedSceneIndex];
            }

            return string.Equals(targetSceneName, activeSceneName, StringComparison.Ordinal);
        }

        private void RefreshOnboardingWarmupState()
        {
            SessionOnboardingSettings.SyncInFlightTrialControls(
                selectedPlayerMode,
                selectedRobotStartupControl,
                selectedPwdStartupControl);

            if (!showOnboarding)
                return;

            if (!CanWarmupCurrentSelectionInActiveScene())
            {
                if (!showTrialStartPrompt)
                {
                    trialStartReady = false;
                    trialWarmupPending = false;
                }
                return;
            }

            if (IsTrialPreviewReady())
            {
                trialStartReady = true;
                trialWarmupPending = false;
                return;
            }

            trialStartReady = false;
            trialWarmupPending = true;
            trialWarmupDelayFrames = 0;
            trialWarmupGoalRepublishFrames = 0;
        }

        public Transform ResolveTransformForObjectId(string objectId)
        {
            if (rewindController != null)
            {
                return rewindController.ResolveTransformForObjectId(objectId);
            }

            return null;
        }

        private void EnsureOnboardingStyles()
        {
            if (onboardingStylesBuilt)
                return;

            onboardingStylesBuilt = true;

            Texture2D MakeTex(Color color)
            {
                Texture2D texture = new Texture2D(1, 1);
                texture.SetPixel(0, 0, color);
                texture.Apply();
                return texture;
            }

            onboardingPanelStyle = new GUIStyle(GUI.skin.box)
            {
                padding = new RectOffset(0, 0, 0, 0),
                normal = { background = MakeTex(new Color(0.08f, 0.09f, 0.11f, 0.98f)) }
            };

            onboardingTitleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 28,
                fontStyle = FontStyle.Bold,
                normal = { textColor = Color.white }
            };

            onboardingSectionStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 20,
                fontStyle = FontStyle.Bold,
                normal = { textColor = Color.white }
            };

            onboardingBodyStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 16,
                wordWrap = true,
                normal = { textColor = new Color(0.82f, 0.86f, 0.9f) }
            };

            onboardingHintStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 13,
                normal = { textColor = new Color(0.62f, 0.67f, 0.72f) }
            };

            onboardingPrimaryButtonStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = 17,
                fontStyle = FontStyle.Bold,
                normal = { background = MakeTex(new Color(0.17f, 0.45f, 0.29f)), textColor = Color.white },
                hover = { background = MakeTex(new Color(0.22f, 0.56f, 0.35f)), textColor = Color.white },
                active = { background = MakeTex(new Color(0.12f, 0.32f, 0.21f)), textColor = Color.white }
            };

            onboardingSecondaryButtonStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = 15,
                normal = { background = MakeTex(new Color(0.19f, 0.22f, 0.27f)), textColor = Color.white },
                hover = { background = MakeTex(new Color(0.26f, 0.3f, 0.36f)), textColor = Color.white }
            };

            onboardingChipStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = 15,
                fontStyle = FontStyle.Bold,
                normal = { background = MakeTex(new Color(0.17f, 0.19f, 0.24f)), textColor = new Color(0.85f, 0.87f, 0.9f) },
                hover = { background = MakeTex(new Color(0.23f, 0.26f, 0.31f)), textColor = Color.white }
            };

            onboardingChipActiveStyle = new GUIStyle(onboardingChipStyle)
            {
                normal = { background = MakeTex(new Color(0.33f, 0.42f, 0.18f)), textColor = Color.white },
                hover = { background = MakeTex(new Color(0.39f, 0.49f, 0.21f)), textColor = Color.white }
            };

            onboardingSceneButtonStyle = new GUIStyle(GUI.skin.button)
            {
                alignment = TextAnchor.MiddleLeft,
                fontSize = 15,
                normal = { background = MakeTex(new Color(0.14f, 0.16f, 0.2f)), textColor = new Color(0.84f, 0.87f, 0.9f) },
                hover = { background = MakeTex(new Color(0.21f, 0.24f, 0.3f)), textColor = Color.white }
            };

            onboardingSceneActiveButtonStyle = new GUIStyle(onboardingSceneButtonStyle)
            {
                fontStyle = FontStyle.Bold,
                normal = { background = MakeTex(new Color(0.16f, 0.37f, 0.49f)), textColor = Color.white },
                hover = { background = MakeTex(new Color(0.2f, 0.45f, 0.6f)), textColor = Color.white }
            };

            onboardingPreviewLabelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 16,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = Color.white }
            };
        }

        private void LoadOnboardingPreviewTextures()
        {
            femaleWheelchairPreview = LoadTextureFromAssets("UIResources/female-wheelchair.png");
            maleWheelchairPreview = LoadTextureFromAssets("UIResources/male_wheelchair_user.png");
            dogwalkerPreview = LoadTextureFromAssets("UIResources/dogwalker.png");
            scooterUserPreview = LoadTextureFromAssets("UIResources/scooteruser.png");
        }

        private Texture2D LoadTextureFromAssets(string relativeAssetPath)
        {
            string fullPath = Path.Combine(Application.dataPath, relativeAssetPath);
            if (!File.Exists(fullPath))
                return null;

            byte[] bytes = File.ReadAllBytes(fullPath);
            Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!texture.LoadImage(bytes))
            {
                Destroy(texture);
                return null;
            }

            texture.name = Path.GetFileNameWithoutExtension(relativeAssetPath);
            return texture;
        }
    }
}
