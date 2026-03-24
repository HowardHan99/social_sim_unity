using System.Collections.Generic;
using UnityEngine;
using Rerun;

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
        [Header("References")]
        [SerializeField] private float topDownHeight = 50f;

        [Header("Pedestrian Over-Shoulder")]
        [SerializeField] private Vector3 overShoulderOffset = new Vector3(-0.5f, 2.0f, -2.0f);

        [Header("PWD Camera")]
        [SerializeField] private float pwdEyeHeight = 1.2f;

        [Header("Trail Overlay")]
        [SerializeField] private float trailLineWidth = 0.05f;

        private LiveTrajectoryRecorder liveRecorder;

        private TrialRecord currentTrial;
        private StateRecording currentRecording;
        private ControlModeLog controlModeLog;

        private bool isRewinding;
        private float currentTime;
        private float playbackSpeed = 1f;
        private bool isPlaying;

        // Offset to convert trial Time.time -> recording-relative timestamps
        private float timeOffset;

        private PerspectiveMode perspectiveMode = PerspectiveMode.TopDown;
        private string selectedPedestrianId;

        private Camera rewindCamera;
        private Camera robotFirstPersonCam;
        private Camera pwdFirstPersonCam;
        private Vector3 savedCameraPos;
        private Quaternion savedCameraRot;
        private Camera savedMainCamera;

        private GameObject trailParent;
        private Dictionary<string, LineRenderer> trailRenderers = new Dictionary<string, LineRenderer>();
        private bool showTrails = true;

        private MultiAgentTrajectoryRenderer trajectoryRenderer;

        [Header("Plan Path")]
        [SerializeField] private Color activePlanColor = new Color(0.2f, 1f, 0.3f, 0.9f);
        [SerializeField] private float activePlanWidth = 0.14f;
        private LineRenderer activePlanLine;

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
        }

        public void EnterRewind(TrialRecord trial, StateRecording recording,
            ControlModeLog modeLog, MultiAgentTrajectoryRenderer trajRenderer, float recordingTimeOffset = 0f)
        {
            if (trial == null || recording == null) return;

            currentTrial = trial;
            currentRecording = recording;
            controlModeLog = modeLog;
            trajectoryRenderer = trajRenderer;
            timeOffset = recordingTimeOffset;

            if (currentRecording.timelineDict == null)
                currentRecording.BuildCache();

            liveRecorder = FindObjectOfType<LiveTrajectoryRecorder>();

            savedMainCamera = Camera.main;
            if (savedMainCamera != null)
            {
                savedCameraPos = savedMainCamera.transform.position;
                savedCameraRot = savedMainCamera.transform.rotation;
            }

            FindAgentCameras();

            currentTime = RecStartTime;
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

            DisableAllRewindCameras();

            if (savedMainCamera != null)
            {
                savedMainCamera.transform.position = savedCameraPos;
                savedMainCamera.transform.rotation = savedCameraRot;
                savedMainCamera.enabled = true;
            }

            ClearTrails();
            transformCache.Clear();
            currentTrial = null;
            currentRecording = null;
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
                    transformCache[objectId] = mwc.transform;
                    return mwc.transform;
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
                robotFirstPersonCam.enabled = true;
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
                pwdFirstPersonCam.enabled = true;
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

        private void ActivateTopDown()
        {
            Vector3 center = ComputeSceneCenter();
            rewindCamera.transform.position = new Vector3(center.x, center.y + topDownHeight, center.z);
            rewindCamera.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            rewindCamera.orthographic = true;
            rewindCamera.orthographicSize = topDownHeight * 0.5f;
            rewindCamera.enabled = true;
        }

        private void ActivateFreeCam()
        {
            Vector3 center = ComputeSceneCenter();
            rewindCamera.transform.position = center + Vector3.up * 10f + Vector3.back * 10f;
            rewindCamera.transform.LookAt(center);
            rewindCamera.orthographic = false;
            rewindCamera.enabled = true;
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

            GUI.Label(new Rect(20, labelY, barWidth, 20),
                $"{playStr} {timeStr}  Speed:{speedStr}  [{perspStr}]  {trailStr}");
            GUI.Label(new Rect(20, labelY + 16, barWidth, 20),
                "Space:Play  Left/Right:Step  +/-:Speed  F1-F5:View  G:Trails  Esc:Exit");
        }
    }
}
