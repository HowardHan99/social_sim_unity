using System;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace IVI
{
    public class CameraScript : MonoBehaviour
    {
        private ComfortMotionBlur comfortMotionBlur;
        private bool warnedMissingMouseX;
        private bool warnedMissingMouseY;
        private bool warnedMissingMouseScroll;

        /// <summary>
        /// Normal speed of camera movement.
        /// </summary>
        public float movementSpeed = 10f;

        /// <summary>
        /// Speed of camera movement when shift is held down,
        /// </summary>
        public float fastMovementSpeed = 100f;

        /// <summary>
        /// Sensitivity for free look.
        /// </summary>
        public float freeLookSensitivity = 3f;

        /// <summary>
        /// Amount to zoom the camera when using the mouse wheel.
        /// </summary>
        public float zoomSensitivity = 10f;

        /// <summary>
        /// Amount to zoom the camera when using the mouse wheel (fast mode).
        /// </summary>
        public float fastZoomSensitivity = 50f;

        [Header("Automation")]
        /// <summary>
        /// Speed of automatic forward movement. Set to 0 to disable.
        /// </summary>
        public float autoForwardSpeed = 0f;

        /// <summary>
        /// Enable automatic looking at a target. Manual look will override this.
        /// </summary>
        public bool enableAutoLookAt = false;

        /// <summary>
        /// Target for the camera to automatically look at.
        /// </summary>
        public Transform lookAtTarget;

        /// <summary>
        /// How fast the camera turns to face the target.
        /// </summary>
        public float autoTurnSpeed = 1.0f;

        /// <summary>
        /// The camera will only start turning if the angle to the target is greater than this value (in degrees).
        /// </summary>
        public float autoTurnAngleThreshold = 5.0f;

        /// <summary>
        /// Set to true when free looking (on right mouse button).
        /// </summary>
        private bool looking = false;

        [Header("Position Lock")]
        [Tooltip("When true, all positional movement is disabled; only rotation (free-look) remains active.")]
        public bool lockPosition = false;

        [Tooltip("When false, mouse wheel does not move the camera along its forward axis (dolly). Free-look rotation is unchanged.")]
        public bool allowMouseScrollZoom = true;

        void Awake()
        {
            comfortMotionBlur = GetComponent<ComfortMotionBlur>();
            if (comfortMotionBlur == null)
                comfortMotionBlur = gameObject.AddComponent<ComfortMotionBlur>();
        }

        void Update()
        {
            if (!lockPosition)
            {
                // Automatic forward movement
                if (autoForwardSpeed != 0f)
                {
                    transform.position += transform.forward * autoForwardSpeed * Time.deltaTime;
                }

                // Automatic look-at
                if (enableAutoLookAt && lookAtTarget != null && !looking)
                {
                    Vector3 directionToTarget = lookAtTarget.position - transform.position;

                    if (directionToTarget.sqrMagnitude > 0.001f)
                    {
                        float angle = Vector3.Angle(transform.forward, directionToTarget);

                        if (angle > autoTurnAngleThreshold)
                        {
                            Quaternion targetRotation = Quaternion.LookRotation(directionToTarget);
                            transform.rotation = Quaternion.Slerp(transform.rotation, targetRotation, autoTurnSpeed * Time.deltaTime);
                        }
                    }
                }

                var fastMode = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                var movementSpeed = fastMode ? this.fastMovementSpeed : this.movementSpeed;

                if (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow))
                {
                    transform.position = transform.position + (-transform.right * movementSpeed * Time.deltaTime);
                }

                if (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow))
                {
                    transform.position = transform.position + (transform.right * movementSpeed * Time.deltaTime);
                }

                if (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow))
                {
                    transform.position = transform.position + (transform.forward * movementSpeed * Time.deltaTime);
                }

                if (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow))
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

                if (Input.GetKey(KeyCode.R) || Input.GetKey(KeyCode.PageUp))
                {
                    transform.position = transform.position + (Vector3.up * movementSpeed * Time.deltaTime);
                }

                if (Input.GetKey(KeyCode.F) || Input.GetKey(KeyCode.PageDown))
                {
                    transform.position = transform.position + (-Vector3.up * movementSpeed * Time.deltaTime);
                }
            }

            if (looking)
            {
                if (!IsRightMouseButtonPressed())
                {
                    StopLooking();
                    return;
                }

                Vector2 mouseDelta = GetMouseLookDelta();
                float newRotationX = transform.localEulerAngles.y + mouseDelta.x * freeLookSensitivity;
                float newRotationY = transform.localEulerAngles.x - mouseDelta.y * freeLookSensitivity;
                transform.localEulerAngles = new Vector3(newRotationY, newRotationX, 0f);
            }

            if (!lockPosition && allowMouseScrollZoom)
            {
                bool fastZoom = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                float axis = GetAxisSafely("Mouse ScrollWheel", ref warnedMissingMouseScroll);
                if (axis != 0)
                {
                    var zoomSensitivity = fastZoom ? this.fastZoomSensitivity : this.zoomSensitivity;
                    transform.position = transform.position + transform.forward * axis * zoomSensitivity;
                }
            }

            if (IsRightMouseButtonPressed() && !looking)
            {
                StartLooking();
            }
            else if (IsRightMouseButtonUp())
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
            if (comfortMotionBlur != null)
                comfortMotionBlur.TriggerTransitionBlur();
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

        private float GetAxisSafely(string axisName, ref bool warnedMissingAxis)
        {
            try
            {
                return Input.GetAxis(axisName);
            }
            catch (Exception)
            {
                if (!warnedMissingAxis)
                {
                    Debug.LogWarning($"[CameraScript] Input axis '{axisName}' is not configured. Defaulting to 0.", this);
                    warnedMissingAxis = true;
                }

                return 0f;
            }
        }

        private bool IsRightMouseButtonDown()
        {
#if ENABLE_INPUT_SYSTEM
            return Mouse.current != null && Mouse.current.rightButton.wasPressedThisFrame;
#else
            return Input.GetMouseButtonDown(1);
#endif
        }

        private bool IsRightMouseButtonUp()
        {
#if ENABLE_INPUT_SYSTEM
            return Mouse.current != null && Mouse.current.rightButton.wasReleasedThisFrame;
#else
            return Input.GetMouseButtonUp(1);
#endif
        }

        private bool IsRightMouseButtonPressed()
        {
#if ENABLE_INPUT_SYSTEM
            return Mouse.current != null && Mouse.current.rightButton.isPressed;
#else
            return Input.GetMouseButton(1);
#endif
        }

        private Vector2 GetMouseLookDelta()
        {
#if ENABLE_INPUT_SYSTEM
            return Mouse.current != null ? Mouse.current.delta.ReadValue() * 0.05f : Vector2.zero;
#else
            return new Vector2(
                GetAxisSafely("Mouse X", ref warnedMissingMouseX),
                GetAxisSafely("Mouse Y", ref warnedMissingMouseY));
#endif
        }
    }
}
