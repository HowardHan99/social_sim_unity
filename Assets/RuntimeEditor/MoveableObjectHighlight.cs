using UnityEngine;

/// <summary>
/// Draws a wireframe bounding box around an object so the user can SEE in the 3D scene
/// (not in a UI panel) that it is moveable in the runtime editor. Added/removed by
/// <see cref="RuntimeEditorManager"/> when editor mode is entered/exited or when an object
/// is bound as moveable at runtime. Non-destructive: it never touches the object's own
/// materials and cleans itself up on destroy.
/// </summary>
[DisallowMultipleComponent]
public class MoveableObjectHighlight : MonoBehaviour
{
    // 16-point path that walks all 12 edges of a box (some edges are revisited).
    static readonly int[] BoxPath = { 0, 1, 2, 3, 0, 4, 5, 1, 5, 6, 2, 6, 7, 3, 7, 4 };

    Color _color = new Color(0.2f, 1f, 0.45f, 1f);
    float _width = 0.02f;
    LineRenderer _line;
    Renderer[] _renderers;

    public void Configure(Color color, float width)
    {
        _color = color;
        _width = Mathf.Max(0.001f, width);
        if (_line != null)
            ApplyStyle();
    }

    void OnEnable()
    {
        EnsureLine();
        RefreshRenderers();
    }

    void OnDisable()
    {
        if (_line != null)
            _line.enabled = false;
    }

    void EnsureLine()
    {
        if (_line != null)
            return;

        var child = new GameObject("MoveableHighlight");
        child.transform.SetParent(transform, false);
        child.hideFlags = HideFlags.DontSave;

        _line = child.AddComponent<LineRenderer>();
        _line.useWorldSpace = true;
        _line.loop = false;
        _line.positionCount = BoxPath.Length;
        _line.numCornerVertices = 0;
        _line.numCapVertices = 0;
        _line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        _line.receiveShadows = false;
        ApplyStyle();
    }

    void ApplyStyle()
    {
        Shader shader = Shader.Find("Custom/RenderOnTop");
        if (shader == null) shader = Shader.Find("Unlit/Color");
        if (shader == null) shader = Shader.Find("Sprites/Default");

        var mat = new Material(shader);
        mat.color = _color;
        _line.material = mat;
        _line.startColor = _color;
        _line.endColor = _color;
        _line.startWidth = _width;
        _line.endWidth = _width;
    }

    void RefreshRenderers()
    {
        _renderers = GetComponentsInChildren<Renderer>(true);
    }

    void LateUpdate()
    {
        if (_line == null)
            return;

        if (!TryGetWorldBounds(out Bounds b))
        {
            _line.enabled = false;
            return;
        }

        _line.enabled = true;

        Vector3 c = b.center;
        Vector3 e = b.extents;
        // Corner order must match the indices used in BoxPath.
        Vector3[] corners =
        {
            c + new Vector3(-e.x, -e.y, -e.z), // 0
            c + new Vector3( e.x, -e.y, -e.z), // 1
            c + new Vector3( e.x,  e.y, -e.z), // 2
            c + new Vector3(-e.x,  e.y, -e.z), // 3
            c + new Vector3(-e.x, -e.y,  e.z), // 4
            c + new Vector3( e.x, -e.y,  e.z), // 5
            c + new Vector3( e.x,  e.y,  e.z), // 6
            c + new Vector3(-e.x,  e.y,  e.z), // 7
        };

        for (int i = 0; i < BoxPath.Length; i++)
            _line.SetPosition(i, corners[BoxPath[i]]);
    }

    bool TryGetWorldBounds(out Bounds bounds)
    {
        bounds = default;
        if (_renderers == null || _renderers.Length == 0)
            RefreshRenderers();

        bool any = false;
        if (_renderers != null)
        {
            foreach (Renderer r in _renderers)
            {
                if (r == null || !r.enabled)
                    continue;
                // Skip our own line and any nested highlight/gizmo renderers.
                if (r is LineRenderer)
                    continue;
                if (!any)
                {
                    bounds = r.bounds;
                    any = true;
                }
                else
                {
                    bounds.Encapsulate(r.bounds);
                }
            }
        }

        // No visible mesh -> don't draw a phantom box on empty space.
        return any;
    }

    void OnDestroy()
    {
        if (_line != null)
        {
            if (_line.sharedMaterial != null)
                Destroy(_line.sharedMaterial);
            Destroy(_line.gameObject);
        }
    }
}
