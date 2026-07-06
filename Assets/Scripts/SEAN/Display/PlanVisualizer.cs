// Copyright (c) 2021, Members of Yale Interactive Machines Group, Yale University,
// Nathan Tsoi
// All rights reserved.
// This source code is licensed under the BSD-style license found in the
// LICENSE file in the root directory of this source tree. 

using UnityEngine;
using SEAN.Display.VolumetricLine;
using Unity.Robotics.ROSTCPConnector;
using Unity.Robotics.ROSTCPConnector.ROSGeometry;
using System.Linq;
using System.Collections.Generic;

namespace SEAN.Display
{
    public class PlanVisualizer : MonoBehaviour
    {
        private SEAN sean;

        public string Topic;
        public Color LineColor;
        public float waitSec = 0.25f;
        public float pThresh = 0.5f;
        private ulong stamp;
        private ulong prevStamp;

        private RosMessageTypes.Nav.MPath message;
        private bool started = false;
        private bool suppressedForReview = false;
        private bool suppressedForManualControl = false;

        /// <summary>The line is hidden while any reason is active.</summary>
        private bool RenderingSuppressed => suppressedForReview || suppressedForManualControl;

        // settings for rendering path
        public Material LightSaberMaterial;

        public int SampledPath = 25; // no matter the length of the path, only sample [pathLength] points
        private int pathLength;

        private List<Vector3> pathPositions;
        private Vector3[] renderPathPositions;

        VolumetricLineStripBehavior lineStripBehavior;
        MeshRenderer lineMeshRenderer;

        /// <summary>
        /// Returns a copy of the current planned path positions, or null if no plan exists.
        /// Used by LiveTrajectoryRecorder to snapshot the ROS nav plan for session review.
        /// </summary>
        public Vector3[] GetCurrentPlanPositions()
        {
            if (renderPathPositions == null || renderPathPositions.Length == 0)
                return null;
            return (Vector3[])renderPathPositions.Clone();
        }

        /// <summary>Independent reasons the live ROS plan line may be hidden.</summary>
        public enum SuppressionReason { SessionReview, ManualControl }

        /// <summary>
        /// When suppressed, the live ROS plan line is force-hidden and will NOT re-render even if
        /// new plan messages keep arriving. Reasons are tracked independently (the line stays
        /// hidden while any is active) so callers don't clobber each other:
        ///  - SessionReview freezes the sim but can't stop ROS republishing the global plan
        ///    (message delivery runs in Update, unaffected by Time.timeScale), so a one-shot
        ///    clear would immediately reappear; review shows its own hideable snapshot copy.
        ///  - ManualControl hides the planned path while a human is driving the robot.
        /// </summary>
        public void SetRenderingSuppressed(SuppressionReason reason, bool suppressed)
        {
            switch (reason)
            {
                case SuppressionReason.SessionReview: suppressedForReview = suppressed; break;
                case SuppressionReason.ManualControl: suppressedForManualControl = suppressed; break;
            }
            ApplySuppressionState();
        }

        /// <summary>Back-compat overload for session-review callers.</summary>
        public void SetRenderingSuppressed(bool suppressed)
        {
            SetRenderingSuppressed(SuppressionReason.SessionReview, suppressed);
        }

        private void ApplySuppressionState()
        {
            if (RenderingSuppressed)
            {
                if (lineStripBehavior != null)
                    EnableLineStrip(false);
            }
            else
            {
                // Restore the last rendered plan line, if we still have one. (Going through
                // ProcessMessage would be skipped by the stamp gate for an unchanged message.)
                if (lineStripBehavior != null && renderPathPositions != null && renderPathPositions.Length > 0)
                    EnableLineStrip(true);
            }
        }

        public void ClearCurrentPlan()
        {
            renderPathPositions = null;
            message = null;
            stamp = 0;
            prevStamp = 0;

            if (pathPositions != null)
                pathPositions.Clear();

            if (lineStripBehavior != null)
                EnableLineStrip(false);
        }

        void Awake()
        {
            // Make sure we catch the first global plan message
            ROSConnection.instance.Subscribe<RosMessageTypes.Nav.MPath>(Topic, ReceiveMessage);
        }

        void Start()
        {
            pathPositions = new List<Vector3>();
            sean = SEAN.instance;
            lineStripBehavior = gameObject.GetComponent<VolumetricLineStripBehavior>();
            if (lineStripBehavior == null)
            {
                lineStripBehavior = gameObject.AddComponent<VolumetricLineStripBehavior>();
                // TemplateMaterial must be set first!
                lineStripBehavior.TemplateMaterial = LightSaberMaterial;
                lineStripBehavior.LightSaberFactor = 1;
                lineStripBehavior.LineWidth = 0.2f;
                lineStripBehavior.LineColor = LineColor;
            }
            started = true;
            ProcessMessage();
        }

        void ReceiveMessage(RosMessageTypes.Nav.MPath message)
        {
            this.message = message;
            ProcessMessage();
        }

        void EnableLineStrip(bool enable)
        {
            if (lineStripBehavior == null)
                return;
            lineStripBehavior.enabled = enable;

            // Disabling the behaviour alone does NOT stop rendering: the line is a mesh drawn
            // by the MeshRenderer (RequireComponent on VolumetricLineStripBehavior), which stays
            // enabled and keeps drawing the last plan. Toggle the renderer so the line truly hides.
            if (lineMeshRenderer == null)
                lineMeshRenderer = lineStripBehavior.GetComponent<MeshRenderer>();
            if (lineMeshRenderer != null)
                lineMeshRenderer.enabled = enable;
        }

        void ProcessMessage()
        {
            if (!started)
            {
                return;
            }
            if (message == null)
            {
                return;
            }
            stamp = message.header.stamp.secs;
            if (prevStamp == null || stamp - prevStamp < waitSec)
            {
                return;
            }
            pathPositions.Clear();
            if (message.poses.Length > 2)
            {
                Vector3 lastP = Vector3.zero;
                for (int i = 0; i < message.poses.Length - 1; i++)
                {
                    Vector3 p = message.poses[i].pose.position.From<FLU>();
                    p.y = sean.robot.position.y;
                    double dist = Vector3.Distance(lastP, p);
                    if (dist > pThresh)
                    {
                        pathPositions.Add(p);
                        lastP = p;
                    }
                    if (pathPositions.Count == SampledPath)
                    {
                        break;
                    }
                }
                for (int i = pathPositions.Count; i < SampledPath; i++)
                {
                    pathPositions.Add(lastP);
                }
                renderPathPositions = pathPositions.ToArray<Vector3>();
                lineStripBehavior.UpdateLineVertices(renderPathPositions);
                // Always compute + store the plan (LiveTrajectoryRecorder reads
                // renderPathPositions); only actually draw it when not suppressed. Manual control
                // and session review hide the line but keep the data flowing to the recorder.
                EnableLineStrip(!RenderingSuppressed);
            }
            else
            {
                renderPathPositions = null;
                EnableLineStrip(false);
            }
            prevStamp = stamp;
        }
    }
}
