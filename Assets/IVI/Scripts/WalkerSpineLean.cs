using UnityEngine;

namespace IVI
{
    /// <summary>
    /// Applies a forward lean to spine bones after the Animator runs.
    /// Humanoid avatars ignore manual bone edits on prefab once an Animator is active;
    /// this script re-applies the lean every frame in LateUpdate.
    /// </summary>
    [DefaultExecutionOrder(1000)]
    public class WalkerSpineLean : MonoBehaviour
    {
        [Tooltip("Root of the rig, usually the child named 'man'.")]
        public Transform rigRoot;

        [Header("Extra rotation per spine bone (degrees, local space)")]
        public Vector3 spine02Offset = new Vector3(-20f, 0f, 0f);
        public Vector3 spine01Offset = new Vector3(-12f, 0f, 0f);
        public Vector3 spineOffset = new Vector3(-8f, 0f, 0f);

        Transform spine02;
        Transform spine01;
        Transform spine;
        Quaternion baseSpine02;
        Quaternion baseSpine01;
        Quaternion baseSpine;
        bool bonesFound;
        bool basesCaptured;

        void Awake()
        {
            if (rigRoot == null)
            {
                var man = transform.Find("man");
                rigRoot = man != null ? man : transform;
            }

            CacheBones();
        }

        void CacheBones()
        {
            if (rigRoot == null) return;

            var hips = rigRoot.Find("Armature/Hips");
            if (hips == null) return;

            spine02 = hips.Find("Spine02");
            spine01 = spine02 != null ? spine02.Find("Spine01") : null;
            spine = spine01 != null ? spine01.Find("Spine") : null;

            bonesFound = spine02 != null;
        }

        void LateUpdate()
        {
            if (!bonesFound) CacheBones();
            if (!bonesFound) return;

            if (!basesCaptured)
            {
                if (spine02 != null) baseSpine02 = spine02.localRotation;
                if (spine01 != null) baseSpine01 = spine01.localRotation;
                if (spine != null) baseSpine = spine.localRotation;
                basesCaptured = true;
            }

            if (spine02 != null)
                spine02.localRotation = baseSpine02 * Quaternion.Euler(spine02Offset);
            if (spine01 != null)
                spine01.localRotation = baseSpine01 * Quaternion.Euler(spine01Offset);
            if (spine != null)
                spine.localRotation = baseSpine * Quaternion.Euler(spineOffset);
        }
    }
}
