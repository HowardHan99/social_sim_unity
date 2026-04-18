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

    private bool isDragging = false;
    private Camera mainCamera;
    private Vector3 currentAxis = Vector3.zero;
    private Plane dragPlane;
    private Vector3 dragOffset;
    private float lastAngle;

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
        // Toggle mode with T (Translate) and R (Rotate)
        if (Input.GetKeyDown(KeyCode.T))
        {
            currentMode = GizmoMode.Translate;
            UpdateGizmoVisibility();
        }
        if (Input.GetKeyDown(KeyCode.R))
        {
            currentMode = GizmoMode.Rotate;
            UpdateGizmoVisibility();
        }

        UpdateGizmoPositions();
        HandleMouseInput();
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

        // Y-axis (Green)
        yLine = CreateCylinderLine("Y_Line", Color.green);
        yHandle = CreateSphere("Y_Handle", Color.green, handleSize);

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

        if (currentMode == GizmoMode.Translate)
        {
            // Update X-axis (Red) - cylinder along X
            Vector3 xMid = pos + Vector3.right * gizmoSize * 0.5f;
            xLine.transform.position = xMid;
            xLine.transform.rotation = Quaternion.Euler(0, 0, 90);
            xLine.transform.localScale = new Vector3(lineWidth, gizmoSize * 0.5f, lineWidth);
            xHandle.transform.position = pos + Vector3.right * gizmoSize;

            // Update Y-axis (Green) - cylinder along Y
            Vector3 yMid = pos + Vector3.up * gizmoSize * 0.5f;
            yLine.transform.position = yMid;
            yLine.transform.rotation = Quaternion.identity;
            yLine.transform.localScale = new Vector3(lineWidth, gizmoSize * 0.5f, lineWidth);
            yHandle.transform.position = pos + Vector3.up * gizmoSize;

            // Update Z-axis (Blue) - cylinder along Z
            Vector3 zMid = pos + Vector3.forward * gizmoSize * 0.5f;
            zLine.transform.position = zMid;
            zLine.transform.rotation = Quaternion.Euler(90, 0, 0);
            zLine.transform.localScale = new Vector3(lineWidth, gizmoSize * 0.5f, lineWidth);
            zHandle.transform.position = pos + Vector3.forward * gizmoSize;
        }
        else if (currentMode == GizmoMode.Rotate)
        {
            UpdateCircleSegments(xCircle, pos, Vector3.right);
            UpdateCircleSegments(yCircle, pos, Vector3.up);
            UpdateCircleSegments(zCircle, pos, Vector3.forward);
        }
    }

    void UpdateCircleSegments(GameObject circleParent, Vector3 center, Vector3 normal)
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
            Vector3 point = center + (right * Mathf.Cos(angle) + forward * Mathf.Sin(angle)) * rotationGizmoRadius;

            Transform segment = circleParent.transform.GetChild(i);
            segment.position = point;
            segment.LookAt(center);
        }
    }

    void UpdateGizmoVisibility()
    {
        if (currentMode == GizmoMode.Translate)
        {
            // Show translate, hide rotate
            if (xLine != null) xLine.SetActive(true);
            if (yLine != null) yLine.SetActive(true);
            if (zLine != null) zLine.SetActive(true);
            if (xHandle != null) xHandle.SetActive(true);
            if (yHandle != null) yHandle.SetActive(true);
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
        //Don't process if pointer is over UI
        if (IsClickOnUI() && Input.GetMouseButtonDown(0))
        {
            Debug.Log("[Raycast] Pointer is over UI. Ignoring click.");
            return;
        }

        Ray ray = mainCamera.ScreenPointToRay(Input.mousePosition);

        if (Input.GetMouseButtonDown(0))
        {
            Debug.Log("Gizmo: Mouse button down");
            if (currentMode == GizmoMode.Translate)
            {
                CheckTranslateHandles(ray);
            }
            else if (currentMode == GizmoMode.Rotate)
            {
                CheckRotateHandles(ray);
            }
        }

        if (Input.GetMouseButton(0) && isDragging)
        {
            Debug.Log("Gizmo: Mouse button down and dragging");
            if (currentMode == GizmoMode.Translate)
            {
                UpdateTranslate(ray);
            }
            else if (currentMode == GizmoMode.Rotate)
            {
                UpdateRotate(ray);
            }
        }

        if (Input.GetMouseButtonUp(0))
        {
            isDragging = false;
        }
    }

    void CheckTranslateHandles(Ray ray)
    {
        Vector3 gizmoCenter = GetGizmoCenter();

        // Check X-axis handle (Red)
        if (IsHandleClicked(ray, gizmoCenter + Vector3.right * gizmoSize))
        {
            Debug.Log("Gizmo: X-axis handle clicked");
            StartAxisDrag(Vector3.right, ray);
            return;
        }

        // Check Y-axis handle (Green)  
        if (IsHandleClicked(ray, gizmoCenter + Vector3.up * gizmoSize))
        {
            Debug.Log("Gizmo: Y-axis handle clicked");
            StartAxisDrag(Vector3.up, ray);
            return;
        }

        // Check Z-axis handle (Blue)
        if (IsHandleClicked(ray, gizmoCenter + Vector3.forward * gizmoSize))
        {
            Debug.Log("Gizmo: Z-axis handle clicked");
            StartAxisDrag(Vector3.forward, ray);
            return;
        }
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

            return Mathf.Abs(distance - rotationGizmoRadius) < rotationHandleSize;
        }

        return false;
    }

    void UpdateTranslate(Ray ray)
    {
        float enter;
        if (dragPlane.Raycast(ray, out enter))
        {
            Vector3 hitPoint = ray.GetPoint(enter);
            Vector3 newPos = hitPoint - dragOffset;
            Vector3 gizmoCenter = GetGizmoCenter();

            Vector3 axisLine = Vector3.Project(newPos - gizmoCenter, currentAxis);
            transform.position = transform.position + axisLine;
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

    bool IsHandleClicked(Ray ray, Vector3 handlePosition)
    {
        Vector3 closestPoint = ClosestPointOnRay(ray, handlePosition);
        return Vector3.Distance(closestPoint, handlePosition) < handleSize;
    }

    Vector3 ClosestPointOnRay(Ray ray, Vector3 point)
    {
        Vector3 pointToOrigin = point - ray.origin;
        float projection = Vector3.Dot(pointToOrigin, ray.direction);
        return ray.origin + ray.direction * projection;
    }

    void StartAxisDrag(Vector3 axis, Ray ray)
    {
        isDragging = true;
        currentAxis = axis;
        Vector3 gizmoCenter = GetGizmoCenter();

        dragPlane = new Plane(mainCamera.transform.forward, gizmoCenter);

        float enter;
        if (dragPlane.Raycast(ray, out enter))
        {
            Vector3 hitPoint = ray.GetPoint(enter);
            dragOffset = hitPoint - gizmoCenter;
        }
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
