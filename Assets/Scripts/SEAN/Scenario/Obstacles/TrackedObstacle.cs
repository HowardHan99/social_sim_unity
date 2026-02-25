// Copyright (c) 2021, Members of Yale Interactive Machines Group, Yale University,
// Nathan Tsoi
// All rights reserved.
// This source code is licensed under the BSD-style license found in the
// LICENSE file in the root directory of this source tree. 

using UnityEngine;

namespace SEAN.Scenario.Obstacles
{
    /// <summary>
    /// Component to mark a GameObject as a trackable obstacle.
    /// Attach this to any GameObject you want to publish to ROS as an obstacle.
    /// </summary>
    [RequireComponent(typeof(Collider))]
    public class TrackedObstacle : MonoBehaviour
    {
        [Tooltip("Unique ID for the obstacle. Leave at 0 to auto-assign a unique ID at runtime.")]
        public int id;

        [Tooltip("Type of the obstacle, e.g., 'box', 'cylinder', 'wall', 'furniture'.")]
        public string type = "box";

        [Tooltip("Enable debug visualization for this obstacle.")]
        public bool showDebug = false;

        /// <summary>
        /// Static counter used to auto-assign unique IDs when id is left at the default value of 0.
        /// This is the root fix for the bug where multiple obstacles sharing the same id (e.g. all
        /// instances of the same prefab) caused the publisher's dictionary to silently drop all but one.
        /// </summary>
        private static int s_nextAutoId = 1;

        /// <summary>
        /// Reset the static counter when entering play mode so IDs start fresh each session.
        /// Required because statics can survive domain reload if it is disabled.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            s_nextAutoId = 1;
        }

        private void Awake()
        {
            // Auto-assign a unique ID when id is left at the default value of 0.
            // This prevents the critical bug where multiple prefab instances (or manually
            // added TrackedObstacles left at default) all share id 0, causing the publisher
            // dictionary to collapse them into a single entry.
            if (id == 0)
            {
                id = s_nextAutoId++;
                Debug.Log($"[TrackedObstacle] Auto-assigned ID {id} to '{gameObject.name}'");
            }
        }

        /// <summary>
        /// Get the size of the obstacle based on its collider.
        /// </summary>
        public Vector3 GetSize()
        {
            Collider c = GetComponent<Collider>();
            // Ensure we use the absolute scale to prevent negative sizes in ROS, which can cause issues.
            Vector3 absScale = new Vector3(Mathf.Abs(transform.lossyScale.x), Mathf.Abs(transform.lossyScale.y), Mathf.Abs(transform.lossyScale.z));

            if (c is BoxCollider)
            {
                BoxCollider bc = (BoxCollider)c;
                // Important: we need to account for the transform's scale
                return Vector3.Scale(bc.size, absScale);
            }
            else if (c is SphereCollider)
            {
                SphereCollider sc = (SphereCollider)c;
                float maxScale = Mathf.Max(absScale.x, absScale.y, absScale.z);
                float diameter = sc.radius * 2 * maxScale;
                return new Vector3(diameter, diameter, diameter);
            }
            else if (c is CapsuleCollider)
            {
                CapsuleCollider cc = (CapsuleCollider)c;
                float radius = cc.radius * Mathf.Max(absScale.x, absScale.z);
                float height = cc.height * absScale.y;
                return new Vector3(radius * 2, height, radius * 2);
            }
            
            // Fallback to collider bounds if not a standard collider type
            return c.bounds.size;
        }

        /// <summary>
        /// Get the world position of the obstacle's center (accounting for collider center offset).
        /// </summary>
        public Vector3 GetCenter()
        {
            Collider c = GetComponent<Collider>();
            
            if (c is BoxCollider)
            {
                BoxCollider bc = (BoxCollider)c;
                return transform.TransformPoint(bc.center);
            }
            else if (c is SphereCollider)
            {
                SphereCollider sc = (SphereCollider)c;
                return transform.TransformPoint(sc.center);
            }
            else if (c is CapsuleCollider)
            {
                CapsuleCollider cc = (CapsuleCollider)c;
                return transform.TransformPoint(cc.center);
            }
            
            // Fallback to collider bounds center
            return c.bounds.center;
        }

        private void OnDrawGizmos()
        {
            if (showDebug)
            {
                Gizmos.color = Color.yellow;
                Gizmos.DrawWireCube(GetCenter(), GetSize());
            }
        }

        private void Start()
        {
            if (showDebug)
            {
                Debug.Log($"[TrackedObstacle] ID:{id} Type:{type} Center:{GetCenter()} Size:{GetSize()}");
            }
        }
    }
}

