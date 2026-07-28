using UnityEngine;

/// <summary>
/// Draws a black frame and a role label ("First-Person View", "Overview",
/// "Back View") over every mini camera panel on the main display, so drivers
/// can tell at a glance what each small screen shows. The full-screen view is
/// deliberately unlabeled. Works for every mini source — the PWD player minis
/// (PWDOverheadCamera / PWDFirstPersonCamera), the SEAN robot's overhead /
/// first / rear panels, and the auto-created practice-rig minis — by
/// classifying whichever enabled cameras currently render a sub-screen rect,
/// so Tab view swaps relabel automatically. Self-bootstraps like
/// AgentViewToggle (no scene wiring); hidden while session review, world
/// building, or the session-onboarding panel is active.
/// </summary>
[DefaultExecutionOrder(6100)]
public class MiniScreenFrames : MonoBehaviour
{
    private const float MiniMaxNormalizedSize = 0.99f;

    private GUIStyle labelStyle;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        if (FindObjectOfType<MiniScreenFrames>() != null)
            return;

        var go = new GameObject("MiniScreenFrames");
        DontDestroyOnLoad(go);
        go.AddComponent<MiniScreenFrames>();
    }

    private void OnGUI()
    {
        if (Event.current.type != EventType.Repaint)
            return;

        var review = SessionReview.SessionReviewManager.Instance;
        if (review != null && (review.IsReviewModeActive || review.IsWorldBuildingModeActive || review.IsOnboardingActive))
            return;

        foreach (var cam in Camera.allCameras)
        {
            if (cam.targetDisplay != 0 || cam.targetTexture != null)
                continue;
            if (cam.rect.width >= MiniMaxNormalizedSize || cam.rect.height >= MiniMaxNormalizedSize)
                continue;

            DrawFrame(cam, ClassifyLabel(cam));
        }
    }

    /// <summary>
    /// Role by camera identity rather than screen slot, so the label follows
    /// the view through Tab swaps (a swapped-down third-person view reads
    /// "Third-Person View" while the first-person view is full screen).
    /// </summary>
    private static string ClassifyLabel(Camera cam)
    {
        var sean = SEAN.SEAN.instance;
        var robot = sean != null ? sean.robot : null;
        if (robot != null)
        {
            if (cam == robot.camera_first) return "First-Person View";
            if (cam == robot.camera_overhead) return "Overview";
            if (cam == robot.camera_rear) return "Back View";
            if (cam == robot.camera_third) return "Third-Person View";
        }

        if (cam.GetComponent<IVI.FirstPersonCameraLevel>() != null)
            return "First-Person View";
        if (cam.GetComponent<IVI.WheelchairCameraSmoothing>() != null)
            return "Third-Person View";
        if (cam.orthographic || cam.name.Contains("Overhead"))
            return "Overview";
        if (cam.name.Contains("Rear"))
            return "Back View";
        return null;
    }

    private void DrawFrame(Camera cam, string label)
    {
        // Camera pixel rects are bottom-left origin; IMGUI is top-left.
        Rect px = cam.pixelRect;
        var panel = new Rect(px.x, Screen.height - px.yMax, px.width, px.height);
        float thickness = Mathf.Max(2f, Screen.height * 0.004f);

        Color previous = GUI.color;
        GUI.color = Color.black;
        var tex = Texture2D.whiteTexture;
        GUI.DrawTexture(new Rect(panel.x, panel.y, panel.width, thickness), tex);
        GUI.DrawTexture(new Rect(panel.x, panel.yMax - thickness, panel.width, thickness), tex);
        GUI.DrawTexture(new Rect(panel.x, panel.y, thickness, panel.height), tex);
        GUI.DrawTexture(new Rect(panel.xMax - thickness, panel.y, thickness, panel.height), tex);

        if (!string.IsNullOrEmpty(label))
        {
            EnsureStyle();
            labelStyle.fontSize = Mathf.Clamp(Mathf.RoundToInt(Screen.height * 0.016f), 11, 22);
            float chipHeight = labelStyle.fontSize + 8f;
            float chipWidth = labelStyle.CalcSize(new GUIContent(label)).x + 16f;
            var chip = new Rect(panel.x + (panel.width - chipWidth) * 0.5f, panel.y, chipWidth, chipHeight);
            GUI.DrawTexture(chip, tex);
            GUI.color = Color.white;
            GUI.Label(chip, label, labelStyle);
        }

        GUI.color = previous;
    }

    private void EnsureStyle()
    {
        if (labelStyle != null)
            return;

        labelStyle = new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontStyle = FontStyle.Bold,
        };
        labelStyle.normal.textColor = Color.white;
    }
}
