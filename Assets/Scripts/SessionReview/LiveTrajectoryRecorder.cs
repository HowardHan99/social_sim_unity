using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Rerun;

namespace SessionReview
{
    [System.Serializable]
    public class PlanPathSnapshot
    {
        public float timestamp;
        public Vector3[] positions;
    }

    [System.Serializable]
    public class PlanPathRecording
    {
        public List<PlanPathSnapshot> snapshots = new List<PlanPathSnapshot>();
    }

    /// <summary>
    /// Self-contained trajectory recorder that samples all registered agents at 10 Hz.
    /// Agents are registered explicitly via TrackAgent() -- typically called by SessionTracker
    /// when it discovers agents at trial start (the right moment in the lifecycle).
    /// Also records the ROS planned navigation path from PlanVisualizer for session review.
    /// </summary>
    public class LiveTrajectoryRecorder : MonoBehaviour
    {
        [SerializeField] private float sampleRate = 10f;
        [SerializeField] private bool freezePlanUpdatesDuringRobotManual = true;

        private float sampleInterval;
        private float lastSampleTime;
        private float recordingStartTime;

        private Dictionary<string, ObjectStateTimeline> timelines = new Dictionary<string, ObjectStateTimeline>();
        private Dictionary<string, Transform> trackedTransforms = new Dictionary<string, Transform>();
        private bool isRecording;

        private SEAN.Display.PlanVisualizer planVisualizer;
        private SEAN.Control.VelocityController robotVelocityController;
        private List<PlanPathSnapshot> planSnapshots = new List<PlanPathSnapshot>();
        private Vector3[] lastRecordedPlan;

        private List<VLMCaptureEvent> vlmCaptures = new List<VLMCaptureEvent>();
        private List<SignalAnnotation> signalAnnotations = new List<SignalAnnotation>();

        public float RecordingStartTime => recordingStartTime;
        public int TrackedCount => trackedTransforms.Count;
        public bool IsRecording => isRecording;

        void Awake()
        {
            sampleInterval = 1f / sampleRate;
        }

        void Start()
        {
            recordingStartTime = Time.time;
            lastSampleTime = recordingStartTime;
            isRecording = true;
            planVisualizer = FindObjectOfType<SEAN.Display.PlanVisualizer>();
            robotVelocityController = FindObjectOfType<SEAN.Control.VelocityController>();
        }

        void Update()
        {
            if (!isRecording) return;

            if (Time.time - lastSampleTime >= sampleInterval)
            {
                SampleAll();
                lastSampleTime = Time.time;
            }
        }

        /// <summary>
        /// Register an agent to be tracked. Call this whenever a new agent is discovered.
        /// Duplicate IDs are ignored. Safe to call multiple times.
        /// </summary>
        public void TrackAgent(string id, Transform t)
        {
            if (string.IsNullOrEmpty(id) || t == null) return;
            if (trackedTransforms.ContainsKey(id)) return;

            trackedTransforms[id] = t;
            if (!timelines.ContainsKey(id))
                timelines[id] = new ObjectStateTimeline { objectId = id, states = new List<ObjectState>() };

            Debug.Log($"[SessionReview] Now tracking: \"{id}\" ({t.gameObject.name})");
        }

        private void SampleAll()
        {
            float timestamp = Time.time - recordingStartTime;

            foreach (var kvp in trackedTransforms)
            {
                if (kvp.Value == null) continue;
                timelines[kvp.Key].states.Add(new ObjectState
                {
                    objectId = kvp.Key,
                    timestamp = timestamp,
                    position = kvp.Value.position,
                    rotation = kvp.Value.rotation,
                    scale = kvp.Value.localScale,
                    properties = new List<SerializedProperty>()
                });
            }

            SamplePlanPath(timestamp);
        }

        private void SamplePlanPath(float timestamp)
        {
            if (planVisualizer == null)
                planVisualizer = FindObjectOfType<SEAN.Display.PlanVisualizer>();
            if (planVisualizer == null) return;

            Vector3[] plan = planVisualizer.GetCurrentPlanPositions();
            if (plan == null || plan.Length == 0) return;

            if (freezePlanUpdatesDuringRobotManual &&
                planSnapshots.Count > 0 &&
                IsRobotManualControlActive())
            {
                return;
            }

            if (lastRecordedPlan != null && plan.Length == lastRecordedPlan.Length)
            {
                bool same = true;
                for (int i = 0; i < plan.Length; i++)
                {
                    if (Vector3.SqrMagnitude(plan[i] - lastRecordedPlan[i]) > 0.01f)
                    { same = false; break; }
                }
                if (same) return;
            }

            lastRecordedPlan = plan;
            planSnapshots.Add(new PlanPathSnapshot
            {
                timestamp = timestamp,
                positions = plan
            });
        }

        private bool IsRobotManualControlActive()
        {
            if (robotVelocityController == null)
                robotVelocityController = FindObjectOfType<SEAN.Control.VelocityController>();

            if (robotVelocityController != null)
                return robotVelocityController.ManualControlActive;

            return SessionOnboardingSettings.HasCompletedOnboarding &&
                   SessionOnboardingSettings.RobotStartupControl == StartupControlMode.Manual;
        }

        public StateRecording BuildSnapshot()
        {
            var recording = new StateRecording
            {
                totalDuration = Time.time - recordingStartTime,
                timelines = new List<ObjectStateTimeline>(timelines.Values)
            };
            recording.BuildCache();
            return recording;
        }

        /// <summary>
        /// Returns plan path snapshots within the given recording-relative time window.
        /// </summary>
        public List<PlanPathSnapshot> GetPlanSnapshots(float recStart, float recEnd)
        {
            var result = new List<PlanPathSnapshot>();
            foreach (var snap in planSnapshots)
            {
                if (snap.timestamp >= recStart && snap.timestamp <= recEnd)
                    result.Add(snap);
            }
            return result;
        }

        /// <summary>
        /// Returns the plan path active at the given recording-relative time.
        /// </summary>
        public Vector3[] GetPlanAtTime(float time)
        {
            Vector3[] last = null;
            foreach (var snap in planSnapshots)
            {
                if (snap.timestamp > time) break;
                last = snap.positions;
            }
            return last;
        }

        public void RecordVLMCapture(string agentId, Vector3 position, Quaternion rotation)
        {
            float timestamp = Time.time - recordingStartTime;
            vlmCaptures.Add(new VLMCaptureEvent
            {
                timestamp = timestamp,
                position = position,
                rotation = rotation,
                agentId = agentId
            });
            RecordSignalAnnotation(new SignalAnnotation
            {
                timestamp = timestamp,
                agentId = agentId,
                type = SignalAnnotationType.VlmCapture,
                position = position,
                rotation = rotation,
                label = "VLM Capture",
                metadata = string.Empty
            });
            Debug.Log($"[SessionReview] VLMAnnotation recorded at t={timestamp:F2}, pos={position}, agent={agentId}");
        }

        public void RecordSignalAnnotation(SignalAnnotation annotation)
        {
            if (annotation == null)
                return;

            if (annotation.timestamp < 0f)
                annotation.timestamp = Time.time - recordingStartTime;

            signalAnnotations.Add(annotation);

            string annotationName = GetAnnotationLogName(annotation.type);
            Debug.Log($"[SessionReview] {annotationName} recorded at t={annotation.timestamp:F2}, pos={annotation.position}, agent={annotation.agentId}");
        }

        public void AttachMetadataToLatestVlmAnnotation(string agentId, string label, string metadata)
        {
            if (signalAnnotations == null || signalAnnotations.Count == 0)
                return;

            for (int i = signalAnnotations.Count - 1; i >= 0; i--)
            {
                SignalAnnotation annotation = signalAnnotations[i];
                if (annotation == null || annotation.type != SignalAnnotationType.VlmCapture)
                    continue;

                if (!string.IsNullOrEmpty(agentId) && annotation.agentId != agentId)
                    continue;

                if (!string.IsNullOrWhiteSpace(label))
                    annotation.label = label;
                annotation.metadata = metadata ?? string.Empty;
                Debug.Log($"[SessionReview] VLMAnnotation updated with replay metadata for agent={annotation.agentId}");
                return;
            }
        }

        private static string GetAnnotationLogName(SignalAnnotationType type)
        {
            switch (type)
            {
                case SignalAnnotationType.VlmCapture:
                    return "VLMAnnotation";
                case SignalAnnotationType.LightingLeft:
                case SignalAnnotationType.LightingRight:
                case SignalAnnotationType.LightingBoth:
                    return "LightingAnnotation";
                default:
                    return "SignalAnnotation";
            }
        }

        public List<VLMCaptureEvent> GetVLMCaptures(float recStart, float recEnd)
        {
            var result = new List<VLMCaptureEvent>();
            foreach (var evt in vlmCaptures)
            {
                if (evt.timestamp >= recStart && evt.timestamp <= recEnd)
                    result.Add(evt);
            }
            return result;
        }

        public List<SignalAnnotation> GetSignalAnnotations(float recStart, float recEnd)
        {
            var result = new List<SignalAnnotation>();
            foreach (var annotation in signalAnnotations)
            {
                if (annotation.timestamp >= recStart && annotation.timestamp <= recEnd)
                    result.Add(annotation);
            }
            return result;
        }

        public Dictionary<string, ObjectState> GetStateAtTime(float time)
        {
            var result = new Dictionary<string, ObjectState>();
            foreach (var timeline in timelines.Values)
            {
                if (timeline.states.Count == 0) continue;
                ObjectState state = Interpolate(timeline, time);
                if (state != null)
                    result[timeline.objectId] = state;
            }
            return result;
        }

        private ObjectState Interpolate(ObjectStateTimeline timeline, float time)
        {
            var states = timeline.states;
            if (states.Count == 0) return null;
            if (time <= states[0].timestamp) return states[0];
            if (time >= states[states.Count - 1].timestamp) return states[states.Count - 1];

            int lo = 0, hi = states.Count - 1;
            while (lo < hi - 1)
            {
                int mid = (lo + hi) / 2;
                if (states[mid].timestamp <= time) lo = mid;
                else hi = mid;
            }

            var a = states[lo];
            var b = states[hi];
            float t = (time - a.timestamp) / Mathf.Max(0.001f, b.timestamp - a.timestamp);

            return new ObjectState
            {
                objectId = a.objectId,
                timestamp = time,
                position = Vector3.Lerp(a.position, b.position, t),
                rotation = Quaternion.Slerp(a.rotation, b.rotation, t),
                scale = Vector3.Lerp(a.scale, b.scale, t),
                properties = new List<SerializedProperty>()
            };
        }

        /// <summary>
        /// Export trajectory data for a specific trial, saving both a combined file
        /// and per-agent files for easy inspection.
        /// </summary>
        public void SaveTrialTrajectories(string trialFolder, TrialRecord trial)
        {
            if (timelines.Count == 0) return;

            if (!Directory.Exists(trialFolder))
                Directory.CreateDirectory(trialFolder);

            float recStart = trial.startTime - recordingStartTime;
            float recEnd = trial.endTime - recordingStartTime;

            var trialTimelines = new List<ObjectStateTimeline>();
            var roleMap = new Dictionary<string, AgentRole>();
            foreach (var r in trial.agentRoles)
                roleMap[r.objectId] = r.role;

            foreach (var kvp in timelines)
            {
                string agentId = kvp.Key;
                var src = kvp.Value;
                var filtered = new ObjectStateTimeline
                {
                    objectId = agentId,
                    states = new List<ObjectState>()
                };

                foreach (var s in src.states)
                {
                    if (s.timestamp >= recStart && s.timestamp <= recEnd)
                        filtered.states.Add(s);
                }

                if (filtered.states.Count == 0) continue;

                trialTimelines.Add(filtered);

                string roleSuffix = roleMap.ContainsKey(agentId)
                    ? roleMap[agentId].ToString().ToLower()
                    : "unknown";
                string safeName = agentId.Replace("/", "_").Replace("\\", "_").Replace(" ", "_");
                string agentFile = Path.Combine(trialFolder, $"trajectory_{safeName}_{roleSuffix}.json");

                var singleRec = new StateRecording
                {
                    totalDuration = recEnd - recStart,
                    timelines = new List<ObjectStateTimeline> { filtered }
                };
                File.WriteAllText(agentFile, JsonUtility.ToJson(singleRec, true));
            }

            var combined = new StateRecording
            {
                totalDuration = recEnd - recStart,
                timelines = trialTimelines
            };
            string combinedPath = Path.Combine(trialFolder, "trajectories_all.json");
            File.WriteAllText(combinedPath, JsonUtility.ToJson(combined, true));

            Debug.Log($"[SessionReview] Saved {trialTimelines.Count} agent trajectories to: {trialFolder}");

            var trialPlans = GetPlanSnapshots(recStart, recEnd);
            if (trialPlans.Count > 0)
            {
                var planData = new PlanPathRecording { snapshots = trialPlans };
                string planFile = Path.Combine(trialFolder, "plan_paths.json");
                File.WriteAllText(planFile, JsonUtility.ToJson(planData, true));
                Debug.Log($"[SessionReview] Saved {trialPlans.Count} plan path snapshots to: {planFile}");
            }

            var trialVLM = GetVLMCaptures(recStart, recEnd);
            if (trialVLM.Count > 0)
            {
                var vlmData = new VLMCaptureRecording { events = trialVLM };
                string vlmFile = Path.Combine(trialFolder, "vlm_captures.json");
                File.WriteAllText(vlmFile, JsonUtility.ToJson(vlmData, true));
                Debug.Log($"[SessionReview] Saved {trialVLM.Count} VLM capture events to: {vlmFile}");
            }

            var trialAnnotations = GetSignalAnnotations(recStart, recEnd);
            if (trialAnnotations.Count > 0)
            {
                var annotationData = new SignalAnnotationRecording { annotations = trialAnnotations };
                string annotationFile = Path.Combine(trialFolder, "signal_annotations.json");
                File.WriteAllText(annotationFile, JsonUtility.ToJson(annotationData, true));
                Debug.Log($"[SessionReview] Saved {trialAnnotations.Count} signal annotations to: {annotationFile}");
            }
        }
    }
}
