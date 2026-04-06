using UnityEngine;
using UnityEngine.UI;
using System.Collections.Generic;
using System.Text.RegularExpressions;

public class TrajectoryVisualizer : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private TextAsset logFile;
    [SerializeField] private Camera sceneCamera;
    [SerializeField] private Button toggleButton;
    
    [Header("Visualization Settings")]
    [SerializeField] private Color trajectoryColor = Color.green;
    [SerializeField] private float lineWidth = 0.1f;
    [SerializeField] private Material lineMaterial;
    
    [Header("Direction Visualization")]
    [SerializeField] private bool showDirections = false;
    [SerializeField] private Color directionColor = Color.red;
    [SerializeField] private float arrowLength = 1f;
    [SerializeField] private int directionSpacing = 5; // Show arrow every N points
    
    [Header("Camera Settings")]
    [SerializeField] private float topDownHeight = 50f;
    [SerializeField] private Vector3 defaultCameraPosition;
    [SerializeField] private Quaternion defaultCameraRotation;
    
    private LineRenderer lineRenderer;
    private TrajectoryLogData trajectoryData;
    private bool isTrajectoryVisible = false;
    private Vector3 trajectoryCenter;
    private List<LineRenderer> directionArrows = new List<LineRenderer>();
    private List<Vector3> trajectoryPositions = new List<Vector3>();
    private List<Quaternion> trajectoryRotations = new List<Quaternion>();

    void Start()
    {
        // Store default camera state
        if (sceneCamera != null)
        {
            defaultCameraPosition = sceneCamera.transform.position;
            defaultCameraRotation = sceneCamera.transform.rotation;
        }
        else
        {
            sceneCamera = Camera.main;
            if (sceneCamera != null)
            {
                defaultCameraPosition = sceneCamera.transform.position;
                defaultCameraRotation = sceneCamera.transform.rotation;
            }
        }
        
        // Setup line renderer
        SetupLineRenderer();
        
        // Load trajectory data
        LoadTrajectoryData();
        
        // Setup button
        if (toggleButton != null)
        {
            toggleButton.onClick.AddListener(ToggleTrajectory);
        }
        
        // Initially hide trajectory
        if (lineRenderer != null)
        {
            lineRenderer.enabled = false;
        }

        RefreshButtonVisibility();
    }

    void Update()
    {
        RefreshButtonVisibility();
    }

    void SetupLineRenderer()
    {
        GameObject lineObj = new GameObject("TrajectoryLine");
        lineObj.transform.SetParent(transform);
        lineRenderer = lineObj.AddComponent<LineRenderer>();
        
        lineRenderer.startWidth = lineWidth;
        lineRenderer.endWidth = lineWidth;
        lineRenderer.startColor = trajectoryColor;
        lineRenderer.endColor = trajectoryColor;
        lineRenderer.useWorldSpace = true;
        
        if (lineMaterial != null)
        {
            lineRenderer.material = lineMaterial;
        }
        else
        {
            // Create default material if none provided
            lineRenderer.material = new Material(Shader.Find("Sprites/Default"));
            lineRenderer.material.color = trajectoryColor;
        }
    }

    void LoadTrajectoryData()
    {
        if (logFile == null)
        {
            Debug.LogError("No log file assigned!");
            return;
        }

        try
        {
            // First, parse raw JSON to debug
            string json = logFile.text;
            Debug.Log("=== PARSING RAW JSON ===");
            ParseAndPrintRawValues(json);
            
            // Then try standard deserialization
            trajectoryData = JsonUtility.FromJson<TrajectoryLogData>(json);
            
            if (trajectoryData != null && trajectoryData.timelines != null && trajectoryData.timelines.Count > 0)
            {
                Debug.Log($"Loaded trajectory with {trajectoryData.timelines[0].states.Count} points");
                BuildTrajectoryLine();
            }
            else
            {
                Debug.LogError("Failed to parse trajectory data or no timelines found");
            }
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Error loading trajectory data: {e.Message}");
        }
    }

    void ParseAndPrintRawValues(string json)
    {
        // Extract states array
        int statesStart = json.IndexOf("\"states\":");
        if (statesStart == -1) return;
        
        // Find the states array content
        int arrayStart = json.IndexOf("[", statesStart);
        int arrayEnd = FindMatchingBracket(json, arrayStart);
        
        if (arrayStart == -1 || arrayEnd == -1) return;
        
        string statesJson = json.Substring(arrayStart, arrayEnd - arrayStart + 1);
        
        // Split by state objects
        List<int> stateStarts = new List<int>();
        int depth = 0;
        for (int i = 0; i < statesJson.Length; i++)
        {
            if (statesJson[i] == '{')
            {
                if (depth == 0) stateStarts.Add(i);
                depth++;
            }
            else if (statesJson[i] == '}')
            {
                depth--;
            }
        }
        
        // Parse all states
        trajectoryPositions.Clear();
        trajectoryRotations.Clear();
        
        for (int idx = 0; idx < stateStarts.Count; idx++)
        {
            int start = stateStarts[idx];
            int end = idx < stateStarts.Count - 1 ? stateStarts[idx + 1] : statesJson.Length;
            
            // Find closing brace
            depth = 0;
            for (int i = start; i < statesJson.Length; i++)
            {
                if (statesJson[i] == '{') depth++;
                else if (statesJson[i] == '}') 
                {
                    depth--;
                    if (depth == 0)
                    {
                        end = i + 1;
                        break;
                    }
                }
            }
            
            string stateStr = statesJson.Substring(start, end - start);
            ExtractPositionAndRotation(stateStr, idx);
            
            // Print sample states
            if (idx < 5 || idx >= stateStarts.Count - 1)
            {
                Debug.Log($"State {idx}: Pos=({trajectoryPositions[idx].x:F3}, {trajectoryPositions[idx].y:F3}, {trajectoryPositions[idx].z:F3}), " +
                         $"Rot=({trajectoryRotations[idx].x:F3}, {trajectoryRotations[idx].y:F3}, {trajectoryRotations[idx].z:F3}, {trajectoryRotations[idx].w:F3})");
            }
        }
        Debug.Log($"Loaded {trajectoryPositions.Count} trajectory points");
    }

    int FindMatchingBracket(string json, int start)
    {
        int depth = 0;
        for (int i = start; i < json.Length; i++)
        {
            if (json[i] == '[') depth++;
            else if (json[i] == ']') 
            {
                depth--;
                if (depth == 0) return i;
            }
        }
        return -1;
    }

    void ExtractPositionAndRotation(string stateJson, int index)
    {
        // Extract position
        Regex posRegex = new Regex(@"""position""\s*:\s*\{[^}]+""x""\s*:\s*([-\d.]+)[^}]+""y""\s*:\s*([-\d.]+)[^}]+""z""\s*:\s*([-\d.]+)");
        Match posMatch = posRegex.Match(stateJson);
        
        // Extract rotation
        Regex rotRegex = new Regex(@"""rotation""\s*:\s*\{[^}]+""x""\s*:\s*([-\d.]+)[^}]+""y""\s*:\s*([-\d.]+)[^}]+""z""\s*:\s*([-\d.]+)[^}]+""w""\s*:\s*([-\d.]+)");
        Match rotMatch = rotRegex.Match(stateJson);
        
        if (posMatch.Success && rotMatch.Success)
        {
            float posX = float.Parse(posMatch.Groups[1].Value);
            float posY = float.Parse(posMatch.Groups[2].Value);
            float posZ = float.Parse(posMatch.Groups[3].Value);
            
            float rotX = float.Parse(rotMatch.Groups[1].Value);
            float rotY = float.Parse(rotMatch.Groups[2].Value);
            float rotZ = float.Parse(rotMatch.Groups[3].Value);
            float rotW = float.Parse(rotMatch.Groups[4].Value);
            
            trajectoryPositions.Add(new Vector3(posX, posY, posZ));
            trajectoryRotations.Add(new Quaternion(rotX, rotY, rotZ, rotW));
        }
    }

    void BuildTrajectoryLine()
    {
        if (trajectoryPositions.Count == 0)
        {
            Debug.LogError("No trajectory positions loaded!");
            return;
        }

        // Set up line renderer positions
        lineRenderer.positionCount = trajectoryPositions.Count;
        Vector3 sum = Vector3.zero;
        
        Debug.Log($"Building trajectory with {trajectoryPositions.Count} points");
        for (int i = 0; i < trajectoryPositions.Count; i++)
        {
            Vector3 pos = trajectoryPositions[i];
            lineRenderer.SetPosition(i, pos);
            sum += pos;
            
            if (i < 3 || i == trajectoryPositions.Count - 1)
            {
                Debug.Log($"Point {i}: ({pos.x:F3}, {pos.y:F3}, {pos.z:F3})");
            }
        }
        
        // Calculate trajectory center for camera positioning
        trajectoryCenter = sum / trajectoryPositions.Count;
        Debug.Log($"Trajectory center: ({trajectoryCenter.x:F3}, {trajectoryCenter.y:F3}, {trajectoryCenter.z:F3})");
        
        // Build direction arrows if enabled
        if (showDirections)
        {
            BuildDirectionArrows();
        }
    }

    void BuildDirectionArrows()
    {
        // Clear existing arrows
        foreach (var arrow in directionArrows)
        {
            if (arrow != null)
                Destroy(arrow.gameObject);
        }
        directionArrows.Clear();

        // Create arrows at intervals
        Debug.Log($"Building direction arrows with spacing {directionSpacing}");
        for (int i = 0; i < trajectoryPositions.Count; i += directionSpacing)
        {
            Vector3 position = trajectoryPositions[i];
            Quaternion rotation = trajectoryRotations[i];
            
            // Debug arrow positions and rotations
            if (i < directionSpacing * 3 || i >= trajectoryPositions.Count - directionSpacing)
            {
                Vector3 euler = rotation.eulerAngles;
                Debug.Log($"Arrow {i}: Pos=({position.x:F3}, {position.y:F3}, {position.z:F3}), Rot=({euler.x:F1}, {euler.y:F1}, {euler.z:F1})");
            }
            
            // Create arrow line renderer
            GameObject arrowObj = new GameObject($"DirectionArrow_{i}");
            arrowObj.transform.SetParent(transform);
            LineRenderer arrow = arrowObj.AddComponent<LineRenderer>();
            
            arrow.positionCount = 2;
            arrow.startWidth = lineWidth * 0.5f;
            arrow.endWidth = lineWidth * 0.5f;
            arrow.startColor = directionColor;
            arrow.endColor = directionColor;
            arrow.useWorldSpace = true;
            
            if (lineMaterial != null)
            {
                arrow.material = lineMaterial;
            }
            else
            {
                arrow.material = new Material(Shader.Find("Sprites/Default"));
                arrow.material.color = directionColor;
            }
            
            // Set arrow positions (from position to forward direction)
            Vector3 forward = rotation * Vector3.forward;
            arrow.SetPosition(0, position);
            arrow.SetPosition(1, position + forward * arrowLength);
            
            arrow.enabled = false; // Initially hidden
            directionArrows.Add(arrow);
        }
        Debug.Log($"Created {directionArrows.Count} direction arrows");
    }

    public void ToggleTrajectory()
    {
        if (!IsReviewActive())
            return;

        isTrajectoryVisible = !isTrajectoryVisible;
        
        if (lineRenderer != null)
        {
            lineRenderer.enabled = isTrajectoryVisible;
        }
        
        // Toggle direction arrows
        foreach (var arrow in directionArrows)
        {
            if (arrow != null)
                arrow.enabled = isTrajectoryVisible;
        }
        
        if (sceneCamera != null)
        {
            if (isTrajectoryVisible)
            {
                // Switch to top-down view
                SwitchToTopDownView();
            }
            else
            {
                // Return to default view
                ReturnToDefaultView();
            }
        }
        
        Debug.Log($"Trajectory visibility: {isTrajectoryVisible}");
    }

    void RefreshButtonVisibility()
    {
        if (toggleButton == null)
            return;

        bool shouldShow = IsReviewActive();
        if (toggleButton.gameObject.activeSelf != shouldShow)
            toggleButton.gameObject.SetActive(shouldShow);

        if (!shouldShow && isTrajectoryVisible)
        {
            isTrajectoryVisible = false;
            if (lineRenderer != null)
                lineRenderer.enabled = false;

            foreach (var arrow in directionArrows)
            {
                if (arrow != null)
                    arrow.enabled = false;
            }
        }
    }

    bool IsReviewActive()
    {
        var reviewManager = SessionReview.SessionReviewManager.Instance;
        return reviewManager != null && reviewManager.IsReviewUiActive;
    }

    void SwitchToTopDownView()
    {
        if (sceneCamera == null) return;
        
        // Position camera above trajectory center looking down
        Vector3 topDownPosition = new Vector3(trajectoryCenter.x, trajectoryCenter.y + topDownHeight, trajectoryCenter.z);
        sceneCamera.transform.position = topDownPosition;
        sceneCamera.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
    }

    void ReturnToDefaultView()
    {
        if (sceneCamera == null) return;
        
        sceneCamera.transform.position = defaultCameraPosition;
        sceneCamera.transform.rotation = defaultCameraRotation;
    }

    void OnDestroy()
    {
        if (toggleButton != null)
        {
            toggleButton.onClick.RemoveListener(ToggleTrajectory);
        }
        
        // Clean up direction arrows
        foreach (var arrow in directionArrows)
        {
            if (arrow != null)
                Destroy(arrow.gameObject);
        }
        directionArrows.Clear();
    }
}
