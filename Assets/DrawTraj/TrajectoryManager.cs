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

    [Header("Follow Trajectory")]
    [Tooltip("When enabled in review, the robot follows the displayed trajectory at this speed.")]
    [Min(0.1f)] public float followRobotSpeed = 0.6f;

    // ── Public state (read by TrajectoryUI) ──────────────────────────────────
    public bool IsDrawMode { get; private set; }
    public bool IsFollowMode { get; private set; }
    public bool HasFollowTrajectory => _followTrajectoryPoints.Count >= 2 && _followTrajectoryLength > 0.01f;

    // ── Private state ────────────────────────────────────────────────────────
    private TrajectoryRenderer _activeRenderer;
    private readonly List<Vector3> _sessionPoints = new List<Vector3>();
    private TrajectoryCollection _sessionCollection;
    // Renderers drawn live this session — replaced by Traj_Display after save
    private readonly List<TrajectoryRenderer> _sessionRenderers = new List<TrajectoryRenderer>();

    private bool _trajectoriesVisible = true;
    private bool _cameraReady = false;   // false while camera is still flying in
    private bool _wasReviewActive;

    // All renderers that are currently displayed (loaded from saved files).
    private readonly List<TrajectoryRenderer> _displayedRenderers = new List<TrajectoryRenderer>();
    private readonly List<Vector3> _followTrajectoryPoints = new List<Vector3>();
    private float _followTrajectoryLength;

    // ── Unity Lifecycle ──────────────────────────────────────────────────────

    private void Start()
    {
        if (mainCamera == null) mainCamera = Camera.main;
        SetupTopDownCamera();
        _wasReviewActive = IsReviewActive();
        if (_wasReviewActive)
            RefreshDisplay();
    }

    private void Update()
    {
        bool reviewActive = IsReviewActive();
        if (reviewActive && !_wasReviewActive && !IsDrawMode)
            RefreshDisplay();
        _wasReviewActive = reviewActive;

        if (IsDrawMode && !reviewActive)
        {
            ExitDrawMode();
            return;
        }

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
        if (!IsReviewActive()) return;
        if (IsDrawMode) return;
        IsDrawMode = true;
        _cameraReady = false;

        SetVisibility(true);
        _sessionCollection = new TrajectoryCollection();
        SwitchCamera(topDown: true);
        BeginStroke();

        // Fly camera to above the target, then seed the first point
        StartCoroutine(FlyToTarget(() =>
        {
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
        if (!IsReviewActive()) return;
        SetVisibility(!_trajectoriesVisible);
    }

    public void ToggleFollowMode()
    {
        if (!IsReviewActive() || IsDrawMode || !HasFollowTrajectory)
            return;

        IsFollowMode = !IsFollowMode;
    }

    public bool ApplyFollowTrajectoryToRobot(float elapsedSeconds)
    {
        if (!IsFollowMode || !HasFollowTrajectory)
            return false;

        var sean = SEAN.SEAN.instance;
        if (sean == null || sean.robot == null || sean.robot.base_link == null)
            return false;

        if (!TryEvaluateFollowPose(elapsedSeconds, out Vector3 followPosition, out Quaternion followRotation))
            return false;

        Transform robotTransform = sean.robot.base_link.transform;
        followPosition.y = robotTransform.position.y;
        robotTransform.position = followPosition;
        robotTransform.rotation = followRotation;

        Rigidbody rb = robotTransform.GetComponent<Rigidbody>();
        if (rb != null)
        {
            rb.velocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }

        return true;
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
        Camera drawCamera = GetDrawingCamera();
        if (drawCamera == null) { onComplete?.Invoke(); yield break; }

        Vector3 startPos = drawCamera.transform.position;
        float startSize = drawCamera.orthographicSize;

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
            drawCamera.transform.position = Vector3.Lerp(startPos, destPos, t);
            drawCamera.orthographicSize = Mathf.Lerp(startSize, destSize, t);
            elapsed += Time.unscaledDeltaTime;
            yield return null;
        }

        // Snap to exact destination
        drawCamera.transform.position = destPos;
        drawCamera.orthographicSize = destSize;

        onComplete?.Invoke();
    }

    // ── Drawing ──────────────────────────────────────────────────────────────

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
        Camera cam = GetDrawingCamera();
        if (cam == null)
            return;
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
        _activeRenderer.ApplyVisualSettings();
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
        _followTrajectoryPoints.Clear();
        _followTrajectoryLength = 0f;

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
                CaptureFollowTrajectory(data);
                StartCoroutine(SpawnDisplayRenderer(data));
            }
        }

        if (!HasFollowTrajectory)
            IsFollowMode = false;

        _trajectoriesVisible = true;
    }

    private void CaptureFollowTrajectory(TrajectoryData data)
    {
        if (_followTrajectoryPoints.Count > 0 || data == null || data.points == null || data.points.Count < 2)
            return;

        _followTrajectoryPoints.Clear();
        for (int i = 0; i < data.points.Count; i++)
            _followTrajectoryPoints.Add(data.points[i].ToVector3());

        _followTrajectoryLength = 0f;
        for (int i = 1; i < _followTrajectoryPoints.Count; i++)
            _followTrajectoryLength += Vector3.Distance(_followTrajectoryPoints[i - 1], _followTrajectoryPoints[i]);
    }

    private bool TryEvaluateFollowPose(float elapsedSeconds, out Vector3 position, out Quaternion rotation)
    {
        position = Vector3.zero;
        rotation = Quaternion.identity;

        if (!HasFollowTrajectory)
            return false;

        float targetDistance = Mathf.Clamp(Mathf.Max(0f, elapsedSeconds) * followRobotSpeed, 0f, _followTrajectoryLength);
        float traversedDistance = 0f;

        for (int i = 1; i < _followTrajectoryPoints.Count; i++)
        {
            Vector3 from = _followTrajectoryPoints[i - 1];
            Vector3 to = _followTrajectoryPoints[i];
            float segmentLength = Vector3.Distance(from, to);
            if (segmentLength <= 0.0001f)
                continue;

            if (traversedDistance + segmentLength >= targetDistance)
            {
                float segmentT = (targetDistance - traversedDistance) / segmentLength;
                position = Vector3.Lerp(from, to, segmentT);

                Vector3 forward = to - from;
                forward.y = 0f;
                if (forward.sqrMagnitude > 0.0001f)
                    rotation = Quaternion.LookRotation(forward.normalized, Vector3.up);
                return true;
            }

            traversedDistance += segmentLength;
        }

        position = _followTrajectoryPoints[_followTrajectoryPoints.Count - 1];
        Vector3 endForward = _followTrajectoryPoints[_followTrajectoryPoints.Count - 1] - _followTrajectoryPoints[_followTrajectoryPoints.Count - 2];
        endForward.y = 0f;
        if (endForward.sqrMagnitude > 0.0001f)
            rotation = Quaternion.LookRotation(endForward.normalized, Vector3.up);
        return true;
    }

    private IEnumerator SpawnDisplayRenderer(TrajectoryData data)
    {
        var go = new GameObject("Traj_Display");
        go.transform.SetParent(transform);
        var r = go.AddComponent<TrajectoryRenderer>();
        r.lineColor = loadedColor;
        r.ApplyVisualSettings();

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
        var rewind = GetReviewController();
        bool reviewActive = IsReviewActive() && rewind != null;

        if (reviewActive)
        {
            if (topDown)
                rewind.SetPerspective(SessionReview.PerspectiveMode.TopDown);

            if (topDownCamera != null)
                topDownCamera.gameObject.SetActive(false);
            return;
        }

        mainCamera?.gameObject.SetActive(!topDown);
        topDownCamera?.gameObject.SetActive(topDown);
    }

    private Camera GetDrawingCamera()
    {
        if (IsReviewActive())
        {
            var rewind = GetReviewController();
            if (rewind != null)
            {
                Camera reviewCamera = rewind.GetActiveReviewCamera();
                if (reviewCamera != null)
                    return reviewCamera;
            }
        }

        if (topDownCamera != null && topDownCamera.gameObject.activeInHierarchy)
            return topDownCamera;

        return mainCamera;
    }

    private SessionReview.RewindController GetReviewController()
    {
        var reviewManager = SessionReview.SessionReviewManager.Instance;
        if (reviewManager == null)
            return null;

        return reviewManager.GetComponent<SessionReview.RewindController>();
    }

    private static bool IsReviewActive()
    {
        var reviewManager = SessionReview.SessionReviewManager.Instance;
        return reviewManager != null && reviewManager.IsReviewUiActive;
    }
}

