// Copyright (c) 2021, Members of Yale Interactive Machines Group, Yale University,
// Nathan Tsoi
// All rights reserved.
// This source code is licensed under the BSD-style license found in the
// LICENSE file in the root directory of this source tree.

using UnityEngine;

namespace SEAN.Tasks
{
    public class CustomStartGoal : Base
    {
        Scenario.PedestrianBehavior.LabStudy scenario;

        public GameObject RobotStartLocation;
        public GameObject RobotGoalLocation;

        [Tooltip("Keep the Goal location node and the runtime Target marker at the same pose, and hide the node's duplicate flag visuals, so there is only ONE goal object to see and drag.")]
        public bool singleGoalMarker = true;

        private bool hasSyncedGoalPose = false;
        private Vector3 syncedGoalPosition;
        private Quaternion syncedGoalRotation;
        private bool loggedMissingLocations = false;

        protected override bool NewTask()
        {
            if (!HasLocations())
            {
                return false;
            }

            robotGoal.SetActive(true);
            robotStart.transform.position = RobotStartLocation.transform.position;
            robotStart.transform.rotation = RobotStartLocation.transform.rotation;
            robotGoal.transform.position = RobotGoalLocation.transform.position;
            robotGoal.transform.rotation = RobotGoalLocation.transform.rotation;
            RememberGoalPose();
            SetTargetFlags(robotGoal);

            return true;
        }

        public override void Start()
        {
            base.Start();
            HideDuplicateGoalFlags();
        }

        // This task's start/goal come from hand-placed scene markers, which the user may move
        // while the trial-start prompt is showing (i.e. after PrepareTaskPreview locked them in).
        // Re-reading the markers here keeps the published goal in sync with where they are NOW.
        // NewTask is a pure marker->flag copy, so re-running it is safe (nothing random to re-roll).
        protected override void RefreshPreparedTask()
        {
            NewTask();
        }

        /// <summary>
        /// The Goal location node and the runtime Target marker are two transforms describing ONE
        /// goal: NewTask copies node -> marker, while world building, the review tools and the ROS
        /// republish read the marker. Mirroring whichever of the two moved onto the other means the
        /// user only ever has to drag one object, and the published goal, the completion check and
        /// the flag can no longer disagree.
        /// </summary>
        void LateUpdate()
        {
            if (!singleGoalMarker)
                return;
            // A bound goal object already writes BOTH transforms every LateUpdate; mirroring on
            // top of that would only fight it.
            if (RobotGoalObjectBinding.BoundObject != null)
                return;
            if (robotGoal == null || RobotGoalLocation == null)
                return;

            Transform marker = robotGoal.transform;
            Transform node = RobotGoalLocation.transform;

            if (!hasSyncedGoalPose)
            {
                // The authored node is the initial authority, same as NewTask.
                marker.SetPositionAndRotation(node.position, node.rotation);
            }
            else if (HasMovedSinceSync(marker))
            {
                node.SetPositionAndRotation(marker.position, marker.rotation);
            }
            else if (HasMovedSinceSync(node))
            {
                marker.SetPositionAndRotation(node.position, node.rotation);
            }

            RememberGoalPose();
        }

        bool HasMovedSinceSync(Transform t)
        {
            return (t.position - syncedGoalPosition).sqrMagnitude > 1e-6f ||
                   Quaternion.Angle(t.rotation, syncedGoalRotation) > 0.01f;
        }

        void RememberGoalPose()
        {
            if (robotGoal == null)
                return;
            syncedGoalPosition = robotGoal.transform.position;
            syncedGoalRotation = robotGoal.transform.rotation;
            hasSyncedGoalPose = true;
        }

        /// <summary>
        /// The Goal location node usually carries its own preview flag cube, which lands right on
        /// top of the runtime Target marker's flag — two identical flags the user has to keep in
        /// sync by hand. Only the marker's flag survives (it is the one GoalBeacon labels and the
        /// review overlays toggle); anything else authored under the node (a door standing in for
        /// the goal) is left alone.
        /// </summary>
        void HideDuplicateGoalFlags()
        {
            if (!singleGoalMarker || RobotGoalLocation == null || robotGoal == null)
                return;
            // Nothing to fall back on if the runtime marker carries no flag of its own.
            if (!HasTargetFlag(robotGoal.transform))
                return;

            foreach (Transform child in RobotGoalLocation.GetComponentsInChildren<Transform>(true))
            {
                if (IsTargetFlag(child))
                    child.gameObject.SetActive(false);
            }
        }

        static bool HasTargetFlag(Transform root)
        {
            foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
            {
                if (IsTargetFlag(child))
                    return true;
            }
            return false;
        }

        static bool IsTargetFlag(Transform t)
        {
            return t.name == TargetFlagCubeName || t.name == TargetFlagArrowName;
        }

        bool HasLocations()
        {
            // robotStart/robotGoal are resolved by Base.EnsureInitialized; re-checking them here
            // keeps NewTask safe on any path that reaches it before the markers exist.
            if (robotStart == null || robotGoal == null)
                return false;
            if (RobotStartLocation != null && RobotGoalLocation != null)
                return true;

            if (!loggedMissingLocations)
            {
                loggedMissingLocations = true;
                Debug.LogError($"[{name}] Robot Start Location and/or Robot Goal Location are not " +
                               "assigned in the Inspector; this task cannot place the robot or publish a goal.");
            }
            return false;
        }
    }
}
