using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class Cameramovement : MonoBehaviour
{
    private Camera cameraComponent;
    private Vector3 initialPosition;
    private Quaternion initialRotation;


    /// <summary>
    /// Normal speed of camera movement.
    /// </summary>
    public float movementSpeed = 5f;

        /// <summary>
        /// Speed of camera movement when shift is held down,
        /// </summary>
        public float fastMovementSpeed = 50f;

        /// <summary>
        /// Sensitivity for free look.
        /// </summary>
        public float freeLookSensitivity = 1.5f;

        /// <summary>
        /// Amount to zoom the camera when using the mouse wheel.
        /// </summary>
        public float zoomSensitivity = 1f;

        /// <summary>
        /// Amount to zoom the camera when using the mouse wheel (fast mode).
        /// </summary>
        public float fastZoomSensitivity = 5f;
        
        /// <summary>
        /// Target display to render to (0 = main display, 1 = second display, etc.)
        /// </summary>
        public int targetDisplay = 1;
        
        /// <summary>
        /// Enable or disable arrow key controls
        /// </summary>
        public bool useArrowKeys = false;

        /// <summary>
        /// Set to true when free looking (on right mouse button).
        /// </summary>
        private bool looking = false;

        /// <summary>
        /// Minimum z position value
        /// </summary>
        public float minZPosition = -100f;
        
        /// <summary>
        /// Maximum z position value
        /// </summary>
        public float maxZPosition = 100f;

    void Start()
    {
        // Get the camera component and set its target display
        cameraComponent = GetComponent<Camera>();
        if (cameraComponent != null)
        {
            cameraComponent.targetDisplay = targetDisplay;
        }
        
        // Store initial position and rotation
        initialPosition = transform.position;
        initialRotation = transform.rotation;
    }

    public void ResetCamera()
    {
        transform.position = initialPosition;
        transform.rotation = initialRotation;
    }
        void Update()
        {
            var fastMode = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            var movementSpeed = fastMode ? this.fastMovementSpeed : this.movementSpeed;

            if (Input.GetKey(KeyCode.A) || (useArrowKeys && Input.GetKey(KeyCode.LeftArrow)))
            {
                transform.position = transform.position + (-transform.right * movementSpeed * Time.deltaTime);
            }

            if (Input.GetKey(KeyCode.D) || (useArrowKeys && Input.GetKey(KeyCode.RightArrow)))
            {
                transform.position = transform.position + (transform.right * movementSpeed * Time.deltaTime);
            }

            if (Input.GetKey(KeyCode.W) || (useArrowKeys && Input.GetKey(KeyCode.UpArrow)))
            {
                transform.position = transform.position + (transform.forward * movementSpeed * Time.deltaTime);
            }

            if (Input.GetKey(KeyCode.S) || (useArrowKeys && Input.GetKey(KeyCode.DownArrow)))
            {
                transform.position = transform.position + (-transform.forward * movementSpeed * Time.deltaTime);
            }

            if (Input.GetKey(KeyCode.Q))
            {
                transform.position = transform.position + (-transform.up * movementSpeed * Time.deltaTime);
            }

            if (Input.GetKey(KeyCode.E))
            {
                transform.position = transform.position + (transform.up * movementSpeed * Time.deltaTime);
            }

            if (Input.GetKey(KeyCode.R) || (useArrowKeys && Input.GetKey(KeyCode.PageUp)))
            {
                transform.position = transform.position + (Vector3.up * movementSpeed * Time.deltaTime);
            }

            if (Input.GetKey(KeyCode.F) || (useArrowKeys && Input.GetKey(KeyCode.PageDown)))
            {
                transform.position = transform.position + (-Vector3.up * movementSpeed * Time.deltaTime);
            }

            if (looking)
            {
                float newRotationX = transform.localEulerAngles.y + Input.GetAxis("Mouse X") * freeLookSensitivity;
                float newRotationY = transform.localEulerAngles.x - Input.GetAxis("Mouse Y") * freeLookSensitivity;
                transform.localEulerAngles = new Vector3(newRotationY, newRotationX, 0f);
            }

            float axis = Input.GetAxis("Mouse ScrollWheel");
            if (axis != 0)
            {
                var zoomSensitivity = fastMode ? this.fastZoomSensitivity : this.zoomSensitivity;
                Vector3 newPosition = transform.position + transform.forward * axis * zoomSensitivity;
                
                // Clamp the z position to prevent extreme values
                newPosition.z = Mathf.Clamp(newPosition.z, minZPosition, maxZPosition);
                
                transform.position = newPosition;
            }

            if (Input.GetKeyDown(KeyCode.Mouse1))
            {
                StartLooking();
            }
            else if (Input.GetKeyUp(KeyCode.Mouse1))
            {
                StopLooking();
            }
        }

        void OnDisable()
        {
            StopLooking();
        }

        /// <summary>
        /// Enable free looking.
        /// </summary>
        public void StartLooking()
        {
            looking = true;
            Cursor.visible = false;
            Cursor.lockState = CursorLockMode.Locked;
        }

        /// <summary>
        /// Disable free looking.
        /// </summary>
        public void StopLooking()
        {
            looking = false;
            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;
        }  
}   
