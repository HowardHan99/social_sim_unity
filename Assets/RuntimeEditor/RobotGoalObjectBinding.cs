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
    const float LabelClearance = 0.35f;

    GameObject boundObject;
    GameObject label;
    readonly List<GameObject> hiddenMarkerChildren = new List<GameObject>();
    GameObject markerWithHiddenChildren;

    public static GameObject BoundObject => Instance != null ? Instance.boundObject : null;

    /// <summary>Label renderers, so screenshot/ROI exports can hide the floating text.</summary>
    public Renderer[] LabelRenderers =>
        label != null ? label.GetComponentsInChildren<Renderer>(true) : new Renderer[0];

    /// <summary>Makes the given scene object the robot goal (rebinds if another object was bound).</summary>
    public static void Bind(GameObject target)
    {
        if (target == null)
            return;

        if (Instance == null)
        {
            var host = new GameObject("RobotGoalObjectBinding");
            Instance = host.AddComponent<RobotGoalObjectBinding>();
        }

        Instance.BindInternal(target);
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
            Instance = this;
    }

    void OnDestroy()
    {
        UnbindInternal();
        if (Instance == this)
            Instance = null;
    }

    void BindInternal(GameObject target)
    {
        boundObject = target;
        HideMarkerVisuals();
        EnsureLabel();
        SyncNow();
        Debug.Log($"[RobotGoal] '{target.name}' is now the robot goal.");
    }

    void UnbindInternal()
    {
        RestoreMarkerVisuals();
        if (label != null)
        {
            Destroy(label);
            label = null;
        }
        if (boundObject != null)
            Debug.Log($"[RobotGoal] '{boundObject.name}' is no longer the robot goal; default marker restored.");
        boundObject = null;
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

        SyncNow();
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
            marker.transform.SetPositionAndRotation(goalPosition, goalRotation);
        }

        // Tasks that re-read scene markers on (re)start would otherwise revert the goal, so the
        // Location marker must carry the bound pose too (same reason SyncMovedRobotMarkersIntoTask
        // writes dragged flags back in SessionReviewManager).
        var custom = FindRobotTask() as SEAN.Tasks.CustomStartGoal;
        if (custom != null && custom.RobotGoalLocation != null)
            custom.RobotGoalLocation.transform.SetPositionAndRotation(goalPosition, goalRotation);

        UpdateLabel(hasBounds, bounds, goalPosition);
    }

    void HideMarkerVisuals()
    {
        RestoreMarkerVisuals();

        GameObject marker = FindRobotGoalMarker();
        if (marker == null)
            return;

        foreach (Transform child in marker.transform)
        {
            if (child.gameObject.activeSelf)
            {
                child.gameObject.SetActive(false);
                hiddenMarkerChildren.Add(child.gameObject);
            }
        }
        markerWithHiddenChildren = marker;
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

        label = new GameObject("RobotGoalLabel");
        label.transform.SetParent(transform, false);

        TextMesh text = label.AddComponent<TextMesh>();
        text.text = LabelText;
        text.anchor = TextAnchor.LowerCenter;
        text.alignment = TextAlignment.Center;
        text.fontSize = 64;
        text.characterSize = 0.08f;
        text.color = new Color(1f, 0.55f, 0.1f, 1f);

        Font font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        if (font != null)
        {
            text.font = font;
            MeshRenderer renderer = label.GetComponent<MeshRenderer>();
            if (renderer != null)
                renderer.material = font.material;
        }
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

    static bool TryGetWorldBounds(GameObject root, out Bounds bounds)
    {
        bounds = default;
        bool hasBounds = false;

        foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>())
        {
            // LineRenderers are selection outlines/trails, not the object's real shape.
            if (renderer == null || renderer is LineRenderer)
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
}
