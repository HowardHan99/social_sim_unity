using UnityEngine;
using UnityEngine.AI;

namespace IVI
{
    public class ManualWheelchairController : MonoBehaviour
    {
        [Header("Manual Control Settings")]
        public float maxMoveSpeed = 1f; // Align it with the max speed of wheelchair driver
        public float forceStrength = 2f;
        public float naturalDeceleration = 0.15f; // To simulate rolling resistance
        public float rotationSpeed = 30f;
        public KeyCode toggleModeKey = KeyCode.RightShift;

        [Header("Movement Keys")]
        public KeyCode forwardKey = KeyCode.UpArrow;
        public KeyCode backwardKey = KeyCode.DownArrow;
        public KeyCode leftKey = KeyCode.LeftArrow;
        public KeyCode rightKey = KeyCode.RightArrow;

        [Header("Alternative WASD Keys")]
        public bool useWASD = true;

        [Header("Status")]
        public bool isManualMode = false;

        // Components
        private SFPWDAgent sfpwdAgent;
        private NavMeshAgent navMeshAgent;
        private Rigidbody rb;
        private SEAN.Scenario.Agents.Base baseAgent;
        private Animator animator;

        // Manual movement - Force-based system
        private Vector3 currentVelocity;
        private Vector3 appliedForce;

        // State preservation
        private Vector3 lastAutomaticVelocity;
        private bool animatorWasEnabled;

        // Input state tracking
        private bool isMovingForward = false;
        private bool isMovingBackward = false;
        private bool isTurningLeft = false;
        private bool isTurningRight = false;

        void Start()
        {
            // Get components
            sfpwdAgent = GetComponent<SFPWDAgent>();
            navMeshAgent = GetComponent<NavMeshAgent>();
            rb = GetComponent<Rigidbody>();
            baseAgent = GetComponent<SEAN.Scenario.Agents.Base>();
            animator = GetComponent<Animator>();

            // If animator is not on this GameObject, check children
            if (animator == null)
            {
                animator = GetComponentInChildren<Animator>();
            }

            // Start in automatic mode by default
            SetAutomaticMode();
        }

        void Update()
        {
            // Check for mode toggle
            if (Input.GetKeyDown(toggleModeKey))
            {
                ToggleControlMode();
            }

            // Handle input based on current mode
            if (isManualMode)
            {
                ProcessMovementActions(); // Reset forces first
                HandleKeyboardInput();     // Apply forces based on input
                ApplyVelocityPhysics();    // Process physics
                ApplyManualMovement();     // Apply final movement
            }
        }

        #region Input Handling

        void HandleKeyboardInput()
        {
            // Reset input states
            isMovingForward = false;
            isMovingBackward = false;
            isTurningLeft = false;
            isTurningRight = false;

            // Check arrow keys
            if (Input.GetKey(forwardKey)) MoveForward();
            if (Input.GetKey(backwardKey)) MoveBackward();
            if (Input.GetKey(leftKey)) TurnLeft();
            if (Input.GetKey(rightKey)) TurnRight();

            // Check WASD keys if enabled
            if (useWASD)
            {
                if (Input.GetKey(KeyCode.W)) MoveForward();
                if (Input.GetKey(KeyCode.S)) MoveBackward();
                if (Input.GetKey(KeyCode.A)) TurnLeft();
                if (Input.GetKey(KeyCode.D)) TurnRight();
            }

            // If no movement input, stop moving
            if (!isMovingForward && !isMovingBackward)
            {
                StopMoving();
            }
        }

        #endregion

        #region Movement Action Methods

        /// <summary>
        /// Command to move forward - applies forward force
        /// </summary>
        public void MoveForward()
        {
            isMovingForward = true;
            ApplyForce(transform.forward * forceStrength);
        }

        /// <summary>
        /// Command to move backward - applies backward force
        /// </summary>
        public void MoveBackward()
        {
            isMovingBackward = true;
            ApplyForce(-transform.forward * forceStrength);
        }

        /// <summary>
        /// Command to turn left
        /// </summary>
        public void TurnLeft()
        {
            isTurningLeft = true;
            ApplyRotation(-1f);
        }

        /// <summary>
        /// Command to turn right
        /// </summary>
        public void TurnRight()
        {
            isTurningRight = true;
            ApplyRotation(1f);
        }

        /// <summary>
        /// Command to stop moving - no force applied, natural deceleration takes over
        /// </summary>
        public void StopMoving()
        {
            // Don't apply any force, let natural deceleration handle it
        }

        /// <summary>
        /// Command to stop turning
        /// </summary>
        public void StopTurning()
        {
            // Rotation stops automatically when not called
        }

        /// <summary>
        /// Emergency brake - applies strong deceleration force
        /// </summary>
        public void EmergencyBrake()
        {
            if (currentVelocity.magnitude > 0.1f)
            {
                Vector3 brakeForce = -currentVelocity.normalized * (forceStrength * 2f);
                ApplyForce(brakeForce);
            }
        }

        #endregion

        #region Movement Implementation

        void ApplyForce(Vector3 force)
        {
            appliedForce += force;
        }

        void ApplyRotation(float direction)
        {
            float rotation = direction * rotationSpeed * Time.deltaTime;
            transform.Rotate(0, rotation, 0);
        }

        void ProcessMovementActions()
        {
            // Reset applied force each frame
            appliedForce = Vector3.zero;

            // Forces are applied by the action methods during this frame
            // No additional processing needed here as forces are accumulated
        }

        void ApplyVelocityPhysics()
        {
            // Apply accumulated forces to velocity
            currentVelocity += appliedForce * Time.deltaTime;

            // Apply natural deceleration (friction/air resistance)
            if (appliedForce.magnitude < 0.1f) // Only apply natural deceleration when no force is being applied
            {
                Vector3 decelerationForce = -currentVelocity.normalized * naturalDeceleration;
                currentVelocity += decelerationForce * Time.deltaTime;

                // Stop completely when velocity is very low to avoid jittering
                if (currentVelocity.magnitude < 0.1f)
                {
                    currentVelocity = Vector3.zero;
                }
            }

            // Clamp to maximum speed
            if (currentVelocity.magnitude > maxMoveSpeed)
            {
                currentVelocity = currentVelocity.normalized * maxMoveSpeed;
            }

            // Clear Y component (keep movement horizontal)
            currentVelocity.y = 0;
        }

        void ApplyManualMovement()
        {
            if (rb != null)
            {
                // Use rigidbody for movement if available
                Vector3 newVelocity = currentVelocity;
                newVelocity.y = rb.velocity.y; // Preserve y velocity for gravity
                rb.velocity = newVelocity;
            }
            else
            {
                // Use transform movement as fallback
                transform.position += currentVelocity * Time.deltaTime;
            }
        }

        #endregion

        #region Mode Management

        void ToggleControlMode()
        {
            if (isManualMode)
            {
                SetAutomaticMode();
                Debug.Log("Switched to Automatic Control Mode");
            }
            else
            {
                SetManualMode();
                Debug.Log("Switched to Manual Control Mode");
            }
        }

        void SetManualMode()
        {
            isManualMode = true;

            // Capture current velocity from the automatic system for smooth transition
            CaptureAutomaticVelocity();

            // Disable automatic components
            if (sfpwdAgent != null)
                sfpwdAgent.enabled = false;

            if (navMeshAgent != null)
            {
                navMeshAgent.enabled = false;
            }

            // Disable animator to prevent animation conflicts with manual movement
            if (animator != null)
            {
                animatorWasEnabled = animator.enabled;
                animator.enabled = false;
                Debug.Log("Animator disabled for manual control");
            }

            // Set initial manual velocity to the captured automatic velocity for smooth transition
            currentVelocity = new Vector3(lastAutomaticVelocity.x, 0, lastAutomaticVelocity.z);
            appliedForce = Vector3.zero;

            // Apply the captured velocity immediately for seamless transition
            if (rb != null)
            {
                Vector3 newVelocity = currentVelocity;
                newVelocity.y = rb.velocity.y; // Preserve y velocity for gravity
                rb.velocity = newVelocity;
            }

            Debug.Log($"Manual mode activated. Initial velocity: {currentVelocity}");
        }

        void SetAutomaticMode()
        {
            isManualMode = false;

            // Re-enable animator if it was previously enabled
            if (animator != null && animatorWasEnabled)
            {
                animator.enabled = true;
                Debug.Log("Animator re-enabled for automatic control");
            }

            // Reset manual movement variables
            currentVelocity = Vector3.zero;
            appliedForce = Vector3.zero;

            // Reset input states
            isMovingForward = false;
            isMovingBackward = false;
            isTurningLeft = false;
            isTurningRight = false;

            // Re-enable automatic components
            if (sfpwdAgent != null)
                sfpwdAgent.enabled = true;

            if (navMeshAgent != null)
            {
                navMeshAgent.enabled = true;
            }

            // Clear any manual velocity but preserve the transition
            if (rb != null)
            {
                // Don't immediately zero the velocity - let the automatic system take over gradually
                // rb.velocity = new Vector3(0, rb.velocity.y, 0);
            }
        }

        void CaptureAutomaticVelocity()
        {
            lastAutomaticVelocity = Vector3.zero;

            // Try to get velocity from different sources
            if (sfpwdAgent != null && sfpwdAgent.enabled)
            {
                // Get velocity from SFPWDAgent
                lastAutomaticVelocity = sfpwdAgent.velocity;
                Debug.Log($"Captured SFPWDAgent velocity: {lastAutomaticVelocity}");
            }
            else if (baseAgent != null && baseAgent.enabled)
            {
                // Fallback to base agent velocity
                lastAutomaticVelocity = baseAgent.velocity;
                Debug.Log($"Captured Base Agent velocity: {lastAutomaticVelocity}");
            }
            else if (rb != null)
            {
                // Fallback to rigidbody velocity
                lastAutomaticVelocity = rb.velocity;
                lastAutomaticVelocity.y = 0; // Remove y component for horizontal movement
                Debug.Log($"Captured Rigidbody velocity: {lastAutomaticVelocity}");
            }
            else if (navMeshAgent != null && navMeshAgent.enabled)
            {
                // Fallback to NavMeshAgent velocity
                lastAutomaticVelocity = navMeshAgent.velocity;
                Debug.Log($"Captured NavMeshAgent velocity: {lastAutomaticVelocity}");
            }

            // Ensure we have a reasonable velocity magnitude
            if (lastAutomaticVelocity.magnitude > maxMoveSpeed)
            {
                lastAutomaticVelocity = lastAutomaticVelocity.normalized * maxMoveSpeed;
                Debug.Log($"Clamped velocity to maxMoveSpeed: {lastAutomaticVelocity}");
            }
        }

        #endregion

        #region Helper Structures

        [System.Serializable]
        public struct InputState
        {
            public bool isMovingForward;
            public bool isMovingBackward;
            public bool isTurningLeft;
            public bool isTurningRight;
        }

        #endregion
    }
}