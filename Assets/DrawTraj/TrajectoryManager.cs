using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// TrajectoryManager — attach to an empty "TrajectoryManager" GameObject.
///
/// SCENE SETUP:
///   1. Attach this script to an empty GameObject named "TrajectoryManager".
///   2. Assign 'mainCamera' (your normal scene camera).
///   3. Leave 'topDownCamera' blank to auto-create, or assign your own.
///   4. Set 'groundLayer' to the layer(s) of your sidewalk / road meshes.
///   5. Assign 'trajectoryTarget' to your avatar/character Transform.
///   6. Wire TrajectoryUI separately (see TrajectoryUI.cs).
///
/// DRAW MODE CONTROLS:
///   Mouse left-click / hold   — draw
///   Touch (finger / pencil)   — draw
///   ESC                       — end session, save, return to normal view
/// </summary>
public class TrajectoryManager : MonoBehaviour
{
    // ── Inspector ────────────────────────────────────────────────────────────

    [Header("Cameras")]
    public Camera mainCamera;
    public Camera topDownCamera;           // leave null to auto-create
    public float topDownHeight = 80f;
    public float topDownOrthoSize = 50f;

    [Header("Target")]
    [Tooltip("The avatar / character the trajectory starts from. " +
             "Camera will fly to above this object when entering draw mode.")]
    public Transform trajectoryTarget;

    [Tooltip("Orthographic size when zoomed in on the target. Smaller = tighter zoom.")]
    public float zoomedOrthoSize = 20f;

    [Tooltip("Seconds for the camera to slide into position over the target.")]
    [Range(0.1f, 2f)] public float cameraFlyDuration = 0.5f;

    [Header("Ground")]
    [Tooltip("Layer mask of your sidewalk / road meshes for raycasting.")]
    public LayerMask groundLayer = ~0;

    [Header("Trajectory Visuals")]
    [Tooltip("Height above surface so the line is always visible.")]
    public float heightOffset = 0.15f;

    [Tooltip("Color used while drawing (live session).")]
    public Color drawColor = new Color(0.2f, 0.85f, 1f, 0.95f);

    [Tooltip("Color used when loading saved trajectories for display.")]
    public Color loadedColor = new Color(1f, 0.6f, 0.15f, 0.9f);

    [Header("Display")]
    [Tooltip("How many of the most-recent saved sessions to show in scene. 0 = all.")]
    [Min(0)] public int visibleSessionCount = 1;

    // ── Public state (read by TrajectoryUI) ──────────────────────────────────
    public bool IsDrawMode { get; private set; }

    // ── Private state ────────────────────────────────────────────────────────
    private TrajectoryRenderer _activeRenderer;
    private readonly List<Vector3> _sessionPoints = new List<Vector3>();
    private TrajectoryCollection _sessionCollection;
    // Renderers drawn live this session — replaced by Traj_Display after save
    private readonly List<TrajectoryRenderer> _sessionRenderers = new List<TrajectoryRenderer>();

    private bool _trajectoriesVisible = true;
    private bool _cameraReady = false;   // false while camera is still flying in

    // All renderers that are currently displayed (loaded from saved files).
    private readonly List<TrajectoryRenderer> _displayedRenderers = new List<TrajectoryRenderer>();

    // ── Unity Lifecycle ──────────────────────────────────────────────────────

    private void Start()
    {
        if (mainCamera == null) mainCamera = Camera.main;
        SetupTopDownCamera();
    }

    private void Update()
    {
        if (!IsDrawMode) return;

        // Block input until camera has finished flying in
        if (_cameraReady)
            HandleDrawInput();

        if (Input.GetKeyDown(KeyCode.Escape))
            ExitDrawMode();
    }

    // ── Public API (called by TrajectoryUI) ──────────────────────────────────

    /// <summary>Enter draw mode: switch camera, fly to target, start a fresh session.</summary>
    public void EnterDrawMode()
    {
        if (IsDrawMode) return;
        IsDrawMode = true;
        _cameraReady = false;

        SetVisibility(false);
        _sessionCollection = new TrajectoryCollection();
        SwitchCamera(topDown: true);
        BeginStroke();

        // Fly camera to above the target, then seed the first point
        StartCoroutine(FlyToTarget(() =>
        {
            PlantStartPoint();
            _cameraReady = true;
        }));
    }

    /// <summary>Exit draw mode: finalise session, save, refresh display.</summary>
    public void ExitDrawMode()
    {
        if (!IsDrawMode) return;
        IsDrawMode = false;
        _cameraReady = false;

        EndStroke();
        SaveSession();
        SwitchCamera(topDown: false);
        RefreshDisplay();
    }

    /// <summary>Toggle visibility of all currently displayed trajectories.</summary>
    public void ToggleVisibility()
    {
        SetVisibility(!_trajectoriesVisible);
    }

    private void SetVisibility(bool visible)
    {
        _trajectoriesVisible = visible;
        foreach (var r in _displayedRenderers)
            if (r != null) r.gameObject.SetActive(_trajectoriesVisible);
    }

    // ── Camera Fly ───────────────────────────────────────────────────────────

    /// <summary>
    /// Smoothly move the top-down camera to above trajectoryTarget and
    /// zoom in, then invoke the callback.
    /// Falls back gracefully if no target is assigned.
    /// </summary>
    private IEnumerator FlyToTarget(System.Action onComplete)
    {
        if (topDownCamera == null) { onComplete?.Invoke(); yield break; }

        Vector3 startPos = topDownCamera.transform.position;
        float startSize = topDownCamera.orthographicSize;

        // Destination: directly above target (or keep current XZ if no target)
        Vector3 destPos = startPos;
        if (trajectoryTarget != null)
        {
            destPos = trajectoryTarget.position;
            destPos.y = topDownHeight;
        }
        float destSize = (trajectoryTarget != null) ? zoomedOrthoSize : topDownOrthoSize;

        float elapsed = 0f;
        while (elapsed < cameraFlyDuration)
        {
            float t = Mathf.SmoothStep(0f, 1f, elapsed / cameraFlyDuration);
            topDownCamera.transform.position = Vector3.Lerp(startPos, destPos, t);
            topDownCamera.orthographicSize = Mathf.Lerp(startSize, destSize, t);
            elapsed += Time.deltaTime;
            yield return null;
        }

        // Snap to exact destination
        topDownCamera.transform.position = destPos;
        topDownCamera.orthographicSize = destSize;

        onComplete?.Invoke();
    }

    // ── Drawing ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Seed the very first trajectory point at the target's ground position
    /// so the path always originates from the character's feet.
    /// </summary>
    private void PlantStartPoint()
    {
        if (trajectoryTarget == null) return;

        // Project target position onto the ground (raycast down from above)
        Ray ray = new Ray(trajectoryTarget.position + Vector3.up * 5f, Vector3.down);
        Vector3 startPoint;

        if (Physics.Raycast(ray, out RaycastHit hit, 20f, groundLayer))
            startPoint = hit.point + Vector3.up * heightOffset;
        else
            startPoint = new Vector3(trajectoryTarget.position.x, heightOffset, trajectoryTarget.position.z);

        _activeRenderer?.AddPoint(startPoint);
        _sessionPoints.Add(startPoint);
    }

    private void HandleDrawInput()
    {
        bool touching = false;

        // ── Touch input (iPad finger / Apple Pencil) ──
        if (Input.touchCount > 0)
        {
            Touch t = Input.GetTouch(0);
            if (t.phase == TouchPhase.Began || t.phase == TouchPhase.Moved || t.phase == TouchPhase.Stationary)
            {
                touching = true;
                TryAddPointFromScreen(t.position);
            }
        }

        // ── Mouse input (fallback / desktop) ──
        if (!touching && Input.GetMouseButton(0))
        {
            TryAddPointFromScreen(Input.mousePosition);
        }
    }

    private void TryAddPointFromScreen(Vector2 screenPos)
    {
        Camera cam = topDownCamera != null ? topDownCamera : mainCamera;
        Ray ray = cam.ScreenPointToRay(screenPos);

        Vector3 point;
        if (Physics.Raycast(ray, out RaycastHit hit, 1000f, groundLayer))
        {
            point = hit.point + Vector3.up * heightOffset;
        }
        else
        {
            // Fallback: flat plane at heightOffset
            Plane ground = new Plane(Vector3.up, Vector3.up * heightOffset);
            if (!ground.Raycast(ray, out float dist)) return;
            point = ray.GetPoint(dist);
        }

        _activeRenderer?.AddPoint(point);
        _sessionPoints.Add(point);
    }

    // ── Stroke / Session Management ──────────────────────────────────────────

    private void BeginStroke()
    {
        var go = new GameObject("Stroke_Active");
        go.transform.SetParent(transform);
        _activeRenderer = go.AddComponent<TrajectoryRenderer>();
        _activeRenderer.lineColor = drawColor;
        _sessionPoints.Clear();
    }

    private void EndStroke()
    {
        if (_activeRenderer == null) return;

        if (_activeRenderer.Points.Count >= 2)
        {
            var data = _activeRenderer.ExportData();
            _sessionCollection.trajectories.Add(data);
            _activeRenderer.gameObject.name = "Stroke_Session";
            _sessionRenderers.Add(_activeRenderer);
        }
        else
        {
            Destroy(_activeRenderer.gameObject);
        }

        _activeRenderer = null;
    }

    private void SaveSession()
    {
        if (_sessionCollection == null || _sessionCollection.trajectories.Count == 0)
        {
            Debug.Log("[Trajectory] Session empty — nothing saved.");
            return;
        }
        TrajectoryIO.SaveNewSession(_sessionCollection);
        Debug.Log($"[Trajectory] Session saved ({_sessionCollection.trajectories.Count} stroke(s)).");
    }

    // ── Display / Viz ────────────────────────────────────────────────────────

    private void RefreshDisplay()
    {
        foreach (var r in _displayedRenderers)
            if (r != null) Destroy(r.gameObject);
        _displayedRenderers.Clear();

        foreach (var r in _sessionRenderers)
            if (r != null) Destroy(r.gameObject);
        _sessionRenderers.Clear();

        string[] files = TrajectoryIO.GetAllSessionFiles();
        int count = (visibleSessionCount <= 0) ? files.Length
                                                : Mathf.Min(visibleSessionCount, files.Length);

        for (int i = 0; i < count; i++)
        {
            TrajectoryCollection col = TrajectoryIO.LoadFromPath(files[i]);
            foreach (var data in col.trajectories)
            {
                if (data.points.Count < 2) continue;
                StartCoroutine(SpawnDisplayRenderer(data));
            }
        }

        _trajectoriesVisible = true;
    }

    private IEnumerator SpawnDisplayRenderer(TrajectoryData data)
    {
        var go = new GameObject("Traj_Display");
        go.transform.SetParent(transform);
        var r = go.AddComponent<TrajectoryRenderer>();
        r.lineColor = loadedColor;

        yield return null;

        r.LoadFromData(data, heightOffset);
        _displayedRenderers.Add(r);
    }

    // ── Camera Setup / Switch ─────────────────────────────────────────────────

    private void SetupTopDownCamera()
    {
        if (topDownCamera != null)
        {
            topDownCamera.gameObject.SetActive(false);
            return;
        }

        var go = new GameObject("Camera_TopDown");
        topDownCamera = go.AddComponent<Camera>();
        topDownCamera.orthographic = true;
        topDownCamera.orthographicSize = topDownOrthoSize;
        topDownCamera.clearFlags = CameraClearFlags.SolidColor;
        topDownCamera.backgroundColor = new Color(0.07f, 0.07f, 0.09f);
        topDownCamera.nearClipPlane = 0.1f;
        topDownCamera.farClipPlane = topDownHeight + 100f;

        Vector3 pos = (mainCamera != null ? mainCamera.transform.position : Vector3.zero);
        pos.y = topDownHeight;
        go.transform.SetPositionAndRotation(pos, Quaternion.Euler(90f, 0f, 0f));
        topDownCamera.gameObject.SetActive(false);
    }

    private void SwitchCamera(bool topDown)
    {
        mainCamera?.gameObject.SetActive(!topDown);
        topDownCamera?.gameObject.SetActive(topDown);
    }
}

