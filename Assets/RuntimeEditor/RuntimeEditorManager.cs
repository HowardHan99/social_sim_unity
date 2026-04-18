using UnityEngine;
using System.Collections.Generic;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using TMPro;

[System.Serializable]
public class SpawnableObject
{
    public string id;
    public GameObject prefab;
    public Button spawnButton; // Reference to the UI button
}

public class RuntimeEditorManager : MonoBehaviour
{
    private const string DefaultSphereId = "0";
    private const string DefaultCylinderId = "1";
    private const string DefaultCapsuleId = "2";

    // Singleton Instance
    public static RuntimeEditorManager Instance { get; private set; }

    [Header("Editor Settings")]
    [Tooltip("Key to toggle runtime editor mode")]
    public KeyCode toggleKey = KeyCode.E;

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


    private GameObject currentSelectedObject;
    private RuntimeEditor currentEditor;
    private Camera mainCamera;
    private List<RuntimeEditor> allEditors = new List<RuntimeEditor>();

    public GameObject CurrentSelectedObject => currentSelectedObject;
    public Camera ActiveRaycastCamera => mainCamera;

    // Event that other systems can subscribe to
    public delegate void EditorModeChanged(bool isActive);
    public event EditorModeChanged OnEditorModeChanged;

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

        // Initialize editor state without clobbering a runtime activation
        if (!isEditorActive)
            SetEditorMode(false);
    }

    void Update()
    {
        SyncGizmoCamera();

        // Toggle editor mode
        if (Input.GetKeyDown(toggleKey))
        {
            ToggleEditorMode();
        }

        // If editor is active, handle object selection
        if (isEditorActive)
        {
            HandleObjectSelection();
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

        if (spawnCanvas != null)
        {
            spawnCanvas.gameObject.SetActive(true);
            //spawnCanvas.worldCamera = mainCamera;
        }

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
                return;
            }

            if (mainCamera == null)
            {
                Debug.LogError("MainCamera is NULL! Cannot perform raycast.");
                return;
            }
            
            // Get ray from camera accounting for viewport rect
            Ray ray = GetRayFromScreenPoint(Input.mousePosition);
            RaycastHit hit;

            // Debug.Log($"[Raycast] Mouse Position: {Input.mousePosition}, Ray Origin: {ray.origin}, Ray Direction: {ray.direction}");

            if (Physics.Raycast(ray, out hit, maxRaycastDistance, selectableLayers))
            {
                GameObject clickedObject = hit.collider.gameObject;
                GameObject editableObject = ResolveEditableObject(clickedObject);
                // Debug.Log($"[Raycast HIT] Object: {clickedObject.name}, Position: {hit.point}, Distance: {hit.distance:F2}m, Layer: {LayerMask.LayerToName(clickedObject.layer)}");

                // To ignore if pointer is over UI
                if(clickedObject.layer == LayerMask.NameToLayer("UI"))
                {
                    Debug.Log("[Raycast] Pointer is over UI. Ignoring click.");
                    return;
                }

                // Check if object is editable
                if (editableObject != null)
                {
                    SelectObject(editableObject);
                }
                else
                {
                    Debug.LogWarning($"[Raycast] Object '{clickedObject.name}' is NOT editable (not in editable list or wrong tag)");
                }
            }
            else
            {
                Debug.Log("[Raycast] No objects hit by raycast");
            }
        }

        // Press Escape to deselect or exit editor mode
        if (Input.GetKeyDown(KeyCode.Escape))
        {
            if (currentSelectedObject != null)
            {
                DeselectObject();
            }
            else
            {
                // If no object selected, exit editor mode
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

    void DeselectObject()
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
            SpawnDefaultPrimitive(id);
            return;
        }

        Vector3 spawnPosition = GetSpawnPosition();

        GameObject spawnedObject = Instantiate(spawnableObject.prefab, spawnPosition, Quaternion.identity);
        Debug.Log($"Spawned object: {spawnedObject.name} at {spawnPosition}");

        RegisterEditableObject(spawnedObject);
        SelectObject(spawnedObject);

    }

    private void SpawnDefaultPrimitive(string id)
    {
        PrimitiveType primitiveType;
        string objectName;
        string obstacleType;

        switch (id)
        {
            case DefaultSphereId:
                primitiveType = PrimitiveType.Sphere;
                objectName = "Sphere";
                obstacleType = "sphere";
                break;
            case DefaultCylinderId:
                primitiveType = PrimitiveType.Cylinder;
                objectName = "Cylinder";
                obstacleType = "cylinder";
                break;
            case DefaultCapsuleId:
                primitiveType = PrimitiveType.Capsule;
                objectName = "Capsule";
                obstacleType = "capsule";
                break;
            default:
                Debug.LogWarning($"Spawnable object with id '{id}' not found!");
                return;
        }

        Vector3 spawnPosition = GetSpawnPosition();
        GameObject spawnedObject = GameObject.CreatePrimitive(primitiveType);
        spawnedObject.name = objectName;
        spawnedObject.transform.position = spawnPosition;

        if (spawnedObject.GetComponent<SEAN.Scenario.Obstacles.TrackedObstacle>() == null)
        {
            var obstacle = spawnedObject.AddComponent<SEAN.Scenario.Obstacles.TrackedObstacle>();
            obstacle.type = obstacleType;
        }

        RegisterEditableObject(spawnedObject);
        SelectObject(spawnedObject);
        Debug.Log($"Spawned default {objectName} at {spawnPosition}");
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
        return mainCamera.transform.position + mainCamera.transform.forward * spawnDistance;
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
}
