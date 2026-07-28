// Copyright (c) 2021, Members of Yale Interactive Machines Group, Yale University,
// Nathan Tsoi
// All rights reserved.
// This source code is licensed under the BSD-style license found in the
// LICENSE file in the root directory of this source tree. 

using UnityEngine;

namespace SEAN.Scenario
{
    public class Robot : MonoBehaviour
    {
        public float radius = 0.16f;
        public GameObject base_link;
        public Camera camera_first;
        public Camera camera_third;
        public Camera camera_overhead;
        [Tooltip("Rear-view camera mounted on the robot's right-back corner, shown as the bottom-right screen panel. Created automatically when left unassigned.")]
        public Camera camera_rear;

        public Trajectory.TrackedTrajectory trajectory { get; private set; }
        private void GetOrAttachTrajectory()
        {
            if (trajectory != null) { return; }
            trajectory = gameObject.GetComponent<Trajectory.TrackedTrajectory>();
            if (trajectory == null)
            {
                trajectory = gameObject.AddComponent(typeof(Trajectory.TrackedTrajectory)) as Trajectory.TrackedTrajectory;
                trajectory.mainGameObject = base_link;
            }
        }
        public void Start()
        {
            GetOrAttachTrajectory();
            AttachComfortBlur(camera_first);
            AttachComfortBlur(camera_third);
            AttachComfortBlur(camera_overhead);
            if (camera_first == null)
            {
                throw new System.ArgumentException("A first person camera must be assigned to the robot " + name);
            }
            if (camera_third == null)
            {
                throw new System.ArgumentException("A third person camera must be assigned to the robot " + name);
            }
            if (camera_overhead == null)
            {
                throw new System.ArgumentException("A overhead camera must be assigned to the robot " + name);
            }
            if (camera_rear == null)
            {
                camera_rear = CreateRearCamera();
            }
            AttachComfortBlur(camera_rear);
        }

        /// <summary>
        /// Backup-style rear-view camera: mounted on the right-back corner of the
        /// chassis looking backwards, rendered as a bottom-right mini panel
        /// (mirroring the overhead mini in the top-left corner).
        /// </summary>
        private Camera CreateRearCamera()
        {
            GameObject camObj = new GameObject("RobotRearCamera");
            camObj.transform.SetParent(base_link.transform, false);
            // Slightly behind and above the chassis so the lens never sits
            // inside body/sensor geometry.
            camObj.transform.localPosition = new Vector3(0.2f, 0.6f, -0.35f);
            camObj.transform.localRotation = Quaternion.Euler(15f, 180f, 0f);

            Camera cam = camObj.AddComponent<Camera>();
            cam.fieldOfView = 70f;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 200f;
            cam.rect = new Rect(0.72f, 0.03f, 0.25f, 0.25f);
            // Same depth as the overhead mini so both panels draw over the
            // full-screen first/third person views.
            cam.depth = camera_overhead.depth;
            cam.targetDisplay = 0;
            return cam;
        }
        public new Transform transform
        {
            get
            {
                return base_link.transform;
            }
        }
        public Vector3 position
        {
            get
            {
                return transform.position;
            }
        }
        public Quaternion rotation
        {
            get
            {
                return transform.rotation;
            }
        }
        public override string ToString()
        {
            return gameObject.name;
        }

        private static void AttachComfortBlur(Camera camera)
        {
            if (camera == null)
                return;

            if (camera.GetComponent<ComfortMotionBlur>() == null)
                camera.gameObject.AddComponent<ComfortMotionBlur>();
        }
    }
}
