using UnityEngine;

[RequireComponent(typeof(Camera))]
public class CenterAnchorOverlay : MonoBehaviour
{
    [SerializeField] private float dotSize = 16f;
    [SerializeField] private float ringSize = 26f;
    [SerializeField] private float verticalOffset = -22f;
    [SerializeField] [Range(0f, 1f)] private float alpha = 0.7f;
    [SerializeField] [Range(0f, 1f)] private float ringAlpha = 0.22f;
    [SerializeField] [Range(0f, 1f)] private float outlineAlpha = 0.22f;
    [SerializeField] private Color anchorColor = new Color(1f, 1f, 1f, 1f);
    [SerializeField] private string overlayLabel = "you are driving the agent";
    [SerializeField] private int fontSize = 15;
    [SerializeField] private float labelOffsetY = 16f;

    private Texture2D anchorTexture;
    private GUIStyle labelStyle;
    private GUIStyle shadowStyle;

    private void Awake()
    {
        EnsureTexture();
    }

    private void OnGUI()
    {
        Camera cam = GetComponent<Camera>();
        if (cam == null || !cam.enabled)
        {
            return;
        }

        EnsureTexture();
        EnsureStyle();

        float centerX = Screen.width * 0.5f;
        float centerY = Screen.height * 0.5f + verticalOffset;

        Color previousColor = GUI.color;

        GUI.color = new Color(0f, 0f, 0f, outlineAlpha);
        GUI.DrawTexture(
            new Rect(centerX - ringSize * 0.5f - 1f, centerY - ringSize * 0.5f - 1f, ringSize + 2f, ringSize + 2f),
            anchorTexture);

        GUI.color = new Color(anchorColor.r, anchorColor.g, anchorColor.b, ringAlpha);
        GUI.DrawTexture(
            new Rect(centerX - ringSize * 0.5f, centerY - ringSize * 0.5f, ringSize, ringSize),
            anchorTexture);

        GUI.color = new Color(0f, 0f, 0f, outlineAlpha);
        GUI.DrawTexture(
            new Rect(centerX - dotSize * 0.5f - 1f, centerY - dotSize * 0.5f - 1f, dotSize + 2f, dotSize + 2f),
            anchorTexture);

        GUI.color = new Color(anchorColor.r, anchorColor.g, anchorColor.b, alpha);
        GUI.DrawTexture(
            new Rect(centerX - dotSize * 0.5f, centerY - dotSize * 0.5f, dotSize, dotSize),
            anchorTexture);

        if (!string.IsNullOrEmpty(overlayLabel))
        {
            Rect labelRect = new Rect(centerX - 120f, centerY + labelOffsetY, 240f, 24f);
            GUI.Label(
                new Rect(labelRect.x + 1f, labelRect.y + 1f, labelRect.width, labelRect.height),
                overlayLabel,
                shadowStyle);
            GUI.Label(labelRect, overlayLabel, labelStyle);
        }

        GUI.color = previousColor;
    }

    private void EnsureTexture()
    {
        if (anchorTexture != null)
        {
            return;
        }

        anchorTexture = Texture2D.whiteTexture;
    }

    private void EnsureStyle()
    {
        if (labelStyle == null)
        {
            labelStyle = new GUIStyle(GUI.skin.label);
        }

        labelStyle.alignment = TextAnchor.MiddleCenter;
        labelStyle.fontSize = fontSize;
        labelStyle.normal.textColor = new Color(anchorColor.r, anchorColor.g, anchorColor.b, Mathf.Max(alpha, 0.72f));

        if (shadowStyle == null)
        {
            shadowStyle = new GUIStyle(GUI.skin.label);
        }

        shadowStyle.alignment = TextAnchor.MiddleCenter;
        shadowStyle.fontSize = fontSize;
        shadowStyle.normal.textColor = new Color(0f, 0f, 0f, 0.4f);
    }
}
