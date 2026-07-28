using UnityEngine;
using System.Collections.Generic;
using UnityEngine.EventSystems;

public class RuntimeEditor : MonoBehaviour     
{
    [Header("Gizmo Settings")]
    public float gizmoSize = 1.0f;
    public float handleSize = 0.2f;
    public float rotationGizmoRadius = 1.5f;
    public float rotationHandleSize = 0.15f;
    public float lineWidth = 0.05f;

    public enum GizmoMode { Translate, Rotate }
    public GizmoMode currentMode = GizmoMode.Translate;

    [Header("Keyboard Move (while selected)")]
    public bool keyboardMoveEnabled = true;
    public float keyboardMoveSpeed = 3f;          // metres per second
    public float keyboardBoostMultiplier = 3f;    // while Shift is held
    [Tooltip("Hold this to fly the camera with WASD instead of moving the selected object.")]
    public KeyCode cameraOverrideKey = KeyCode.LeftAlt;

    // Below this many on-screen pixels a handle becomes effectively unclickable, so hit-testing
    // widens to this radius even when the handle's world-space sphere is smaller.
    private const float MinHandlePixelRadius = 12f;

    private bool isDragging = false;
    private Camera mainCamera;
    [Tooltip("Lock vertical (Y-axis) movement during world-building so drags/keys only move objects on the horizontal plane. Height can still be set explicitly (world-building side panel).")]
    public bool lockVerticalMovement = true;

    private Vector3 currentAxis = Vector3.zero;
    private Plane dragPlane;
    private Vector3 dragStartHit;      // where the grab ray met the drag plane, at mouse-down
    private Vector3 dragStartPosition; // transform.position at mouse-down
    private float lastAngle;
    private Vector3 dragBeforePos;
    private Quaternion dragBeforeRot;

    // Continuous WASD nudge of the selected object; batched into one undo entry per key-hold.
    private bool keyboardMoveActive = false;
    private Vector3 keyboardMoveBeforePos;
    private Quaternion keyboardMoveBeforeRot;

    // Free "grab the body" dragging: when no axis handle is grabbed, the object itself can be
    // dragged across the ground plane. This makes props (and the robot) moveable even when the
    // thin axis gizmo is hard to see/hit in the world-building view.
    private bool isBodyDragging = false;
    private bool pendingBodyDrag = false;
    private Plane bodyDragPlane;
    private Vector3 bodyDragOffset;
    private Vector3 mouseDownScreenPos;
    private const float BodyDragThreshold = 4f; // pixels of motion before a click becomes a drag

    // Gizmo visual objects
    private GameObject gizmoContainer;
    private GameObject xLine, yLine, zLine;
    private GameObject xHandle, yHandle, zHandle;
    private GameObject xCircle, yCircle, zCircle;

    int gizmoLayer = -1;

    int GizmoLayer
    {
        get
        {
            if (gizmoLayer == -1)
            {
                gizmoLayer = LayerMask.NameToLayer("Gizmo");
            }
            return gizmoLayer;
        }
    }


    public void SetRaycastCamera(Camera camera)
    {
        mainCamera = camera;
    }

    /// <summary>
    /// Ensures <see cref="mainCamera"/> points at a live camera. Unity's overloaded ==
    /// reports a destroyed object as null, so this recovers both from a never-assigned
    /// camera (NullReferenceException) and from one destroyed by a trial restart / scene
    /// reload (MissingReferenceException). Returns false if no usable camera exists.
    /// </summary>
    bool EnsureCamera()
    {
        if (mainCamera != null)
            return true;

        if (RuntimeEditorManager.Instance != null && RuntimeEditorManager.Instance.ActiveRaycastCamera != null)
        {
            mainCamera = RuntimeEditorManager.Instance.ActiveRaycastCamera;
            return true;
        }

        mainCamera = Camera.main;
        return mainCamera != null;
    }

    void Start()
    {
        if (mainCamera == null)
        {
            mainCamera = Camera.main;
        }
        CreateGizmoVisuals();
    }

    void Update()
    {
        // Toggle mode with T (Translate) and R (Rotate) -- unless an IMGUI text field has
        // focus (e.g. the "Save World" name box), where those letters are text being typed.
        bool typingInTextField = GUIUtility.keyboardControl != 0;

        if (!typingInTextField && Input.GetKeyDown(KeyCode.T))
        {
            currentMode = GizmoMode.Translate;
            UpdateGizmoVisibility();
        }
        if (!typingInTextField && Input.GetKeyDown(KeyCode.R))
        {
            currentMode = GizmoMode.Rotate;
            UpdateGizmoVisibility();
        }

        UpdateGizmoPositions();
        HandleMouseInput();
        HandleKeyboardMove();

        // lockVerticalMovement is enforced per input path (body drag keeps Y, axis drags move
        // strictly along X/Z, Q/E is gated, the Y handle is hidden and un-clickable) rather than
        // by a global Y-clamp here — a clamp would also revert explicit height edits from the
        // world-building Height field and break undo/redo of them.
    }

    /// <summary>
    /// World-space length of the gizmo arms. Fixed <see cref="gizmoSize"/> alone puts the handles
    /// inside anything bigger than a metre (invisible, unclickable) and shrinks them to a few
    /// pixels when the building camera pulls back, so grow it past the object's own bounds and
    /// keep a roughly constant on-screen size. Hit-testing uses this same value, so the grab
    /// region always matches what is drawn.
    /// </summary>
    float CurrentGizmoScale()
    {
        float scale = gizmoSize;

        if (TryGetWorldBounds(out Bounds b))
            scale = Mathf.Max(scale, b.extents.magnitude * 0.9f);

        if (mainCamera != null)
        {
            float viewScale = mainCamera.orthographic
                ? mainCamera.orthographicSize * 0.35f
                : Vector3.Distance(mainCamera.transform.position, GetGizmoCenter())
                  * Mathf.Tan(mainCamera.fieldOfView * 0.5f * Mathf.Deg2Rad) * 0.35f;
            scale = Mathf.Max(scale, viewScale);
        }

        return scale;
    }

    float CurrentHandleRadius(float scale)
    {
        return handleSize * (scale / Mathf.Max(0.0001f, gizmoSize));
    }

    /// <summary>How many world units one screen pixel spans at <paramref name="worldPoint"/>.</summary>
    float WorldUnitsPerPixel(Vector3 worldPoint)
    {
        if (mainCamera == null)
            return 0f;

        float pixelHeight = Mathf.Max(1, mainCamera.pixelHeight);
        if (mainCamera.orthographic)
            return mainCamera.orthographicSize * 2f / pixelHeight;

        float dist = Vector3.Distance(mainCamera.transform.position, worldPoint);
        return 2f * dist * Mathf.Tan(mainCamera.fieldOfView * 0.5f * Mathf.Deg2Rad) / pixelHeight;
    }

    void CreateGizmoVisuals()
    {
        // Create container
        gizmoContainer = new GameObject("GizmoContainer");
        gizmoContainer.transform.position = transform.position;
        gizmoContainer.layer = GizmoLayer;
        
        // // TEST: Set gizmo objects at Ignore Raycast layer
        // int ignoreRaycastLayer = LayerMask.NameToLayer("Ignore Raycast");
        // if (ignoreRaycastLayer != -1)
        // {
        //     SetLayerRecursively(gizmoContainer, ignoreRaycastLayer);
        // }

        // Create translate gizmo
        CreateTranslateGizmo();

        // Create rotate gizmo
        CreateRotateGizmo();

        UpdateGizmoVisibility();
    }

    void SetLayerRecursively(GameObject obj, int layer)
    {
        obj.layer = layer;
        foreach (Transform child in obj.transform)
        {
            SetLayerRecursively(child.gameObject, layer);
        }
    }

    void CreateTranslateGizmo()
    {
        // X-axis (Red)
        xLine = CreateCylinderLine("X_Line", Color.red);
        xHandle = CreateSphere("X_Handle", Color.red, handleSize);

        // Y-axis (Green) -- hidden when vertical movement is locked (world-building), and
        // TryGetTranslateAxis skips it so the invisible handle can't grab clicks either.
        yLine = CreateCylinderLine("Y_Line", Color.green);
        yHandle = CreateSphere("Y_Handle", Color.green, handleSize);
        if (lockVerticalMovement)
        {
            yLine.SetActive(false);
            yHandle.SetActive(false);
        }

        // Z-axis (Blue)
        zLine = CreateCylinderLine("Z_Line", Color.blue);
        zHandle = CreateSphere("Z_Handle", Color.blue, handleSize);
    }

    void CreateRotateGizmo()
    {
        xCircle = CreateTorusCircle("X_Circle", Color.red);
        yCircle = CreateTorusCircle("Y_Circle", Color.green);
        zCircle = CreateTorusCircle("Z_Circle", Color.blue);
    }

    Material CreateRenderOnTopMaterial(Color color)
    {
        // Create material with unlit shader
        Material material = new Material(Shader.Find("Custom/RenderOnTop"));
        //Material material = new Material(Shader.Find("Unlit/Color"));
        material.color = color;
        return material;
    }

    GameObject CreateCylinderLine(string name, Color color)
    {
        GameObject cylinder = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        cylinder.name = name;
        cylinder.transform.SetParent(gizmoContainer.transform);

        // Remove collider
        Destroy(cylinder.GetComponent<Collider>());
        cylinder.layer = GizmoLayer;

        // Set material
        Renderer renderer = cylinder.GetComponent<Renderer>();
        renderer.material = CreateRenderOnTopMaterial(color);
        

        return cylinder;
    }

    GameObject CreateSphere(string name, Color color, float size)
    {
        GameObject sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        sphere.name = name;
        sphere.transform.SetParent(gizmoContainer.transform);
        sphere.transform.localScale = Vector3.one * size;

        // Remove collider
        Destroy(sphere.GetComponent<Collider>());
        sphere.layer = GizmoLayer;

        // Set material
        Renderer renderer = sphere.GetComponent<Renderer>();
        renderer.material = CreateRenderOnTopMaterial(color);

        return sphere;
    }

    GameObject CreateTorusCircle(string name, Color color)
    {
        GameObject circleParent = new GameObject(name);
        circleParent.transform.SetParent(gizmoContainer.transform);

        // Create circle using multiple small cubes
        int segments = 32;
        for (int i = 0; i < segments; i++)
        {
            GameObject segment = GameObject.CreatePrimitive(PrimitiveType.Cube);
            segment.name = name + "_Segment_" + i;
            segment.transform.SetParent(circleParent.transform);
            segment.transform.localScale = new Vector3(lineWidth, lineWidth, rotationGizmoRadius * 0.2f);

            // Remove collider
            Destroy(segment.GetComponent<Collider>());
            segment.layer = GizmoLayer; 

            // Set material
            Renderer renderer = segment.GetComponent<Renderer>();
            renderer.material = CreateRenderOnTopMaterial(color);
        }

        return circleParent;
    }

    void UpdateGizmoPositions()
    {
        Vector3 pos = GetGizmoCenter();
        // // push gizmo slightly in front of the object
        // Vector3 camDir = (gizmoContainer.transform.position - mainCamera.transform.position).normalized;
        // gizmoContainer.transform.position += camDir * 0.02f;
        
        gizmoContainer.transform.position = pos;

        // Same scale the hit-test uses, so what you see is what you can grab.
        float scale = CurrentGizmoScale();
        float relative = scale / Mathf.Max(0.0001f, gizmoSize);
        float width = lineWidth * relative;
        float handleScale = CurrentHandleRadius(scale);

        if (currentMode == GizmoMode.Translate)
        {
            // Update X-axis (Red) - cylinder along X
            Vector3 xMid = pos + Vector3.right * scale * 0.5f;
            xLine.transform.position = xMid;
            xLine.transform.rotation = Quaternion.Euler(0, 0, 90);
            xLine.transform.localScale = new Vector3(width, scale * 0.5f, width);
            xHandle.transform.position = pos + Vector3.right * scale;
            xHandle.transform.localScale = Vector3.one * handleScale;

            // Update Y-axis (Green) - cylinder along Y
            Vector3 yMid = pos + Vector3.up * scale * 0.5f;
            yLine.transform.position = yMid;
            yLine.transform.rotation = Quaternion.identity;
            yLine.transform.localScale = new Vector3(width, scale * 0.5f, width);
            yHandle.transform.position = pos + Vector3.up * scale;
            yHandle.transform.localScale = Vector3.one * handleScale;

            // Update Z-axis (Blue) - cylinder along Z
            Vector3 zMid = pos + Vector3.forward * scale * 0.5f;
            zLine.transform.position = zMid;
            zLine.transform.rotation = Quaternion.Euler(90, 0, 0);
            zLine.transform.localScale = new Vector3(width, scale * 0.5f, width);
            zHandle.transform.position = pos + Vector3.forward * scale;
            zHandle.transform.localScale = Vector3.one * handleScale;
        }
        else if (currentMode == GizmoMode.Rotate)
        {
            UpdateCircleSegments(xCircle, pos, Vector3.right, relative);
            UpdateCircleSegments(yCircle, pos, Vector3.up, relative);
            UpdateCircleSegments(zCircle, pos, Vector3.forward, relative);
        }
    }

    void UpdateCircleSegments(GameObject circleParent, Vector3 center, Vector3 normal, float relative)
    {
        Vector3 forward = Vector3.Slerp(normal, -normal, 0.5f);
        if (forward == normal || forward == -normal)
            forward = Vector3.up;

        Vector3 right = Vector3.Cross(normal, forward).normalized;
        forward = Vector3.Cross(right, normal).normalized;

        int segmentCount = circleParent.transform.childCount;
        for (int i = 0; i < segmentCount; i++)
        {
            float angle = i * 360f / segmentCount * Mathf.Deg2Rad;
            Vector3 point = center + (right * Mathf.Cos(angle) + forward * Mathf.Sin(angle))
                            * rotationGizmoRadius * relative;

            Transform segment = circleParent.transform.GetChild(i);
            segment.position = point;
            segment.localScale = new Vector3(lineWidth, lineWidth, rotationGizmoRadius * 0.2f) * relative;
            segment.LookAt(center);
        }
    }

    void UpdateGizmoVisibility()
    {
        if (currentMode == GizmoMode.Translate)
        {
            // Show translate, hide rotate
            if (xLine != null) xLine.SetActive(true);
            if (yLine != null) yLine.SetActive(!lockVerticalMovement);
            if (zLine != null) zLine.SetActive(true);
            if (xHandle != null) xHandle.SetActive(true);
            if (yHandle != null) yHandle.SetActive(!lockVerticalMovement);
            if (zHandle != null) zHandle.SetActive(true);

            if (xCircle != null) xCircle.SetActive(false);
            if (yCircle != null) yCircle.SetActive(false);
            if (zCircle != null) zCircle.SetActive(false);
        }
        else if (currentMode == GizmoMode.Rotate)
        {
            // Show rotate, hide translate
            if (xLine != null) xLine.SetActive(false);
            if (yLine != null) yLine.SetActive(false);
            if (zLine != null) zLine.SetActive(false);
            if (xHandle != null) xHandle.SetActive(false);
            if (yHandle != null) yHandle.SetActive(false);
            if (zHandle != null) zHandle.SetActive(false);

            if (xCircle != null) xCircle.SetActive(true);
            if (yCircle != null) yCircle.SetActive(true);
            if (zCircle != null) zCircle.SetActive(true);
        }
    }

    void HandleMouseInput()
    {
        // No raycast camera (e.g. the main camera was disabled/destroyed during a camera
        // hand-off such as Session Review -> World Building). Try to re-acquire one, and if
        // there still isn't one, bail this frame instead of NRE'ing on mainCamera.
        if (mainCamera == null)
        {
            mainCamera = Camera.main;
            if (mainCamera == null) return;
        }

        //Don't process if pointer is over UI
        if (IsClickOnUI() && Input.GetMouseButtonDown(0))
        {
            Debug.Log("[Raycast] Pointer is over UI. Ignoring click.");
            return;
        }

        if (!EnsureCamera())
            return;

        Ray ray = mainCamera.ScreenPointToRay(Input.mousePosition);

        if (Input.GetMouseButtonDown(0))
        {
            Debug.Log("Gizmo: Mouse button down");
            bool wasDragging = isDragging;
            if (currentMode == GizmoMode.Translate)
                CheckTranslateHandles(ray);
            else if (currentMode == GizmoMode.Rotate)
                CheckRotateHandles(ray);

            // Capture transform state the moment a new (handle) drag starts
            if (!wasDragging && isDragging)
            {
                dragBeforePos = transform.position;
                dragBeforeRot = transform.rotation;
            }
            else if (!isDragging && currentMode == GizmoMode.Translate && IsRayOverObject(ray))
            {
                // No axis handle was grabbed, but the click landed on the object itself: arm a
                // free ground-plane drag. We only commit once the pointer moves past a threshold,
                // so a plain click (used to (re)select) never nudges the object.
                pendingBodyDrag = true;
                mouseDownScreenPos = Input.mousePosition;
                dragBeforePos = transform.position;
                dragBeforeRot = transform.rotation;
                BeginBodyDragPlane(ray);
            }
        }

        if (Input.GetMouseButton(0))
        {
            if (pendingBodyDrag && !isDragging &&
                ((Vector2)Input.mousePosition - (Vector2)mouseDownScreenPos).magnitude > BodyDragThreshold)
            {
                isDragging = true;
                isBodyDragging = true;
            }

            if (isDragging)
            {
                if (currentMode == GizmoMode.Rotate)
                    UpdateRotate(ray);
                else if (isBodyDragging)
                    UpdateBodyDrag(ray);
                else
                    UpdateTranslate(ray);
            }
        }

        if (Input.GetMouseButtonUp(0))
        {
            if (isDragging)
            {
                // Keep physics bodies in sync so the move survives un-pausing the game.
                SyncRigidbodiesToTransform();
                RuntimeEditorManager.Instance?.PushTransformAction(
                    gameObject, dragBeforePos, dragBeforeRot,
                    transform.position, transform.rotation);
            }
            isDragging = false;
            isBodyDragging = false;
            pendingBodyDrag = false;
        }
    }

    /// <summary>
    /// WASD/QE nudge of the selected object. This component only runs while its object is selected,
    /// so the keys are live exactly when there is something to move; <see cref="SimpleCameraController"/>
    /// yields them for the same window (hold <see cref="cameraOverrideKey"/> to fly the camera anyway).
    /// Movement is camera-relative on the ground plane, so W always pushes the prop away from the viewer.
    /// </summary>
    void HandleKeyboardMove()
    {
        if (!keyboardMoveEnabled || isDragging || pendingBodyDrag)
        {
            CommitKeyboardMove();
            return;
        }

        // An IMGUI text field has focus (the "Save World" name box): these are characters, not hotkeys.
        // Ctrl is reserved for undo/redo, and the override key belongs to the camera.
        if (GUIUtility.keyboardControl != 0 ||
            Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl) ||
            Input.GetKey(cameraOverrideKey) || Input.GetKey(KeyCode.RightAlt))
        {
            CommitKeyboardMove();
            return;
        }

        Vector3 input = Vector3.zero;
        if (Input.GetKey(KeyCode.W)) input.z += 1f;
        if (Input.GetKey(KeyCode.S)) input.z -= 1f;
        if (Input.GetKey(KeyCode.A)) input.x -= 1f;
        if (Input.GetKey(KeyCode.D)) input.x += 1f;
        if (!lockVerticalMovement)
        {
            if (Input.GetKey(KeyCode.E)) input.y += 1f;
            if (Input.GetKey(KeyCode.Q)) input.y -= 1f;
        }

        if (input == Vector3.zero || !EnsureCamera())
        {
            CommitKeyboardMove();
            return;
        }

        Vector3 forward = Vector3.ProjectOnPlane(mainCamera.transform.forward, Vector3.up);
        if (forward.sqrMagnitude < 1e-4f)
        {
            // Looking straight down (the default building view): the camera's up vector is what
            // points "away" on screen.
            forward = Vector3.ProjectOnPlane(mainCamera.transform.up, Vector3.up);
            if (forward.sqrMagnitude < 1e-4f)
                forward = Vector3.forward;
        }
        forward.Normalize();
        Vector3 right = Vector3.Cross(Vector3.up, forward);

        if (!keyboardMoveActive)
        {
            keyboardMoveActive = true;
            keyboardMoveBeforePos = transform.position;
            keyboardMoveBeforeRot = transform.rotation;
        }

        float speed = keyboardMoveSpeed;
        if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))
            speed *= keyboardBoostMultiplier;

        // World building runs with Time.timeScale at 0, so scaled deltaTime would be a flat zero.
        Vector3 delta = (right * input.x + Vector3.up * input.y + forward * input.z).normalized
                        * speed * Time.unscaledDeltaTime;
        transform.position += delta;
    }

    /// <summary>Closes an in-progress WASD move: one undo entry per key-hold, not per frame.</summary>
    void CommitKeyboardMove()
    {
        if (!keyboardMoveActive)
            return;

        keyboardMoveActive = false;

        if (transform.position == keyboardMoveBeforePos && transform.rotation == keyboardMoveBeforeRot)
            return;

        SyncRigidbodiesToTransform();
        RuntimeEditorManager.Instance?.PushTransformAction(
            gameObject, keyboardMoveBeforePos, keyboardMoveBeforeRot,
            transform.position, transform.rotation);
    }

    // True if the click ray passes through this object's visual bounds (ignoring the gizmo itself).
    bool IsRayOverObject(Ray ray)
    {
        return TryGetWorldBounds(out Bounds b) && b.IntersectRay(ray);
    }

    bool TryGetWorldBounds(out Bounds bounds)
    {
        bounds = default;
        bool has = false;
        foreach (Renderer r in GetComponentsInChildren<Renderer>())
        {
            if (r == null || r is LineRenderer || !r.enabled)
                continue;
            if (gizmoContainer != null && r.transform.IsChildOf(gizmoContainer.transform))
                continue;
            if (!has) { bounds = r.bounds; has = true; }
            else bounds.Encapsulate(r.bounds);
        }
        return has;
    }

    void BeginBodyDragPlane(Ray ray)
    {
        // Slide along a horizontal plane through the object — the natural motion in both the
        // top-down and tilted world-building views.
        bodyDragPlane = new Plane(Vector3.up, transform.position);
        bodyDragOffset = bodyDragPlane.Raycast(ray, out float enter)
            ? ray.GetPoint(enter) - transform.position
            : Vector3.zero;
    }

    void UpdateBodyDrag(Ray ray)
    {
        if (bodyDragPlane.Raycast(ray, out float enter))
        {
            Vector3 target = ray.GetPoint(enter) - bodyDragOffset;
            target.y = transform.position.y; // ground-plane move only; keep height
            transform.position = target;
        }
    }

    // Sync any physics bodies (e.g. the robot's base_link Rigidbody) to the moved transform and
    // zero their velocity. Without this the stale physics position snaps the object back when the
    // game un-pauses (Time.timeScale returns to 1 after world building).
    void SyncRigidbodiesToTransform()
    {
        foreach (Rigidbody rb in GetComponentsInChildren<Rigidbody>())
        {
            if (rb == null)
                continue;
            rb.position = rb.transform.position;
            rb.rotation = rb.transform.rotation;
            rb.velocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }
    }

    void CheckTranslateHandles(Ray ray)
    {
        if (TryGetTranslateAxis(ray, out Vector3 axis))
        {
            Debug.Log($"Gizmo: {axis} handle clicked");
            StartAxisDrag(axis, ray);
        }
    }

    /// <summary>
    /// Picks the translate handle under <paramref name="ray"/>, nearest-to-camera first. The old
    /// fixed X-then-Y-then-Z order handed the drag to whichever axis was declared first whenever
    /// two handles overlapped on screen, which reads as "the gizmo moved the wrong way".
    /// </summary>
    bool TryGetTranslateAxis(Ray ray, out Vector3 axis)
    {
        axis = Vector3.zero;
        Vector3 gizmoCenter = GetGizmoCenter();
        float scale = CurrentGizmoScale();
        float radius = CurrentHandleRadius(scale);
        float bestDistance = float.MaxValue;

        foreach (Vector3 candidate in new[] { Vector3.right, Vector3.up, Vector3.forward })
        {
            // The Y handle is hidden while vertical movement is locked; skip it in hit-testing
            // too so the invisible handle can't swallow a click meant for X/Z or selection.
            if (lockVerticalMovement && candidate == Vector3.up)
                continue;

            Vector3 handlePos = gizmoCenter + candidate * scale;
            if (!IsHandleClicked(ray, handlePos, radius))
                continue;

            float distance = Vector3.Distance(ray.origin, handlePos);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                axis = candidate;
            }
        }

        return axis != Vector3.zero;
    }

    /// <summary>
    /// True if this click belongs to the gizmo rather than to selection. <see cref="RuntimeEditorManager"/>
    /// calls this before its own selection raycast: the Gizmo layer is excluded from
    /// <c>selectableLayers</c>, so without this the selection ray passes straight through the handle
    /// being grabbed, hits whatever prop is behind it, and re-selects it — which disables this
    /// component mid-drag. That was the intermittent "gizmo move doesn't work".
    /// </summary>
    public bool WouldConsumeClick()
    {
        if (isDragging || pendingBodyDrag)
            return true;

        if (!EnsureCamera())
            return false;

        Ray ray = mainCamera.ScreenPointToRay(Input.mousePosition);

        if (currentMode == GizmoMode.Translate)
            return TryGetTranslateAxis(ray, out _);

        Vector3 center = GetGizmoCenter();
        return IsRotationHandleClicked(ray, center, Vector3.right)
            || IsRotationHandleClicked(ray, center, Vector3.up)
            || IsRotationHandleClicked(ray, center, Vector3.forward);
    }

    void CheckRotateHandles(Ray ray)
    {
        Vector3 center = GetGizmoCenter();

        // Check X-axis rotation
        if (IsRotationHandleClicked(ray, center, Vector3.right))
        {
            StartRotationDrag(Vector3.right, ray);
            return;
        }

        // Check Y-axis rotation
        if (IsRotationHandleClicked(ray, center, Vector3.up))
        {
            StartRotationDrag(Vector3.up, ray);
            return;
        }

        // Check Z-axis rotation
        if (IsRotationHandleClicked(ray, center, Vector3.forward))
        {
            StartRotationDrag(Vector3.forward, ray);
            return;
        }
    }

    bool IsRotationHandleClicked(Ray ray, Vector3 center, Vector3 normal)
    {
        Plane plane = new Plane(normal, center);
        float enter;

        if (plane.Raycast(ray, out enter))
        {
            Vector3 hitPoint = ray.GetPoint(enter);
            float distance = Vector3.Distance(hitPoint, center);
            float scale = CurrentGizmoScale() / Mathf.Max(0.0001f, gizmoSize);
            float band = Mathf.Max(rotationHandleSize * scale,
                                   WorldUnitsPerPixel(center) * MinHandlePixelRadius);

            return Mathf.Abs(distance - rotationGizmoRadius * scale) < band;
        }

        return false;
    }

    void UpdateTranslate(Ray ray)
    {
        float enter;
        if (dragPlane.Raycast(ray, out enter))
        {
            // Rigid 1:1 follow: how far along the axis has the grab point travelled since
            // mouse-down? The old code re-derived a delta from the (bounds-based) gizmo centre
            // every frame, which only converged on the cursor asymptotically and drifted for any
            // object whose pivot isn't its bounds centre.
            Vector3 hitPoint = ray.GetPoint(enter);
            float travel = Vector3.Dot(hitPoint - dragStartHit, currentAxis);
            transform.position = dragStartPosition + currentAxis * travel;
        }
    }

    void UpdateRotate(Ray ray)
    {
        float enter;
        if (dragPlane.Raycast(ray, out enter))
        {
            Vector3 hitPoint = ray.GetPoint(enter);
            Vector3 gizmoCenter = GetGizmoCenter();
            Vector3 direction = (hitPoint - gizmoCenter).normalized;

            float angle = 0f;

            if (currentAxis == Vector3.right)
            {
                angle = Mathf.Atan2(direction.z, direction.y) * Mathf.Rad2Deg;
            }
            else if (currentAxis == Vector3.up)
            {
                angle = Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;
            }
            else if (currentAxis == Vector3.forward)
            {
                angle = Mathf.Atan2(direction.y, direction.x) * Mathf.Rad2Deg;
            }

            float deltaAngle = Mathf.DeltaAngle(lastAngle, angle);
            transform.Rotate(currentAxis, deltaAngle, Space.World);
            lastAngle = angle;
        }
    }

    bool IsHandleClicked(Ray ray, Vector3 handlePosition, float radius)
    {
        Vector3 closestPoint = ClosestPointOnRay(ray, handlePosition);
        // A handle that is physically small or far away still needs a usable grab region.
        float tolerance = Mathf.Max(radius, WorldUnitsPerPixel(handlePosition) * MinHandlePixelRadius);
        return Vector3.Distance(closestPoint, handlePosition) < tolerance;
    }

    Vector3 ClosestPointOnRay(Ray ray, Vector3 point)
    {
        Vector3 pointToOrigin = point - ray.origin;
        // Clamp to the forward half-line: without this a handle *behind* the camera projects to a
        // near point and registers as clicked.
        float projection = Mathf.Max(0f, Vector3.Dot(pointToOrigin, ray.direction));
        return ray.origin + ray.direction * projection;
    }

    void StartAxisDrag(Vector3 axis, Ray ray)
    {
        Vector3 gizmoCenter = GetGizmoCenter();
        Vector3 viewDir = mainCamera.orthographic
            ? mainCamera.transform.forward
            : (gizmoCenter - mainCamera.transform.position).normalized;

        // Drag on a plane that CONTAINS the axis and faces the camera as squarely as possible.
        // The old plane used the camera forward as its normal, which collapses to zero motion
        // whenever the axis points at the camera — e.g. the green Y handle in the top-down
        // building view, where grabbing Y did nothing at all.
        Vector3 normal = Vector3.Cross(axis, Vector3.Cross(viewDir, axis));
        if (normal.sqrMagnitude < 1e-6f)
        {
            // Sighting straight down the axis: any containing plane works, pick a stable one.
            normal = Vector3.Cross(axis, Vector3.up);
            if (normal.sqrMagnitude < 1e-6f)
                normal = Vector3.Cross(axis, Vector3.right);
        }

        dragPlane = new Plane(normal.normalized, gizmoCenter);

        float enter;
        if (!dragPlane.Raycast(ray, out enter))
        {
            // Grazing ray — starting the drag would apply a stale offset and teleport the object.
            isDragging = false;
            return;
        }

        isDragging = true;
        currentAxis = axis;
        dragStartHit = ray.GetPoint(enter);
        dragStartPosition = transform.position;
    }

    void StartRotationDrag(Vector3 axis, Ray ray)
    {
        isDragging = true;
        currentAxis = axis;
        Vector3 gizmoCenter = GetGizmoCenter();

        dragPlane = new Plane(axis, gizmoCenter);

        float enter;
        if (dragPlane.Raycast(ray, out enter))
        {
            Vector3 hitPoint = ray.GetPoint(enter);
            Vector3 direction = (hitPoint - gizmoCenter).normalized;

            if (axis == Vector3.right)
            {
                lastAngle = Mathf.Atan2(direction.z, direction.y) * Mathf.Rad2Deg;
            }
            else if (axis == Vector3.up)
            {
                lastAngle = Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;
            }
            else if (axis == Vector3.forward)
            {
                lastAngle = Mathf.Atan2(direction.y, direction.x) * Mathf.Rad2Deg;
            }
        }
    }

    Vector3 GetGizmoCenter()
    {
        Renderer[] renderers = GetComponentsInChildren<Renderer>();
        bool hasBounds = false;
        Bounds combined = default;

        foreach (Renderer renderer in renderers)
        {
            if (renderer == null || !renderer.enabled)
                continue;

            if (gizmoContainer != null && renderer.transform.IsChildOf(gizmoContainer.transform))
                continue;

            if (!hasBounds)
            {
                combined = renderer.bounds;
                hasBounds = true;
            }
            else
            {
                combined.Encapsulate(renderer.bounds);
            }
        }

        if (hasBounds)
            return combined.center;

        Collider[] colliders = GetComponentsInChildren<Collider>();
        foreach (Collider collider in colliders)
        {
            if (collider == null || !collider.enabled)
                continue;

            if (gizmoContainer != null && collider.transform.IsChildOf(gizmoContainer.transform))
                continue;

            if (!hasBounds)
            {
                combined = collider.bounds;
                hasBounds = true;
            }
            else
            {
                combined.Encapsulate(collider.bounds);
            }
        }

        return hasBounds ? combined.center : transform.position;
    }

    public void ShowGizmo(bool show)
    {
        if (gizmoContainer != null)
        {
            gizmoContainer.SetActive(show);
        }
    }

    void OnEnable()
    {
        ShowGizmo(true);
    }

    void OnDisable()
    {
        ShowGizmo(false);
        // Deselection can happen mid-drag (Escape, right-click, or another object being selected).
        // A disabled component never sees the matching MouseButtonUp, so without this reset the
        // stale isDragging/pendingBodyDrag flags resume a phantom drag the next time this object
        // is selected and the object jumps to the cursor.
        isDragging = false;
        isBodyDragging = false;
        pendingBodyDrag = false;
        CommitKeyboardMove();
    }

    void OnDestroy()
    {
        if (gizmoContainer != null)
        {
            Destroy(gizmoContainer);
        }
    }

    bool IsClickOnUI()
    {
        if (SessionReview.SessionReviewManager.Instance != null &&
            SessionReview.SessionReviewManager.Instance.IsPointerOverWorldBuildingUi())
        {
            Debug.Log("[Raycast] Pointer is over world-building UI. Ignoring click.");
            return true;
        }

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
}
