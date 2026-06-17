using System.Collections;
using UnityEngine;

namespace IVI
{
    /// <summary>
    /// Keeps a prop glued to an animated hand bone.
    /// Uses world-space follow by default because parenting to GetBoneTransform bones
    /// is unreliable when Optimize Game Objects is enabled on the model.
    /// </summary>
    [DefaultExecutionOrder(10001)]
    public class AttachPropToHand : MonoBehaviour
    {
        public Transform prop;
        public string handTag = "Hand";
        public HumanBodyBones handBone = HumanBodyBones.RightHand;

        [Tooltip("Optional: if nothing is tagged Hand yet, tag the first child with this name.")]
        public string autoTagBoneName = "Bip01 R Hand";

        [Header("Local pose relative to the hand bone")]
        public Vector3 localPosition = new Vector3(0.006f, 0.002f, 0.004f);
        public Vector3 localEulerAngles = new Vector3(-68f, 8f, 88f);
        public Vector3 localScale = new Vector3(4f, 4f, 4f);

        [Tooltip("World-space follow each frame. Recommended when the avatar uses Optimize Game Objects.")]
        public bool useWorldSpaceFollow = true;

        [Tooltip("When enabled, keeps the prop's current world pose instead of snapping to the hand.")]
        public bool preserveWorldPoseOnAttach = false;

        [Header("Debug (read-only)")]
        public bool debugAttached;
        public string debugHandTarget;
        public float debugDistanceToHand;
        public Vector3 debugHandWorldPosition;
        public Vector3 debugPropWorldPosition;

        Animator animator;
        Transform followRoot;
        bool attached;

        void Start()
        {
            followRoot = transform;
            StartCoroutine(AttachWhenReady());
        }

        IEnumerator AttachWhenReady()
        {
            for (int i = 0; i < 90 && !attached; i++)
            {
                if (TryBindAnimator())
                {
                    attached = true;
                    yield break;
                }

                yield return null;
            }

            yield return new WaitForEndOfFrame();
            attached = TryBindAnimator();

            if (!attached)
            {
                Debug.LogWarning(
                    $"[AttachPropToHand] Could not find hand on '{name}'. " +
                    $"No animated {handBone} bone and no child tagged '{handTag}'.",
                    this);
            }
        }

        void LateUpdate()
        {
            if (!attached || preserveWorldPoseOnAttach || prop == null)
                return;

            Transform hand = ResolveHandTransform();
            if (hand == null)
                return;

            debugHandTarget = hand.name;
            debugHandWorldPosition = hand.position;

            if (useWorldSpaceFollow)
            {
                if (prop.parent != followRoot)
                    prop.SetParent(followRoot, true);

                prop.SetPositionAndRotation(
                    hand.TransformPoint(localPosition),
                    hand.rotation * Quaternion.Euler(localEulerAngles));
                prop.localScale = localScale;
            }
            else
            {
                if (prop.parent != hand)
                    prop.SetParent(hand, false);

                prop.localPosition = localPosition;
                prop.localRotation = Quaternion.Euler(localEulerAngles);
                prop.localScale = localScale;
            }

            debugPropWorldPosition = prop.position;
            debugDistanceToHand = Vector3.Distance(prop.position, hand.position);
            debugAttached = true;
        }

        bool TryBindAnimator()
        {
            if (prop == null)
            {
                Debug.LogWarning("[AttachPropToHand] Prop is not assigned.", this);
                return false;
            }

            animator = AvatarAnimatorUtility.GetLocomotionAnimator(gameObject);
            if (animator != null && !animator.isInitialized && animator.runtimeAnimatorController != null)
                animator.Rebind();

            return ResolveHandTransform() != null;
        }

        Transform ResolveHandTransform()
        {
            if (animator != null)
            {
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
