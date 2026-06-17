using System.Collections;
using UnityEngine;

namespace IVI
{
    /// <summary>
    /// Parents a prop to a humanoid body bone (hand, head, etc.) and applies a local pose.
    /// </summary>
    public class AttachPropToHand : MonoBehaviour
    {
        public Transform prop;
        public HumanBodyBones handBone = HumanBodyBones.RightHand;

        [Tooltip("Fallback bone name if GetBoneTransform fails (e.g. Bip01 R Hand, Bip01 Head).")]
        public string autoTagBoneName = "Bip01 R Hand";

        [Header("Local pose relative to the body bone")]
        public Vector3 localPosition = new Vector3(0.006f, 0.002f, 0.004f);
        public Vector3 localEulerAngles = new Vector3(-68f, 8f, 88f);
        public Vector3 localScale = new Vector3(4f, 4f, 4f);

        [Tooltip("On attach, derive local offsets from the prop's current scene pose.")]
        public bool captureOffsetFromCurrentPoseOnStart = false;

        [Tooltip("Apply Local Scale after parenting.")]
        public bool applyLocalScale = true;

        [Header("Debug (read-only)")]
        public bool debugAttached;
        public string debugAnchorTarget;

        Animator animator;

        void Start()
        {
            StartCoroutine(AttachWhenReady());
        }

        IEnumerator AttachWhenReady()
        {
            for (int i = 0; i < 90; i++)
            {
                if (TryAttach())
                    yield break;

                yield return null;
            }

            yield return new WaitForEndOfFrame();
            if (!TryAttach())
            {
                Debug.LogWarning(
                    $"[AttachPropToHand] Could not parent prop on '{name}'. " +
                    $"No animated {handBone} and no child named '{autoTagBoneName}'.",
                    this);
            }
        }

        bool TryAttach()
        {
            if (prop == null)
            {
                Debug.LogWarning("[AttachPropToHand] Prop is not assigned.", this);
                return false;
            }

            if (debugAttached)
                return true;

            animator = AvatarAnimatorUtility.GetLocomotionAnimator(gameObject);
            if (animator != null && !animator.isInitialized && animator.runtimeAnimatorController != null)
                animator.Rebind();

            Transform anchor = ResolveAnchorTransform();
            if (anchor == null)
                return false;

            if (captureOffsetFromCurrentPoseOnStart)
                CaptureOffsetFromCurrentPose(anchor);

            prop.SetParent(anchor, false);
            prop.localPosition = localPosition;
            prop.localRotation = Quaternion.Euler(localEulerAngles);

            if (applyLocalScale)
                prop.localScale = localScale;

            debugAnchorTarget = anchor.name;
            debugAttached = true;
            return true;
        }

        void CaptureOffsetFromCurrentPose(Transform anchor)
        {
            localPosition = anchor.InverseTransformPoint(prop.position);
            localEulerAngles = (Quaternion.Inverse(anchor.rotation) * prop.rotation).eulerAngles;

            if (applyLocalScale)
                localScale = prop.localScale;
        }

        Transform ResolveAnchorTransform()
        {
            if (animator != null)
            {
                Transform bone = animator.GetBoneTransform(handBone);
                if (bone != null)
                    return bone;
            }

            if (string.IsNullOrEmpty(autoTagBoneName))
                return null;

            foreach (Transform t in transform.GetComponentsInChildren<Transform>(true))
            {
                if (t.name == autoTagBoneName)
                    return t;
            }

            return null;
        }
    }
}
