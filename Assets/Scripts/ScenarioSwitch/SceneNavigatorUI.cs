using UnityEngine;

/// <summary>
/// Attach this component to any persistent GameObject alongside SceneChange
/// to get an in-game overlay for navigating scenes.
/// Toggle the overlay with the Tab key (configurable below).
/// </summary>
[RequireComponent(typeof(SceneChange))]
public class SceneNavigatorUI : MonoBehaviour
{
    [Header("Appearance")]
    [SerializeField] private KeyCode toggleKey = KeyCode.Tab;
    [SerializeField] private bool showOnStart   = true;
    [SerializeField] private bool showSceneList = true;

    private SceneChange sceneChange;
    private bool isVisible;

    // Layout constants
    private const float PanelWidth  = 260f;
    private const float ButtonH     = 30f;
    private const float Padding     = 10f;
    private const float SceneRowH   = 24f;
    private const float HeaderH     = 36f;

    // Cached styles (built once per skin change)
    private GUIStyle panelStyle;
    private GUIStyle headerStyle;
    private GUIStyle labelStyle;
    private GUIStyle activeRowStyle;
    private GUIStyle inactiveRowStyle;
    private GUIStyle navButtonStyle;
    private GUIStyle closeButtonStyle;
    private bool stylesBuilt;

    private Vector2 scrollPos;

    private void Awake()
    {
        sceneChange = GetComponent<SceneChange>();
        isVisible   = showOnStart;
    }

    private void Update()
    {
        if (Input.GetKeyDown(toggleKey))
            isVisible = !isVisible;
    }

    private void OnGUI()
    {
        if (!isVisible) return;

        EnsureStyles();

        float listHeight = showSceneList
            ? Mathf.Min(sceneChange.SceneCount * SceneRowH + 4f, 200f)
            : 0f;

        float panelHeight = HeaderH + Padding
            + ButtonH + Padding             // nav row
            + (showSceneList ? listHeight + Padding : 0f)
            + Padding;

        // Anchor to top-right
        float x = Screen.width - PanelWidth - 12f;
        float y = 12f;
        Rect panelRect = new Rect(x, y, PanelWidth, panelHeight);

        // Shadow
        GUI.color = new Color(0, 0, 0, 0.35f);
        GUI.Box(new Rect(panelRect.x + 3, panelRect.y + 3, panelRect.width, panelRect.height), GUIContent.none, panelStyle);
        GUI.color = Color.white;

        GUI.Box(panelRect, GUIContent.none, panelStyle);

        float cx = panelRect.x + Padding;
        float cy = panelRect.y;
        float innerW = PanelWidth - Padding * 2f;

        // ── Header ───────────────────────────────────────────────────────────
        GUI.Label(new Rect(cx, cy + 6f, innerW - 30f, HeaderH - 6f),
            "Scene Navigator", headerStyle);

        // Toggle scene list button (top-right of header)
        if (GUI.Button(new Rect(panelRect.x + PanelWidth - 34f, cy + 7f, 24f, 22f),
            showSceneList ? "▲" : "▼", closeButtonStyle))
        {
            showSceneList = !showSceneList;
        }
        cy += HeaderH;

        // ── Current scene label ──────────────────────────────────────────────
        string currentName = sceneChange.CurrentSceneName;
        int    currentIdx  = sceneChange.CurrentSceneIndex;
        int    total       = sceneChange.SceneCount;

        GUI.Label(new Rect(cx, cy, innerW, 18f),
            $"<b>{currentName}</b>  <color=#aaaaaa>[{currentIdx + 1} / {total}]</color>",
            labelStyle);
        cy += 20f;

        // ── Prev / Next buttons ──────────────────────────────────────────────
        float halfW = (innerW - 4f) * 0.5f;

        if (GUI.Button(new Rect(cx, cy, halfW, ButtonH), "◀  Prev", navButtonStyle))
            sceneChange.GoToPreviousScene();

        if (GUI.Button(new Rect(cx + halfW + 4f, cy, halfW, ButtonH), "Next  ▶", navButtonStyle))
            sceneChange.GoToNextScene();

        cy += ButtonH + Padding;

        // ── Scene list ────────────────────────────────────────────────────────
        if (showSceneList && total > 0)
        {
            Rect scrollViewRect = new Rect(cx, cy, innerW, listHeight);
            Rect contentRect    = new Rect(0, 0, innerW - 16f, total * SceneRowH);

            scrollPos = GUI.BeginScrollView(scrollViewRect, scrollPos, contentRect);

            for (int i = 0; i < total; i++)
            {
                Rect rowRect = new Rect(0, i * SceneRowH, contentRect.width, SceneRowH - 2f);
                GUIStyle style = (i == currentIdx) ? activeRowStyle : inactiveRowStyle;

                if (GUI.Button(rowRect, $"  {i + 1}.  {sceneChange.SceneNames[i]}", style))
                    sceneChange.LoadSceneAtIndex(i);
            }

            GUI.EndScrollView();
        }

        // ── Footer hint ───────────────────────────────────────────────────────
        GUIStyle hint = new GUIStyle(GUI.skin.label)
        {
            fontSize  = 9,
            alignment = TextAnchor.MiddleCenter,
            normal    = { textColor = new Color(0.5f, 0.5f, 0.5f) }
        };
        GUI.Label(new Rect(panelRect.x, panelRect.yMax - 14f, PanelWidth, 14f),
            $"[{toggleKey}] toggle  |  Ctrl+P / Ctrl+N", hint);
    }

    // ── Style builder ─────────────────────────────────────────────────────────

    private void EnsureStyles()
    {
        if (stylesBuilt) return;
        stylesBuilt = true;

        Texture2D MakeTex(Color c)
        {
            var t = new Texture2D(1, 1);
            t.SetPixel(0, 0, c);
            t.Apply();
            return t;
        }

        panelStyle = new GUIStyle(GUI.skin.box)
        {
            border  = new RectOffset(6, 6, 6, 6),
            padding = new RectOffset(0, 0, 0, 0),
            normal  = { background = MakeTex(new Color(0.10f, 0.10f, 0.14f, 0.92f)) }
        };

        headerStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize  = 14,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleLeft,
            normal    = { textColor = Color.white }
        };

        labelStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize  = 11,
            richText  = true,
            alignment = TextAnchor.MiddleLeft,
            normal    = { textColor = new Color(0.85f, 0.95f, 0.85f) }
        };

        navButtonStyle = new GUIStyle(GUI.skin.button)
        {
            fontSize  = 12,
            fontStyle = FontStyle.Bold,
            normal    = { background = MakeTex(new Color(0.20f, 0.35f, 0.20f)), textColor = Color.white },
            hover     = { background = MakeTex(new Color(0.28f, 0.50f, 0.28f)), textColor = Color.white },
            active    = { background = MakeTex(new Color(0.15f, 0.25f, 0.15f)), textColor = Color.white }
        };

        activeRowStyle = new GUIStyle(GUI.skin.button)
        {
            fontSize  = 11,
            alignment = TextAnchor.MiddleLeft,
            fontStyle = FontStyle.Bold,
            normal    = { background = MakeTex(new Color(0.20f, 0.45f, 0.20f)), textColor = Color.white },
            hover     = { background = MakeTex(new Color(0.25f, 0.55f, 0.25f)), textColor = Color.white }
        };

        inactiveRowStyle = new GUIStyle(GUI.skin.button)
        {
            fontSize  = 11,
            alignment = TextAnchor.MiddleLeft,
            normal    = { background = MakeTex(new Color(0.16f, 0.16f, 0.20f)), textColor = new Color(0.80f, 0.80f, 0.85f) },
            hover     = { background = MakeTex(new Color(0.22f, 0.22f, 0.30f)), textColor = Color.white }
        };

        closeButtonStyle = new GUIStyle(GUI.skin.button)
        {
            fontSize  = 10,
            alignment = TextAnchor.MiddleCenter,
            normal    = { background = MakeTex(new Color(0.25f, 0.25f, 0.30f)), textColor = Color.white },
            hover     = { background = MakeTex(new Color(0.35f, 0.35f, 0.40f)), textColor = Color.white }
        };
    }
}
