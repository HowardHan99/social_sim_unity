using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Binds the robot's navigation goal to an arbitrary scene object, so the default flag-cube
/// marker is only ONE way to express a goal. While an object is bound:
///  - the goal marker's flag visuals (TargetFlagCube/TargetFlagArrow) are hidden — the marker
///    root itself stays, because its transform is what feeds ROS goal publishing, completion
///    checks and metrics,
///  - a floating "ROBOT GOAL" text label hovers above the bound object and billboards to the
///    active camera,
///  - every LateUpdate the marker root (and CustomStartGoal.RobotGoalLocation, when the task
///    has one) is synced to the object's ground-anchored bounds position, so dragging the
///    object in world building moves the object, the label AND the actual goal as one unit.
/// Unbinding (or deleting the bound object) restores the flag-cube marker at the last synced
/// position.
/// </summary>
public class RobotGoalObjectBinding : MonoBehaviour
{
    public static RobotGoalObjectBinding Instance { get; private set; }

    const string LabelText = "ROBOT GOAL";
    const float LabelClearance = 0.2f;
    const string OverlayName = "RobotGoalOverlay";
    const float OverlayAlpha = 0.35f;
    // A real goal prop has a handful of renderers; an environment root has dozens/hundreds.
    const int MaxStartupBindRenderers = 24;
    // Shared by the surface overlay and the floating label, so "this object is the goal"
    // reads as one color.
    static readonly Color GoalColor = new Color(1f, 0.55f, 0.1f, 1f);

    [Header("Startup Binding")]
    [Tooltip("Bind this object as the robot goal as soon as the scene starts, without pressing the goal key in world building. Leave empty when this component sits ON the goal object itself — it then binds its own GameObject.")]
    public GameObject initialGoalObject;

    struct BoundsBoxFit
    {
        public Renderer source;
        public Transform box;
    }

    GameObject boundObject;
    GameObject label;
    TextMesh labelMesh;
    GameObject labelBackground;
    Material labelBackgroundMaterial;
    readonly List<BoundsBoxFit> boundsBoxPieces = new List<BoundsBoxFit>();
    readonly List<GameObject> overlayPieces = new List<GameObject>();
    GameObject overlayOwner;
    Material overlayMaterial;
    readonly List<GameObject> hiddenMarkerChildren = new List<GameObject>();
    GameObject markerWithHiddenChildren;

    public static GameObject BoundObject => Instance != null ? Instance.boundObject : null;

    /// <summary>
    /// Renderers of the goal UI (floating text + goal outline), so screenshot/ROI exports can
    /// hide them like the flag-cube marker. Deliberately excludes the bound object itself.
    /// </summary>
    public Renderer[] GoalUiRenderers
    {
        get
        {
            var result = new List<Renderer>();
            if (label != null)
                result.AddRange(label.GetComponentsInChildren<Renderer>(true));
            foreach (GameObject piece in overlayPieces)
            {
                if (piece != null)
                    result.AddRange(piece.GetComponentsInChildren<Renderer>(true));
            }
            return result.ToArray();
        }
    }

    /// <summary>
    /// Ground-plane (XZ) distance from a position to the bound goal object's footprint — the
    /// closest point of its renderer bounds, not its center. A bound object has physical size
    /// (a door face sits flush with a wall), so center distance can be physically unreachable
    /// within the sub-meter completion radius. Returns the fallback when nothing is bound.
    /// </summary>
    public static float GroundDistanceToGoal(Vector3 fromPosition, float fallback)
    {
        RobotGoalObjectBinding inst = Instance;
        if (inst == null || inst.boundObject == null || !inst.boundObject.activeInHierarchy)
            return fallback;
        if (!inst.TryGetWorldBounds(inst.boundObject, out Bounds bounds))
            return fallback;

        float dx = Mathf.Max(0f, Mathf.Abs(fromPosition.x - bounds.center.x) - bounds.extents.x);
        float dz = Mathf.Max(0f, Mathf.Abs(fromPosition.z - bounds.center.z) - bounds.extents.z);
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    /// <summary>
    /// Makes the given scene object the robot goal (rebinds if another object was bound).
    /// Returns false (with a Console error) when the target is goal plumbing — a marker or
    /// location node — which can never BE the goal.
    /// </summary>
    public static bool Bind(GameObject target)
    {
        if (target == null)
            return false;

        if (!CanBind(target, out string reason))
        {
            Debug.LogError($"[RobotGoal] Cannot bind '{target.name}': {reason}");
            return false;
        }

        if (Instance == null)
        {
            var host = new GameObject("RobotGoalObjectBinding");
            Instance = host.AddComponent<RobotGoalObjectBinding>();
        }

        Instance.BindInternal(target);
        return true;
    }

    /// <summary>
    /// The runtime Start/Target markers and the CustomStartGoal location nodes are goal
    /// PLUMBING — the binding drives their transforms, so binding one of them (or a parent)
    /// would make the goal chase wherever the marker happens to sit and tint its flag-cube
    /// preview orange. An object authored UNDER a location node (a door under "Goal") is fine.
    /// </summary>
    static bool CanBind(GameObject target, out string reason)
    {
        SEAN.Tasks.Base task = FindRobotTask();
        if (task != null)
        {
            bool touchesMarker =
                IsSelfOrContains(target, task.robotGoal) ||
                IsSelfOrContains(target, task.robotStart) ||
                (task.robotGoal != null && target.transform.IsChildOf(task.robotGoal.transform));
            if (touchesMarker)
            {
                reason = "it is (or contains) the runtime Start/Target marker. Put RobotGoalObjectBinding " +
                         "on the goal object itself (e.g. the door) instead.";
                return false;
            }
        }

        var custom = task as SEAN.Tasks.CustomStartGoal;
        if (custom != null &&
            (IsSelfOrContains(target, custom.RobotGoalLocation) || IsSelfOrContains(target, custom.RobotStartLocation)))
        {
            reason = "it is (or contains) a CustomStartGoal Start/Goal location node. Put RobotGoalObjectBinding " +
                     "on the goal object itself (e.g. the door) — the object may sit UNDER the Goal node, " +
                     "but the component must not be ON the Goal node.";
            return false;
        }

        reason = null;
        return true;
    }

    static bool IsSelfOrContains(GameObject target, GameObject infrastructure)
    {
        if (target == null || infrastructure == null)
            return false;
        return target == infrastructure || infrastructure.transform.IsChildOf(target.transform);
    }

    /// <summary>Clears the binding and restores the default flag-cube goal marker.</summary>
    public static void Unbind()
    {
        if (Instance != null)
            Instance.UnbindInternal();
    }

    void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
        }
        else if (Instance != this)
        {
            Debug.LogWarning($"[RobotGoal] Multiple RobotGoalObjectBinding components in the scene; only '{Instance.gameObject.name}' is used.");
            enabled = false;
        }
    }

    void Start()
    {
        // Runtime hosts created by Bind() are already bound before their Start runs; this
        // auto-bind is for components authored into the scene (startup binding).
        if (Instance != this || boundObject != null)
            return;

        GameObject target = initialGoalObject != null ? initialGoalObject : gameObject;
        // A bare manager object has nothing visible to serve as a goal.
        if (target == gameObject && GetComponentInChildren<Renderer>() == null)
            return;

        if (!CanBind(target, out string startupReason))
        {
            Debug.LogError($"[RobotGoal] NOT auto-binding '{target.name}': {startupReason}");
            return;
        }

        // Misconfiguration guard: a component left on (or a field pointed at) an environment
        // ROOT would tint every mesh under it and put the goal at their combined bounds.
        int rendererCount = target.GetComponentsInChildren<Renderer>().Length;
        if (rendererCount > MaxStartupBindRenderers)
        {
            Debug.LogError(
                $"[RobotGoal] NOT auto-binding '{target.name}': it contains {rendererCount} renderers, " +
                "which looks like an environment root rather than a single goal object. Put " +
                "RobotGoalObjectBinding on the goal object itself, or point Initial Goal Object at it.");
            return;
        }

        BindInternal(target);
    }

    void OnDestroy()
    {
        UnbindInternal();
        if (Instance == this)
            Instance = null;
    }

    void OnDisable()
    {
        // When this component sits ON the goal object and that object gets deactivated
        // (world-building delete), LateUpdate no longer runs to auto-unbind — do it here.
        if (boundObject != null && !boundObject.activeInHierarchy)
            UnbindInternal();
    }

    void BindInternal(GameObject target)
    {
        // Rebinding must release the previous object's goal outline back to the editor.
        if (boundObject != null && boundObject != target)
            UnbindInternal();

        boundObject = target;
        HideMarkerVisuals();
        EnsureLabel();
        UpdateLabelBackground();
        EnsureOverlay();
        SyncNow();

        // Footprint in the log makes a mis-bound goal (env root, combined mesh) obvious: a door
        // is ~1-2 m, an optimized scene chunk is tens of meters.
        string footprint = TryGetWorldBounds(target, out Bounds b)
            ? $"footprint {b.size.x:F1} x {b.size.z:F1} m"
            : "no renderers";
        Debug.Log($"[RobotGoal] '{target.name}' is now the robot goal ({footprint}).");
    }

    void UnbindInternal()
    {
        GameObject released = boundObject;
        boundObject = null;

        RestoreMarkerVisuals();
        if (label != null)
        {
            Destroy(label);
            label = null;
            labelMesh = null;
            labelBackground = null;
        }
        if (labelBackgroundMaterial != null)
        {
            Destroy(labelBackgroundMaterial);
            labelBackgroundMaterial = null;
        }
        DestroyOverlay();
        if (released != null)
            Debug.Log($"[RobotGoal] '{released.name}' is no longer the robot goal; default marker restored.");
    }

    void LateUpdate()
    {
        if (boundObject == null)
            return;

        // World-building delete deactivates objects rather than destroying them; either way a
        // goal object that vanished must not keep steering the robot — fall back to the cube.
        if (!boundObject.activeInHierarchy)
        {
            UnbindInternal();
            return;
        }

        // Self-heal: external cleanup may have destroyed the overlay pieces.
        EnsureOverlay();
        SyncNow();
    }

    /// <summary>
    /// Lays a translucent goal-colored tint over the bound object's own meshes: each renderer
    /// gets a child duplicate sharing the same mesh (and bones, for skinned meshes) drawn with
    /// a transparent material, so the object itself reads highlighted instead of being framed
    /// by a thin wireframe box. As children, the pieces move with the object automatically.
    /// </summary>
    void EnsureOverlay()
    {
        if (boundObject == null)
            return;
        if (overlayOwner == boundObject && overlayPieces.Count > 0 && overlayPieces[0] != null)
            return;

        DestroyOverlay();
        overlayOwner = boundObject;

        foreach (MeshRenderer source in boundObject.GetComponentsInChildren<MeshRenderer>())
        {
            if (source == null || source.gameObject.name == OverlayName)
                continue;

            // Static batching replaced this renderer's sharedMesh with the whole combined
            // batch at scene load, and its own submeshes no longer start at index 0 — copying
            // that mesh draws OTHER buildings (whatever sits in the leading submeshes). The
            // renderer's bounds stay object-accurate, so tint a box fitted to them instead.
            if (source.isPartOfStaticBatch)
            {
                CreateBoundsBoxPiece(source);
                continue;
            }

            MeshFilter filter = source.GetComponent<MeshFilter>();
            if (filter == null || filter.sharedMesh == null)
                continue;

            GameObject piece = CreateOverlayPiece(source.transform);
            piece.AddComponent<MeshFilter>().sharedMesh = filter.sharedMesh;
            ConfigureOverlayRenderer(piece.AddComponent<MeshRenderer>(), DrawnSubMeshCount(source, filter.sharedMesh));
        }

        foreach (SkinnedMeshRenderer source in boundObject.GetComponentsInChildren<SkinnedMeshRenderer>())
        {
            if (source == null || source.sharedMesh == null || source.gameObject.name == OverlayName)
                continue;

            GameObject piece = CreateOverlayPiece(source.transform);
            SkinnedMeshRenderer renderer = piece.AddComponent<SkinnedMeshRenderer>();
            renderer.sharedMesh = source.sharedMesh;
            renderer.bones = source.bones;
            renderer.rootBone = source.rootBone;
            ConfigureOverlayRenderer(renderer, DrawnSubMeshCount(source, source.sharedMesh));
        }
    }

    GameObject CreateOverlayPiece(Transform attachTo)
    {
        var piece = new GameObject(OverlayName);
        piece.hideFlags = HideFlags.DontSave;
        piece.transform.SetParent(attachTo, false);
        piece.layer = attachTo.gameObject.layer;
        overlayPieces.Add(piece);
        return piece;
    }

    void CreateBoundsBoxPiece(Renderer source)
    {
        // World-space box (not parented): it is refitted to the renderer's bounds every sync.
        var piece = new GameObject(OverlayName);
        piece.hideFlags = HideFlags.DontSave;
        piece.layer = source.gameObject.layer;
        overlayPieces.Add(piece);

        piece.AddComponent<MeshFilter>().sharedMesh = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
        ConfigureOverlayRenderer(piece.AddComponent<MeshRenderer>(), 1);

        boundsBoxPieces.Add(new BoundsBoxFit { source = source, box = piece.transform });
        FitBoundsBox(source, piece.transform);
    }

    static void FitBoundsBox(Renderer source, Transform box)
    {
        if (source == null || box == null)
            return;
        Bounds b = source.bounds;
        box.position = b.center;
        box.rotation = Quaternion.identity;
        // Slight inflation so the box's faces sit just outside the object's own surfaces.
        box.localScale = b.size * 1.02f + Vector3.one * 0.01f;
    }

    void ConfigureOverlayRenderer(Renderer renderer, int subMeshCount)
    {
        // A renderer draws only as many submeshes as it has materials.
        var materials = new Material[Mathf.Max(1, subMeshCount)];
        for (int i = 0; i < materials.Length; i++)
            materials[i] = GetOverlayMaterial();
        renderer.sharedMaterials = materials;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = false;
    }

    Material GetOverlayMaterial()
    {
        if (overlayMaterial != null)
            return overlayMaterial;

        // Sprites/Default: unlit, transparent, double-sided, tintable — draws as a translucent
        // film over the object's opaque surfaces.
        Shader shader = Shader.Find("Sprites/Default");
        if (shader == null)
            shader = Shader.Find("Unlit/Color");

        overlayMaterial = new Material(shader);
        Color tint = GoalColor;
        tint.a = OverlayAlpha;
        overlayMaterial.color = tint;
        return overlayMaterial;
    }

    void DestroyOverlay()
    {
        foreach (GameObject piece in overlayPieces)
        {
            if (piece != null)
                Destroy(piece);
        }
        overlayPieces.Clear();
        boundsBoxPieces.Clear();
        overlayOwner = null;

        if (overlayMaterial != null)
        {
            Destroy(overlayMaterial);
            overlayMaterial = null;
        }
    }

    void SyncNow()
    {
        bool hasBounds = TryGetWorldBounds(boundObject, out Bounds bounds);
        Vector3 goalPosition = hasBounds
            ? new Vector3(bounds.center.x, bounds.min.y, bounds.center.z)
            : boundObject.transform.position;
        Quaternion goalRotation = Quaternion.Euler(0f, boundObject.transform.eulerAngles.y, 0f);

        GameObject marker = FindRobotGoalMarker();
        if (marker != null)
        {
            if (markerWithHiddenChildren != marker)
                HideMarkerVisuals();
            MoveGoalTransformKeepingBoundObject(marker.transform, goalPosition, goalRotation);
        }

        // Tasks that re-read scene markers on (re)start would otherwise revert the goal, so the
        // Location marker must carry the bound pose too (same reason SyncMovedRobotMarkersIntoTask
        // writes dragged flags back in SessionReviewManager).
        var custom = FindRobotTask() as SEAN.Tasks.CustomStartGoal;
        if (custom != null && custom.RobotGoalLocation != null)
            MoveGoalTransformKeepingBoundObject(custom.RobotGoalLocation.transform, goalPosition, goalRotation);

        for (int i = 0; i < boundsBoxPieces.Count; i++)
            FitBoundsBox(boundsBoxPieces[i].source, boundsBoxPieces[i].box);

        UpdateLabel(hasBounds, bounds, goalPosition);
    }

    /// <summary>
    /// Moves a goal marker transform to the given pose. When the bound object was authored as a
    /// DESCENDANT of that marker (e.g. a door placed under the CustomStartGoal "Goal" node),
    /// moving the marker would drag the object along and then chase it again next frame — an
    /// endless drift. Restoring the object's world pose right after keeps only the marker moving.
    /// </summary>
    void MoveGoalTransformKeepingBoundObject(Transform goalTransform, Vector3 position, Quaternion rotation)
    {
        bool boundIsDescendant = boundObject != null && boundObject.transform.IsChildOf(goalTransform);
        Vector3 keepPosition = boundIsDescendant ? boundObject.transform.position : default;
        Quaternion keepRotation = boundIsDescendant ? boundObject.transform.rotation : Quaternion.identity;

        goalTransform.SetPositionAndRotation(position, rotation);

        if (boundIsDescendant)
            boundObject.transform.SetPositionAndRotation(keepPosition, keepRotation);
    }

    void HideMarkerVisuals()
    {
        RestoreMarkerVisuals();

        GameObject marker = FindRobotGoalMarker();
        if (marker != null)
        {
            HideChildrenExceptBound(marker);
            markerWithHiddenChildren = marker;
        }

        // The CustomStartGoal "Goal" node may carry its own preview flag cube. The synced
        // location node sits right on the bound object, so that preview would show up there —
        // hide it too, but never the bound object itself (it may be authored under this node).
        var custom = FindRobotTask() as SEAN.Tasks.CustomStartGoal;
        if (custom != null && custom.RobotGoalLocation != null && custom.RobotGoalLocation != marker)
            HideChildrenExceptBound(custom.RobotGoalLocation);
    }

    void HideChildrenExceptBound(GameObject parent)
    {
        foreach (Transform child in parent.transform)
        {
            if (boundObject != null &&
                (child.gameObject == boundObject || boundObject.transform.IsChildOf(child)))
                continue;

            if (child.gameObject.activeSelf)
            {
                child.gameObject.SetActive(false);
                hiddenMarkerChildren.Add(child.gameObject);
            }
        }
    }

    void RestoreMarkerVisuals()
    {
        foreach (GameObject child in hiddenMarkerChildren)
        {
            if (child != null)
                child.SetActive(true);
        }
        hiddenMarkerChildren.Clear();
        markerWithHiddenChildren = null;
    }

    void EnsureLabel()
    {
        if (label != null)
            return;

        // Deliberately NOT parented under this component or the bound object: a child label
        // would inflate the bound object's renderer bounds and push itself upward every frame.
        label = new GameObject("RobotGoalLabel");

        TextMesh text = label.AddComponent<TextMesh>();
        labelMesh = text;
        text.richText = true;
        text.text = LabelText;
        text.anchor = TextAnchor.LowerCenter;
        text.alignment = TextAlignment.Center;
        text.fontSize = 64;
        text.characterSize = 0.03f;
        text.color = GoalColor;

        Font font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        if (font != null)
        {
            text.font = font;
            MeshRenderer renderer = label.GetComponent<MeshRenderer>();
            if (renderer != null)
                renderer.material = font.material;
        }
    }

    /// <summary>
    /// Puts a white backdrop panel behind the label text so it stays readable against any
    /// scenery. Child of the label, so it follows position and billboarding automatically;
    /// sized from the rendered text measured with the label held upright.
    /// </summary>
    void UpdateLabelBackground()
    {
        if (label == null || labelMesh == null)
            return;

        MeshRenderer textRenderer = label.GetComponent<MeshRenderer>();
        if (textRenderer == null)
            return;

        if (labelBackground == null)
        {
            labelBackground = new GameObject("RobotGoalLabelBackground");
            labelBackground.hideFlags = HideFlags.DontSave;
            labelBackground.transform.SetParent(label.transform, false);
            labelBackground.AddComponent<MeshFilter>().sharedMesh = Resources.GetBuiltinResource<Mesh>("Cube.fbx");

            MeshRenderer renderer = labelBackground.AddComponent<MeshRenderer>();
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            Shader shader = Shader.Find("Sprites/Default");
            if (shader == null)
                shader = Shader.Find("Unlit/Color");
            labelBackgroundMaterial = new Material(shader);
            labelBackgroundMaterial.color = new Color(1f, 1f, 1f, 0.9f);
            renderer.sharedMaterial = labelBackgroundMaterial;
        }

        Quaternion previousRotation = label.transform.rotation;
        label.transform.rotation = Quaternion.identity;
        Bounds textBounds = textRenderer.bounds;
        label.transform.rotation = previousRotation;

        float width = textBounds.size.x;
        float height = textBounds.size.y;
        // Anchor is LowerCenter, so the text rises from the label origin; +Z puts the panel
        // just behind the glyphs from the viewing camera's side.
        labelBackground.transform.localPosition = new Vector3(0f, height * 0.5f, 0.02f);
        labelBackground.transform.localScale = new Vector3(width + 0.12f, height + 0.06f, 0.004f);
    }

    void UpdateLabel(bool hasBounds, Bounds bounds, Vector3 goalPosition)
    {
        if (label == null)
            return;

        Vector3 top = hasBounds
            ? new Vector3(bounds.center.x, bounds.max.y, bounds.center.z)
            : goalPosition + Vector3.up * 1.5f;
        label.transform.position = top + Vector3.up * LabelClearance;

        Camera cam = FindLabelCamera();
        if (cam != null)
            label.transform.rotation = cam.transform.rotation;
    }

    static Camera FindLabelCamera()
    {
        RuntimeEditorManager editor = RuntimeEditorManager.Instance;
        if (editor != null && editor.isEditorActive &&
            editor.ActiveRaycastCamera != null && editor.ActiveRaycastCamera.isActiveAndEnabled)
            return editor.ActiveRaycastCamera;

        Camera main = Camera.main;
        if (main != null && main.isActiveAndEnabled)
            return main;

        foreach (Camera cam in Camera.allCameras)
        {
            if (cam != null && cam.isActiveAndEnabled)
                return cam;
        }
        return null;
    }

    static SEAN.Tasks.Base FindRobotTask()
    {
        try
        {
            var sean = SEAN.SEAN.instance;
            return sean != null ? sean.robotTask : null;
        }
        catch (System.Exception)
        {
            // Scene without a (valid) SEAN rig.
            return null;
        }
    }

    static GameObject FindRobotGoalMarker()
    {
        SEAN.Tasks.Base task = FindRobotTask();
        return task != null ? task.robotGoal : null;
    }

    bool TryGetWorldBounds(GameObject root, out Bounds bounds)
    {
        bounds = default;
        bool hasBounds = false;

        foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>())
        {
            // LineRenderers are selection outlines/trails, not the object's real shape; the
            // floating label and the overlay pieces must never feed back into the bounds that
            // position them. Renderer.bounds is used as-is: it stays object-accurate even for
            // statically batched renderers (whose sharedMesh becomes the whole combined batch).
            if (renderer == null || renderer is LineRenderer)
                continue;
            if (renderer.gameObject.name == OverlayName)
                continue;
            if (label != null && renderer.transform.IsChildOf(label.transform))
                continue;

            if (!hasBounds)
            {
                bounds = renderer.bounds;
                hasBounds = true;
            }
            else
            {
                bounds.Encapsulate(renderer.bounds);
            }
        }

        return hasBounds;
    }

    /// <summary>
    /// How many submeshes the renderer actually draws: one per material, capped at the mesh's
    /// submesh count.
    /// </summary>
    static int DrawnSubMeshCount(Renderer renderer, Mesh mesh)
    {
        if (mesh == null)
            return 0;
        int materials = renderer.sharedMaterials != null ? renderer.sharedMaterials.Length : 0;
        return Mathf.Clamp(materials, 1, mesh.subMeshCount);
    }
}
