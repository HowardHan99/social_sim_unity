using System.Collections.Generic;
using UnityEngine;
using Rerun;
using System;

namespace SessionReview
{
    public enum PerspectiveMode
    {
        RobotFirstPerson,
        PWDFirstPerson,
        PedestrianOverShoulder,
        TopDown,
        FreeCam
    }

    public class RewindController : MonoBehaviour
    {
        public event Action PlaybackReachedEnd;

        [Header("References")]
        [SerializeField] private float topDownHeight = 50f;

        [Header("Pedestrian Over-Shoulder")]
        [SerializeField] private Vector3 overShoulderOffset = new Vector3(-0.5f, 2.0f, -2.0f);

        [Header("PWD Camera")]
        [SerializeField] private float pwdEyeHeight = 1.2f;

        [Header("Trail Overlay")]
        [SerializeField] private float trailLineWidth = 0.05f;

        [Header("Free Camera")]
        [SerializeField] private float freeCamMoveSpeed = 8f;
        [SerializeField] private float freeCamFastMultiplier = 2.5f;
        [SerializeField] private float freeCamLookSensitivity = 2.5f;
        [SerializeField] private float freeCamZoomSpeed = 10f;

        private LiveTrajectoryRecorder liveRecorder;

        private TrialRecord currentTrial;
        private StateRecording currentRecording;
        private ControlModeLog controlModeLog;
        private List<SignalAnnotation> signalAnnotations;

        private bool isRewinding;
        private float currentTime;
        private float playbackSpeed = 1f;
        private bool isPlaying;

        // Offset to convert trial Time.time -> recording-relative timestamps
        private float timeOffset;

        private PerspectiveMode perspectiveMode = PerspectiveMode.TopDown;
        private string selectedPedestrianId;

        private Camera rewindCamera;
        private AudioListener rewindAudioListener;
        private ComfortMotionBlur rewindComfortBlur;
        private Camera robotFirstPersonCam;
        private Camera pwdFirstPersonCam;
        private readonly Dictionary<Behaviour, bool> disabledCameraBehaviours = new Dictionary<Behaviour, bool>();
        private Vector3 savedCameraPos;
        private Quaternion savedCameraRot;
        private Camera savedMainCamera;
        private float freeCamYaw;
        private float freeCamPitch;
        private bool freeCamLooking;
        private Vector3 lastFreeCamMousePosition;

        private GameObject trailParent;
        private Dictionary<string, LineRenderer> trailRenderers = new Dictionary<string, LineRenderer>();
        private bool showTrails = true;

        private MultiAgentTrajectoryRenderer trajectoryRenderer;
        private TrajectoryManager drawTrajectoryManager;

        [Header("Plan Path")]
        [SerializeField] private Color activePlanColor = new Color(0.2f, 1f, 0.3f, 0.9f);
        [SerializeField] private float activePlanWidth = 0.14f;
        private LineRenderer activePlanLine;

        [Header("Signal Replay")]
        [SerializeField] private float lightingReplayHoldSeconds = 1.1f;
        [SerializeField] private float vlmReplayHoldSeconds = 2.0f;
        [SerializeField] private bool showSignalReplayStatus = true;
        private RobotSignalLightController reviewSignalLightController;
        private TTSManager reviewTtsManager;
        private string activeVlmReplayLabel = string.Empty;
        private float activeVlmReplayAge = -1f;
        private float lastEvaluatedSignalTime = -1f;
        private int lastTriggeredVlmAnnotationIndex = -1;
        private int lastTriggeredLightingAnnotationIndex = -1;
        private readonly HashSet<int> activeLightingReplayCandidates = new HashSet<int>();
        private bool manualLightingReplayTestActive;

        public bool IsRewinding => isRewinding;
        public float CurrentTime => currentTime;
        public float NormalizedTime => currentTrial != null && currentTrial.Duration > 0
            ? (currentTime - RecStartTime) / RecDuration
            : 0f;
        public float PlaybackSpeed => playbackSpeed;
        public bool IsPlaying => isPlaying;
        public PerspectiveMode CurrentPerspective => perspectiveMode;

        private float RecStartTime => currentTrial.startTime - timeOffset;
        private float RecEndTime => currentTrial.endTime - timeOffset;
        private float RecDuration => currentTrial.Duration;

        void Awake()
        {
            rewindCamera = gameObject.AddComponent<Camera>();
            rewindCamera.enabled = false;
            rewindCamera.nearClipPlane = 0.1f;
            rewindCamera.fieldOfView = 70f;
            rewindAudioListener = gameObject.GetComponent<AudioListener>();
            if (rewindAudioListener == null)
                rewindAudioListener = gameObject.AddComponent<AudioListener>();
            rewindAudioListener.enabled = false;

            // Inherit HDR/rendering-path from the main camera so tone-mapping
            // and post-processing behave consistently from the very first frame.
            var mainCam = Camera.main;
            if (mainCam != null)
            {
                rewindCamera.allowHDR = mainCam.allowHDR;
                rewindCamera.renderingPath = mainCam.renderingPath;
            }

            rewindComfortBlur = gameObject.GetComponent<ComfortMotionBlur>();
            if (rewindComfortBlur == null)
                rewindComfortBlur = gameObject.AddComponent<ComfortMotionBlur>();
        }

        public void EnterRewind(TrialRecord trial, StateRecording recording,
            ControlModeLog modeLog, MultiAgentTrajectoryRenderer trajRenderer, float recordingTimeOffset = 0f,
            List<SignalAnnotation> annotations = null)
        {
            if (trial == null || recording == null) return;

            currentTrial = trial;
            currentRecording = recording;
            controlModeLog = modeLog;
            trajectoryRenderer = trajRenderer;
            drawTrajectoryManager = FindObjectOfType<TrajectoryManager>();
            timeOffset = recordingTimeOffset;
            signalAnnotations = annotations ?? trial.signalAnnotations ?? new List<SignalAnnotation>();

            if (currentRecording.timelineDict == null)
                currentRecording.BuildCache();

            liveRecorder = FindObjectOfType<LiveTrajectoryRecorder>();
            reviewSignalLightController = FindObjectOfType<RobotSignalLightController>();
            reviewTtsManager = FindObjectOfType<TTSManager>();
            activeVlmReplayLabel = string.Empty;
            activeVlmReplayAge = -1f;
            lastTriggeredVlmAnnotationIndex = -1;
            lastTriggeredLightingAnnotationIndex = -1;
            activeLightingReplayCandidates.Clear();
            manualLightingReplayTestActive = false;

            savedMainCamera = Camera.main;
            if (savedMainCamera != null)
            {
                savedCameraPos = savedMainCamera.transform.position;
                savedCameraRot = savedMainCamera.transform.rotation;
                // Disable the main camera so it doesn't render simultaneously with
                // rewindCamera — two cameras rendering to the same target in the same
                // frame causes brightness flickering and depth-sort artefacts.
                savedMainCamera.enabled = false;
            }

            if (rewindAudioListener != null)
                rewindAudioListener.enabled = true;

            FindAgentCameras();
            DisableLiveCameraDrivers();

            currentTime = RecStartTime;
            lastEvaluatedSignalTime = currentTime;
            isRewinding = true;
            isPlaying = false;
            playbackSpeed = 1f;

            ApplyStateAtCurrentTime();
            SetPerspective(PerspectiveMode.TopDown);
        }

        public void ExitRewind()
        {
            if (!isRewinding) return;
            isRewinding = false;
            isPlaying = false;
            freeCamLooking = false;
            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;

            DisableAllRewindCameras();

            if (savedMainCamera != null)
            {
                savedMainCamera.transform.position = savedCameraPos;
                savedMainCamera.transform.rotation = savedCameraRot;
                savedMainCamera.enabled = true;
            }

            if (rewindAudioListener != null)
                rewindAudioListener.enabled = false;

            ClearTrails();
            ClearReplayBehaviors();
            transformCache.Clear();
            RestoreLiveCameraDrivers();
            currentTrial = null;
            currentRecording = null;
            signalAnnotations = null;
            lastEvaluatedSignalTime = -1f;
            lastTriggeredVlmAnnotationIndex = -1;
            lastTriggeredLightingAnnotationIndex = -1;
            activeLightingReplayCandidates.Clear();
            manualLightingReplayTestActive = false;
        }

        void Update()
        {
            if (!isRewinding) return;

            if (isPlaying)
            {
                currentTime += Time.unscaledDeltaTime * playbackSpeed;
                if (currentTime > RecEndTime)
                {
                    currentTime = RecEndTime;
                    isPlaying = false;
                    PlaybackReachedEnd?.Invoke();
                }
                else if (currentTime < RecStartTime)
                {
                    currentTime = RecStartTime;
                    isPlaying = false;
                }
                ApplyStateAtCurrentTime();
            }

            if (showTrails)
                UpdateTrails();

            UpdateActivePlanPath();

            if (perspectiveMode == PerspectiveMode.PedestrianOverShoulder)
                UpdateOverShoulderCamera();
            else if (perspectiveMode == PerspectiveMode.RobotFirstPerson)
                UpdateRobotFPCamera();
            else if (perspectiveMode == PerspectiveMode.PWDFirstPerson)
                UpdatePwdFPCamera();
            else if (perspectiveMode == PerspectiveMode.FreeCam)
                UpdateFreeCam();
        }

        public void SetNormalizedTime(float t)
        {
            if (currentTrial == null) return;
            currentTime = RecStartTime + Mathf.Clamp01(t) * RecDuration;
            ApplyStateAtCurrentTime();
        }

        public void StepForward()
        {
            if (currentTrial == null) return;
            currentTime = Mathf.Min(currentTime + 0.1f, RecEndTime);
            ApplyStateAtCurrentTime();
        }

        public void StepBackward()
        {
            if (currentTrial == null) return;
            currentTime = Mathf.Max(currentTime - 0.1f, RecStartTime);
            ApplyStateAtCurrentTime();
        }

        public void TogglePlayPause()
        {
            isPlaying = !isPlaying;
        }

        public void SetPlaybackSpeed(float speed)
        {
            playbackSpeed = Mathf.Clamp(speed, -4f, 4f);
        }

        public void JumpToStart()
        {
            if (currentTrial == null) return;
            currentTime = RecStartTime;
            ApplyStateAtCurrentTime();
        }

        public void JumpToEnd()
        {
            if (currentTrial == null) return;
            currentTime = RecEndTime;
            ApplyStateAtCurrentTime();
        }

        public void SetPerspective(PerspectiveMode mode)
        {
            perspectiveMode = mode;
            DisableAllRewindCameras();

            switch (mode)
            {
                case PerspectiveMode.RobotFirstPerson:
                    ActivateRobotFP();
                    break;
                case PerspectiveMode.PWDFirstPerson:
                    ActivatePWDFP();
                    break;
                case PerspectiveMode.PedestrianOverShoulder:
                    ActivatePedestrianView();
                    break;
                case PerspectiveMode.TopDown:
                    ActivateTopDown();
                    break;
                case PerspectiveMode.FreeCam:
                    ActivateFreeCam();
                    break;
            }

            if (rewindComfortBlur != null)
                rewindComfortBlur.TriggerTransitionBlur();
        }

        public void CyclePerspective()
        {
            int next = ((int)perspectiveMode + 1) % 5;
            SetPerspective((PerspectiveMode)next);
        }

        public void SelectPedestrian(string objectId)
        {
            selectedPedestrianId = objectId;
            if (perspectiveMode == PerspectiveMode.PedestrianOverShoulder)
                SetPerspective(PerspectiveMode.PedestrianOverShoulder);
        }

        public void ToggleTrails()
        {
            showTrails = !showTrails;
            if (!showTrails) ClearTrails();
        }

        public bool IsTopDownActive()
        {
            return perspectiveMode == PerspectiveMode.TopDown &&
                   rewindCamera != null &&
                   rewindCamera.enabled &&
                   rewindCamera.orthographic;
        }

        public void FocusTopDownOnBounds(Bounds bounds, bool switchToTopDown = true)
        {
            if (switchToTopDown)
                SetPerspective(PerspectiveMode.TopDown);

            FocusActiveTopDownCamera(bounds);
        }

        public void AdjustTopDownZoom(float zoomMultiplier)
        {
            if (!IsTopDownActive())
                return;

            float nextSize = rewindCamera.orthographicSize * Mathf.Max(0.1f, zoomMultiplier);
            rewindCamera.orthographicSize = Mathf.Clamp(nextSize, 3f, topDownHeight * 0.5f);
        }

        public void ZoomTopDownAtScreenPoint(Vector2 screenPoint, float zoomMultiplier)
        {
            if (!IsTopDownActive())
                return;

            if (!TryGetTopDownGroundPoint(screenPoint, out Vector3 beforePoint))
            {
                AdjustTopDownZoom(zoomMultiplier);
                return;
            }

            AdjustTopDownZoom(zoomMultiplier);

            if (TryGetTopDownGroundPoint(screenPoint, out Vector3 afterPoint))
            {
                Vector3 delta = beforePoint - afterPoint;
                delta.y = 0f;
                rewindCamera.transform.position += delta;
            }
        }

        public void PanTopDownFromScreenDelta(Vector2 screenDelta)
        {
            if (!IsTopDownActive())
                return;

            float pixelsPerScreenHeight = Mathf.Max(Screen.height, 1f);
            float worldUnitsPerPixel = (rewindCamera.orthographicSize * 2f) / pixelsPerScreenHeight;
            Vector3 translation = new Vector3(-screenDelta.x * worldUnitsPerPixel, 0f, -screenDelta.y * worldUnitsPerPixel);
            rewindCamera.transform.position += translation;
        }

        private void ApplyStateAtCurrentTime()
        {
            if (liveRecorder == null) return;

            var states = liveRecorder.GetStateAtTime(currentTime);
            if (states == null || states.Count == 0) return;

            foreach (var kvp in states)
            {
                Transform t = FindTransformForId(kvp.Key);
                if (t == null) continue;
                t.position = kvp.Value.position;
                t.rotation = kvp.Value.rotation;
            }

            ApplyDrawTrajectoryFollowState();

            ApplySignalReplayState();
        }

        private void ApplyDrawTrajectoryFollowState()
        {
            if (drawTrajectoryManager == null)
                drawTrajectoryManager = FindObjectOfType<TrajectoryManager>();

            if (drawTrajectoryManager == null || currentTrial == null)
                return;

            float elapsedSeconds = Mathf.Max(0f, currentTime - RecStartTime);
            drawTrajectoryManager.ApplyFollowTrajectoryToRobot(elapsedSeconds);
        }

        private void ApplySignalReplayState()
        {
            TriggerCrossedSignalBehaviors();
            TriggerLightingReplayWindowFallback();
            ApplyVlmReplayState();
        }

        private void TriggerCrossedSignalBehaviors()
        {
            if (signalAnnotations == null || signalAnnotations.Count == 0)
            {
                lastEvaluatedSignalTime = currentTime;
                return;
            }

            float previousTime = lastEvaluatedSignalTime;
            if (previousTime < 0f)
            {
                lastEvaluatedSignalTime = currentTime;
                return;
            }

            bool movingForward = currentTime >= previousTime;
            if (!movingForward)
            {
                if (reviewSignalLightController != null)
                    reviewSignalLightController.ClearReviewSignalState();
                if (reviewTtsManager != null)
                    reviewTtsManager.StopPlayback();
                lastTriggeredVlmAnnotationIndex = -1;
                lastTriggeredLightingAnnotationIndex = -1;
                activeLightingReplayCandidates.Clear();
                Debug.Log($"[SessionReview] Replay moved backward to t={currentTime:F2}; reset signal replay state.");
                lastEvaluatedSignalTime = currentTime;
                return;
            }

            for (int i = 0; i < signalAnnotations.Count; i++)
            {
                SignalAnnotation annotation = signalAnnotations[i];
                if (annotation.timestamp <= previousTime || annotation.timestamp > currentTime)
                    continue;

                if (annotation.type == SignalAnnotationType.VlmCapture)
                {
                    TriggerVlmReplay(annotation, i);
                }
                else
                {
                    TriggerLightingReplay(annotation, i);
                }
            }

            lastEvaluatedSignalTime = currentTime;
        }

        private void TriggerLightingReplayWindowFallback()
        {
            if (signalAnnotations == null || signalAnnotations.Count == 0)
                return;

            for (int i = 0; i < signalAnnotations.Count; i++)
            {
                SignalAnnotation annotation = signalAnnotations[i];
                if (!IsLightingAnnotation(annotation.type))
                    continue;

                float age = currentTime - annotation.timestamp;
                bool insideWindow = age >= 0f && age <= lightingReplayHoldSeconds;
                if (!insideWindow)
                {
                    activeLightingReplayCandidates.Remove(i);
                    continue;
                }

                if (activeLightingReplayCandidates.Contains(i))
                    continue;

                Debug.Log($"[SessionReview] LightingAnnotation entered replay window at t={currentTime:F2} (annotation t={annotation.timestamp:F2}, type={annotation.type}).");
                TriggerLightingReplay(annotation, i);
                activeLightingReplayCandidates.Add(i);
            }
        }

        private void TriggerLightingReplay(SignalAnnotation annotation, int annotationIndex)
        {
            if (reviewSignalLightController == null)
                reviewSignalLightController = FindObjectOfType<RobotSignalLightController>();

            if (reviewSignalLightController == null)
            {
                Debug.LogWarning("[SessionReview] LightingAnnotation replay skipped because RobotSignalLightController was not found.");
                return;
            }

            if (lastTriggeredLightingAnnotationIndex == annotationIndex)
                return;

            Debug.Log($"[SessionReview] LightingAnnotation replay trigger at t={currentTime:F2} for annotation #{annotationIndex} ({annotation.type}).");
            switch (annotation.type)
            {
                case SignalAnnotationType.LightingLeft:
                    reviewSignalLightController.PlayReviewFlashLeft();
                    break;
                case SignalAnnotationType.LightingRight:
                    reviewSignalLightController.PlayReviewFlashRight();
                    break;
                case SignalAnnotationType.LightingBoth:
                    reviewSignalLightController.PlayReviewFlashBoth();
                    break;
                default:
                    return;
            }

            lastTriggeredLightingAnnotationIndex = annotationIndex;
        }

        private void TriggerVlmReplay(SignalAnnotation annotation, int annotationIndex)
        {
            if (lastTriggeredVlmAnnotationIndex == annotationIndex)
                return;

            if (reviewTtsManager == null)
                reviewTtsManager = FindObjectOfType<TTSManager>();

            string responseText = annotation.metadata;
            if (!string.IsNullOrWhiteSpace(responseText) && reviewTtsManager != null)
            {
                reviewTtsManager.PlaySpeech(responseText);
                lastTriggeredVlmAnnotationIndex = annotationIndex;
            }
        }

        private void ApplyVlmReplayState()
        {
            activeVlmReplayLabel = string.Empty;
            activeVlmReplayAge = -1f;

            if (signalAnnotations == null)
                return;

            SignalAnnotation best = null;
            float bestAge = float.MaxValue;
            for (int i = 0; i < signalAnnotations.Count; i++)
            {
                SignalAnnotation annotation = signalAnnotations[i];
                if (annotation.type != SignalAnnotationType.VlmCapture)
                    continue;

                float age = currentTime - annotation.timestamp;
                if (age < 0f || age > vlmReplayHoldSeconds)
                    continue;

                if (age < bestAge)
                {
                    best = annotation;
                    bestAge = age;
                }
            }

            if (best != null)
            {
                activeVlmReplayLabel = !string.IsNullOrWhiteSpace(best.metadata)
                    ? best.metadata
                    : (string.IsNullOrWhiteSpace(best.label) ? "VLMAnnotation active" : best.label);
                activeVlmReplayAge = bestAge;
            }
        }

        private void ClearReplayBehaviors()
        {
            activeVlmReplayLabel = string.Empty;
            activeVlmReplayAge = -1f;

            if (reviewSignalLightController == null)
                reviewSignalLightController = FindObjectOfType<RobotSignalLightController>();

            if (reviewSignalLightController != null)
                reviewSignalLightController.ClearReviewSignalState();
            if (reviewTtsManager == null)
                reviewTtsManager = FindObjectOfType<TTSManager>();
            if (reviewTtsManager != null)
                reviewTtsManager.StopPlayback();
            activeLightingReplayCandidates.Clear();
            manualLightingReplayTestActive = false;
        }

        private static bool IsLightingAnnotation(SignalAnnotationType type)
        {
            return type == SignalAnnotationType.LightingLeft ||
                   type == SignalAnnotationType.LightingRight ||
                   type == SignalAnnotationType.LightingBoth;
        }

        public void ToggleLightingReplayTest()
        {
            if (reviewSignalLightController == null)
                reviewSignalLightController = FindObjectOfType<RobotSignalLightController>();

            if (reviewSignalLightController == null)
            {
                Debug.LogWarning("[SessionReview] Lighting replay test failed: RobotSignalLightController was not found.");
                return;
            }

            manualLightingReplayTestActive = !manualLightingReplayTestActive;
            reviewSignalLightController.SetReviewSignalState(manualLightingReplayTestActive, manualLightingReplayTestActive);
            Debug.Log($"[SessionReview] Lighting replay test toggled: active={manualLightingReplayTestActive}.");
        }

        public void PlayAudioReplayTest()
        {
            if (reviewTtsManager == null)
                reviewTtsManager = FindObjectOfType<TTSManager>();

            if (reviewTtsManager == null)
            {
                Debug.LogWarning("[SessionReview] Audio replay test failed: TTSManager was not found.");
                return;
            }

            string speechText = GetLatestReplaySpeechText();
            if (string.IsNullOrWhiteSpace(speechText))
                speechText = "Replay audio test.";

            Debug.Log($"[SessionReview] Audio replay test speaking: {speechText}");
            reviewTtsManager.PlaySpeech(speechText);
        }

        private string GetLatestReplaySpeechText()
        {
            if (signalAnnotations == null)
                return string.Empty;

            for (int i = signalAnnotations.Count - 1; i >= 0; i--)
            {
                SignalAnnotation annotation = signalAnnotations[i];
                if (annotation != null &&
                    annotation.type == SignalAnnotationType.VlmCapture &&
                    !string.IsNullOrWhiteSpace(annotation.metadata))
                {
                    return annotation.metadata;
                }
            }

            return string.Empty;
        }

        private Dictionary<string, Transform> transformCache = new Dictionary<string, Transform>();

        private Transform FindTransformForId(string objectId)
        {
            if (transformCache.TryGetValue(objectId, out Transform cached) && cached != null)
                return cached;

            var sean = SEAN.SEAN.instance;
            if (sean != null)
            {
                if (sean.robot != null && sean.robot.base_link != null &&
                    SessionTracker.GetObjectId(sean.robot.base_link) == objectId)
                {
                    transformCache[objectId] = sean.robot.base_link.transform;
                    return sean.robot.base_link.transform;
                }

                var mwc = FindObjectOfType<IVI.ManualWheelchairController>();
                if (mwc != null && SessionTracker.GetObjectId(mwc.gameObject) == objectId)
                {
                    Transform resolved = SessionTracker.ResolveTrackingTransform(mwc.gameObject);
                    transformCache[objectId] = resolved;
                    return resolved;
                }

                if (sean.pedestrianBehavior != null && sean.pedestrianBehavior.agents != null)
                {
                    foreach (var agent in sean.pedestrianBehavior.agents)
                    {
                        if (agent != null && SessionTracker.GetObjectId(agent.gameObject) == objectId)
                        {
                            transformCache[objectId] = agent.transform;
                            return agent.transform;
                        }
                    }
                }
            }

            // Fallback: scan all INavigable instances
            foreach (var nav in FindObjectsOfType<IVI.INavigable>())
            {
                if (nav != null && SessionTracker.GetObjectId(nav.gameObject) == objectId)
                {
                    transformCache[objectId] = nav.transform;
                    return nav.transform;
                }
            }

            // Last resort: find by GameObject name matching the objectId
            var go = GameObject.Find(objectId);
            if (go != null)
            {
                transformCache[objectId] = go.transform;
                return go.transform;
            }

            return null;
        }

        public Transform ResolveTransformForObjectId(string objectId)
        {
            return FindTransformForId(objectId);
        }

        public Camera GetActiveReviewCamera()
        {
            if (rewindCamera != null && rewindCamera.enabled)
                return rewindCamera;

            if (savedMainCamera != null && savedMainCamera.enabled)
                return savedMainCamera;

            return null;
        }

        private void FindAgentCameras()
        {
            robotFirstPersonCam = null;
            pwdFirstPersonCam = null;

            var sean = SEAN.SEAN.instance;
            if (sean != null && sean.robot != null)
                robotFirstPersonCam = sean.robot.camera_first;

            var mwc = FindObjectOfType<IVI.ManualWheelchairController>();
            if (mwc != null)
            {
                var cam = mwc.GetComponentInChildren<Camera>(true);
                if (cam != null)
                    pwdFirstPersonCam = cam;
            }
        }

        private void DisableLiveCameraDrivers()
        {
            disabledCameraBehaviours.Clear();
            CacheAndDisableCameraBehaviours(robotFirstPersonCam);
            CacheAndDisableCameraBehaviours(pwdFirstPersonCam);
        }

        private void RestoreLiveCameraDrivers()
        {
            foreach (var kvp in disabledCameraBehaviours)
            {
                if (kvp.Key != null)
                    kvp.Key.enabled = kvp.Value;
            }

            disabledCameraBehaviours.Clear();
        }

        private void CacheAndDisableCameraBehaviours(Camera camera)
        {
            if (camera == null)
                return;

            CacheAndDisableBehaviour(camera.GetComponent<IVI.CameraScript>());
            CacheAndDisableBehaviour(camera.GetComponent<IVI.WheelchairCameraSmoothing>());
        }

        private void CacheAndDisableBehaviour(Behaviour behaviour)
        {
            if (behaviour == null || disabledCameraBehaviours.ContainsKey(behaviour))
                return;

            disabledCameraBehaviours[behaviour] = behaviour.enabled;
            behaviour.enabled = false;
        }

        private void DisableAllRewindCameras()
        {
            rewindCamera.enabled = false;
            rewindCamera.orthographic = false;
            if (robotFirstPersonCam != null) robotFirstPersonCam.enabled = false;
            if (pwdFirstPersonCam != null) pwdFirstPersonCam.enabled = false;
        }

        private void ActivateRobotFP()
        {
            if (robotFirstPersonCam != null)
            {
                ConfigureRewindFromSourceCamera(robotFirstPersonCam);
                rewindCamera.enabled = true;
                return;
            }

            var sean = SEAN.SEAN.instance;
            if (sean != null && sean.robot != null && sean.robot.base_link != null)
            {
                Transform robotT = sean.robot.base_link.transform;
                rewindCamera.transform.position = robotT.position + Vector3.up * 0.5f;
                rewindCamera.transform.rotation = robotT.rotation;
                rewindCamera.enabled = true;
            }
        }

        private void ActivatePWDFP()
        {
            if (pwdFirstPersonCam != null)
            {
                ConfigureRewindFromSourceCamera(pwdFirstPersonCam);
                rewindCamera.enabled = true;
                return;
            }

            var mwc = FindObjectOfType<IVI.ManualWheelchairController>();
            if (mwc != null)
            {
                rewindCamera.transform.position = mwc.transform.position + Vector3.up * pwdEyeHeight;
                rewindCamera.transform.rotation = mwc.transform.rotation;
                rewindCamera.enabled = true;
            }
        }

        private void ActivatePedestrianView()
        {
            if (string.IsNullOrEmpty(selectedPedestrianId))
            {
                if (currentTrial != null)
                {
                    foreach (var role in currentTrial.agentRoles)
                    {
                        if (role.role == AgentRole.BackgroundPed || role.role == AgentRole.BackgroundPWD)
                        {
                            selectedPedestrianId = role.objectId;
                            break;
                        }
                    }
                }
            }

            if (string.IsNullOrEmpty(selectedPedestrianId))
            {
                ActivateTopDown();
                return;
            }

            Transform agentT = FindTransformForId(selectedPedestrianId);
            if (agentT != null)
            {
                Vector3 pos = agentT.position + agentT.rotation * overShoulderOffset;
                rewindCamera.transform.position = pos;
                rewindCamera.transform.LookAt(agentT.position + Vector3.up * 1.5f);
                rewindCamera.enabled = true;
                return;
            }

            ActivateTopDown();
        }

        private void UpdateOverShoulderCamera()
        {
            if (string.IsNullOrEmpty(selectedPedestrianId) || !rewindCamera.enabled) return;

            Transform agentT = FindTransformForId(selectedPedestrianId);
            if (agentT != null)
            {
                rewindCamera.transform.position = agentT.position + agentT.rotation * overShoulderOffset;
                rewindCamera.transform.LookAt(agentT.position + Vector3.up * 1.5f);
            }
        }

        private void UpdateRobotFPCamera()
        {
            if (robotFirstPersonCam != null)
            {
                ConfigureRewindFromSourceCamera(robotFirstPersonCam);
                rewindCamera.enabled = true;
            }
        }

        private void UpdatePwdFPCamera()
        {
            if (pwdFirstPersonCam != null)
            {
                ConfigureRewindFromSourceCamera(pwdFirstPersonCam);
                rewindCamera.enabled = true;
            }
        }

        private void ActivateTopDown()
        {
            Vector3 center = ComputeSceneCenter();
            var defaultBounds = new Bounds(center, new Vector3(topDownHeight, 1f, topDownHeight));
            FocusActiveTopDownCamera(defaultBounds);
        }

        private void FocusActiveTopDownCamera(Bounds bounds)
        {
            float aspect = rewindCamera != null && rewindCamera.aspect > 0.01f
                ? rewindCamera.aspect
                : Mathf.Max(1f, (float)Screen.width / Mathf.Max(1, Screen.height));

            float paddedWidth = Mathf.Max(bounds.size.x, 1f) + 4f;
            float paddedDepth = Mathf.Max(bounds.size.z, 1f) + 4f;
            float orthographicSize = Mathf.Max(paddedDepth * 0.5f, paddedWidth / Mathf.Max(aspect, 0.01f) * 0.5f);
            float cameraHeight = Mathf.Max(bounds.max.y + 10f, bounds.center.y + topDownHeight);

            rewindCamera.transform.position = new Vector3(bounds.center.x, cameraHeight, bounds.center.z);
            rewindCamera.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            rewindCamera.orthographic = true;
            rewindCamera.orthographicSize = Mathf.Max(orthographicSize, 5f);
            rewindCamera.enabled = true;
        }

        private bool TryGetTopDownGroundPoint(Vector2 screenPoint, out Vector3 groundPoint)
        {
            groundPoint = Vector3.zero;

            if (!IsTopDownActive())
                return false;

            Ray ray = rewindCamera.ScreenPointToRay(screenPoint);
            Plane plane = new Plane(Vector3.up, Vector3.zero);
            if (!plane.Raycast(ray, out float enter))
                return false;

            groundPoint = ray.GetPoint(enter);
            return true;
        }

        private void ActivateFreeCam()
        {
            Vector3 center = ComputeSceneCenter();
            rewindCamera.transform.position = center + Vector3.up * 10f + Vector3.back * 10f;
            rewindCamera.transform.LookAt(center);
            rewindCamera.orthographic = false;
            rewindCamera.enabled = true;
            Vector3 euler = rewindCamera.transform.eulerAngles;
            freeCamYaw = euler.y;
            freeCamPitch = NormalizePitch(euler.x);
            freeCamLooking = false;
        }

        private void UpdateFreeCam()
        {
            if (rewindCamera == null || !rewindCamera.enabled)
                return;

            if (Input.GetKeyDown(KeyCode.Mouse2))
            {
                freeCamLooking = true;
                Cursor.visible = false;
                Cursor.lockState = CursorLockMode.Locked;
                lastFreeCamMousePosition = Input.mousePosition;
                if (rewindComfortBlur != null)
                    rewindComfortBlur.TriggerTransitionBlur();
            }
            else if (Input.GetKeyUp(KeyCode.Mouse2))
            {
                freeCamLooking = false;
                Cursor.visible = true;
                Cursor.lockState = CursorLockMode.None;
            }

            if (freeCamLooking)
            {
                Vector3 mouseDelta = Input.mousePosition - lastFreeCamMousePosition;
                freeCamYaw += mouseDelta.x * freeCamLookSensitivity * 0.02f;
                freeCamPitch -= mouseDelta.y * freeCamLookSensitivity * 0.02f;
                freeCamPitch = Mathf.Clamp(freeCamPitch, -85f, 85f);
                rewindCamera.transform.rotation = Quaternion.Euler(freeCamPitch, freeCamYaw, 0f);
                lastFreeCamMousePosition = Input.mousePosition;
            }

            float speed = freeCamMoveSpeed;
            if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))
                speed *= freeCamFastMultiplier;

            Vector3 move = Vector3.zero;
            if (Input.GetKey(KeyCode.W))
                move += rewindCamera.transform.forward;
            if (Input.GetKey(KeyCode.S))
                move -= rewindCamera.transform.forward;
            if (Input.GetKey(KeyCode.A))
                move -= rewindCamera.transform.right;
            if (Input.GetKey(KeyCode.D))
                move += rewindCamera.transform.right;
            if (Input.GetKey(KeyCode.Q))
                move += Vector3.up;
            if (Input.GetKey(KeyCode.E))
                move -= Vector3.up;

            if (move.sqrMagnitude > 0.001f)
            {
                move.Normalize();
                rewindCamera.transform.position += move * speed * Time.unscaledDeltaTime;
            }

            float scroll = Input.mouseScrollDelta.y;
            if (Mathf.Abs(scroll) > 0.01f)
                rewindCamera.transform.position += rewindCamera.transform.forward * (scroll * freeCamZoomSpeed);
        }

        private static float NormalizePitch(float pitch)
        {
            while (pitch > 180f)
                pitch -= 360f;
            return pitch;
        }

        private void ConfigureRewindFromSourceCamera(Camera sourceCamera)
        {
            if (sourceCamera == null || rewindCamera == null)
                return;

            rewindCamera.transform.position = sourceCamera.transform.position;
            rewindCamera.transform.rotation = sourceCamera.transform.rotation;
            rewindCamera.orthographic = sourceCamera.orthographic;
            rewindCamera.fieldOfView = sourceCamera.fieldOfView;
            rewindCamera.orthographicSize = sourceCamera.orthographicSize;
            rewindCamera.nearClipPlane = sourceCamera.nearClipPlane;
            rewindCamera.farClipPlane = sourceCamera.farClipPlane;
            // Match HDR and rendering path so post-processing / tone-mapping
            // behaviour is identical to the source camera — mismatch causes
            // brightness differences each time perspectives switch.
            rewindCamera.allowHDR = sourceCamera.allowHDR;
            rewindCamera.renderingPath = sourceCamera.renderingPath;
        }

        private Vector3 ComputeSceneCenter()
        {
            if (currentTrial == null || liveRecorder == null)
                return Vector3.zero;

            var states = liveRecorder.GetStateAtTime(currentTime);
            if (states == null || states.Count == 0)
                return Vector3.zero;

            Vector3 sum = Vector3.zero;
            foreach (var kvp in states)
                sum += kvp.Value.position;

            return sum / states.Count;
        }

        private void UpdateTrails()
        {
            if (currentTrial == null || currentRecording == null) return;

            if (trailParent == null)
            {
                trailParent = new GameObject("RewindTrails");
                trailParent.transform.SetParent(transform);
            }

            foreach (var roleEntry in currentTrial.agentRoles)
            {
                string objectId = roleEntry.objectId;
                if (!currentRecording.timelineDict.TryGetValue(objectId, out ObjectStateTimeline timeline))
                    continue;

                var positions = new List<Vector3>();
                foreach (var state in timeline.states)
                {
                    if (state.timestamp < RecStartTime) continue;
                    if (state.timestamp > currentTime) break;
                    Vector3 p = state.position;
                    p.y += 0.03f;
                    positions.Add(p);
                }

                if (positions.Count < 2) continue;

                if (!trailRenderers.TryGetValue(objectId, out LineRenderer lr))
                {
                    var obj = new GameObject("Trail_" + objectId);
                    obj.transform.SetParent(trailParent.transform);
                    lr = obj.AddComponent<LineRenderer>();
                    lr.startWidth = trailLineWidth;
                    lr.endWidth = trailLineWidth;
                    lr.useWorldSpace = true;
                    lr.material = new Material(Shader.Find("Sprites/Default"));

                    Color c = GetAgentColor(roleEntry.role);
                    lr.startColor = new Color(c.r, c.g, c.b, 0.2f);
                    lr.endColor = c;

                    trailRenderers[objectId] = lr;
                }

                lr.positionCount = positions.Count;
                lr.SetPositions(positions.ToArray());
            }
        }

        private Color GetAgentColor(AgentRole role)
        {
            switch (role)
            {
                case AgentRole.Robot: return new Color(0.9f, 0.2f, 0.2f);
                case AgentRole.PWDPlayer: return new Color(0.7f, 0.2f, 0.8f);
                case AgentRole.BackgroundPWD: return new Color(0.2f, 0.5f, 0.9f);
                default: return new Color(0.2f, 0.8f, 0.3f);
            }
        }

        private void UpdateActivePlanPath()
        {
            if (liveRecorder == null) return;

            Vector3[] plan = liveRecorder.GetPlanAtTime(currentTime);

            if (plan == null || plan.Length < 2)
            {
                if (activePlanLine != null)
                    activePlanLine.enabled = false;
                return;
            }

            if (activePlanLine == null)
            {
                var obj = new GameObject("ActivePlanPath");
                obj.transform.SetParent(transform);
                activePlanLine = obj.AddComponent<LineRenderer>();
                activePlanLine.startWidth = activePlanWidth;
                activePlanLine.endWidth = activePlanWidth;
                activePlanLine.useWorldSpace = true;
                activePlanLine.material = new Material(Shader.Find("Sprites/Default"));
                activePlanLine.material.color = Color.white;
                activePlanLine.startColor = activePlanColor;
                activePlanLine.endColor = activePlanColor;
                activePlanLine.numCornerVertices = 4;
            }

            activePlanLine.positionCount = plan.Length;
            for (int i = 0; i < plan.Length; i++)
            {
                Vector3 p = plan[i];
                p.y += 0.08f;
                activePlanLine.SetPosition(i, p);
            }
            activePlanLine.enabled = true;
        }

        private void ClearTrails()
        {
            if (trailParent != null)
                Destroy(trailParent);
            trailRenderers.Clear();
            trailParent = null;

            if (activePlanLine != null)
            {
                Destroy(activePlanLine.gameObject);
                activePlanLine = null;
            }
        }

        void OnGUI()
        {
            if (!isRewinding) return;

            float barWidth = Screen.width - 40f;
            float barY = Screen.height - 70f;

            GUI.Box(new Rect(15, barY - 5, barWidth + 10, 65), "");

            float newT = GUI.HorizontalSlider(
                new Rect(20, barY, barWidth, 20),
                NormalizedTime, 0f, 1f);

            if (Mathf.Abs(newT - NormalizedTime) > 0.001f)
                SetNormalizedTime(newT);

            float labelY = barY + 24f;
            float elapsed = currentTime - RecStartTime;
            string timeStr = $"{elapsed:F1}s / {RecDuration:F1}s";
            string speedStr = $"{playbackSpeed:F2}x";
            string playStr = isPlaying ? "||" : ">";
            string perspStr = perspectiveMode.ToString();
            string trailStr = showTrails ? "Trails:ON" : "Trails:OFF";
            string controlsStr = perspectiveMode == PerspectiveMode.FreeCam
                ? "MMB:Look  WASD:Move  Q/E:Up/Down  Wheel:Zoom  Shift:Fast  Esc:Exit"
                : "Space:Play  Left/Right:Step  +/-:Speed  F1-F5:View  G:Trails  Esc:Exit";

            GUI.Label(new Rect(20, labelY, barWidth, 20),
                $"{playStr} {timeStr}  Speed:{speedStr}  [{perspStr}]  {trailStr}");
            GUI.Label(new Rect(20, labelY + 16, barWidth, 20),
                controlsStr);

            if (showSignalReplayStatus)
                DrawSignalReplayStatus();
        }

        private void DrawSignalReplayStatus()
        {
            bool lightingActive = false;
            if (reviewSignalLightController != null)
            {
                // Reflect the rewind-applied state through current annotation time window rather than live controller internals.
                lightingActive = HasActiveLightingAnnotation();
            }

            bool vlmActive = !string.IsNullOrEmpty(activeVlmReplayLabel);
            if (!lightingActive && !vlmActive)
                return;

            float width = 330f;
            float height = vlmActive ? 68f : 48f;
            Rect rect = new Rect(Screen.width - width - 20f, Screen.height - 150f, width, height);
            GUI.Box(rect, "");

            string lightingText = lightingActive ? "LightingAnnotation: ACTIVE" : "LightingAnnotation: idle";
            GUI.Label(new Rect(rect.x + 10f, rect.y + 8f, rect.width - 20f, 20f), lightingText);

            string vlmText = vlmActive
                ? $"VLMAnnotation: {activeVlmReplayLabel}"
                : "VLMAnnotation: idle";
            GUI.Label(new Rect(rect.x + 10f, rect.y + 28f, rect.width - 20f, 20f), vlmText);

            if (vlmActive)
                GUI.Label(new Rect(rect.x + 10f, rect.y + 46f, rect.width - 20f, 18f), $"Age: {activeVlmReplayAge:F1}s");
        }

        private bool HasActiveLightingAnnotation()
        {
            if (signalAnnotations == null)
                return false;

            for (int i = 0; i < signalAnnotations.Count; i++)
            {
                SignalAnnotation annotation = signalAnnotations[i];
                if (annotation.type != SignalAnnotationType.LightingLeft &&
                    annotation.type != SignalAnnotationType.LightingRight &&
                    annotation.type != SignalAnnotationType.LightingBoth)
                {
                    continue;
                }

                float age = currentTime - annotation.timestamp;
                if (age >= 0f && age <= lightingReplayHoldSeconds)
                    return true;
            }

            return false;
        }
    }
}
