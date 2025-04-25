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

        // PID Controller
        public float P = 1, I = 1, D = 1;
        private float integral, lastError;

        // Manual control variables
        private bool manualControlActive = false;
        public float manualLinearSpeed = 2.0f;
        public float manualAngularSpeed = 2.0f;

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
                }
                else
                {
                    Debug.Log("ROS control activated");
                }
            }

            // Process manual control input
            if (manualControlActive)
            {
                HandleManualInput();
            }
        }

        private void HandleManualInput()
        {
            // WASD control
            targetLinVelocity = 0;
            targetAngVelocity = 0;

            // Forward/Backward
            if (UnityEngine.Input.GetKey(KeyCode.W))
            {
                targetLinVelocity = manualLinearSpeed;
            }
            else if (UnityEngine.Input.GetKey(KeyCode.S))
            {
                targetLinVelocity = -manualLinearSpeed;
            }

            // Turning
            if (UnityEngine.Input.GetKey(KeyCode.A))
            {
                targetAngVelocity = manualAngularSpeed;
            }
            else if (UnityEngine.Input.GetKey(KeyCode.D))
            {
                targetAngVelocity = -manualAngularSpeed;
            }

            // Stop with H key
            if (UnityEngine.Input.GetKey(KeyCode.H))
            {
                targetLinVelocity = 0;
                targetAngVelocity = 0;
            }

            // Update timestamp to prevent timeout in FixedUpdate
            lastMessageTS = Time.time;
        }

        private void FixedUpdate()
        {
            //ApplyLocalPositionToVisuals(wheelColl);
            //// All in local (base_link) coorindates
            //if (targetLinVelocity == 0.0f) {
            //    rb.velocity = new Vector3(0,0,0);
            //} else {
            //    rb.AddRelativeForce(Pid(targetLinVelocity, rb.transform.forward * rb.velocity, Time.deltaTime));
            //}
            //if (targetAngVelocity == 0.0f) {
            //    rb.angularVelocity = new Vector3(0,0,0);
            //} else {
            //    rb.AddRelativeTorque(Pid(targetAngVelocity, rb.transform.forward * rb.angularVelocity, Time.deltaTime));
            //}

            // Only check for ROS message timeout if not in manual control
            if (!manualControlActive && Time.time - lastMessageTS > maxTimeDeltaSec)
            {
                targetAngVelocity = targetLinVelocity = 0;
            }

            if (targetAngVelocity == 0.0f)
            {
                rb.angularVelocity = new Vector3(0, 0, 0);
            }
            else
            {
                rb.angularVelocity = new Vector3(0, -1 * targetAngVelocity, 0);
            }
            if (targetLinVelocity == 0.0f)
            {
                rb.velocity = new Vector3(0, rb.velocity.y, 0);
            }
            else
            {
                rb.velocity = rb.transform.forward * targetLinVelocity;
                // print("velocity: " + rb.velocity);
            }
            //print("velocity: " + rb.velocity);
        }

        override sealed protected void CmdVelMessage(RosMessageTypes.Geometry.MTwist msg)
        {
            // print("in callback message");
            if (msg == null) { return; }
            if (rb == null) { return; }
            
            // Only update velocity from ROS when manual control is not active
            if (!manualControlActive)
            {
                targetLinVelocity = (float)msg.linear.x;
                targetAngVelocity = (float)msg.angular.z;
                lastMessageTS = Time.time;
            }
        }

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
