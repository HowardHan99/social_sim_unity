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
}
