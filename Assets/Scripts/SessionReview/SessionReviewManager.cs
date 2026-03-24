using UnityEngine;
using UnityEngine.SceneManagement;
using System.IO;

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

        private bool showOnboarding;
        private bool onboardingPausedTime;
        private float onboardingSavedTimeScale = 1f;
        private OnboardingPlayerMode selectedPlayerMode = OnboardingPlayerMode.Robot;
        private SEAN.Scenario.Agents.PwdGender selectedPwdGender = SEAN.Scenario.Agents.PwdGender.Male;
        private int selectedSceneIndex = -1;
        private Vector2 onboardingSceneScroll;

        private GUIStyle onboardingPanelStyle;
        private GUIStyle onboardingTitleStyle;
        private GUIStyle onboardingBodyStyle;
        private GUIStyle onboardingHintStyle;
        private GUIStyle onboardingPrimaryButtonStyle;
        private GUIStyle onboardingSecondaryButtonStyle;
        private GUIStyle onboardingChipStyle;
        private GUIStyle onboardingChipActiveStyle;
        private GUIStyle onboardingSceneButtonStyle;
        private GUIStyle onboardingSceneActiveButtonStyle;
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
            if (Input.GetKeyDown(onboardingToggleKey) && (SessionOnboardingSettings.HasCompletedOnboarding || !showOnboarding))
                SetOnboardingVisible(!showOnboarding);

            if (showOnboarding)
                return;

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
            if (showOnboarding)
                DrawOnboardingUI();

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
            if (inRewindMode || showOnboarding) return;

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

            const float panelWidth = 720f;
            const float panelHeight = 520f;
            float panelX = (Screen.width - panelWidth) * 0.5f;
            float panelY = (Screen.height - panelHeight) * 0.5f;
            Rect panelRect = new Rect(panelX, panelY, panelWidth, panelHeight);

            GUI.color = new Color(0f, 0f, 0f, 0.6f);
            GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), Texture2D.whiteTexture);
            GUI.color = Color.white;

            GUI.Box(panelRect, GUIContent.none, onboardingPanelStyle);

            float x = panelRect.x + 24f;
            float y = panelRect.y + 22f;
            float innerWidth = panelRect.width - 48f;

            GUI.Label(new Rect(x, y, innerWidth, 30f), "Session Onboarding", onboardingTitleStyle);
            y += 36f;

            GUI.Label(new Rect(x, y, innerWidth, 42f),
                "Choose who is playing, pick the PWD player gender when human control is enabled, and select the session scene to launch.",
                onboardingBodyStyle);
            y += 54f;

            GUI.Label(new Rect(x, y, 220f, 24f), "Who is playing?", onboardingTitleStyle);
            y += 34f;

            if (DrawChipButton(new Rect(x, y, 140f, 34f), "Robot", selectedPlayerMode == OnboardingPlayerMode.Robot))
                selectedPlayerMode = OnboardingPlayerMode.Robot;
            if (DrawChipButton(new Rect(x + 152f, y, 140f, 34f), "Human", selectedPlayerMode == OnboardingPlayerMode.Human))
                selectedPlayerMode = OnboardingPlayerMode.Human;
            y += 50f;

            if (selectedPlayerMode == OnboardingPlayerMode.Human)
            {
                GUI.Label(new Rect(x, y, innerWidth, 24f), "PWD player gender", onboardingTitleStyle);
                y += 34f;

                DrawGenderPreviewCard(new Rect(x, y, 184f, 162f), "Male", maleWheelchairPreview,
                    selectedPwdGender == SEAN.Scenario.Agents.PwdGender.Male,
                    () => selectedPwdGender = SEAN.Scenario.Agents.PwdGender.Male);

                DrawGenderPreviewCard(new Rect(x + 200f, y, 184f, 162f), "Female", femaleWheelchairPreview,
                    selectedPwdGender == SEAN.Scenario.Agents.PwdGender.Female,
                    () => selectedPwdGender = SEAN.Scenario.Agents.PwdGender.Female);

                if (DrawChipButton(new Rect(x, y + 172f, 140f, 34f), "Male", selectedPwdGender == SEAN.Scenario.Agents.PwdGender.Male))
                    selectedPwdGender = SEAN.Scenario.Agents.PwdGender.Male;
                if (DrawChipButton(new Rect(x + 152f, y + 172f, 140f, 34f), "Female", selectedPwdGender == SEAN.Scenario.Agents.PwdGender.Female))
                    selectedPwdGender = SEAN.Scenario.Agents.PwdGender.Female;
                y += 224f;
            }

            GUI.Label(new Rect(x, y, innerWidth, 24f), "Session to play", onboardingTitleStyle);
            y += 30f;

            DrawSceneSelection(new Rect(x, y, innerWidth, 260f));

            string actionLabel = SessionOnboardingSettings.HasCompletedOnboarding ? "Apply and Reload" : "Start Session";
            if (GUI.Button(new Rect(panelRect.x + panelRect.width - 184f, panelRect.y + panelRect.height - 58f, 160f, 36f),
                actionLabel, onboardingPrimaryButtonStyle))
            {
                ApplyOnboardingSelection();
            }

            if (SessionOnboardingSettings.HasCompletedOnboarding)
            {
                if (GUI.Button(new Rect(panelRect.x + 24f, panelRect.y + panelRect.height - 58f, 120f, 36f),
                    "Close", onboardingSecondaryButtonStyle))
                {
                    SetOnboardingVisible(false);
                }
            }
            else
            {
                GUI.Label(new Rect(panelRect.x + 24f, panelRect.y + panelRect.height - 52f, 260f, 24f),
                    $"[{onboardingToggleKey}] opens this panel later.",
                    onboardingHintStyle);
            }
        }

        private void DrawSceneSelection(Rect rect)
        {
            var sceneChange = FindObjectOfType<SceneChange>();
            if (sceneChange == null || sceneChange.SceneCount == 0)
            {
                GUI.Box(rect, "No SceneChange component with configured scenes was found.", onboardingSceneButtonStyle);
                return;
            }

            if (selectedSceneIndex < 0 || selectedSceneIndex >= sceneChange.SceneCount)
                selectedSceneIndex = sceneChange.CurrentSceneIndex;

            Rect viewRect = new Rect(0f, 0f, rect.width - 18f, sceneChange.SceneCount * 36f);
            onboardingSceneScroll = GUI.BeginScrollView(rect, onboardingSceneScroll, viewRect);

            for (int i = 0; i < sceneChange.SceneCount; i++)
            {
                Rect rowRect = new Rect(0f, i * 36f, viewRect.width, 30f);
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

            Rect imageRect = new Rect(rect.x + 10f, rect.y + 10f, rect.width - 20f, rect.height - 42f);
            if (preview != null)
            {
                GUI.DrawTexture(imageRect, preview, ScaleMode.ScaleToFit, true);
            }
            else
            {
                GUI.Label(imageRect, "Preview not found", onboardingHintStyle);
            }

            GUI.Label(new Rect(rect.x + 10f, rect.yMax - 28f, rect.width - 20f, 20f), label, onboardingBodyStyle);

            if (GUI.Button(rect, GUIContent.none, GUIStyle.none))
                onClick?.Invoke();
        }

        private void ApplyOnboardingSelection()
        {
            var sceneChange = FindObjectOfType<SceneChange>();
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

            SceneManager.LoadScene(targetSceneName);
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
                fontSize = 18,
                fontStyle = FontStyle.Bold,
                normal = { textColor = Color.white }
            };

            onboardingBodyStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12,
                wordWrap = true,
                normal = { textColor = new Color(0.82f, 0.86f, 0.9f) }
            };

            onboardingHintStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 11,
                normal = { textColor = new Color(0.62f, 0.67f, 0.72f) }
            };

            onboardingPrimaryButtonStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = 13,
                fontStyle = FontStyle.Bold,
                normal = { background = MakeTex(new Color(0.17f, 0.45f, 0.29f)), textColor = Color.white },
                hover = { background = MakeTex(new Color(0.22f, 0.56f, 0.35f)), textColor = Color.white },
                active = { background = MakeTex(new Color(0.12f, 0.32f, 0.21f)), textColor = Color.white }
            };

            onboardingSecondaryButtonStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = 12,
                normal = { background = MakeTex(new Color(0.19f, 0.22f, 0.27f)), textColor = Color.white },
                hover = { background = MakeTex(new Color(0.26f, 0.3f, 0.36f)), textColor = Color.white }
            };

            onboardingChipStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = 12,
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
                fontSize = 12,
                normal = { background = MakeTex(new Color(0.14f, 0.16f, 0.2f)), textColor = new Color(0.84f, 0.87f, 0.9f) },
                hover = { background = MakeTex(new Color(0.21f, 0.24f, 0.3f)), textColor = Color.white }
            };

            onboardingSceneActiveButtonStyle = new GUIStyle(onboardingSceneButtonStyle)
            {
                fontStyle = FontStyle.Bold,
                normal = { background = MakeTex(new Color(0.16f, 0.37f, 0.49f)), textColor = Color.white },
                hover = { background = MakeTex(new Color(0.2f, 0.45f, 0.6f)), textColor = Color.white }
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
