// Copyright (c) 2021, Members of Yale Interactive Machines Group, Yale University,
// Nathan Tsoi
// All rights reserved.
// This source code is licensed under the BSD-style license found in the
// LICENSE file in the root directory of this source tree. 

using UnityEngine;
using Unity.Robotics.ROSTCPConnector;
using System.Collections.Generic;

namespace SEAN.TF
{
    public class BaseTransformPublisher : MonoBehaviour
    {
        protected ROSConnection ros;
        private RosMessageTypes.Std.MTime LastHeader = new RosMessageTypes.Std.MTime();

        protected void Start()
        {
            ros = ROSConnection.instance;
        }

        public class NamedTransform
        {
            public string name;
            public RosMessageTypes.Geometry.MPoseStamped pose;

            public NamedTransform(string name, RosMessageTypes.Geometry.MPoseStamped pose)
            {
                this.name = name;
                this.pose = pose;
            }
        }

        protected void PublishIfNew(NamedTransform transform)
        {
            List<NamedTransform> transforms = new List<NamedTransform>();
            transforms.Add(transform);
            PublishIfNew(transforms);
        }

        protected void PublishIfNew(List<NamedTransform> transforms)
        {
            // TEMPORARY NAVSTACK WORKAROUND:
            // Force the ROS clock to publish immediately before stamping/sending
            // transforms so pose updates do not trail behind the latest simulated time.
            SEAN.instance.clock.Publish();

            foreach (NamedTransform transform in transforms)
            {
                SEAN.instance.clock.UpdateMHeader(transform.pose.header);
                // TEMPORARY NAVSTACK WORKAROUND:
                // Avoid dropping the entire transform batch when Unity/ROS clock stamps
                // do not advance exactly as expected. We currently prefer continuous
                // map/base_link updates over aggressive de-duplication while debugging
                // local planner state lag.
                string name = transform.name;
                if (!name.StartsWith("/"))
                {
                    name = "/" + name;
                }
                ros.Send(name, transform.pose);
            }
            LastHeader.secs = transforms[0].pose.header.stamp.secs;
            LastHeader.nsecs = transforms[0].pose.header.stamp.nsecs;
        }

        protected void PublishDirect(NamedTransform transform)
        {
            // TEMPORARY NAVSTACK WORKAROUND:
            // Direct-send the critical robot pose transform each physics step so
            // nav stack state is driven by the freshest available base pose.
            SEAN.instance.clock.Publish();
            SEAN.instance.clock.UpdateMHeader(transform.pose.header);

            string name = transform.name;
            if (!name.StartsWith("/"))
            {
                name = "/" + name;
            }

            ros.Send(name, transform.pose);
        }
    }
}
