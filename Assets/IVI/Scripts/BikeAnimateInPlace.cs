using System.Collections.Generic;
using UnityEngine;

namespace IVI
{
    /// <summary>
    /// Keeps the Sepeda rig pedaling in place by restoring bone local positions after each animation update.
    /// The jalan clip bakes forward/back translation into the armature; rotations (pedals, wheels) are kept.
    /// </summary>
    [DefaultExecutionOrder(200)]
    public class BikeAnimateInPlace : MonoBehaviour
    {
        Transform[] lockedBones;
        Vector3[] initialLocalPositions;

        void Awake()
        {
            Transform armature = FindDeepChild(transform, "Armature_sepeda");
            if (armature == null)
            {
                Debug.LogWarning("[BikeAnimateInPlace] Armature_sepeda not found.", this);
                enabled = false;
                return;
            }

            var bones = new List<Transform>();
            CollectDescendants(armature, bones);
            lockedBones = bones.ToArray();
            initialLocalPositions = new Vector3[lockedBones.Length];

            for (int i = 0; i < lockedBones.Length; i++)
                initialLocalPositions[i] = lockedBones[i].localPosition;
        }

        void LateUpdate()
        {
            if (lockedBones == null)
                return;

            for (int i = 0; i < lockedBones.Length; i++)
                lockedBones[i].localPosition = initialLocalPositions[i];
        }

        static void CollectDescendants(Transform root, List<Transform> results)
        {
            results.Add(root);
            for (int i = 0; i < root.childCount; i++)
                CollectDescendants(root.GetChild(i), results);
        }

        static Transform FindDeepChild(Transform parent, string name)
        {
            foreach (Transform child in parent.GetComponentsInChildren<Transform>(true))
            {
                if (child.name == name)
                    return child;
            }

            return null;
        }
    }
}
