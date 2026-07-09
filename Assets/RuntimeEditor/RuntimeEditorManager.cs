using UnityEngine;
using System.Collections.Generic;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using TMPro;
using System.Collections;

[System.Serializable]
public class SpawnableObject
{
    public string id;
    public GameObject prefab;
    public Button spawnButton; // Reference to the UI button
}

public class RuntimeEditorManager : MonoBehaviour
{
    // Singleton Instance
    public static RuntimeEditorManager Instance { get; private set; }

    [Header("Editor Settings")]
    [Tooltip("Key to toggle runtime editor mode")]
    public KeyCode toggleKey = KeyCode.E;

    [Tooltip("When true, an external system (e.g. Session Review world building) owns enter/exit. The editor will NOT self-toggle on its hotkey and ESC only deselects — so ESC can't tear down the external camera/flow.")]
    public bool externalLifecycleControl = false;

    [Header("Editable Objects")]
    [Tooltip("Tag for objects that can be edited. Leave empty to edit all objects.")]
    public string editableTag = "Editable";

    [Tooltip("Manually assign objects to edit (optional)")]
    public List<GameObject> editableObjects = new List<GameObject>();

    [Header("Raycast Settings")]
    [Tooltip("Layers that can be selected. Set to 'Everything' to select all layers, or customize in Inspector")]
    public LayerMask selectableLayers = -1; // -1 means everything by default

    [Tooltip("Maximum raycast distance")]
    public float maxRaycastDistance = 1000f;

    [Tooltip("Ignore UI layer automatically")]
    public bool ignoreUILayer = true;

    [Header("References")]
    [Tooltip("Camera controller to enable/disable during edit mode")]
    public MonoBehaviour cameraController;

    [Tooltip("Specific camera to use for raycasting (leave empty to use Camera.main)")]
    public Camera raycastCamera;

    [Header("Runtime State")]
    public bool isEditorActive = false;

    [Header("Gizmo Rendering")]
    public string gizmoLayerName = "Gizmo";
    private Camera gizmoCamera;

    [Header("Spawn Objects")]
    public Canvas spawnCanvas;
    public List<SpawnableObject> spawnableObjects = new List<SpawnableObject>();
    public float spawnDistance = 3f;

    [Tooltip("When true, spawnCanvas is not shown in editor mode (e.g. Session Review World Building uses its own spawn UI).")]
    public bool suppressSpawnCanvas;

    [Header("Moveable Highlight")]
    [Tooltip("Outline every moveable object in the 3D scene while editor mode is active, so it's clear what can be dragged.")]
    public bool highlightMoveableObjects = true;

    [Tooltip("Wireframe color used to mark moveable objects in the scene.")]
    public Color moveableHighlightColor = new Color(0.2f, 1f, 0.45f, 1f);

    [Tooltip("Line width (world units) of the moveable outline.")]
    public float moveableHighlightWidth = 0.02f;

    [Tooltip("Allow binding the moveable script to a non-moveable object at runtime (only while bindMoveableKey is held, never on a plain click).")]
    public bool allowRuntimeBinding = true;

    [Tooltip("Hold this key and click a non-moveable object to make it moveable. A plain click never binds, so it can't spawn stray collider objects.")]
    public KeyCode bindMoveableKey = KeyCode.LeftShift;

    [Tooltip("Show an on-screen debug HUD describing what each click hits, resolves to, and the action taken.")]
    public bool showClickDebug = true;

    // Last-click diagnostics rendered by the debug HUD.
    private string _clickDebug = "(no click yet)";

    // ----- Box (marquee) selection: hold bindMoveableKey and left-drag to add every enclosed prop. -----
    private const float BoxDragThreshold = 6f; // pixels before a click becomes a drag
    private bool _boxPointerActive;
    private bool _isBoxSelecting;
    private Vector2 _boxStartScreen;
    private Vector2 _boxEndScreen;


    private GameObject currentSelectedObject;
    private RuntimeEditor currentEditor;
    private Camera mainCamera;
    private List<RuntimeEditor> allEditors = new List<RuntimeEditor>();

    public GameObject CurrentSelectedObject => currentSelectedObject;
    public Camera ActiveRaycastCamera => mainCamera;

    // Event that other systems can subscribe to
    public delegate void EditorModeChanged(bool isActive);
    public event EditorModeChanged OnEditorModeChanged;

    // ===== UNDO / REDO SYSTEM =====

    private interface IEditorAction
    {
        void Undo();
        void Redo();
    }

    private class SpawnAction : IEditorAction
    {
        readonly GameObject obj;
        readonly RuntimeEditorManager mgr;
        public SpawnAction(GameObject spawned, RuntimeEditorManager manager) { obj = spawned; mgr = manager; }

        public void Undo()
        {
            if (obj == null) return;
            if (mgr.currentSelectedObject == obj) mgr.DeselectObject();
            obj.SetActive(false);
        }

        public void Redo()
        {
            if (obj == null) return;
            obj.SetActive(true);
        }
    }

    private class DeleteAction : IEditorAction
    {
        readonly GameObject obj;
        readonly RuntimeEditorManager mgr;
        public DeleteAction(GameObject deleted, RuntimeEditorManager manager) { obj = deleted; mgr = manager; }

        public void Undo()
        {
            if (obj == null) return;
            obj.SetActive(true);
        }

        public void Redo()
        {
            if (obj == null) return;
            if (mgr.currentSelectedObject == obj) mgr.DeselectObject();
            obj.SetActive(false);
        }
    }

    private class TransformAction : IEditorAction
    {
        readonly GameObject obj;
        readonly Vector3 beforePos, afterPos;
        readonly Quaternion beforeRot, afterRot;

        public TransformAction(GameObject target, Vector3 bPos, Quaternion bRot, Vector3 aPos, Quaternion aRot)
        {
            obj = target;
            beforePos = bPos; beforeRot = bRot;
            afterPos = aPos; afterRot = aRot;
        }

        public void Undo()
        {
            if (obj == null) return;
            obj.transform.position = beforePos;
            obj.transform.rotation = beforeRot;
        }

        public void Redo()
        {
            if (obj == null) return;
            obj.transform.position = afterPos;
            obj.transform.rotation = afterRot;
        }
    }

    private readonly Stack<IEditorAction> undoStack = new Stack<IEditorAction>();
    private readonly Stack<IEditorAction> redoStack = new Stack<IEditorAction>();

    public int UndoCount => undoStack.Count;
    public int RedoCount => redoStack.Count;

    void Awake()
    {
        // Singleton pattern - ensure only one instance exists
        if (Instance == null)
        {
            Instance = this;
        }
        else
        {
            Debug.LogWarning("[RuntimeEditorManager] Multiple instances detected! Destroying duplicate.");
            Destroy(gameObject);
        }
    }

    void Start()
    {
        // Use manually assigned camera if available, otherwise use Camera.main
        if (raycastCamera != null)
        {
            mainCamera = raycastCamera;
            Debug.Log($"[RuntimeEditor] Using manually assigned raycast camera: {mainCamera.name}");
        }
        else
        {
            mainCamera = Camera.main;
            
            if (mainCamera == null)
            {
                Debug.LogError("Camera.main is NULL! Looking for any active camera...");
                Camera[] allCameras = FindObjectsOfType<Camera>();
                if (allCameras.Length > 0)
                {
                    mainCamera = allCameras[0];
                    Debug.LogWarning($"Using first found camera: {mainCamera.name}");
                }
            }
            else
            {
                Debug.Log($"[RuntimeEditor] Using Camera.main: {mainCamera.name}");
            }
        }
        
        if (mainCamera != null)
        {
            EnsureCameraCullingCoversSelectableLayers(mainCamera);
            ExcludeGizmoLayerFromMainCamera();
            // Debug.Log($"[RuntimeEditor] Camera Details:");
            // Debug.Log($"  - Name: {mainCamera.name}, GameObject: {mainCamera.gameObject.name}");
            // Debug.Log($"  - Position: {mainCamera.transform.position}, Rotation: {mainCamera.transform.rotation.eulerAngles}");
            // Debug.Log($"  - FOV: {mainCamera.fieldOfView}, Near: {mainCamera.nearClipPlane}, Far: {mainCamera.farClipPlane}");
            // Debug.Log($"  - Viewport Rect: {mainCamera.rect}");
            // Debug.Log($"  - RenderTexture: {(mainCamera.targetTexture != null ? mainCamera.targetTexture.name : "None (renders to screen)")}");
        }
        else
        {
            Debug.LogError("[RuntimeEditor] No camera found! Raycasting will not work.");
        }

        RefreshGizmoCamera();
        SetupSpawnButtons();

        // Configure layer mask
        if (ignoreUILayer)
        {
            int uiLayer = LayerMask.NameToLayer("UI");
            if (uiLayer != -1)
            {
                selectableLayers &= ~(1 << uiLayer);
            }
        }

        // Always exclude "Ignore Raycast" layer
        int ignoreRaycastLayer = LayerMask.NameToLayer("Ignore Raycast");
        if (ignoreRaycastLayer != -1)
        {
            selectableLayers &= ~(1 << ignoreRaycastLayer);
        }

        // Always exclude the gizmo layer from selection. Gizmo handles are primitives with colliders;
        // in free-cam 3D they sit between the camera and objects and would otherwise swallow the
        // selection ray (intermittent "click stops working"). Excluding it lets clicks pass through to
        // the real object behind. The gizmo's own drag raycast uses its own mask, so dragging still works.
        int gizmoSelLayer = LayerMask.NameToLayer(gizmoLayerName);
        if (gizmoSelLayer != -1)
        {
            selectableLayers &= ~(1 << gizmoSelLayer);
        }

        // Initialize editor state without clobbering a runtime activation
        if (!isEditorActive)
            SetEditorMode(false);
    }

    void Update()
    {
        SyncGizmoCamera();

        // Toggle editor mode (suppressed when an external system owns the lifecycle).
        if (!externalLifecycleControl && Input.GetKeyDown(toggleKey))
        {
            ToggleEditorMode();
        }

        // If editor is active, handle object selection and undo/redo/delete
        if (isEditorActive)
        {
            HandleObjectSelection();
            HandleBoxSelection();

            bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            if (ctrl && Input.GetKeyDown(KeyCode.Z))
            {
                bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                if (shift) RedoLastAction(); else UndoLastAction();
            }
            else if (ctrl && Input.GetKeyDown(KeyCode.Y))
            {
                RedoLastAction();
            }

            if (Input.GetKeyDown(KeyCode.Delete) && currentSelectedObject != null)
                DeleteSelectedObject();
        }
    }

    void ToggleEditorMode()
    {
        SetEditorMode(!isEditorActive);
    }

    public void SetEditorMode(bool active)
    {
        isEditorActive = active;

        if (isEditorActive)
        {
            EnterEditorMode();
        }
        else
        {
            ExitEditorMode();
        }

        // Notify other systems
        OnEditorModeChanged?.Invoke(isEditorActive);
    }

    public void SetEditorCamera(Camera camera, MonoBehaviour controller = null)
    {
        if (camera == null)
        {
            Debug.LogWarning("[RuntimeEditor] Cannot assign a null editor camera.");
            return;
        }

        raycastCamera = camera;
        mainCamera = camera;
        EnsureCameraCullingCoversSelectableLayers(mainCamera);
        ExcludeGizmoLayerFromMainCamera();

        if (controller != null)
            cameraController = controller;

        RefreshGizmoCamera();

        if (currentEditor != null)
            currentEditor.SetRaycastCamera(mainCamera);
    }

    void EnterEditorMode()
    {
        // Enable camera controller if assigned
        if (cameraController != null)
        {
            cameraController.enabled = true;
        }

        // Show cursor
        Cursor.visible = true;
        Cursor.lockState = CursorLockMode.None;

        // Prepare editable objects
        PrepareEditableObjects();

        // Show in the 3D scene which objects are moveable.
        RefreshMoveableHighlights(true);

        if (spawnCanvas != null)
            spawnCanvas.gameObject.SetActive(!suppressSpawnCanvas);

        // Pause the game when entering editor mode
        if (PauseManager.Instance != null)
        {
            PauseManager.Instance.PauseGame();
            Debug.Log("Game paused - Editor mode active");
        }

        Debug.Log("Editor Mode Enabled - Press " + toggleKey + " or ESC to exit. Click objects to edit them.");
    }

    void ExitEditorMode()
    {
        // Deselect current object
        if (currentSelectedObject != null)
        {
            DeselectObject();
        }

        // Hide the moveable outlines.
        RefreshMoveableHighlights(false);

        // Cleanup all editors
        CleanupAllEditors();

        if (spawnCanvas != null)
        {
            spawnCanvas.gameObject.SetActive(false);
        }

        // Unpause the game when exiting editor mode
        if (PauseManager.Instance != null)
        {
            PauseManager.Instance.UnpauseGame();
            Debug.Log("Game unpaused - Normal mode resumed");
        }

        Debug.Log("Editor Mode Disabled - Normal mode resumed.");
    }

    void PrepareEditableObjects()
    {
        // If no objects manually assigned, find them by tag
        if (editableObjects.Count == 0 && !string.IsNullOrEmpty(editableTag))
        {
            GameObject[] taggedObjects = GameObject.FindGameObjectsWithTag(editableTag);
            editableObjects.AddRange(taggedObjects);
        }

        foreach (var obstacle in FindObjectsOfType<SEAN.Scenario.Obstacles.TrackedObstacle>())
        {
            if (obstacle != null && !editableObjects.Contains(obstacle.gameObject))
            {
                editableObjects.Add(obstacle.gameObject);
                EnsureObjectLayerIsSelectable(obstacle.gameObject);
            }
        }

        // Same discovery pipeline as the mailbox above, but for the ROS-free MoveableProp marker:
        // anything tagged with MoveableProp becomes draggable when editor mode starts.
        foreach (var prop in FindObjectsOfType<MoveableProp>())
        {
            if (prop != null && !editableObjects.Contains(prop.gameObject))
            {
                editableObjects.Add(prop.gameObject);
                EnsureObjectLayerIsSelectable(prop.gameObject);
            }
        }

        // Robots: register the robot root so it can be dragged around during world editing (the game is
        // paused in editor mode, so this doesn't fight the controller). Selection resolves to this root.
        foreach (var robot in FindObjectsOfType<SEAN.Scenario.Robot>())
        {
            if (robot != null && !robot.gameObject.isStatic && !editableObjects.Contains(robot.gameObject))
            {
                editableObjects.Add(robot.gameObject);
                EnsureObjectLayerIsSelectable(robot.gameObject);
            }
        }

        if (editableObjects.Count == 0)
        {
            // Debug.LogWarning("No editable objects found! Assign objects manually or set a tag.");
        }
    }

    void HandleObjectSelection()
    {
        // Left click to select object
        if (Input.GetMouseButtonDown(0))
        {
            if (IsClickOnUI())
            {
                Debug.Log("[Raycast] Pointer is over UI. Ignoring click.");
                _clickDebug = "Click absorbed by a UI element (EventSystem). A full-screen Canvas can block selection.";
                return;
            }

            if (mainCamera == null)
            {
                Debug.LogError("MainCamera is NULL! Cannot perform raycast.");
                _clickDebug = "ERROR: raycast camera is NULL — selection cannot work.";
                return;
            }

            // Get ray from camera accounting for viewport rect
            Ray ray = GetRayFromScreenPoint(Input.mousePosition);
            RaycastHit hit;

            bool shiftHeld = allowRuntimeBinding && Input.GetKey(bindMoveableKey);

            GameObject clickedObject = null;
            GameObject editableObject = null;

            // Primary: precise collider raycast.
            if (Physics.Raycast(ray, out hit, maxRaycastDistance, selectableLayers))
            {
                clickedObject = hit.collider.gameObject;

                // To ignore if pointer is over UI
                if (clickedObject.layer == LayerMask.NameToLayer("UI"))
                {
                    Debug.Log("[Raycast] Pointer is over UI. Ignoring click.");
                    _clickDebug = $"Hit '{clickedObject.name}' on UI layer — ignored.";
                    return;
                }

                GameObject resolved = ResolveEditableObject(clickedObject);
                // Neglect static objects (ground, buildings, scenery) — they aren't draggable props.
                bool staticBlocked = clickedObject.isStatic || (resolved != null && resolved.isStatic);
                if (resolved != null && !staticBlocked)
                    editableObject = resolved;
            }

            // Fallback: a moveable prop's collider may be missing or tight-fitted to its opaque
            // pixels (e.g. a PNG sprite/quad), so a click on a transparent region misses the
            // collider raycast (or passes through to the static ground behind it). Select the
            // front-most moveable object whose visual bounds the cursor ray passes through.
            if (editableObject == null)
                editableObject = FindMoveableUnderCursor(ray);

            if (editableObject != null)
            {
                SelectObject(editableObject);
                _clickDebug = clickedObject != null
                    ? $"SELECTED: {editableObject.name}  (collider hit '{clickedObject.name}')"
                    : $"SELECTED: {editableObject.name}  (via transparent/bounds fallback)";
            }
            else if (shiftHeld && clickedObject != null)
            {
                // Explicit bind: only when the user holds the bind key.
                GameObject bound = MakeObjectMoveable(clickedObject);
                if (bound != null)
                {
                    SelectObject(bound);
                    _clickDebug =
                        $"MADE MOVEABLE + SELECTED: {bound.name}" +
                        (bound != clickedObject ? $"  (resolved up from clicked '{clickedObject.name}')" : "");
                }
                else
                {
                    _clickDebug = $"HIT: {clickedObject.name} -> bind FAILED (no visible mesh on it or its parents).";
                }
            }
            else if (clickedObject != null)
            {
                Debug.Log($"[Raycast] '{clickedObject.name}' is not moveable. Hold [{bindMoveableKey}] and click to make it moveable.");
                _clickDebug =
                    $"HIT: {clickedObject.name} [layer {LayerMask.LayerToName(clickedObject.layer)}]\n" +
                    $"-> NOT moveable. Hold [{bindMoveableKey}] + click to add it.";
            }
            else
            {
                Debug.Log("[Raycast] No objects hit by raycast");
                _clickDebug =
                    $"NO HIT (cam '{mainCamera.name}', mask {LayerMaskToString(selectableLayers)}).\n" +
                    "Nothing selectable under the cursor.";
            }
        }

        // Press Escape to deselect or exit editor mode
        if (Input.GetKeyDown(KeyCode.Escape))
        {
            if (currentSelectedObject != null)
            {
                DeselectObject();
            }
            else if (!externalLifecycleControl)
            {
                // If no object selected, exit editor mode — but only when we own the lifecycle.
                // Under external control (world building), ESC must not tear down the editor/camera.
                SetEditorMode(false);
            }
        }

        // Right click to deselect
        if (Input.GetMouseButtonDown(1))
        {
            if (currentSelectedObject != null)
            {
                DeselectObject();
            }
        }
    }

    /// <summary>
    /// Marquee selection: hold <see cref="bindMoveableKey"/> and left-drag a box to add every enclosed,
    /// non-static prop to the editable list in one go. A drag below <see cref="BoxDragThreshold"/> pixels is
    /// treated as a normal click (handled by <see cref="HandleObjectSelection"/>) and does nothing here.
    /// </summary>
    void HandleBoxSelection()
    {
        if (!allowRuntimeBinding)
            return;

        bool shift = Input.GetKey(bindMoveableKey);

        // Start tracking on shift + left-press (ignore presses that start over UI).
        if (shift && Input.GetMouseButtonDown(0) && !IsClickOnUI())
        {
            _boxPointerActive = true;
            _isBoxSelecting = false;
            _boxStartScreen = _boxEndScreen = (Vector2)Input.mousePosition;
            return;
        }

        if (!_boxPointerActive)
            return;

        if (Input.GetMouseButton(0))
        {
            _boxEndScreen = (Vector2)Input.mousePosition;
            if (!_isBoxSelecting && (_boxEndScreen - _boxStartScreen).magnitude > BoxDragThreshold)
                _isBoxSelecting = true;
            return;
        }

        // Mouse released — apply if it was an actual drag.
        if (Input.GetMouseButtonUp(0))
        {
            if (_isBoxSelecting)
                BoxAddObjects(MakeScreenRect(_boxStartScreen, _boxEndScreen));
            _boxPointerActive = false;
            _isBoxSelecting = false;
        }
    }

    /// <summary>Adds every non-static prop whose screen position falls inside the marquee to the editable list.</summary>
    void BoxAddObjects(Rect screenRect)
    {
        if (mainCamera == null)
            return;

        int uiLayer = LayerMask.NameToLayer("UI");
        var added = new HashSet<GameObject>();
        GameObject last = null;

        foreach (Renderer r in FindObjectsOfType<Renderer>())
        {
            if (r == null || r is LineRenderer)
                continue;

            GameObject go = r.gameObject;
            if (!go.activeInHierarchy || go.isStatic || go.layer == uiLayer)
                continue;

            // Project the renderer center into the same full-screen space the marquee is drawn in.
            Vector3 vp = mainCamera.WorldToViewportPoint(r.bounds.center);
            if (vp.z <= 0f)
                continue; // behind the camera
            var screen = new Vector2(vp.x * Screen.width, vp.y * Screen.height);
            if (!screenRect.Contains(screen))
                continue;

            GameObject target = MakeObjectMoveable(go);
            if (target != null && added.Add(target))
                last = target;
        }

        if (last != null)
            SelectObject(last);

        _clickDebug = $"BOX SELECT: added {added.Count} object(s) to the editable list.";
        Debug.Log($"[RuntimeEditor] Box select added {added.Count} object(s).");
    }

    /// <summary>Builds a Rect from two screen points (mouse-space, bottom-left origin).</summary>
    static Rect MakeScreenRect(Vector2 a, Vector2 b)
    {
        float xMin = Mathf.Min(a.x, b.x);
        float yMin = Mathf.Min(a.y, b.y);
        return new Rect(xMin, yMin, Mathf.Abs(a.x - b.x), Mathf.Abs(a.y - b.y));
    }

    /// <summary>
    /// Creates a ray from screen point accounting for custom viewport rect.
    /// Maps full screen (0,0 to Screen.width, Screen.height) to camera viewport (0,0 to 1,1)
    /// so clicking anywhere on screen works correctly with the camera's custom viewport.
    /// </summary>
    Ray GetRayFromScreenPoint(Vector3 screenPoint)
    {
        if (mainCamera == null)
            return new Ray();
        
        // Get camera's viewport rect for debugging
        Rect viewportRect = mainCamera.rect;
        // Debug.Log($"[Camera Info] Viewport Rect: {viewportRect}, Target Texture: {(mainCamera.targetTexture != null ? mainCamera.targetTexture.name : "None")}, Screen Size: {Screen.width}x{Screen.height}");
        
        // If camera has a render texture, we need to handle this differently
        if (mainCamera.targetTexture != null)
        {
            // Debug.LogWarning("[Raycast] Camera is rendering to a RenderTexture. Mouse position may need adjustment for ROS integration.");
            // For render texture, use ScreenPointToRay directly
            return mainCamera.ScreenPointToRay(screenPoint);
        }
        
        // Convert screen point to normalized screen coordinates (0-1)
        float normalizedScreenX = screenPoint.x / Screen.width;
        float normalizedScreenY = screenPoint.y / Screen.height;
        
        // Map full screen to viewport space (0-1)
        // This allows clicking anywhere on screen to raycast through the camera's view
        Vector3 viewportPoint = new Vector3(normalizedScreenX, normalizedScreenY, 0);
        
        // Debug.Log($"[Ray Calculation] Normalized Screen: ({normalizedScreenX:F3}, {normalizedScreenY:F3}) -> Viewport: ({viewportPoint.x:F3}, {viewportPoint.y:F3})");
        
        // Use ViewportPointToRay which properly handles the camera's viewport rect
        return mainCamera.ViewportPointToRay(viewportPoint);
    }

    bool IsObjectEditable(GameObject obj)
    {
        return ResolveEditableObject(obj) != null;
    }

    GameObject ResolveEditableObject(GameObject obj)
    {
        if (obj == null)
            return null;

        // Robot special-case: clicking ANY part under a robot selects the whole robot, even if the robot
        // root isn't registered and regardless of which child collider was hit. This makes robots reliably
        // clickable (e.g. after switching to a 3D view where a different sub-mesh is under the cursor).
        var robot = obj.GetComponentInParent<SEAN.Scenario.Robot>();
        if (robot != null && !robot.gameObject.isStatic)
            return robot.gameObject;

        // If no restrictions, allow all objects
        if (editableObjects.Count == 0 && string.IsNullOrEmpty(editableTag))
        {
            return obj;
        }

        Transform current = obj.transform;
        while (current != null)
        {
            GameObject candidate = current.gameObject;

            if (editableObjects.Contains(candidate))
                return candidate;

            if (!string.IsNullOrEmpty(editableTag) && candidate.CompareTag(editableTag))
                return candidate;

            current = current.parent;
        }

        return null;
    }

    /// <summary>
    /// Returns the front-most moveable object (registered editable, or any TrackedObstacle prop)
    /// whose world-space renderer bounds the cursor ray passes through. Used as a fallback when the
    /// precise collider raycast misses — e.g. clicking a transparent region of a PNG sprite/quad
    /// whose collider is missing or tight-fitted to opaque pixels. Returns null if none match.
    /// </summary>
    GameObject FindMoveableUnderCursor(Ray ray)
    {
        GameObject best = null;
        float bestDist = float.MaxValue;

        void Consider(GameObject root)
        {
            if (root == null || !root.activeInHierarchy || root.isStatic)
                return;
            foreach (Renderer r in root.GetComponentsInChildren<Renderer>())
            {
                if (r == null || r is LineRenderer || !r.enabled)
                    continue;
                if (r.bounds.IntersectRay(ray, out float d) && d < bestDist)
                {
                    bestDist = d;
                    best = root;
                }
            }
        }

        for (int i = 0; i < editableObjects.Count; i++)
            Consider(editableObjects[i]);
        foreach (var obstacle in FindObjectsOfType<SEAN.Scenario.Obstacles.TrackedObstacle>())
            Consider(obstacle.gameObject);

        return best;
    }

    public void SelectObject(GameObject obj)
    {
        // Deselect previous object if any
        if (currentSelectedObject != null && currentSelectedObject != obj)
        {
            DeselectObject();
        }

        currentSelectedObject = obj;

        // Add or get RuntimeEditor component
        currentEditor = obj.GetComponent<RuntimeEditor>();
        if (currentEditor == null)
        {
            currentEditor = obj.AddComponent<RuntimeEditor>();
            allEditors.Add(currentEditor);
        }

        // Pass the raycast camera to the editor
        currentEditor.SetRaycastCamera(mainCamera);

        // Enable the editor
        currentEditor.enabled = true;

        Debug.Log("Selected: " + obj.name + " - Press T for translate, R for rotate");
    }

    public void DeselectObject()
    {
        if (currentEditor != null)
        {
            currentEditor.ShowGizmo(false);
            currentEditor.enabled = false;
        }

        currentSelectedObject = null;
        currentEditor = null;

        Debug.Log("Object deselected");
    }

    void CleanupAllEditors()
    {
        // Remove all RuntimeEditor components that were added
        foreach (RuntimeEditor editor in allEditors)
        {
            if (editor != null)
            {
                Destroy(editor);
            }
        }

        allEditors.Clear();
        currentEditor = null;
        currentSelectedObject = null;
    }

    void OnGUI()
    {
        if (!isEditorActive) return;

        // Legend explaining the moveable outline; shown in both normal and World Building modes.
        DrawMoveableLegend();

        // Debug HUD describing the last click; shown in both modes when enabled.
        DrawClickDebug();

        // Marquee rectangle while box-selecting.
        DrawSelectionBox();

        if (suppressSpawnCanvas)
            return;

        // Simple UI overlay
        GUIStyle style = new GUIStyle(GUI.skin.label);
        style.fontSize = 16;
        style.normal.textColor = Color.white;
        style.alignment = TextAnchor.UpperLeft;

        string helpText = "=== RUNTIME EDITOR MODE ===\n";
        helpText += "Press [" + toggleKey + "] or [ESC] to exit\n";
        helpText += "Click object to select\n";

        if (currentSelectedObject != null)
        {
            helpText += "\nSelected: " + currentSelectedObject.name + "\n";
            helpText += "[T] Translate Mode\n";
            helpText += "[R] Rotate Mode\n";
            helpText += "[ESC] Deselect (press again to exit)";
        }
        else
        {
            helpText += "\nNo object selected\n";
            helpText += "[ESC] Exit editor mode";
        }

        GUI.Label(new Rect(10, 10, 300, 200), helpText, style);
    }

    /// <summary>Draws the translucent marquee rectangle (in GUI space) while box-selecting.</summary>
    void DrawSelectionBox()
    {
        if (!_isBoxSelecting)
            return;

        Rect r = MakeScreenRect(_boxStartScreen, _boxEndScreen);
        // Convert mouse-space (bottom-left, y up) to GUI-space (top-left, y down).
        Rect gui = new Rect(r.xMin, Screen.height - r.yMax, r.width, r.height);

        Color prev = GUI.color;
        GUI.color = new Color(moveableHighlightColor.r, moveableHighlightColor.g, moveableHighlightColor.b, 0.15f);
        GUI.DrawTexture(gui, Texture2D.whiteTexture);
        GUI.color = new Color(moveableHighlightColor.r, moveableHighlightColor.g, moveableHighlightColor.b, 0.9f);
        GUI.Box(gui, GUIContent.none);
        GUI.color = prev;
    }

    /// <summary>
    /// Top-right debug HUD showing the camera, editable count, current selection and what the last click did.
    /// </summary>
    void DrawClickDebug()
    {
        if (!showClickDebug)
            return;

        string cam = mainCamera != null ? mainCamera.name : "NULL";
        string sel = currentSelectedObject != null ? currentSelectedObject.name : "(none)";

        string text =
            "=== CLICK DEBUG ===\n" +
            $"Cam: {cam}\n" +
            $"Editable count: {editableObjects.Count}\n" +
            $"Selected: {sel}\n" +
            $"Bind key held: {(allowRuntimeBinding && Input.GetKey(bindMoveableKey))}\n" +
            $"Last click:\n{_clickDebug}";

        var style = new GUIStyle(GUI.skin.box)
        {
            fontSize = 12,
            alignment = TextAnchor.UpperLeft,
            wordWrap = true,
            richText = false
        };
        style.normal.textColor = Color.yellow;

        float w = 360f;
        float h = 168f;
        GUI.Box(new Rect(Screen.width - w - 12f, 12f, w, h), text, style);
    }

    /// <summary>
    /// Bottom-left on-screen legend explaining what the in-scene moveable outline means and how to
    /// bind non-moveable objects at runtime.
    /// </summary>
    void DrawMoveableLegend()
    {
        if (!highlightMoveableObjects)
            return;

        const float pad = 10f;
        const float swatch = 16f;
        float panelW = 290f;
        float panelH = allowRuntimeBinding ? 92f : 48f;
        var panel = new Rect(16f, Screen.height - panelH - 16f, panelW, panelH);

        GUI.Box(panel, GUIContent.none);

        var label = new GUIStyle(GUI.skin.label)
        {
            fontSize = 13,
            alignment = TextAnchor.MiddleLeft,
            wordWrap = true
        };
        label.normal.textColor = Color.white;

        // Color swatch matching the wireframe color.
        var swatchRect = new Rect(panel.x + pad, panel.y + pad, swatch, swatch);
        Color prev = GUI.color;
        GUI.color = moveableHighlightColor;
        GUI.DrawTexture(swatchRect, Texture2D.whiteTexture);
        GUI.color = prev;

        GUI.Label(
            new Rect(swatchRect.xMax + 8f, panel.y + pad - 2f, panelW - swatch - pad * 2f - 8f, 20f),
            "Outlined = moveable (drag it)", label);

        if (allowRuntimeBinding)
        {
            GUI.Label(
                new Rect(panel.x + pad, panel.y + pad + swatch + 6f, panelW - pad * 2f, 58f),
                $"Hold [{bindMoveableKey}] + click an un-outlined object to make it moveable.\n" +
                $"Hold [{bindMoveableKey}] + drag a box to add many at once.", label);
        }
    }

    // ===== LAYER MANAGEMENT HELPER METHODS =====
    
    /// <summary>
    /// Checks if a specific layer is included in the LayerMask
    /// </summary>
    bool IsLayerInMask(int layer, LayerMask layerMask)
    {
        return ((1 << layer) & layerMask) != 0;
    }

    /// <summary>
    /// Converts LayerMask to readable string showing included layers
    /// </summary>
    string LayerMaskToString(LayerMask layerMask)
    {
        if (layerMask.value == -1)
            return "Everything";
        
        if (layerMask.value == 0)
            return "Nothing";

        List<string> layerNames = new List<string>();
        
        for (int i = 0; i < 32; i++)
        {
            if (IsLayerInMask(i, layerMask))
            {
                string layerName = LayerMask.LayerToName(i);
                if (!string.IsNullOrEmpty(layerName))
                {
                    layerNames.Add($"{layerName}({i})");
                }
                else
                {
                    layerNames.Add($"Layer{i}");
                }
            }
        }

        return layerNames.Count > 0 ? string.Join(", ", layerNames) : "Custom Mask";
    }

    /// <summary>
    /// Add a layer to the selectable layers mask (call at runtime to modify)
    /// </summary>
    public void AddSelectableLayer(string layerName)
    {
        int layer = LayerMask.NameToLayer(layerName);
        if (layer != -1)
        {
            selectableLayers |= (1 << layer);
            Debug.Log($"[RuntimeEditor] Added layer '{layerName}' to selectable layers");
        }
        else
        {
            Debug.LogWarning($"[RuntimeEditor] Layer '{layerName}' not found!");
        }
    }

    /// <summary>
    /// Remove a layer from the selectable layers mask (call at runtime to modify)
    /// </summary>
    public void RemoveSelectableLayer(string layerName)
    {
        int layer = LayerMask.NameToLayer(layerName);
        if (layer != -1)
        {
            selectableLayers &= ~(1 << layer);
            Debug.Log($"[RuntimeEditor] Removed layer '{layerName}' from selectable layers");
        }
        else
        {
            Debug.LogWarning($"[RuntimeEditor] Layer '{layerName}' not found!");
        }
    }

    /// <summary>
    /// Set selectable layers to only specific layers
    /// </summary>
    public void SetSelectableLayers(params string[] layerNames)
    {
        selectableLayers = 0; // Clear all
        
        foreach (string layerName in layerNames)
        {
            int layer = LayerMask.NameToLayer(layerName);
            if (layer != -1)
            {
                selectableLayers |= (1 << layer);
            }
            else
            {
                Debug.LogWarning($"[RuntimeEditor] Layer '{layerName}' not found!");
            }
        }
        
        Debug.Log($"[RuntimeEditor] Selectable layers set to: {LayerMaskToString(selectableLayers)}");
    }

    public void SetupGizmoCamera()
    {
        if (mainCamera == null) return;
        int gizmoLayer = LayerMask.NameToLayer(gizmoLayerName);
        if (gizmoLayer == -1)
        {
            Debug.LogError($"[RuntimeEditor] Gizmo layer '{gizmoLayerName}' not found!");
            return;
        }
        GameObject gizmoCameraObject = new GameObject("Gizmo Camera");
        gizmoCameraObject.transform.SetParent(mainCamera.transform);
        gizmoCameraObject.transform.localPosition = Vector3.zero;
        gizmoCameraObject.transform.localRotation = Quaternion.identity;
        
        gizmoCamera = gizmoCameraObject.AddComponent<Camera>();
        gizmoCamera.CopyFrom(mainCamera);
        gizmoCamera.cullingMask = 1 << gizmoLayer;
        gizmoCamera.clearFlags = CameraClearFlags.Depth;
        gizmoCamera.depth = mainCamera.depth + 1;
        ExcludeGizmoLayerFromMainCamera();
    }

    public void RefreshGizmoCamera()
    {
        if (gizmoCamera != null)
        {
            Destroy(gizmoCamera.gameObject);
            gizmoCamera = null;
        }

        SetupGizmoCamera();
    }

    void SetupSpawnButtons()
    {
        foreach (var spawnableObject in spawnableObjects)
        {
            if (spawnableObject.spawnButton != null && spawnableObject.prefab != null)
            {
                // Get the prefab name
                string objectName = spawnableObject.prefab.name;
                
                // Set button text to "Add + [object name]"
                TMP_Text buttonText = spawnableObject.spawnButton.GetComponentInChildren<TMP_Text>();
                if (buttonText != null)
                {
                    buttonText.text = "Add + " + objectName;
                }
                else
                {
                    // Fallback to regular Text component if TMP_Text is not found
                    Text legacyButtonText = spawnableObject.spawnButton.GetComponentInChildren<Text>();
                    if (legacyButtonText != null)
                    {
                        legacyButtonText.text = "Add + " + objectName;
                    }
                }
                
                Debug.Log($"[RuntimeEditor] Set button text to 'Add + {objectName}'");
            }
        }
    }


    //============= SPAWNING =============
    public void SpawnObject(string id)
    {
        if (!isEditorActive)
        {
            Debug.LogWarning("Editor mode is not active! Cannot spawn objects.");
            return;
        }

        if (mainCamera == null)
        {
            Debug.LogError("Main camera is not set! Cannot spawn objects.");
            return;
        }
        SpawnableObject spawnableObject = spawnableObjects.Find(s => s.id == id);
        if (spawnableObject == null || spawnableObject.prefab == null)
        {
            Debug.LogWarning($"[RuntimeEditor] Spawnable object with id '{id}' not found or has no prefab assigned.");
            return;
        }

        Vector3 spawnPosition = GetSpawnPosition();

        GameObject spawnedObject = Instantiate(spawnableObject.prefab, spawnPosition, Quaternion.identity);
        if (spawnedObject.GetComponent<SEAN.Scenario.Obstacles.TrackedObstacle>() == null)
        {
            var obstacle = spawnedObject.AddComponent<SEAN.Scenario.Obstacles.TrackedObstacle>();
            obstacle.type = spawnableObject.prefab.name.ToLower();
        }

        Debug.Log($"Spawned object: {spawnedObject.name} at {spawnPosition}");

        RegisterEditableObject(spawnedObject);
        SelectObject(spawnedObject);

        undoStack.Push(new SpawnAction(spawnedObject, this));
        redoStack.Clear();
    }

    Vector3 GetSpawnPosition()
    {
        Vector3 screenCenter = new Vector3(Screen.width / 2, Screen.height / 2, 0);
        Ray ray = mainCamera.ScreenPointToRay(screenCenter);
        RaycastHit hit;
        if (Physics.Raycast(ray, out hit, maxRaycastDistance, selectableLayers))
        {
            return hit.point;
        }

        // Top-down / ortho: ray usually points into the scene; avoid using world-space forward alone (can miss the ground).
        return ray.GetPoint(spawnDistance);
    }

    void RegisterEditableObject(GameObject obj)
    {
        if(!string.IsNullOrEmpty(editableTag))
        {
            obj.tag = editableTag;
        }

        if (!editableObjects.Contains(obj))
        {
            editableObjects.Add(obj);
        }

        EnsureObjectLayerIsSelectable(obj);
        if (mainCamera != null)
            EnsureCameraCanSeeLayer(mainCamera, obj.layer);

        // Mark it as moveable in the scene while editor mode is active.
        if (isEditorActive)
            ApplyMoveableHighlight(obj, true);

        // // Add RuntimeEditor component if not exists
        // RuntimeEditor editor = obj.GetComponent<RuntimeEditor>();
        // if (editor == null)
        // {
        //     editor = obj.AddComponent<RuntimeEditor>();
        //     allEditors.Add(editor);
        // }

        // Set correct camera fir gizmo raycasting
        // if (mainCamera != null)
        // {
        //     editor.SetRaycastCamera(mainCamera);
        // }

        //editor.enabled = true;
    }

    /// <summary>
    /// Adds an object to the editable list at runtime so it becomes moveable — the lightweight "register
    /// logic" only (same list the mailbox lands in), with NO component bundling: it does not attach
    /// MoveableProp, TrackedObstacle or any collider. Moveability is then handled by the RuntimeEditor
    /// gizmo that <see cref="SelectObject"/> adds on selection. Returns the object that is now moveable.
    /// </summary>
    public GameObject MakeObjectMoveable(GameObject obj)
    {
        if (obj == null)
            return null;

        // Resolve up to the logical entity root (prop / robot) so we register and move the whole object,
        // not a child mesh.
        GameObject target = ResolvePropRoot(obj);

        if (target == null)
        {
            Debug.LogWarning($"[RuntimeEditor] '{obj.name}' has no visible mesh; not adding to the editable list.");
            return null;
        }

        // Static objects are scenery and can't be dragged meaningfully — don't register them.
        if (target.isStatic)
        {
            Debug.LogWarning($"[RuntimeEditor] '{target.name}' is static; not adding to the editable list.");
            return null;
        }

        RegisterEditableObject(target);
        Debug.Log($"[RuntimeEditor] '{target.name}' added to the editable list (now moveable).");
        return target;
    }

    /// <summary>
    /// Resolves a clicked object up to its logical entity root so the WHOLE thing is selected/moved, not a
    /// child mesh. Prefers the highest ancestor carrying a root marker (TrackedObstacle / Robot / TrackedAgent
    /// / MoveableProp); if none, falls back to the nearest visible-mesh ancestor.
    /// </summary>
    static GameObject ResolvePropRoot(GameObject obj)
    {
        if (obj == null)
            return null;

        GameObject markerRoot = null;
        for (Transform t = obj.transform; t != null; t = t.parent)
        {
            GameObject go = t.gameObject;
            if (go.GetComponent<SEAN.Scenario.Obstacles.TrackedObstacle>() != null
                || go.GetComponent<SEAN.Scenario.Robot>() != null
                || go.GetComponent<SEAN.Scenario.Trajectory.TrackedAgent>() != null
                || go.GetComponent<MoveableProp>() != null)
            {
                markerRoot = go; // keep climbing so we end on the HIGHEST marked ancestor (the entity root)
            }
        }

        return markerRoot != null ? markerRoot : ResolveRendererRoot(obj);
    }

    /// <summary>
    /// Returns the nearest GameObject (the clicked object or an ancestor) that owns a real mesh
    /// Renderer, so binding/highlighting targets a visible prop rather than a bare collider.
    /// </summary>
    static GameObject ResolveRendererRoot(GameObject obj)
    {
        if (obj == null)
            return null;

        if (HasRealRenderer(obj))
            return obj;

        Transform t = obj.transform.parent;
        while (t != null)
        {
            if (HasRealRenderer(t.gameObject))
                return t.gameObject;
            t = t.parent;
        }

        return null;
    }

    static bool HasRealRenderer(GameObject obj)
    {
        if (obj == null)
            return false;

        var renderers = obj.GetComponentsInChildren<Renderer>(true);
        foreach (Renderer r in renderers)
        {
            if (r != null && !(r is LineRenderer))
                return true;
        }
        return false;
    }

    /// <summary>Adds or removes the moveable outline on every currently editable object.</summary>
    void RefreshMoveableHighlights(bool show)
    {
        bool wantHighlight = show && highlightMoveableObjects;

        if (!wantHighlight)
        {
            // Remove every outline, including any orphaned ones.
            foreach (var h in FindObjectsOfType<MoveableObjectHighlight>())
            {
                if (h != null)
                    Destroy(h);
            }
            return;
        }

        for (int i = 0; i < editableObjects.Count; i++)
            ApplyMoveableHighlight(editableObjects[i], true);

        // Tagged objects that aren't in the explicit list (e.g. pre-existing scene props).
        foreach (var obstacle in FindObjectsOfType<SEAN.Scenario.Obstacles.TrackedObstacle>())
        {
            if (obstacle != null)
                ApplyMoveableHighlight(obstacle.gameObject, true);
        }
    }

    void ApplyMoveableHighlight(GameObject obj, bool show)
    {
        if (obj == null)
            return;

        var highlight = obj.GetComponent<MoveableObjectHighlight>();
        if (show)
        {
            if (!highlightMoveableObjects)
                return;
            // Skip static scenery and rendererless markers so only genuinely selectable props are outlined.
            if (obj.isStatic || !HasRealRenderer(obj))
            {
                if (highlight != null)
                    Destroy(highlight);
                return;
            }
            if (highlight == null)
                highlight = obj.AddComponent<MoveableObjectHighlight>();
            highlight.Configure(moveableHighlightColor, moveableHighlightWidth);
            highlight.enabled = true;
        }
        else if (highlight != null)
        {
            Destroy(highlight);
        }
    }

    bool IsClickOnUI()
    {
        if (EventSystem.current == null) return false;

        if (EventSystem.current.IsPointerOverGameObject()) return true;

        PointerEventData pointerEventData = new PointerEventData(EventSystem.current);
        pointerEventData.position = Input.mousePosition;
        List<RaycastResult> results = new List<RaycastResult>();
        EventSystem.current.RaycastAll(pointerEventData, results);
        
        bool hitUI = results.Count > 0;
        if (hitUI)
        {
            Debug.Log("[Raycast] Pointer is over UI. Ignoring click.");
        }
        return hitUI;
    }

    void EnsureObjectLayerIsSelectable(GameObject obj)
    {
        if (obj == null)
            return;

        if (obj.layer >= 0 && obj.layer < 32)
            selectableLayers |= 1 << obj.layer;
    }

    void EnsureCameraCullingCoversSelectableLayers(Camera camera)
    {
        if (camera == null)
            return;

        camera.cullingMask |= selectableLayers;

        int raycastLayer = LayerMask.NameToLayer("Raycast");
        if (raycastLayer != -1)
            EnsureCameraCanSeeLayer(camera, raycastLayer);
    }

    void EnsureCameraCanSeeLayer(Camera camera, int layer)
    {
        if (camera == null || layer < 0 || layer >= 32)
            return;

        camera.cullingMask |= 1 << layer;
    }

    void ExcludeGizmoLayerFromMainCamera()
    {
        if (mainCamera == null)
            return;

        int gizmoLayer = LayerMask.NameToLayer(gizmoLayerName);
        if (gizmoLayer == -1)
            return;

        mainCamera.cullingMask &= ~(1 << gizmoLayer);
    }

    void SyncGizmoCamera()
    {
        if (mainCamera == null || gizmoCamera == null)
            return;

        int gizmoLayer = LayerMask.NameToLayer(gizmoLayerName);
        if (gizmoLayer == -1)
            return;

        gizmoCamera.CopyFrom(mainCamera);
        gizmoCamera.cullingMask = 1 << gizmoLayer;
        gizmoCamera.clearFlags = CameraClearFlags.Depth;
        gizmoCamera.depth = mainCamera.depth + 1;
        ExcludeGizmoLayerFromMainCamera();
    }

    // ===== UNDO / REDO PUBLIC API =====

    public void PushTransformAction(GameObject target, Vector3 beforePos, Quaternion beforeRot, Vector3 afterPos, Quaternion afterRot)
    {
        if (target == null) return;
        if (Vector3.Distance(beforePos, afterPos) < 0.001f && Quaternion.Angle(beforeRot, afterRot) < 0.1f) return;
        undoStack.Push(new TransformAction(target, beforePos, beforeRot, afterPos, afterRot));
        redoStack.Clear();
    }

    public void UndoLastAction()
    {
        if (undoStack.Count == 0) return;
        var action = undoStack.Pop();
        action.Undo();
        redoStack.Push(action);
    }

    public void RedoLastAction()
    {
        if (redoStack.Count == 0) return;
        var action = redoStack.Pop();
        action.Redo();
        undoStack.Push(action);
    }

    public void DeleteSelectedObject()
    {
        if (currentSelectedObject == null) return;
        GameObject toDelete = currentSelectedObject;
        DeselectObject();
        toDelete.SetActive(false);
        undoStack.Push(new DeleteAction(toDelete, this));
        redoStack.Clear();
    }
}
