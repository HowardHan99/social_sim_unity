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

            // Find all obstacles in the scene at startup
            sceneObstacles = FindObjectsOfType<TrackedObstacle>();

            if (showDebug)
            {
                Debug.Log($"[ObstaclePublisher] Initialized. Found {sceneObstacles.Length} obstacles in the scene.");
                Debug.Log($"[ObstaclePublisher] Publishing to topic: {topicName} in frame: {frame} at {publishRateHz} Hz");
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
                obstacleMsg.scale = Util.Geometry.GetGeometryVector3(unitySize.To<FLU>());

                message.obstacles[i] = obstacleMsg;
            }

            // Debug logging
            publishCount++;
            if (showDebug && publishCount % 10 == 0) // Log every 10th publish to avoid spam
            {
                Debug.Log($"[ObstaclePublisher] Published {sceneObstacles.Length} obstacles (count: {publishCount})");
            }

            // Detailed JSON logging for debugging (use sparingly)
            if (printJsonMessage)
            {
                string jsonMessage = JsonUtility.ToJson(message, true);
                Debug.Log($"[ObstaclePublisher] Message JSON:\n{jsonMessage}");
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
                Debug.Log($"[ObstaclePublisher] Refreshed obstacle list. Now tracking {sceneObstacles.Length} obstacles.");
            }
        }

        /// <summary>
        /// Force an immediate publish (useful for testing or when obstacles are moved).
        /// </summary>
        public void ForcePublish()
        {
            if (showDebug)
            {
                Debug.Log("[ObstaclePublisher] Force publishing obstacles.");
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

