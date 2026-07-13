using System;
using System.Collections.Generic;
using System.IO;
using Rerun;
using UnityEngine;

namespace SessionReview
{
    /// <summary>Lightweight listing entry for one saved trial folder (no trajectory data loaded yet).</summary>
    public class SavedTrialInfo
    {
        public string folderPath;
        public string folderName;
        public ushort trialNumber;
        public string trialName;
        public string sceneName;
        public float durationSeconds;
        public DateTime savedAt;
    }

    /// <summary>A fully loaded saved trial, ready to hand to the review pipeline.</summary>
    public class SavedTrialSession
    {
        public SavedTrialInfo info;
        public TrialRecord trial;
        public StateRecording recording;
        public List<PlanPathSnapshot> planSnapshots = new List<PlanPathSnapshot>();
        public List<VLMCaptureEvent> vlmCaptures = new List<VLMCaptureEvent>();
        public List<SignalAnnotation> signalAnnotations = new List<SignalAnnotation>();
        // Equivalent of the original session's recordingStartTime, recovered from the data
        // (see Load): subtract from trial Time.time values to get recording timestamps.
        public float timeOffset;
    }

    /// <summary>
    /// Reads the per-trial folders TrialDataArchive writes under SessionLogs/ back into
    /// review-ready data, so a trial can be replayed after Unity was closed and reopened.
    /// </summary>
    public static class SavedTrialLoader
    {
        public static List<SavedTrialInfo> ListSavedTrials()
        {
            var result = new List<SavedTrialInfo>();
            string root = TrialDataArchive.LogFolder;
            if (!Directory.Exists(root))
                return result;

            foreach (string folder in Directory.GetDirectories(root))
            {
                string infoPath = Path.Combine(folder, "trial_info.json");
                string trajPath = Path.Combine(folder, "trajectories_all.json");
                if (!File.Exists(infoPath) || !File.Exists(trajPath))
                    continue;

                try
                {
                    var trial = JsonUtility.FromJson<TrialRecord>(File.ReadAllText(infoPath));
                    if (trial == null)
                        continue;

                    result.Add(new SavedTrialInfo
                    {
                        folderPath = folder,
                        folderName = Path.GetFileName(folder),
                        trialNumber = trial.trialNumber,
                        trialName = trial.trialName,
                        sceneName = trial.sceneName,
                        durationSeconds = trial.Duration,
                        savedAt = Directory.GetLastWriteTime(folder)
                    });
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[SessionReview] Skipping unreadable trial folder \"{folder}\": {ex.Message}");
                }
            }

            result.Sort((a, b) => b.savedAt.CompareTo(a.savedAt));
            return result;
        }

        public static SavedTrialSession Load(string folderPath)
        {
            string infoPath = Path.Combine(folderPath, "trial_info.json");
            string trajPath = Path.Combine(folderPath, "trajectories_all.json");
            if (!File.Exists(infoPath) || !File.Exists(trajPath))
            {
                Debug.LogError($"[SessionReview] Cannot load replay: missing trial_info.json or trajectories_all.json in {folderPath}");
                return null;
            }

            var trial = JsonUtility.FromJson<TrialRecord>(File.ReadAllText(infoPath));
            var recording = JsonUtility.FromJson<StateRecording>(File.ReadAllText(trajPath));
            if (trial == null || recording == null || recording.timelines == null || recording.timelines.Count == 0)
            {
                Debug.LogError($"[SessionReview] Cannot load replay: empty trial record or recording in {folderPath}");
                return null;
            }
            recording.BuildCache();

            var session = new SavedTrialSession
            {
                trial = trial,
                recording = recording,
                vlmCaptures = trial.vlmCaptures ?? new List<VLMCaptureEvent>(),
                signalAnnotations = trial.signalAnnotations ?? new List<SignalAnnotation>()
            };

            string planPath = Path.Combine(folderPath, "plan_paths.json");
            if (File.Exists(planPath))
            {
                var plans = JsonUtility.FromJson<PlanPathRecording>(File.ReadAllText(planPath));
                if (plans != null && plans.snapshots != null)
                    session.planSnapshots = plans.snapshots;
            }

            string vlmPath = Path.Combine(folderPath, "vlm_captures.json");
            if (File.Exists(vlmPath))
            {
                var vlm = JsonUtility.FromJson<VLMCaptureRecording>(File.ReadAllText(vlmPath));
                if (vlm != null && vlm.events != null)
                    session.vlmCaptures = vlm.events;
            }

            string annotationPath = Path.Combine(folderPath, "signal_annotations.json");
            if (File.Exists(annotationPath))
            {
                var annotations = JsonUtility.FromJson<SignalAnnotationRecording>(File.ReadAllText(annotationPath));
                if (annotations != null && annotations.annotations != null)
                    session.signalAnnotations = annotations.annotations;
            }

            // Timestamps inside the files are relative to the original session's recording
            // start, which was never written to disk. Recover the offset from the earliest
            // saved sample: everything on disk was filtered to the trial window, so the
            // earliest sample sits within one sample interval of the trial start.
            float earliest = float.MaxValue;
            foreach (var timeline in recording.timelines)
            {
                if (timeline != null && timeline.states != null && timeline.states.Count > 0)
                    earliest = Mathf.Min(earliest, timeline.states[0].timestamp);
            }
            foreach (var snap in session.planSnapshots)
                earliest = Mathf.Min(earliest, snap.timestamp);
            foreach (var evt in session.vlmCaptures)
                earliest = Mathf.Min(earliest, evt.timestamp);
            foreach (var annotation in session.signalAnnotations)
                earliest = Mathf.Min(earliest, annotation.timestamp);
            if (earliest == float.MaxValue)
                earliest = 0f;

            session.timeOffset = trial.startTime - earliest;

            SessionReview.SessionReviewLog.Log(
                $"[SessionReview] Loaded saved trial \"{trial.trialName}\" from {folderPath} " +
                $"({recording.timelines.Count} timelines, {session.planSnapshots.Count} plan snapshots).");
            return session;
        }
    }
}
