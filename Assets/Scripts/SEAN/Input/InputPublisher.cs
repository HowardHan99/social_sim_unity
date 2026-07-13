// Copyright (c) 2021, Members of Yale Interactive Machines Group, Yale University,
// Nathan Tsoi
// All rights reserved.
// This source code is licensed under the BSD-style license found in the
// LICENSE file in the root directory of this source tree. 

using UnityEngine;
using Unity.Robotics.ROSTCPConnector;

namespace SEAN.Input
{
    public class InputPublisher : MonoBehaviour
    {
        private ROSConnection ros;
        private SEAN sean;
        public string TopicCmdVel = "/social_sim/cmd_vel";
        public string TopicTrigger = "/social_sim/trigger";

        /// <summary>
        ///  enable local joystick input
        /// </summary>
        public bool EnableJoystick = true;
        public bool PublishOnlyWhenRobotControlled = true;
        /// <summary>
        ///  enable local keyboard input
        /// </summary>
        public bool EnableKeyboard = true;

        public bool LocalInput
        {
            get
            {
                return RobotLocalInputAllowed() && (EnableJoystick || EnableKeyboard);
            }
        }

        /// <summary>
        ///  scale joystick input by this amount
        /// </summary>
        public float JoystickScaleLinear = -0.8f;
        public float JoystickScaleAngular = -8.0f;
        public string JoystickLinearAxis = "joystickLinearAxis";
        public string JoystickAngularAxis = "LogitechTwist";
        public string JoystickStartAxis = "L1";
        public float JoystickDeadzone = 0.03f;
        public float JoystickLinearFullThrow = 0.1f;
        public float JoystickAngularFullThrow = 1.0f;
        public float JoystickLinearResponseExponent = 1.6f;
        public float JoystickAngularResponseExponent = 1.0f;
        public float JoystickLinearAcceleration = 4.0f;
        public float JoystickLinearDeceleration = 3.0f;
        public float JoystickAngularAcceleration = 20.0f;

        /// <summary>
        ///  keyboard inputs translate directly to this output value
        /// </summary>
        public float FixedScaleLinear = -0.8f;
        public float FixedScaleAngular = 0.8f;

        private float _horizontal = 0;
        public float Horizontal { get { return _horizontal; } }
        private float _vertical = 0;
        public float Vertical { get { return _vertical; } }
        private bool _l1 = false;
        public bool L1 { get { return _l1; } }
        private float joystickLinearCenter;
        private float joystickAngularCenter;
        private bool joystickCenterCaptured;
        private bool warnedMissingJoystickAxis;
        private float currentJoystickLinear;
        private float currentJoystickAngular;

        void Start()
        {
            ros = ROSConnection.instance;
            sean = SEAN.instance;
            ApplyJoystickResponseDefaults();
        }

        void Update()
        {
            if (!RobotLocalInputAllowed())
            {
                joystickCenterCaptured = false;
                currentJoystickLinear = 0f;
                currentJoystickAngular = 0f;
                _horizontal = 0f;
                _vertical = 0f;
                _l1 = false;
                return;
            }

            if (EnableJoystick && UnityEngine.Input.GetJoystickNames().Length > 0)
            {
                ReadJoystick();
            }
            else if (EnableKeyboard)
            {
                joystickCenterCaptured = false;
                currentJoystickLinear = 0f;
                currentJoystickAngular = 0f;
                ReadKeyboard();
            }
            else
            {
                joystickCenterCaptured = false;
                currentJoystickLinear = 0f;
                currentJoystickAngular = 0f;
                _horizontal = 0f;
                _vertical = 0f;
                _l1 = false;
            }
        }

        void ReadKeyboard()
        {
            if (UnityEngine.Input.GetKey(KeyCode.Space))
            {
                _l1 = true;
            }
            else
            {
                _l1 = false;
            }
            if (UnityEngine.Input.GetKey(KeyCode.A) || UnityEngine.Input.GetKey(KeyCode.LeftArrow))
            {
                _horizontal = -FixedScaleAngular;
            }
            else if (UnityEngine.Input.GetKey(KeyCode.D) || UnityEngine.Input.GetKey(KeyCode.RightArrow))
            {
                _horizontal = FixedScaleAngular;
            }
            else
            {
                _horizontal = 0;
            }
            if (UnityEngine.Input.GetKey(KeyCode.W) || UnityEngine.Input.GetKey(KeyCode.UpArrow))
            {
                _vertical = -FixedScaleLinear;
            }
            else if (UnityEngine.Input.GetKey(KeyCode.S) || UnityEngine.Input.GetKey(KeyCode.DownArrow))
            {
                _vertical = FixedScaleLinear;
            }
            else
            {
                _vertical = 0;
            }
            Send();
        }

        void ReadJoystick()
        {
            // Set in project settings:
            // Button mapping (mac?):
            // 1: left analog stick
            // 2: right analog stick
            // 3: start button
            // 4: d-pad up
            // 5: d-pad right
            // 6: d-pad down
            // 7: d-pad left
            // 8: l2 trigger
            // 9: r2 trigger
            // 10: l1 trigger
            // 11: r1 trigger
            //https://forum.unity.com/threads/ps3-button-map.89288/

            // Button mapping (linux?):
            // 4: l1
            if (!joystickCenterCaptured)
            {
                joystickLinearCenter = ReadJoystickRawAxis(JoystickLinearAxis);
                joystickAngularCenter = ReadJoystickRawAxis(JoystickAngularAxis);
                joystickCenterCaptured = true;
            }

            float targetAngular = ProcessJoystickInput(ReadJoystickRawAxis(JoystickAngularAxis) - joystickAngularCenter, JoystickAngularFullThrow, JoystickAngularResponseExponent) * JoystickScaleAngular;
            float targetLinear = ProcessJoystickInput(ReadJoystickRawAxis(JoystickLinearAxis) - joystickLinearCenter, JoystickLinearFullThrow, JoystickLinearResponseExponent) * JoystickScaleLinear;
            float linearStep = Mathf.Abs(targetLinear) > Mathf.Abs(currentJoystickLinear)
                ? JoystickLinearAcceleration
                : JoystickLinearDeceleration;
            currentJoystickAngular = Mathf.MoveTowards(
                currentJoystickAngular,
                targetAngular,
                Mathf.Max(0f, JoystickAngularAcceleration) * Time.deltaTime);
            currentJoystickLinear = Mathf.MoveTowards(
                currentJoystickLinear,
                targetLinear,
                Mathf.Max(0f, linearStep) * Time.deltaTime);
            _horizontal = currentJoystickAngular;
            _vertical = currentJoystickLinear;
            _l1 = GetAxisSafely(ResolveJoystickAxisName(JoystickStartAxis)) != 0;

            if (Mathf.Abs(_horizontal) > 0.01f && Time.frameCount % 15 == 0)
                Debug.Log($"[TURNDIAG] InputPublisher horizontal={_horizontal:F3} scaleAng={JoystickScaleAngular} L1={_l1}", this);

            Send();
        }

        private bool RobotLocalInputAllowed()
        {
            if (!PublishOnlyWhenRobotControlled)
                return true;

            if (sean == null)
                sean = SEAN.instance;

            return sean != null &&
                   sean.ControlledAgent == Scenario.Agents.ControlledAgent.Robot &&
                   !HumanPwdManualSessionActive();
        }

        private bool HumanPwdManualSessionActive()
        {
            return SessionReview.SessionOnboardingSettings.HasCompletedOnboarding &&
                   SessionReview.SessionOnboardingSettings.PlayerMode == SessionReview.OnboardingPlayerMode.Human &&
                   SessionReview.SessionOnboardingSettings.PwdStartupControl == SessionReview.StartupControlMode.Manual;
        }

        private float ReadJoystickRawAxis(string axisName)
        {
            if (string.IsNullOrWhiteSpace(axisName))
                return 0f;

            string resolved = ResolveJoystickAxisName(axisName);
            return GetAxisSafely(resolved) * JoystickProfiles.AxisSign(resolved);
        }

        // Legacy aliases and per-device remapping (Logitech stick vs. gamepad) both live in
        // JoystickProfiles so every manual controller reads the same physical axes.
        private string ResolveJoystickAxisName(string axisName)
        {
            return JoystickProfiles.ResolveAxis(axisName);
        }

        private float GetAxisSafely(string axisName)
        {
            if (string.IsNullOrWhiteSpace(axisName))
                return 0f;

            try
            {
                return UnityEngine.Input.GetAxisRaw(axisName);
            }
            catch (System.ArgumentException)
            {
                if (!warnedMissingJoystickAxis)
                {
                    Debug.LogWarning($"[InputPublisher] Input axis '{axisName}' is not configured. Joystick value forced to 0.", this);
                    warnedMissingJoystickAxis = true;
                }
                return 0f;
            }
        }

        private float ApplyJoystickDeadzone(float value)
        {
            return Mathf.Abs(value) >= JoystickDeadzone ? value : 0f;
        }

        private float NormalizeJoystickThrow(float value, float fullThrow)
        {
            return Mathf.Clamp(value / Mathf.Max(0.01f, Mathf.Abs(fullThrow)), -1f, 1f);
        }

        private float ProcessJoystickInput(float value, float fullThrow, float responseExponent)
        {
            float normalized = NormalizeJoystickThrow(ApplyJoystickDeadzone(value), fullThrow);
            return ApplyJoystickResponseCurve(normalized, responseExponent);
        }

        private float ApplyJoystickResponseCurve(float value, float responseExponent)
        {
            float exponent = Mathf.Max(0.25f, responseExponent);
            return Mathf.Sign(value) * Mathf.Pow(Mathf.Abs(value), exponent);
        }

        private void ApplyJoystickResponseDefaults()
        {
            if (Mathf.Approximately(JoystickLinearFullThrow, 0.25f))
                JoystickLinearFullThrow = 0.1f;
            if (JoystickLinearResponseExponent <= 0f)
                JoystickLinearResponseExponent = 1.6f;
            if (JoystickAngularResponseExponent <= 0f)
                JoystickAngularResponseExponent = 1.2f;
        }

        public RosMessageTypes.Geometry.MTwist CmdVel
        {
            get
            {
                // cmd vel
                RosMessageTypes.Geometry.MTwist twist = new RosMessageTypes.Geometry.MTwist();
                twist.linear.x = Vertical;
                twist.angular.z = Horizontal;
                return twist;
            }
        }

        private void Send()
        {
            // trigger
            RosMessageTypes.Std.MBool b = new RosMessageTypes.Std.MBool();
            b.data = L1;
            ros.Send(TopicTrigger, b);
            // cmd vel
            ros.Send(TopicCmdVel, CmdVel);
        }
    }
}
