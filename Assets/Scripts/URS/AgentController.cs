using System.Linq;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Unity.RenderStreaming.Samples
{
    class AgentController : MonoBehaviour
    {
        [SerializeField] GameObject player;
        [SerializeField] GameObject cameraPivot;
        [SerializeField] InputReceiver playerInput;
        [SerializeField] TextMesh label;
        [SerializeField] GameObject captionForMobile;
        [SerializeField] GameObject captionForDesktop;

        [SerializeField] float moveSpeed = 10f;  // Reduced from 100f for walking speed
        [SerializeField] float rotateSpeed = 100f;  // Increased for camera rotation only
        [SerializeField] float jumpSpeed = 8f;  // Reduced from 500f for more realistic jump

        const float CooldownJump = 0.5f; // Reduced from 1.2f for more responsive jumping

        Vector2 inputMovement;
        Vector2 inputLook;
        Vector3 initialPosition;
        bool inputJump;
        bool isGrounded = true;
        float cooldownJumpDelta = CooldownJump;

        protected void Awake()
        {
            playerInput.onDeviceChange += OnDeviceChange;
            initialPosition = transform.position;
        }

        void OnDeviceChange(InputDevice device, InputDeviceChange change)
        {
            switch (change)
            {
                case InputDeviceChange.Added:
                    {
                        playerInput.PerformPairingWithDevice(device);
                        CheckPairedDevices();
                        return;
                    }
                case InputDeviceChange.Removed:
                    {
                        playerInput.UnpairDevices(device);
                        CheckPairedDevices();
                        return;
                    }
            }
        }

        public void CheckPairedDevices()
        {
            if (!playerInput.user.valid)
                return;

            bool hasTouchscreenDevice =
                playerInput.user.pairedDevices.Count(_ => _.path.Contains("Touchscreen")) > 0;

            captionForMobile.SetActive(hasTouchscreenDevice);
            captionForDesktop.SetActive(!hasTouchscreenDevice);
        }

        private void Update()
        {
            var rb = player.GetComponent<Rigidbody>();

            // Get camera's forward direction but keep it on the horizontal plane
            var cameraForward = cameraPivot.transform.forward;
            cameraForward.y = 0;
            cameraForward.Normalize();

            var cameraRight = cameraPivot.transform.right;
            cameraRight.y = 0;
            cameraRight.Normalize();

            // Calculate movement direction based on camera orientation
            var moveDirection = (cameraRight * inputMovement.x + cameraForward * inputMovement.y);

            // Apply movement force (for pedestrian movement)
            rb.AddForce(moveDirection * Time.deltaTime * moveSpeed * 10f); // Multiplied by 10 for Force mode

            // Limit horizontal velocity for more controlled movement
            var horizontalVelocity = new Vector3(rb.velocity.x, 0, rb.velocity.z);
            if (horizontalVelocity.magnitude > moveSpeed)
            {
                horizontalVelocity = horizontalVelocity.normalized * moveSpeed;
                rb.velocity = new Vector3(horizontalVelocity.x, rb.velocity.y, horizontalVelocity.z);
            }

            // Camera rotation (only rotate camera, not player)
            var cameraAngles = new Vector3(-inputLook.y, inputLook.x);
            var newAngles = cameraPivot.transform.localEulerAngles + cameraAngles * Time.deltaTime * rotateSpeed;

            // Clamp vertical rotation between -45 and 45 degrees
            if (newAngles.x > 180) newAngles.x -= 360;
            newAngles.x = Mathf.Clamp(newAngles.x, -45, 45);

            cameraPivot.transform.localEulerAngles = new Vector3(newAngles.x, newAngles.y, 0);

            // Simple ground check (you may want to use raycast for better accuracy)
            isGrounded = player.transform.position.y < initialPosition.y + 0.1f;

            // Jump logic
            if (inputJump && cooldownJumpDelta <= 0.0f && isGrounded)
            {
                rb.AddForce(Vector3.up * jumpSpeed, ForceMode.Impulse);
                cooldownJumpDelta = CooldownJump;
            }

            // Jump cooldown
            if (cooldownJumpDelta >= 0.0f)
            {
                inputJump = false;
                cooldownJumpDelta -= Time.deltaTime;
            }

            // Reset if the player falls off the floor
            if (player.transform.position.y < -5)
            {
                player.transform.position = initialPosition;
                rb.velocity = Vector3.zero;
            }
        }

        public void SetLabel(string text)
        {
            label.text = text;
        }

        public void OnControlsChanged()
        {
        }

        public void OnDeviceLost()
        {
        }

        public void OnDeviceRegained()
        {
        }

        public void OnMovement(InputAction.CallbackContext value)
        {
            inputMovement = value.ReadValue<Vector2>();
        }

        public void OnLook(InputAction.CallbackContext value)
        {
            inputLook = value.ReadValue<Vector2>();
        }

        public void OnJump(InputAction.CallbackContext value)
        {
            if (value.performed)
            {
                inputJump = true;
            }
        }
    }
}