using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Rerun;

namespace SessionReview
{
    /// <summary>
    /// Self-contained trajectory recorder that samples all registered agents at 10 Hz.
    /// Agents are registered explicitly via TrackAgent() -- typically called by SessionTracker
    /// when it discovers agents at trial start (the right moment in the lifecycle).
    /// </summary>
    public class LiveTrajectoryRecorder : MonoBehaviour
    {
        [SerializeField] private float sampleRate = 10f;

        private float sampleInterval;
        private float lastSampleTime;
        private float recordingStartTime;

        private Dictionary<string, ObjectStateTimeline> timelines = new Dictionary<string, ObjectStateTimeline>();
        private Dictionary<string, Transform> trackedTransforms = new Dictionary<string, Transform>();
        private bool isRecording;

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
        }
    }
}
