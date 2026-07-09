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
///   Apple Pencil              — draw (fingers reserved for navigation; resting palm ignored)
///   One-finger drag           — pan
///   Two-finger pinch / drag   — zoom + pan
///   On-screen buttons         — Undo · Clear · Zoom · Pencil-only · Finish · Cancel
/// DRAW MODE CONTROLS (desktop):
///   Mouse left-hold           — draw
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
    [Min(1)] public int smoothingWindow = 15;

    [Tooltip("Number of smoothing passes. 0 to disable.")]
    [Min(0)] public int smoothingPasses = 5;

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
    public bool showTouchDebug = true;

    [Tooltip("Closest zoom-in (smallest orthographic size) allowed while drawing.")]
    [Min(1f)] public float minZoomOrthoSize = 4f;

    [Tooltip("Farthest zoom-out (largest orthographic size) allowed while drawing.")]
    [Min(1f)] public float maxZoomOrthoSize = 120f;

    [Tooltip("Mouse-wheel / Zoom-button step as a fraction of the current zoom (desktop & on-screen buttons).")]
    [Range(0.01f, 0.9f)] public float zoomStepFraction = 0.15f;

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

    /// <summary>True when there is at least one stroke (finished or in-progress) to undo.</summary>
    public bool CanUndo => _activeRenderer != null || _sessionRenderers.Count > 0;

    public PencilDetectionMode PencilDetection
    {
        get => pencilDetection;
        set => pencilDetection = value;
    }

    public bool ShowTouchDebug
    {
        get => showTouchDebug;
        set => showTouchDebug = value;
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
    private float _followDistance;      // arc-length already traversed
    private float _followLastElapsed;   // last elapsedSeconds we saw
    private bool _followSessionActive;  // accumulator initialised?

    // Touch navigation / stroke edge-detection
    private TrajectoryUI _ui;
    private bool _strokeDown;            // a drawing contact is currently pressed
    private bool _navActive;            // a finger pan/zoom gesture is in progress
    private int _navFingerCount;        // fingers used by the active nav gesture
    private Vector2 _lastNavCentroid;
    private float _lastNavPinchDist;
    private bool _mousePanning;          // desktop middle-mouse pan (standalone scene)
    private Vector2 _lastMousePanPos;

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
        _strokeDown = false;
        _navActive = false;
        _mousePanning = false;

        SetVisibility(true);
        _sessionCollection = new TrajectoryCollection();
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
    }

    /// <summary>Remove the most recent stroke (or the in-progress one) from this session.</summary>
    public void UndoLastStroke()
    {
        if (!IsDrawMode) return;

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

        if (_sessionCollection != null && _sessionCollection.trajectories.Count > 0)
            _sessionCollection.trajectories.RemoveAt(_sessionCollection.trajectories.Count - 1);
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
        _sessionCollection = new TrajectoryCollection();
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
        _sessionCollection = null;

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
        }
        else
        {
            float dt = elapsedSeconds - _followLastElapsed;
            _followLastElapsed = elapsedSeconds;
            if (dt < 0f) // review scrubbed backwards — reset
            {
                _followDistance = 0f;
            }
            else
            {
                _followDistance += dt * EffectiveFollowSpeed;
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

        // ── Stroke begin / continue / end (rising & falling edges) ────────────
        if (drawDown)
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

        // ── Finger pan / pinch-zoom (never while the pencil is drawing) ───────
        bool canNavigate = !stylusDown &&
                           (fingerCount >= 2 || (applePencilOnly && fingerCount == 1));
        if (canNavigate)
            HandleTouchNavigation(fingerCount, finger0, finger1);
        else
            _navActive = false;

        // ── Desktop pan / zoom (standalone scene; review supplies its own) ────
        if (!IsReviewActive() && Input.touchCount == 0)
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
            ZoomDrawCamera(Input.mousePosition, scroll > 0f ? (1f - zoomStepFraction) : (1f + zoomStepFraction));

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
        Vector2 guiPoint = new Vector2(screenPos.x, Screen.height - screenPos.y);
        if (SessionReview.ReviewPanels.AnyPanelContains(guiPoint))
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
        if (!showTouchDebug)
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
            var processed = FilterOutliers(_activeRenderer.Points, outlierJumpMultiplier);
            for (int i = 0; i < smoothingPasses; i++)
                processed = SmoothMovingAverage(processed, smoothingWindow);
            if (processed.Count >= 2)
                _activeRenderer.ReplacePoints(processed);

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
        _followSessionActive = false;

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

