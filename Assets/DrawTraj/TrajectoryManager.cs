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
/// DRAW MODE CONTROLS (iPad):
///   Apple Pencil              — draw / erase (tap DRAW or ERASE to toggle; fingers navigate)
///   One-finger drag           — pan
///   Two-finger pinch / drag   — zoom + pan
///   On-screen buttons         — DRAW · ADD STOP · ERASE · Undo · Clear · Zoom · Finish · Cancel
///   ADD STOP tool             — tap = place / remove · press-drag = move (snaps onto the line)
/// DRAW MODE CONTROLS (desktop):
///   Mouse left-drag           — draw / erase / move stops (while the matching tool is toggled on)
///   Mouse wheel / MMB drag    — zoom / pan (standalone scene only; review supplies its own)
///   ESC                       — finish & save
/// </summary>
public enum PencilDetectionMode
{
    /// <summary>Stylus if the Input System Pen device is pressed, OR Touch.type==Stylus, OR the contact radius is small. Best default.</summary>
    Auto,
    /// <summary>Stylus only if Touch.type reports Stylus (fails on devices that always report Direct).</summary>
    StylusType,
    /// <summary>Stylus if the contact radius is below fingerRadiusThreshold (fingers have a fatter contact).</summary>
    Radius,
    /// <summary>Stylus if the contact reports a real pen pressure (pressure &lt; max, i.e. variable).</summary>
    Pressure,
    /// <summary>Draw only from the Input System Pen device (Apple Pencil); every finger touch navigates.</summary>
    PenDevice
}

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

    [Header("Post-processing")]
    [Tooltip("Drop points whose jump to the prior kept point exceeds median-step * this multiplier. 0 to disable.")]
    [Min(0f)] public float outlierJumpMultiplier = 4f;

    [Tooltip("Moving-average window size for smoothing (odd numbers recommended). <=1 to disable.")]
    [Min(1)] public int smoothingWindow = 3;

    [Tooltip("Number of smoothing passes. 0 to disable.")]
    [Min(0)] public int smoothingPasses = 1;

    [Tooltip("Strokes whose endpoints are closer than this (meters) are connected into one " +
             "trajectory on save/load, so a briefly lifted pen doesn't split the path in review.")]
    [Min(0f)] public float strokeConnectDistance = 2f;

    [Header("Eraser")]
    [Tooltip("Eraser brush radius in screen pixels (world size follows the current zoom).")]
    [Min(1f)] public float eraserRadiusPixels = 28f;

    [Header("Stop Points")]
    [Tooltip("Marker color for stop points placed with the ADD STOP tool.")]
    public Color stopPointColor = new Color(0.9f, 0.15f, 0.15f, 0.95f);

    [Tooltip("Stop-marker disc radius in world meters.")]
    [Min(0.05f)] public float stopPointRadius = 0.4f;

    [Tooltip("Seconds the robot pauses at each stop point while following the drawn trajectory.")]
    [Min(0f)] public float stopPointPauseSeconds = 3f;

    [Tooltip("A stop point placed within this distance (m) of the drawn line snaps onto it. 0 disables snapping.")]
    [Min(0f)] public float stopPointSnapDistance = 1.5f;

    [Header("Follow Trajectory")]
    [Tooltip("Base speed (m/s) the robot uses when following the drawn trajectory.")]
    [Min(0.01f)] public float followRobotSpeed = 0.6f;

    [Tooltip("Runtime multiplier on top of followRobotSpeed (adjustable from UI / keys).")]
    [Range(0.05f, 5f)] public float followSpeedMultiplier = 1f;

    [Tooltip("Min / max allowed values for the runtime speed multiplier.")]
    public float followSpeedMultiplierMin = 0.1f;
    public float followSpeedMultiplierMax = 3f;

    [Header("Touch / iPad Input")]
    [Tooltip("When ON, only an Apple Pencil (stylus) adds trajectory points; fingers are reserved for " +
             "pan/zoom and a resting palm is ignored. Turn OFF to draw with a single finger when no pencil is available.")]
    public bool applePencilOnly = true;

    [Tooltip("How a pencil contact is told apart from a finger. Many iPads report Touch.type as 'Direct' for " +
             "BOTH, so 'Auto'/'Radius' (contact size) is usually the reliable choice. Use the on-screen Touch Debug " +
             "readout to see what your finger vs pencil actually report, then pick the mode + threshold that splits them.")]
    public PencilDetectionMode pencilDetection = PencilDetectionMode.Auto;

    [Tooltip("Contacts with radius BELOW this are treated as a pencil; at/above it as a finger. " +
             "Read the Touch Debug overlay and set this between your pencil radius and your finger radius.")]
    [Min(0f)] public float fingerRadiusThreshold = 8f;

    [Tooltip("Show the live per-touch debug overlay (type / radius / pressure) while drawing, " +
             "to calibrate pencil-vs-finger detection on your device.")]
    public bool touchDebugOverlay = false;

    [Tooltip("Closest zoom-in (smallest orthographic size) allowed while drawing.")]
    [Min(1f)] public float minZoomOrthoSize = 4f;

    [Tooltip("Farthest zoom-out (largest orthographic size) allowed while drawing.")]
    [Min(1f)] public float maxZoomOrthoSize = 120f;

    [Tooltip("Mouse-wheel / Zoom-button step as a fraction of the current zoom (desktop & on-screen buttons).")]
    [Range(0.01f, 0.9f)] public float zoomStepFraction = 0.09f;

    // ── Public state (read by TrajectoryUI) ──────────────────────────────────
    public bool IsDrawMode { get; private set; }
    public bool IsFollowMode { get; private set; }
    public bool HasFollowTrajectory => _followTrajectoryPoints.Count >= 2 && _followTrajectoryLength > 0.01f;
    public float EffectiveFollowSpeed => Mathf.Max(0f, followRobotSpeed * followSpeedMultiplier);

    public float FollowSpeedMultiplier
    {
        get => followSpeedMultiplier;
        set => followSpeedMultiplier = Mathf.Clamp(value, followSpeedMultiplierMin, followSpeedMultiplierMax);
    }

    // ── Touch / draw state (read by TrajectoryUI) ─────────────────────────────
    public bool ApplePencilOnly
    {
        get => applePencilOnly;
        set => applePencilOnly = value;
    }

    /// <summary>True once a contact classified as a pencil has been seen this run.</summary>
    public bool StylusDetected { get; private set; }

    /// <summary>True when there is at least one stroke (finished or in-progress) or stop point to undo.</summary>
    public bool CanUndo => _activeRenderer != null || _sessionRenderers.Count > 0 || _sessionStopPoints.Count > 0;

    /// <summary>Stop points placed so far this draw session (read by TrajectoryUI).</summary>
    public int SessionStopPointCount => _sessionStopPoints.Count;

    public PencilDetectionMode PencilDetection
    {
        get => pencilDetection;
        set => pencilDetection = value;
    }

    public bool ShowTouchDebug
    {
        get => touchDebugOverlay;
        set => touchDebugOverlay = value;
    }

    public float FingerRadiusThreshold => fingerRadiusThreshold;

    /// <summary>Nudge the finger/pencil radius split from an on-screen button (on-device calibration).</summary>
    public void AdjustRadiusThreshold(float delta)
    {
        fingerRadiusThreshold = Mathf.Max(0f, fingerRadiusThreshold + delta);
    }

    /// <summary>Human-readable per-touch readout (type/radius/pressure) for on-screen calibration.</summary>
    public string TouchDebugReadout { get; private set; } = "";

    /// <summary>Cycle the pencil-vs-finger detection strategy (wired to an on-screen button).</summary>
    public void CyclePencilDetection()
    {
        pencilDetection = (PencilDetectionMode)(((int)pencilDetection + 1) % 5);
    }

    // ── Private state ────────────────────────────────────────────────────────
    private TrajectoryRenderer _activeRenderer;
    private readonly List<Vector3> _sessionPoints = new List<Vector3>();
    // Renderers drawn live this session — replaced by Traj_Display after save.
    // The saved collection is built from these at save time (the eraser can split
    // or delete strokes mid-session, so no incremental collection is kept).
    private readonly List<TrajectoryRenderer> _sessionRenderers = new List<TrajectoryRenderer>();

    // Stop points placed with the ADD STOP tool this draw session, and their markers
    // (index-aligned). Displayed markers are the ones respawned from the saved file.
    private readonly List<Vector3> _sessionStopPoints = new List<Vector3>();
    private readonly List<GameObject> _sessionStopMarkers = new List<GameObject>();
    private readonly List<GameObject> _displayedStopMarkers = new List<GameObject>();
    // Stop points loaded from the newest saved session. They are imported into the
    // session on EnterDrawMode so existing stops can be moved / deleted like fresh ones.
    private readonly List<Vector3> _loadedStopPoints = new List<Vector3>();
    private bool _stopsEdited;           // session changed the stop set (place/move/delete/clear)

    // Legend row for the drawn trajectory in the review Legend panel (bottom-right).
    private const string DrawnLegendKey = "drawn_trajectory";

    private bool _trajectoriesVisible = true;
    private bool _cameraReady = false;   // false while camera is still flying in
    private bool _wasReviewActive;

    // All renderers that are currently displayed (loaded from saved files).
    private readonly List<TrajectoryRenderer> _displayedRenderers = new List<TrajectoryRenderer>();
    private readonly List<Vector3> _followTrajectoryPoints = new List<Vector3>();
    private float _followTrajectoryLength;
    private float _followDistance;      // arc-length already traversed
    private float _followLastElapsed;   // last elapsedSeconds we saw
    private bool _followSessionActive;  // accumulator initialised?
    // Arc-length positions of the saved stop points along the follow polyline (sorted),
    // the next one ahead of the robot, and the pause time left at the current stop.
    private readonly List<float> _followStopDistances = new List<float>();
    private int _followNextStopIndex;
    private float _followPauseRemaining;

    // Touch navigation / stroke edge-detection
    private TrajectoryUI _ui;
    private bool _strokeDown;            // a drawing contact is currently pressed
    private bool _stopTapDown;           // a stop-tool contact is currently pressed
    // Stop-tool gesture: the stop grabbed at press (-1 = none), whether the contact
    // travelled far enough to count as a drag, and whether the press placed the stop.
    private int _stopDragIndex = -1;
    private bool _stopDragMoved;
    private bool _stopJustPlaced;
    private Vector2 _stopPressScreen;
    private const float StopDragThresholdPx = 14f; // pixels before a tap becomes a drag
    private bool _navActive;            // a finger pan/zoom gesture is in progress
    private int _navFingerCount;        // fingers used by the active nav gesture
    private Vector2 _lastNavCentroid;
    private float _lastNavPinchDist;
    private bool _mousePanning;          // desktop middle-mouse pan (standalone scene)
    private Vector2 _lastMousePanPos;
    private bool _lmbPanning;            // disarmed left-mouse drag pans instead of drawing
    private Vector2 _lastLmbPanPos;

    // ── Unity Lifecycle ──────────────────────────────────────────────────────

    private void Start()
    {
        if (mainCamera == null) mainCamera = Camera.main;
        if (_ui == null) _ui = GetComponent<TrajectoryUI>() ?? FindObjectOfType<TrajectoryUI>();
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
        // Review UI gone (world building / gameplay) — the drawn lines must not
        // linger over those modes.
        if (!reviewActive && _wasReviewActive && !IsDrawMode)
            SetVisibility(false);
        _wasReviewActive = reviewActive;

        if (IsDrawMode && !reviewActive)
        {
            ExitDrawMode();
            SetVisibility(false);
            return;
        }

        if (!IsDrawMode)
        {
            if (reviewActive)
                SyncLegendVisibility();
            return;
        }

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
        _strokeDown = false;
        _stopTapDown = false;
        _navActive = false;
        _mousePanning = false;
        _lmbPanning = false;
        ClearSessionStopPoints();

        // The saved stop points join the session so they can be moved / deleted like
        // fresh ones; their display markers are replaced by editable session markers.
        foreach (var p in _loadedStopPoints)
        {
            _sessionStopPoints.Add(p);
            _sessionStopMarkers.Add(CreateStopMarker(p));
        }
        ClearDisplayedStopMarkers();
        _stopsEdited = false;

        SetVisibility(true);
        SwitchCamera(topDown: true);

        // A stroke now begins on the first pen-down so multiple strokes + Undo work.
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
        _strokeDown = false;
        _navActive = false;

        EndStroke();      // finalise an in-progress stroke (null-safe)
        SaveSession();
        SwitchCamera(topDown: false);
        RefreshDisplay();

        // A freshly saved drawing must be visible even if the Legend row was
        // toggled off during a previous review pass.
        GetLegendRenderer()?.SetExternalGroupVisible(DrawnLegendKey, true);
    }

    /// <summary>Remove the most recent stroke (or the in-progress one) from this session.</summary>
    public void UndoLastStroke()
    {
        if (!IsDrawMode) return;

        // With the STOP tool armed, Undo removes the most recent stop point instead.
        if (_ui != null && _ui.StopInputArmed && _sessionStopPoints.Count > 0)
        {
            CancelStopGesture();
            int lastStop = _sessionStopPoints.Count - 1;
            _sessionStopPoints.RemoveAt(lastStop);
            if (lastStop < _sessionStopMarkers.Count)
            {
                if (_sessionStopMarkers[lastStop] != null)
                    Destroy(_sessionStopMarkers[lastStop]);
                _sessionStopMarkers.RemoveAt(lastStop);
            }
            _stopsEdited = true;
            return;
        }

        // An in-progress stroke is discarded first.
        if (_activeRenderer != null)
        {
            Destroy(_activeRenderer.gameObject);
            _activeRenderer = null;
            _strokeDown = false;
            return;
        }

        int last = _sessionRenderers.Count - 1;
        if (last < 0) return;

        if (_sessionRenderers[last] != null)
            Destroy(_sessionRenderers[last].gameObject);
        _sessionRenderers.RemoveAt(last);
    }

    /// <summary>Discard every stroke drawn so far this session (stays in draw mode).</summary>
    public void ClearCurrentSession()
    {
        if (!IsDrawMode) return;

        if (_activeRenderer != null)
        {
            Destroy(_activeRenderer.gameObject);
            _activeRenderer = null;
            _strokeDown = false;
        }
        foreach (var r in _sessionRenderers)
            if (r != null) Destroy(r.gameObject);
        _sessionRenderers.Clear();
        if (_sessionStopPoints.Count > 0)
            _stopsEdited = true;
        ClearSessionStopPoints();
    }

    /// <summary>Leave draw mode WITHOUT saving — discards everything drawn this session.</summary>
    public void CancelDrawMode()
    {
        if (!IsDrawMode) return;

        IsDrawMode = false;
        _cameraReady = false;
        _strokeDown = false;
        _navActive = false;

        if (_activeRenderer != null)
        {
            Destroy(_activeRenderer.gameObject);
            _activeRenderer = null;
        }
        foreach (var r in _sessionRenderers)
            if (r != null) Destroy(r.gameObject);
        _sessionRenderers.Clear();
        ClearSessionStopPoints();

        SwitchCamera(topDown: false);
        RefreshDisplay();
    }

    /// <summary>Toggle visibility of all currently displayed trajectories.</summary>
    public void ToggleVisibility()
    {
        if (!IsReviewActive()) return;
        SetVisibility(!_trajectoriesVisible);
        // Keep the Legend row (bottom-right panel) in agreement with this button.
        GetLegendRenderer()?.SetExternalGroupVisible(DrawnLegendKey, _trajectoriesVisible);
    }

    /// <summary>
    /// Mirrors the drawn-trajectory visibility with its row in the review Legend
    /// panel (bottom-right). Registers the row lazily — the Legend rebuilds its
    /// entries every time a review is (re)entered, so a one-shot registration
    /// could be wiped; re-registering when the entry is missing self-heals that.
    /// </summary>
    private void SyncLegendVisibility()
    {
        if (_displayedRenderers.Count == 0)
            return;

        var legend = GetLegendRenderer();
        if (legend == null)
            return;

        if (!legend.HasLegendEntry(DrawnLegendKey))
        {
            if (!legend.IsShowing)
                return; // legend not built yet — register once it is
            legend.RegisterExternalLegendGroup(DrawnLegendKey, "Drawn Trajectory",
                loadedColor, initiallyVisible: _trajectoriesVisible);
        }

        bool visible = legend.IsExternalGroupVisible(DrawnLegendKey);
        if (visible != _trajectoriesVisible)
            SetVisibility(visible);
    }

    private SessionReview.MultiAgentTrajectoryRenderer GetLegendRenderer()
    {
        var reviewManager = SessionReview.SessionReviewManager.Instance;
        return reviewManager != null
            ? reviewManager.GetComponent<SessionReview.MultiAgentTrajectoryRenderer>()
            : null;
    }

    public void ToggleFollowMode()
    {
        if (!IsReviewActive() || IsDrawMode || !HasFollowTrajectory)
            return;

        IsFollowMode = !IsFollowMode;
        _followSessionActive = false;
    }

    public string LastFollowSkipReason { get; private set; } = "";
    public float LastFollowDistance => _followDistance;
    public float LastFollowElapsed => _followLastElapsed;

    public bool ReviewIsPlaying
    {
        get
        {
            var rc = GetReviewController();
            return rc != null && rc.IsPlaying;
        }
    }
    public float ReviewPlaybackSpeed
    {
        get
        {
            var rc = GetReviewController();
            return rc != null ? rc.PlaybackSpeed : 0f;
        }
    }
    public float ReviewNormalizedTime
    {
        get
        {
            var rc = GetReviewController();
            return rc != null ? rc.NormalizedTime : 0f;
        }
    }
    public int ReviewToggleCount
    {
        get
        {
            var rc = GetReviewController();
            return rc != null ? rc.TogglePlayPauseCount : 0;
        }
    }

    /// <summary>
    /// The path currently being drawn, for the live Ghost-Robot preview while in draw
    /// mode: the in-progress stroke if one is active, else the most recent finished
    /// stroke this session. Returns false when nothing has been drawn yet (callers then
    /// fall back to the saved follow-trajectory).
    /// </summary>
    public bool TryGetLiveDrawnPath(out Vector3[] points)
    {
        points = null;

        if (_activeRenderer != null && _activeRenderer.Points != null && _activeRenderer.Points.Count >= 2)
        {
            points = _activeRenderer.Points.ToArray();
            return true;
        }

        for (int i = _sessionRenderers.Count - 1; i >= 0; i--)
        {
            var r = _sessionRenderers[i];
            if (r != null && r.Points != null && r.Points.Count >= 2)
            {
                points = r.Points.ToArray();
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The saved follow-trajectory polyline (world points). Used by the review
    /// Ghost-Robot comparison to place and drag a ghost along the drawn path.
    /// Read-only: does not move the real robot or require Follow mode to be on.
    /// </summary>
    public bool TryGetFollowPathPoints(out Vector3[] points)
    {
        points = null;
        if (!HasFollowTrajectory)
            return false;

        points = _followTrajectoryPoints.ToArray();
        return true;
    }

    /// <summary>
    /// Evaluates a pose along the drawn follow-trajectory at a normalized progress
    /// (0 = start, 1 = end). Read-only: does not move the real robot or require
    /// Follow mode to be on.
    /// </summary>
    public bool TryEvaluateFollowPoseAtNormalized(float normalized, out Vector3 position, out Quaternion rotation)
    {
        position = Vector3.zero;
        rotation = Quaternion.identity;
        if (!HasFollowTrajectory)
            return false;

        float distance = Mathf.Clamp01(normalized) * _followTrajectoryLength;
        return TryEvaluateFollowPoseAtDistance(distance, out position, out rotation);
    }

    public bool ApplyFollowTrajectoryToRobot(float elapsedSeconds)
    {
        if (!IsFollowMode)
        {
            LastFollowSkipReason = "IsFollowMode=false";
            _followSessionActive = false;
            return false;
        }
        if (!HasFollowTrajectory)
        {
            LastFollowSkipReason = $"HasFollowTrajectory=false (pts={_followTrajectoryPoints.Count} len={_followTrajectoryLength:F2})";
            _followSessionActive = false;
            LogFollowSkipOnce();
            return false;
        }

        var sean = SEAN.SEAN.instance;
        if (sean == null || sean.robot == null || sean.robot.base_link == null)
        {
            LastFollowSkipReason = "SEAN robot/base_link null";
            LogFollowSkipOnce();
            return false;
        }

        // Accumulate distance from delta elapsed × current speed so speed changes
        // take effect from the current point onward (no position jump on slider).
        if (!_followSessionActive)
        {
            _followSessionActive = true;
            _followDistance = 0f;
            _followLastElapsed = Mathf.Max(0f, elapsedSeconds);
            _followNextStopIndex = 0;
            _followPauseRemaining = 0f;
        }
        else
        {
            float dt = elapsedSeconds - _followLastElapsed;
            _followLastElapsed = elapsedSeconds;
            if (dt < 0f) // review scrubbed backwards — reset
            {
                _followDistance = 0f;
                _followNextStopIndex = 0;
                _followPauseRemaining = 0f;
            }
            else
            {
                // A stop point currently holds the robot: consume the pause first,
                // then spend whatever time is left moving again.
                if (_followPauseRemaining > 0f)
                {
                    float paused = Mathf.Min(dt, _followPauseRemaining);
                    _followPauseRemaining -= paused;
                    dt -= paused;
                }

                if (dt > 0f)
                {
                    float target = _followDistance + dt * EffectiveFollowSpeed;

                    // Skip stops at or behind the current position so a stop is
                    // honoured exactly once per pass.
                    while (_followNextStopIndex < _followStopDistances.Count &&
                           _followStopDistances[_followNextStopIndex] <= _followDistance)
                        _followNextStopIndex++;

                    if (_followNextStopIndex < _followStopDistances.Count &&
                        target >= _followStopDistances[_followNextStopIndex])
                    {
                        target = _followStopDistances[_followNextStopIndex];
                        _followNextStopIndex++;
                        _followPauseRemaining = stopPointPauseSeconds;
                    }

                    _followDistance = target;
                }
            }
        }
        _followDistance = Mathf.Clamp(_followDistance, 0f, _followTrajectoryLength);

        if (!TryEvaluateFollowPoseAtDistance(_followDistance, out Vector3 followPosition, out Quaternion followRotation))
        {
            LastFollowSkipReason = "TryEvaluateFollowPoseAtDistance failed";
            LogFollowSkipOnce();
            return false;
        }

        LastFollowSkipReason = "";

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
        foreach (var m in _displayedStopMarkers)
            if (m != null) m.SetActive(_trajectoriesVisible);
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
        BuildTouchDebug();

        bool stylusDown = false;
        Vector2 stylusPos = Vector2.zero;

        // Apple Pencil exposed through the Input System Pen device (separate from touches).
        // Honoured in Auto and in the dedicated PenDevice mode.
#if ENABLE_INPUT_SYSTEM
        if (pencilDetection == PencilDetectionMode.PenDevice || pencilDetection == PencilDetectionMode.Auto)
        {
            var pen = UnityEngine.InputSystem.Pen.current;
            if (pen != null && pen.tip.isPressed)
            {
                Vector2 penPos = pen.position.ReadValue();
                if (!IsBlockedByUI(penPos))
                {
                    stylusDown = true;
                    stylusPos = penPos;
                    StylusDetected = true;
                }
            }
        }
#endif

        Vector2 finger0 = Vector2.zero, finger1 = Vector2.zero;
        int fingerCount = 0;

        for (int i = 0; i < Input.touchCount; i++)
        {
            Touch t = Input.GetTouch(i);
            bool ended = t.phase == TouchPhase.Ended || t.phase == TouchPhase.Canceled;

            if (TouchIsStylus(t))
            {
                StylusDetected = true;
                if (!ended && !IsBlockedByUI(t.position))
                {
                    stylusDown = true;
                    stylusPos = t.position;
                }
                continue;
            }

            // Finger touch — ignore lifted touches and taps that land on the controls.
            if (ended || IsBlockedByUI(t.position))
                continue;

            if (fingerCount == 0) finger0 = t.position;
            else if (fingerCount == 1) finger1 = t.position;
            fingerCount++;
        }

        // ── Resolve the drawing contact ───────────────────────────────────────
        // Apple Pencil always draws. A finger only draws when pencil-only is OFF.
        // While the pencil is down, fingers are ignored entirely (palm rejection).
        bool drawDown = false;
        Vector2 drawPos = Vector2.zero;

        if (stylusDown)
        {
            drawDown = true;
            drawPos = stylusPos;
        }
        else if (!applePencilOnly && fingerCount == 1)
        {
            drawDown = true;
            drawPos = finger0;
        }
        else if (Input.touchCount == 0 && Input.GetMouseButton(0) && !IsBlockedByUI(Input.mousePosition))
        {
            drawDown = true;
            drawPos = Input.mousePosition;
        }

        // ── DRAW / STOP / ERASE gate ──────────────────────────────────────────
        // Strokes only land while the bottom-left DRAW toggle is on; with ADD STOP
        // on, a tap places / removes a stop marker and a press-drag moves one, and
        // with ERASE on the same contact erases. Disarmed contacts navigate only,
        // so panning around never scribbles by accident.
        bool stopArmed = _ui != null && _ui.StopInputArmed;
        bool drawArmed = !stopArmed && (_ui == null || _ui.DrawInputArmed);
        bool eraseArmed = !stopArmed && _ui != null && _ui.EraseInputArmed;
        bool armed = drawArmed || eraseArmed || stopArmed;
        if (!armed)
            drawDown = false;

        // A tool switch mid-contact abandons the stop gesture so lifting the contact
        // later can never delete the stop that was grabbed under the old tool.
        if (!stopArmed)
            CancelStopGesture();

        // ── Stroke begin / continue / end (rising & falling edges) ────────────
        if (drawDown && stopArmed)
        {
            if (_strokeDown)
            {
                _strokeDown = false;
                EndStroke();
            }
            if (!_stopTapDown)
            {
                _stopTapDown = true;
                StopToolPress(drawPos);
            }
            else
            {
                StopToolDrag(drawPos);
            }
        }
        else if (drawDown && eraseArmed)
        {
            if (_strokeDown)
            {
                _strokeDown = false;
                EndStroke();
            }
            EraseAtScreen(drawPos);
        }
        else if (drawDown)
        {
            if (!_strokeDown)
            {
                _strokeDown = true;
                BeginStroke();
            }
            TryAddPointFromScreen(drawPos);
        }
        else if (_strokeDown)
        {
            _strokeDown = false;
            EndStroke();
        }

        if (!drawDown)
        {
            if (_stopTapDown)
                StopToolRelease();
            _stopTapDown = false;
        }

        // ── Finger pan / pinch-zoom (never while the pencil is drawing) ───────
        // While disarmed a single finger always pans, whatever the pencil mode.
        bool canNavigate = !stylusDown &&
                           (fingerCount >= 2 || ((applePencilOnly || !armed) && fingerCount == 1));
        if (canNavigate)
            HandleTouchNavigation(fingerCount, finger0, finger1);
        else
            _navActive = false;

        // ── Disarmed left-mouse drag pans the draw camera (desktop) ───────────
        if (!armed && Input.touchCount == 0)
        {
            if (Input.GetMouseButtonDown(0) && !IsBlockedByUI(Input.mousePosition))
            {
                _lmbPanning = true;
                _lastLmbPanPos = Input.mousePosition;
            }
            if (!Input.GetMouseButton(0))
                _lmbPanning = false;
            if (_lmbPanning)
            {
                Vector2 cur = Input.mousePosition;
                PanDrawCamera(cur - _lastLmbPanPos);
                _lastLmbPanPos = cur;
            }
        }
        else
        {
            _lmbPanning = false;
        }

        // ── Desktop pan / zoom of the draw camera (wheel + middle mouse) ──────
        // SessionReviewManager skips its own top-down mouse input while draw mode
        // is active, so this is the only handler and never double-zooms.
        if (Input.touchCount == 0)
            HandleMouseNavigation();
    }

    private void HandleTouchNavigation(int fingerCount, Vector2 finger0, Vector2 finger1)
    {
        if (fingerCount >= 2)
        {
            Vector2 centroid = (finger0 + finger1) * 0.5f;
            float dist = Vector2.Distance(finger0, finger1);

            // (Re)seed the gesture when it starts or the finger count changes.
            if (!_navActive || _navFingerCount < 2)
            {
                _navActive = true;
                _navFingerCount = 2;
                _lastNavCentroid = centroid;
                _lastNavPinchDist = dist;
                return;
            }

            Vector2 centroidDelta = centroid - _lastNavCentroid;
            if (centroidDelta.sqrMagnitude > 0f)
                PanDrawCamera(centroidDelta);

            // Pinch out (fingers apart -> dist up -> ratio < 1) zooms in, anchored at the centroid.
            if (dist > 1f && _lastNavPinchDist > 1f)
                ZoomDrawCamera(centroid, _lastNavPinchDist / dist);

            _lastNavCentroid = centroid;
            _lastNavPinchDist = dist;
        }
        else // single-finger pan (pencil-only mode)
        {
            if (!_navActive || _navFingerCount != 1)
            {
                _navActive = true;
                _navFingerCount = 1;
                _lastNavCentroid = finger0;
                return;
            }

            PanDrawCamera(finger0 - _lastNavCentroid);
            _lastNavCentroid = finger0;
        }
    }

    private void HandleMouseNavigation()
    {
        float scroll = Input.mouseScrollDelta.y;
        if (Mathf.Abs(scroll) > 0.01f && !IsBlockedByUI(Input.mousePosition))
        {
            // Scale by the actual scroll amount so smooth-scrolling mice, which spread one
            // notch over several frames, do not compound a full step every frame.
            ZoomDrawCamera(Input.mousePosition, Mathf.Pow(1f - zoomStepFraction, scroll));
        }

        if (Input.GetMouseButtonDown(2) && !IsBlockedByUI(Input.mousePosition))
        {
            _mousePanning = true;
            _lastMousePanPos = Input.mousePosition;
        }
        if (_mousePanning && Input.GetMouseButton(2))
        {
            Vector2 cur = Input.mousePosition;
            PanDrawCamera(cur - _lastMousePanPos);
            _lastMousePanPos = cur;
        }
        if (Input.GetMouseButtonUp(2))
            _mousePanning = false;
    }

    // ── Camera pan / zoom (orthographic top-down draw camera) ─────────────────
    private void PanDrawCamera(Vector2 screenDelta)
    {
        Camera cam = GetDrawingCamera();
        if (cam == null || !cam.orthographic)
            return;

        float worldPerPixel = (cam.orthographicSize * 2f) / Mathf.Max(Screen.height, 1f);
        // Top-down camera (rot 90,0,0): screen +X -> world +X, screen +Y -> world +Z.
        // Drag the content under the finger => move the camera opposite to the delta.
        cam.transform.position += new Vector3(-screenDelta.x * worldPerPixel, 0f, -screenDelta.y * worldPerPixel);
    }

    private void ZoomDrawCamera(Vector2 screenAnchor, float zoomMultiplier)
    {
        Camera cam = GetDrawingCamera();
        if (cam == null || !cam.orthographic)
            return;

        bool haveBefore = TryGetGroundPoint(cam, screenAnchor, out Vector3 before);

        float maxSize = Mathf.Max(minZoomOrthoSize, maxZoomOrthoSize);
        cam.orthographicSize = Mathf.Clamp(cam.orthographicSize * zoomMultiplier, minZoomOrthoSize, maxSize);

        // Keep the world point under the anchor fixed (zoom toward the finger / cursor).
        if (haveBefore && TryGetGroundPoint(cam, screenAnchor, out Vector3 after))
        {
            Vector3 delta = before - after;
            delta.y = 0f;
            cam.transform.position += delta;
        }
    }

    private static bool TryGetGroundPoint(Camera cam, Vector2 screenPoint, out Vector3 groundPoint)
    {
        groundPoint = Vector3.zero;
        Ray ray = cam.ScreenPointToRay(screenPoint);
        Plane plane = new Plane(Vector3.up, Vector3.zero);
        if (!plane.Raycast(ray, out float enter))
            return false;
        groundPoint = ray.GetPoint(enter);
        return true;
    }

    private bool IsBlockedByUI(Vector2 screenPos)
    {
        if (_ui != null && _ui.BlocksInputAt(screenPos))
            return true;

        // Review overlay panels (Legend, Metrics, ...) and the replay scrubber stay
        // usable while drawing so the stroke can be compared against the trial
        // trajectories at a chosen replay point. Touches/clicks on them must operate
        // the control only — never also paint a stroke or pan.
        Vector2 guiPoint = SessionReview.ReviewUiScale.ScreenToGui(screenPos);
        if (SessionReview.ReviewPanels.AnyPanelContains(guiPoint))
            return true;
        if (SessionReview.UiScaleController.ControlContains(guiPoint))
            return true;
        if (SessionReview.RosOverlayVisibility.ControlContains(guiPoint))
            return true;

        var rc = GetReviewController();
        return rc != null && rc.ProgressBarContains(guiPoint);
    }

    /// <summary>
    /// Decide whether a touch came from the Apple Pencil. Touch.type is unreliable on
    /// many iPads (reports Direct for both finger and pencil), so Auto/Radius/Pressure
    /// fall back to physical contact characteristics. Calibrate with the Touch Debug overlay.
    /// </summary>
    private bool TouchIsStylus(Touch t)
    {
        switch (pencilDetection)
        {
            case PencilDetectionMode.PenDevice:
                // The pencil is read from the Pen device; every touch here is a finger.
                return false;

            case PencilDetectionMode.StylusType:
                return t.type == TouchType.Stylus;

            case PencilDetectionMode.Radius:
                return t.radius > 0f && t.radius < fingerRadiusThreshold;

            case PencilDetectionMode.Pressure:
                // A pencil reports a variable pressure below its max; a finger usually pegs at max (or 0).
                return t.maximumPossiblePressure > 0f &&
                       t.pressure > 0.001f &&
                       t.pressure < t.maximumPossiblePressure - 0.001f;

            default: // Auto
                return t.type == TouchType.Stylus ||
                       (t.radius > 0f && t.radius < fingerRadiusThreshold);
        }
    }

    private void BuildTouchDebug()
    {
        if (!touchDebugOverlay)
        {
            TouchDebugReadout = "";
            return;
        }

        var sb = new System.Text.StringBuilder();
        string pen = "n/a (legacy Input)";
#if ENABLE_INPUT_SYSTEM
        var penDev = UnityEngine.InputSystem.Pen.current;
        pen = penDev != null
            ? $"present tip={penDev.tip.isPressed} p={penDev.pressure.ReadValue():F2}"
            : "none";
#endif
        sb.Append($"Mode={pencilDetection}  radiusThr={fingerRadiusThreshold:F1}  touches={Input.touchCount}\nPen device: {pen}");

        for (int i = 0; i < Input.touchCount; i++)
        {
            Touch t = Input.GetTouch(i);
            string tag = TouchIsStylus(t) ? "PENCIL" : "finger";
            sb.Append($"\n#{i} {tag} | type={t.type} r={t.radius:F1}±{t.radiusVariance:F1} " +
                      $"press={t.pressure:F2}/{t.maximumPossiblePressure:F2} alt={t.altitudeAngle:F2}");
        }

        TouchDebugReadout = sb.ToString();
    }

    /// <summary>Step zoom from an on-screen button (zooms about the screen centre).</summary>
    public void ZoomStep(bool zoomIn)
    {
        if (!IsDrawMode) return;
        Vector2 center = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
        ZoomDrawCamera(center, zoomIn ? (1f - zoomStepFraction) : (1f + zoomStepFraction));
    }

    private void TryAddPointFromScreen(Vector2 screenPos)
    {
        if (!TryGetDrawSurfacePoint(screenPos, out Vector3 point))
            return;

        _activeRenderer?.AddPoint(point);
        _sessionPoints.Add(point);
    }

    /// <summary>Project a screen position onto the drawing surface (ground mesh, or a flat plane fallback).</summary>
    private bool TryGetDrawSurfacePoint(Vector2 screenPos, out Vector3 point)
    {
        point = Vector3.zero;
        Camera cam = GetDrawingCamera();
        if (cam == null)
            return false;
        Ray ray = cam.ScreenPointToRay(screenPos);

        if (Physics.Raycast(ray, out RaycastHit hit, 1000f, groundLayer))
        {
            point = hit.point + Vector3.up * heightOffset;
            return true;
        }

        // Fallback: flat plane at heightOffset
        Plane ground = new Plane(Vector3.up, Vector3.up * heightOffset);
        if (!ground.Raycast(ray, out float dist))
            return false;
        point = ray.GetPoint(dist);
        return true;
    }

    /// <summary>
    /// Erase every session-stroke point within the eraser brush of the given screen
    /// position. A stroke can lose its ends, be split into several pieces, or be
    /// removed entirely; pieces left with fewer than 2 points are dropped.
    /// </summary>
    private void EraseAtScreen(Vector2 screenPos)
    {
        if (!TryGetDrawSurfacePoint(screenPos, out Vector3 center))
            return;

        Camera cam = GetDrawingCamera();
        float worldPerPixel = (cam != null && cam.orthographic)
            ? (cam.orthographicSize * 2f) / Mathf.Max(Screen.height, 1f)
            : 0.02f;
        float radius = Mathf.Max(0.05f, eraserRadiusPixels * worldPerPixel);
        float sqrRadius = radius * radius;

        for (int i = _sessionRenderers.Count - 1; i >= 0; i--)
        {
            var r = _sessionRenderers[i];
            if (r == null) { _sessionRenderers.RemoveAt(i); continue; }

            List<Vector3> pts = r.Points;
            bool removedAny = false;
            var runs = new List<List<Vector3>>();
            List<Vector3> current = null;
            for (int p = 0; p < pts.Count; p++)
            {
                Vector3 d = pts[p] - center;
                d.y = 0f; // brush is a vertical cylinder — height offset must not matter
                if (d.sqrMagnitude <= sqrRadius)
                {
                    removedAny = true;
                    current = null;
                    continue;
                }
                if (current == null)
                {
                    current = new List<Vector3>();
                    runs.Add(current);
                }
                current.Add(pts[p]);
            }

            if (!removedAny)
                continue;

            runs.RemoveAll(run => run.Count < 2);
            if (runs.Count == 0)
            {
                Destroy(r.gameObject);
                _sessionRenderers.RemoveAt(i);
                continue;
            }

            r.ReplacePoints(runs[0]);
            for (int k = 1; k < runs.Count; k++)
                _sessionRenderers.Insert(i + k, CreateSessionStroke(runs[k]));
        }
    }

    // ── Stop Points ──────────────────────────────────────────────────────────

    /// <summary>
    /// ADD STOP press: grab the stop under the contact (a later drag moves it, a plain
    /// tap removes it on release), or place a new one (snapped onto the drawn line).
    /// A just-placed stop is grabbed too, so the same gesture can fine-tune its position.
    /// </summary>
    private void StopToolPress(Vector2 screenPos)
    {
        CancelStopGesture();
        _stopPressScreen = screenPos;

        if (!TryGetDrawSurfacePoint(screenPos, out Vector3 point))
            return;

        float grabRadius = Mathf.Max(stopPointRadius * 1.5f, 0.5f);
        float grabSqr = grabRadius * grabRadius;
        for (int i = _sessionStopPoints.Count - 1; i >= 0; i--)
        {
            Vector3 d = _sessionStopPoints[i] - point;
            d.y = 0f;
            if (d.sqrMagnitude <= grabSqr)
            {
                _stopDragIndex = i;
                return;
            }
        }

        point = SnapToDrawnLine(point);
        _sessionStopPoints.Add(point);
        _sessionStopMarkers.Add(CreateStopMarker(point));
        _stopDragIndex = _sessionStopPoints.Count - 1;
        _stopJustPlaced = true; // releasing without moving must not delete it
        _stopsEdited = true;
    }

    /// <summary>Drag the grabbed stop point along under the contact (snapping onto the line).</summary>
    private void StopToolDrag(Vector2 screenPos)
    {
        if (_stopDragIndex < 0 || _stopDragIndex >= _sessionStopPoints.Count)
        {
            _stopDragIndex = -1;
            return;
        }

        // A tap only becomes a drag past a small threshold so removal taps don't jitter the marker.
        if (!_stopDragMoved && (screenPos - _stopPressScreen).magnitude < StopDragThresholdPx)
            return;

        if (!TryGetDrawSurfacePoint(screenPos, out Vector3 point))
            return;

        _stopDragMoved = true;
        _stopsEdited = true;
        point = SnapToDrawnLine(point);
        _sessionStopPoints[_stopDragIndex] = point;
        if (_stopDragIndex < _sessionStopMarkers.Count && _sessionStopMarkers[_stopDragIndex] != null)
            _sessionStopMarkers[_stopDragIndex].transform.position = point + Vector3.up * 0.02f;
    }

    /// <summary>Contact lifted: a plain tap on an existing stop (no drag, not just placed) removes it.</summary>
    private void StopToolRelease()
    {
        int index = _stopDragIndex;
        bool moved = _stopDragMoved;
        bool placed = _stopJustPlaced;
        CancelStopGesture();

        if (index < 0 || moved || placed || index >= _sessionStopPoints.Count)
            return;

        _sessionStopPoints.RemoveAt(index);
        if (index < _sessionStopMarkers.Count)
        {
            if (_sessionStopMarkers[index] != null)
                Destroy(_sessionStopMarkers[index]);
            _sessionStopMarkers.RemoveAt(index);
        }
        _stopsEdited = true;
    }

    private void CancelStopGesture()
    {
        _stopDragIndex = -1;
        _stopDragMoved = false;
        _stopJustPlaced = false;
    }

    /// <summary>
    /// Snaps a tapped point onto the nearest drawn line point — this session's strokes
    /// first, else the displayed saved trajectory (so stops can be added to an existing
    /// drawing in a later session) — when within stopPointSnapDistance.
    /// </summary>
    private Vector3 SnapToDrawnLine(Vector3 point)
    {
        if (stopPointSnapDistance <= 0f)
            return point;

        float bestSqr = stopPointSnapDistance * stopPointSnapDistance;
        Vector3 best = point;
        bool found = false;

        void Consider(List<Vector3> pts)
        {
            if (pts == null) return;
            foreach (var p in pts)
            {
                Vector3 d = p - point;
                d.y = 0f;
                if (d.sqrMagnitude < bestSqr)
                {
                    bestSqr = d.sqrMagnitude;
                    best = p;
                    found = true;
                }
            }
        }

        foreach (var r in _sessionRenderers)
            if (r != null) Consider(r.Points);
        if (_activeRenderer != null)
            Consider(_activeRenderer.Points);
        if (!found)
            foreach (var r in _displayedRenderers)
                if (r != null) Consider(r.Points);

        return found ? best : point;
    }

    /// <summary>Red STOP disc + label, readable from the top-down draw camera.</summary>
    private GameObject CreateStopMarker(Vector3 point)
    {
        var root = new GameObject("StopPoint_Marker");
        root.transform.SetParent(transform);
        root.transform.position = point + Vector3.up * 0.02f;

        var disc = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        Destroy(disc.GetComponent<Collider>()); // must never block the ground raycasts
        disc.name = "Disc";
        disc.transform.SetParent(root.transform, false);
        disc.transform.localScale = new Vector3(stopPointRadius * 2f, 0.02f, stopPointRadius * 2f);
        var rend = disc.GetComponent<Renderer>();
        rend.material = new Material(Shader.Find("Sprites/Default")) { color = stopPointColor };
        rend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        rend.receiveShadows = false;

        var labelGo = new GameObject("Label");
        labelGo.transform.SetParent(root.transform, false);
        labelGo.transform.localPosition = new Vector3(0f, 0.06f, 0f);
        // Lies flat on the ground with its readable face up and text-up along +Z,
        // matching the top-down draw camera (screen up = world +Z).
        labelGo.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
        var label = labelGo.AddComponent<TextMesh>();
        label.text = "STOP";
        label.anchor = TextAnchor.MiddleCenter;
        label.alignment = TextAlignment.Center;
        label.fontSize = 64;
        label.characterSize = stopPointRadius * 0.1f;
        label.fontStyle = FontStyle.Bold;
        label.color = Color.white;
        // A runtime-created TextMesh has no font assigned and renders nothing without one.
        Font font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        if (font != null)
        {
            label.font = font;
            labelGo.GetComponent<MeshRenderer>().material = font.material;
        }

        return root;
    }

    private void ClearSessionStopPoints()
    {
        CancelStopGesture();
        _sessionStopPoints.Clear();
        foreach (var m in _sessionStopMarkers)
            if (m != null) Destroy(m);
        _sessionStopMarkers.Clear();
    }

    private void ClearDisplayedStopMarkers()
    {
        foreach (var m in _displayedStopMarkers)
            if (m != null) Destroy(m);
        _displayedStopMarkers.Clear();
    }

    /// <summary>
    /// Projects each saved stop point onto the follow polyline and stores its
    /// arc-length position (sorted) so Follow mode can pause the robot there.
    /// </summary>
    private void CaptureFollowStopDistances(List<Vector3> stopPoints)
    {
        _followStopDistances.Clear();
        _followNextStopIndex = 0;
        _followPauseRemaining = 0f;
        if (stopPoints == null || stopPoints.Count == 0 || !HasFollowTrajectory)
            return;

        foreach (var stop in stopPoints)
        {
            float traversed = 0f;
            float bestSqr = float.MaxValue;
            float bestDistance = 0f;
            for (int i = 1; i < _followTrajectoryPoints.Count; i++)
            {
                Vector3 from = _followTrajectoryPoints[i - 1];
                Vector3 to = _followTrajectoryPoints[i];
                Vector3 seg = to - from;
                seg.y = 0f;
                Vector3 rel = stop - from;
                rel.y = 0f;
                float segLenSqr = seg.sqrMagnitude;
                float t = segLenSqr > 0.0001f ? Mathf.Clamp01(Vector3.Dot(rel, seg) / segLenSqr) : 0f;
                Vector3 closest = Vector3.Lerp(from, to, t);
                Vector3 d = stop - closest;
                d.y = 0f;
                if (d.sqrMagnitude < bestSqr)
                {
                    bestSqr = d.sqrMagnitude;
                    bestDistance = traversed + Vector3.Distance(from, to) * t;
                }
                traversed += Vector3.Distance(from, to);
            }
            _followStopDistances.Add(bestDistance);
        }

        _followStopDistances.Sort();
        // Merge stops that landed on (nearly) the same spot along the line.
        for (int i = _followStopDistances.Count - 1; i > 0; i--)
            if (_followStopDistances[i] - _followStopDistances[i - 1] < 0.05f)
                _followStopDistances.RemoveAt(i);
    }

    private TrajectoryRenderer CreateSessionStroke(List<Vector3> points)
    {
        var go = new GameObject("Stroke_Session");
        go.transform.SetParent(transform);
        var r = go.AddComponent<TrajectoryRenderer>();
        r.lineColor = drawColor;
        r.ApplyVisualSettings();
        r.ReplacePoints(points);
        return r;
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
            var processed = FilterOutliers(_activeRenderer.Points, outlierJumpMultiplier);
            for (int i = 0; i < smoothingPasses; i++)
                processed = SmoothMovingAverage(processed, smoothingWindow);
            if (processed.Count >= 2)
                _activeRenderer.ReplacePoints(processed);

            _activeRenderer.gameObject.name = "Stroke_Session";
            _sessionRenderers.Add(_activeRenderer);
        }
        else
        {
            Destroy(_activeRenderer.gameObject);
        }

        _activeRenderer = null;
    }

    /// <summary>
    /// True iff a Follow call in the current frame would actually move the
    /// robot. Callers (e.g. RewindController) should skip their own "apply
    /// recorded robot pose" step when this is true, so the recording can't
    /// fight with the drawn trajectory.
    /// </summary>
    public bool WillApplyFollowThisFrame()
    {
        if (!IsFollowMode || !HasFollowTrajectory)
            return false;
        var sean = SEAN.SEAN.instance;
        if (sean == null || sean.robot == null || sean.robot.base_link == null)
            return false;
        return true;
    }

    private string _lastLoggedSkipReason = "";
    private void LogFollowSkipOnce()
    {
        if (LastFollowSkipReason == _lastLoggedSkipReason) return;
        _lastLoggedSkipReason = LastFollowSkipReason;
        Debug.LogWarning($"[TrajectoryManager] Follow skipped: {LastFollowSkipReason}");
    }

    // ── Post-processing ──────────────────────────────────────────────────────

    private static List<Vector3> FilterOutliers(List<Vector3> pts, float jumpMultiplier)
    {
        var result = new List<Vector3>(pts.Count);
        if (pts == null || pts.Count == 0) return result;
        if (jumpMultiplier <= 0f || pts.Count < 3)
        {
            result.AddRange(pts);
            return result;
        }

        var lengths = new List<float>(pts.Count - 1);
        for (int i = 1; i < pts.Count; i++)
            lengths.Add(Vector3.Distance(pts[i - 1], pts[i]));
        lengths.Sort();
        float median = lengths[lengths.Count / 2];
        if (median < 1e-4f) median = 0.05f;
        float threshold = median * jumpMultiplier;

        result.Add(pts[0]);
        for (int i = 1; i < pts.Count - 1; i++)
        {
            if (Vector3.Distance(result[result.Count - 1], pts[i]) <= threshold)
                result.Add(pts[i]);
        }
        // Always keep the last point so the stroke endpoint is preserved.
        result.Add(pts[pts.Count - 1]);
        return result;
    }

    private static List<Vector3> SmoothMovingAverage(List<Vector3> pts, int window)
    {
        if (pts == null || pts.Count < 3 || window <= 1)
            return new List<Vector3>(pts ?? new List<Vector3>());

        int half = window / 2;
        var result = new List<Vector3>(pts.Count);
        for (int i = 0; i < pts.Count; i++)
        {
            int s = Mathf.Max(0, i - half);
            int e = Mathf.Min(pts.Count - 1, i + half);
            Vector3 sum = Vector3.zero;
            int n = 0;
            for (int j = s; j <= e; j++) { sum += pts[j]; n++; }
            result.Add(sum / n);
        }
        // Anchor endpoints to keep start/end exactly where drawn.
        result[0] = pts[0];
        result[result.Count - 1] = pts[pts.Count - 1];
        return result;
    }

    private void SaveSession()
    {
        var polylines = new List<List<Vector3>>();
        foreach (var r in _sessionRenderers)
            if (r != null && r.Points != null && r.Points.Count >= 2)
                polylines.Add(new List<Vector3>(r.Points));

        polylines = ConnectNearbyPolylines(polylines, strokeConnectDistance);

        // No new strokes and an untouched stop set — nothing worth a new file.
        // Deleting every stop IS an edit: that save persists the empty stop set.
        if (polylines.Count == 0 && !_stopsEdited)
        {
            Debug.Log("[Trajectory] Session empty — nothing saved.");
            return;
        }

        var collection = new TrajectoryCollection();
        foreach (var line in polylines)
        {
            var data = new TrajectoryData();
            foreach (var p in line)
                data.points.Add(new TrajectoryPoint(p));
            collection.trajectories.Add(data);
        }

        // A stops-only session annotates the previously saved drawing: carry that
        // drawing's strokes forward so the new session file stays self-contained.
        if (collection.trajectories.Count == 0)
        {
            string[] previous = TrajectoryIO.GetAllSessionFiles();
            if (previous.Length > 0)
                collection.trajectories = TrajectoryIO.LoadFromPath(previous[0]).trajectories;
        }

        foreach (var p in _sessionStopPoints)
            collection.stopPoints.Add(new TrajectoryPoint(p));

        TrajectoryIO.SaveNewSession(collection);
        SaveIntoReviewedTrialFolder(collection);
        Debug.Log($"[Trajectory] Session saved ({collection.trajectories.Count} trajectory(ies), {collection.stopPoints.Count} stop point(s)).");
    }

    /// <summary>
    /// Mirror the drawn strokes into the reviewed trial's SessionLogs folder
    /// (drawn_trajectory_*.json next to trial_info.json). persistentDataPath saves
    /// stay on the review device; this copy travels with the trial data.
    /// </summary>
    private static void SaveIntoReviewedTrialFolder(TrajectoryCollection collection)
    {
        string folder = SessionReview.SessionReviewManager.CurrentReviewTrialFolder;
        if (string.IsNullOrEmpty(folder) || !System.IO.Directory.Exists(folder))
            return;
        try
        {
            string stamp = System.DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string path = System.IO.Path.Combine(folder, $"drawn_trajectory_{stamp}.json");
            System.IO.File.WriteAllText(path, JsonUtility.ToJson(collection, prettyPrint: true));
            Debug.Log($"[Trajectory] Drawn trajectory also saved to trial folder: {path}");
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"[Trajectory] Could not save drawn trajectory into trial folder: {ex.Message}");
        }
    }

    /// <summary>
    /// Greedily joins polylines whose endpoints are within maxGap of each other,
    /// reversing pieces as needed, so a briefly lifted pen still produces one
    /// continuous trajectory (Follow mode only ever uses the first one).
    /// </summary>
    private static List<List<Vector3>> ConnectNearbyPolylines(List<List<Vector3>> lines, float maxGap)
    {
        var result = new List<List<Vector3>>();
        foreach (var l in lines)
            if (l != null && l.Count >= 2)
                result.Add(new List<Vector3>(l));

        if (maxGap <= 0f || result.Count < 2)
            return result;

        float sqrGap = maxGap * maxGap;
        bool mergedAny = true;
        while (mergedAny && result.Count > 1)
        {
            mergedAny = false;
            for (int i = 0; i < result.Count && !mergedAny; i++)
            {
                for (int j = i + 1; j < result.Count && !mergedAny; j++)
                {
                    var a = result[i];
                    var b = result[j];
                    Vector3 aStart = a[0], aEnd = a[a.Count - 1];
                    Vector3 bStart = b[0], bEnd = b[b.Count - 1];

                    float dEndStart = (aEnd - bStart).sqrMagnitude;     // a → b
                    float dEndEnd = (aEnd - bEnd).sqrMagnitude;         // a → reversed b
                    float dStartEnd = (aStart - bEnd).sqrMagnitude;     // b → a
                    float dStartStart = (aStart - bStart).sqrMagnitude; // reversed a → b

                    float best = Mathf.Min(Mathf.Min(dEndStart, dEndEnd), Mathf.Min(dStartEnd, dStartStart));
                    if (best > sqrGap)
                        continue;

                    if (best == dEndStart) { a.AddRange(b); }
                    else if (best == dEndEnd) { b.Reverse(); a.AddRange(b); }
                    else if (best == dStartEnd) { b.AddRange(a); result[i] = b; }
                    else { a.Reverse(); a.AddRange(b); }

                    result.RemoveAt(j);
                    mergedAny = true;
                }
            }
        }
        return result;
    }

    // ── Display / Viz ────────────────────────────────────────────────────────

    private void RefreshDisplay()
    {
        foreach (var r in _displayedRenderers)
            if (r != null) Destroy(r.gameObject);
        _displayedRenderers.Clear();
        ClearDisplayedStopMarkers();
        _followTrajectoryPoints.Clear();
        _followTrajectoryLength = 0f;
        _followStopDistances.Clear();
        _followNextStopIndex = 0;
        _followPauseRemaining = 0f;
        _followSessionActive = false;

        foreach (var r in _sessionRenderers)
            if (r != null) Destroy(r.gameObject);
        _sessionRenderers.Clear();
        ClearSessionStopPoints();

        string[] files = TrajectoryIO.GetAllSessionFiles();
        int count = (visibleSessionCount <= 0) ? files.Length
                                                : Mathf.Min(visibleSessionCount, files.Length);

        _loadedStopPoints.Clear();
        for (int i = 0; i < count; i++)
        {
            TrajectoryCollection col = TrajectoryIO.LoadFromPath(files[i]);
            var polylines = new List<List<Vector3>>();
            foreach (var data in col.trajectories)
            {
                if (data.points.Count < 2) continue;
                var line = new List<Vector3>(data.points.Count);
                foreach (var pt in data.points)
                    line.Add(pt.ToVector3());
                polylines.Add(line);
            }

            // Also heals sessions saved before strokes were connected on save.
            polylines = ConnectNearbyPolylines(polylines, strokeConnectDistance);

            foreach (var line in polylines)
            {
                CaptureFollowTrajectory(line);
                StartCoroutine(SpawnDisplayRenderer(line));
            }

            // Stop points come only from the newest session so stale markers from
            // older files can't stack up under the current drawing.
            if (i == 0 && col.stopPoints != null)
                foreach (var pt in col.stopPoints)
                    _loadedStopPoints.Add(pt.ToVector3());
        }

        foreach (var p in _loadedStopPoints)
        {
            var marker = CreateStopMarker(p);
            marker.SetActive(IsReviewActive());
            _displayedStopMarkers.Add(marker);
        }
        CaptureFollowStopDistances(_loadedStopPoints);

        if (!HasFollowTrajectory)
            IsFollowMode = false;

        _trajectoriesVisible = true;
    }

    private void CaptureFollowTrajectory(List<Vector3> points)
    {
        if (_followTrajectoryPoints.Count > 0 || points == null || points.Count < 2)
            return;

        _followTrajectoryPoints.AddRange(points);

        _followTrajectoryLength = 0f;
        for (int i = 1; i < _followTrajectoryPoints.Count; i++)
            _followTrajectoryLength += Vector3.Distance(_followTrajectoryPoints[i - 1], _followTrajectoryPoints[i]);
    }

    private bool TryEvaluateFollowPoseAtDistance(float targetDistance, out Vector3 position, out Quaternion rotation)
    {
        position = Vector3.zero;
        rotation = Quaternion.identity;

        if (!HasFollowTrajectory)
            return false;

        targetDistance = Mathf.Clamp(targetDistance, 0f, _followTrajectoryLength);
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

    private IEnumerator SpawnDisplayRenderer(List<Vector3> line)
    {
        var go = new GameObject("Traj_Display");
        go.transform.SetParent(transform);
        var r = go.AddComponent<TrajectoryRenderer>();
        r.lineColor = loadedColor;
        r.ApplyVisualSettings();

        yield return null;

        var lifted = new List<Vector3>(line.Count);
        foreach (var p in line)
            lifted.Add(p + Vector3.up * heightOffset);
        r.ReplacePoints(lifted);
        // Visibility may have been toggled off (Legend row / review exit) while
        // this renderer was still one frame away from existing.
        r.gameObject.SetActive(_trajectoriesVisible && IsReviewActive());
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

