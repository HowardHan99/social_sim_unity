using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// TrajectoryUI — attach to your Canvas or a UI manager GameObject.
///
/// UI SETUP (create these in your Canvas, then assign below):
///
///   [ Button ]  drawButton      — "Draw Trajectory"  → OnClick: manager.EnterDrawMode()
///   [ Button ]  toggleButton    — "Show / Hide"       → OnClick: manager.ToggleVisibility()
///   [ GameObject ] normalButtons — parent that holds both buttons above
///                                  (hidden during draw mode)
///   [ GameObject ] drawModeOverlay — a panel shown only during draw mode, containing:
///       [ Text/TMP ]  modeLabel   — displays "Draw Mode"
///       [ Text/TMP ]  hintLabel   — displays "Press ESC to exit"
///
/// Wire the two buttons' OnClick events in the Inspector to call the matching
/// methods on TrajectoryManager directly, then assign the reference fields below.
/// </summary>
public class TrajectoryUI : MonoBehaviour
{
    [Header("Manager Reference")]
    public TrajectoryManager manager;

    [Header("UI Groups")]
    [Tooltip("Parent GameObject that holds the Draw and Show/Hide buttons.")]
    public GameObject normalButtonsGroup;

    [Tooltip("Overlay panel shown while in draw mode (contains mode label + hint).")]
    public GameObject drawModeOverlay;

    private void Start()
    {
        if (manager == null)
        {
            Debug.LogError("[TrajectoryUI] No TrajectoryManager assigned!");
            return;
        }
        RefreshUI();
    }

    private void Update()
    {
        // Keep UI in sync with manager state each frame.
        // (Cheap bool check — fine to run every frame.)
        RefreshUI();
    }

    // ── Internal ─────────────────────────────────────────────────────────────

    private void RefreshUI()
    {
        if (manager == null) return;

        bool drawMode = manager.IsDrawMode;

        if (normalButtonsGroup != null)
            normalButtonsGroup.SetActive(!drawMode);

        if (drawModeOverlay != null)
            drawModeOverlay.SetActive(drawMode);
    }
}
