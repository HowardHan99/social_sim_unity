// Copyright (c) 2021, Members of Yale Interactive Machines Group, Yale University,
// Nathan Tsoi
// All rights reserved.
// This source code is licensed under the BSD-style license found in the
// LICENSE file in the root directory of this source tree. 

using System;
using UnityEngine;

namespace SEAN.Environment
{
    public class Environment : MonoBehaviour
    {
        public string name { get; private set; }

        public GameObject environment { get { return gameObject.transform.GetChild(0).gameObject; } }

        public void Start()
        {
            // First child is the name of the environment
            name = environment.name;
        }

        public Camera topViewCamera
        {
            get
            {
                // Search recursively for TopViewCamera, including inactive objects
                Camera[] cameras = environment.GetComponentsInChildren<Camera>(true);
                foreach (Camera cam in cameras)
                {
                    if (cam.gameObject.name == "TopViewCamera")
                    {
                        cam.gameObject.tag = "TopViewCamera";
                        return cam;
                    }
                }

                // Fallback: search all transforms recursively
                Transform cameraTransform = FindTransformRecursive(environment.transform, "TopViewCamera");
                if (cameraTransform != null)
                {
                    Camera cam = cameraTransform.GetComponent<Camera>();
                    if (cam != null)
                    {
                        cameraTransform.gameObject.tag = "TopViewCamera";
                        return cam;
                    }
                }

                Debug.LogError($"TopViewCamera not found under {environment.name}. Searched {cameras.Length} cameras.");
                return null;
            }
        }

        // Helper to recursively search for a transform by name
        private Transform FindTransformRecursive(Transform parent, string name)
        {
            if (parent.name == name)
                return parent;

            foreach (Transform child in parent)
            {
                Transform result = FindTransformRecursive(child, name);
                if (result != null)
                    return result;
            }

            return null;
        }

        // Helper to get full path of GameObject
        private string GetGameObjectPath(GameObject obj)
        {
            string path = obj.name;
            Transform current = obj.transform.parent;
            while (current != null)
            {
                path = current.name + "/" + path;
                current = current.parent;
            }
            return path;
        }
    }
}