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
                Transform cameraTransform = environment.transform.Find("TopViewCamera");
                if (cameraTransform == null)
                {
                    Debug.LogError("TopViewCamera not found under " + environment.name);
                    return null;
                }
                GameObject gameObject = cameraTransform.gameObject;
                gameObject.tag = "TopViewCamera";
                return gameObject.GetComponent<Camera>();
            }
        }
    }
}