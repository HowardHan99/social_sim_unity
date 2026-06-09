using UnityEngine;

namespace IVI
{
    /// <summary>
    /// Parents a prop (e.g. white cane) to a Humanoid hand bone at runtime.
    /// Works with Optimize Game Objects rigs where hand bones are not in the hierarchy.
    /// </summary>
    public class AttachPropToHand : MonoBehaviour
    {
        public Transform prop;
        public Animator animator;
        public HumanBodyBones handBone = HumanBodyBones.RightHand;

        [Header("Local pose on the hand bone")]
        public Vector3 localPosition = new Vector3(0.04f, 0.02f, 0.08f);
        public Vector3 localEulerAngles = new Vector3(-75f, 15f, 95f);
        public Vector3 localScale = new Vector3(7f, 7f, 7f);

        [Tooltip("When enabled, keeps world pose on attach then applies the local offsets above.")]
        public bool preserveWorldPoseOnAttach = false;

        void Start()
        {
            Attach();
        }

        public void Attach()
        {
            if (prop == null)
                prop = transform;

            if (animator == null)
                animator = GetComponentInChildren<Animator>(true);

            if (animator == null || prop == null)
            {
                Debug.LogWarning("[AttachPropToHand] Missing Animator or prop.", this);
                return;
            }

            Transform hand = animator.GetBoneTransform(handBone);
            if (hand == null)
            {
                Debug.LogWarning($"[AttachPropToHand] Could not find bone {handBone} on {animator.name}.", this);
                return;
            }

            prop.SetParent(hand, preserveWorldPoseOnAttach);

            if (!preserveWorldPoseOnAttach)
            {
                prop.localPosition = localPosition;
                prop.localRotation = Quaternion.Euler(localEulerAngles);
                prop.localScale = localScale;
            }
        }
    }
}
