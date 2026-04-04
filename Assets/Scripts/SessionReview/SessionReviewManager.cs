using UnityEngine;
using UnityEngine.SceneManagement;
using System.IO;
using SEAN.Scenario.Obstacles;

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

        [Header("Onboarding")]
        [SerializeField] private bool showOnboardingOnStart = true;
        [SerializeField] private KeyCode onboardingToggleKey = KeyCode.O;

        [Header("Post-Trial Prompt")]
        [SerializeField] private bool usePostTrialPrompt = true;
        [SerializeField] private KeyCode replayTrialKey = KeyCode.R;

        [Header("Pre-Trial Ready Prompt")]
        [SerializeField] private KeyCode startTrialKey = KeyCode.Return;
        [SerializeField] private KeyCode exportReviewKey = KeyCode.E;

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
        private bool postTrialPromptPausedTime;
        private bool trialStartPromptPausedTime;
        private bool trialWarmupPending;
        private int trialWarmupDelayFrames;
        private TrialEndInfo latestTrialEndInfo;
        private TrialRecord currentReviewTrial;
        private Rerun.StateRecording currentReviewRecording;
        private float currentReviewTimeOffset;
        private bool showReviewExportPanel;
        private ReviewExportSettings reviewExportSettings = new ReviewExportSettings();
        private Bounds reviewExportEnvelope;
        private string lastReviewExportPath;
        private static Texture2D lineTexture;
        private bool showReviewCompletionPrompt;
        private bool inWorldBuildingMode;
        private Camera worldBuildingCamera;
        private Cameramovement worldBuildingCameraMovement;
        private ObstaclePublisher worldBuildingObstaclePublisher;
        private TrackedObstacle selectedWorldObstacle;
        private Vector3 selectedWorldObstacleStartPosition;
        private Vector3 selectedWorldObstacleDragOffset;
        private Plane worldBuildingDragPlane;
        private bool isDraggingWorldObstacle;
        private Vector3 pendingWorldBuildingCameraPosition;
        private Quaternion pendingWorldBuildingCameraRotation;
        private float pendingWorldBuildingOrthoSize;
        private bool hasPendingWorldBuildingCameraPose;
        private bool worldBuildingPausedTime;
        private float worldBuildingSavedTimeScale = 1f;
        private Camera worldBuildingPreviousMainCamera;
        private bool worldBuildingPreviousMainCameraEnabled;
        private int worldBuildingPreviousTargetDisplay;
        private int worldBuildingPreviousMovementTargetDisplay;
        private bool hasWorldBuildingTargetDisplayOverride;

        public bool UsePostTrialPrompt => usePostTrialPrompt;
        public bool BlocksAutomaticTrialStart => showTrialStartPrompt || trialWarmupPending;

        private static readonly float[] speedSteps = { 0.25f, 0.5f, 1f, 2f, 4f };
        private int currentSpeedIndex = 2;
        private float savedTimeScale = 1f;

        private bool showOnboarding;
        private bool onboardingPausedTime;
        private float onboardingSavedTimeScale = 1f;
        private OnboardingPlayerMode selectedPlayerMode = OnboardingPlayerMode.Robot;
        private SEAN.Scenario.Agents.PwdGender selectedPwdGender = SEAN.Scenario.Agents.PwdGender.Male;
        private int selectedSceneIndex = -1;
        private Vector2 onboardingSceneScroll;

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
            ExitWorldBuildingMode();

            if (inRewindMode)
                ExitReviewMode();

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
                StartTrialFromPrompt();
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
            if (Input.GetKeyDown(replayTrialKey))
                StartNextTrialFromReviewCompletion();

            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
                StartNextTrialFromReviewCompletion();
        }

        private void HandleWorldBuildingInput()
        {
            if (worldBuildingCamera == null)
                return;

            if (Input.GetMouseButtonDown(0))
                BeginWorldObstacleDrag();

            if (isDraggingWorldObstacle && Input.GetMouseButton(0))
                UpdateWorldObstacleDrag();

            if (isDraggingWorldObstacle && Input.GetMouseButtonUp(0))
                EndWorldObstacleDrag();

            if (Input.GetKeyDown(KeyCode.Escape))
            {
                CancelWorldObstacleDrag();
                ExitWorldBuildingMode();
                showPostTrialPrompt = true;
                PauseForPostTrialPrompt();
            }
        }

        private void HandleRewindInput()
        {
            if (Input.GetKeyDown(exportReviewKey))
            {
                showReviewExportPanel = !showReviewExportPanel;
            }

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

            ExitWorldBuildingMode();

            if (inRewindMode)
                rewindController.ExitRewind();

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

            // Freeze the simulation
            savedTimeScale = Time.timeScale;
            Time.timeScale = 0f;

            trajectoryRenderer.ShowTrajectories(trial, recording, controlModeLog, planSnapshots, vlmCaptures, timeOffset);
            metricsOverlay.ShowTrial(trial);
            rewindController.EnterRewind(trial, recording, controlModeLog, trajectoryRenderer, timeOffset);

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
            ExitWorldBuildingMode();
            if (inRewindMode)
                ExitReviewMode();

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
            if (!trialStartReady)
                return;

            var sean = SEAN.SEAN.instance;
            if (sean == null || sean.robotTask == null)
                return;

            SessionOnboardingSettings.MarkTrialStarted();
            showTrialStartPrompt = false;

            ResetControlledMotion();
            sean.robotTask.StartPendingOrNewTask();

            if (trialStartPromptPausedTime)
            {
                Time.timeScale = savedTimeScale;
                trialStartPromptPausedTime = false;
            }
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

        public void OpenOnboardingFromPostTrial()
        {
            showReviewCompletionPrompt = false;
            CaptureReviewCameraForWorldBuilding();
            ExitWorldBuildingMode();
            if (inRewindMode)
                ExitReviewMode();

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

            if (showTrialStartPrompt)
                DrawTrialStartPrompt();

            if (showPostTrialPrompt)
                DrawPostTrialPrompt();

            if (showReviewCompletionPrompt)
                DrawReviewCompletionPrompt();

            if (inWorldBuildingMode)
                DrawWorldBuildingOverlay();

            if (inRewindMode)
            {
                DrawReviewRoiOverlay();

                string perspective = rewindController.CurrentPerspective.ToString();
                string playing = rewindController.IsPlaying ? "PLAYING" : "PAUSED";
                GUI.Box(new Rect(Screen.width - 340, 10, 330, 50), "");
                GUI.Label(new Rect(Screen.width - 335, 15, 320, 20),
                    $"REWIND [{playing}] Trial {reviewTrialIndex + 1}/{trialArchive.TrialCount}");
                GUI.Label(new Rect(Screen.width - 335, 35, 320, 20),
                    $"{perspective} | F1-F5:View  Tab/Esc:Exit");

                DrawReviewExportPanel();
            }
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
            float height = 300f;
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
                GUI.Label(new Rect(x, rect.yMax - 86f, innerWidth, 28f), $"Last export: {lastReviewExportPath}");
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
                showReviewCompletionPrompt = false;
        }

        private void DrawWorldBuildingOverlay()
        {
            float width = 520f;
            float height = 196f;
            Rect rect = new Rect(24f, 24f, width, height);
            GUI.Box(rect, "");
            GUI.Label(new Rect(rect.x + 16f, rect.y + 14f, rect.width - 32f, 24f), "World Building");
            GUI.Label(new Rect(rect.x + 16f, rect.y + 40f, rect.width - 32f, 40f),
                "Simulation is paused and the top-down edit camera is active. Drag tracked obstacles on the ground plane, and use the camera controls when you are not dragging.");

            string selectionText = selectedWorldObstacle != null
                ? $"Selected: {selectedWorldObstacle.gameObject.name} ({selectedWorldObstacle.type})"
                : "Selected: none";
            GUI.Label(new Rect(rect.x + 16f, rect.y + 78f, rect.width - 32f, 22f), selectionText);
            GUI.Label(new Rect(rect.x + 16f, rect.y + 100f, rect.width - 32f, 22f),
                "Controls: Left click drag obstacle | WASD/R/F move camera | Right mouse look | Esc back");

            if (GUI.Button(new Rect(rect.x + 16f, rect.y + 144f, 122f, 28f), "Back To Menu"))
            {
                CancelWorldObstacleDrag();
                ExitWorldBuildingMode();
                showPostTrialPrompt = true;
                PauseForPostTrialPrompt();
            }

            if (GUI.Button(new Rect(rect.x + 152f, rect.y + 144f, 122f, 28f), "Choose Scenario"))
                OpenOnboardingFromPostTrial();

            if (GUI.Button(new Rect(rect.x + 288f, rect.y + 144f, 122f, 28f), "Run Again"))
                StartNextTrialFromPrompt();
        }

        private void HandleReviewPlaybackReachedEnd()
        {
            if (!inRewindMode)
                return;

            showReviewCompletionPrompt = true;
        }

        private void DrawTrialStartPrompt()
        {
            float width = 460f;
            float height = 170f;
            Rect rect = new Rect((Screen.width - width) * 0.5f, 24f, width, height);

            GUI.Box(rect, "");
            GUI.Label(new Rect(rect.x + 18f, rect.y + 16f, rect.width - 36f, 28f),
                trialStartReady ? "SESSION READY" : "LOADING SESSION");
            GUI.Label(new Rect(rect.x + 18f, rect.y + 50f, rect.width - 36f, 48f),
                trialStartReady
                    ? "Robot, PWD, and cameras are loaded. Start when you are ready."
                    : "Preparing robot, pedestrians, and camera view...");

            GUI.enabled = trialStartReady;
            if (GUI.Button(new Rect(rect.x + 116f, rect.y + 108f, 228f, 38f),
                trialStartReady ? $"Start Trial [{startTrialKey}]" : "Loading..."))
                StartTrialFromPrompt();
            GUI.enabled = true;
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

            PauseForWorldBuildingMode();
            PrepareTopDownWorldBuildingCamera(worldBuildingCamera);

            worldBuildingCameraMovement = worldBuildingCamera.GetComponent<Cameramovement>();
            if (worldBuildingCameraMovement != null)
            {
                worldBuildingPreviousMovementTargetDisplay = worldBuildingCameraMovement.targetDisplay;
                worldBuildingCameraMovement.targetDisplay = 0;
                worldBuildingCameraMovement.enabled = true;
            }
            worldBuildingObstaclePublisher = FindObjectOfType<ObstaclePublisher>();
            selectedWorldObstacle = null;
            isDraggingWorldObstacle = false;

            if (hasPendingWorldBuildingCameraPose)
            {
                worldBuildingCamera.transform.position = pendingWorldBuildingCameraPosition;
                worldBuildingCamera.transform.rotation = pendingWorldBuildingCameraRotation;
                if (worldBuildingCamera.orthographic)
                    worldBuildingCamera.orthographicSize = pendingWorldBuildingOrthoSize;
            }

            worldBuildingCamera.enabled = true;
            showPostTrialPrompt = false;
            showReviewCompletionPrompt = false;
            inWorldBuildingMode = true;
        }

        private void ExitWorldBuildingMode()
        {
            CancelWorldObstacleDrag();
            inWorldBuildingMode = false;

            if (worldBuildingCameraMovement != null)
            {
                worldBuildingCameraMovement.enabled = false;
                if (hasWorldBuildingTargetDisplayOverride)
                    worldBuildingCameraMovement.targetDisplay = worldBuildingPreviousMovementTargetDisplay;
            }

            if (worldBuildingCamera != null)
            {
                worldBuildingCamera.enabled = false;
                if (hasWorldBuildingTargetDisplayOverride)
                    worldBuildingCamera.targetDisplay = worldBuildingPreviousTargetDisplay;
            }

            if (worldBuildingPreviousMainCamera != null &&
                worldBuildingPreviousMainCamera != worldBuildingCamera)
            {
                worldBuildingPreviousMainCamera.enabled = worldBuildingPreviousMainCameraEnabled;
            }
            else if (worldBuildingPreviousMainCamera == worldBuildingCamera &&
                     worldBuildingCamera != null)
            {
                worldBuildingCamera.enabled = worldBuildingPreviousMainCameraEnabled;
            }

            worldBuildingCameraMovement = null;
            worldBuildingCamera = null;
            worldBuildingObstaclePublisher = null;
            selectedWorldObstacle = null;
            hasPendingWorldBuildingCameraPose = false;
            worldBuildingPreviousMainCamera = null;
            hasWorldBuildingTargetDisplayOverride = false;

            ResumeFromWorldBuildingMode();
        }

        private void BeginWorldObstacleDrag()
        {
            Ray ray = worldBuildingCamera.ScreenPointToRay(Input.mousePosition);
            if (!Physics.Raycast(ray, out RaycastHit hit, 1000f))
            {
                selectedWorldObstacle = null;
                return;
            }

            TrackedObstacle obstacle = hit.collider.GetComponentInParent<TrackedObstacle>();
            if (obstacle == null || !obstacle.gameObject.activeInHierarchy)
            {
                selectedWorldObstacle = null;
                return;
            }

            selectedWorldObstacle = obstacle;
            selectedWorldObstacleStartPosition = obstacle.transform.position;
            worldBuildingDragPlane = new Plane(Vector3.up, obstacle.GetCenter());

            if (worldBuildingDragPlane.Raycast(ray, out float enter))
            {
                Vector3 hitPoint = ray.GetPoint(enter);
                selectedWorldObstacleDragOffset = obstacle.transform.position - hitPoint;
                isDraggingWorldObstacle = true;
                if (worldBuildingCameraMovement != null)
                    worldBuildingCameraMovement.enabled = false;
            }
        }

        private void UpdateWorldObstacleDrag()
        {
            if (selectedWorldObstacle == null)
                return;

            Ray ray = worldBuildingCamera.ScreenPointToRay(Input.mousePosition);
            if (!worldBuildingDragPlane.Raycast(ray, out float enter))
                return;

            Vector3 hitPoint = ray.GetPoint(enter);
            Vector3 targetPosition = hitPoint + selectedWorldObstacleDragOffset;
            targetPosition.y = selectedWorldObstacleStartPosition.y;
            selectedWorldObstacle.transform.position = targetPosition;
        }

        private void EndWorldObstacleDrag()
        {
            if (selectedWorldObstacle != null && worldBuildingObstaclePublisher != null)
                worldBuildingObstaclePublisher.ForcePublish();

            isDraggingWorldObstacle = false;
            if (worldBuildingCameraMovement != null && inWorldBuildingMode)
                worldBuildingCameraMovement.enabled = true;
        }

        private void CancelWorldObstacleDrag()
        {
            isDraggingWorldObstacle = false;
            if (worldBuildingCameraMovement != null && inWorldBuildingMode)
                worldBuildingCameraMovement.enabled = true;
        }

        private void PauseForWorldBuildingMode()
        {
            if (worldBuildingPausedTime)
                return;

            worldBuildingSavedTimeScale = Time.timeScale;
            Time.timeScale = 0f;
            worldBuildingPausedTime = true;
        }

        private void ResumeFromWorldBuildingMode()
        {
            if (!worldBuildingPausedTime)
                return;

            Time.timeScale = worldBuildingSavedTimeScale;
            worldBuildingPausedTime = false;
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

            var manualWheelchair = FindObjectOfType<IVI.ManualWheelchairController>();
            if (manualWheelchair != null)
                ZeroRigidbodies(manualWheelchair.transform);
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
            cameraToUse.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
        }

        private void ShowTrialStartPrompt()
        {
            showTrialStartPrompt = true;
            trialStartReady = false;
            trialWarmupPending = true;
            trialWarmupDelayFrames = 2;
        }

        private void ProcessTrialWarmup()
        {
            if (!showTrialStartPrompt || !trialWarmupPending)
                return;

            if (trialWarmupDelayFrames > 0)
            {
                trialWarmupDelayFrames--;
                return;
            }

            var sean = SEAN.SEAN.instance;
            if (sean == null || sean.robotTask == null)
                return;

            if (!sean.robotTask.hasPreparedTaskPreview)
            {
                sean.robotTask.PrepareTaskPreview();
            }

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
            }
        }

        private bool IsTrialPreviewReady()
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

            return true;
        }

        private void InitializeOnboardingSelection()
        {
            selectedPlayerMode = SessionOnboardingSettings.PlayerMode;
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
        }

        private void SetOnboardingVisible(bool visible)
        {
            if (showOnboarding == visible)
                return;

            showOnboarding = visible;

            if (showOnboarding)
            {
                onboardingSavedTimeScale = Time.timeScale;
                Time.timeScale = 0f;
                onboardingPausedTime = true;
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
            float panelHeight = Mathf.Min(Screen.height * 0.8f, 780f);
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

            GUI.Label(new Rect(x, y, 260f, 30f), "Who Is Playing?", onboardingSectionStyle);
            y += 42f;

            if (DrawChipButton(new Rect(x, y, 180f, 46f), "Robot", selectedPlayerMode == OnboardingPlayerMode.Robot))
                selectedPlayerMode = OnboardingPlayerMode.Robot;
            if (DrawChipButton(new Rect(x + 196f, y, 180f, 46f), "Human", selectedPlayerMode == OnboardingPlayerMode.Human))
                selectedPlayerMode = OnboardingPlayerMode.Human;
            y += 66f;

            if (selectedPlayerMode == OnboardingPlayerMode.Human)
            {
                GUI.Label(new Rect(x, y, innerWidth, 30f), "PWD Player Gender", onboardingSectionStyle);
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
            }

            GUI.Label(new Rect(x, y, innerWidth, 30f), "Session To Play", onboardingSectionStyle);
            y += 40f;

            var sceneChange = FindObjectOfType<SceneChange>();
            float footerTop = panelRect.y + panelRect.height - 86f;
            float sceneHeight = sceneChange != null && sceneChange.SceneCount > 0
                ? Mathf.Max(220f, footerTop - y - 12f)
                : 160f;
            DrawSceneSelection(new Rect(x, y, innerWidth, sceneHeight));

            string actionLabel = SessionOnboardingSettings.HasCompletedOnboarding ? "Apply and Reload" : "Start Session";
            if (GUI.Button(new Rect(panelRect.x + panelRect.width - 264f, panelRect.y + panelRect.height - 74f, 228f, 50f),
                actionLabel, onboardingPrimaryButtonStyle))
            {
                ApplyOnboardingSelection();
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

            Rect viewRect = new Rect(0f, 0f, rect.width - 18f, sceneChange.SceneCount * 48f);
            onboardingSceneScroll = GUI.BeginScrollView(rect, onboardingSceneScroll, viewRect);

            for (int i = 0; i < sceneChange.SceneCount; i++)
            {
                Rect rowRect = new Rect(0f, i * 48f, viewRect.width, 40f);
                bool isActive = i == selectedSceneIndex;
                string label = $"{i + 1}. {sceneChange.SceneNames[i]}";
                if (GUI.Button(rowRect, label, isActive ? onboardingSceneActiveButtonStyle : onboardingSceneButtonStyle))
                    selectedSceneIndex = i;
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

            SessionOnboardingSettings.Apply(selectedPlayerMode, selectedPwdGender, targetSceneIndex, targetSceneName);
            SetOnboardingVisible(false);

            if (sceneChange != null && sceneChange.SceneCount > 0)
            {
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
