using System.Collections;
using UnityEngine;

namespace IVI
{
    /// <summary>
    /// Pulls arms inward (phone-holding pose) on top of walk/idle animation.
    /// Uses elbow IK hints + direct bone rotation after the Animator updates.
    /// </summary>
    public class PhoneUserArmPose : MonoBehaviour
    {
        [Header("Arms — degrees / strength")]
        [Tooltip("Main control: pull elbows toward the torso.")]
        [Range(0f, 90f)] public float armInward = 45f;

        [Tooltip("Raise upper arm forward toward chest.")]
        [Range(-90f, 90f)] public float rightArmForward = 25f;
        [Range(-90f, 90f)] public float leftArmForward = 10f;

        [Tooltip("Bend elbow — forearm toward chest / phone.")]
        [Range(0f, 120f)] public float rightElbowBend = 65f;
        [Range(0f, 120f)] public float leftElbowBend = 85f;

        [Tooltip("Roll forearms inward toward the torso midline.")]
        [Range(0f, 60f)] public float forearmInward = 35f;

        [Header("Spine — degrees")]
        public Vector3 spineEuler = new Vector3(-25f, 0f, 0f);
        public Vector3 chestEuler = new Vector3(-15f, 0f, 0f);
        public Vector3 neckEuler = new Vector3(12f, 0f, 0f);

        [Tooltip("0 = animation only, 1 = full pose")]
        [Range(0f, 1f)] public float poseWeight = 1f;

        [Header("Debug (read-only)")]
        public bool bonesReady;
        public string debugAnimatorTarget;
        public int debugBonesAppliedLastFrame;
        public bool debugRightUpperArmFound;
        public bool debugLeftUpperArmFound;

        PhoneUserArmPoseDriver driver;

        void OnEnable()
        {
            StartCoroutine(InitDriverWhenReady());
        }

        void OnDisable()
        {
            if (driver != null)
                driver.Unbind();
        }

        IEnumerator InitDriverWhenReady()
        {
            bonesReady = false;

            for (int i = 0; i < 90; i++)
            {
                if (TryEnsureDriver())
                    break;

                yield return null;
            }

            if (!bonesReady)
                Debug.LogWarning("[PhoneUserArmPose] Animator / bones not ready.", this);
        }

        bool TryEnsureDriver()
        {
            Animator animator = AvatarAnimatorUtility.GetLocomotionAnimator(gameObject);
            debugAnimatorTarget = animator != null ? animator.gameObject.name : "(none)";

            if (animator == null || animator.avatar == null || !animator.avatar.isHuman)
                return false;

            if (!animator.isInitialized && animator.runtimeAnimatorController != null)
                animator.Rebind();

            driver = animator.GetComponent<PhoneUserArmPoseDriver>();
            if (driver == null)
                driver = animator.gameObject.AddComponent<PhoneUserArmPoseDriver>();

            bonesReady = driver.Bind(this, animator);
            return bonesReady;
        }

        internal void ReportDebug(int bonesApplied, bool rightArm, bool leftArm)
        {
            debugBonesAppliedLastFrame = bonesApplied;
            debugRightUpperArmFound = rightArm;
            debugLeftUpperArmFound = leftArm;
        }

        internal void ApplyElbowHints(Animator animator)
        {
            if (poseWeight <= 0f)
            {
                animator.SetIKHintPositionWeight(AvatarIKHint.RightElbow, 0f);
                animator.SetIKHintPositionWeight(AvatarIKHint.LeftElbow, 0f);
                return;
            }

            float pull = armInward * poseWeight * 0.004f;
            Transform root = animator.transform;

            ApplyHint(animator, AvatarIKHint.RightElbow, HumanBodyBones.RightLowerArm,
                armInward > 0f ? -root.right * pull : Vector3.zero,
                root.forward * (rightArmForward * poseWeight * 0.002f));
            ApplyHint(animator, AvatarIKHint.LeftElbow, HumanBodyBones.LeftLowerArm,
                armInward > 0f ? root.right * pull : Vector3.zero,
                root.forward * (leftArmForward * poseWeight * 0.002f));
        }

        static void ApplyHint(Animator animator, AvatarIKHint hint, HumanBodyBones lowerArmBone, Vector3 inwardOffset, Vector3 forwardOffset)
        {
            Transform elbow = PhoneUserArmPoseDriver.ResolveBone(animator, lowerArmBone);
            Vector3 worldOffset = inwardOffset + forwardOffset;
            if (elbow == null || worldOffset.sqrMagnitude < 0.0000001f)
            {
                animator.SetIKHintPositionWeight(hint, 0f);
                return;
            }

            animator.SetIKHintPositionWeight(hint, 1f);
            animator.SetIKHintPosition(hint, elbow.position + worldOffset);
        }

        internal int ApplyBoneOffsets(Animator animator)
        {
            if (poseWeight <= 0f)
                return 0;

            float w = poseWeight;
            int applied = 0;

            applied += ApplyBone(animator, HumanBodyBones.RightShoulder, new Vector3(0f, 0f, armInward * 0.15f * w));
            applied += ApplyBone(animator, HumanBodyBones.LeftShoulder, new Vector3(0f, 0f, -armInward * 0.15f * w));

            applied += ApplyBone(animator, HumanBodyBones.RightUpperArm,
                new Vector3(rightArmForward * w, 0f, -armInward * w));
            applied += ApplyBone(animator, HumanBodyBones.LeftUpperArm,
                new Vector3(leftArmForward * w, 0f, armInward * w));

            applied += ApplyBone(animator, HumanBodyBones.RightLowerArm,
                new Vector3(rightElbowBend * w, 0f, forearmInward * w));
            applied += ApplyBone(animator, HumanBodyBones.LeftLowerArm,
                new Vector3(leftElbowBend * w, 0f, -forearmInward * w));

            applied += ApplyBone(animator, HumanBodyBones.Spine, spineEuler * w);
            applied += ApplyBone(animator, HumanBodyBones.Chest, chestEuler * w);
            applied += ApplyBone(animator, HumanBodyBones.Neck, neckEuler * w);

            return applied;
        }

        static int ApplyBone(Animator animator, HumanBodyBones bone, Vector3 euler)
        {
            if (euler.sqrMagnitude < 0.0001f)
                return 0;

            Transform t = PhoneUserArmPoseDriver.ResolveBone(animator, bone);
            if (t == null)
                return 0;

            t.localRotation = t.localRotation * Quaternion.Euler(euler);
            return 1;
        }
    }

    /// <summary>
    /// On the Animator GameObject — OnAnimatorIK for elbow hints, LateUpdate for bone rotation.
    /// </summary>
    [DefaultExecutionOrder(10000)]
    sealed class PhoneUserArmPoseDriver : MonoBehaviour
    {
        static readonly string[] RightUpperArmNames = { "Bip01 R UpperArm", "RightUpperArm" };
        static readonly string[] LeftUpperArmNames = { "Bip01 L UpperArm", "LeftUpperArm" };
        static readonly string[] RightLowerArmNames = { "Bip01 R Forearm", "RightLowerArm" };
        static readonly string[] LeftLowerArmNames = { "Bip01 L Forearm", "LeftLowerArm" };
        static readonly string[] RightShoulderNames = { "Bip01 R Clavicle", "RightShoulder" };
        static readonly string[] LeftShoulderNames = { "Bip01 L Clavicle", "LeftShoulder" };

        PhoneUserArmPose source;
        Animator animator;

        public bool Bind(PhoneUserArmPose settings, Animator targetAnimator)
        {
            source = settings;
            animator = targetAnimator;
            return animator != null;
        }

        public void Unbind()
        {
            source = null;
            animator = null;
        }

        void OnAnimatorIK(int layerIndex)
        {
            if (source == null || animator == null)
                return;

            source.ApplyElbowHints(animator);
        }

        void LateUpdate()
        {
            if (source == null || animator == null)
                return;

            bool rightArm = ResolveBone(animator, HumanBodyBones.RightUpperArm) != null;
            bool leftArm = ResolveBone(animator, HumanBodyBones.LeftUpperArm) != null;
            int applied = source.ApplyBoneOffsets(animator);
            source.ReportDebug(applied, rightArm, leftArm);
        }

        internal static Transform ResolveBone(Animator animator, HumanBodyBones bone)
        {
            Transform t = animator.GetBoneTransform(bone);
            if (t != null)
                return t;

            string[] names = NamesForBone(bone);
            if (names == null)
                return null;

            return FindDeepChild(animator.transform, names);
        }

        static string[] NamesForBone(HumanBodyBones bone)
        {
            switch (bone)
            {
                case HumanBodyBones.RightUpperArm: return RightUpperArmNames;
                case HumanBodyBones.LeftUpperArm: return LeftUpperArmNames;
                case HumanBodyBones.RightLowerArm: return RightLowerArmNames;
                case HumanBodyBones.LeftLowerArm: return LeftLowerArmNames;
                case HumanBodyBones.RightShoulder: return RightShoulderNames;
                case HumanBodyBones.LeftShoulder: return LeftShoulderNames;
                default: return null;
            }
        }

        static Transform FindDeepChild(Transform root, string[] names)
        {
            foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
            {
                for (int i = 0; i < names.Length; i++)
                {
                    if (child.name == names[i])
                        return child;
                }
            }

            return null;
        }
    }
}
