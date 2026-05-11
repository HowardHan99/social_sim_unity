// Copyright (c) 2021, Members of Yale Interactive Machines Group, Yale University,
// Nathan Tsoi
// All rights reserved.
// This source code is licensed under the BSD-style license found in the
// LICENSE file in the root directory of this source tree. 

using System.Collections.Generic;
using UnityEngine;
using Unity.Robotics.ROSTCPConnector;

namespace SEAN.Scenario.Agents
{

    public class PositionPublisher : MonoBehaviour
    {
        private ROSConnection ros;
        private SEAN sean;

        public string topicName = "/social_sim/agent_positions";
        public string frame = "map";

        void Start()
        {
            ros = ROSConnection.instance;
            sean = SEAN.instance;
        }

        private void Update()
        {
            RosMessageTypes.Geometry.MPoseArray message = new RosMessageTypes.Geometry.MPoseArray();
            message.header.frame_id = frame;
            message.header.stamp = sean.clock.LastPublishedTime();
            List<Scenario.Trajectory.TrackedAgent> people = GetPublishedAgents();
            message.poses = new RosMessageTypes.Geometry.MPose[people.Count];
            int i = 0;
            foreach (Trajectory.TrackedAgent person in people)
            {
                message.poses[i++] = Util.Geometry.GetMPose(person.gameObject.transform);
            }
            ros.Send(topicName, message);
        }

        private List<Scenario.Trajectory.TrackedAgent> GetPublishedAgents()
        {
            List<Scenario.Trajectory.TrackedAgent> people = new List<Scenario.Trajectory.TrackedAgent>();
            HashSet<Scenario.Trajectory.TrackedAgent> seen = new HashSet<Scenario.Trajectory.TrackedAgent>();

            foreach (Trajectory.TrackedAgent person in sean.pedestrianBehavior.agents)
            {
                if (person == null || !person.isActiveAndEnabled || !person.gameObject.activeInHierarchy)
                    continue;

                people.Add(person);
                seen.Add(person);
            }

            foreach (Trajectory.TrackedAgent person in Resources.FindObjectsOfTypeAll<Trajectory.TrackedAgent>())
            {
                if (person == null || seen.Contains(person) || !person.gameObject.scene.IsValid() || !person.gameObject.activeInHierarchy)
                    continue;

                if (!IsRuntimePlayerAgent(person))
                    continue;

                people.Add(person);
                seen.Add(person);
            }

            return people;
        }

        private static bool IsRuntimePlayerAgent(Trajectory.TrackedAgent person)
        {
            if (person == null)
                return false;

            return person.GetComponent<PlayerAgent>() != null ||
                   person.GetComponent<IVI.ManualWheelchairController>() != null ||
                   person.GetComponent<IVI.SFPWDAgent>() != null;
        }
    }
}
