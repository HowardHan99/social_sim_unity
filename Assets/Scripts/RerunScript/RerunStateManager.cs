using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UltimateReplay;
using System.Linq; // Added for Distinct()

namespace Rerun
{
    /// <summary>
    /// Manager for the custom state recording and playback system
    /// </summary>
    [System.Serializable]
    public class RerunStateManager : MonoBehaviour
    {
        // Static instance for global access
        public static RerunStateManager Instance { get; private set; }

        [SerializeField]
        private RerunManager rerunManager;
        
        [SerializeField]
        [Tooltip("List of objects tracked for state recording/playback - can be populated by self-registration or manual assignment")]
        public List<TrackedObject> trackedObjects = new List<TrackedObject>();
        
        [SerializeField]
        private float samplingRate = 0.1f; // Record 10 times per second
        
        [SerializeField]
        private string stateFileExtension = ".rerunstate";
        
        [SerializeField]
        private bool enableDetailedLogging = false; // Disabled by default to reduce log spam
        
        // State recording
        private bool isRecording = false;
        private float recordingStartTime = 0f;
        private float lastSampleTime = 0f;
        
        // State data - lazily initialized
        [SerializeField]
        [Tooltip("Timeline data for each tracked object")]
        private List<ObjectStateTimeline> serializedObjectTimelines = null;
        
        // Use lazy initialization for dictionaries to reduce memory pressure
        private Dictionary<string, ObjectStateTimeline> objectTimelines = null;
        
        [SerializeField]
        private StateRecording serializedRecording = null;
        private StateRecording currentRecording = null;
        
        // Mapping between objects - lazily initialized
        private Dictionary<string, GameObject> sourceObjects = null;
        private Dictionary<string, GameObject> cloneObjects = null;
        
        // Lazy initialization of collections
        private Dictionary<string, ObjectStateTimeline> ObjectTimelines 
        {
            get 
            {
                if (objectTimelines == null)
                    objectTimelines = new Dictionary<string, ObjectStateTimeline>();
                return objectTimelines;
            }
        }
        
        private Dictionary<string, GameObject> SourceObjects
        {
            get
            {
                if (sourceObjects == null)
                    sourceObjects = new Dictionary<string, GameObject>();
                return sourceObjects;
            }
        }
        
        private Dictionary<string, GameObject> CloneObjects
        {
            get
            {
                if (cloneObjects == null)
                    cloneObjects = new Dictionary<string, GameObject>();
                return cloneObjects;
            }
        }
        
        void Awake()
        {
            // Singleton pattern
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            // DontDestroyOnLoad(gameObject); // Consider if you need this manager to persist across scene loads

            // Disable detailed logging by default to reduce log spam
            // enableDetailedLogging = false; // Commented out to respect Inspector setting
        }
        
        void Start()
        {
            if (rerunManager == null)
            {
                rerunManager = GetComponent<RerunManager>();
                if (rerunManager == null)
                {
                    enabled = false;
                    return;
                }
            }
            
            // Auto-create TrackedObject components on ReplayObjects if they don't have one
            // This can be helpful but make sure it doesn't conflict with manual setup
            // AutoAddTrackedObjectComponents(); // Consider if this is still needed with self-registration
            
            if (serializedObjectTimelines == null) // Initialize if null
                serializedObjectTimelines = new List<ObjectStateTimeline>();
            
            // Initial scan for objects that might have been in the scene before this manager awakes,
            // or if self-registration failed for some TrackedObjects.
            // Self-registration via TrackedObject.OnEnable is now the primary method for dynamic objects.
            FindTrackedObjects(true); // Pass true to append to existing (e.g. Inspector-assigned or early self-registered)

            if (trackedObjects.Count > 0)
            {
                BuildObjectMappings();
            }
            
            LoadSerializedData(); // Load any persistent state data
        }
        
        /// <summary>
        /// Registers a TrackedObject with the manager.
        /// Called by TrackedObject itself in OnEnable.
        /// </summary>
        public void RegisterTrackedObject(TrackedObject newTrackedObject)
        {
            if (newTrackedObject == null || string.IsNullOrEmpty(newTrackedObject.objectId))
            {
                return;
            }

            if (!trackedObjects.Any(to => to.objectId == newTrackedObject.objectId))
            {
                trackedObjects.Add(newTrackedObject);
                // If a recording is in progress, we might need to initialize a timeline for it.
                if (isRecording && ObjectTimelines != null && !ObjectTimelines.ContainsKey(newTrackedObject.objectId))
                {
                    ObjectTimelines[newTrackedObject.objectId] = new ObjectStateTimeline { objectId = newTrackedObject.objectId, states = new List<ObjectState>() };
                }
                // Rebuild mappings if needed, or defer until next state capture/application
                BuildObjectMappings(); // Or a more lightweight update if possible
            }
            else
            {
                // Optional: Handle cases where an object with the same ID tries to register again.
                // This could happen if OnEnable is called multiple times without OnDisable, or if IDs are not unique.
                // Ensure the reference is updated if it somehow changed (e.g. prefab re-instantiation with same persistent ID)
                int index = trackedObjects.FindIndex(to => to.objectId == newTrackedObject.objectId);
                if (index != -1 && trackedObjects[index] != newTrackedObject)
                {
                    trackedObjects[index] = newTrackedObject; 
                }
            }
        }

        /// <summary>
        /// Unregisters a TrackedObject from the manager.
        /// Called by TrackedObject itself in OnDisable.
        /// </summary>
        public void UnregisterTrackedObject(TrackedObject objectToRemove)
        {
            if (objectToRemove == null)
            {
                return;
            }

            // Remove by instance first, then by ID if instance not found (safer for general case)
            bool removed = trackedObjects.Remove(objectToRemove);
            if (!removed)
            {
                // Fallback: Try removing by ID if the instance wasn't directly in the list (e.g., if it was a copy with same ID)
                trackedObjects.RemoveAll(to => to != null && to.objectId == objectToRemove.objectId);
            }

            // Timeline data is kept even after unregistration as the object might re-appear
            // Rebuild mappings if needed
            // BuildObjectMappings(); // Could be deferred
        }
        
        /// <summary>
        /// Find all objects with TrackedObject component. Can append to or replace the existing list.
        /// </summary>
        public void FindTrackedObjects(bool append = false)
        {
            if (!append)
            {
                trackedObjects.Clear();
            }
            
            TrackedObject[] allFoundInScene = FindObjectsOfType<TrackedObject>(true); // true to include inactive

            foreach (var objInScene in allFoundInScene)
            {
                if (objInScene != null)
                {
                    // Add if not already present (based on instance or unique ID)
                    if (!trackedObjects.Any(to => to == objInScene || (to != null && to.objectId == objInScene.objectId)))
                    {
                        trackedObjects.Add(objInScene);
                    }
                }
            }
            // Ensure uniqueness again if append was true and might have introduced duplicates from inspector + scene scan
            if(append) trackedObjects = trackedObjects.Where(to => to != null).Distinct().ToList();
        }
        
        /// <summary>
        /// Auto-adds TrackedObject components to all ReplayObjects in the scene
        /// </summary>
        public void AutoAddTrackedObjectComponents()
        {
            // Find all ReplayObjects in the scene
            ReplayObject[] allReplayObjects = FindObjectsOfType<ReplayObject>();
            int addedCount = 0;
            
            foreach (ReplayObject replayObj in allReplayObjects)
            {
                // Skip null objects
                if (replayObj == null) continue;
                
                // Check if it already has a TrackedObject component
                TrackedObject existing = replayObj.GetComponent<TrackedObject>();
                if (existing == null)
                {
                    // Add TrackedObject component
                    TrackedObject newTracked = replayObj.gameObject.AddComponent<TrackedObject>();
                    
                    // Set up the reference to the ReplayObject
                    newTracked.replayObject = replayObj;
                    
                    // Generate a unique ID if needed
                    if (string.IsNullOrEmpty(newTracked.objectId))
                    {
                        // This will trigger the ID generation in Awake, but just in case:
                        string path = GetObjectHierarchyPath(replayObj.gameObject);
                        newTracked.objectId = $"{replayObj.gameObject.name}_{path.GetHashCode():X8}";
                    }
                    
                    addedCount++;
                }
            }
        }
        
        /// <summary>
        /// Load the serialized data into the runtime dictionary
        /// </summary>
        private void LoadSerializedData()
        {
            // Setup the objectTimelines dictionary from serialized data
            if (serializedObjectTimelines != null)
            {
                ObjectTimelines.Clear(); // Clear previous runtime data
                foreach (var timeline in serializedObjectTimelines)
                {
                    if (timeline != null && !string.IsNullOrEmpty(timeline.objectId))
                    {
                        ObjectTimelines[timeline.objectId] = timeline;
                    }
                }
            }
            else
            {
                serializedObjectTimelines = new List<ObjectStateTimeline>();
            }
            
            // Setup the current recording if available
            if (serializedRecording != null)
            {
                currentRecording = serializedRecording;
                try
                {
                    currentRecording.BuildCache(); // Essential for timelineDict
                }
                catch (System.Exception)
                {
                    currentRecording = null; 
                }
            }
            else
            {
                // Initialize currentRecording if it's null and we expect to use it
                currentRecording = new StateRecording(); 
            }
        }
        
        /// <summary>
        /// Update the serialized data from the runtime dictionary for inspector viewing
        /// </summary>
        private void UpdateSerializedData()
        {
            // Update serialized data when viewed in inspector
            if (objectTimelines != null)
            {
                // Update the serialized list from the dictionary
                if (serializedObjectTimelines == null) serializedObjectTimelines = new List<ObjectStateTimeline>();
                serializedObjectTimelines.Clear();
                foreach (var timeline in objectTimelines.Values)
                {
                    serializedObjectTimelines.Add(timeline);
                }
            }
            
            // Update the serialized recording
            // Ensure currentRecording is not null before assigning
            if (currentRecording != null) 
            {
                 serializedRecording = currentRecording;
            }
            else
            {
                // If currentRecording is null, ensure serializedRecording is also nulled or empty
                // to reflect this in the inspector, or initialized to an empty recording.
                serializedRecording = new StateRecording(); // Or null, depending on desired Inspector representation
            }
        }
        
        void Update()
        {
            // Check if we need to record state
            if (isRecording && (Time.time - lastSampleTime >= samplingRate))
            {
                RecordCurrentState();
                lastSampleTime = Time.time;
            }
        }
        
        void OnValidate()
        {
            // Update serialized data when viewed in inspector
            UpdateSerializedData();
        }
        
        
        /// <summary>
        /// Build mappings between object IDs and both source and clone GameObjects
        /// </summary>
        public void BuildObjectMappings()
        {
            SourceObjects.Clear();
            CloneObjects.Clear();
            
            // DO NOT call FindTrackedObjects directly - it creates an infinite loop
            
            // First pass - categorize objects as source or clone
            foreach (TrackedObject tracked in trackedObjects)
            {
                if (tracked == null || !tracked.gameObject.activeInHierarchy) 
                {
                    // Skip inactive objects
                    continue;
                }
                
                bool isSource = IsSourceObject(tracked.gameObject);
                
                if (isSource)
                {
                    SourceObjects[tracked.objectId] = tracked.gameObject;
                }
                else
                {
                    CloneObjects[tracked.objectId] = tracked.gameObject;
                }
            }
            
            // Second pass - attempt to match any missing pairs by path similarity if IDs don't match
            HashSet<string> unmatchedSourceIds = new HashSet<string>(SourceObjects.Keys);
            unmatchedSourceIds.ExceptWith(CloneObjects.Keys);
            
            HashSet<string> unmatchedCloneIds = new HashSet<string>(CloneObjects.Keys);
            unmatchedCloneIds.ExceptWith(SourceObjects.Keys);
            
            // If we have unmatched objects, try to match them by their path
            if (unmatchedSourceIds.Count > 0 && unmatchedCloneIds.Count > 0)
            {
                Dictionary<string, string> sourcePaths = new Dictionary<string, string>();
                Dictionary<string, string> clonePaths = new Dictionary<string, string>();
                
                // Build path dictionaries
                foreach (string sourceId in unmatchedSourceIds)
                {
                    if (SourceObjects.TryGetValue(sourceId, out GameObject sourceObj))
                    {
                        sourcePaths[sourceId] = GetObjectHierarchyPath(sourceObj);
                    }
                }
                
                foreach (string cloneId in unmatchedCloneIds)
                {
                    if (CloneObjects.TryGetValue(cloneId, out GameObject cloneObj))
                    {
                        clonePaths[cloneId] = GetObjectHierarchyPath(cloneObj);
                    }
                }
                
                // For each unmatched source, try to find the best matching clone by path
                foreach (string sourceId in unmatchedSourceIds)
                {
                    if (!sourcePaths.TryGetValue(sourceId, out string sourcePath)) continue;
                    
                    // Find best matching clone path
                    float bestMatch = 0;
                    string bestMatchingCloneId = null;
                    
                    foreach (string cloneId in unmatchedCloneIds)
                    {
                        if (!clonePaths.TryGetValue(cloneId, out string clonePath)) continue;
                        
                        // Calculate path similarity (simplified)
                        float similarity = CalculatePathSimilarity(sourcePath, clonePath);
                        if (similarity > bestMatch)
                        {
                            bestMatch = similarity;
                            bestMatchingCloneId = cloneId;
                        }
                    }
                    
                    // If we found a good match (>80% path similarity), use it
                    if (bestMatch > 0.8f && bestMatchingCloneId != null)
                    {
                        TrackedObject sourceTracked = SourceObjects[sourceId].GetComponent<TrackedObject>();
                        TrackedObject cloneTracked = CloneObjects[bestMatchingCloneId].GetComponent<TrackedObject>();
                        
                        // Update the clone's ID to match the source for future lookups
                        cloneTracked.objectId = sourceTracked.objectId;
                        
                        // Update the mapping
                        CloneObjects.Remove(bestMatchingCloneId);
                        CloneObjects[sourceTracked.objectId] = cloneTracked.gameObject;
                        
                        // Remove from unmatched set
                        unmatchedCloneIds.Remove(bestMatchingCloneId);
                    }
                }
            }
        }
        
        /// <summary>
        /// Calculate similarity between two object paths
        /// </summary>
        private float CalculatePathSimilarity(string path1, string path2)
        {
            // Simple implementation - compare the paths by their components
            string[] components1 = path1.Split('/');
            string[] components2 = path2.Split('/');
            
            int minLength = Mathf.Min(components1.Length, components2.Length);
            int maxLength = Mathf.Max(components1.Length, components2.Length);
            
            int matchCount = 0;
            for (int i = 0; i < minLength; i++)
            {
                // Compare just the name parts, ignoring any indices that might be in the path
                string name1 = components1[i].Split('_')[0];
                string name2 = components2[i].Split('_')[0];
                
                if (name1.Equals(name2, System.StringComparison.OrdinalIgnoreCase))
                {
                    matchCount++;
                }
            }
            
            return (float)matchCount / maxLength;
        }
        
        /// <summary>
        /// Gets the full hierarchy path of an object
        /// </summary>
        private string GetObjectHierarchyPath(GameObject obj)
        {
            string path = obj.name;
            Transform parent = obj.transform.parent;
            while (parent != null)
            {
                path = parent.name + "/" + path;
                parent = parent.parent;
            }
            return path;
        }
        
        /// <summary>
        /// Verifies that source and clone have the same structure
        /// </summary>
        private void VerifyObjectStructures(GameObject source, GameObject clone)
        {
            // Check if the clone has the same structure as the source
            if (source.transform.childCount != clone.transform.childCount)
            {
                return;
            }
            
            // Check if they have the same components
            Component[] sourceComponents = source.GetComponents<Component>();
            Component[] cloneComponents = clone.GetComponents<Component>();
            
            if (sourceComponents.Length != cloneComponents.Length)
            {
                return;
            }
            
            // Check children recursively
            for (int i = 0; i < source.transform.childCount && i < clone.transform.childCount; i++)
            {
                Transform sourceChild = source.transform.GetChild(i);
                Transform cloneChild = clone.transform.GetChild(i);
                
                VerifyObjectStructures(sourceChild.gameObject, cloneChild.gameObject);
            }
        }
        
        /// <summary>
        /// Determine if an object is a source object based on hierarchy
        /// </summary>
        private bool IsSourceObject(GameObject obj)
        {
            if (rerunManager == null || 
                rerunManager.SimulationSource == null || 
                rerunManager.SimulationClone == null)
                return true;
                
            // Check if the object is a child of the simulation source
            Transform parent = obj.transform;
            while (parent != null)
            {
                if (parent.gameObject == rerunManager.SimulationSource.gameObject)
                    return true;
                if (parent.gameObject == rerunManager.SimulationClone.gameObject)
                    return false;
                parent = parent.parent;
            }
            
            // By default assume it's a source object
            return true;
        }
        
        /// <summary>
        /// Begin recording state data
        /// </summary>
        public void BeginStateRecording()
        {
            // Clear previous data
            ObjectTimelines.Clear();
            
            // Initialize timelines for each tracked object
            foreach (TrackedObject obj in trackedObjects)
            {
                if (obj == null || string.IsNullOrEmpty(obj.objectId)) 
                {
                    continue;
                }
                
                // All tracked objects should have a timeline for potential recording
                ObjectTimelines[obj.objectId] = new ObjectStateTimeline { objectId = obj.objectId, states = new List<ObjectState>() };
            }
            
            recordingStartTime = Time.time;
            lastSampleTime = recordingStartTime;
            isRecording = true;
        }
        
        /// <summary>
        /// Stop recording state data
        /// </summary>
        public void StopStateRecording()
        {
            if (!isRecording) return;
            
            isRecording = false;
            
            // Skip if no data recorded
            if (ObjectTimelines.Count == 0)
            {
                return;
            }
            
            // Create state recording
            currentRecording = new StateRecording
            {
                totalDuration = Time.time - recordingStartTime,
                timelines = new List<ObjectStateTimeline>(ObjectTimelines.Values)
            };
            
            // Update serialized data for inspector viewing
            UpdateSerializedData();
            
            SaveStateRecording();
        }
        
        /// <summary>
        /// Record current state of all tracked objects
        /// </summary>
        private void RecordCurrentState()
        {
            float timestamp = Time.time - recordingStartTime;
            
            // Record all active tracked objects, not just "source" ones.
            foreach (TrackedObject obj in trackedObjects)
            {
                if (obj == null || !obj.gameObject.activeInHierarchy || string.IsNullOrEmpty(obj.objectId))
                {
                    continue;
                }
                
                // Capture the current state
                ObjectState state = obj.CaptureState(timestamp);
                
                // Add to timeline
                if (ObjectTimelines.TryGetValue(obj.objectId, out ObjectStateTimeline timeline))
                {
                    if (timeline.states == null)
                        timeline.states = new List<ObjectState>();
                        
                    timeline.states.Add(state);
                }
            }
        }
        
        /// <summary>
        /// Save the current recording to file
        /// </summary>
        private void SaveStateRecording()
        {
            if (currentRecording == null) return;
            
            // Get the same filename that RerunManager uses for its recordings
            string replayFilePath = rerunManager.GetCurrentFilePath();
            if (string.IsNullOrEmpty(replayFilePath))
            {
                return;
            }
            
            string stateFilePath = Path.ChangeExtension(replayFilePath, stateFileExtension);
            
            // Create the directory if it doesn't exist
            string directory = Path.GetDirectoryName(stateFilePath);
            if (!Directory.Exists(directory) && !string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
            
            // Serialize to JSON
            string json = JsonUtility.ToJson(currentRecording, true);
            File.WriteAllText(stateFilePath, json);
        }
        
        /// <summary>
        /// Load state recording for a specific replay file
        /// </summary>
        public void LoadStateRecording(string replayFilePath)
        {
            string stateFilePath = Path.ChangeExtension(replayFilePath, stateFileExtension);
            
            if (!File.Exists(stateFilePath))
            {
                currentRecording = null;
                return;
            }
            
            try
            {
                string json = File.ReadAllText(stateFilePath);
                
                currentRecording = JsonUtility.FromJson<StateRecording>(json);
                currentRecording.BuildCache();
                
                // Update serialized data for inspector viewing
                UpdateSerializedData();
            }
            catch (System.Exception)
            {
                currentRecording = null;
            }
        }
        
        /// <summary>
        /// Get the state of all objects at a specific time
        /// </summary>
        public Dictionary<string, ObjectState> GetStateAtTime(float time)
        {
            Dictionary<string, ObjectState> result = new Dictionary<string, ObjectState>();
            
            if (currentRecording == null || currentRecording.timelineDict == null)
                return result;
                
            foreach (var timeline in currentRecording.timelineDict.Values)
            {
                if (timeline.states.Count == 0) continue;
                
                ObjectState state = FindStateAtTime(timeline, time);
                if (state != null)
                {
                    result[timeline.objectId] = state;
                }
            }
            
            return result;
        }
        
        /// <summary>
        /// Get the state of all objects at a normalized time (0-1)
        /// </summary>
        public Dictionary<string, ObjectState> GetStateAtNormalizedTime(float normalizedTime)
        {
            if (currentRecording == null) return new Dictionary<string, ObjectState>();
            
            return GetStateAtTime(normalizedTime * currentRecording.totalDuration);
        }
        
        /// <summary>
        /// Find the state at a specific time, interpolating if necessary
        /// </summary>
        private ObjectState FindStateAtTime(ObjectStateTimeline timeline, float targetTime)
        {
            if (timeline.states.Count == 0) return null;
            
            // Early out for times before the first recorded state
            if (targetTime <= timeline.states[0].timestamp)
                return timeline.states[0];
                
            // Early out for times after the last recorded state
            if (targetTime >= timeline.states[timeline.states.Count - 1].timestamp)
                return timeline.states[timeline.states.Count - 1];
                
            // Find the two states to interpolate between
            ObjectState before = null;
            ObjectState after = null;
            
            for (int i = 0; i < timeline.states.Count - 1; i++)
            {
                if (timeline.states[i].timestamp <= targetTime && 
                    timeline.states[i + 1].timestamp >= targetTime)
                {
                    before = timeline.states[i];
                    after = timeline.states[i + 1];
                    break;
                }
            }
            
            // If we didn't find a pair, return null
            if (before == null || after == null)
                return null;
                
            // Interpolate between states
            return InterpolateStates(before, after, targetTime);
        }
        
        /// <summary>
        /// Interpolate between two states
        /// </summary>
        private ObjectState InterpolateStates(ObjectState before, ObjectState after, float targetTime)
        {
            // Calculate interpolation factor
            float timeRange = after.timestamp - before.timestamp;
            float t = (targetTime - before.timestamp) / timeRange;
            
            // Create interpolated state
            ObjectState result = new ObjectState
            {
                objectId = before.objectId,
                timestamp = targetTime,
                position = Vector3.Lerp(before.position, after.position, t),
                rotation = Quaternion.Slerp(before.rotation, after.rotation, t),
                scale = Vector3.Lerp(before.scale, after.scale, t),
                properties = new List<SerializedProperty>()
            };
            
            // Make sure property caches are built
            if (before.propertyCache == null) before.BuildCache();
            if (after.propertyCache == null) after.BuildCache();
            
            // Combine all property keys
            HashSet<string> allKeys = new HashSet<string>();
            foreach (var key in before.propertyCache.Keys) allKeys.Add(key);
            foreach (var key in after.propertyCache.Keys) allKeys.Add(key);
            
            // Interpolate properties
            foreach (string key in allKeys)
            {
                // If property exists in both states, interpolate
                if (before.propertyCache.ContainsKey(key) && after.propertyCache.ContainsKey(key))
                {
                    object interpolated = InterpolateProperty(
                        before.propertyCache[key], after.propertyCache[key], t);
                        
                    // Add to result
                    var prop = new SerializedProperty { key = key };
                    prop.SetValue(interpolated);
                    result.properties.Add(prop);
                }
                // Otherwise use the one that exists
                else if (before.propertyCache.ContainsKey(key))
                {
                    var prop = new SerializedProperty { key = key };
                    prop.SetValue(before.propertyCache[key]);
                    result.properties.Add(prop);
                }
                else if (after.propertyCache.ContainsKey(key))
                {
                    var prop = new SerializedProperty { key = key };
                    prop.SetValue(after.propertyCache[key]);
                    result.properties.Add(prop);
                }
            }
            
            // Build cache
            result.BuildCache();
            
            return result;
        }
        
        /// <summary>
        /// Interpolate between two property values
        /// </summary>
        private object InterpolateProperty(object before, object after, float t)
        {
            // Handle different types
            if (before is Vector3 vBefore && after is Vector3 vAfter)
                return Vector3.Lerp(vBefore, vAfter, t);
                
            if (before is Quaternion qBefore && after is Quaternion qAfter)
                return Quaternion.Slerp(qBefore, qAfter, t);
                
            if (before is float fBefore && after is float fAfter)
                return Mathf.Lerp(fBefore, fAfter, t);
                
            if (before is int iBefore && after is int iAfter)
                return Mathf.RoundToInt(Mathf.Lerp(iBefore, iAfter, t));
                
            if (before is Color cBefore && after is Color cAfter)
                return Color.Lerp(cBefore, cAfter, t);
                
            // For non-interpolatable types, use the value closer to target time
            return t < 0.5f ? before : after;
        }
        
        /// <summary>
        /// Apply state at current playback position to source objects
        /// </summary>
        public void LiveFromCurrentPosition()
        {
            if (rerunManager == null)
            {
                return;
            }
            
            if (!ReplayManager.IsReplaying(rerunManager.playbackHandle))
            {
                return;
            }
            
            // Get current playback time
            float normalizedTime = ReplayManager.GetPlaybackTimeNormalized(rerunManager.playbackHandle);
            ReplayTime playbackTime = ReplayManager.GetPlaybackTime(rerunManager.playbackHandle);
            
            // Validate recording data
            if (currentRecording == null)
            {
                // Fallback to capturing current states of ALL active tracked objects
                // This handles cases where there's no recording file or pedestrians without clones.
                currentRecording = CaptureCurrentStatesOfAllTrackedObjects();

                if (currentRecording == null || currentRecording.timelines.Count == 0)
                {
                     return;
                }
            }
            
            // Ensure the currentRecording's cache is built, especially if it was just created or loaded
            if (currentRecording.timelineDict == null || currentRecording.timelineDict.Count == 0)
            {
                currentRecording.BuildCache();
            }

            if (currentRecording == null || currentRecording.timelines.Count == 0)
            {
                return;
            }
            
            // Get the current state of all objects
            Dictionary<string, ObjectState> states = GetStateAtNormalizedTime(normalizedTime);
            
            if (states.Count == 0)
            {
                // Try to create states from current clone objects as a fallback
                states = CaptureCloneStates();
                
                if (states.Count == 0)
                {
                    return;
                }
            }
            
            // Start coroutine to apply states with proper timing
            StartCoroutine(ApplyStatesAfterDelay(states, playbackTime, normalizedTime));
        }
        
        /// <summary>
        /// Creates a basic recording with just the current state of all clone objects
        /// </summary>
        private void CreateBasicRecordingFromClone() //This method might be obsolete or less used now
        {
            Dictionary<string, ObjectState> cloneStates = CaptureCloneStates();
            
            if (cloneStates.Count == 0)
            {
                return;
            }
            
            // Create state timelines for each object
            List<ObjectStateTimeline> timelines = new List<ObjectStateTimeline>();
            
            foreach (var pair in cloneStates)
            {
                ObjectStateTimeline timeline = new ObjectStateTimeline
                {
                    objectId = pair.Key,
                    states = new List<ObjectState> { pair.Value }
                };
                
                timelines.Add(timeline);
            }
            
            // Create the recording
            currentRecording = new StateRecording
            {
                totalDuration = 1.0f, // Just use 1 second as duration
                timelines = timelines
            };
            
            currentRecording.BuildCache();
        }
        
        /// <summary>
        /// Captures the current state of all clone objects
        /// </summary>
        private Dictionary<string, ObjectState> CaptureCloneStates() // This method might be obsolete or less used now
        {
            Dictionary<string, ObjectState> states = new Dictionary<string, ObjectState>();
            
            if (rerunManager?.SimulationClone == null)
            {
                return states;
            }
            
            // Find all TrackedObjects on clone
            TrackedObject[] cloneTrackedObjects = rerunManager.SimulationClone.GetComponentsInChildren<TrackedObject>(true);
            
            foreach (TrackedObject tracked in cloneTrackedObjects)
            {
                if (tracked == null) continue;
                
                // Capture state
                ObjectState state = tracked.CaptureState(0f);
                states[tracked.objectId] = state;
            }
            
            if (states.Count == 0)
            {
                // Try to add TrackedObject components to all ReplayObjects in the clone
                GameObject cloneObj = rerunManager.SimulationClone.gameObject;
                ReplayObject[] replayObjects = cloneObj.GetComponentsInChildren<ReplayObject>(true);
                
                foreach (ReplayObject replayObj in replayObjects)
                {
                    if (replayObj == null) continue;
                    
                    TrackedObject tracked = replayObj.gameObject.GetComponent<TrackedObject>();
                    if (tracked == null)
                    {
                        tracked = replayObj.gameObject.AddComponent<TrackedObject>();
                        tracked.replayObject = replayObj;
                    }
                    
                    // Now capture the state
                    ObjectState state = tracked.CaptureState(0f);
                    states[tracked.objectId] = state;
                }
            }
            
            return states;
        }
        
        /// <summary>
        /// Captures the current state of all active TrackedObjects.
        /// This is a more general fallback than CreateBasicRecordingFromClone.
        /// </summary>
        private StateRecording CaptureCurrentStatesOfAllTrackedObjects()
        {
            List<ObjectStateTimeline> timelines = new List<ObjectStateTimeline>();
            float currentTime = 0f; // Or use Time.time if a global timestamp makes sense here

            foreach (TrackedObject tracked in trackedObjects)
            {
                if (tracked == null || !tracked.gameObject.activeInHierarchy || string.IsNullOrEmpty(tracked.objectId))
                {
                    continue;
                }

                // Capture state (timestamp 0 for a "snapshot")
                ObjectState state = tracked.CaptureState(currentTime); 
                ObjectStateTimeline timeline = new ObjectStateTimeline
                {
                    objectId = tracked.objectId,
                    states = new List<ObjectState> { state }
                };
                timelines.Add(timeline);
            }

            if (timelines.Count == 0)
            {
                return null;
            }

            StateRecording recording = new StateRecording
            {
                totalDuration = 1.0f, // Arbitrary duration for a snapshot
                timelines = timelines
            };
            recording.BuildCache(); // Important!
            return recording;
        }
        
        /// <summary>
        /// Apply states after a short delay to ensure proper object activation
        /// </summary>
        private IEnumerator ApplyStatesAfterDelay(Dictionary<string, ObjectState> states, ReplayTime playbackTime, float normalizedTime)
        {
            // Stop playback
            rerunManager.SafeStopPlayback();
            
            // Wait for a frame to ensure everything is processed
            yield return null;
            yield return null; // Add an extra frame for stability
            
            // Collect important references before making any changes
            GameObject sourceObj = rerunManager.SimulationSource?.gameObject;
            GameObject cloneObj = rerunManager.SimulationClone?.gameObject;
            
            if (sourceObj == null)
            {
                yield break;
            }
            
            if (cloneObj == null)
            {
                yield break;
            }
            
            // Step 1: Deactivate the clone
            if (cloneObj != null)
            {
                cloneObj.SetActive(false);
            }
            
            // Step 2: Activate the source
            if (sourceObj != null)
            {
                // Activate the source
                sourceObj.SetActive(true);
            }
            
            // Wait for another frame to ensure activation is fully processed
            yield return null;
            yield return null; // Add an extra frame for stability
            
            // Rebuild object mappings to ensure we have correct references
            FindTrackedObjects(); // Refresh the list of all tracked objects first
            BuildObjectMappings(); // Then map them
            
            // Apply states to source objects
            int appliedCount = 0;
            int failedCount = 0;
            int skippedCount = 0;
            
            foreach (var pair in states)
            {
                string objectId = pair.Key;
                ObjectState state = pair.Value;
                
                if (SourceObjects.TryGetValue(objectId, out GameObject targetObj))
                {
                    // Verify the source object is active
                    if (!targetObj.activeInHierarchy)
                    {
                        targetObj.SetActive(true);
                        
                        if (!targetObj.activeInHierarchy)
                        {
                            skippedCount++;
                            continue;
                        }
                    }
                    
                    TrackedObject trackedObj = targetObj.GetComponent<TrackedObject>();
                    if (trackedObj != null)
                    {
                        try
                        {
                            trackedObj.ApplyState(state);
                            appliedCount++;
                        }
                        catch (System.Exception)
                        {
                            failedCount++;
                        }
                    }
                    else
                    {
                        failedCount++;
                    }
                }
                else
                {
                    failedCount++;
                }
            }
            
            string timeString = ReplayTime.GetCorrectedTimeValueString(playbackTime.Time);
            
            // Update info string in RerunManager
            rerunManager.SetInfoString($"Live view (from position: {timeString})");
        }

        // Add this method to hook into the RerunManager's live transition
        private void OnEnable()
        {
            // Subscribe to any events or hooks in RerunManager if needed
        }
        
        // Method to test recording a state file
        public void TestSaveStateFile()
        {
            // Create a simple test recording
            if (trackedObjects.Count == 0)
            {
                return;
            }
            
            ObjectStateTimeline timeline = new ObjectStateTimeline 
            { 
                objectId = trackedObjects[0].objectId 
            };
            
            // Create a test state
            ObjectState state = trackedObjects[0].CaptureState(0);
            timeline.states.Add(state);
            
            currentRecording = new StateRecording
            {
                totalDuration = 1.0f,
                timelines = new List<ObjectStateTimeline> { timeline }
            };
            
            // Save to file
            SaveStateRecording();
            
            // Check if the file was created
            string replayFilePath = rerunManager.GetCurrentFilePath();
            if (string.IsNullOrEmpty(replayFilePath))
            {
                // Use a fallback path for testing
                string fallbackPath = Path.Combine(Application.persistentDataPath, "test_recording.rerun");
                string stateFilePath = Path.ChangeExtension(fallbackPath, stateFileExtension);
                
                // Serialize to JSON
                string json = JsonUtility.ToJson(currentRecording, true);
                File.WriteAllText(stateFilePath, json);
            }
        }
        
        // Find any existing state recording files
        public void FindExistingStateFiles()
        {
            // Check in the persistent data path
            string persistentPath = Application.persistentDataPath;
            
            try
            {
                string[] files = Directory.GetFiles(persistentPath, "*" + stateFileExtension, SearchOption.AllDirectories);
            }
            catch (System.Exception)
            {
            }
            
            // Also check in the project's recording folder if known
            if (rerunManager != null)
            {
                string recordingFolder = Path.GetDirectoryName(rerunManager.GetCurrentFilePath());
                if (!string.IsNullOrEmpty(recordingFolder) && Directory.Exists(recordingFolder))
                {
                    try
                    {
                        string[] files = Directory.GetFiles(recordingFolder, "*" + stateFileExtension, SearchOption.AllDirectories);
                    }
                    catch (System.Exception)
                    {
                    }
                }
            }
        }
        
        /// <summary>
        /// Replay from a specific time offset (e.g., replay from 10 seconds ago to now)
        /// </summary>
        public void ReplayFromTimeOffset(float secondsToReplayFromPast)
        {
            
            // Check if we have a valid recording
            if (currentRecording == null || currentRecording.timelines == null || currentRecording.timelines.Count == 0)
            {
                Debug.LogWarning("No state recording available. Cannot replay past seconds.");
                return;
            }
            
            // Ensure the recording cache is built
            if (currentRecording.timelineDict == null || currentRecording.timelineDict.Count == 0)
            {
                currentRecording.BuildCache();
            }
            
            // Calculate the start time (totalDuration - secondsToReplay, but not less than 0)
            float startTime = Mathf.Max(0, currentRecording.totalDuration - secondsToReplayFromPast);
            float endTime = currentRecording.totalDuration;
            
            if (startTime >= endTime)
            {
                Debug.LogWarning($"Recording duration ({currentRecording.totalDuration}s) is shorter than requested replay duration ({secondsToReplayFromPast}s).");
                startTime = 0;
            }
            
            Debug.Log($"Replaying from {startTime}s to {endTime}s (duration: {endTime - startTime}s)");
            
            // Start the replay coroutine
            StartCoroutine(ReplayTimeRangeCoroutine(startTime, endTime));
        }
        
        /// <summary>
        /// Coroutine to replay a specific time range by applying states frame by frame
        /// </summary>
        private IEnumerator ReplayTimeRangeCoroutine(float startTime, float endTime)
        {
            
            // Wait a frame for activation to complete
            yield return null;
            
            // Rebuild object mappings
            BuildObjectMappings();
            
            float currentTime = startTime;
            float replayStartRealTime = Time.time;
            
            Debug.Log($"Starting replay at real time: {replayStartRealTime}");
            
            // Replay loop
            while (currentTime <= endTime)
            {
                // Get states at current time
                Dictionary<string, ObjectState> states = GetStateAtTime(currentTime);
                
                // Apply states to source objects
                foreach (var pair in states)
                {
                    string objectId = pair.Key;
                    ObjectState state = pair.Value;
                    
                    if (SourceObjects.TryGetValue(objectId, out GameObject targetObj))
                    {
                        if (targetObj != null && targetObj.activeInHierarchy)
                        {
                            TrackedObject trackedObj = targetObj.GetComponent<TrackedObject>();
                            if (trackedObj != null)
                            {
                                try
                                {
                                    trackedObj.ApplyState(state);
                                }
                                catch (System.Exception ex)
                                {
                                    Debug.LogWarning($"Failed to apply state to {objectId}: {ex.Message}");
                                }
                            }
                        }
                    }
                }
                
                // Wait for next frame
                yield return null;
                
                // Advance time based on actual elapsed time (so replay happens at normal speed)
                float elapsedRealTime = Time.time - replayStartRealTime;
                currentTime = startTime + elapsedRealTime;
            }
            
            Debug.Log("Replay complete.");
            
            // Update info string
            rerunManager.SetInfoString("Replay complete - Live view");
        }
    }
} 