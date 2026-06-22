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

    public class Publisher : MonoBehaviour
    {
        private ROSConnection ros;
        private SEAN sean;

        public string topicName = "/social_sim/agents";
        public string frame = "map";

        void Start()
        {
            ros = ROSConnection.instance;
            sean = SEAN.instance;
        }

        private void Update()
        {
            RosMessageTypes.SocialSimRos.MAgentArray message = new RosMessageTypes.SocialSimRos.MAgentArray();
            message.header.frame_id = frame;
            message.header.stamp = sean.clock.LastPublishedTime();

            List<Trajectory.TrackedAgent> people = GetPublishedAgents();
            message.agents = new RosMessageTypes.SocialSimRos.MAgent[people.Count];
            int i = 0;
            foreach (Trajectory.TrackedAgent person in people)
            {
                RosMessageTypes.SocialSimRos.MAgent agent = new RosMessageTypes.SocialSimRos.MAgent();
                agent.type = GetAgentType(person);
                agent.pose = Util.Geometry.GetMPose(person.gameObject.transform);
                Trajectory.TrackedTrajectory trajectory = person.trajectory;
                if (trajectory == null)
                    trajectory = person.GetComponent<Trajectory.TrackedTrajectory>();
                if (trajectory == null)
                    trajectory = person.gameObject.AddComponent<Trajectory.TrackedTrajectory>();
                agent.twist = Util.Geometry.GetMTwist(trajectory);
                message.agents[i++] = agent;
            }
            ros.Send(topicName, message);
        }

        private List<Trajectory.TrackedAgent> GetPublishedAgents()
        {
            List<Trajectory.TrackedAgent> people = new List<Trajectory.TrackedAgent>();
            HashSet<Trajectory.TrackedAgent> seen = new HashSet<Trajectory.TrackedAgent>();

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

        private static string GetAgentType(Trajectory.TrackedAgent person)
        {
            if (person != null &&
                (person.GetComponent<PlayerAgent>() != null ||
                 person.GetComponent<IVI.ManualWheelchairController>() != null ||
                 person.gameObject.name.Contains("Player")))
            {
                return "player";
            }

            return "person";
        }
    }
}
