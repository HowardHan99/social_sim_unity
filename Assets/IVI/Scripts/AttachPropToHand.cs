using System.Collections;
using UnityEngine;

namespace IVI
{
    /// <summary>
    /// Parents a prop to the animated hand and snaps it to the hand's world pose.
    /// Resolves the hand via Animator.GetBoneTransform first, then a child tagged Hand.
    /// </summary>
    [DefaultExecutionOrder(1000)]
    public class AttachPropToHand : MonoBehaviour
    {
        public Transform prop;
        public string handTag = "Hand";
        public HumanBodyBones handBone = HumanBodyBones.RightHand;

        [Tooltip("Optional: if nothing is tagged Hand yet, tag the first child with this name.")]
        public string autoTagBoneName = "Bip01 R Hand";

        [Header("Local pose on the hand (applied after snapping to hand world pose)")]
        public Vector3 localPosition = new Vector3(0.08f, 0.02f, 0.04f);
        public Vector3 localEulerAngles = new Vector3(-75f, 15f, 95f);
        public Vector3 localScale = new Vector3(4f, 4f, 4f);

        [Tooltip("When enabled, keeps the prop's current world pose instead of snapping to the hand.")]
        public bool preserveWorldPoseOnAttach = false;

        [Header("Debug (read-only)")]
        public bool debugAttached;
        public string debugHandTarget;
        public Vector3 debugHandWorldPosition;
        public Vector3 debugPropWorldPositionBefore;
        public Vector3 debugPropWorldPositionAfter;

        bool attached;

        void Start()
        {
            StartCoroutine(AttachWhenReady());
        }

        IEnumerator AttachWhenReady()
        {
            // Wait until the humanoid avatar/bones are initialized.
            for (int i = 0; i < 30 && !attached; i++)
            {
                if (Attach())
                    yield break;

                yield return null;
            }

            yield return new WaitForEndOfFrame();

            if (!attached)
                Attach();

            if (!attached)
            {
                Debug.LogWarning(
                    $"[AttachPropToHand] Could not attach prop on '{name}'. " +
                    $"No animated {handBone} bone and no child tagged '{handTag}'.",
                    this);
            }
        }

        public bool Attach()
        {
            if (attached)
                return true;

            if (prop == null)
            {
                Debug.LogWarning("[AttachPropToHand] Prop is not assigned.", this);
                return false;
            }

            Transform hand = ResolveHandTransform();
            if (hand == null)
                return false;

            debugHandTarget = hand.name;
            debugHandWorldPosition = hand.position;
            debugPropWorldPositionBefore = prop.position;

            if (preserveWorldPoseOnAttach)
            {
                prop.SetParent(hand, true);
            }
            else
            {
                prop.SetParent(hand, false);
                prop.position = hand.position;
                prop.rotation = hand.rotation;
                // prop.localScale = localScale;
                // prop.localPosition = localPosition;
                // prop.localRotation = Quaternion.Euler(localEulerAngles);
            }

            debugPropWorldPositionAfter = prop.position;
            attached = true;
            debugAttached = true;
            return true;
        }

        Transform ResolveHandTransform()
        {
            Animator animator = AvatarAnimatorUtility.GetLocomotionAnimator(gameObject);
            if (animator != null)
            {
                if (!animator.isInitialized && animator.runtimeAnimatorController != null)
                    animator.Rebind();

                Transform bone = animator.GetBoneTransform(handBone);
                if (bone != null)
                    return bone;
            }

            Transform tagged = FindTaggedChild(handTag);
            if (tagged != null)
                return tagged;

            if (string.IsNullOrEmpty(autoTagBoneName))
                return null;

            foreach (Transform t in transform.GetComponentsInChildren<Transform>(true))
            {
                if (t.name != autoTagBoneName)
                    continue;

                t.gameObject.tag = handTag;
                return t;
            }

            return null;
        }

        Transform FindTaggedChild(string tag)
        {
            foreach (Transform t in transform.GetComponentsInChildren<Transform>(true))
            {
                if (t.CompareTag(tag))
                    return t;
            }

            return null;
        }
    }
}
