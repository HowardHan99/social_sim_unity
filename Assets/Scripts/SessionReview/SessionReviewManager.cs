using UnityEngine;
using UnityEngine.SceneManagement;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System;
using UnityTemplateProjects;

namespace SessionReview
{
    /// <summary>What happens after the "Save This Scene?" modal is answered.</summary>
    public enum WorldBuildingSaveFollowUp
    {
        /// <summary>Opened by the World Building "Save World" button: go back to editing.</summary>
        None,
        /// <summary>Opened by an onboarding Apply that reloads the scene: continue the switch.</summary>
        ContinueApply,
        /// <summary>Opened by "Run Again": reload the base scene and go to the trial-start prompt.</summary>
        RestartTrial
    }

    public class SessionReviewManager : MonoBehaviour
    {
        public static SessionReviewManager Instance { get; private set; }

        [Header("Keyboard Shortcuts")]
        [SerializeField] private KeyCode reviewToggleKey = KeyCode.T;
        [SerializeField] private KeyCode prevTrialKey = KeyCode.LeftBracket;
        [SerializeField] private KeyCode nextTrialKey = KeyCode.RightBracket;
        [SerializeField] private KeyCode playPauseKey = KeyCode.Space;
        [SerializeField] private KeyCode ghostTrailKey = KeyCode.G;
        [SerializeField] private KeyCode toggleInfoKey = KeyCode.I;
        [SerializeField] private KeyCode toggleGhostRobotsKey = KeyCode.F6;
        [Tooltip("While reviewing: immediately export the ROI using the current export settings (no panel needed).")]
        [SerializeField] private KeyCode quickRoiExportKey = KeyCode.F7;
        [Tooltip("Open/close the browser that lists trials saved by previous sessions (SessionLogs) for replay.")]
        [SerializeField] private KeyCode loadReplayKey = KeyCode.F12;

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
        [Tooltip("Automatically export the ROI (with the current export settings) when a live review ends, unless one was already exported during that review. Loaded-from-disk replays are skipped.")]
        [SerializeField] private bool autoExportRoiOnReviewExit = true;
        [SerializeField] private bool requirePlanBeforeTrialStart = true;
        [SerializeField] private bool allowStartWithoutRosBackend = true;

        [Header("Test Scene")]
        [Tooltip("Auto-enabled when the active scene is named TestScene. Keeps PWD manual control "
                 + "active without Start Trial and skips onboarding on play.")]
        [SerializeField] private bool allowPwdManualControlWithoutTrial;

        private SessionTracker sessionTracker;
        private ControlModeLog controlModeLog;
        private TrialDataArchive trialArchive;
        private MultiAgentTrajectoryRenderer trajectoryRenderer;
        private MetricsOverlayUI metricsOverlay;
        private ReplaySignalOverlay replaySignalOverlay;
        private RewindController rewindController;
        private LiveTrajectoryRecorder trajectoryRecorder;

        private int reviewTrialIndex = -1;
        private bool inRewindMode;
        private bool showPostTrialPrompt;
        private bool showTrialStartPrompt;
        private bool trialStartReady;
        private bool bypassRosBackendForTrialStart;
        private bool trialStartPromptPausedTime;
        private bool trialWarmupPending;
        private int trialWarmupDelayFrames;
        private int trialWarmupGoalRepublishFrames;
        private TrialEndInfo latestTrialEndInfo;
        private bool sessionFullyComplete;
        private TrialRecord currentReviewTrial;
        private Rerun.StateRecording currentReviewRecording;

        /// <summary>Absolute SessionLogs folder of the trial currently under review
        /// (live archive or loaded from disk); null outside review. DrawTraj uses it
        /// to mirror drawn trajectories into the trial's own folder.</summary>
        public static string CurrentReviewTrialFolder { get; private set; }
        private float currentReviewTimeOffset;
        private bool showReviewExportPanel;
        private ReviewExportSettings reviewExportSettings = new ReviewExportSettings();
        private Bounds reviewExportEnvelope;
        private string lastReviewExportPath;
        private float lastReviewExportToastTime = -999f;
        private bool isReviewingLoadedTrial;
        private string loadedTrialLabel;
        private bool showLoadTrialPanel;
        private Vector2 loadTrialScroll;
        private List<SavedTrialInfo> savedTrialList;
        private bool isTopDownPanning;
        private Vector2 lastTopDownMousePosition;
        private static Texture2D lineTexture;
        private Vector2 worldBuildingAddObjectsScroll;
        private Vector2 worldBuildingAddCharactersScroll;
        private string worldBuildingSpawnSearch = "";
        private bool worldBuildingAddObjectsMinimized = false;
        private bool worldBuildingAddCharactersMinimized = true;
        private bool worldBuildingGenerateObjectsMinimized = true;
        private bool worldBuildingWeatherMinimized = true;
        private bool worldBuildingOverlayMinimized;

        // Drag/resize state for the world-building panels. Position/size are in scaled GUI
        // units; while the has* flags are false the panel keeps its default docked layout.
        // Static so a rearranged layout survives scene reloads within a run (and because the
        // overlay rect getter is static). Double-clicking a header clears both overrides.
        private sealed class WorldBuildingPanelLayout
        {
            public bool hasCustomPosition;
            public Vector2 position;
            public bool hasCustomSize;
            public Vector2 size;
            public Vector2 dragOffset;
            public Vector2 resizeStartMouse;
            public Vector2 resizeStartSize;
            public Vector2 resizeStartPosition;
        }

        private static readonly WorldBuildingPanelLayout worldBuildingOverlayLayout = new WorldBuildingPanelLayout();
        private static readonly WorldBuildingPanelLayout worldBuildingGeneratePanelLayout = new WorldBuildingPanelLayout();
        private static readonly WorldBuildingPanelLayout worldBuildingCharactersPanelLayout = new WorldBuildingPanelLayout();
        private static readonly WorldBuildingPanelLayout worldBuildingObjectsPanelLayout = new WorldBuildingPanelLayout();
        private static readonly WorldBuildingPanelLayout worldBuildingWeatherPanelLayout = new WorldBuildingPanelLayout();
        private const float WorldBuildingSidePanelMarginBase = 24f;
        private const float WorldBuildingSidePanelGapBase = 10f;
        private const float WorldBuildingSidePanelHeaderHeightBase = 44f;
        private const float WorldBuildingAddObjectsExpandedHeaderHeightBase = 68f;
        private const float WorldBuildingGenerateObjectsBodyHeightBase = 108f;
        private const float WorldBuildingGenerateObjectsLoadingLabelHeightBase = 22f;
        private const float WorldBuildingSidePanelEmptyBodyHeightBase = 56f;
        private const float WorldBuildingSpawnCardPanelMinScrollHeightBase = 120f;
        private const float WorldBuildingOverlayHeaderHeightBase = 44f;
        private const float WorldBuildingOverlayWidthBase = 380f;
        private const string WorldBuildingAddObjectsSubtitle =
            "Click Add on a card to place that object in the scene.";

        // World-building UI scales up with resolution (1x at 1600x900) so text stays readable on large
        // displays. Based on the virtual (user-zoomed) screen so it stacks cleanly with ReviewUiScale.
        private static float WorldBuildingUiScale =>
            Mathf.Clamp(Mathf.Min(ReviewUiScale.Width / 1600f, ReviewUiScale.Height / 900f), 1f, 1.75f);

        private static float WorldBuildingSidePanelMargin => WorldBuildingSidePanelMarginBase * WorldBuildingUiScale;
        private static float WorldBuildingSidePanelGap => WorldBuildingSidePanelGapBase * WorldBuildingUiScale;
        private static float WorldBuildingSidePanelHeaderHeight => WorldBuildingSidePanelHeaderHeightBase * WorldBuildingUiScale;
        private static float WorldBuildingAddObjectsExpandedHeaderHeight => WorldBuildingAddObjectsExpandedHeaderHeightBase * WorldBuildingUiScale;
        private static float WorldBuildingGenerateObjectsBodyHeight => WorldBuildingGenerateObjectsBodyHeightBase * WorldBuildingUiScale;
        private static float WorldBuildingGenerateObjectsLoadingLabelHeight => WorldBuildingGenerateObjectsLoadingLabelHeightBase * WorldBuildingUiScale;
        private static float WorldBuildingSidePanelEmptyBodyHeight => WorldBuildingSidePanelEmptyBodyHeightBase * WorldBuildingUiScale;
        private static float WorldBuildingSpawnCardPanelMinScrollHeight => WorldBuildingSpawnCardPanelMinScrollHeightBase * WorldBuildingUiScale;
        private static float WorldBuildingOverlayHeaderHeight => WorldBuildingOverlayHeaderHeightBase * WorldBuildingUiScale;
        private static float WorldBuildingOverlayWidth => WorldBuildingOverlayWidthBase * WorldBuildingUiScale;
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

        private string aiGenerationPrompt = "";
        private GenerateModel meshyGenerator;
        private bool meshyGlbImportInProgress;
        private static float worldBuildingOverlayHeight = 228f;
        private const float WorldBuildingSidePanelWidthBase = 380f;
        private const float WorldBuildingSidePanelMaxWidthBase = 420f;
        private const float WorldBuildingSidePanelCustomMaxWidthBase = 560f;
        private const float WorldBuildingSpawnPaletteCardHeightBase = 112f;
        private const float WorldBuildingSpawnPaletteCardGapBase = 10f;
        private const float WorldBuildingSpawnPalettePreferredCardWidth = 190f;
        private const float WorldBuildingSpawnPaletteMinimumTwoColumnWidth = 300f;
        private static float WorldBuildingSidePanelWidth => WorldBuildingSidePanelWidthBase * WorldBuildingUiScale;
        private static float WorldBuildingSidePanelMaxWidth => WorldBuildingSidePanelMaxWidthBase;
        private static float WorldBuildingSidePanelCustomMaxWidth => WorldBuildingSidePanelCustomMaxWidthBase;
        private static float WorldBuildingPaletteMetricScale => Mathf.Clamp(WorldBuildingUiScale, 1f, 1.25f);
        private static float WorldBuildingSpawnPaletteCardHeight => WorldBuildingSpawnPaletteCardHeightBase * WorldBuildingPaletteMetricScale;
        private static float WorldBuildingSpawnPaletteCardGap => WorldBuildingSpawnPaletteCardGapBase * WorldBuildingPaletteMetricScale;
        private static float WorldBuildingSpawnPaletteSearchBlockHeight => 38f * WorldBuildingUiScale;

        private GUIStyle worldBuildingTitleStyle;
        private GUIStyle worldBuildingBodyStyle;
        private GUIStyle worldBuildingSubtitleStyle;
        private GUIStyle worldBuildingButtonStyle;
        private GUIStyle worldBuildingTextFieldStyle;
        private int worldBuildingStylesFontSize = -1;
        // Measured during OnGUI from the wrapped subtitle text; -1 until the first draw.
        private float worldBuildingAddObjectsHeaderHeight = -1f;
        // Feedback line under the Generate Object button ("Added X from the library", "Generating...").
        private string worldBuildingGenerateStatus;
        private float worldBuildingGenerateStatusHeight = -1f;
        // Weather panel hint, measured during OnGUI like the generate status; -1 until drawn.
        private const string WorldBuildingWeatherHint =
            "Saved with this world and applied when it runs. The top-down map stays clear —switch to free camera to preview.";
        private float worldBuildingWeatherHintHeight = -1f;
        // Prompt whose library match was just placed; repeating it forces a real generation.
        private string lastLibraryMatchPrompt;

        public bool UsePostTrialPrompt => usePostTrialPrompt;
        public bool IsReviewModeActive => inRewindMode;
        public bool IsWorldBuildingModeActive => inWorldBuildingMode;
        public bool IsPostTrialPromptActive => showPostTrialPrompt;
        public bool IsTrialStartPromptActive => showTrialStartPrompt;
        public bool IsOnboardingActive => showOnboarding;
        public bool IsReviewUiActive => inRewindMode && !inWorldBuildingMode;

        /// <summary>
        /// True while DrawTraj owns the screen (its DRAW MODE panel and DRAW/ERASE gates are up).
        /// Review overlays that are only noise while sketching a trajectory hide themselves on
        /// this; input handlers also use it to leave pan/zoom to TrajectoryManager's draw camera.
        /// </summary>
        public bool IsDrawTrajectoryModeActive
        {
            get
            {
                // Re-find only when the cache is stale: a scene reload destroys the manager,
                // and this is read from OnGUI (several times a frame) where FindObjectOfType
                // is far too expensive to call unconditionally.
                if (drawTrajManager == null)
                    drawTrajManager = FindObjectOfType<TrajectoryManager>();
                return drawTrajManager != null && drawTrajManager.IsDrawMode;
            }
        }
        private TrajectoryManager drawTrajManager;

        /// <summary>Static form of <see cref="IsDrawTrajectoryModeActive"/> for overlays without a manager reference.</summary>
        public static bool DrawTrajectoryModeActive =>
            Instance != null && Instance.IsDrawTrajectoryModeActive;
        public bool IsLiveTrialRunning
        {
            get
            {
                var sean = SEAN.SEAN.instance;
                return sean != null && sean.robotTask != null && sean.robotTask.isRunning;
            }
        }

        public bool BlocksAutomaticTrialStart => showOnboarding || showTrialStartPrompt || trialWarmupPending;

        /// <summary>
        /// True while any session UI is blocking gameplay input (onboarding, prompts, review,
        /// warmup). The POST-TRIAL menu is deliberately NOT included: the game keeps running
        /// beneath it and a manually driven agent must stay drivable while it is up.
        /// </summary>
        public bool IsMovementInputBlocked =>
            showOnboarding || showTrialStartPrompt || trialWarmupPending ||
            inRewindMode || showReviewCompletionPrompt;

        public bool ShouldShowEndInteractionButton => CanEndCurrentInteraction;
        public string EndInteractionButtonLabel => endInteractionButtonLabel;

        private bool CanEndCurrentInteraction =>
            showEndInteractionButton &&
            sessionTracker != null &&
            sessionTracker.IsTracking &&
            !IsMovementInputBlocked &&
            !showPostTrialPrompt &&
            !inWorldBuildingMode &&
            !showOnboarding;

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
        // Prefab name from Resources/PlayerCharacters; empty = built-in wheelchair pair.
        private string selectedPlayerCharacterId = string.Empty;
        // Participant/session id edit buffer (persisted via ParticipantSession on apply).
        private string sessionIdInput = string.Empty;
        private GUIStyle onboardingTextFieldStyle;
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

        // ---- Saved World Building scenarios (SessionLogs/<session>/scenario/) ----
        // The onboarding page lists them as optional thumbnail cards under the preset
        // scene list; -1 keeps the default behavior of launching the preset scene clean.
        private List<WorldBuildingScenarioInfo> savedScenarios = new List<WorldBuildingScenarioInfo>();
        private readonly List<int> visibleScenarioIndices = new List<int>();
        private int selectedScenarioIndex = -1;
        // Picker scope: off = only the current session's saved worlds (+ unassigned);
        // on = every session's (the "Show all sessions" toggle over the header).
        private bool showAllSessionScenarios = false;
        // "Save This Scene?" modal. Opened by the World Building "Save World" button, or by an
        // Apply / Run Again that is about to reload the scene while World Building changes exist.
        private bool showWorldBuildingSavePrompt;
        private WorldBuildingSaveFollowUp worldBuildingSaveFollowUp = WorldBuildingSaveFollowUp.None;
        private string worldBuildingSaveNameInput = string.Empty;
        private Texture2D worldBuildingSaveThumbnail;
        // Feedback line under the World Building overlay buttons ("Saved ..." / "Nothing to save").
        private string worldBuildingSaveStatus;

        private const float ScenarioCardWidth = 236f;
        private const float ScenarioCardHeight = 204f;
        private const float ScenarioCardGapX = 16f;
        private const float ScenarioCardGapY = 12f;

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            // Tab now belongs to the first-/third-person camera switch
            // (AgentViewToggle); migrate scenes that still serialize Tab here.
            if (reviewToggleKey == KeyCode.Tab)
                reviewToggleKey = KeyCode.T;
            EnsureComponents();
            LoadOnboardingPreviewTextures();
            ApplyTestScenePwdManualDefaults();
        }

        /// <summary>
        /// TestScene-only: immediate PWD manual driving without onboarding or Start Trial.
        /// </summary>
        private void ApplyTestScenePwdManualDefaults()
        {
            if (!IsNamedTestScene())
                return;

            allowPwdManualControlWithoutTrial = true;
            showOnboardingOnStart = false;
        }

        /// <summary>
        /// True in the practice/training scene. Also gates practice-only UI elsewhere
        /// (e.g. the "Aa" zoom badge in <see cref="UiScaleController"/>).
        /// </summary>
        public static bool IsNamedTestScene()
        {
            return string.Equals(
                SceneManager.GetActiveScene().name,
                "TestScene",
                StringComparison.OrdinalIgnoreCase);
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

            replaySignalOverlay = GetComponent<ReplaySignalOverlay>();
            if (replaySignalOverlay == null)
                replaySignalOverlay = gameObject.AddComponent<ReplaySignalOverlay>();

            rewindController = GetComponent<RewindController>();
            if (rewindController == null)
                rewindController = gameObject.AddComponent<RewindController>();
            rewindController.PlaybackReachedEnd -= HandleReviewPlaybackReachedEnd;
            rewindController.PlaybackReachedEnd += HandleReviewPlaybackReachedEnd;

            trajectoryRecorder = GetComponent<LiveTrajectoryRecorder>();
            if (trajectoryRecorder == null)
                trajectoryRecorder = gameObject.AddComponent<LiveTrajectoryRecorder>();

            // Owns its own hotkey + button strip; hidden during a trial, shown for review.
            if (GetComponent<RosOverlayVisibility>() == null)
                gameObject.AddComponent<RosOverlayVisibility>();

            meshyGenerator = GetComponent<GenerateModel>();
            if (meshyGenerator == null)
                meshyGenerator = gameObject.AddComponent<GenerateModel>();

            meshyGenerator.Completed -= OnMeshyGenerateComplete;
            meshyGenerator.Completed += OnMeshyGenerateComplete;
        }

        void Start()
        {
            if (sessionTracker != null)
            {
                sessionTracker.TrialEnded += OnTrialEnded;
                sessionTracker.SessionFullyComplete += OnSessionFullyComplete;
            }

            if (vlmCaptureButton != null)
                vlmCaptureButton.onClick.AddListener(RecordVLMCapture);

            InitializeOnboardingSelection();

            if (allowPwdManualControlWithoutTrial)
            {
                selectedPlayerMode = OnboardingPlayerMode.Human;
                selectedPwdStartupControl = StartupControlMode.Manual;
                StartCoroutine(EnableTestScenePwdManualControl());
            }
            else if (showOnboardingOnStart && !SessionOnboardingSettings.HasCompletedOnboarding)
            {
                SetOnboardingVisible(true);
            }
            else if (SessionOnboardingSettings.HasCompletedOnboarding && SessionOnboardingSettings.PendingTrialStart)
            {
                ShowTrialStartPrompt();
            }
        }

        private IEnumerator EnableTestScenePwdManualControl()
        {
            // PWDPlayer + ManualWheelchairController finish InitAfterBase on the next frame.
            yield return null;
            yield return null;
            ApplyStartupControlDefaults();
        }

        void OnDestroy()
        {
            if (vlmCaptureButton != null)
                vlmCaptureButton.onClick.RemoveListener(RecordVLMCapture);

            if (sessionTracker != null)
            {
                sessionTracker.TrialEnded -= OnTrialEnded;
                sessionTracker.SessionFullyComplete -= OnSessionFullyComplete;
            }
            if (rewindController != null)
                rewindController.PlaybackReachedEnd -= HandleReviewPlaybackReachedEnd;
            if (meshyGenerator != null)
                meshyGenerator.Completed -= OnMeshyGenerateComplete;
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
            sessionFullyComplete = false;
            // Show the review menu the instant the first agent arrives, but DO NOT
            // freeze time: the simulation keeps running so agents that have not yet
            // reached their goal continue to navigate. Entering review ([T]/Review)
            // still freezes time for replay; the user can do that at any point.
            showPostTrialPrompt = usePostTrialPrompt;
            SessionReview.SessionReviewLog.Log($"[SessionReview] Trial #{info.trialNumber} ended ({info.reason}). " +
                      $"Run continues; press [{reviewToggleKey}] to review.");
        }

        // Every primary agent (robot + PWD) has reached its goal: surface the post-trial
        // menu instead of dropping straight into review. The game deliberately keeps
        // running (no time freeze): a manually driven agent stays drivable under the menu,
        // and auto agents stop themselves at their goals.
        private void OnSessionFullyComplete()
        {
            // The trial was archived when the first agent arrived; extend it to now so the
            // review covers both agents' full paths.
            trialArchive?.FinalizeLatestTrial(Time.time, TrialEndReason.Completion);
            sessionFullyComplete = true;

            // Already reviewing or world building: leave the user where they are; the
            // finalized trial stays reachable through those modes' own menus.
            if (inRewindMode || inWorldBuildingMode)
                return;

            if (!usePostTrialPrompt)
            {
                // Prompt disabled in the inspector: keep the direct-to-review handoff.
                if (trialArchive != null && trialArchive.TrialCount > 0)
                    EnterRewindMode(trialArchive.TrialCount - 1);
                return;
            }

            showPostTrialPrompt = true;
        }

        void Update()
        {
            ProcessTrialWarmup();
            HandleInput();
        }

        private void HandleInput()
        {
            // While the onboarding panel is open, keyboard focus means the user is typing
            // in the Session ID field -- don't let the letter close the panel. Outside the
            // panel a stale focus (IMGUI sliders latch keyboardControl too) must not eat
            // the hotkey.
            bool typingInOnboarding = showOnboarding && GUIUtility.keyboardControl != 0;
            if (Input.GetKeyDown(onboardingToggleKey) && !typingInOnboarding && !showWorldBuildingSavePrompt &&
                (SessionOnboardingSettings.HasCompletedOnboarding || !showOnboarding))
                SetOnboardingVisible(!showOnboarding);

            // Saved-replay browser. Deliberately available during onboarding too: after a
            // fresh Unity start the onboarding screen is the first thing shown, and loading
            // an old trajectory should not require starting a live trial first.
            if (Input.GetKeyDown(loadReplayKey) && !inWorldBuildingMode && !showWorldBuildingSavePrompt)
            {
                ToggleLoadTrialPanel();
                return;
            }

            if (showLoadTrialPanel && Input.GetKeyDown(KeyCode.Escape))
            {
                showLoadTrialPanel = false;
                return;
            }

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

            // The review key only works when there are completed trials
            if (Input.GetKeyDown(reviewToggleKey) && trialArchive.TrialCount > 0)
                EnterRewindMode(trialArchive.TrialCount - 1);
        }

        private void HandleTrialStartPromptInput()
        {
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                ReturnToOnboardingFromTrialPrompt();
                return;
            }

            if (Input.GetKeyDown(startTrialKey) ||
                Input.GetKeyDown(KeyCode.KeypadEnter) ||
                SEAN.Input.JoystickProfiles.UiStartPressedThisFrame())
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

            // Dismiss the menu and unfreeze time (mid-run: remaining agents keep
            // navigating; after full completion: the scene simply idles);
            // The review key still re-opens review at any time.
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                HidePostTrialPrompt();
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
            // The save modal is up: it owns input, and the editor-inactive check below must not
            // tear World Building down while the operator is typing a name.
            if (showWorldBuildingSavePrompt)
                return;

            if (runtimeEditorManager == null)
            {
                ExitWorldBuildingMode(true);
                showPostTrialPrompt = true;
                return;
            }

            bool mouseOverWorldBuildingUi = IsMouseOverWorldBuildingUi();
            if (worldBuildingCameraController != null)
                worldBuildingCameraController.enabled = !mouseOverWorldBuildingUi;

            if (!runtimeEditorManager.isEditorActive)
            {
                ExitWorldBuildingMode(true);
                showPostTrialPrompt = true;
            }
        }

        private void HandleRewindInput()
        {
            // Typing a custom message in the Robot Signal panel: every review hotkey below
            // is a printable character or a view switch, so they must stand down until the
            // field lets go of the keyboard. Escape is the one that still gets through —
            // it is how the reviewer drops focus (see the ladder below).
            if (ReplaySignalOverlay.IsTypingMessage)
            {
                if (Input.GetKeyDown(KeyCode.Escape) && replaySignalOverlay != null)
                    replaySignalOverlay.TryHandleEscape();
                return;
            }

            // While draw-trajectory mode owns the pointer, TrajectoryManager pans/zooms
            // its own draw camera; zooming the hidden rewind camera here would fight it.
            if (!IsDrawTrajectoryModeActive)
                HandleTopDownMouseInput();

            if (IsDrawTrajectoryModeActive)
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

            if (Input.GetKeyDown(quickRoiExportKey))
                ExportCurrentRoiNow();

            if (Input.GetKeyDown(KeyCode.Escape))
            {
                // Back out one layer at a time instead of ending the review (and
                // tearing down the gameplay camera) on the very first press:
                //   1) close the export panel,
                //   2) close the Robot Signal panel, returning to the view the reviewer
                //      stepped into the robot from,
                //   3) return to the default top-down view,
                //   4) only then surface the next-step menu.
                if (showReviewExportPanel)
                {
                    showReviewExportPanel = false;
                }
                else if (replaySignalOverlay != null && replaySignalOverlay.TryHandleEscape())
                {
                    // Handled by the signal panel.
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
                SessionReview.SessionReviewLog.Log($"[SessionReview] Space pressed. isPlaying {before} -> {rewindController.IsPlaying}  speed={rewindController.PlaybackSpeed:F2}");
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
                SessionReview.SessionReviewLog.Log("[SessionReview] Replay lighting test key detected.");
                rewindController.ToggleLightingReplayTest();
            }

            if (audioTestPressed)
            {
                SessionReview.SessionReviewLog.Log("[SessionReview] Replay audio test key detected.");
                rewindController.PlayAudioReplayTest();
            }

            if (Input.GetKeyDown(robotFPKey))
            {
                // Shift picks the chase view instead: the robot's indicators are out of frame
                // in its own first-person view, so signalling is done from third person.
                bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                rewindController.SetPerspective(shift
                    ? PerspectiveMode.RobotThirdPerson
                    : PerspectiveMode.RobotFirstPerson);
            }
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

            if (Input.GetKeyDown(toggleInfoKey))
                rewindController.ToggleReplayInfo();

            if (Input.GetKeyDown(toggleGhostRobotsKey))
                rewindController.ToggleGhostComparison();

            // Trial navigation takes priority over speed when the same keys are bound to both.
            // Speed only fires when no trial switch occurred this frame. A loaded-from-disk
            // replay is not part of the live archive, so trial switching is disabled there
            // (the keys fall through to speed control).
            bool trialSwitched = false;
            if (!isReviewingLoadedTrial && Input.GetKeyDown(prevTrialKey) && trialArchive.TrialCount > 1)
            {
                trialSwitched = true;
                EnterRewindMode(Mathf.Max(0, reviewTrialIndex - 1));
            }
            if (!trialSwitched && !isReviewingLoadedTrial && Input.GetKeyDown(nextTrialKey) && trialArchive.TrialCount > 1)
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
                // Scale by the actual scroll amount so smooth-scrolling mice, which spread one
                // notch over several frames, do not compound a full step every frame.
                float zoomMultiplier = Mathf.Pow(0.92f, scroll);
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
            Vector2 guiPoint = ReviewUiScale.GuiMousePosition();

            if (showLoadTrialPanel && LoadTrialPanelRect.Contains(guiPoint))
                return true;

            // Draggable review panels (Metrics, etc.) manage their own scroll, so scrolling
            // over one must not also zoom the top-down scene behind it.
            if (ReviewPanels.AnyPanelContains(guiPoint))
                return true;

            if (UiScaleController.ControlContains(guiPoint))
                return true;

            Rect topRightStatusRect = new Rect(ReviewUiScale.Width - 340f, 10f, 330f, 50f);
            if (topRightStatusRect.Contains(guiPoint))
                return true;

            if (rewindController != null && rewindController.CurrentPerspective == PerspectiveMode.TopDown)
            {
                Rect topDownControlsRect = new Rect(ReviewUiScale.Width - 500f, 66f, 304f, 28f);
                if (topDownControlsRect.Contains(guiPoint))
                    return true;
            }

            if (showReviewExportPanel)
            {
                Rect exportButtonRect = new Rect(ReviewUiScale.Width - 170f, 70f, 140f, 32f);
                Rect exportPanelRect = new Rect(ReviewUiScale.Width - 380f, 110f, 360f, 340f);
                if (exportButtonRect.Contains(guiPoint) ||
                    exportPanelRect.Contains(guiPoint))
                    return true;
            }

            float barW = Mathf.Min(ReviewUiScale.Width - 40f, 1180f);
            float barX = (ReviewUiScale.Width - barW) * 0.5f;
            Rect progressBarRect = new Rect(barX, ReviewUiScale.Height - 116f, barW, 110f);
            return progressBarRect.Contains(guiPoint);
        }

        public void EnterRewindMode(int trialIndex)
        {
            var trial = trialArchive.GetTrial(trialIndex);
            if (trial == null) return;

            // Live review replays from the in-memory recorder; drop any loaded-from-disk
            // override a previous saved-trial replay may have left active.
            trajectoryRecorder.ClearReplayOverride();
            isReviewingLoadedTrial = false;
            loadedTrialLabel = null;

            var recording = trajectoryRecorder.BuildSnapshot();
            float timeOffset = trajectoryRecorder.RecordingStartTime;

            // The trial is archived the instant the FIRST primary agent reaches its goal, so its
            // endTime initially covers only that first arrival. FinalizeLatestTrial (fired on
            // SessionFullyComplete) normally stretches it to the LAST arrival, but if review is
            // opened before the whole session finishes -- or an agent never arrives -- the window
            // stays clipped at the first arrival and the replay is truncated. Stretch it here so
            // the progress-bar length is governed by the LAST agent to arrive (or, for a still-
            // running latest trial, the latest recorded moment) instead of the first.
            ExtendReviewWindowToLastArrival(trial, trialIndex);

            float recStart = trial.startTime - timeOffset;
            float recEnd = trial.endTime - timeOffset;
            var planSnapshots = trajectoryRecorder.GetPlanSnapshots(recStart, recEnd);
            var vlmCaptures = trajectoryRecorder.GetVLMCaptures(recStart, recEnd);
            var signalAnnotations = trajectoryRecorder.GetSignalAnnotations(recStart, recEnd);

            BeginReviewSession(trial, recording, timeOffset, planSnapshots, vlmCaptures, signalAnnotations, trialIndex);
        }

        /// <summary>
        /// Widen a trial's review window so its end time reflects the LAST primary agent (robot +
        /// PWD player) to arrive rather than the first (the moment the trial is first archived).
        /// No-op once the window already covers the last arrival, so a fully-completed trial that
        /// FinalizeLatestTrial already stretched is left untouched.
        /// </summary>
        private void ExtendReviewWindowToLastArrival(TrialRecord trial, int trialIndex)
        {
            if (trial == null)
                return;

            float end = trial.endTime;

            // Latest arrival latched on the record. Its AgentArrivalInfo objects are shared with
            // the live SessionTracker, so arrivals that happened after the first archive show up
            // here even without a FinalizeLatestTrial pass.
            if (trial.agentArrivals != null)
            {
                foreach (var arrival in trial.agentArrivals)
                {
                    if (arrival == null || !arrival.arrived)
                        continue;
                    if (arrival.role != AgentRole.Robot && arrival.role != AgentRole.PWDPlayer)
                        continue;
                    if (arrival.arrivalTime > end)
                        end = arrival.arrivalTime;
                }
            }

            // Reviewing the latest trial while the session is still running: the sim advanced past
            // the first arrival and kept recording, so cover everything up to now rather than
            // discarding those samples (e.g. an agent that is stuck and never formally "arrives").
            bool isLatestTrial = trialArchive != null && trialIndex == trialArchive.TrialCount - 1;
            if (isLatestTrial && sessionTracker != null && sessionTracker.IsTracking)
                end = Mathf.Max(end, Time.time);

            if (end > trial.endTime)
            {
                SessionReview.SessionReviewLog.Log(
                    $"[SessionReview] Extended review window end {trial.endTime:F2}s -> {end:F2}s " +
                    "so the progress bar spans up to the last agent to arrive.");
                trial.endTime = end;
            }
        }

        /// <summary>
        /// Replay a trial saved to disk by a previous session (SessionLogs/trial_*). The
        /// loaded data is served through LiveTrajectoryRecorder's replay override so the
        /// whole review pipeline (playback, trails, ROI export) works without live data.
        /// </summary>
        public void EnterRewindModeFromDisk(SavedTrialInfo info)
        {
            if (info == null)
                return;

            SavedTrialSession session;
            try
            {
                session = SavedTrialLoader.Load(info.folderPath);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SessionReview] Failed to load saved trial from {info.folderPath}: {ex}");
                return;
            }
            if (session == null)
                return;

            showLoadTrialPanel = false;
            HidePostTrialPrompt();
            if (showOnboarding)
                SetOnboardingVisible(false);
            // Dismissing the ready prompt must also lift its time freeze, or the review
            // below would capture 0 as the timescale to restore on exit.
            if (trialStartPromptPausedTime)
            {
                Time.timeScale = savedTimeScale;
                trialStartPromptPausedTime = false;
            }
            showTrialStartPrompt = false;

            trajectoryRecorder.SetReplayOverride(session.recording, session.planSnapshots,
                session.vlmCaptures, session.signalAnnotations);
            isReviewingLoadedTrial = true;
            loadedTrialLabel = $"{session.trial.trialName} ({info.folderName})";
            session.trial.archiveFolder = info.folderPath;
            CurrentReviewTrialFolder = info.folderPath;

            BeginReviewSession(session.trial, session.recording, session.timeOffset,
                session.planSnapshots, session.vlmCaptures, session.signalAnnotations, -1);
        }

        private void BeginReviewSession(TrialRecord trial, Rerun.StateRecording recording, float timeOffset,
            List<PlanPathSnapshot> planSnapshots, List<VLMCaptureEvent> vlmCaptures,
            List<SignalAnnotation> signalAnnotations, int trialIndex)
        {
            bool wasInReview = inRewindMode;
            if (wasInReview)
                rewindController.ExitRewind();

            ExitWorldBuildingMode(true);

            reviewTrialIndex = trialIndex;
            if (trialIndex >= 0)
                CurrentReviewTrialFolder = trial != null ? trial.archiveFolder : null;
            inRewindMode = true;
            showReviewCompletionPrompt = false;
            currentSpeedIndex = 2;

            // Freeze the simulation. Only capture the pre-review time scale when entering
            // fresh: switching trials mid-review would otherwise capture the frozen 0 and
            // leave the simulation stuck after the review ends.
            if (!wasInReview)
                savedTimeScale = Time.timeScale;
            Time.timeScale = 0f;

            // Suppress the live ROS plan line (GlobalPlanVisualizer) for the whole review.
            // It is a separate persistent renderer the review "Hide All" menu can't toggle,
            // and it keeps redrawing whenever ROS republishes the global plan (message delivery
            // runs in Update, unaffected by the frozen Time.timeScale), so a one-shot clear
            // reappears immediately. The review overlay shows its own hideable snapshot copy
            // ("ROS Nav Plan") instead. Restored in ExitReviewMode.
            foreach (var live in FindObjectsOfType<SEAN.Display.PlanVisualizer>())
                live.SetRenderingSuppressed(true);

            // Review is where the robot's intent is actually wanted, so the switch the trial ran
            // with flips back on. Must precede EnterRewind: that is where the "ROS Nav Plan"
            // legend row is registered, and it reads its initial visibility from this switch.
            // One exception: when a human drove the robot, the recorded plan is only what ROS
            // would have done —kept available behind the legend row / Control Traj toggle,
            // but not shown until a reviewer asks for it.
            RosOverlayVisibility.SetRobotGoalVisible(true);
            RosOverlayVisibility.SetPlanVisible(!WasRobotManuallyDriven(trial));

            // The "Pedestrian Goal" label must mark the route THIS recording drove, not
            // whatever route the live scene happens to be on (see the method's doc).
            AlignPedestrianGoalLabelToTrial(trial, recording, timeOffset);

            trajectoryRenderer.ShowTrajectories(trial, recording, controlModeLog, planSnapshots, vlmCaptures, signalAnnotations, timeOffset);
            metricsOverlay.ShowTrial(trial);
            rewindController.EnterRewind(trial, recording, controlModeLog, trajectoryRenderer, timeOffset, signalAnnotations);
            // Must follow EnterRewind: the panel reads the perspective the review just reset.
            if (replaySignalOverlay != null)
                replaySignalOverlay.BeginReview(rewindController, trial);

            currentReviewTrial = trial;
            currentReviewRecording = recording;
            currentReviewTimeOffset = timeOffset;
            showReviewExportPanel = false;
            lastReviewExportPath = null;
            if (!ReviewRoiExporter.TryComputeTrajectoryEnvelope(trial, recording, timeOffset, out reviewExportEnvelope))
                reviewExportEnvelope = new Bounds(Vector3.zero, new Vector3(10f, 1f, 10f));
        }

        /// <summary>
        /// Points the "Pedestrian Goal" label at the goal of the route the REVIEWED trial's
        /// pedestrian actually drove. The live route (RandomAvatar.LastPlayerGoalObject) can
        /// disagree with the reviewed recording: a loaded replay runs in a scene whose route
        /// followed TODAY'S onboarding, and an in-session review may come after the role (and
        /// with it the route) swapped. The route is identified by the pedestrian's position at
        /// the trial-window start — it spawns centimetres from its route's start marker, and
        /// the two start markers sit metres apart, so this is unambiguous. (The GOAL markers
        /// are NOT a usable discriminator: in sidewalkOutofStore the pedestrian halts ~1 m
        /// before "end", which is closer to "end2" than to "end". trial.playerGoalPosition is
        /// not used either: trials saved before 2026-08-14 carry the _StartAndGoal rig's
        /// ~origin position there.)
        /// </summary>
        private void AlignPedestrianGoalLabelToTrial(TrialRecord trial, Rerun.StateRecording recording, float timeOffset)
        {
            if (trial == null || recording == null || recording.timelines == null)
                return;

            string pwdId = null;
            if (trial.agentRoles != null)
            {
                var roleEntry = trial.agentRoles.Find(r => r != null && r.role == AgentRole.PWDPlayer);
                if (roleEntry != null)
                    pwdId = roleEntry.objectId;
            }
            if (string.IsNullOrEmpty(pwdId))
                return;

            // The pedestrian's position at the trial-window start. A LIVE review's recording
            // spans the whole session, so the timeline's first sample can predate this trial —
            // take the first sample inside the window instead (saved replays are pre-windowed,
            // where this is simply the first sample).
            float recStart = trial.startTime - timeOffset;
            Vector3 startPos = Vector3.zero;
            bool foundStart = false;
            foreach (var timeline in recording.timelines)
            {
                if (timeline == null || timeline.objectId != pwdId ||
                    timeline.states == null || timeline.states.Count == 0)
                    continue;
                foreach (var state in timeline.states)
                {
                    if (state == null || state.timestamp < recStart - 0.5f)
                        continue;
                    startPos = state.position;
                    foundStart = true;
                    break;
                }
                break;
            }
            if (!foundStart)
                return;

            SEAN.Scenario.Agents.RandomAvatar spawner = null;
            foreach (var ra in FindObjectsOfType<SEAN.Scenario.Agents.RandomAvatar>(true))
            {
                if (ra != null && ra.isPwdPlayer)
                {
                    spawner = ra;
                    break;
                }
            }
            if (spawner == null)
                return;

            // Match the spawn position to a route's START marker, then label that route's goal.
            GameObject bestGoal = null;
            float bestDist = float.MaxValue;
            var routes = new[]
            {
                new { start = spawner.startObjectName, goal = spawner.goalObjectName },
                new { start = spawner.robotTrialStartObjectName, goal = spawner.robotTrialGoalObjectName },
            };
            foreach (var route in routes)
            {
                if (string.IsNullOrEmpty(route.start) || string.IsNullOrEmpty(route.goal))
                    continue;
                GameObject startMarker = SEAN.Scenario.Agents.RandomAvatar.FindSceneObjectByName(route.start);
                GameObject goalMarker = SEAN.Scenario.Agents.RandomAvatar.FindSceneObjectByName(route.goal);
                if (startMarker == null || goalMarker == null)
                    continue;
                float dist = SEAN.Util.Geometry.GroundPlaneDist(startPos, startMarker.transform.position);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestGoal = goalMarker;
                }
            }

            // The pedestrian spawns essentially ON its start marker (NavMesh-sampled, sub-cm in
            // the recorded data), so the nearest-start comparison is exact; the absolute cap
            // only rejects recordings from other layouts. It must stay below the tightest
            // start-marker pair across the scenario scenes (sidewalkNarrowroad's are 0.78 m
            // apart) so a borderline spawn can never pass as the OTHER route.
            if (bestGoal == null || bestDist > 0.35f)
                return;

            SEAN.Scenario.Agents.RandomAvatar.OverridePlayerGoalObject(bestGoal);
            SessionReviewLog.Log($"[SessionReview] Pedestrian goal label aligned to '{bestGoal.name}' " +
                                 $"(reviewed pedestrian spawned {bestDist:F2} m from its route's start marker).");
        }

        /// <summary>
        /// Did a human drive the robot during this trial? Read from the trial record itself
        /// (its control-mode entries carry the state active at the trial-window start), so it
        /// works for disk-loaded trials too; the live ControlModeLog is the fallback for older
        /// saves that only recorded in-window transitions.
        /// </summary>
        private bool WasRobotManuallyDriven(TrialRecord trial)
        {
            if (trial == null)
                return false;

            var robotIds = new HashSet<string> { "robot" }; // ControlModeLog's id fallback
            foreach (var roleEntry in trial.agentRoles)
            {
                if (roleEntry.role == AgentRole.Robot && !string.IsNullOrEmpty(roleEntry.objectId))
                    robotIds.Add(roleEntry.objectId);
            }

            foreach (var entry in trial.controlModeEntries)
            {
                if (entry.mode == ControlMode.Manual && robotIds.Contains(entry.agentId))
                    return true;
            }

            if (!isReviewingLoadedTrial && controlModeLog != null)
            {
                foreach (string robotId in robotIds)
                {
                    if (controlModeLog.GetModeAtTime(robotId, trial.startTime) == ControlMode.Manual)
                        return true;
                }
            }

            return false;
        }

        public void ExitReviewMode()
        {
            // Guarantee at least one ROI export per live review: if the reviewer never
            // exported (key or button), save one on the way out while the review data is
            // still bound. Loaded-from-disk replays are skipped —their trial data is
            // already on disk, and re-exporting on every viewing would just pile up folders.
            if (autoExportRoiOnReviewExit && inRewindMode && !isReviewingLoadedTrial &&
                currentReviewTrial != null && string.IsNullOrEmpty(lastReviewExportPath))
            {
                ExportCurrentRoiNow();
            }

            inRewindMode = false;
            showReviewCompletionPrompt = false;
            trajectoryRenderer.ClearAll();
            metricsOverlay.Hide();
            // Before CurrentReviewTrialFolder is cleared below: that folder is where the
            // reviewer's replay-time signals are written.
            if (replaySignalOverlay != null)
                replaySignalOverlay.EndReview();
            rewindController.ExitRewind();

            // Restore simulation
            Time.timeScale = savedTimeScale;

            // Re-enable the live ROS plan line that was suppressed on review entry.
            foreach (var live in FindObjectsOfType<SEAN.Display.PlanVisualizer>())
                live.SetRenderingSuppressed(false);

            showReviewExportPanel = false;
            currentReviewTrial = null;
            currentReviewRecording = null;
            currentReviewTimeOffset = 0f;
            trajectoryRecorder.ClearReplayOverride();
            isReviewingLoadedTrial = false;
            loadedTrialLabel = null;
            CurrentReviewTrialFolder = null;
        }

        public void StartNextTrialFromPrompt()
        {
            // "Run Again" pressed from inside World Building means "run what I just built": the
            // edits —dragged start/goal markers above all —ARE the point of the run, so it
            // restarts in place and keeps them. Reloading here would silently revert the goal the
            // operator just moved, for both the robot and the pedestrian.
            if (inWorldBuildingMode)
            {
                StartNextTrialInPlace();
                return;
            }

            // Outside World Building an edited world is an *option*, never sticky state: the next
            // trial starts from the original scene, and the edits come back only when the operator
            // picks their card on the session page. Unsaved edits are offered to the save modal
            // first so a restart cannot silently throw the work away.
            //
            // Gated on the onboarding flow: the reloaded scene only finds its way back to the
            // trial-start prompt through SessionOnboardingSettings (see Start()), so a scene
            // that never ran onboarding restarts in place instead of coming up with no prompt.
            if (SessionOnboardingSettings.HasCompletedOnboarding)
            {
                switch (ClassifyWorldBuildingChanges())
                {
                    case WorldBuildingChangeState.Unsaved:
                        CaptureReviewCameraForWorldBuilding();
                        OpenWorldBuildingSavePrompt(WorldBuildingSaveFollowUp.RestartTrial);
                        return;
                    case WorldBuildingChangeState.Saved:
                        RestartTrialWithCleanScene();
                        return;
                }
            }

            StartNextTrialInPlace();
        }

        /// <summary>
        /// Restart without a scene reload, so whatever is in the world right now is what runs.
        /// Used when the world already matches the base scene, and when the operator asked for
        /// this run from inside World Building -- see <see cref="StartNextTrialFromPrompt"/>.
        /// </summary>
        private void StartNextTrialInPlace()
        {
            CaptureReviewCameraForWorldBuilding();
            showReviewCompletionPrompt = false;
            if (inRewindMode)
                ExitReviewMode();
            ExitWorldBuildingMode(true);

            HidePostTrialPrompt();
            latestTrialEndInfo = null;
            sessionFullyComplete = false;

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
            ApplyPwdTrialRoute(); // re-select pedestrian route + reposition + hide markers now that the role is known
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
            // Global user zoom (see UiScaleController): fonts and layout scale together,
            // and all layout below uses ReviewUiScale.Width/Height as the screen size.
            ReviewUiScale.Apply();

            // Modal: drawn instead of (not over) the onboarding panel, and last of all so no
            // click can fall through to the World Building editor behind it.
            if (showWorldBuildingSavePrompt)
            {
                EnsureOnboardingStyles();
                DrawWorldBuildingSavePrompt();
                return;
            }

            if (showOnboarding)
                DrawOnboardingUI();

            DrawStatusBadge();

            DrawEndInteractionButton();

            if (showTrialStartPrompt)
                DrawTrialStartPrompt();

            if (showPostTrialPrompt)
                DrawPostTrialPrompt();

            // Both prompts are next-step menus about the replay as a whole —pure clutter
            // over a drawing canvas, and the draw session has to be finished or cancelled
            // before any of their buttons make sense anyway.
            bool drawing = IsDrawTrajectoryModeActive;

            if (showReviewCompletionPrompt && !drawing)
                DrawReviewCompletionPrompt();

            if (inRewindMode)
            {
                DrawReviewRoiOverlay();

                // The REWIND status box is skipped while drawing: the DRAW MODE panel already
                // spells out the same pan/zoom controls, so it is duplicated noise on the right.
                if (!drawing)
                {
                    string perspective = rewindController.CurrentPerspective.ToString();
                    string playing = rewindController.IsPlaying ? "PLAYING" : "PAUSED";
                    string trialLabel = isReviewingLoadedTrial
                        ? $"Loaded: {loadedTrialLabel}"
                        : $"Trial {reviewTrialIndex + 1}/{trialArchive.TrialCount}";
                    GUI.Box(new Rect(ReviewUiScale.Width - 340, 10, 330, 50), "");
                    GUI.Label(new Rect(ReviewUiScale.Width - 335, 15, 320, 20),
                        $"REWIND [{playing}] {trialLabel}");
                    GUI.Label(new Rect(ReviewUiScale.Width - 335, 35, 320, 20),
                        $"{perspective} | F1-F5:View  Wheel:Zoom  MMB:Pan  {reviewToggleKey}/Esc:Exit");
                }

                DrawEndReviewButton();
                DrawTopDownReviewControls();

                DrawReviewExportPanel();

                // Row of toggles to re-open any review panel (Metrics/Legend/Trajectory/
                // Robot Signal) closed via its title-bar [x].
                ReviewPanels.DrawToggleBar();
            }

            if (inWorldBuildingMode)
                DrawWorldBuildingOverlay();

            DrawRoiExportToast();

            if (showLoadTrialPanel)
                DrawLoadTrialPanel();
        }

        private void DrawEndInteractionButton()
        {
            if (!showEndInteractionButton)
                return;
            if (EndInteractionOverlay.HandlesEndInteractionButton)
                return;

            // Only available while an interaction is actively running and nothing else
            // (onboarding, prompts, review, world building) is occupying the screen.
            if (sessionTracker == null || !sessionTracker.IsTracking)
                return;
            if (IsMovementInputBlocked || showPostTrialPrompt || inWorldBuildingMode || showOnboarding)
                return;

            float width = 220f;
            float height = 40f;
            float x = (ReviewUiScale.Width - width) * 0.5f;
            float y = ReviewUiScale.Height - height - 24f;

            // The uGUI Send Signal button (robot player) also sits bottom-center, in
            // unscaled canvas pixels —at higher UI scales the two land on the same
            // spot. While it is visible, sit directly above it instead of on top.
            if (SignalUIManager.TryGetVisibleSendSignalRect(out Rect signalRect))
            {
                float signalTopGui = ReviewUiScale.ScreenToGui(new Vector2(0f, signalRect.yMax)).y;
                y = Mathf.Min(y, signalTopGui - height - 10f);
            }

            if (GUI.Button(new Rect(x, Mathf.Max(10f, y), width, height), endInteractionButtonLabel))
                EndInteractionAndProceed();
        }

        public void EndCurrentInteraction()
        {
            if (!CanEndCurrentInteraction)
                return;

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
            float x = ReviewUiScale.Width - width - 18f;

            // Sit fully above the replay progress bar. Its scrubber is an IMGUI slider
            // that consumes clicks first, so a button overlapping it scrubs the timeline
            // instead of ending the review. The constant is the fallback for the frame
            // before the bar has drawn once.
            float y = ReviewUiScale.Height - 124f - height;
            if (rewindController != null && rewindController.TryGetProgressBarRect(out Rect bar))
                y = bar.y - height - 12f;

            if (GUI.Button(new Rect(x, Mathf.Max(10f, y), width, height), "End Review / Menu"))
                EndReviewAndShowNextStepMenu();
        }

        private void DrawTopDownReviewControls()
        {
            if (rewindController == null || rewindController.CurrentPerspective != PerspectiveMode.TopDown)
                return;

            Bounds roi = ReviewRoiExporter.ApplySettings(reviewExportEnvelope, reviewExportSettings);
            float top = 66f;
            float right = ReviewUiScale.Width - 500f;
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
            Rect buttonRect = new Rect(ReviewUiScale.Width - 170f, 70f, buttonWidth, 32f);
            if (GUI.Button(buttonRect, showReviewExportPanel ? $"Hide Export [{exportReviewKey}]" : $"Export ROI [{exportReviewKey}]"))
                showReviewExportPanel = !showReviewExportPanel;

            if (!showReviewExportPanel)
                return;

            float width = 360f;
            float height = 340f;
            Rect rect = new Rect(ReviewUiScale.Width - width - 20f, 110f, width, height);
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

            DrawSliderRow(x, ref y, innerWidth, "Pad X", ref reviewExportSettings.paddingX, 0f, 60f);
            DrawSliderRow(x, ref y, innerWidth, "Pad Z", ref reviewExportSettings.paddingZ, 0f, 60f);
            DrawSliderRow(x, ref y, innerWidth, "Offset X", ref reviewExportSettings.offsetX, -60f, 60f);
            DrawSliderRow(x, ref y, innerWidth, "Offset Z", ref reviewExportSettings.offsetZ, -60f, 60f);

            reviewExportSettings.exportImage = GUI.Toggle(
                new Rect(x, y, innerWidth, 22f),
                reviewExportSettings.exportImage,
                "Export top-down PNGs (plain + trajectory)");
            y += 26f;

            float resolution = reviewExportSettings.imageMaxResolution;
            DrawSliderRow(x, ref y, innerWidth, "Image Max", ref resolution, 512f, 2048f);
            reviewExportSettings.imageMaxResolution = Mathf.RoundToInt(resolution / 64f) * 64;

            if (GUI.Button(new Rect(x, rect.yMax - 96f, 150f, 34f), "Focus Top-Down"))
            {
                rewindController?.FocusTopDownOnBounds(roi);
            }

            if (GUI.Button(new Rect(x, rect.yMax - 54f, 150f, 34f), $"Export ROI [{quickRoiExportKey}]"))
            {
                ExportCurrentRoiNow();
            }

            if (!string.IsNullOrEmpty(lastReviewExportPath))
            {
                GUI.Label(new Rect(x, rect.yMax - 126f, innerWidth, 28f), $"Last export: {lastReviewExportPath}");
            }
        }

        /// <summary>
        /// Export the current trial's ROI immediately with the current export settings.
        /// Bound to quickRoiExportKey so no panel interaction is needed; also used by the
        /// export panel's button.
        /// </summary>
        private void ExportCurrentRoiNow()
        {
            if (!inRewindMode || currentReviewTrial == null || currentReviewRecording == null)
                return;

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
            lastReviewExportToastTime = Time.unscaledTime;
        }

        // Transient confirmation so the quick-export key gives feedback even when the
        // export panel is closed. Unscaled time: the review freezes Time.timeScale.
        private void DrawRoiExportToast()
        {
            if (string.IsNullOrEmpty(lastReviewExportPath))
                return;
            if (Time.unscaledTime - lastReviewExportToastTime > 5f)
                return;

            string text = lastReviewExportPath == "Export failed"
                ? "ROI export FAILED (see console)"
                : $"ROI saved: {lastReviewExportPath}";
            float width = Mathf.Min(ReviewUiScale.Width - 40f, Mathf.Max(360f, text.Length * 7.5f));
            GUI.Box(new Rect((ReviewUiScale.Width - width) * 0.5f, 64f, width, 26f), text);
        }

        private Rect LoadTrialPanelRect
        {
            get
            {
                float width = Mathf.Min(680f, ReviewUiScale.Width - 60f);
                float height = Mathf.Min(440f, ReviewUiScale.Height - 60f);
                return new Rect((ReviewUiScale.Width - width) * 0.5f, (ReviewUiScale.Height - height) * 0.5f, width, height);
            }
        }

        private void ToggleLoadTrialPanel()
        {
            showLoadTrialPanel = !showLoadTrialPanel;
            if (showLoadTrialPanel)
                savedTrialList = SavedTrialLoader.ListSavedTrials();
        }

        private void DrawLoadTrialPanel()
        {
            Rect rect = LoadTrialPanelRect;

            // Dim the background so the list reads clearly over any mode behind it.
            Color previousColor = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.45f);
            GUI.DrawTexture(new Rect(0f, 0f, ReviewUiScale.Width, ReviewUiScale.Height), Texture2D.whiteTexture);
            GUI.color = previousColor;

            GUI.Box(rect, "");

            float x = rect.x + 16f;
            float y = rect.y + 12f;
            float innerWidth = rect.width - 32f;

            GUI.Label(new Rect(x, y, innerWidth, 24f),
                $"LOAD SAVED TRIAL REPLAY   [{loadReplayKey}] toggle   [Esc] close");
            y += 26f;
            GUI.Label(new Rect(x, y, innerWidth, 20f), $"Folder: {TrialDataArchive.LogFolder}");
            y += 26f;

            string activeScene = SceneManager.GetActiveScene().name;
            float footerHeight = 56f;

            if (savedTrialList == null || savedTrialList.Count == 0)
            {
                GUI.Label(new Rect(x, y, innerWidth, 40f),
                    "No saved trials found. Trials are saved automatically when a trial ends.");
            }
            else
            {
                float rowHeight = 32f;
                float listHeight = rect.yMax - footerHeight - y;
                Rect viewRect = new Rect(0f, 0f, innerWidth - 20f, savedTrialList.Count * (rowHeight + 4f));
                loadTrialScroll = GUI.BeginScrollView(new Rect(x, y, innerWidth, listHeight), loadTrialScroll, viewRect);

                float rowY = 0f;
                foreach (var info in savedTrialList)
                {
                    string scene = string.IsNullOrEmpty(info.sceneName) ? "scene unknown" : info.sceneName;
                    string sceneWarning = !string.IsNullOrEmpty(info.sceneName) && info.sceneName != activeScene
                        ? "  (!) other scene"
                        : "";
                    string session = string.IsNullOrEmpty(info.sessionId) ? "" : $"[{info.sessionId}]  ";
                    string label = $"#{info.trialNumber:D3}  {session}{info.savedAt:yyyy-MM-dd HH:mm}  {scene}  " +
                                   $"{info.durationSeconds:F0}s{sceneWarning}";
                    GUI.Label(new Rect(0f, rowY + 4f, viewRect.width - 84f, rowHeight), label);
                    if (GUI.Button(new Rect(viewRect.width - 76f, rowY, 76f, 28f), "Load"))
                    {
                        EnterRewindModeFromDisk(info);
                        GUI.EndScrollView();
                        return;
                    }
                    rowY += rowHeight + 4f;
                }

                GUI.EndScrollView();
            }

            GUI.Label(new Rect(x, rect.yMax - footerHeight + 6f, innerWidth - 190f, footerHeight - 12f),
                $"Active scene: {activeScene}. A replay from another scene will draw its paths over the wrong environment.");

            if (GUI.Button(new Rect(rect.xMax - 186f, rect.yMax - 46f, 80f, 30f), "Refresh"))
                savedTrialList = SavedTrialLoader.ListSavedTrials();
            if (GUI.Button(new Rect(rect.xMax - 98f, rect.yMax - 46f, 80f, 30f), "Close"))
                showLoadTrialPanel = false;
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

            guiPoint = ReviewUiScale.ScreenToGui(screenPoint);
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
            // Hidden together with the Agent Speed panel; F8 toggles both.
            if (!AgentSpeedOverlay.HudVisible) return;

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
                text = $"TRIAL ENDED | {trials} trial(s) ready | [{reviewToggleKey}] Review | [{loadReplayKey}] Load";
                bgColor = new Color(0.3f, 0.15f, 0f, 0.85f);
            }
            else
            {
                text = $"Tracking {tracked} agents | Waiting for trial to end... | [{loadReplayKey}] Load Replay";
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
            GUI.Box(new Rect(ReviewUiScale.Width - w - 15, 10, w, 30), text, style);
            GUI.backgroundColor = Color.white;
        }

        private void DrawPostTrialPrompt()
        {
            string reasonText = latestTrialEndInfo != null ? latestTrialEndInfo.reason.ToString() : "Completion";

            float width = 480f;
            float height = 176f;
            Rect rect = new Rect((ReviewUiScale.Width - width) * 0.5f, 24f, width, height);

            GUI.Box(rect, "");
            if (sessionFullyComplete)
            {
                GUI.Label(new Rect(rect.x + 18f, rect.y + 16f, rect.width - 36f, 24f),
                    $"TRIAL COMPLETE ({reasonText})");
                GUI.Label(new Rect(rect.x + 18f, rect.y + 44f, rect.width - 36f, 48f),
                    "All agents reached their goals. The run is paused.\n" +
                    $"[{replayTrialKey}] Run again   [{reviewToggleKey}] Review   [Esc] Close menu");
            }
            else
            {
                GUI.Label(new Rect(rect.x + 18f, rect.y + 16f, rect.width - 36f, 24f),
                    $"TRIAL READY TO REVIEW ({reasonText})");
                GUI.Label(new Rect(rect.x + 18f, rect.y + 44f, rect.width - 36f, 48f),
                    "An agent reached its goal. The run keeps going for the others.\n" +
                    $"[{replayTrialKey}] Run again   [{reviewToggleKey}] Review   [Esc] Keep watching");
            }

            float by = rect.y + 108f;
            if (GUI.Button(new Rect(rect.x + 18f, by, 140f, 34f), $"Run Again [{replayTrialKey}]"))
                StartNextTrialFromPrompt();

            if (GUI.Button(new Rect(rect.x + 168f, by, 140f, 34f), $"Review [{reviewToggleKey}]"))
            {
                HidePostTrialPrompt();
                EnterRewindMode(trialArchive.TrialCount - 1);
            }

            string dismissLabel = sessionFullyComplete ? "Close Menu [Esc]" : "Keep Watching [Esc]";
            if (GUI.Button(new Rect(rect.x + 318f, by, 140f, 34f), dismissLabel))
                HidePostTrialPrompt();
        }

        private void DrawReviewCompletionPrompt()
        {
            float width = 520f;
            float height = 196f;
            Rect rect = new Rect((ReviewUiScale.Width - width) * 0.5f, 24f, width, height);

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

        private void EnsureWorldBuildingStyles()
        {
            float s = WorldBuildingUiScale;
            int bodyFontSize = Mathf.RoundToInt(15f * s);
            int buttonFontSize = Mathf.RoundToInt(15f * Mathf.Clamp(s, 1f, 1.35f));
            if (worldBuildingStylesFontSize == bodyFontSize && worldBuildingTitleStyle != null)
                return;

            worldBuildingStylesFontSize = bodyFontSize;

            worldBuildingTitleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.RoundToInt(18f * s),
                fontStyle = FontStyle.Bold,
                normal = { textColor = Color.white }
            };

            worldBuildingBodyStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = bodyFontSize,
                wordWrap = true,
                richText = false
            };

            worldBuildingSubtitleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.RoundToInt(13f * s),
                wordWrap = true,
                normal = { textColor = new Color(0.78f, 0.82f, 0.86f) }
            };

            worldBuildingButtonStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = buttonFontSize
            };

            worldBuildingTextFieldStyle = new GUIStyle(GUI.skin.textField)
            {
                fontSize = bodyFontSize
            };
        }

        // World building hides the vertical gizmo axis (lockVerticalMovement), so the selected
        // object's exact height is edited here instead: type a value and press Enter, or nudge.
        private string worldBuildingHeightText = "";
        private GameObject worldBuildingHeightTarget;
        private const string WorldBuildingHeightControlName = "WorldBuildingHeightField";
        private const float WorldBuildingHeightNudgeStep = 0.1f;

        private float DrawWorldBuildingHeightRow(float x, float y, float width, GameObject target)
        {
            float s = WorldBuildingUiScale;
            float rowHeight = 26f * s;
            float labelWidth = 62f * s;
            float nudgeWidth = 30f * s;
            float gap = 6f * s;
            float fieldWidth = Mathf.Max(50f * s, width - labelWidth - (nudgeWidth + gap) * 2f);

            // While the field has focus the user's in-progress text wins; otherwise mirror the
            // object's live Y (selection changes, undo/redo, nudges).
            bool fieldFocused = GUI.GetNameOfFocusedControl() == WorldBuildingHeightControlName;
            if (!fieldFocused || worldBuildingHeightTarget != target)
            {
                worldBuildingHeightText = target.transform.position.y.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
                worldBuildingHeightTarget = target;
            }

            GUI.Label(new Rect(x, y, labelWidth, rowHeight), "Height:", worldBuildingBodyStyle);

            // Commit on Enter, before the text field consumes the key event.
            if (fieldFocused && Event.current.type == EventType.KeyDown &&
                (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter))
            {
                if (float.TryParse(worldBuildingHeightText, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float typedY))
                    runtimeEditorManager?.SetSelectedObjectHeight(typedY);
                GUI.FocusControl(null);
                Event.current.Use();
            }

            GUI.SetNextControlName(WorldBuildingHeightControlName);
            worldBuildingHeightText = GUI.TextField(
                new Rect(x + labelWidth, y, fieldWidth, rowHeight), worldBuildingHeightText, worldBuildingTextFieldStyle);

            float nudgeX = x + labelWidth + fieldWidth + gap;
            if (GUI.Button(new Rect(nudgeX, y, nudgeWidth, rowHeight), "-", worldBuildingButtonStyle))
                runtimeEditorManager?.SetSelectedObjectHeight(target.transform.position.y - WorldBuildingHeightNudgeStep);
            if (GUI.Button(new Rect(nudgeX + nudgeWidth + gap, y, nudgeWidth, rowHeight), "+", worldBuildingButtonStyle))
                runtimeEditorManager?.SetSelectedObjectHeight(target.transform.position.y + WorldBuildingHeightNudgeStep);

            return y + rowHeight + 4f * s;
        }

        private void DrawWorldBuildingOverlay()
        {
            EnsureWorldBuildingStyles();
            float s = WorldBuildingUiScale;

            if (worldBuildingOverlayMinimized)
                worldBuildingOverlayHeight = WorldBuildingOverlayHeaderHeight;

            Rect rect = GetWorldBuildingOverlayRect();
            HandleWorldBuildingPanelDragAndResize(rect, worldBuildingOverlayLayout, WorldBuildingOverlayHeaderHeight, allowResize: false);
            GUI.Box(rect, "");
            DrawWorldBuildingOverlayHeader(rect);

            if (!worldBuildingOverlayMinimized)
            {
                float contentX = rect.x + 16f * s;
                float contentWidth = rect.width - 32f * s;
                float y = rect.y + WorldBuildingOverlayHeaderHeight;

                string cameraMode = worldBuildingCameraController != null && worldBuildingCameraController.IsTopDownView()
                    ? "Top-down"
                    : "Free camera";
                string selectionText = runtimeEditorManager != null && runtimeEditorManager.CurrentSelectedObject != null
                    ? runtimeEditorManager.CurrentSelectedObject.name
                    : "none";

                float infoRowHeight = 24f * s;
                GUI.Label(new Rect(contentX, y, contentWidth, infoRowHeight),
                    $"Camera: {cameraMode}    Selected: {selectionText}", worldBuildingBodyStyle);
                y += infoRowHeight;

                GameObject heightTarget = runtimeEditorManager != null ? runtimeEditorManager.CurrentSelectedObject : null;
                if (heightTarget != null)
                    y = DrawWorldBuildingHeightRow(contentX, y, contentWidth, heightTarget);

                y += 6f * s;

                float buttonHeight = 34f * s;
                float buttonGap = 10f * s;
                float buttonWidth = (contentWidth - buttonGap) / 2f;

                if (GUI.Button(new Rect(contentX, y, buttonWidth, buttonHeight), "Back To Menu", worldBuildingButtonStyle))
                {
                    ExitWorldBuildingMode(true);
                    showPostTrialPrompt = true;
                }

                if (GUI.Button(new Rect(contentX + (buttonWidth + buttonGap), y, buttonWidth, buttonHeight), "Choose Scenario", worldBuildingButtonStyle))
                    OpenOnboardingFromPostTrial();

                y += buttonHeight + buttonGap;

                if (GUI.Button(new Rect(contentX, y, buttonWidth, buttonHeight), "Run Again", worldBuildingButtonStyle))
                    StartNextTrialFromPrompt();

                // Save the built world without having to switch scene/character first.
                if (GUI.Button(new Rect(contentX + (buttonWidth + buttonGap), y, buttonWidth, buttonHeight), "Save World...", worldBuildingButtonStyle))
                    OpenWorldBuildingSavePromptFromEditor();

                y += buttonHeight + buttonGap;

                int undoCount = runtimeEditorManager != null ? runtimeEditorManager.UndoCount : 0;
                int redoCount = runtimeEditorManager != null ? runtimeEditorManager.RedoCount : 0;
                bool hasSelection = runtimeEditorManager?.CurrentSelectedObject != null;
                float thirdWidth = (contentWidth - buttonGap * 2f) / 3f;

                // Ctrl+Z / Ctrl+Y / Del still work as hotkeys; the labels stay short so
                // the three actions fit one row.
                GUI.enabled = undoCount > 0;
                if (GUI.Button(new Rect(contentX, y, thirdWidth, buttonHeight), $"Undo [{undoCount}]", worldBuildingButtonStyle))
                    runtimeEditorManager?.UndoLastAction();

                GUI.enabled = redoCount > 0;
                if (GUI.Button(new Rect(contentX + (thirdWidth + buttonGap), y, thirdWidth, buttonHeight), $"Redo [{redoCount}]", worldBuildingButtonStyle))
                    runtimeEditorManager?.RedoLastAction();

                GUI.enabled = hasSelection;
                if (GUI.Button(new Rect(contentX + (thirdWidth + buttonGap) * 2f, y, thirdWidth, buttonHeight), "Delete", worldBuildingButtonStyle))
                    runtimeEditorManager?.DeleteSelectedObject();

                GUI.enabled = true;

                if (!string.IsNullOrEmpty(worldBuildingSaveStatus))
                {
                    y += buttonHeight + 6f * s;
                    float statusHeight = worldBuildingBodyStyle.CalcHeight(new GUIContent(worldBuildingSaveStatus), contentWidth);
                    GUI.Label(new Rect(contentX, y, contentWidth, statusHeight), worldBuildingSaveStatus, worldBuildingBodyStyle);
                    worldBuildingOverlayHeight = (y + statusHeight + 12f * s) - rect.y;
                }
                else
                {
                    worldBuildingOverlayHeight = (y + buttonHeight + 12f * s) - rect.y;
                }
            }

            DrawWorldBuildingSidePanels();
        }

        private void DrawWorldBuildingOverlayHeader(Rect rect)
        {
            float s = WorldBuildingUiScale;
            float toggleWidth = 30f * s;
            float toggleHeight = 24f * s;
            Rect toggleRect = new Rect(
                rect.xMax - toggleWidth - 10f * s,
                rect.y + 8f * s,
                toggleWidth,
                toggleHeight);

            GUI.Label(
                new Rect(rect.x + 16f * s, rect.y + 10f * s, rect.width - toggleWidth - 32f * s, 26f * s),
                "World Building",
                worldBuildingTitleStyle);

            RuntimeEditorManager.DrawMinimizeToggleButton(toggleRect, ref worldBuildingOverlayMinimized, worldBuildingStylesFontSize);
        }

        private void DrawWorldBuildingSidePanels()
        {
            EnsureWorldBuildingStyles();
            worldBuildingAddObjectsHeaderHeight = ComputeWorldBuildingAddObjectsHeaderHeight(
                GetWorldBuildingPanelWidth(worldBuildingObjectsPanelLayout, GetWorldBuildingSidePanelWidth()));
            EnsureWorldBuildingSpawnLibraryCurrent();

            GetWorldBuildingSidePanelRects(
                out Rect weatherRect,
                out Rect generateRect,
                out Rect charactersRect,
                out Rect objectsRect);

            // Handle drag/resize before any drawing, in reverse draw order so the visually
            // topmost panel (drawn last) gets first claim on mouse events when panels overlap.
            HandleWorldBuildingPanelDragAndResize(
                objectsRect,
                worldBuildingObjectsPanelLayout,
                worldBuildingAddObjectsMinimized ? objectsRect.height : GetWorldBuildingAddObjectsHeaderHeight(),
                allowResize: !worldBuildingAddObjectsMinimized,
                gripOnRight: true);
            HandleWorldBuildingPanelDragAndResize(
                charactersRect,
                worldBuildingCharactersPanelLayout,
                Mathf.Min(WorldBuildingSidePanelHeaderHeight, charactersRect.height),
                allowResize: !worldBuildingAddCharactersMinimized);
            HandleWorldBuildingPanelDragAndResize(
                generateRect,
                worldBuildingGeneratePanelLayout,
                Mathf.Min(WorldBuildingSidePanelHeaderHeight, generateRect.height),
                allowResize: !worldBuildingGenerateObjectsMinimized);
            HandleWorldBuildingPanelDragAndResize(
                weatherRect,
                worldBuildingWeatherPanelLayout,
                Mathf.Min(WorldBuildingSidePanelHeaderHeight, weatherRect.height),
                allowResize: !worldBuildingWeatherMinimized);

            IReadOnlyList<WorldBuildingSpawnUiRow> objectRows = WorldBuildingSpawnLibrary.LastObjectUiRows;
            IReadOnlyList<WorldBuildingSpawnUiRow> characterRows = WorldBuildingSpawnLibrary.LastCharacterUiRows;

            int previousDepth = GUI.depth;
            GUI.depth = -1000;
            DrawWorldBuildingWeatherPanel(weatherRect);
            DrawWorldBuildingPanelGrip(weatherRect, !worldBuildingWeatherMinimized);
            DrawWorldBuildingGenerateObjectsPanel(generateRect);
            DrawWorldBuildingPanelGrip(generateRect, !worldBuildingGenerateObjectsMinimized);
            DrawWorldBuildingAddCharactersPanel(charactersRect, characterRows);
            DrawWorldBuildingPanelGrip(charactersRect, !worldBuildingAddCharactersMinimized);
            DrawWorldBuildingAddObjectsPanel(objectsRect, objectRows);
            DrawWorldBuildingPanelGrip(objectsRect, !worldBuildingAddObjectsMinimized, gripOnRight: true);
            GUI.depth = previousDepth;
        }

        private void DrawWorldBuildingAddObjectsPanel(Rect panelRect, IReadOnlyList<WorldBuildingSpawnUiRow> rows)
        {
            GUI.Box(panelRect, "");
            DrawWorldBuildingSidePanelHeader(
                panelRect,
                "Add Objects",
                WorldBuildingAddObjectsSubtitle,
                ref worldBuildingAddObjectsMinimized,
                GetWorldBuildingAddObjectsHeaderHeight(),
                gripOnRight: true);

            if (worldBuildingAddObjectsMinimized)
                return;

            if (rows == null || rows.Count == 0)
            {
                float s = WorldBuildingUiScale;
                GUI.Label(
                    new Rect(
                        panelRect.x + 14f * s,
                        panelRect.y + GetWorldBuildingAddObjectsHeaderHeight() + 12f * s,
                        panelRect.width - 28f * s,
                        64f * s),
                    "No object prefabs registered.\nExpected assets under Resources/WorldBuildingSpawns with optional thumbnails in Resources/WorldBuildingUI.",
                    worldBuildingBodyStyle);
                return;
            }

            DrawWorldBuildingSpawnCardGrid(
                panelRect,
                GetWorldBuildingAddObjectsHeaderHeight(),
                rows,
                ref worldBuildingAddObjectsScroll,
                ref worldBuildingSpawnSearch,
                "Search objects and characters...",
                true);
        }

        private void DrawWorldBuildingAddCharactersPanel(Rect panelRect, IReadOnlyList<WorldBuildingSpawnUiRow> rows)
        {
            GUI.Box(panelRect, "");
            DrawWorldBuildingSidePanelHeader(
                panelRect,
                "Add Characters",
                null,
                ref worldBuildingAddCharactersMinimized,
                WorldBuildingSidePanelHeaderHeight);

            if (worldBuildingAddCharactersMinimized)
                return;

            if (rows == null || rows.Count == 0)
            {
                float s = WorldBuildingUiScale;
                GUI.Label(
                    new Rect(
                        panelRect.x + 14f * s,
                        panelRect.y + WorldBuildingSidePanelHeaderHeight + 8f * s,
                        panelRect.width - 28f * s,
                        40f * s),
                    "No character prefabs with thumbnails yet.",
                    worldBuildingBodyStyle);
                return;
            }

            DrawWorldBuildingSpawnCardGrid(
                panelRect,
                WorldBuildingSidePanelHeaderHeight,
                rows,
                ref worldBuildingAddCharactersScroll,
                ref worldBuildingSpawnSearch,
                "Search objects and characters...",
                worldBuildingAddObjectsMinimized);
        }

        private void DrawWorldBuildingGenerateObjectsPanel(Rect panelRect)
        {
            GUI.Box(panelRect, "");
            DrawWorldBuildingSidePanelHeader(
                panelRect,
                "Generate Objects",
                null,
                ref worldBuildingGenerateObjectsMinimized,
                WorldBuildingSidePanelHeaderHeight);

            if (worldBuildingGenerateObjectsMinimized)
                return;

            DrawWorldBuildingGenerateObjectsBody(panelRect);
        }

        /// <summary>
        /// The Weather panel: picks the weather the built world runs with. Clear/Fog switch
        /// applies live (fog previews in the free camera; the orthographic top-down map is
        /// kept fog-free by FogController so editing stays possible), Light/Medium/Heavy set
        /// the fog density. The choice is captured into scene.json by
        /// WorldBuildingScenarioStore and re-applied on restore. Rain/Snow are placeholders
        /// until those effects exist.
        /// </summary>
        private void DrawWorldBuildingWeatherPanel(Rect panelRect)
        {
            GUI.Box(panelRect, "");
            DrawWorldBuildingSidePanelHeader(
                panelRect,
                "Weather",
                null,
                ref worldBuildingWeatherMinimized,
                WorldBuildingSidePanelHeaderHeight);

            if (worldBuildingWeatherMinimized)
                return;

            float s = WorldBuildingUiScale;
            float x = panelRect.x + 14f * s;
            float w = panelRect.width - 28f * s;
            float y = panelRect.y + WorldBuildingSidePanelHeaderHeight;

            worldBuildingWeatherHintHeight = worldBuildingSubtitleStyle.CalcHeight(
                new GUIContent(WorldBuildingWeatherHint), w);
            GUI.Label(new Rect(x, y, w, worldBuildingWeatherHintHeight), WorldBuildingWeatherHint, worldBuildingSubtitleStyle);
            y += worldBuildingWeatherHintHeight + 8f * s;

            Weather.FogController fog = Weather.FogController.Instance;
            bool fogOn = fog != null && fog.FogActive;

            float rowHeight = 34f * s;
            float gap = 8f * s;
            float buttonWidth = (w - gap * 3f) / 4f;

            if (GUI.Toggle(new Rect(x, y, buttonWidth, rowHeight), !fogOn, "Clear", worldBuildingButtonStyle) && fogOn)
                fog.SetFog(false);
            if (GUI.Toggle(new Rect(x + (buttonWidth + gap), y, buttonWidth, rowHeight), fogOn, "Fog", worldBuildingButtonStyle) && !fogOn)
                Weather.FogController.EnsureInstance().SetFog(true);

            GUI.enabled = false;
            GUI.Toggle(new Rect(x + (buttonWidth + gap) * 2f, y, buttonWidth, rowHeight), false, "Rain", worldBuildingButtonStyle);
            GUI.Toggle(new Rect(x + (buttonWidth + gap) * 3f, y, buttonWidth, rowHeight), false, "Snow", worldBuildingButtonStyle);
            GUI.enabled = true;
            y += rowHeight + gap;

            if (!fogOn)
                return;

            string[] presetLabels = { "Light", "Medium", "Heavy" };
            float presetWidth = (w - gap * (presetLabels.Length - 1)) / presetLabels.Length;
            for (int i = 0; i < presetLabels.Length; i++)
            {
                bool active = fog.presetIndex == i;
                if (GUI.Toggle(new Rect(x + (presetWidth + gap) * i, y, presetWidth, rowHeight), active, presetLabels[i], worldBuildingButtonStyle) && !active)
                    fog.SetPreset(i);
            }
        }

        private float GetWorldBuildingWeatherPanelHeight()
        {
            if (worldBuildingWeatherMinimized)
                return WorldBuildingSidePanelHeaderHeight;

            float s = WorldBuildingUiScale;
            // Measured during the previous draw (needs OnGUI); fall back to two lines until then.
            float hintHeight = worldBuildingWeatherHintHeight > 0f ? worldBuildingWeatherHintHeight : 36f * s;
            bool fogOn = Weather.FogController.Instance != null && Weather.FogController.Instance.FogActive;
            float rowHeight = 34f * s;
            float gap = 8f * s;
            float body = hintHeight + gap + rowHeight + (fogOn ? gap + rowHeight : 0f);
            return WorldBuildingSidePanelHeaderHeight + body + 12f * s;
        }

        private void DrawWorldBuildingSidePanelHeader(
            Rect panelRect,
            string title,
            string subtitle,
            ref bool minimized,
            float expandedHeaderHeight,
            bool gripOnRight = false)
        {
            float s = WorldBuildingUiScale;
            float toggleWidth = 30f * s;
            float toggleHeight = 24f * s;
            // With the resize grip in the top-right corner the toggle shifts left of it.
            float gripReserve = gripOnRight ? 20f * s : 0f;
            Rect toggleRect = new Rect(
                panelRect.xMax - toggleWidth - 10f * s - gripReserve,
                panelRect.y + 8f * s,
                toggleWidth,
                toggleHeight);

            GUI.Label(
                new Rect(panelRect.x + 14f * s, panelRect.y + 10f * s, panelRect.width - toggleWidth - 32f * s - gripReserve, 26f * s),
                title,
                worldBuildingTitleStyle);

            if (!minimized && !string.IsNullOrEmpty(subtitle))
            {
                GUI.Label(
                    new Rect(panelRect.x + 14f * s, panelRect.y + 36f * s, panelRect.width - 28f * s, expandedHeaderHeight - 44f * s),
                    subtitle,
                    worldBuildingSubtitleStyle);
            }

            RuntimeEditorManager.DrawMinimizeToggleButton(toggleRect, ref minimized, worldBuildingStylesFontSize);
        }

        // Header height for the Add Objects panel depends on how the subtitle wraps at the current
        // panel width and font scale, so it is measured rather than fixed.
        private float ComputeWorldBuildingAddObjectsHeaderHeight(float panelWidth)
        {
            float s = WorldBuildingUiScale;
            float subtitleHeight = worldBuildingSubtitleStyle.CalcHeight(
                new GUIContent(WorldBuildingAddObjectsSubtitle),
                panelWidth - 28f * s);
            return 36f * s + subtitleHeight + 8f * s;
        }

        private float GetWorldBuildingAddObjectsHeaderHeight()
        {
            if (worldBuildingAddObjectsHeaderHeight > 0f)
                return worldBuildingAddObjectsHeaderHeight;

            // Fallback before the first OnGUI pass has measured the subtitle.
            return WorldBuildingSidePanelHeaderHeight + 40f * WorldBuildingUiScale;
        }

        private static float GetWorldBuildingSpawnPaletteContentWidth(float panelWidth)
        {
            float innerPad = 12f * WorldBuildingUiScale;
            const float scrollBarReserve = 18f;
            return Mathf.Max(1f, panelWidth - innerPad * 2f - scrollBarReserve);
        }

        private static int GetWorldBuildingSpawnPaletteColumnCount(float contentWidth)
        {
            float gap = WorldBuildingSpawnPaletteCardGap;
            int columns = Mathf.FloorToInt((contentWidth + gap) / (WorldBuildingSpawnPalettePreferredCardWidth + gap));

            if (contentWidth >= WorldBuildingSpawnPaletteMinimumTwoColumnWidth * WorldBuildingPaletteMetricScale)
                columns = Mathf.Max(columns, 2);

            return Mathf.Clamp(Mathf.Max(1, columns), 1, 6);
        }

        private bool DrawWorldBuildingSpawnSearchField(
            Rect panelRect,
            float y,
            float innerPad,
            ref string search,
            string placeholder)
        {
            float s = WorldBuildingUiScale;
            string previous = search ?? string.Empty;
            bool hasSearch = !string.IsNullOrWhiteSpace(previous);
            float fieldHeight = 28f * s;
            float clearSize = 26f * s;
            Rect fieldRect = new Rect(
                panelRect.x + innerPad,
                y + 4f * s,
                panelRect.width - innerPad * 2f,
                fieldHeight);
            Rect textRect = hasSearch
                ? new Rect(fieldRect.x, fieldRect.y, fieldRect.width - clearSize - 6f * s, fieldRect.height)
                : fieldRect;

            string next = GUI.TextField(textRect, previous, 64, worldBuildingTextFieldStyle);
            if (!hasSearch && Event.current.type == EventType.Repaint)
            {
                Color previousColor = GUI.color;
                GUI.color = new Color(0.68f, 0.72f, 0.76f, 0.78f);
                GUI.Label(
                    new Rect(textRect.x + 7f * s, textRect.y + 4f * s, textRect.width - 14f * s, textRect.height),
                    placeholder,
                    worldBuildingSubtitleStyle);
                GUI.color = previousColor;
            }

            if (hasSearch)
            {
                Rect clearRect = new Rect(fieldRect.xMax - clearSize, fieldRect.y, clearSize, clearSize);
                if (GUI.Button(clearRect, "x", worldBuildingButtonStyle))
                    next = string.Empty;
            }

            bool changed = !string.Equals(previous, next, StringComparison.Ordinal);
            search = next;
            return changed;
        }

        private static int CountWorldBuildingFilteredRows(
            IReadOnlyList<WorldBuildingSpawnUiRow> rows,
            string search)
        {
            if (rows == null)
                return 0;

            int count = 0;
            for (int i = 0; i < rows.Count; i++)
            {
                if (WorldBuildingRowMatchesSearch(rows[i], search))
                    count++;
            }

            return count;
        }

        private static bool WorldBuildingRowMatchesSearch(WorldBuildingSpawnUiRow row, string search)
        {
            if (row == null)
                return false;
            if (string.IsNullOrWhiteSpace(search))
                return true;

            string haystack = (row.DisplayName ?? string.Empty) + " "
                              + (row.SpawnId ?? string.Empty) + " "
                              + Path.GetFileNameWithoutExtension(row.ImportGlbPath ?? string.Empty);
            string[] tokens = search.Split(
                new[] { ' ', '\t', '_', '-', '.', '/', '\\' },
                StringSplitOptions.RemoveEmptyEntries);

            for (int i = 0; i < tokens.Length; i++)
            {
                if (haystack.IndexOf(tokens[i], StringComparison.OrdinalIgnoreCase) < 0)
                    return false;
            }

            return tokens.Length > 0;
        }

        private void DrawWorldBuildingSpawnCardGrid(
            Rect panelRect,
            float headerHeight,
            IReadOnlyList<WorldBuildingSpawnUiRow> rows,
            ref Vector2 scroll,
            ref string search,
            string searchPlaceholder,
            bool drawSearchField)
        {
            int totalCards = rows?.Count ?? 0;
            if (totalCards == 0)
                return;

            float s = WorldBuildingUiScale;
            float innerPad = 12f * s;
            float contentY = panelRect.y + headerHeight;
            float searchBlockHeight = drawSearchField ? WorldBuildingSpawnPaletteSearchBlockHeight : 0f;
            if (drawSearchField &&
                DrawWorldBuildingSpawnSearchField(
                        panelRect,
                        contentY,
                        innerPad,
                        ref search,
                        searchPlaceholder))
            {
                worldBuildingAddObjectsScroll = Vector2.zero;
                worldBuildingAddCharactersScroll = Vector2.zero;
            }
            contentY += searchBlockHeight;

            int visibleCards = CountWorldBuildingFilteredRows(rows, search);
            if (visibleCards == 0)
            {
                GUI.Label(
                    new Rect(
                        panelRect.x + innerPad,
                        contentY + 4f * s,
                        panelRect.width - innerPad * 2f,
                        40f * s),
                    "No matches.",
                    worldBuildingSubtitleStyle);
                return;
            }

            // The panel height already encodes the auto min/max clamps (or the user's resize),
            // so the viewport simply fills whatever space the panel offers.
            float availableHeight = panelRect.height - headerHeight - searchBlockHeight - 16f * s;
            if (availableHeight <= 0f)
                return;

            float scrollBarReserve = 18f;
            Rect viewRect = new Rect(
                panelRect.x + innerPad,
                contentY,
                panelRect.width - innerPad * 2f,
                availableHeight);
            float innerW = viewRect.width - scrollBarReserve;
            int columnCount = GetWorldBuildingSpawnPaletteColumnCount(innerW);
            int rowCount = (visibleCards + columnCount - 1) / columnCount;
            float scrollInnerHeight = rowCount * (WorldBuildingSpawnPaletteCardHeight + WorldBuildingSpawnPaletteCardGap)
                                      + WorldBuildingSpawnPaletteCardGap;
            float scrollViewportH = Mathf.Min(scrollInnerHeight, availableHeight);
            if (scrollViewportH <= 0f)
                return;

            viewRect.height = scrollViewportH;
            float cardW = (innerW - (columnCount - 1) * WorldBuildingSpawnPaletteCardGap)
                          / columnCount;
            float contentW = viewRect.width - scrollBarReserve;
            float contentH = Mathf.Max(scrollInnerHeight, viewRect.height);
            Rect contentRect = new Rect(0f, 0f, contentW, contentH);

            scroll = GUI.BeginScrollView(viewRect, scroll, contentRect, false, true);
            int slot = 0;
            for (int i = 0; i < totalCards; i++)
            {
                WorldBuildingSpawnUiRow row = rows[i];
                if (!WorldBuildingRowMatchesSearch(row, search))
                    continue;

                int r = slot / columnCount;
                int c = slot % columnCount;
                float cardX = c * (cardW + WorldBuildingSpawnPaletteCardGap);
                float cardY = WorldBuildingSpawnPaletteCardGap + r * (WorldBuildingSpawnPaletteCardHeight + WorldBuildingSpawnPaletteCardGap);
                Rect cardRect = new Rect(cardX, cardY, cardW, WorldBuildingSpawnPaletteCardHeight);

                DrawSpawnPreviewCard(cardRect, row);
                slot++;
            }

            GUI.EndScrollView();
        }

        private void DrawWorldBuildingGenerateObjectsBody(Rect panelRect)
        {
            float s = WorldBuildingUiScale;
            float pad = 14f * s;
            float innerW = panelRect.width - pad * 2f;
            float y = panelRect.y + WorldBuildingSidePanelHeaderHeight + 6f * s;

            if (runtimeEditorManager != null)
                EnsureWorldBuildingSpawnPrefabsConfigured();

            GUI.Label(new Rect(panelRect.x + pad, y, innerW, 22f * s), "Describe an object:", worldBuildingBodyStyle);
            y += 26f * s;

            aiGenerationPrompt = GUI.TextField(new Rect(panelRect.x + pad, y, innerW, 26f * s), aiGenerationPrompt ?? string.Empty, worldBuildingTextFieldStyle);
            y += 34f * s;

            bool canRunMeshyAction = !IsMeshyModelLoading();

            GUI.enabled = canRunMeshyAction;
            if (GUI.Button(new Rect(panelRect.x + pad, y, innerW, 32f * s), "Generate Object", worldBuildingButtonStyle))
                GenerateObjectFromPrompt();
            GUI.enabled = true;
            y += 38f * s;

            if (IsMeshyModelLoading())
            {
                GUI.Label(new Rect(panelRect.x + pad, y, innerW, WorldBuildingGenerateObjectsLoadingLabelHeight), "Loading...", worldBuildingBodyStyle);
                y += WorldBuildingGenerateObjectsLoadingLabelHeight;
            }

            if (!string.IsNullOrEmpty(worldBuildingGenerateStatus))
            {
                worldBuildingGenerateStatusHeight =
                    worldBuildingSubtitleStyle.CalcHeight(new GUIContent(worldBuildingGenerateStatus), innerW);
                GUI.Label(
                    new Rect(panelRect.x + pad, y, innerW, worldBuildingGenerateStatusHeight),
                    worldBuildingGenerateStatus,
                    worldBuildingSubtitleStyle);
            }
            else
            {
                worldBuildingGenerateStatusHeight = -1f;
            }
        }

        private bool IsMeshyModelLoading()
        {
            return meshyGlbImportInProgress
                || (meshyGenerator != null && meshyGenerator.IsGenerating);
        }

        private void GenerateObjectFromPrompt()
        {
            Debug.Log("[GenerateModel] Generate Object button clicked.");

            string prompt = (aiGenerationPrompt ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(prompt))
            {
                worldBuildingGenerateStatus = "Type a description first.";
                Debug.LogWarning("[GenerateModel] Text field is empty —enter a description first.");
                return;
            }

            // 1) Reuse a prefab already in the palette when the prompt is describing one —
            // instant, free, and it comes with the collider/setup the AI import lacks.
            // Pressing Generate again on the same prompt overrides the match and generates.
            bool forceGenerate = string.Equals(prompt, lastLibraryMatchPrompt, StringComparison.OrdinalIgnoreCase);
            if (!forceGenerate && TrySpawnExistingLibraryMatch(prompt))
                return;

            lastLibraryMatchPrompt = null;

            // 2) Reuse a model previously generated for this exact prompt.
            string localPath = MeshyGlbSceneImporter.GetSavedGlbPath(prompt);
            if (!forceGenerate && File.Exists(localPath))
            {
                worldBuildingGenerateStatus = $"Reusing the model generated earlier for \"{prompt}\".";
                Debug.Log($"[GenerateModel] Saved GLB found —importing instead of generating: {localPath}");
                StartImportSavedGlb(localPath, prompt);
                return;
            }

            if (meshyGenerator == null)
            {
                worldBuildingGenerateStatus = "Generator unavailable —check the console.";
                Debug.LogError("[GenerateModel] No GenerateModel component found on SessionReviewManager GameObject.");
                return;
            }

            // 3) Nothing to reuse —generate.
            worldBuildingGenerateStatus = $"Generating \"{prompt}\"... this can take a minute.";
            Debug.Log($"[GenerateModel] No library match and no saved GLB —calling Meshy for \"{prompt}\"");
            meshyGenerator.Generate(prompt);
        }

        /// <summary>
        /// Places the palette prefab the prompt is asking for, if there is a confident match.
        /// Returns false when the prompt describes something the library does not have.
        /// </summary>
        private bool TrySpawnExistingLibraryMatch(string prompt)
        {
            EnsureWorldBuildingSpawnLibraryCurrent();

            WorldBuildingSpawnUiRow match = WorldBuildingSpawnLibrary.FindBestMatch(prompt);
            if (match == null || string.IsNullOrEmpty(match.SpawnId))
                return false;

            if (runtimeEditorManager == null)
                runtimeEditorManager = RuntimeEditorManager.Instance;

            if (runtimeEditorManager == null)
            {
                Debug.LogWarning("[GenerateModel] Library match found but RuntimeEditorManager is missing —falling through to generation.");
                return false;
            }

            SpawnWorldBuildingRow(match);
            lastLibraryMatchPrompt = prompt;
            worldBuildingGenerateStatus =
                $"Added \"{match.DisplayName}\" from the library. Press Generate again to build a new model instead.";
            Debug.Log($"[GenerateModel] Prompt \"{prompt}\" matched existing palette entry \"{match.DisplayName}\" —spawned instead of generating.");
            return true;
        }

        private void OnMeshyGenerateComplete(GenerateModelResult result)
        {
            if (result == null)
                return;

            if (!result.Success)
            {
                worldBuildingGenerateStatus = $"Generation failed: {result.Error}";
                Debug.LogWarning($"[GenerateModel] Generation failed —not importing: {result.Error}");
                return;
            }

            if (!inWorldBuildingMode)
            {
                Debug.LogWarning("[GenerateModel] Model ready but World Building is not active —GLB saved to disk only.");
                return;
            }

            if (string.IsNullOrEmpty(result.LocalGlbPath) || !File.Exists(result.LocalGlbPath))
            {
                worldBuildingGenerateStatus = "Generation finished but the model file is missing —check the console.";
                Debug.LogError($"[GenerateModel] GLB missing after generation: {result.LocalGlbPath}");
                return;
            }

            worldBuildingGenerateStatus = $"Generated \"{result.Prompt}\" —placing it in the scene.";
            WorldBuildingSpawnLibrary.RefreshFromResources(force: true);
            EnsureWorldBuildingSpawnPrefabsConfigured();
            StartImportSavedGlb(result.LocalGlbPath, result.Prompt);
        }

        private void StartImportSavedGlb(string localGlbPath, string displayName)
        {
            if (meshyGlbImportInProgress)
            {
                Debug.LogWarning("[GenerateModel] GLB import already in progress.");
                return;
            }

            if (runtimeEditorManager == null)
                runtimeEditorManager = RuntimeEditorManager.Instance;

            if (runtimeEditorManager == null)
            {
                Debug.LogError("[GenerateModel] RuntimeEditorManager not found —cannot spawn imported GLB.");
                return;
            }

            if (!runtimeEditorManager.isEditorActive)
            {
                Debug.LogError("[GenerateModel] Editor mode is not active —enter World Building first.");
                return;
            }

            meshyGlbImportInProgress = true;
            Debug.Log($"[GenerateModel] Importing saved GLB: {localGlbPath}");
            StartCoroutine(ImportSavedGlbCoroutine(localGlbPath, displayName));
        }

        private IEnumerator ImportSavedGlbCoroutine(string localGlbPath, string displayName)
        {
            yield return MeshyGlbSceneImporter.ImportAndSpawn(
                localGlbPath,
                displayName,
                runtimeEditorManager,
                error => Debug.LogError($"[GenerateModel] Import/spawn failed: {error}"));
            meshyGlbImportInProgress = false;
        }

        private float GetWorldBuildingSpawnCardPanelHeight(
            IReadOnlyList<WorldBuildingSpawnUiRow> rows,
            bool minimized,
            float expandedHeaderHeight,
            float panelWidth,
            string search,
            bool includeSearchField)
        {
            if (minimized)
                return WorldBuildingSidePanelHeaderHeight;

            int totalCards = rows?.Count ?? 0;
            if (totalCards == 0)
                return expandedHeaderHeight + WorldBuildingSidePanelEmptyBodyHeight;

            float s = WorldBuildingUiScale;
            float searchBlockHeight = includeSearchField ? WorldBuildingSpawnPaletteSearchBlockHeight : 0f;
            float innerW = GetWorldBuildingSpawnPaletteContentWidth(panelWidth);
            int columnCount = GetWorldBuildingSpawnPaletteColumnCount(innerW);
            int visibleCards = CountWorldBuildingFilteredRows(rows, search);
            if (visibleCards == 0)
                return expandedHeaderHeight + searchBlockHeight + WorldBuildingSidePanelEmptyBodyHeight;

            int rowCount = (visibleCards + columnCount - 1) / columnCount;
            float scrollAreaMin = WorldBuildingSpawnCardPanelMinScrollHeight;
            float scrollAreaMax = Mathf.Min(280f * s, ReviewUiScale.Height * 0.38f);
            float scrollInnerHeight = rowCount * (WorldBuildingSpawnPaletteCardHeight + WorldBuildingSpawnPaletteCardGap)
                                      + WorldBuildingSpawnPaletteCardGap;
            float scrollViewportH = Mathf.Clamp(scrollInnerHeight, scrollAreaMin, scrollAreaMax);
            return expandedHeaderHeight + searchBlockHeight + scrollViewportH + 16f * s;
        }

        private static float GetWorldBuildingSpawnCardPanelMinimumExpandedHeight(
            float expandedHeaderHeight,
            bool includeSearchField)
        {
            float searchBlockHeight = includeSearchField ? WorldBuildingSpawnPaletteSearchBlockHeight : 0f;
            return expandedHeaderHeight + searchBlockHeight
                   + WorldBuildingSpawnCardPanelMinScrollHeight + 16f * WorldBuildingUiScale;
        }

        private void EnsureWorldBuildingSpawnLibraryCurrent()
        {
            if (runtimeEditorManager != null)
                EnsureWorldBuildingSpawnPrefabsConfigured();
            else
                WorldBuildingSpawnLibrary.RefreshFromResources();
        }

        private float GetWorldBuildingGenerateObjectsPanelHeight()
        {
            if (worldBuildingGenerateObjectsMinimized)
                return WorldBuildingSidePanelHeaderHeight;

            float bodyHeight = WorldBuildingGenerateObjectsBodyHeight;
            if (IsMeshyModelLoading())
                bodyHeight += WorldBuildingGenerateObjectsLoadingLabelHeight;

            if (!string.IsNullOrEmpty(worldBuildingGenerateStatus))
            {
                // Measured during the previous draw; fall back to two lines until then.
                bodyHeight += worldBuildingGenerateStatusHeight > 0f
                    ? worldBuildingGenerateStatusHeight + 6f * WorldBuildingUiScale
                    : WorldBuildingGenerateObjectsLoadingLabelHeight * 2f;
            }

            return WorldBuildingSidePanelHeaderHeight + bodyHeight + 12f * WorldBuildingUiScale;
        }

        private static float GetWorldBuildingSidePanelWidth()
        {
            // Keep the docked palette compact when UI scale grows; it should reveal more rows,
            // not become a huge side panel.
            return Mathf.Min(WorldBuildingSidePanelWidth, WorldBuildingSidePanelMaxWidth, ReviewUiScale.Width * 0.42f);
        }

        private static Rect GetWorldBuildingOverlayRect()
        {
            if (worldBuildingOverlayLayout.hasCustomPosition)
            {
                Vector2 pos = ClampWorldBuildingPanelPosition(worldBuildingOverlayLayout.position, WorldBuildingOverlayWidth);
                worldBuildingOverlayLayout.position = pos;
                return new Rect(pos.x, pos.y, WorldBuildingOverlayWidth, worldBuildingOverlayHeight);
            }

            float margin = 24f * WorldBuildingUiScale;
            return new Rect(margin, margin, WorldBuildingOverlayWidth, worldBuildingOverlayHeight);
        }

        private bool IsMouseOverWorldBuildingUi()
        {
            Vector2 guiPoint = ReviewUiScale.GuiMousePosition();

            if (GetWorldBuildingOverlayRect().Contains(guiPoint))
                return true;

            if (UiScaleController.ControlContains(guiPoint))
                return true;

            if (runtimeEditorManager != null && runtimeEditorManager.ContainsWorldBuildingHelperUi(guiPoint))
                return true;

            return ContainsWorldBuildingSidePanel(guiPoint);
        }

        public bool IsPointerOverWorldBuildingUi()
        {
            // While the save modal is up it owns every click, so the editor underneath must
            // not select/spawn/drag anything.
            if (showWorldBuildingSavePrompt)
                return true;

            return inWorldBuildingMode && IsMouseOverWorldBuildingUi();
        }

        private bool ContainsWorldBuildingSidePanel(Vector2 guiPoint)
        {
            EnsureWorldBuildingSpawnLibraryCurrent();

            GetWorldBuildingSidePanelRects(
                out Rect weatherRect,
                out Rect generateRect,
                out Rect charactersRect,
                out Rect objectsRect);

            return weatherRect.Contains(guiPoint) ||
                   generateRect.Contains(guiPoint) ||
                   charactersRect.Contains(guiPoint) ||
                   objectsRect.Contains(guiPoint);
        }

        private void GetWorldBuildingSidePanelRects(out Rect weatherRect, out Rect generateRect, out Rect charactersRect, out Rect objectsRect)
        {
            float panelWidth = GetWorldBuildingSidePanelWidth();
            IReadOnlyList<WorldBuildingSpawnUiRow> objectRows = WorldBuildingSpawnLibrary.LastObjectUiRows;
            IReadOnlyList<WorldBuildingSpawnUiRow> characterRows = WorldBuildingSpawnLibrary.LastCharacterUiRows;
            float weatherWidth = GetWorldBuildingPanelWidth(worldBuildingWeatherPanelLayout, panelWidth);
            float generateWidth = GetWorldBuildingPanelWidth(worldBuildingGeneratePanelLayout, panelWidth);
            float charactersWidth = GetWorldBuildingPanelWidth(worldBuildingCharactersPanelLayout, panelWidth);
            float objectsWidth = GetWorldBuildingPanelWidth(worldBuildingObjectsPanelLayout, panelWidth);
            bool characterPanelShowsSearch = worldBuildingAddObjectsMinimized;

            float weatherHeight = GetWorldBuildingWeatherPanelHeight();
            float generateHeight = GetWorldBuildingGenerateObjectsPanelHeight();
            float charactersHeight = GetWorldBuildingSpawnCardPanelHeight(
                characterRows,
                worldBuildingAddCharactersMinimized,
                WorldBuildingSidePanelHeaderHeight,
                charactersWidth,
                worldBuildingSpawnSearch,
                characterPanelShowsSearch);
            float objectsHeight = GetWorldBuildingSpawnCardPanelHeight(
                objectRows,
                worldBuildingAddObjectsMinimized,
                GetWorldBuildingAddObjectsHeaderHeight(),
                objectsWidth,
                worldBuildingSpawnSearch,
                true);

            // User resizes override the content-driven heights while expanded. The generate
            // panel keeps its content height —its grip only changes width.
            float maxPanelHeight = ReviewUiScale.Height - 16f;
            if (!worldBuildingAddCharactersMinimized && worldBuildingCharactersPanelLayout.hasCustomSize)
                charactersHeight = Mathf.Clamp(
                    worldBuildingCharactersPanelLayout.size.y,
                    GetWorldBuildingSpawnCardPanelMinimumExpandedHeight(WorldBuildingSidePanelHeaderHeight, characterPanelShowsSearch),
                    maxPanelHeight);
            if (!worldBuildingAddObjectsMinimized && worldBuildingObjectsPanelLayout.hasCustomSize)
                objectsHeight = Mathf.Clamp(
                    worldBuildingObjectsPanelLayout.size.y,
                    GetWorldBuildingSpawnCardPanelMinimumExpandedHeight(GetWorldBuildingAddObjectsHeaderHeight(), true),
                    maxPanelHeight);

            // Dragged-away (floating) panels leave their docked stack; only docked panels
            // share the stack height budget and overflow reduction. The Add Objects palette
            // docks on the LEFT edge (the tool panels keep the right), so it budgets its
            // height separately against the space below the docked overlay panel.
            bool weatherFloating = worldBuildingWeatherPanelLayout.hasCustomPosition;
            bool generateFloating = worldBuildingGeneratePanelLayout.hasCustomPosition;
            bool charactersFloating = worldBuildingCharactersPanelLayout.hasCustomPosition;
            bool objectsFloating = worldBuildingObjectsPanelLayout.hasCustomPosition;

            float maxStackHeight = Mathf.Max(
                WorldBuildingSidePanelHeaderHeight,
                ReviewUiScale.Height - WorldBuildingSidePanelMargin * 2f);
            int dockedCount = (weatherFloating ? 0 : 1) + (generateFloating ? 0 : 1)
                              + (charactersFloating ? 0 : 1);
            float stackHeight = (weatherFloating ? 0f : weatherHeight)
                                + (generateFloating ? 0f : generateHeight)
                                + (charactersFloating ? 0f : charactersHeight)
                                + WorldBuildingSidePanelGap * Mathf.Max(0, dockedCount - 1);
            float overflow = Mathf.Max(0f, stackHeight - maxStackHeight);
            if (!charactersFloating)
                ReducePanelHeightForOverflow(
                    ref charactersHeight,
                    worldBuildingAddCharactersMinimized,
                    GetWorldBuildingSpawnCardPanelMinimumExpandedHeight(WorldBuildingSidePanelHeaderHeight, characterPanelShowsSearch),
                    ref overflow);
            if (!generateFloating)
                ReducePanelHeightForOverflow(
                    ref generateHeight,
                    worldBuildingGenerateObjectsMinimized,
                    WorldBuildingSidePanelHeaderHeight,
                    ref overflow);
            if (!weatherFloating)
                ReducePanelHeightForOverflow(
                    ref weatherHeight,
                    worldBuildingWeatherMinimized,
                    WorldBuildingSidePanelHeaderHeight,
                    ref overflow);

            if (!objectsFloating)
            {
                float leftTop = worldBuildingOverlayLayout.hasCustomPosition
                    ? WorldBuildingSidePanelMargin
                    : GetWorldBuildingOverlayRect().yMax + WorldBuildingSidePanelGap;
                float maxObjectsHeight = Mathf.Max(
                    WorldBuildingSidePanelHeaderHeight,
                    ReviewUiScale.Height - WorldBuildingSidePanelMargin - leftTop);
                float objectsOverflow = Mathf.Max(0f, objectsHeight - maxObjectsHeight);
                ReducePanelHeightForOverflow(
                    ref objectsHeight,
                    worldBuildingAddObjectsMinimized,
                    GetWorldBuildingSpawnCardPanelMinimumExpandedHeight(GetWorldBuildingAddObjectsHeaderHeight(), true),
                    ref objectsOverflow);
            }

            float yBottom = ReviewUiScale.Height - WorldBuildingSidePanelMargin;
            weatherRect = PlaceWorldBuildingSidePanel(worldBuildingWeatherPanelLayout, weatherWidth, weatherHeight, ref yBottom);
            generateRect = PlaceWorldBuildingSidePanel(worldBuildingGeneratePanelLayout, generateWidth, generateHeight, ref yBottom);
            charactersRect = PlaceWorldBuildingSidePanel(worldBuildingCharactersPanelLayout, charactersWidth, charactersHeight, ref yBottom);

            float leftYBottom = ReviewUiScale.Height - WorldBuildingSidePanelMargin;
            objectsRect = PlaceWorldBuildingSidePanel(worldBuildingObjectsPanelLayout, objectsWidth, objectsHeight, ref leftYBottom, dockLeft: true);
        }

        // Docked panels stack bottom-up along the right edge (left edge for dockLeft
        // panels); floating panels sit wherever the user dragged them (clamped so the
        // header always stays reachable).
        private static Rect PlaceWorldBuildingSidePanel(
            WorldBuildingPanelLayout layout,
            float width,
            float height,
            ref float yBottom,
            bool dockLeft = false)
        {
            if (layout.hasCustomPosition)
            {
                Vector2 pos = ClampWorldBuildingPanelPosition(layout.position, width);
                layout.position = pos;
                return new Rect(pos.x, pos.y, width, height);
            }

            float x = dockLeft ? WorldBuildingSidePanelMargin : GetWorldBuildingSidePanelX(width);
            Rect rect = new Rect(x, yBottom - height, width, height);
            yBottom = rect.y - WorldBuildingSidePanelGap;
            return rect;
        }

        private static float GetWorldBuildingPanelWidth(WorldBuildingPanelLayout layout, float defaultWidth)
        {
            if (!layout.hasCustomSize)
                return defaultWidth;

            float maxWidth = Mathf.Min(WorldBuildingSidePanelCustomMaxWidth, ReviewUiScale.Width - 32f);
            float minWidth = Mathf.Min(240f * WorldBuildingUiScale, defaultWidth, maxWidth);
            return Mathf.Clamp(layout.size.x, minWidth, maxWidth);
        }

        private static Vector2 ClampWorldBuildingPanelPosition(Vector2 position, float panelWidth)
        {
            float s = WorldBuildingUiScale;
            float minVisible = 100f * s;
            position.x = Mathf.Clamp(position.x, minVisible - panelWidth, ReviewUiScale.Width - minVisible);
            position.y = Mathf.Clamp(position.y, 0f, ReviewUiScale.Height - 40f * s);
            return position;
        }

        private static float GetWorldBuildingSidePanelX(float panelWidth)
        {
            return ReviewUiScale.Width - panelWidth - WorldBuildingSidePanelMargin;
        }

        private static void ReducePanelHeightForOverflow(
            ref float height,
            bool minimized,
            float minHeight,
            ref float overflow)
        {
            if (overflow <= 0f || minimized)
                return;

            float availableReduction = Mathf.Max(0f, height - minHeight);
            float reduction = Mathf.Min(availableReduction, overflow);
            height -= reduction;
            overflow -= reduction;
        }

        /// <summary>
        /// Drag (header) / resize (corner grip) handling for a world-building panel. Call
        /// before drawing the panel body so the grip's MouseDown wins over the scroll view.
        /// Left-button drag on the header moves the panel (it becomes floating); dragging the
        /// grip resizes it; double-clicking the header snaps it back to the default layout.
        /// The grip sits in the top corner that actually moves as the panel grows —top-left
        /// for right-docked panels, top-right (gripOnRight) for left-docked ones —so the
        /// grip follows the cursor instead of sitting on a pinned edge.
        /// </summary>
        private static void HandleWorldBuildingPanelDragAndResize(
            Rect panelRect,
            WorldBuildingPanelLayout layout,
            float dragHandleHeight,
            bool allowResize,
            bool gripOnRight = false)
        {
            float s = WorldBuildingUiScale;
            int dragId = GUIUtility.GetControlID(FocusType.Passive);
            int resizeId = GUIUtility.GetControlID(FocusType.Passive);
            Event e = Event.current;

            Rect gripRect = GetWorldBuildingPanelGripRect(panelRect, gripOnRight);

            // Keep the minimize toggle (right of the header) and the grip clickable; with the
            // grip on the right both reserves sit on the same side.
            float toggleReserve = 48f * s;
            float gripReserve = allowResize ? gripRect.width : 0f;
            float leftReserve = gripOnRight ? 0f : gripReserve;
            float rightReserve = toggleReserve + (gripOnRight ? gripReserve : 0f);
            Rect dragRect = new Rect(
                panelRect.x + leftReserve,
                panelRect.y,
                Mathf.Max(0f, panelRect.width - leftReserve - rightReserve),
                Mathf.Min(dragHandleHeight, panelRect.height));

            switch (e.type)
            {
                case EventType.MouseDown:
                    if (e.button != 0)
                        break;
                    if (allowResize && gripRect.Contains(e.mousePosition))
                    {
                        GUIUtility.hotControl = resizeId;
                        layout.resizeStartMouse = e.mousePosition;
                        layout.resizeStartSize = new Vector2(panelRect.width, panelRect.height);
                        layout.resizeStartPosition = new Vector2(panelRect.x, panelRect.y);
                        e.Use();
                    }
                    else if (dragRect.Contains(e.mousePosition))
                    {
                        if (e.clickCount >= 2)
                        {
                            layout.hasCustomPosition = false;
                            layout.hasCustomSize = false;
                            e.Use();
                        }
                        else
                        {
                            GUIUtility.hotControl = dragId;
                            layout.dragOffset = e.mousePosition - new Vector2(panelRect.x, panelRect.y);
                            e.Use();
                        }
                    }
                    break;

                case EventType.MouseDrag:
                    if (GUIUtility.hotControl == dragId)
                    {
                        layout.hasCustomPosition = true;
                        layout.position = ClampWorldBuildingPanelPosition(e.mousePosition - layout.dragOffset, panelRect.width);
                        e.Use();
                    }
                    else if (GUIUtility.hotControl == resizeId)
                    {
                        // Top-left grip: dragging up/left grows the panel. Top-right grip
                        // (left-docked panels): dragging up/right grows it.
                        Vector2 delta = e.mousePosition - layout.resizeStartMouse;
                        layout.hasCustomSize = true;
                        layout.size = gripOnRight
                            ? new Vector2(layout.resizeStartSize.x + delta.x, layout.resizeStartSize.y - delta.y)
                            : layout.resizeStartSize - delta;

                        // A floating panel is anchored by its top-left corner, so that corner has
                        // to travel with the cursor for the opposite corner to stay put. With the
                        // grip on the right the left edge is the anchored one, so only y travels.
                        // Docked panels are pinned to their edges by the layout pass already.
                        if (layout.hasCustomPosition)
                            layout.position = gripOnRight
                                ? new Vector2(layout.resizeStartPosition.x, layout.resizeStartPosition.y + delta.y)
                                : layout.resizeStartPosition + delta;
                        e.Use();
                    }
                    break;

                case EventType.MouseUp:
                    if (GUIUtility.hotControl == dragId || GUIUtility.hotControl == resizeId)
                    {
                        GUIUtility.hotControl = 0;
                        e.Use();
                    }
                    break;
            }
        }

        // Single source of truth for the grip's hit area and its drawn position, so the two can't drift.
        private static Rect GetWorldBuildingPanelGripRect(Rect panelRect, bool gripOnRight = false)
        {
            float gripSize = 18f * WorldBuildingUiScale;
            float x = gripOnRight ? panelRect.xMax - gripSize : panelRect.x;
            return new Rect(x, panelRect.y, gripSize, gripSize);
        }

        // Draw after the panel body so the grip stays visible on top of it.
        private static void DrawWorldBuildingPanelGrip(Rect panelRect, bool allowResize, bool gripOnRight = false)
        {
            if (!allowResize || Event.current.type != EventType.Repaint)
                return;

            DrawWorldBuildingResizeGrip(GetWorldBuildingPanelGripRect(panelRect, gripOnRight), gripOnRight);
        }

        // Classic triangle-of-dots resize grip, right-angled into the panel's top corner so it
        // points the way the panel grows. On the left the footprint stays inside 14 * s, which
        // is where the header title starts, so the dots never collide with the title text.
        private static void DrawWorldBuildingResizeGrip(Rect gripRect, bool gripOnRight)
        {
            float s = WorldBuildingUiScale;
            float dot = 2f * s;
            float step = 4f * s;
            Color previous = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, 0.45f);
            for (int i = 0; i < 3; i++)
            {
                for (int j = 0; j + i < 3; j++)
                {
                    float x = gripOnRight
                        ? gripRect.xMax - 2.5f * s - i * step - dot
                        : gripRect.x + 2.5f * s + i * step;
                    float y = gripRect.y + 2.5f * s + j * step;
                    GUI.DrawTexture(new Rect(x, y, dot, dot), Texture2D.whiteTexture);
                }
            }
            GUI.color = previous;
        }

        private void DrawSpawnPreviewCard(Rect rect, WorldBuildingSpawnUiRow row)
        {
            if (row == null)
                return;

            float s = WorldBuildingPaletteMetricScale;
            GUI.Box(rect, "");

            float pad = 10f * s;
            float buttonHeight = 30f * s;
            string displayName = string.IsNullOrEmpty(row.DisplayName) ? row.SpawnId : row.DisplayName;

            // Character cards offer both spawn modes, so the name moves to a label and the
            // button row splits into Static | Moving.
            bool modeButtons = row.SupportsAgentModes && !row.IsImportedGlb;
            float nameLabelHeight = modeButtons ? 20f * s : 0f;

            Rect previewRect = new Rect(
                rect.x + pad,
                rect.y + pad,
                rect.width - pad * 2f,
                rect.height - buttonHeight - nameLabelHeight - pad * 3f);
            DrawWorldBuildingSpawnThumbnail(previewRect, row.Thumbnail);

            if (!modeButtons)
            {
                string buttonLabel = rect.width < 220f ? displayName : $"Add {displayName}";
                if (GUI.Button(new Rect(rect.x + pad, rect.yMax - buttonHeight - pad, rect.width - pad * 2f, buttonHeight), buttonLabel, worldBuildingButtonStyle))
                    SpawnWorldBuildingRow(row);
                return;
            }

            GUI.Label(
                new Rect(rect.x + pad, rect.yMax - buttonHeight - nameLabelHeight - pad, rect.width - pad * 2f, nameLabelHeight),
                displayName,
                worldBuildingSubtitleStyle);

            float buttonGap = 6f * s;
            float halfWidth = (rect.width - pad * 2f - buttonGap) / 2f;
            Rect staticRect = new Rect(rect.x + pad, rect.yMax - buttonHeight - pad, halfWidth, buttonHeight);
            Rect walkingRect = new Rect(staticRect.xMax + buttonGap, staticRect.y, halfWidth, buttonHeight);
            if (GUI.Button(staticRect, "Static", worldBuildingButtonStyle))
                SpawnWorldBuildingRow(row, dynamicAgent: false);
            if (GUI.Button(walkingRect, "Moving", worldBuildingButtonStyle))
                SpawnWorldBuildingRow(row, dynamicAgent: true);
        }

        private void SpawnWorldBuildingRow(WorldBuildingSpawnUiRow row, bool? dynamicAgent = null)
        {
            if (row == null)
                return;

            if (runtimeEditorManager == null)
                runtimeEditorManager = RuntimeEditorManager.Instance;

            if (row.IsImportedGlb)
            {
                if (!File.Exists(row.ImportGlbPath))
                {
                    worldBuildingGenerateStatus = $"Generated model file missing: {row.DisplayName}";
                    Debug.LogWarning($"[WorldBuilding] Meshy GLB missing for palette row '{row.DisplayName}': {row.ImportGlbPath}");
                    WorldBuildingSpawnLibrary.RefreshFromResources(force: true);
                    EnsureWorldBuildingSpawnPrefabsConfigured();
                    return;
                }

                worldBuildingGenerateStatus = $"Adding generated model \"{row.DisplayName}\".";
                StartImportSavedGlb(row.ImportGlbPath, row.DisplayName);
                return;
            }

            if (runtimeEditorManager != null && !string.IsNullOrEmpty(row.SpawnId))
                runtimeEditorManager.SpawnObject(row.SpawnId, dynamicAgent);
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
            float height = allowStartWithoutRosBackend ? 410f : 384f;
            Rect rect = new Rect((ReviewUiScale.Width - width) * 0.5f, 24f, width, height);

            GUI.Box(rect, "");

            float pad = 18f;
            float x = rect.x + pad;
            float w = rect.width - pad * 2f;
            float y = rect.y + 16f;

            GUI.Label(new Rect(x, y, w, 28f),
                trialStartReady ? "SESSION READY" : "LOADING SESSION");
            y += 34f;

            GUI.Label(new Rect(x, y, w, 24f),
                trialStartReady
                    ? "Robot, human, and cameras are loaded. Start when you are ready."
                    : "Preparing robot, pedestrians, and camera view...");
            y += 40f;

            // Which role the human controls. The camera follows this role.
            GUI.Label(new Rect(x, y, w, 26f), "You play", onboardingSectionStyle);
            y += 32f;
            if (DrawChipButton(new Rect(x, y, 172f, 34f), "Robot",
                selectedPlayerMode == OnboardingPlayerMode.Robot))
            {
                SetPlayerMode(OnboardingPlayerMode.Robot);
            }
            if (DrawChipButton(new Rect(x + 182f, y, 172f, 34f), "Human",
                selectedPlayerMode == OnboardingPlayerMode.Human))
            {
                SetPlayerMode(OnboardingPlayerMode.Human);
            }
            y += 46f;

            // Manual / Auto per role, independent of who the human plays.
            GUI.Label(new Rect(x, y, 230f, 26f), "Robot Control", onboardingSectionStyle);
            GUI.Label(new Rect(x + 254f, y, 230f, 26f), "Human Control", onboardingSectionStyle);
            y += 32f;
            if (DrawChipButton(new Rect(x, y, 110f, 34f), "Manual",
                selectedRobotStartupControl == StartupControlMode.Manual))
            {
                SetRobotStartupControl(StartupControlMode.Manual);
            }
            if (DrawChipButton(new Rect(x + 118f, y, 110f, 34f), "Auto",
                selectedRobotStartupControl == StartupControlMode.Auto))
            {
                SetRobotStartupControl(StartupControlMode.Auto);
            }
            if (DrawChipButton(new Rect(x + 254f, y, 110f, 34f), "Manual",
                selectedPwdStartupControl == StartupControlMode.Manual))
            {
                SetPwdStartupControl(StartupControlMode.Manual);
            }
            if (DrawChipButton(new Rect(x + 372f, y, 110f, 34f), "Auto",
                selectedPwdStartupControl == StartupControlMode.Auto))
            {
                SetPwdStartupControl(StartupControlMode.Auto);
            }
            y += 46f;

            GUI.Label(new Rect(x, y, w, 34f),
                "Set roles independently. WASD drives your role; the other manual role uses the arrow keys.",
                onboardingHintStyle);
            y += 40f;

            if (allowStartWithoutRosBackend)
            {
                GUI.Label(new Rect(x, y, w, 22f),
                    trialStartReady
                        ? "ROS-backed start is ready. You can still bypass ROS explicitly if needed."
                        : noRosReady
                            ? "No ROS backend? The scene is ready; only the live planner is missing."
                            : "Preparing non-ROS scene setup before a bypass start is allowed.",
                    onboardingHintStyle);
                y += 26f;
            }

            GUI.enabled = trialStartReady || bypassRosBackendForTrialStart;
            if (GUI.Button(new Rect(x, y, w, 34f),
                trialStartReady
                    ? $"Start Trial [{startTrialKey}{(SEAN.Input.JoystickProfiles.GamepadActive ? " / Y" : "")}]"
                    : "Loading..."))
            {
                StartTrialFromPrompt();
            }
            GUI.enabled = true;
            y += 42f;

            float halfW = (w - 10f) * 0.5f;
            if (allowStartWithoutRosBackend)
            {
                GUI.enabled = trialStartReady || noRosReady;
                if (GUI.Button(new Rect(x, y, halfW, 32f), "Start Without ROS Backend"))
                {
                    StartTrialWithoutRosBackend();
                }
                GUI.enabled = true;

                if (GUI.Button(new Rect(x + halfW + 10f, y, halfW, 32f), "Go Back [Esc]"))
                {
                    ReturnToOnboardingFromTrialPrompt();
                }
            }
            else if (GUI.Button(new Rect(x, y, w, 32f), "Go Back [Esc]"))
            {
                ReturnToOnboardingFromTrialPrompt();
            }
        }

        // The post-trial menu never freezes time: a manually driven agent stays drivable
        // while it is up, auto agents stop themselves at their goals, and the simulation
        // (pedestrians, recording) keeps running underneath.
        private void HidePostTrialPrompt()
        {
            showPostTrialPrompt = false;
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
                return;
            }

            worldBuildingCamera = sean.environment.topViewCamera;
            if (worldBuildingCamera == null)
            {
                showPostTrialPrompt = true;
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
            runtimeEditorManager.worldBuildingSupplementaryHelpText = BuildWorldBuildingControlsHelpText();
            // World building owns the editor lifecycle, so ESC/hotkey must not self-exit and tear down
            // the world-building camera. Exit is via the "Back To Menu" button instead.
            runtimeEditorManager.externalLifecycleControl = true;
            runtimeEditorManager.SetEditorMode(true);

            showPostTrialPrompt = false;
            showReviewCompletionPrompt = false;
            worldBuildingAddObjectsMinimized = false;
            worldBuildingAddCharactersMinimized = true;
            worldBuildingGenerateObjectsMinimized = true;
            inWorldBuildingMode = true;

            CaptureWorldBuildingTaskMarkerBaseline();
            ShowTaskStartMarkersForWorldBuilding();
        }

        // Start markers are deactivated on every scene load (Tasks.Base.initStartAndGoal) so
        // participants never see them mid-trial. World building re-activates them: the start
        // FLAG is the reliable thing to drag —the robot body usually is not on its start
        // point when world building opens after a run. Exit re-hides exactly the markers this
        // activated, so a scene that authors its own always-on marker is left alone.
        private readonly List<GameObject> worldBuildingShownStartMarkers = new List<GameObject>();

        private void ShowTaskStartMarkersForWorldBuilding()
        {
            worldBuildingShownStartMarkers.Clear();

            var sean = SEAN.SEAN.instance;
            if (sean == null)
                return;
            SEAN.Tasks.Base task;
            try { task = sean.robotTask; }
            catch (Exception) { return; }
            if (task == null)
                return;

            foreach (GameObject marker in new[] { task.robotStart, task.playerStart })
            {
                if (marker == null || marker.activeSelf)
                    continue;
                marker.SetActive(true);
                worldBuildingShownStartMarkers.Add(marker);
            }
        }

        private void HideTaskStartMarkersAfterWorldBuilding()
        {
            foreach (GameObject marker in worldBuildingShownStartMarkers)
            {
                if (marker != null)
                    marker.SetActive(false);
            }
            worldBuildingShownStartMarkers.Clear();
        }

        // Poses captured when world building opens, so exit can detect a user-moved robot
        // goal/robot and write the change back into the CustomStartGoal scene markers.
        private bool hasWorldBuildingTaskMarkerBaseline;
        private Vector3 worldBuildingRobotGoalBaselinePosition;
        private Quaternion worldBuildingRobotGoalBaselineRotation;
        private Vector3 worldBuildingRobotBaseLinkBaselinePosition;
        private Quaternion worldBuildingRobotBaseLinkBaselineRotation;
        private bool hasWorldBuildingRobotStartBaseline;
        private Vector3 worldBuildingRobotStartBaselinePosition;
        private Quaternion worldBuildingRobotStartBaselineRotation;

        private void CaptureWorldBuildingTaskMarkerBaseline()
        {
            hasWorldBuildingTaskMarkerBaseline = false;

            var sean = SEAN.SEAN.instance;
            if (sean == null)
                return;

            SEAN.Tasks.Base task;
            try { task = sean.robotTask; }
            catch (Exception) { return; }
            if (task == null || task.robotGoal == null)
                return;

            worldBuildingRobotGoalBaselinePosition = task.robotGoal.transform.position;
            worldBuildingRobotGoalBaselineRotation = task.robotGoal.transform.rotation;

            if (sean.robot != null && sean.robot.base_link != null)
            {
                worldBuildingRobotBaseLinkBaselinePosition = sean.robot.base_link.transform.position;
                worldBuildingRobotBaseLinkBaselineRotation = sean.robot.base_link.transform.rotation;
            }

            hasWorldBuildingRobotStartBaseline = task.robotStart != null;
            if (hasWorldBuildingRobotStartBaseline)
            {
                worldBuildingRobotStartBaselinePosition = task.robotStart.transform.position;
                worldBuildingRobotStartBaselineRotation = task.robotStart.transform.rotation;
            }

            hasWorldBuildingTaskMarkerBaseline = true;
        }

        /// <summary>
        /// Robot start/goal edits made in world building must OUTLIVE the next task refresh:
        /// CustomStartGoal.NewTask re-copies robotStart/robotGoal from its scene Location markers
        /// on every (re)start and goal republish, which would silently revert a dragged goal flag
        /// and leave ROS navigating to the old destination. Writing the user's edits back into
        /// those markers makes the trial warmup republish the NEW goal to /move_base_simple/goal.
        /// </summary>
        private void SyncMovedRobotMarkersIntoTask()
        {
            if (!hasWorldBuildingTaskMarkerBaseline)
                return;
            hasWorldBuildingTaskMarkerBaseline = false;

            var sean = SEAN.SEAN.instance;
            if (sean == null)
                return;

            SEAN.Tasks.Base task;
            try { task = sean.robotTask; }
            catch (Exception) { return; }

            var custom = task as SEAN.Tasks.CustomStartGoal;
            if (custom == null)
                return;

            if (task.robotGoal != null && custom.RobotGoalLocation != null)
            {
                Transform goal = task.robotGoal.transform;
                if (HasWorldBuildingPoseChanged(worldBuildingRobotGoalBaselinePosition, worldBuildingRobotGoalBaselineRotation, goal))
                {
                    custom.RobotGoalLocation.transform.SetPositionAndRotation(goal.position, goal.rotation);
                    Debug.Log($"[SessionReview] World building moved the robot goal to {goal.position}; RobotGoalLocation updated so ROS receives the new destination.");
                }
            }

            if (sean.robot != null && sean.robot.base_link != null && custom.RobotStartLocation != null)
            {
                Transform baseLink = sean.robot.base_link.transform;
                if (HasWorldBuildingPoseChanged(worldBuildingRobotBaseLinkBaselinePosition, worldBuildingRobotBaseLinkBaselineRotation, baseLink))
                {
                    custom.RobotStartLocation.transform.SetPositionAndRotation(baseLink.position, baseLink.rotation);
                    Debug.Log($"[SessionReview] World building moved the robot to {baseLink.position}; RobotStartLocation updated to match.");
                }
            }

            // Checked AFTER the robot body: when both were dragged, the start FLAG wins —it is
            // the explicit "start here" statement, while the robot may simply sit where the last
            // run left it (usually nowhere near its start point).
            if (hasWorldBuildingRobotStartBaseline && task.robotStart != null && custom.RobotStartLocation != null)
            {
                Transform start = task.robotStart.transform;
                if (HasWorldBuildingPoseChanged(worldBuildingRobotStartBaselinePosition, worldBuildingRobotStartBaselineRotation, start))
                {
                    custom.RobotStartLocation.transform.SetPositionAndRotation(start.position, start.rotation);
                    Debug.Log($"[SessionReview] World building moved the start flag to {start.position}; RobotStartLocation updated so the next run starts there.");
                }
            }
        }

        private static bool HasWorldBuildingPoseChanged(Vector3 basePosition, Quaternion baseRotation, Transform current)
        {
            return (current.position - basePosition).sqrMagnitude > 1e-4f ||
                   Quaternion.Angle(baseRotation, current.rotation) > 0.5f;
        }

        private void ExitWorldBuildingMode(bool restoreGameplayCameras = false)
        {
            SyncMovedRobotMarkersIntoTask();
            // A dragged pedestrian start/goal marker must reach the LIVE spawned PWD too —
            // its waypoints were baked from the markers at spawn time. No teleport here:
            // only the goal/anchor updates while the participant keeps driving.
            WorldBuildingScenarioRestorer.SyncPwdSpawnerWaypoints(teleportToStart: false);
            HideTaskStartMarkersAfterWorldBuilding();
            inWorldBuildingMode = false;

            if (runtimeEditorManager != null)
            {
                runtimeEditorManager.suppressSpawnCanvas = false;
                runtimeEditorManager.externalLifecycleControl = false;
                runtimeEditorManager.worldBuildingSupplementaryHelpText = null;
                runtimeEditorManager.CloseWorldBuildingHelpPopup();

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

        private string BuildWorldBuildingControlsHelpText()
        {
            KeyCode bindKey = runtimeEditorManager != null
                ? runtimeEditorManager.bindMoveableKey
                : KeyCode.LeftShift;
            KeyCode goalKey = runtimeEditorManager != null
                ? runtimeEditorManager.setGoalKey
                : KeyCode.G;
            return
                "Left click select/drag gizmo | " +
                $"{bindKey}+click add moveable | {bindKey}+drag box-select many | " +
                "T translate | R rotate | " +
                $"{goalKey} make selected object the robot goal (again to restore cube) | " +
                "Right mouse free cam | Middle mouse pan | Wheel zoom | " +
                "F4 top-down reset | Esc deselect | \"Back To Menu\" to exit";
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

        /// <summary>
        /// After the "You play: Robot/Human" role is chosen, tell the PWD's RandomAvatar to
        /// re-select its route (primary vs robot-trial start/end), reposition the pedestrian, and
        /// hide/show the pedestrian markers. The PWD spawned in Awake before the role existed, so
        /// this trial-start pass is what actually makes switching the role change the pedestrian.
        /// </summary>
        private void ApplyPwdTrialRoute()
        {
            bool robotTrial = selectedPlayerMode == OnboardingPlayerMode.Robot;
            foreach (var ra in FindObjectsOfType<SEAN.Scenario.Agents.RandomAvatar>(true))
            {
                if (ra != null && ra.isPwdPlayer)
                {
                    ra.ApplyTrialRoute(robotTrial);
                    return;
                }
            }
            Debug.LogWarning("[PWD] ApplyPwdTrialRoute: no isPwdPlayer RandomAvatar found to apply the trial route.");
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
        /// Sync prefab-backed spawns from Resources/WorldBuildingSpawns. Meshy GLB rows stay in
        /// the IMGUI palette and import through StartImportSavedGlb instead of this prefab list.
        /// </summary>
        private void EnsureWorldBuildingSpawnPrefabsConfigured()
        {
            if (runtimeEditorManager == null)
                return;

            WorldBuildingSpawnLibrary.RefreshFromResources();
            IReadOnlyList<SpawnableObject> built = WorldBuildingSpawnLibrary.LastSpawnables;
            if (built == null || built.Count == 0)
            {
                IReadOnlyList<WorldBuildingSpawnUiRow> uiRows = WorldBuildingSpawnLibrary.LastUiRows;
                if (uiRows == null || uiRows.Count == 0)
                    Debug.LogWarning("[SessionReview] World building: no spawn entries found (pair WorldBuildingSpawns prefabs + WorldBuildingUI textures, or generate Meshy GLBs first).");

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

            RuntimeEditorManager.DisableStrayEditorComponents(runtimeEditorManager.gameObject);
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

            // Every trial starts with the robot's intent hidden, whatever the last review left
            // on: a participant must not see the planned path or the goal marker while driving.
            RosOverlayVisibility.SetAllVisible(false);

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
            // The human's chosen role drives with WASD; the other role (if also manual)
            // is remapped to the arrow keys so the two don't move together.
            bool robotIsPlayerRole = selectedPlayerMode == OnboardingPlayerMode.Robot;
            bool pwdIsPlayerRole = selectedPlayerMode == OnboardingPlayerMode.Human;

            var velocityController = FindObjectOfType<SEAN.Control.VelocityController>();
            if (velocityController != null)
            {
                bool robotManual = selectedRobotStartupControl == StartupControlMode.Manual;
                velocityController.startInManualMode = robotManual;
                velocityController.SetManualControlActive(robotManual);
                velocityController.manualUseArrowKeys = !robotIsPlayerRole;
            }

            var pwdControllers = FindObjectsOfType<IVI.ManualWheelchairController>(true);
            bool pwdManual = selectedPwdStartupControl == StartupControlMode.Manual;
            bool allowPwdMovement = ShouldAllowPwdMovement();
            foreach (var pwdController in pwdControllers)
            {
                if (pwdController == null)
                    continue;

                pwdController.startInManualMode = pwdManual;
                pwdController.manualUseArrowKeys = !pwdIsPlayerRole;
                if (allowPwdMovement)
                    UnfreezePwdController(pwdController, pwdManual);
                else
                    FreezePwdController(pwdController);
            }

            // The camera follows whichever role the human is playing, regardless of
            // whether that role is Manual or Auto (so an all-Auto session watched as the
            // PWD still shows the PWD view instead of snapping back to the robot).
            if (pwdIsPlayerRole)
                ActivatePwdCameraAsMain();
            else
                RestoreRobotGameplayCameras();
        }

        private bool ShouldAllowPwdMovement()
        {
            if (allowPwdManualControlWithoutTrial && !inRewindMode && !inWorldBuildingMode)
                return true;

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
            "wheelchairCamera", "PWDThirdPersonCamera", "PWDFirstPersonCamera", "PWDOverheadCamera"
        };

        // The PWD's auxiliary on-screen panels (top-down + first-person) created by
        // RandomAvatar.AttachPlayerMiniScreens. Kept rendering alongside the main
        // PWD view rather than being disabled with the rest of the cameras.
        private static readonly HashSet<string> pwdMiniCameraNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "PWDOverheadCamera", "PWDFirstPersonCamera"
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

        private static bool IsPwdMiniCamera(Camera cam)
        {
            return cam != null && pwdMiniCameraNames.Contains(cam.name);
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
            // Optional rear-view mini (Robot.Start creates it); no warning if absent.
            if (sean.robot.camera_rear != null)
                EnableRobotCam(sean.robot.camera_rear, "camera_rear");
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

            SessionReview.SessionReviewLog.Log($"[SessionReview] Destroying legacy standalone main camera '{legacyMain.name}'");
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

                // Keep the PWD's own top-down / first-person mini panels rendering on
                // the main display alongside the third-person view (they mirror the
                // robot's two on-screen views). Everything else is turned off.
                if (IsPwdMiniCamera(cam))
                {
                    cam.gameObject.SetActive(true);
                    cam.enabled = true;
                    cam.targetDisplay = 0;
                    // Ensure the panels composite on top of the full-screen main view.
                    if (cam.depth <= pwdCam.depth)
                        cam.depth = pwdCam.depth + 10f;
                    continue;
                }

                cam.enabled = false;
            }

            SessionReview.SessionReviewLog.Log($"[SessionReview] Activated PWD camera '{pwdCam.name}' as main view");
        }

        private void InitializeOnboardingSelection()
        {
            selectedPlayerMode = SessionOnboardingSettings.PlayerMode;
            selectedRobotStartupControl = SessionOnboardingSettings.RobotStartupControl;
            selectedPwdStartupControl = SessionOnboardingSettings.PwdStartupControl;
            selectedPwdGender = SessionOnboardingSettings.SelectedPwdGender;
            selectedPlayerCharacterId = SessionOnboardingSettings.SelectedPlayerCharacterId;
            sessionIdInput = ParticipantSession.Id;
            PlayerCharacterLibrary.Refresh();

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
                RefreshSavedScenarios();
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

            float panelWidth = Mathf.Min(ReviewUiScale.Width * 0.78f, 1100f);
            float panelHeight = Mathf.Min(ReviewUiScale.Height * 0.88f, 860f);
            float panelX = (ReviewUiScale.Width - panelWidth) * 0.5f;
            float panelY = (ReviewUiScale.Height - panelHeight) * 0.5f;
            Rect panelRect = new Rect(panelX, panelY, panelWidth, panelHeight);

            GUI.color = new Color(0f, 0f, 0f, 0.6f);
            GUI.DrawTexture(new Rect(0f, 0f, ReviewUiScale.Width, ReviewUiScale.Height), Texture2D.whiteTexture);
            GUI.color = Color.white;

            GUI.Box(panelRect, GUIContent.none, onboardingPanelStyle);

            float x = panelRect.x + 36f;
            float y = panelRect.y + 30f;
            float innerWidth = panelRect.width - 72f;

            // Session ID sits in the header's top-right corner, out of the scroll view, so
            // the scrollable content starts with the actual session choices.
            float sessionIdWidth = Mathf.Clamp(innerWidth * 0.32f, 200f, 320f);
            float sessionIdX = panelRect.x + panelRect.width - 36f - sessionIdWidth;
            float headerTextWidth = Mathf.Max(200f, sessionIdX - x - 24f);

            GUI.Label(new Rect(x, y, headerTextWidth, 42f), "Session Onboarding", onboardingTitleStyle);

            if (onboardingTextFieldStyle == null)
            {
                onboardingTextFieldStyle = new GUIStyle(GUI.skin.textField)
                {
                    fontSize = 18,
                    alignment = TextAnchor.MiddleLeft,
                    padding = new RectOffset(12, 12, 8, 8)
                };
            }

            GUI.Label(new Rect(sessionIdX, y - 2f, sessionIdWidth, 28f), "Session ID", onboardingSectionStyle);
            sessionIdInput = GUI.TextField(new Rect(sessionIdX, y + 28f, sessionIdWidth, 44f),
                sessionIdInput ?? string.Empty, 64, onboardingTextFieldStyle);
            GUI.Label(new Rect(sessionIdX, y + 74f, sessionIdWidth, 22f),
                "Saved into each trial's log.",
                onboardingHintStyle);

            y += 50f;

            GUI.Label(new Rect(x, y, headerTextWidth, 56f),
                "Choose who is playing, pick the player character when human control is enabled, and select the session scene to launch.",
                onboardingBodyStyle);
            y += 72f;
            var sceneChange = FindObjectOfType<SceneChange>();
            float footerTop = panelRect.y + panelRect.height - 86f;
            float scrollTop = y;
            float scrollHeight = Mathf.Max(180f, footerTop - scrollTop - 18f);
            Rect scrollRect = new Rect(x, scrollTop, innerWidth, scrollHeight);
            float contentHeight = GetOnboardingContentHeight(innerWidth - 18f, sceneChange);
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

        private const float CharacterCardMaxWidth = 220f;
        // Below this a card is too narrow to read, so the grid wraps instead of shrinking further.
        private const float CharacterCardMinWidth = 140f;
        private const float CharacterCardHeight = 196f;
        private const float CharacterCardGapX = 16f;
        private const float CharacterCardGapY = 12f;

        private static int GetCharacterCardCount()
        {
            return 2 + PlayerCharacterLibrary.OptionsWithPreview.Count; // wheelchair male/female + library entries with preview art
        }

        /// <summary>
        /// Player characters are meant to be compared side by side, so they share one row and
        /// the cards shrink to fit; only a library too wide even at the minimum width wraps.
        /// </summary>
        private static void GetCharacterGridLayout(float width, out int cardsPerRow, out float cardWidth)
        {
            int count = Mathf.Max(1, GetCharacterCardCount());

            float oneRowWidth = (width - CharacterCardGapX * (count - 1)) / count;
            if (oneRowWidth >= CharacterCardMinWidth)
            {
                cardsPerRow = count;
                cardWidth = Mathf.Min(CharacterCardMaxWidth, oneRowWidth);
                return;
            }

            cardWidth = CharacterCardMinWidth;
            cardsPerRow = Mathf.Max(1, Mathf.FloorToInt((width + CharacterCardGapX) / (cardWidth + CharacterCardGapX)));
        }

        // Height of the wheelchair + walking-character card grid. Must stay in lockstep
        // with the grid drawn in DrawOnboardingContent or the scroll view clips.
        private float GetCharacterGridHeight(float width)
        {
            GetCharacterGridLayout(width, out int cardsPerRow, out _);
            int rows = Mathf.CeilToInt(GetCharacterCardCount() / (float)cardsPerRow);
            return rows * (CharacterCardHeight + CharacterCardGapY) - CharacterCardGapY;
        }

        private float GetOnboardingContentHeight(float width, SceneChange sceneChange)
        {
            bool pickingCharacter = selectedPwdStartupControl == StartupControlMode.Manual;

            float height = 0f;
            height += 42f + 46f + 20f; // "Who Is Playing?" row (Session ID lives in the header)

            if (pickingCharacter)
                height += 42f + GetCharacterGridHeight(width) + 18f;

            height += 40f;

            int sceneCount = sceneChange != null ? sceneChange.SceneCount : 0;
            float sceneHeight = sceneCount > 0
                ? Mathf.Max(160f, sceneCount * 48f + 8f)
                : 160f;

            height += 30f + sceneHeight;

            height += 12f + 30f + 34f + GetScenarioGridHeight(width, GetVisibleScenarioIndices(sceneChange).Count);

            if (pickingCharacter)
                height += 24f + 42f + 150f + 22f; // preview-only characters, last

            return height + 8f;
        }

        private static int GetScenarioCardsPerRow(float width)
        {
            return Mathf.Max(1, Mathf.FloorToInt((width + ScenarioCardGapX) / (ScenarioCardWidth + ScenarioCardGapX)));
        }

        /// <summary>
        /// Indices into <see cref="savedScenarios"/> of the worlds built on the scene picked
        /// above: a saved world is a variant of its base scene, so showing worlds from other
        /// scenes here would offer a pick that silently changes the scene selection. Rebuilt
        /// every frame into the same list; the height pass and the draw pass must see the same
        /// set or the scroll view clips.
        /// </summary>
        private List<int> GetVisibleScenarioIndices(SceneChange sceneChange)
        {
            visibleScenarioIndices.Clear();
            if (savedScenarios.Count == 0)
                return visibleScenarioIndices;

            string sceneName = null;
            if (sceneChange != null && sceneChange.SceneCount > 0)
                sceneName = sceneChange.SceneNames[Mathf.Clamp(selectedSceneIndex, 0, sceneChange.SceneCount - 1)];

            for (int i = 0; i < savedScenarios.Count; i++)
            {
                WorldBuildingScenarioInfo info = savedScenarios[i];
                if (info == null)
                    continue;

                // No scene list to compare against: show everything rather than nothing.
                if (sceneName != null &&
                    !string.Equals(info.SceneName, sceneName, StringComparison.OrdinalIgnoreCase))
                    continue;

                visibleScenarioIndices.Add(i);
            }

            // A card that scrolled out of view must not stay selected behind the operator's back.
            if (selectedScenarioIndex >= 0 && !visibleScenarioIndices.Contains(selectedScenarioIndex))
                selectedScenarioIndex = -1;

            return visibleScenarioIndices;
        }

        // Height of the saved-scenario card grid. Must stay in lockstep with the grid
        // drawn in DrawOnboardingContent or the scroll view clips.
        private float GetScenarioGridHeight(float width, int cardCount)
        {
            if (cardCount == 0)
                return 0f;
            int rows = Mathf.CeilToInt(cardCount / (float)GetScenarioCardsPerRow(width));
            return rows * (ScenarioCardHeight + ScenarioCardGapY) - ScenarioCardGapY;
        }

        private void DrawOnboardingContent(float width, SceneChange sceneChange)
        {
            float x = 0f;
            float y = 0f;

            GUI.Label(new Rect(x, y, 260f, 30f), "Who Is Playing?", onboardingSectionStyle);
            y += 42f;

            if (DrawChipButton(new Rect(x, y, 180f, 46f), "Human", selectedPlayerMode == OnboardingPlayerMode.Human))
                ApplyRecommendedStartupControlsForPlayerMode(OnboardingPlayerMode.Human);
            if (DrawChipButton(new Rect(x + 196f, y, 180f, 46f), "Robot", selectedPlayerMode == OnboardingPlayerMode.Robot))
                ApplyRecommendedStartupControlsForPlayerMode(OnboardingPlayerMode.Robot);
            y += 66f;

            if (selectedPwdStartupControl == StartupControlMode.Manual)
            {
                GUI.Label(new Rect(x, y, width, 30f), "Player Character", onboardingSectionStyle);
                y += 42f;

                GetCharacterGridLayout(width, out int cardsPerRow, out float cardWidth);
                int cardIndex = 0;

                Rect NextCardRect()
                {
                    int col = cardIndex % cardsPerRow;
                    int row = cardIndex / cardsPerRow;
                    cardIndex++;
                    return new Rect(x + col * (cardWidth + CharacterCardGapX),
                                    y + row * (CharacterCardHeight + CharacterCardGapY),
                                    cardWidth, CharacterCardHeight);
                }

                bool wheelchairSelected = string.IsNullOrEmpty(selectedPlayerCharacterId);

                DrawGenderPreviewCard(NextCardRect(), "Wheelchair (Male)", maleWheelchairPreview,
                    wheelchairSelected && selectedPwdGender == SEAN.Scenario.Agents.PwdGender.Male,
                    () =>
                    {
                        selectedPlayerCharacterId = string.Empty;
                        selectedPwdGender = SEAN.Scenario.Agents.PwdGender.Male;
                    });

                DrawGenderPreviewCard(NextCardRect(), "Wheelchair (Female)", femaleWheelchairPreview,
                    wheelchairSelected && selectedPwdGender == SEAN.Scenario.Agents.PwdGender.Female,
                    () =>
                    {
                        selectedPlayerCharacterId = string.Empty;
                        selectedPwdGender = SEAN.Scenario.Agents.PwdGender.Female;
                    });

                foreach (var option in PlayerCharacterLibrary.OptionsWithPreview)
                {
                    if (option == null) continue;
                    string optionId = option.Id;
                    DrawGenderPreviewCard(NextCardRect(), option.DisplayName, option.Thumbnail,
                        string.Equals(selectedPlayerCharacterId, optionId, StringComparison.OrdinalIgnoreCase),
                        () => selectedPlayerCharacterId = optionId);
                }

                y += GetCharacterGridHeight(width) + 18f;
            }

            GUI.Label(new Rect(x, y, width, 30f), "Session To Play", onboardingSectionStyle);
            y += 40f;

            int sceneCount = sceneChange != null ? sceneChange.SceneCount : 0;
            float sceneHeight = sceneCount > 0
                ? Mathf.Max(160f, sceneCount * 48f + 8f)
                : 160f;
            DrawSceneSelection(new Rect(x, y, width, sceneHeight));
            y += sceneHeight;

            // Saved worlds belong to the scene above, so they sit directly under it and only
            // the ones built on the picked scene are listed.
            y += 12f;
            GUI.Label(new Rect(x, y, width - 210f, 30f), "Saved World Building Scenes (optional)", onboardingSectionStyle);
            // Default lists only this session's saved worlds; toggle on to browse every session.
            if (DrawChipButton(new Rect(x + width - 200f, y - 2f, 200f, 32f), "Show all sessions", showAllSessionScenarios))
            {
                showAllSessionScenarios = !showAllSessionScenarios;
                RefreshSavedScenarios();
            }
            y += 30f;

            // Computed after the toggle so a scope change this frame indexes the fresh list,
            // never a stale (possibly longer) one.
            List<int> visible = GetVisibleScenarioIndices(sceneChange);
            GUI.Label(new Rect(x, y, width, 26f),
                visible.Count > 0
                    ? "Worlds you built on the scene above. Pick one to rebuild it; leave unselected to start that scene clean."
                    : "No saved worlds for the scene above yet —build one in World Building and save it to see it here.",
                onboardingHintStyle);
            y += 34f;

            int perRow = GetScenarioCardsPerRow(width);
            for (int slot = 0; slot < visible.Count; slot++)
            {
                int index = visible[slot];
                int col = slot % perRow;
                int row = slot / perRow;
                Rect cardRect = new Rect(
                    x + col * (ScenarioCardWidth + ScenarioCardGapX),
                    y + row * (ScenarioCardHeight + ScenarioCardGapY),
                    ScenarioCardWidth, ScenarioCardHeight);
                DrawScenarioCard(cardRect, savedScenarios[index], index);
            }

            y += GetScenarioGridHeight(width, visible.Count);

            // Preview-only characters: nothing here is selectable, so it goes last.
            if (selectedPwdStartupControl == StartupControlMode.Manual)
            {
                y += 24f;
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
        }

        private void DrawScenarioCard(Rect rect, WorldBuildingScenarioInfo info, int index)
        {
            bool active = index == selectedScenarioIndex;
            GUI.Box(rect, GUIContent.none, active ? onboardingSceneActiveButtonStyle : onboardingSceneButtonStyle);

            Rect imageRect = new Rect(rect.x + 10f, rect.y + 10f, rect.width - 20f, rect.height - 76f);
            if (info.Thumbnail != null)
                GUI.DrawTexture(imageRect, info.Thumbnail, ScaleMode.ScaleToFit, true);
            else
                GUI.Label(imageRect, "(no snapshot)", onboardingHintStyle);

            GUI.Label(new Rect(rect.x + 10f, rect.yMax - 60f, rect.width - 20f, 24f), info.Name, onboardingPreviewLabelStyle);
            string owner = string.Equals(info.SessionId ?? string.Empty, ParticipantSession.Id ?? string.Empty, StringComparison.Ordinal)
                ? string.Empty
                : $" · session {(string.IsNullOrEmpty(info.SessionId) ? "unassigned" : info.SessionId)}";
            GUI.Label(new Rect(rect.x + 10f, rect.yMax - 34f, rect.width - 20f, 24f),
                $"{info.SceneName} · {info.ObjectCount} change(s){owner}", onboardingHintStyle);

            if (GUI.Button(rect, GUIContent.none, GUIStyle.none))
            {
                if (active)
                {
                    // Toggle back to the default: preset scene without the saved world.
                    selectedScenarioIndex = -1;
                }
                else
                {
                    selectedScenarioIndex = index;
                    // Keep the preset list in sync so the warmup/target scene matches the card.
                    int sceneIdx = IndexOfSceneName(FindObjectOfType<SceneChange>(), info.SceneName);
                    if (sceneIdx >= 0 && sceneIdx != selectedSceneIndex)
                    {
                        selectedSceneIndex = sceneIdx;
                        RefreshOnboardingWarmupState();
                    }
                }
            }
        }

        private static int IndexOfSceneName(SceneChange sceneChange, string sceneName)
        {
            if (sceneChange == null || string.IsNullOrEmpty(sceneName))
                return -1;
            for (int i = 0; i < sceneChange.SceneCount; i++)
            {
                if (string.Equals(sceneChange.SceneNames[i], sceneName, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            return -1;
        }

        private void RefreshSavedScenarios()
        {
            foreach (WorldBuildingScenarioInfo old in savedScenarios)
            {
                if (old?.Thumbnail != null)
                    Destroy(old.Thumbnail);
            }

            savedScenarios = WorldBuildingScenarioStore.ListScenarios(showAllSessionScenarios);
            if (selectedScenarioIndex >= savedScenarios.Count)
                selectedScenarioIndex = -1;
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

            // Display order is shuffled per session id (same id -> same order); the
            // stored selection is still a real scene index, so play logic is untouched.
            int[] displayOrder = SessionSceneOrder.Permutation(sessionIdInput, sceneChange.SceneCount);

            float totalHeight = sceneChange.SceneCount * 48f;
            if (totalHeight <= rect.height)
            {
                for (int pos = 0; pos < displayOrder.Length; pos++)
                {
                    int sceneIdx = displayOrder[pos];
                    Rect rowRect = new Rect(rect.x, rect.y + pos * 48f, rect.width, 40f);
                    bool isActive = sceneIdx == selectedSceneIndex;
                    string label = $"{pos + 1}. {sceneChange.SceneNames[sceneIdx]}";
                    if (GUI.Button(rowRect, label, isActive ? onboardingSceneActiveButtonStyle : onboardingSceneButtonStyle))
                    {
                        selectedSceneIndex = sceneIdx;
                        selectedScenarioIndex = -1;
                        RefreshOnboardingWarmupState();
                    }
                }

                return;
            }

            Rect viewRect = new Rect(0f, 0f, rect.width - 18f, totalHeight);
            onboardingSceneScroll = GUI.BeginScrollView(rect, onboardingSceneScroll, viewRect);

            for (int pos = 0; pos < displayOrder.Length; pos++)
            {
                int sceneIdx = displayOrder[pos];
                Rect rowRect = new Rect(0f, pos * 48f, viewRect.width, 40f);
                bool isActive = sceneIdx == selectedSceneIndex;
                string label = $"{pos + 1}. {sceneChange.SceneNames[sceneIdx]}";
                if (GUI.Button(rowRect, label, isActive ? onboardingSceneActiveButtonStyle : onboardingSceneButtonStyle))
                {
                    selectedSceneIndex = sceneIdx;
                    selectedScenarioIndex = -1;
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
            // Commit the typed session id BEFORE anything can write to disk: a scenario saved
            // from the prompt below must land in the session folder the onboarding page will
            // then read its card list from, not in the previous session's folder.
            ParticipantSession.Id = sessionIdInput;
            sessionIdInput = ParticipantSession.Id; // re-read trimmed value

            // Reloading tears down every runtime-placed World Building object. Before that
            // happens, offer to save the built world as a reusable scenario (snapshot + name).
            if (ShouldOfferWorldBuildingSave())
            {
                OpenWorldBuildingSavePrompt(WorldBuildingSaveFollowUp.ContinueApply);
                return;
            }

            CompleteOnboardingApply();
        }

        /// <summary>How the live world compares to the base scene and to what is on disk.</summary>
        private enum WorldBuildingChangeState
        {
            /// <summary>Untouched by World Building -- this is the base scene.</summary>
            None,
            /// <summary>Edited, and those edits are not saved as a scenario yet.</summary>
            Unsaved,
            /// <summary>Edited, but identical to the scenario it was saved to / restored from.</summary>
            Saved
        }

        private static WorldBuildingChangeState ClassifyWorldBuildingChanges()
        {
            string signature = WorldBuildingScenarioStore.ComputeCurrentSignature();
            if (string.IsNullOrEmpty(signature))
                return WorldBuildingChangeState.None;

            return signature == WorldBuildingScenarioStore.RestoredSignature
                ? WorldBuildingChangeState.Saved
                : WorldBuildingChangeState.Unsaved;
        }

        /// <summary>
        /// Reloads the base scene and lands back on the trial-start prompt, so the next run uses
        /// the original world instead of whatever World Building left behind.
        /// </summary>
        private void RestartTrialWithCleanScene()
        {
            string sceneName = SceneManager.GetActiveScene().name;
            if (!Application.CanStreamedLevelBeLoaded(sceneName))
            {
                Debug.LogWarning($"[SessionReview] Scene '{sceneName}' is not in Build Settings; restarting in place, so World Building changes stay in the world.");
                StartNextTrialInPlace();
                return;
            }

            SessionReviewLog.Log($"[SessionReview] Run Again: reloading '{sceneName}' for a clean world. Saved World Building scenes stay available as cards on the session page.");

            showReviewCompletionPrompt = false;
            if (inRewindMode)
                ExitReviewMode();
            ExitWorldBuildingMode(true);
            HidePostTrialPrompt();
            latestTrialEndInfo = null;
            sessionFullyComplete = false;

            // Nothing to restore after the load: a card launch is the only thing that arms this.
            WorldBuildingScenarioStore.PendingLoadFolder = null;
            // Marks PendingTrialStart so the reloaded scene opens the trial-start prompt (Start()).
            SessionOnboardingSettings.UpdateStartupControls(
                selectedPlayerMode,
                selectedRobotStartupControl,
                selectedPwdStartupControl);

            // The prompt/review paused time and timeScale survives a load -- unfreeze first or
            // the fresh scene comes up stopped.
            if (trialStartPromptPausedTime)
            {
                trialStartPromptPausedTime = false;
                Time.timeScale = savedTimeScale > 0f ? savedTimeScale : 1f;
            }
            else if (Time.timeScale <= 0f)
            {
                Time.timeScale = savedTimeScale > 0f ? savedTimeScale : 1f;
            }

            SceneManager.LoadScene(sceneName);
        }

        private bool ShouldOfferWorldBuildingSave()
        {
            if (showWorldBuildingSavePrompt)
                return false;

            // Untouched, or already on disk (just restored from / saved to a scenario).
            if (ClassifyWorldBuildingChanges() != WorldBuildingChangeState.Unsaved)
                return false;

            if (!OnboardingApplyWillReloadScene())
            {
                // Nothing is torn down, so nothing is at risk. The World Building "Save World"
                // button covers saving in this case.
                SessionReviewLog.Log("[SessionReview] World Building changes exist but this apply does not reload the scene; no save prompt (use \"Save World\" in World Building).");
                return false;
            }

            return true;
        }

        private bool OnboardingApplyWillReloadScene()
        {
            if (GetSelectedScenario() != null)
                return true; // scenario launches always reload

            var sceneChange = FindObjectOfType<SceneChange>();
            string currentSceneName = SceneManager.GetActiveScene().name;
            string targetSceneName = currentSceneName;
            if (sceneChange != null && sceneChange.SceneCount > 0)
                targetSceneName = sceneChange.SceneNames[Mathf.Clamp(selectedSceneIndex, 0, sceneChange.SceneCount - 1)];

            if (!string.Equals(targetSceneName, currentSceneName, StringComparison.Ordinal))
                return true;

            if (!Application.CanStreamedLevelBeLoaded(currentSceneName))
                return false;

            // Same scene: reload when the player character changed (mirrors
            // TryReloadForPlayerSelectionChange), or when World Building edited this world --
            // no card picked means "start the preset scene clean", which needs the base scene
            // back.
            return !SpawnedPlayerMatchesSelection() ||
                   ClassifyWorldBuildingChanges() != WorldBuildingChangeState.None;
        }

        private WorldBuildingScenarioInfo GetSelectedScenario()
        {
            return selectedScenarioIndex >= 0 && selectedScenarioIndex < savedScenarios.Count
                ? savedScenarios[selectedScenarioIndex]
                : null;
        }

        /// <summary>
        /// Shows the save modal. <paramref name="followUp"/> says what happens once it is
        /// answered -- see <see cref="WorldBuildingSaveFollowUp"/>.
        /// </summary>
        private void OpenWorldBuildingSavePrompt(WorldBuildingSaveFollowUp followUp)
        {
            if (worldBuildingSaveThumbnail != null)
                Destroy(worldBuildingSaveThumbnail);
            worldBuildingSaveThumbnail = WorldBuildingScenarioStore.CaptureThumbnail();
            worldBuildingSaveNameInput = SceneManager.GetActiveScene().name + " " + DateTime.Now.ToString("MM-dd HH:mm");
            worldBuildingSaveFollowUp = followUp;
            showWorldBuildingSavePrompt = true;
            GUIUtility.keyboardControl = 0;
        }

        /// <summary>Called by the World Building overlay's "Save World" button.</summary>
        private void OpenWorldBuildingSavePromptFromEditor()
        {
            if (string.IsNullOrEmpty(WorldBuildingScenarioStore.ComputeCurrentSignature()))
            {
                worldBuildingSaveStatus = "Nothing to save yet —place, move or delete something first.";
                return;
            }

            worldBuildingSaveStatus = null;
            OpenWorldBuildingSavePrompt(WorldBuildingSaveFollowUp.None);
        }

        private void ConfirmWorldBuildingSavePrompt(bool save)
        {
            WorldBuildingSaveFollowUp followUp = worldBuildingSaveFollowUp;

            if (save)
            {
                string folder = WorldBuildingScenarioStore.SaveCurrentScenario(
                    worldBuildingSaveNameInput, worldBuildingSaveThumbnail);
                worldBuildingSaveStatus = $"Saved \"{worldBuildingSaveNameInput}\" —pick it on the session page to rebuild this world.";
                SessionReviewLog.Log($"[SessionReview] World Building scenario saved to '{folder}'.");

                // Only refresh when staying: the list is sorted newest-first, so rebuilding it
                // mid-apply would shift selectedScenarioIndex onto the scenario just saved
                // instead of the card the operator picked.
                if (followUp == WorldBuildingSaveFollowUp.None)
                    RefreshSavedScenarios();
            }

            showWorldBuildingSavePrompt = false;
            worldBuildingSaveFollowUp = WorldBuildingSaveFollowUp.None;
            GUIUtility.keyboardControl = 0;

            if (followUp == WorldBuildingSaveFollowUp.ContinueApply)
                CompleteOnboardingApply();
            else if (followUp == WorldBuildingSaveFollowUp.RestartTrial)
                RestartTrialWithCleanScene();
        }

        private void CompleteOnboardingApply()
        {
            var sceneChange = FindObjectOfType<SceneChange>();
            string currentSceneName = SceneManager.GetActiveScene().name;
            string targetSceneName = currentSceneName;
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

            // A selected saved scenario overrides the preset target: its base scene is loaded
            // and the recorded objects are respawned after the load.
            WorldBuildingScenarioInfo scenario = GetSelectedScenario();
            if (scenario != null)
            {
                int scenarioSceneIndex = IndexOfSceneName(sceneChange, scenario.SceneName);
                if (scenarioSceneIndex < 0 && !Application.CanStreamedLevelBeLoaded(scenario.SceneName))
                {
                    Debug.LogWarning($"[SessionReview] Saved scenario scene '{scenario.SceneName}' is not in Build Settings; launching the preset scene instead.");
                    scenario = null;
                    selectedScenarioIndex = -1;
                }
                else
                {
                    targetSceneName = scenario.SceneName;
                    if (scenarioSceneIndex >= 0)
                        targetSceneIndex = scenarioSceneIndex;
                }
            }

            ParticipantSession.Id = sessionIdInput;
            sessionIdInput = ParticipantSession.Id; // re-read trimmed value

            // Release the Session ID text field's focus so keyboardControl-guarded
            // hotkeys (O, U) work again after the panel closes.
            GUIUtility.keyboardControl = 0;

            SessionOnboardingSettings.Apply(
                selectedPlayerMode,
                selectedPwdGender,
                selectedPlayerCharacterId,
                targetSceneIndex,
                targetSceneName,
                selectedRobotStartupControl,
                selectedPwdStartupControl);
            SetOnboardingVisible(false);

            if (scenario != null)
            {
                // Always reload, even into the same scene: restore needs a clean base world.
                WorldBuildingScenarioStore.PendingLoadFolder = scenario.Folder;
                SessionReviewLog.Log($"[SessionReview] Launching saved scenario '{scenario.Name}' (scene '{scenario.SceneName}') from '{scenario.Folder}'.");
                selectedScenarioIndex = -1;
                int scenarioSceneIndex = IndexOfSceneName(sceneChange, scenario.SceneName);
                if (sceneChange != null && scenarioSceneIndex >= 0)
                    sceneChange.LoadSceneAtIndex(scenarioSceneIndex);
                else
                    SceneManager.LoadScene(scenario.SceneName);
                return;
            }

            if (sceneChange != null && sceneChange.SceneCount > 0)
            {
                if (targetSceneName == currentSceneName)
                {
                    if (TryReloadForPlayerSelectionChange(currentSceneName))
                        return;
                    if (TryReloadForCleanBaseWorld(currentSceneName))
                        return;
                    ShowTrialStartPrompt();
                    return;
                }

                sceneChange.LoadSceneAtIndex(targetSceneIndex);
                return;
            }

            if (targetSceneName == currentSceneName)
            {
                if (TryReloadForPlayerSelectionChange(currentSceneName))
                    return;
                if (TryReloadForCleanBaseWorld(currentSceneName))
                    return;
                ShowTrialStartPrompt();
                return;
            }

            SceneManager.LoadScene(targetSceneName);
        }

        /// <summary>
        /// No scenario card picked means "start the preset scene clean", so a world that World
        /// Building edited has to be reloaded away. Returns true when a reload was started.
        /// </summary>
        private bool TryReloadForCleanBaseWorld(string currentSceneName)
        {
            if (ClassifyWorldBuildingChanges() == WorldBuildingChangeState.None)
                return false;

            if (!Application.CanStreamedLevelBeLoaded(currentSceneName))
            {
                Debug.LogWarning($"[SessionReview] Scene '{currentSceneName}' is not in Build Settings; cannot reload, so World Building changes stay in the world.");
                return false;
            }

            SessionReviewLog.Log($"[SessionReview] No saved scene picked; reloading '{currentSceneName}' so the preset starts clean.");
            SceneManager.LoadScene(currentSceneName);
            return true;
        }

        private void DrawWorldBuildingSavePrompt()
        {
            GUI.color = new Color(0f, 0f, 0f, 0.6f);
            GUI.DrawTexture(new Rect(0f, 0f, ReviewUiScale.Width, ReviewUiScale.Height), Texture2D.whiteTexture);
            GUI.color = Color.white;

            float panelWidth = Mathf.Min(680f, ReviewUiScale.Width - 60f);
            float innerWidth = panelWidth - 72f;
            float thumbHeight = Mathf.Min(innerWidth * 9f / 16f, ReviewUiScale.Height * 0.4f);
            float panelHeight = Mathf.Min(ReviewUiScale.Height - 40f, 330f + thumbHeight);
            Rect panelRect = new Rect(
                (ReviewUiScale.Width - panelWidth) * 0.5f,
                (ReviewUiScale.Height - panelHeight) * 0.5f,
                panelWidth, panelHeight);
            GUI.Box(panelRect, GUIContent.none, onboardingPanelStyle);

            float x = panelRect.x + 36f;
            float y = panelRect.y + 26f;

            GUI.Label(new Rect(x, y, innerWidth, 40f), "Save This Scene?", onboardingTitleStyle);
            y += 46f;
            string promptBody = worldBuildingSaveFollowUp == WorldBuildingSaveFollowUp.None
                ? "You changed this scene with World Building (placed, moved or deleted objects). Save the whole scene and it appears on the session page as a world you can reload later."
                : "You changed this scene with World Building (placed, moved or deleted objects). The next run starts from the original scene —save this one to keep it as a pickable world on the session page.";
            GUI.Label(new Rect(x, y, innerWidth, 48f), promptBody, onboardingBodyStyle);
            y += 56f;

            Rect thumbRect = new Rect(x, y, innerWidth, thumbHeight);
            GUI.color = new Color(0.12f, 0.15f, 0.19f, 0.95f);
            GUI.DrawTexture(thumbRect, Texture2D.whiteTexture);
            GUI.color = Color.white;
            if (worldBuildingSaveThumbnail != null)
                GUI.DrawTexture(thumbRect, worldBuildingSaveThumbnail, ScaleMode.ScaleToFit, true);
            else
                GUI.Label(thumbRect, "(no snapshot available)", onboardingHintStyle);
            y += thumbHeight + 14f;

            if (onboardingTextFieldStyle == null)
            {
                onboardingTextFieldStyle = new GUIStyle(GUI.skin.textField)
                {
                    fontSize = 18,
                    alignment = TextAnchor.MiddleLeft,
                    padding = new RectOffset(12, 12, 8, 8)
                };
            }

            GUI.Label(new Rect(x, y, 90f, 40f), "Name", onboardingSectionStyle);
            worldBuildingSaveNameInput = GUI.TextField(new Rect(x + 100f, y, innerWidth - 100f, 40f),
                worldBuildingSaveNameInput ?? string.Empty, 64, onboardingTextFieldStyle);

            bool continues = worldBuildingSaveFollowUp != WorldBuildingSaveFollowUp.None;

            float buttonY = panelRect.y + panelRect.height - 66f;
            if (GUI.Button(new Rect(x, buttonY, 120f, 46f), "Cancel", onboardingSecondaryButtonStyle))
            {
                // Cancel always means "go back to what I was doing" -- it must not run the
                // follow-up, or the scene would reload behind the operator's back.
                showWorldBuildingSavePrompt = false;
                worldBuildingSaveFollowUp = WorldBuildingSaveFollowUp.None;
                GUIUtility.keyboardControl = 0;
            }

            float saveW = 200f;
            float saveX = panelRect.x + panelRect.width - 36f - saveW;

            // Opened by a scene/character switch or a Run Again: offer to go on without saving.
            // Opened by the World Building "Save World" button: there is nothing to continue to,
            // so Cancel (above) is the only way out besides saving.
            if (continues)
            {
                float skipW = 230f;
                if (GUI.Button(new Rect(saveX - 12f - skipW, buttonY, skipW, 46f), "Continue Without Saving", onboardingSecondaryButtonStyle))
                    ConfirmWorldBuildingSavePrompt(false);
            }

            string saveLabel = continues ? "Save & Continue" : "Save World";
            if (GUI.Button(new Rect(saveX, buttonY, saveW, 46f), saveLabel, onboardingPrimaryButtonStyle))
                ConfirmWorldBuildingSavePrompt(true);
        }

        /// <summary>
        /// The PWD player is spawned once during scene load, so a character/gender pick
        /// made afterwards in this same scene needs a reload to take effect. Returns true
        /// when a reload was started.
        /// </summary>
        private bool TryReloadForPlayerSelectionChange(string currentSceneName)
        {
            if (SpawnedPlayerMatchesSelection())
                return false;

            if (!Application.CanStreamedLevelBeLoaded(currentSceneName))
            {
                Debug.LogWarning($"[SessionReview] Player selection changed but scene '{currentSceneName}' is not in Build Settings; cannot reload to respawn the player.");
                return false;
            }

            SessionReviewLog.Log("[SessionReview] Player character selection changed; reloading scene to respawn the player.");
            SceneManager.LoadScene(currentSceneName);
            return true;
        }

        private bool SpawnedPlayerMatchesSelection()
        {
            // No live player (e.g. deferred-spawn scenes) -- nothing to respawn.
            if (GameObject.Find("PWDPlayer") == null)
                return true;

            string spawnedId = SEAN.Scenario.Agents.RandomAvatar.LastSpawnedCharacterId ?? string.Empty;
            string wantedId = selectedPlayerCharacterId ?? string.Empty;
            if (!string.Equals(spawnedId, wantedId, StringComparison.OrdinalIgnoreCase))
                return false;

            // Built-in wheelchair pair: the gendered prefab must match too.
            if (wantedId.Length == 0 &&
                SEAN.Scenario.Agents.RandomAvatar.LastSpawnedGender != selectedPwdGender)
                return false;

            return true;
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

        private void SetPlayerMode(OnboardingPlayerMode mode)
        {
            // Switch which role the human controls WITHOUT resetting the Manual/Auto
            // choices (unlike ApplyRecommendedStartupControlsForPlayerMode). If the ready
            // prompt is open this re-applies the camera + WASD/arrow scheme immediately.
            if (selectedPlayerMode == mode)
                return;
            selectedPlayerMode = mode;
            RefreshOnboardingWarmupState();
            ApplyStartupControlsIfTrialPromptVisible();
        }

        private void ReturnToOnboardingFromTrialPrompt()
        {
            // Restore the time the ready prompt froze before reopening onboarding, so
            // onboarding captures the running timescale rather than the frozen 0.
            if (trialStartPromptPausedTime)
            {
                Time.timeScale = savedTimeScale;
                trialStartPromptPausedTime = false;
            }

            showTrialStartPrompt = false;
            trialStartReady = false;
            trialWarmupPending = false;
            SetOnboardingVisible(true);
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
            femaleWheelchairPreview = LoadPreviewTexture("female-wheelchair");
            maleWheelchairPreview = LoadPreviewTexture("male_wheelchair_user");
            dogwalkerPreview = LoadPreviewTexture("dogwalker");
            scooterUserPreview = LoadPreviewTexture("scooteruser");
        }

        /// <summary>
        /// Preview art lives in Resources/PlayerCharactersUI (same folder as the walking
        /// character thumbnails; also works in builds). The legacy Assets/UIResources disk
        /// path is kept as a fallback for editor setups that still have the old folder.
        /// </summary>
        private Texture2D LoadPreviewTexture(string baseName)
        {
            Texture2D texture = Resources.Load<Texture2D>("PlayerCharactersUI/" + baseName);
            if (texture != null)
                return texture;

            return LoadTextureFromAssets("UIResources/" + baseName + ".png");
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
