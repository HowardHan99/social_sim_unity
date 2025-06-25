using UnityEngine;
using UnityEngine.AI;

namespace IVI
{
    public class ManualWheelchairController : MonoBehaviour
    {
        [Header("Manual Control Settings")]
        public float moveSpeed = 3f;
        public float rotationSpeed = 90f;
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
        
        // Manual movement
        private Vector3 manualVelocity;
        
        void Start()
        {
            // Get components
            sfpwdAgent = GetComponent<SFPWDAgent>();
            navMeshAgent = GetComponent<NavMeshAgent>();
            rb = GetComponent<Rigidbody>();
            baseAgent = GetComponent<SEAN.Scenario.Agents.Base>();
            
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
                HandleManualInput();
                ApplyManualMovement();
            }
        }
        
        void HandleManualInput()
        {
            float horizontal = 0f;
            float vertical = 0f;
            
            // Check arrow keys
            if (Input.GetKey(forwardKey)) vertical += 1f;
            if (Input.GetKey(backwardKey)) vertical -= 1f;
            if (Input.GetKey(leftKey)) horizontal -= 1f;
            if (Input.GetKey(rightKey)) horizontal += 1f;
            
            // Check WASD keys if enabled
            if (useWASD)
            {
                if (Input.GetKey(KeyCode.W)) vertical += 1f;
                if (Input.GetKey(KeyCode.S)) vertical -= 1f;
                if (Input.GetKey(KeyCode.A)) horizontal -= 1f;
                if (Input.GetKey(KeyCode.D)) horizontal += 1f;
            }
            
            // Calculate movement
            Vector3 moveDirection = transform.forward * vertical;
            float rotation = horizontal * rotationSpeed * Time.deltaTime;
            
            // Apply rotation
            if (Mathf.Abs(rotation) > 0.01f)
            {
                transform.Rotate(0, rotation, 0);
            }
            
            // Set manual velocity
            manualVelocity = moveDirection * moveSpeed;
        }
        
        void ApplyManualMovement()
        {
            if (rb != null)
            {
                // Use rigidbody for movement if available
                Vector3 newVelocity = manualVelocity;
                newVelocity.y = rb.velocity.y; // Preserve y velocity for gravity
                rb.velocity = newVelocity;
            }
            else
            {
                // Use transform movement as fallback
                transform.position += manualVelocity * Time.deltaTime;
            }
        }
        
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
            
            // Disable automatic components
            if (sfpwdAgent != null)
                sfpwdAgent.enabled = false;
            
            if (navMeshAgent != null)
            {
                navMeshAgent.enabled = false;
            }
            
            // Reset manual velocity
            manualVelocity = Vector3.zero;
            
            // Stop any existing movement
            if (rb != null)
            {
                rb.velocity = new Vector3(0, rb.velocity.y, 0);
            }
        }
        
        void SetAutomaticMode()
        {
            isManualMode = false;
            
            // Reset manual velocity
            manualVelocity = Vector3.zero;
            
            // Re-enable automatic components
            if (sfpwdAgent != null)
                sfpwdAgent.enabled = true;
            
            if (navMeshAgent != null)
            {
                navMeshAgent.enabled = true;
            }
            
            // Clear any manual velocity
            if (rb != null)
            {
                rb.velocity = new Vector3(0, rb.velocity.y, 0);
            }
        }
        
        void OnGUI()
        {
            // Display current mode in the corner
            string modeText = isManualMode ? "MANUAL MODE" : "AUTO MODE";
            string controlText = isManualMode ? 
                "Use Arrow Keys/WASD to move\nRight Shift: Switch to Auto" : 
                "Right Shift: Switch to Manual";
            
            GUI.Box(new Rect(10, 10, 250, 60), $"{modeText}\n{controlText}");
        }
        
        // Public methods for external control
        public void ForceManualMode()
        {
            SetManualMode();
        }
        
        public void ForceAutomaticMode()
        {
            SetAutomaticMode();
        }
        
        public bool IsInManualMode()
        {
            return isManualMode;
        }
    }
} 