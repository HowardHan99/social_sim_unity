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
    private bool stylesBuilt;

    private void Start()
    {
        if (manager == null)
            manager = GetComponent<TrajectoryManager>();

        if (manager == null)
            Debug.LogError("[TrajectoryUI] No TrajectoryManager assigned!");
    }

    private void OnGUI()
    {
        if (manager == null || !IsReviewActive())
            return;

        EnsureStyles();

        bool drawMode = manager.IsDrawMode;
        float x = anchor.x;
        float y = Screen.height + anchor.y;

        if (drawMode)
        {
            float panelWidth = buttonWidth + 24f;
            float panelHeight = 78f;
            GUI.Box(new Rect(x - 12f, y - 12f, panelWidth, panelHeight), GUIContent.none, panelStyle);
            GUI.Label(new Rect(x, y, buttonWidth, 24f), "Draw mode active", hintStyle);
            GUI.Label(new Rect(x, y + 26f, buttonWidth, 24f), "LMB draw  Wheel zoom  MMB pan  Esc finish", hintStyle);
            return;
        }

        bool hasFollowTrajectory = manager.HasFollowTrajectory;
        string showHideLabel = "Show/Hide Traj";
        string drawLabel = "Draw Traj";
        string followLabel = manager.IsFollowMode ? "Stop Follow" : "Follow Traj";

        bool showSpeedRow = manager.IsFollowMode && hasFollowTrajectory;
        float speedRowHeight = showSpeedRow ? (buttonHeight + buttonSpacing) : 0f;
        float panelHeightButtons = buttonHeight * 3f + buttonSpacing * 2f + 24f + speedRowHeight;
        GUI.Box(new Rect(x - 12f, y - 12f, buttonWidth + 24f, panelHeightButtons), GUIContent.none, panelStyle);

        if (GUI.Button(new Rect(x, y, buttonWidth, buttonHeight), showHideLabel, buttonStyle))
            manager.ToggleVisibility();

        if (GUI.Button(new Rect(x, y + buttonHeight + buttonSpacing, buttonWidth, buttonHeight), drawLabel, buttonStyle))
            manager.EnterDrawMode();

        GUI.enabled = hasFollowTrajectory;
        if (GUI.Button(
            new Rect(x, y + (buttonHeight + buttonSpacing) * 2f, buttonWidth, buttonHeight),
            followLabel,
            manager.IsFollowMode ? buttonActiveStyle : buttonStyle))
        {
            manager.ToggleFollowMode();
        }
        GUI.enabled = true;

        // Diagnostic HUD — positioned below the Export ROI panel (panel occupies
        // y=70..450 on the right edge), so it stays visible whether Export is open or not.
        {
            float hudW = 360f;
            float hudH = 120f;
            Rect hudRect = new Rect(Screen.width - hudW - 20f, 470f, hudW, hudH);
            GUI.Box(hudRect, GUIContent.none, panelStyle);
            string skip = string.IsNullOrEmpty(manager.LastFollowSkipReason) ? "follow OK" : $"follow SKIP: {manager.LastFollowSkipReason}";
            GUI.Label(new Rect(hudRect.x + 8f, hudRect.y + 4f, hudRect.width - 16f, 22f),
                $"Play={manager.ReviewIsPlaying}  t={manager.ReviewNormalizedTime:P0}  rate={manager.ReviewPlaybackSpeed:F2}x  toggles={manager.ReviewToggleCount}", hintStyle);
            GUI.Label(new Rect(hudRect.x + 8f, hudRect.y + 24f, hudRect.width - 16f, 22f),
                $"FollowMode={manager.IsFollowMode}  HasTraj={manager.HasFollowTrajectory}", hintStyle);
            GUI.Label(new Rect(hudRect.x + 8f, hudRect.y + 44f, hudRect.width - 16f, 22f),
                $"dist={manager.LastFollowDistance:F2}m  speed={manager.EffectiveFollowSpeed:F2}m/s", hintStyle);
            GUI.Label(new Rect(hudRect.x + 8f, hudRect.y + 64f, hudRect.width - 16f, 22f),
                $"elapsed={manager.LastFollowElapsed:F2}s", hintStyle);
            GUI.Label(new Rect(hudRect.x + 8f, hudRect.y + 86f, hudRect.width - 16f, 30f),
                skip, hintStyle);
        }

        if (showSpeedRow)
        {
            float rowY = y + (buttonHeight + buttonSpacing) * 3f;
            float labelW = 70f;
            float sliderW = buttonWidth - labelW;
            float speed = manager.EffectiveFollowSpeed;
            GUI.Label(new Rect(x, rowY, labelW, buttonHeight), $"{speed:0.00} m/s", hintStyle);
            float mult = GUI.HorizontalSlider(
                new Rect(x + labelW, rowY + buttonHeight * 0.35f, sliderW, buttonHeight),
                manager.FollowSpeedMultiplier,
                manager.followSpeedMultiplierMin,
                manager.followSpeedMultiplierMax);
            manager.FollowSpeedMultiplier = mult;
        }
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
    }

    private static bool IsReviewActive()
    {
        var reviewManager = SessionReview.SessionReviewManager.Instance;
        return reviewManager != null && reviewManager.IsReviewUiActive;
    }
}
