using UnityEngine;

/// <summary>
/// Controller for temporary replay camera with WASD movement and mouse rotation
/// </summary>
public class TempCameraController : MonoBehaviour
{
    [Header("Movement Settings")]
    public float moveSpeed = 5f;
    public float fastMoveSpeed = 10f;
    public float verticalMoveSpeed = 3f;

    [Header("Rotation Settings")]
    public float mouseSensitivity = 2f;
    public float minVerticalAngle = -90f;
    public float maxVerticalAngle = 90f;

    private float rotationX = 0f;
    private float rotationY = 0f;
    private bool cursorLocked = false;

    void Start()
    {
        // Initialize rotation from current camera rotation
        Vector3 currentRotation = transform.rotation.eulerAngles;
        rotationY = currentRotation.y;
        rotationX = currentRotation.x;
        
        // Normalize X rotation to -180 to 180 range
        if (rotationX > 180f)
            rotationX -= 360f;
    }

    void Update()
    {
        HandleCursorLock();
        HandleRotation();
        HandleMovement();
    }

    void HandleCursorLock()
    {
        // Right-click to lock/unlock cursor for camera rotation
        if (Input.GetMouseButtonDown(1))
        {
            cursorLocked = !cursorLocked;
            Cursor.lockState = cursorLocked ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !cursorLocked;
        }
        
        // Also allow ESC to unlock cursor
        if (Input.GetKeyDown(KeyCode.Escape))
        {
            cursorLocked = false;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
    }

    void HandleRotation()
    {
        if (cursorLocked)
        {
            // Get mouse input
            float mouseX = Input.GetAxis("Mouse X") * mouseSensitivity;
            float mouseY = Input.GetAxis("Mouse Y") * mouseSensitivity;

            // Update rotation
            rotationY += mouseX;
            rotationX -= mouseY;

            // Clamp vertical rotation
            rotationX = Mathf.Clamp(rotationX, minVerticalAngle, maxVerticalAngle);

            // Apply rotation
            transform.rotation = Quaternion.Euler(rotationX, rotationY, 0f);
        }
    }

    void HandleMovement()
    {
        // Determine move speed (hold Shift for faster movement)
        float currentMoveSpeed = Input.GetKey(KeyCode.LeftShift) ? fastMoveSpeed : moveSpeed;

        // Get input
        float horizontal = Input.GetAxis("Horizontal"); // A/D
        float vertical = Input.GetAxis("Vertical");     // W/S
        float upDown = 0f;

        // Q/E for vertical movement
        if (Input.GetKey(KeyCode.Q))
            upDown = -1f;
        if (Input.GetKey(KeyCode.E))
            upDown = 1f;

        // Calculate movement direction relative to camera orientation
        Vector3 forward = transform.forward;
        Vector3 right = transform.right;
        
        // Movement on horizontal plane
        Vector3 movement = (forward * vertical + right * horizontal) * currentMoveSpeed * Time.deltaTime;
        
        // Add vertical movement (world space)
        movement += Vector3.up * upDown * verticalMoveSpeed * Time.deltaTime;

        // Apply movement
        transform.position += movement;
    }

    void OnDestroy()
    {
        // Restore cursor when camera is destroyed
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
    }
}

