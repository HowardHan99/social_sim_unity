// Copyright (c) 2021, Members of Yale Interactive Machines Group, Yale University,
// Nathan Tsoi
// All rights reserved.
// This source code is licensed under the BSD-style license found in the
// LICENSE file in the root directory of this source tree. 

using UnityEngine;

namespace SEAN.Control
{
    public class VelocityController : ControlSubscriber
    {
        private Rigidbody rb;

        private float targetLinVelocity, targetAngVelocity;
        public float maxTimeDeltaSec = 0.25f;
        private float lastMessageTS = 0;

        // PID Controller - Not used in momentum-based transition directly, but kept for potential future use
        public float P = 1, I = 1, D = 1;
        private float integral, lastError;

        // Manual control variables
        private bool manualControlActive = false;
        public float manualLinearSpeed = 0.01f;
        public float manualAngularSpeed = 0.01f;

        // Momentum-based Transition variables
        private float prevLinVelocity = 0f;
        private float prevAngVelocity = 0f;
        public float velocityDamping = 0.9f;

        // Debug fields to show last received ROS commands in Inspector
        [Header("Debug ROS Speeds (Read-Only)")]
        public float DebugROSLinSpeed;
        public float DebugROSAngSpeed;

        protected void Start()
        {
            base.Start();
            rb = sean.robot.base_link.GetComponent<Rigidbody>();
            // ROSConnection.instance.Subscribe<RosMessageTypes.Geometry.MTwist>(Topic, CmdVelMessage);
        }

        private void Update()
        {
            // Toggle manual control with left shift
            if (UnityEngine.Input.GetKeyDown(KeyCode.LeftShift))
            {
                manualControlActive = !manualControlActive;
                if (manualControlActive)
                {
                    Debug.Log("Manual control activated");
                    // When switching to manual, ensure prevVelocities are current target velocities
                    // to avoid momentary hiccup if Update runs before a new manual input processes.
                    // This helps if switching while robot was moving under ROS.
                    prevLinVelocity = targetLinVelocity;
                    prevAngVelocity = targetAngVelocity;
                }
                else
                {
                    Debug.Log("ROS control activated");
                    // When switching to ROS, ensure prevVelocities are current target velocities
                    // to avoid momentary hiccup if Update runs before a new ROS message processes.
                    // This helps if switching while robot was moving under manual control.
                    prevLinVelocity = targetLinVelocity;
                    prevAngVelocity = targetAngVelocity;
                }
            }

            // Process manual control input if active
            if (manualControlActive)
            {
                HandleManualInput();
            }
        }

        private void HandleManualInput()
        {
            // Initialize desired linear velocity to the current (previous frame's target) velocity
            // This ensures that if W/S is not pressed, the intention is to maintain current linear momentum.
            float manualDesiredLin = prevLinVelocity;
            
            // Initialize desired angular velocity to 0 by default.
            // This means if A/D is not pressed, the robot will try to stop turning.
            float manualDesiredAng = 0f; 

            // Forward/Backward
            if (UnityEngine.Input.GetKey(KeyCode.W))
            {
                manualDesiredLin = manualLinearSpeed; // Accelerate/set target to forward speed
            }
            else if (UnityEngine.Input.GetKey(KeyCode.S))
            {
                manualDesiredLin = -manualLinearSpeed; // Accelerate/set target to backward speed
            }

            // Turning (A for Right, D for Left as per original logic)
            // If A or D is pressed, set a desired angular speed.
            // If not, manualDesiredAng remains 0f, causing the robot to decelerate its turn.
            if (UnityEngine.Input.GetKey(KeyCode.A))
            {
                manualDesiredAng = manualAngularSpeed; 
            }
            else if (UnityEngine.Input.GetKey(KeyCode.D))
            {
                manualDesiredAng = -manualAngularSpeed;
            }

            // Stop with H key - explicitly sets desired to 0 for smooth deceleration
            if (UnityEngine.Input.GetKey(KeyCode.H))
            {
                manualDesiredLin = 0;
                manualDesiredAng = 0;
            }

            float interpolationFactor = 1.0f - Mathf.Clamp(velocityDamping, 0.0f, 0.999f);

            // Smoothly update target velocities
            targetLinVelocity = Mathf.Lerp(prevLinVelocity, manualDesiredLin, interpolationFactor);
            targetAngVelocity = Mathf.Lerp(prevAngVelocity, manualDesiredAng, interpolationFactor);

            // Update previous velocities for next frame's calculation
            prevLinVelocity = targetLinVelocity;
            prevAngVelocity = targetAngVelocity;

            // Update timestamp to prevent ROS timeout while in manual control
            lastMessageTS = Time.time;
        }

        private void FixedUpdate()
        {
            // Only check for ROS message timeout if not in manual control
            if (!manualControlActive && Time.time - lastMessageTS > maxTimeDeltaSec)
            {
                // Hard stop on timeout for safety, could be smoothed too if desired
                targetAngVelocity = 0;
                targetLinVelocity = 0;
                // Also reset prevVelocities to reflect the stop
                prevAngVelocity = 0;
                prevLinVelocity = 0;
            }

            if (rb == null) return;

            // Apply angular velocity
            if (Mathf.Approximately(targetAngVelocity, 0.0f) && Mathf.Approximately(rb.angularVelocity.y, 0.0f))
            {
                rb.angularVelocity = Vector3.zero; // Ensure it's truly zero if target is zero
            }
            else
            {
                rb.angularVelocity = new Vector3(0, -1 * targetAngVelocity, 0);
            }

            // Apply linear velocity
            if (Mathf.Approximately(targetLinVelocity, 0.0f) && Mathf.Approximately(rb.velocity.magnitude, 0.0f))
            {
                 // If target is 0 and current velocity is already very small (or zero), set rb.velocity to zero.
                 // This helps prevent tiny residual velocities if rb.velocity.y is non-zero from physics.
                rb.velocity = new Vector3(0, rb.velocity.y, 0); // Keep current Y velocity (gravity, etc.)
            }
            else
            {
                // Apply forward/backward velocity, preserving existing Y velocity (for gravity/physics)
                Vector3 currentVelocity = rb.velocity;
                Vector3 targetWorldVelocity = rb.transform.forward * targetLinVelocity;
                rb.velocity = new Vector3(targetWorldVelocity.x, currentVelocity.y, targetWorldVelocity.z);
            }
        }

        override sealed protected void CmdVelMessage(RosMessageTypes.Geometry.MTwist msg)
        {
            if (msg == null) { return; }
            if (rb == null) { return; }
            
            // Update debug fields with raw ROS values
            DebugROSLinSpeed = (float)msg.linear.x;
            DebugROSAngSpeed = (float)msg.angular.z;

            // Only update velocity from ROS when manual control is not active
            if (!manualControlActive)
            {
                float rosLinVelocity = DebugROSLinSpeed; // Use the stored debug value
                float rosAngVelocity = DebugROSAngSpeed; // Use the stored debug value

                float interpolationFactor = 1.0f - Mathf.Clamp(velocityDamping, 0.0f, 0.999f);

                targetLinVelocity = Mathf.Lerp(prevLinVelocity, rosLinVelocity, interpolationFactor);
                targetAngVelocity = Mathf.Lerp(prevAngVelocity, rosAngVelocity, interpolationFactor);

                prevLinVelocity = targetLinVelocity;
                prevAngVelocity = targetAngVelocity;
                lastMessageTS = Time.time;
            }
        }

        // PID function - not directly used by momentum control but part of the original script
        private float Pid(float setpoint, float actual, float timeFrame)
        {
            float present = setpoint - actual;
            integral += present * timeFrame;
            float deriv = (present - lastError) / timeFrame;
            lastError = present;
            return present * P + integral * I + deriv * D;
        }
    }
}
