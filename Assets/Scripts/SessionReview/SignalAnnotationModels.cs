using System;
using System.Collections.Generic;
using UnityEngine;

namespace SessionReview
{
    [Serializable]
    public enum SignalAnnotationType
    {
        Unknown,
        VlmCapture,
        LightingLeft,
        LightingRight,
        LightingBoth
    }

    [Serializable]
    public class SignalAnnotation
    {
        public float timestamp;
        public string agentId;
        public SignalAnnotationType type;
        public Vector3 position;
        public Quaternion rotation;
        public string label;
        public string metadata;
    }

    [Serializable]
    public class SignalAnnotationRecording
    {
        public List<SignalAnnotation> annotations = new List<SignalAnnotation>();
    }

    /// <summary>
    /// One signal a REVIEWER sent while replaying a trial ("from the robot's seat, I
    /// would have signalled here"), as opposed to the SignalAnnotations the robot player
    /// actually sent during the live run. Kept in its own file (review_signals.json)
    /// so analysis never mixes post-hoc annotations with what really happened.
    /// </summary>
    [Serializable]
    public class ReviewSignalEvent
    {
        /// <summary>Recording-relative replay time — the same clock as SignalAnnotation.timestamp.</summary>
        public float replayTime;
        /// <summary>Seconds from the trial's start, i.e. the position on the review scrubber.</summary>
        public float trialElapsed;
        /// <summary>"Lighting" or "Voice".</summary>
        public string channel;
        public SignalAnnotationType type;
        public string message;
        public string agentId;
        public Vector3 position;
        public Quaternion rotation;
        /// <summary>Review camera the reviewer was looking through when they sent it.</summary>
        public string perspective;
        /// <summary>Wall clock of the review action itself (not of the replayed moment).</summary>
        public string sentAtLocal;
    }

    [Serializable]
    public class ReviewSignalRecording
    {
        public string trialName;
        public int trialNumber;
        public string sessionId;
        public string savedAtLocal;
        public List<ReviewSignalEvent> events = new List<ReviewSignalEvent>();
    }
}
