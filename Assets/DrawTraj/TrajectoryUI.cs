using UnityEngine;

/// <summary>
/// Lightweight IMGUI overlay for DrawTraj controls during session review.
/// This avoids any dependency on scene-authored Canvas/Button objects.
/// </summary>
public class TrajectoryUI : MonoBehaviour
{
    [Header("Manager Reference")]
    public TrajectoryManager manager;

    [Header("Layout")]
    [SerializeField] private Vector2 anchor = new Vector2(24f, -188f);
    [SerializeField] private float buttonWidth = 188f;
    [SerializeField] private float buttonHeight = 34f;
    [SerializeField] private float buttonSpacing = 8f;

    private GUIStyle panelStyle;
    private GUIStyle buttonStyle;
    private GUIStyle buttonActiveStyle;
    private GUIStyle hintStyle;
    private GUIStyle headerStyle;
    private GUIStyle finishStyle;
    private GUIStyle cancelStyle;
    private GUIStyle debugStyle;
    private bool stylesBuilt;

    // Draggable/resizable/closable chrome for the default control buttons.
    private readonly SessionReview.ReviewPanels.State controlsPanel = new SessionReview.ReviewPanels.State();

    // Bounding rects of the draw-mode control panel and the touch-debug box (GUI space,
    // origin top-left). TrajectoryManager queries BlocksInputAt() so taps on these don't
    // also draw or pan the camera.
    private Rect _drawPanelRectGui;
    private bool _drawPanelActive;
    private Rect _debugBoxRectGui;
    private bool _debugBoxActive;

    private void Start()
    {
        if (manager == null)
            manager = GetComponent<TrajectoryManager>();

        if (manager == null)
            Debug.LogError("[TrajectoryUI] No TrajectoryManager assigned!");
    }

    private void OnGUI()
    {
        _drawPanelActive = false;
        _debugBoxActive = false;

        if (manager == null || !IsReviewActive())
            return;

        EnsureStyles();

        if (manager.IsDrawMode)
        {
            DrawDrawModePanel();
            return;
        }

        // Default control panel: draggable / resizable / closable window chrome.
        // The old floating "Play=.. FollowMode=.." diagnostic HUD now lives inside
        // the review Metrics panel (MetricsOverlayUI).
        float defaultHeight = SessionReview.ReviewPanels.TitleH + buttonHeight * 4f + buttonSpacing * 4f + 8f;
        Rect defaultRect = new Rect(24f, Screen.height - defaultHeight - 24f, buttonWidth + 24f, defaultHeight);
        if (SessionReview.ReviewPanels.Begin(controlsPanel, this, "Trajectory", defaultRect, out Rect content))
            DrawControlsPanelBody(content);
        SessionReview.ReviewPanels.End(controlsPanel);
    }

    private void DrawControlsPanelBody(Rect content)
    {
        bool hasFollowTrajectory = manager.HasFollowTrajectory;
        string showHideLabel = "Show/Hide Traj";
        string drawLabel = "Draw Traj";
        string followLabel = manager.IsFollowMode ? "Stop Follow" : "Follow Traj";

        // Responsive: buttons and text scale with the panel size.
        float scale = controlsPanel.FontScale;
        var bStyle = new GUIStyle(buttonStyle) { fontSize = Mathf.RoundToInt(17f * scale) };
        var bActiveStyle = new GUIStyle(buttonActiveStyle) { fontSize = Mathf.RoundToInt(17f * scale) };
        var hStyle = new GUIStyle(hintStyle) { fontSize = Mathf.RoundToInt(15f * scale) };

        float bh = buttonHeight * scale;
        float x = content.x;
        float y = content.y;
        float w = content.width;
        float step = bh + buttonSpacing * scale;

        if (GUI.Button(new Rect(x, y, w, bh), showHideLabel, bStyle))
            manager.ToggleVisibility();

        if (GUI.Button(new Rect(x, y + step, w, bh), drawLabel, bStyle))
            manager.EnterDrawMode();

        GUI.enabled = hasFollowTrajectory;
        if (GUI.Button(new Rect(x, y + step * 2f, w, bh), followLabel,
            manager.IsFollowMode ? bActiveStyle : bStyle))
        {
            manager.ToggleFollowMode();
        }
        GUI.enabled = true;

        if (manager.IsFollowMode && hasFollowTrajectory)
        {
            float rowY = y + step * 3f;
            float labelW = 70f * scale;
            float sliderW = Mathf.Max(40f, w - labelW);
            float speed = manager.EffectiveFollowSpeed;
            GUI.Label(new Rect(x, rowY, labelW, bh), $"{speed:0.00} m/s", hStyle);
            float mult = GUI.HorizontalSlider(
                new Rect(x + labelW, rowY + bh * 0.35f, sliderW, bh),
                manager.FollowSpeedMultiplier,
                manager.followSpeedMultiplierMin,
                manager.followSpeedMultiplierMax);
            manager.FollowSpeedMultiplier = mult;
        }
    }

    private void DrawDrawModePanel()
    {
        const float pad = 14f;
        const float bw = 240f;   // panel / button width
        const float bh = 56f;    // touch-friendly button height
        const float sp = 10f;    // spacing
        const float x = 24f;
        const float top = 110f;

        const float headerH = 26f;
        const float hintH = 46f;
        const int rows = 8;      // undo, clear, zoom row, pencil, detect/debug row, traj/ghosts row, finish, cancel
        float contentH = headerH + 6f + hintH + 8f + bh * rows + sp * (rows - 1);

        Rect panel = new Rect(x - pad, top - pad, bw + pad * 2f, contentH + pad * 2f);
        _drawPanelRectGui = panel;
        _drawPanelActive = true;
        GUI.Box(panel, GUIContent.none, panelStyle);

        float cy = top;
        GUI.Label(new Rect(x, cy, bw, headerH), "DRAW MODE", headerStyle);
        cy += headerH + 6f;

        string line1 = manager.ApplePencilOnly
            ? (manager.StylusDetected ? "Apple Pencil draws" : "Pencil-only - waiting for pencil")
            : "Finger or pencil draws";
        GUI.Label(new Rect(x, cy, bw, hintH), line1 + "\n1 finger = pan  -  2 fingers = pinch-zoom", hintStyle);
        cy += hintH + 8f;

        GUI.enabled = manager.CanUndo;
        if (GUI.Button(new Rect(x, cy, bw, bh), "Undo last", buttonStyle))
            manager.UndoLastStroke();
        cy += bh + sp;

        if (GUI.Button(new Rect(x, cy, bw, bh), "Clear all", buttonStyle))
            manager.ClearCurrentSession();
        GUI.enabled = true;
        cy += bh + sp;

        float halfW = (bw - sp) * 0.5f;
        if (GUI.Button(new Rect(x, cy, halfW, bh), "Zoom +", buttonStyle))
            manager.ZoomStep(true);
        if (GUI.Button(new Rect(x + halfW + sp, cy, halfW, bh), "Zoom -", buttonStyle))
            manager.ZoomStep(false);
        cy += bh + sp;

        string pencilLabel = manager.ApplePencilOnly ? "Pencil-only: ON" : "Pencil-only: OFF";
        if (GUI.Button(new Rect(x, cy, bw, bh), pencilLabel,
            manager.ApplePencilOnly ? buttonActiveStyle : buttonStyle))
            manager.ApplePencilOnly = !manager.ApplePencilOnly;
        cy += bh + sp;

        // Detection strategy + debug toggle (for calibrating pencil-vs-finger).
        if (GUI.Button(new Rect(x, cy, halfW, bh), "Detect: " + manager.PencilDetection, buttonStyle))
            manager.CyclePencilDetection();
        if (GUI.Button(new Rect(x + halfW + sp, cy, halfW, bh),
            manager.ShowTouchDebug ? "Debug: ON" : "Debug: OFF",
            manager.ShowTouchDebug ? buttonActiveStyle : buttonStyle))
            manager.ShowTouchDebug = !manager.ShowTouchDebug;
        cy += bh + sp;

        // Comparison overlays: the trial trajectory lines + Legend panel, and the
        // ghost robots (drawn / planned / driven), so the stroke being drawn can be
        // compared against them without leaving draw mode.
        var trialTraj = GetTrialTrajectoryRenderer();
        var ghosts = GetGhostComparison();

        GUI.enabled = trialTraj != null;
        bool trajShowing = trialTraj != null && trialTraj.IsShowing;
        if (GUI.Button(new Rect(x, cy, halfW, bh),
            trajShowing ? "Traj: ON" : "Traj: OFF",
            trajShowing ? buttonActiveStyle : buttonStyle))
            trialTraj.SetVisible(!trajShowing);

        GUI.enabled = ghosts != null;
        bool ghostsOn = ghosts != null && ghosts.ShowGhosts;
        if (GUI.Button(new Rect(x + halfW + sp, cy, halfW, bh),
            ghostsOn ? "Ghosts: ON" : "Ghosts: OFF",
            ghostsOn ? buttonActiveStyle : buttonStyle))
            ghosts.ToggleGhosts();
        GUI.enabled = true;
        cy += bh + sp;

        if (GUI.Button(new Rect(x, cy, bw, bh), "Finish & Save", finishStyle))
            manager.ExitDrawMode();
        cy += bh + sp;

        if (GUI.Button(new Rect(x, cy, bw, bh), "Cancel (discard)", cancelStyle))
            manager.CancelDrawMode();

        DrawTouchDebugOverlay();
    }

    /// <summary>
    /// Live readout of every active touch (type / radius / pressure) so the user can see
    /// what their finger vs Apple Pencil actually report and pick a detection mode + threshold.
    /// </summary>
    private void DrawTouchDebugOverlay()
    {
        if (manager == null || !manager.ShowTouchDebug)
            return;

        float w = 440f;
        float h = 210f;
        Rect box = new Rect(Screen.width - w - 16f, 110f, w, h);
        _debugBoxRectGui = box;
        _debugBoxActive = true;
        GUI.Box(box, GUIContent.none, panelStyle);

        GUI.Label(new Rect(box.x + 10f, box.y + 8f, w - 20f, 22f),
            "TOUCH DEBUG — touch with finger, then pencil", headerStyle);

        string body = string.IsNullOrEmpty(manager.TouchDebugReadout)
            ? "(no active touches)"
            : manager.TouchDebugReadout;
        GUI.Label(new Rect(box.x + 10f, box.y + 34f, w - 20f, h - 92f), body, debugStyle);

        // Live radius-threshold tuning (so you can calibrate on the iPad itself).
        float ty = box.y + h - 50f;
        GUI.Label(new Rect(box.x + 10f, ty, 180f, 40f),
            $"radius thr = {manager.FingerRadiusThreshold:F1}", hintStyle);
        if (GUI.Button(new Rect(box.x + w - 200f, ty, 90f, 40f), "thr -", buttonStyle))
            manager.AdjustRadiusThreshold(-1f);
        if (GUI.Button(new Rect(box.x + w - 104f, ty, 90f, 40f), "thr +", buttonStyle))
            manager.AdjustRadiusThreshold(1f);
    }

    /// <summary>
    /// True when a screen-space point (Input coordinates, origin bottom-left) lands on
    /// the draw-mode control panel. TrajectoryManager calls this so taps on the buttons
    /// don't also draw a point or pan the camera.
    /// </summary>
    public bool BlocksInputAt(Vector2 screenPos)
    {
        Vector2 guiPoint = new Vector2(screenPos.x, Screen.height - screenPos.y);

        if (_drawPanelActive && _drawPanelRectGui.Contains(guiPoint))
            return true;
        if (_debugBoxActive && _debugBoxRectGui.Contains(guiPoint))
            return true;

        return false;
    }

    private void EnsureStyles()
    {
        if (stylesBuilt)
            return;

        stylesBuilt = true;

        Texture2D MakeTexture(Color color)
        {
            Texture2D texture = new Texture2D(1, 1);
            texture.SetPixel(0, 0, color);
            texture.Apply();
            return texture;
        }

        panelStyle = new GUIStyle(GUI.skin.box)
        {
            padding = new RectOffset(0, 0, 0, 0),
            normal = { background = MakeTexture(new Color(0.08f, 0.09f, 0.11f, 0.82f)) }
        };

        buttonStyle = new GUIStyle(GUI.skin.button)
        {
            fontSize = 16,
            alignment = TextAnchor.MiddleCenter,
            normal =
            {
                background = MakeTexture(new Color(0.9f, 0.9f, 0.92f, 0.96f)),
                textColor = new Color(0.14f, 0.16f, 0.18f)
            },
            hover =
            {
                background = MakeTexture(new Color(0.97f, 0.97f, 0.98f, 0.98f)),
                textColor = new Color(0.08f, 0.09f, 0.11f)
            },
            active =
            {
                background = MakeTexture(new Color(0.76f, 0.78f, 0.81f, 0.98f)),
                textColor = new Color(0.08f, 0.09f, 0.11f)
            }
        };

        buttonActiveStyle = new GUIStyle(buttonStyle)
        {
            fontStyle = FontStyle.Bold,
            normal =
            {
                background = MakeTexture(new Color(0.2f, 0.45f, 0.3f, 0.96f)),
                textColor = Color.white
            },
            hover =
            {
                background = MakeTexture(new Color(0.25f, 0.54f, 0.36f, 0.98f)),
                textColor = Color.white
            },
            active =
            {
                background = MakeTexture(new Color(0.14f, 0.34f, 0.22f, 0.98f)),
                textColor = Color.white
            }
        };

        hintStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 14,
            wordWrap = true,
            normal = { textColor = new Color(0.9f, 0.92f, 0.95f) }
        };

        headerStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 18,
            fontStyle = FontStyle.Bold,
            normal = { textColor = new Color(0.95f, 0.97f, 1f) }
        };

        finishStyle = new GUIStyle(buttonStyle)
        {
            fontSize = 17,
            fontStyle = FontStyle.Bold,
            normal = { background = MakeTexture(new Color(0.16f, 0.5f, 0.28f, 0.97f)), textColor = Color.white },
            hover = { background = MakeTexture(new Color(0.2f, 0.6f, 0.34f, 0.98f)), textColor = Color.white },
            active = { background = MakeTexture(new Color(0.12f, 0.4f, 0.22f, 0.98f)), textColor = Color.white }
        };

        cancelStyle = new GUIStyle(buttonStyle)
        {
            fontSize = 17,
            normal = { background = MakeTexture(new Color(0.55f, 0.2f, 0.22f, 0.96f)), textColor = Color.white },
            hover = { background = MakeTexture(new Color(0.66f, 0.24f, 0.26f, 0.98f)), textColor = Color.white },
            active = { background = MakeTexture(new Color(0.45f, 0.16f, 0.18f, 0.98f)), textColor = Color.white }
        };

        debugStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 13,
            wordWrap = true,
            richText = false,
            normal = { textColor = new Color(0.78f, 0.95f, 0.82f) }
        };
    }

    private static bool IsReviewActive()
    {
        var reviewManager = SessionReview.SessionReviewManager.Instance;
        return reviewManager != null && reviewManager.IsReviewUiActive;
    }

    /// <summary>The review trial-trajectory overlay (lines + Legend panel), if reviewing.</summary>
    private static SessionReview.MultiAgentTrajectoryRenderer GetTrialTrajectoryRenderer()
    {
        var reviewManager = SessionReview.SessionReviewManager.Instance;
        return reviewManager != null
            ? reviewManager.GetComponent<SessionReview.MultiAgentTrajectoryRenderer>()
            : null;
    }

    /// <summary>The ghost-robot comparison component (lives beside RewindController), if reviewing.</summary>
    private static SessionReview.GhostRobotComparison GetGhostComparison()
    {
        var reviewManager = SessionReview.SessionReviewManager.Instance;
        return reviewManager != null
            ? reviewManager.GetComponent<SessionReview.GhostRobotComparison>()
            : null;
    }
}
