#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

using UnityEngine;

namespace UnityTemplateProjects
{
    public class SimpleCameraController : MonoBehaviour
    {
        [SerializeField] private Camera mainCamera;
        
        class CameraState
        {
            public float yaw;
            public float pitch;
            public float roll;
            public float x;
            public float y;
            public float z;

            public void SetFromTransform(Transform t)
            {
                pitch = t.eulerAngles.x;
                yaw = t.eulerAngles.y;
                roll = t.eulerAngles.z;
                x = t.position.x;
                y = t.position.y;
                z = t.position.z;
            }

            public void Translate(Vector3 translation)
            {
                Vector3 rotatedTranslation = Quaternion.Euler(pitch, yaw, roll) * translation;

                x += rotatedTranslation.x;
                y += rotatedTranslation.y;
                z += rotatedTranslation.z;
            }

            public void LerpTowards(CameraState target, float positionLerpPct, float rotationLerpPct)
            {
                yaw = Mathf.Lerp(yaw, target.yaw, rotationLerpPct);
                pitch = Mathf.Lerp(pitch, target.pitch, rotationLerpPct);
                roll = Mathf.Lerp(roll, target.roll, rotationLerpPct);
                
                x = Mathf.Lerp(x, target.x, positionLerpPct);
                y = Mathf.Lerp(y, target.y, positionLerpPct);
                z = Mathf.Lerp(z, target.z, positionLerpPct);
            }

            public void UpdateTransform(Transform t)
            {
                t.eulerAngles = new Vector3(pitch, yaw, roll);
                t.position = new Vector3(x, y, z);
            }
        }

        const float k_MouseSensitivityMultiplier = 0.01f;

        CameraState m_TargetCameraState = new CameraState();
        CameraState m_InterpolatingCameraState = new CameraState();
        CameraState m_InitialCameraState = new CameraState(); // Store initial state before edit mode
        
        private bool m_WasEditorActive = false; // Track editor state changes
        private Vector3 m_LastMousePosition;
        private bool m_HasLastMousePosition;

        [Header("Movement Settings")]
        [Tooltip("Middle-mouse pan as a multiple of exact cursor tracking (1 = the ground point stays under the cursor).")]
        public float panMultiplier = 1.0f;

        [Tooltip("Mouse-wheel zoom step as a fraction of the current view size per wheel notch.")]
        [Range(0.01f, 0.5f)] public float zoomStepFraction = 0.12f;

        [Tooltip("Movement speed for keyboard navigation in free camera mode.")]
        public float moveSpeed = 8.0f;
        
        [Tooltip("Exponential boost factor on translation, controllable by mouse wheel.")]
        public float boost = 3.5f;

        [Tooltip("Time it takes to interpolate camera position 99% of the way to the target."), Range(0.001f, 1f)]
        public float positionLerpTime = 0.2f;

        [Header("Rotation Settings")]
        [Tooltip("Multiplier for the sensitivity of the rotation.")]
        public float mouseSensitivity = 60.0f;

        [Tooltip("X = Change in mouse position.\nY = Multiplicative factor for camera rotation.")]
        public AnimationCurve mouseSensitivityCurve = new AnimationCurve(new Keyframe(0f, 0.5f, 0f, 5f), new Keyframe(1f, 2.5f, 0f, 0f));

        [Tooltip("Time it takes to interpolate camera rotation 99% of the way to the target."), Range(0.001f, 1f)]
        public float rotationLerpTime = 0.01f;

        [Tooltip("Whether or not to invert our Y axis for mouse input to rotation.")]
        public bool invertY = false;

        [Header("Top-Down View")]
        [Tooltip("Key to snap the editor camera back to top-down map view.")]
        public KeyCode topDownResetKey = KeyCode.F4;

        [Tooltip("Pitch angle used for the top-down map view.")]
        public float topDownPitch = 90f;

        [Tooltip("Default height above the ground plane when snapping back to top-down.")]
        public float topDownHeight = 35f;

        [Tooltip("Minimum orthographic size used in top-down map view.")]
        public float minTopDownSize = 8f;

#if ENABLE_INPUT_SYSTEM
        InputAction movementAction;
        InputAction verticalMovementAction;
        InputAction lookAction;
        InputAction boostFactorAction;
        bool        mouseRightButtonPressed;

        void Start()
        {
            var map = new InputActionMap("Simple Camera Controller");

            lookAction = map.AddAction("look", binding: "<Mouse>/delta");
            movementAction = map.AddAction("move", binding: "<Gamepad>/leftStick");
            verticalMovementAction = map.AddAction("Vertical Movement");
            boostFactorAction = map.AddAction("Boost Factor", binding: "<Mouse>/scroll");

            lookAction.AddBinding("<Gamepad>/rightStick").WithProcessor("scaleVector2(x=15, y=15)");
            movementAction.AddCompositeBinding("Dpad")
                .With("Up", "<Keyboard>/w")
                .With("Up", "<Keyboard>/upArrow")
                .With("Down", "<Keyboard>/s")
                .With("Down", "<Keyboard>/downArrow")
                .With("Left", "<Keyboard>/a")
                .With("Left", "<Keyboard>/leftArrow")
                .With("Right", "<Keyboard>/d")
                .With("Right", "<Keyboard>/rightArrow");
            verticalMovementAction.AddCompositeBinding("Dpad")
                .With("Up", "<Keyboard>/pageUp")
                .With("Down", "<Keyboard>/pageDown")
                .With("Up", "<Keyboard>/e")
                .With("Down", "<Keyboard>/q")
                .With("Up", "<Gamepad>/rightshoulder")
                .With("Down", "<Gamepad>/leftshoulder");
            boostFactorAction.AddBinding("<Gamepad>/Dpad").WithProcessor("scaleVector2(x=1, y=4)");

            movementAction.Enable();
            lookAction.Enable();
            verticalMovementAction.Enable();
            boostFactorAction.Enable();
        }
#endif

        void OnEnable()
        {
            if (mainCamera == null)
                mainCamera = GetComponent<Camera>();

            SyncToCurrentTransform();
            m_InitialCameraState.SetFromTransform(transform); // Store initial state
            m_LastMousePosition = Input.mousePosition;
            m_HasLastMousePosition = true;
        }

        public void SyncToCurrentTransform()
        {
            if (mainCamera == null)
                mainCamera = GetComponent<Camera>();

            m_TargetCameraState.SetFromTransform(transform);
            m_InterpolatingCameraState.SetFromTransform(transform);
        }

        Vector3 GetInputTranslationDirection()
        {
            Vector3 direction = Vector3.zero;
#if ENABLE_INPUT_SYSTEM
            var moveDelta = movementAction.ReadValue<Vector2>();
            direction.x = moveDelta.x;
            direction.z = moveDelta.y;
            direction.y = verticalMovementAction.ReadValue<Vector2>().y;
#else
            if (Input.GetKey(KeyCode.W))
            {
                direction += Vector3.forward;
            }
            if (Input.GetKey(KeyCode.S))
            {
                direction += Vector3.back;
            }
            if (Input.GetKey(KeyCode.A))
            {
                direction += Vector3.left;
            }
            if (Input.GetKey(KeyCode.D))
            {
                direction += Vector3.right;
            }
            if (Input.GetKey(KeyCode.Q))
            {
                direction += Vector3.down;
            }
            if (Input.GetKey(KeyCode.E))
            {
                direction += Vector3.up;
            }
#endif
            return direction;
        }
        
        void Update()
        {
            // Check if editor is active - camera only moveable when editor is active
            if (RuntimeEditorManager.Instance != null)
            {
                bool isEditorActive = RuntimeEditorManager.Instance.isEditorActive;
                
                // Detect when editor becomes active - store initial state
                if (isEditorActive && !m_WasEditorActive)
                {
                    m_InitialCameraState.SetFromTransform(transform);
                    if (!IsTopDownView())
                        SnapToTopDownView();
                }
                
                // Detect when editor becomes inactive - restore initial state
                if (!isEditorActive && m_WasEditorActive)
                {
                    m_TargetCameraState = new CameraState();
                    m_TargetCameraState.yaw = m_InitialCameraState.yaw;
                    m_TargetCameraState.pitch = m_InitialCameraState.pitch;
                    m_TargetCameraState.roll = m_InitialCameraState.roll;
                    m_TargetCameraState.x = m_InitialCameraState.x;
                    m_TargetCameraState.y = m_InitialCameraState.y;
                    m_TargetCameraState.z = m_InitialCameraState.z;
                    
                    m_InterpolatingCameraState.yaw = m_InitialCameraState.yaw;
                    m_InterpolatingCameraState.pitch = m_InitialCameraState.pitch;
                    m_InterpolatingCameraState.roll = m_InitialCameraState.roll;
                    m_InterpolatingCameraState.x = m_InitialCameraState.x;
                    m_InterpolatingCameraState.y = m_InitialCameraState.y;
                    m_InterpolatingCameraState.z = m_InitialCameraState.z;
                    
                    m_InitialCameraState.UpdateTransform(transform);
                }
                
                m_WasEditorActive = isEditorActive;
                
                if (!isEditorActive)
                {
                    Cursor.visible = true;
                    Cursor.lockState = CursorLockMode.None;
                    return; // Exit early if editor is not active
                }
            }

            if (Input.GetKeyDown(topDownResetKey))
            {
                SnapToTopDownView();
            }

            // Hide and lock cursor when right mouse button or middle mouse button pressed
            if (IsRightMouseButtonDown() || IsMiddleMouseButtonPressed())
            {
                Cursor.visible = true;
                Cursor.lockState = CursorLockMode.None;
            }

            // Unlock and show cursor when both buttons released
            if (IsRightMouseButtonUp() && !IsMiddleMouseButtonPressed())
            {
                Cursor.visible = true;
                Cursor.lockState = CursorLockMode.None;
            }

            // Rotation with right mouse button
            if (IsCameraRotationAllowed())
            {
                EnsurePerspectiveForFreeLook();

                var mouseMovement = GetInputLookRotation() * k_MouseSensitivityMultiplier * mouseSensitivity;
                if (invertY)
                    mouseMovement.y = -mouseMovement.y;
                
                var mouseSensitivityFactor = mouseSensitivityCurve.Evaluate(mouseMovement.magnitude);

                m_TargetCameraState.yaw += mouseMovement.x * mouseSensitivityFactor;
                m_TargetCameraState.pitch += mouseMovement.y * mouseSensitivityFactor;
            }

            Vector3 translation = GetInputTranslationDirection();
            if (translation.sqrMagnitude > 0.0001f)
            {
                float boostMultiplier = IsBoostPressed() ? boost : 1f;
                m_TargetCameraState.Translate(translation * moveSpeed * boostMultiplier * Time.unscaledDeltaTime);
            }
            
            // Panning with middle mouse button, scaled so the grabbed ground point tracks the cursor
            if (IsMiddleMouseButtonPressed())
            {
                Vector2 mouseDelta = GetMouseDeltaPixels();
                float worldPerPixel = GetWorldUnitsPerPixel();
                Vector3 panTranslation =
                    (-transform.right * mouseDelta.x - transform.up * mouseDelta.y) * (worldPerPixel * panMultiplier);

                m_TargetCameraState.x += panTranslation.x;
                m_TargetCameraState.y += panTranslation.y;
                m_TargetCameraState.z += panTranslation.z;
            }

            // Zoom with mouse scroll wheel, as a fraction of the current view per notch
            float scrollDelta = GetScrollDelta();
            if (Mathf.Abs(scrollDelta) > 0.01f)
            {
                float zoomFactor = Mathf.Pow(1f - zoomStepFraction, scrollDelta);
                if (mainCamera != null && mainCamera.orthographic)
                {
                    mainCamera.orthographicSize = Mathf.Max(minTopDownSize, mainCamera.orthographicSize * zoomFactor);
                }
                else
                {
                    Vector3 zoomTranslation = transform.forward * (GetViewDistance() * (1f - zoomFactor));
                    m_TargetCameraState.x += zoomTranslation.x;
                    m_TargetCameraState.y += zoomTranslation.y;
                    m_TargetCameraState.z += zoomTranslation.z;
                }
            }

            // Framerate-independent interpolation
            // Calculate the lerp amount, such that we get 99% of the way to our target in the specified time
            // Use unscaledDeltaTime so camera works even when game is paused
            var positionLerpPct = 1f - Mathf.Exp((Mathf.Log(1f - 0.99f) / positionLerpTime) * Time.unscaledDeltaTime);
            var rotationLerpPct = 1f - Mathf.Exp((Mathf.Log(1f - 0.99f) / rotationLerpTime) * Time.unscaledDeltaTime);
            m_InterpolatingCameraState.LerpTowards(m_TargetCameraState, positionLerpPct, rotationLerpPct);

            m_InterpolatingCameraState.UpdateTransform(transform);
        }

        public bool IsTopDownView()
        {
            return mainCamera != null && mainCamera.orthographic;
        }

        public void SnapToTopDownView()
        {
            if (mainCamera == null)
                mainCamera = GetComponent<Camera>();

            Vector3 focusPoint = TryGetGroundFocusPoint(out Vector3 groundPoint)
                ? groundPoint
                : transform.position + Vector3.forward * 5f;

            float size = mainCamera != null && mainCamera.orthographic
                ? Mathf.Max(mainCamera.orthographicSize, minTopDownSize)
                : minTopDownSize;

            if (mainCamera != null)
            {
                mainCamera.orthographic = true;
                mainCamera.orthographicSize = size;
            }

            m_TargetCameraState.yaw = 0f;
            m_TargetCameraState.pitch = topDownPitch;
            m_TargetCameraState.roll = 0f;
            m_TargetCameraState.x = focusPoint.x;
            m_TargetCameraState.y = Mathf.Max(focusPoint.y + topDownHeight, topDownHeight);
            m_TargetCameraState.z = focusPoint.z;

            m_InterpolatingCameraState.yaw = m_TargetCameraState.yaw;
            m_InterpolatingCameraState.pitch = m_TargetCameraState.pitch;
            m_InterpolatingCameraState.roll = m_TargetCameraState.roll;
            m_InterpolatingCameraState.x = m_TargetCameraState.x;
            m_InterpolatingCameraState.y = m_TargetCameraState.y;
            m_InterpolatingCameraState.z = m_TargetCameraState.z;
            m_InterpolatingCameraState.UpdateTransform(transform);
        }

        private void EnsurePerspectiveForFreeLook()
        {
            if (mainCamera == null || !mainCamera.orthographic)
                return;

            Vector3 focusPoint = TryGetGroundFocusPoint(out Vector3 groundPoint)
                ? groundPoint
                : transform.position + Vector3.down * Mathf.Max(transform.position.y, 1f);

            float distance = Mathf.Max(mainCamera.orthographicSize * 2.5f, 12f);
            Quaternion freeLookRotation = Quaternion.Euler(55f, m_TargetCameraState.yaw, 0f);
            Vector3 cameraPosition = focusPoint - freeLookRotation * Vector3.forward * distance;

            mainCamera.orthographic = false;
            m_TargetCameraState.SetFromTransform(transform);
            m_TargetCameraState.pitch = freeLookRotation.eulerAngles.x;
            m_TargetCameraState.yaw = freeLookRotation.eulerAngles.y;
            m_TargetCameraState.roll = 0f;
            m_TargetCameraState.x = cameraPosition.x;
            m_TargetCameraState.y = cameraPosition.y;
            m_TargetCameraState.z = cameraPosition.z;

            m_InterpolatingCameraState.SetFromTransform(transform);
        }

        private bool TryGetGroundFocusPoint(out Vector3 groundPoint)
        {
            groundPoint = Vector3.zero;

            if (mainCamera == null)
                return false;

            Ray ray = mainCamera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
            Plane groundPlane = new Plane(Vector3.up, Vector3.zero);
            if (!groundPlane.Raycast(ray, out float enter))
                return false;

            groundPoint = ray.GetPoint(enter);
            return true;
        }

        Vector2 GetMouseDeltaPixels()
        {
#if ENABLE_INPUT_SYSTEM
            return Mouse.current != null ? Mouse.current.delta.ReadValue() : Vector2.zero;
#else
            return GetInputLookRotation();
#endif
        }

        // Distance from the camera to what it is looking at, used to scale pan/zoom steps.
        float GetViewDistance()
        {
            return TryGetGroundFocusPoint(out Vector3 groundPoint)
                ? Vector3.Distance(transform.position, groundPoint)
                : Mathf.Max(transform.position.y, 5f);
        }

        float GetWorldUnitsPerPixel()
        {
            if (mainCamera == null)
                return 0.02f;

            float viewHeightWorld = mainCamera.orthographic
                ? mainCamera.orthographicSize * 2f
                : 2f * GetViewDistance() * Mathf.Tan(mainCamera.fieldOfView * 0.5f * Mathf.Deg2Rad);

            return viewHeightWorld / Mathf.Max(mainCamera.pixelHeight, 1);
        }

        float GetBoostFactor()
        {
#if ENABLE_INPUT_SYSTEM
            return boostFactorAction.ReadValue<Vector2>().y * 0.01f;
#else
            return Input.mouseScrollDelta.y * 0.01f;
#endif
        }

        Vector2 GetInputLookRotation()
        {
            // try to compensate the diff between the two input systems by multiplying with empirical values
#if ENABLE_INPUT_SYSTEM
            var delta = lookAction.ReadValue<Vector2>();
            delta *= 0.5f; // Account for scaling applied directly in Windows code by old input system.
            delta *= 0.1f; // Account for sensitivity setting on old Mouse X and Y axes.
            return delta;
#else
            Vector3 currentMousePosition = Input.mousePosition;
            if (!m_HasLastMousePosition)
            {
                m_LastMousePosition = currentMousePosition;
                m_HasLastMousePosition = true;
                return Vector2.zero;
            }

            Vector3 delta = currentMousePosition - m_LastMousePosition;
            m_LastMousePosition = currentMousePosition;
            return new Vector2(delta.x, delta.y);
#endif
        }

        bool IsBoostPressed()
        {
#if ENABLE_INPUT_SYSTEM
            bool boost = Keyboard.current != null ? Keyboard.current.leftShiftKey.isPressed : false; 
            boost |= Gamepad.current != null ? Gamepad.current.xButton.isPressed : false;
            return boost;
#else
            return Input.GetKey(KeyCode.LeftShift);
#endif

        }

        bool IsCameraRotationAllowed()
        {
#if ENABLE_INPUT_SYSTEM
            bool canRotate = Mouse.current != null ? Mouse.current.rightButton.isPressed : false;
            canRotate |= Gamepad.current != null ? Gamepad.current.rightStick.ReadValue().magnitude > 0 : false;
            return canRotate;
#else
            return Input.GetMouseButton(1);
#endif
        }

        bool IsRightMouseButtonDown()
        {
#if ENABLE_INPUT_SYSTEM
            return Mouse.current != null ? Mouse.current.rightButton.isPressed : false;
#else
            return Input.GetMouseButtonDown(1);
#endif
        }

        bool IsRightMouseButtonUp()
        {
#if ENABLE_INPUT_SYSTEM
            return Mouse.current != null ? !Mouse.current.rightButton.isPressed : false;
#else
            return Input.GetMouseButtonUp(1);
#endif
        }

        bool IsMiddleMouseButtonPressed()
        {
#if ENABLE_INPUT_SYSTEM
            return Mouse.current != null ? Mouse.current.middleButton.isPressed : false;
#else
            return Input.GetMouseButton(2);
#endif
        }

        float GetScrollDelta()
        {
#if ENABLE_INPUT_SYSTEM
            return Mouse.current != null ? Mouse.current.scroll.ReadValue().y / 120f : 0f;
#else
            return Input.mouseScrollDelta.y;
#endif
        }

    }

}
