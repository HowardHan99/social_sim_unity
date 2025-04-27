using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class FirstCameraDisplay : MonoBehaviour
{
    private Camera cameraComponent;
    private Vector3 initialPosition;
    private Quaternion initialRotation;

    // Display settings
    [Tooltip("Set to 1 for secondary display")]
    public int targetDisplay = 1;

    // Movement settings
    public float movementSpeed = 5f;
    public float fastMovementSpeed = 50f;
    
    // Mouse look settings
    public float mouseSensitivity = 2f;
    private bool isLooking = false;
    
    // Zoom settings
    public float zoomSensitivity = 1f;
    public float fastZoomSensitivity = 5f;
    
    // Position limits
    public float minZPosition = -100f;
    public float maxZPosition = 100f;
    
    // Control settings
    public bool useArrowKeys = true;

    // Start is called before the first frame update
    void Start()
    {
        // Get the camera component and set it to the first display (0)
        cameraComponent = GetComponent<Camera>();
        if (cameraComponent != null)
        {
            cameraComponent.targetDisplay = targetDisplay;
        }
        
        // Store initial position and rotation
        initialPosition = transform.position;
        initialRotation = transform.rotation;
    }

    // Update is called once per frame
    void Update()
    {
        // Movement with WASD
        HandleKeyboardMovement();
        
        // Mouse look (when right mouse button is held)
        HandleMouseLook();
        
        // Zoom with mouse wheel
        HandleZoom();
    }
    
    private void HandleKeyboardMovement()
    {
        bool fastMode = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
        float currentSpeed = fastMode ? fastMovementSpeed : movementSpeed;
        
        // Forward/Backward
        if (useArrowKeys && Input.GetKey(KeyCode.UpArrow))
            transform.position += transform.forward * currentSpeed * Time.deltaTime;
        if (useArrowKeys && Input.GetKey(KeyCode.DownArrow))
            transform.position -= transform.forward * currentSpeed * Time.deltaTime;
            
        // Left/Right
        if (useArrowKeys && Input.GetKey(KeyCode.LeftArrow))
            transform.position -= transform.right * currentSpeed * Time.deltaTime;
        if (useArrowKeys && Input.GetKey(KeyCode.RightArrow))
            transform.position += transform.right * currentSpeed * Time.deltaTime;
            
        // Up/Down
        if (useArrowKeys && Input.GetKey(KeyCode.PageUp))
            transform.position += transform.up * currentSpeed * Time.deltaTime;
        if (useArrowKeys && Input.GetKey(KeyCode.PageDown))
            transform.position -= transform.up * currentSpeed * Time.deltaTime;
    }
    
    private void HandleMouseLook()
    {
        // Start/stop looking with right mouse button
        if (Input.GetKeyDown(KeyCode.Mouse1))
            StartLooking();
        else if (Input.GetKeyUp(KeyCode.Mouse1))
            StopLooking();
            
        // Rotate camera based on mouse movement
        if (isLooking)
        {
            float rotX = transform.localEulerAngles.y + Input.GetAxis("Mouse X") * mouseSensitivity;
            float rotY = transform.localEulerAngles.x - Input.GetAxis("Mouse Y") * mouseSensitivity;
            transform.localEulerAngles = new Vector3(rotY, rotX, 0f);
        }
    }
    
    private void HandleZoom()
    {
        float scrollInput = Input.GetAxis("Mouse ScrollWheel");
        if (scrollInput != 0)
        {
            bool fastMode = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            float currentZoomSpeed = fastMode ? fastZoomSensitivity : zoomSensitivity;
            
            Vector3 newPosition = transform.position + transform.forward * scrollInput * currentZoomSpeed;
            
            // Clamp Z position to prevent extreme values
            newPosition.z = Mathf.Clamp(newPosition.z, minZPosition, maxZPosition);
            
            transform.position = newPosition;
        }
    }
    
    private void StartLooking()
    {
        isLooking = true;
        Cursor.visible = false;
        Cursor.lockState = CursorLockMode.Locked;
    }
    
    private void StopLooking()
    {
        isLooking = false;
        Cursor.visible = true;
        Cursor.lockState = CursorLockMode.None;
    }
    
    void OnDisable()
    {
        // Make sure cursor is restored when script is disabled
        StopLooking();
    }
    
    public void ResetCamera()
    {
        transform.position = initialPosition;
        transform.rotation = initialRotation;
    }
}
