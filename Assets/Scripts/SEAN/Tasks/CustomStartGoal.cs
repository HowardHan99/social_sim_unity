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

        protected override bool NewTask()
        {
            robotGoal.SetActive(true);
            robotStart.transform.position = RobotStartLocation.transform.position;
            robotStart.transform.rotation = RobotStartLocation.transform.rotation;
            robotGoal.transform.position = RobotGoalLocation.transform.position;
            robotGoal.transform.rotation = RobotGoalLocation.transform.rotation;
            SetTargetFlags(robotGoal);

            return true;
        }

        // This task's start/goal come from hand-placed scene markers, which the user may move
        // while the trial-start prompt is showing (i.e. after PrepareTaskPreview locked them in).
        // Re-reading the markers here keeps the published goal in sync with where they are NOW.
        // NewTask is a pure marker->flag copy, so re-running it is safe (nothing random to re-roll).
        protected override void RefreshPreparedTask()
        {
            NewTask();
        }
    }
}