// Copyright (c) 2021, Members of Yale Interactive Machines Group, Yale University,
// Nathan Tsoi
// All rights reserved.
// This source code is licensed under the BSD-style license found in the
// LICENSE file in the root directory of this source tree. 

using UnityEngine;
using Unity.Robotics.ROSTCPConnector;
using Unity.Robotics.ROSTCPConnector.ROSGeometry;

namespace SEAN.Scenario.Obstacles
{
    /// <summary>
    /// Publisher for obstacle data to ROS.
    /// Finds all TrackedObstacle components in the scene and publishes their transforms.
    /// Unlike agent publishing, this publishes at a lower rate since obstacles don't move frequently.
    /// </summary>
    public class ObstaclePublisher : MonoBehaviour
    {
        [Tooltip("The ROS topic to publish obstacle data to.")]
        public string topicName = "/social_sim/obstacles";

        [Tooltip("The frame of reference for the obstacle poses.")]
        public string frame = "map";

        [Tooltip("How often to publish obstacle data (in seconds). Set to 0 for every frame.")]
        public float publishRateHz = 1.0f;

        [Tooltip("Enable debug logging.")]
        public bool showDebug = true;

        [Tooltip("Print full JSON message (verbose, use only for debugging).")]
        public bool printJsonMessage = false;

        private ROSConnection ros;
        private SEAN sean;
        private TrackedObstacle[] sceneObstacles;
        private float lastPublishTime;
        private int publishCount = 0;

        void Start()
        {
            ros = ROSConnection.instance;
            sean = SEAN.instance;

            // Register the publisher with ROS before publishing any messages
            // ros.RegisterPublisher(topicName, RosMessageTypes.SocialSimRos.MObstacleArray.RosMessageName);

            // Find all obstacles in the scene at startup
            sceneObstacles = FindObjectsOfType<TrackedObstacle>();

            if (showDebug)
            {
                // Debug.Log($"[ObstaclePublisher] Initialized. Found {sceneObstacles.Length} obstacles in the scene.");
                // Debug.Log($"[ObstaclePublisher] Publishing to topic: {topicName} in frame: {frame} at {publishRateHz} Hz");
            }

            lastPublishTime = Time.time;
        }

        private void Update()
        {
            // Publish at specified rate (or every frame if publishRateHz is 0)
            if (publishRateHz == 0 || Time.time - lastPublishTime > (1.0f / publishRateHz))
            {
                PublishObstacles();
                lastPublishTime = Time.time;
            }
        }

        private void PublishObstacles()
        {
            var message = new RosMessageTypes.SocialSimRos.MObstacleArray();
            message.header.frame_id = frame;
            message.header.stamp = sean.clock.LastPublishedTime();
            message.obstacles = new RosMessageTypes.SocialSimRos.MObstacle[sceneObstacles.Length];

            for (int i = 0; i < sceneObstacles.Length; i++)
            {
                var obstacle = sceneObstacles[i];
                var obstacleMsg = new RosMessageTypes.SocialSimRos.MObstacle();

                obstacleMsg.id = obstacle.id;
                obstacleMsg.type = obstacle.type;

                // Get pose with coordinate system transformation (Unity LHS Y-up to ROS RHS Z-up)
                obstacleMsg.pose = Util.Geometry.GetMPose(obstacle.transform);

                // Get size with coordinate system transformation
                Vector3 unitySize = obstacle.GetSize();
                obstacleMsg.scale = Util.Geometry.GetGeometryVector3Scale(unitySize);

                message.obstacles[i] = obstacleMsg;
            }

            // Debug logging
            publishCount++;
            if (showDebug)
            {
                // A compact, single-line summary that is easy to read in the collapsed console view.
                string summary = $"[ObstaclePublisher] Publishing {message.obstacles.Length} obstacles. Frame: '{message.header.frame_id}', Stamp: {message.header.stamp.secs}";
                // Debug.Log(summary);
            }

            // Detailed multi-line logging for debugging (use sparingly)
            if (printJsonMessage)
            {
                var logBuilder = new System.Text.StringBuilder();
                logBuilder.AppendLine("[ObstaclePublisher] Full Message Details:");
                logBuilder.AppendFormat("  Header: frame='{0}', stamp={1}.{2:D9}\n", message.header.frame_id, message.header.stamp.secs, message.header.stamp.nsecs);
                logBuilder.AppendFormat("  Obstacle Count: {0}\n", message.obstacles.Length);
                foreach (var obstacleMsg in message.obstacles)
                {
                    logBuilder.AppendFormat("    - ID: {0}, Type: '{1}', Pos: ({2:F2}, {3:F2}, {4:F2}), Scale: ({5:F2}, {6:F2}, {7:F2})\n",
                        obstacleMsg.id,
                        obstacleMsg.type,
                        obstacleMsg.pose.position.x,
                        obstacleMsg.pose.position.y,
                        obstacleMsg.pose.position.z,
                        obstacleMsg.scale.x,
                        obstacleMsg.scale.y,
                        obstacleMsg.scale.z
                    );
                }
                // Debug.Log(logBuilder.ToString());
            }

            ros.Send(topicName, message);
        }

        /// <summary>
        /// Manually refresh the list of obstacles in the scene.
        /// Call this if obstacles are added/removed at runtime.
        /// </summary>
        public void RefreshObstacles()
        {
            sceneObstacles = FindObjectsOfType<TrackedObstacle>();
            if (showDebug)
            {
                // Debug.Log($"[ObstaclePublisher] Refreshed obstacle list. Now tracking {sceneObstacles.Length} obstacles.");
            }
        }

        /// <summary>
        /// Force an immediate publish (useful for testing or when obstacles are moved).
        /// </summary>
        public void ForcePublish()
        {
            if (showDebug)
            {
                // Debug.Log("[ObstaclePublisher] Force publishing obstacles.");
            }
            PublishObstacles();
        }

        private void OnValidate()
        {
            // Ensure publish rate is not negative
            if (publishRateHz < 0)
            {
                publishRateHz = 0;
            }
        }
    }
}

