using UnityEngine;

namespace IVI
{
    /// <summary>
    /// Adds a phone-looking pose on top of locomotion animation via HumanPose muscles.
    /// Works with Optimize Game Objects on Rocketbox Bip01 humanoid rigs.
    /// Tune muscle targets in Play mode on the Phone User prefab instance.
    /// </summary>
    [DefaultExecutionOrder(10000)]
    public class PhoneUserPose : MonoBehaviour
    {
        [Header("Look down (muscle target, roughly -1..1)")]
        [Range(-1f, 1f)] public float spineFrontBack = 1f;
        [Range(-1f, 1f)] public float chestFrontBack = 1f;
        [Range(-1f, 1f)] public float upperChestFrontBack = 1f;
        [Range(-1f, 1f)] public float neckNodDown = 1f;
        [Range(-1f, 1f)] public float headNodDown = 1f;

        [Header("Arms inward / phone hold (muscle target)")]
        [Range(-1f, 1f)] public float leftArmFrontBack = 1f;
        [Range(-1f, 1f)] public float leftArmDownUp = 1f;
        [Range(-1f, 1f)] public float leftArmInOut = 1f;
        [Range(-1f, 1f)] public float leftForearmStretch = 1f;
        [Range(-1f, 1f)] public float rightArmFrontBack = 1f;
        [Range(-1f, 1f)] public float rightArmDownUp = -1f;
        [Range(-1f, 1f)] public float rightArmInOut = -1f;
        [Range(-1f, 1f)] public float rightForearmStretch = 1f;

        [Header("Optional")]
        [Range(0f, 1f)] public float poseWeight = 1f;
        [Min(1f)] public float poseStrength = 5f;

        Animator animator;
        HumanPoseHandler poseHandler;
        HumanPose humanPose;

        void Awake()
        {
            animator = AvatarAnimatorUtility.GetLocomotionAnimator(gameObject);
        }

        void OnEnable()
        {
            TryCreateHandler();
        }

        void OnDisable()
        {
            poseHandler?.Dispose();
            poseHandler = null;
        }

        void LateUpdate()
        {
            if (poseWeight <= 0f)
                return;

            if (animator == null)
                animator = AvatarAnimatorUtility.GetLocomotionAnimator(gameObject);

            if (animator == null)
                return;

            if (poseHandler == null)
                TryCreateHandler();

            if (poseHandler == null)
                return;

            poseHandler.GetHumanPose(ref humanPose);

            BlendMuscle(HumanBodyBones.Spine, 0, spineFrontBack);
            BlendMuscle(HumanBodyBones.Chest, 0, chestFrontBack);
            BlendMuscle(HumanBodyBones.UpperChest, 0, upperChestFrontBack);
            BlendMuscle(HumanBodyBones.Neck, 0, neckNodDown);
            BlendMuscle(HumanBodyBones.Head, 0, headNodDown);

            BlendMuscle(HumanBodyBones.LeftUpperArm, 1, leftArmFrontBack);
            BlendMuscle(HumanBodyBones.LeftUpperArm, 0, leftArmDownUp);
            BlendMuscle(HumanBodyBones.LeftUpperArm, 2, leftArmInOut);
            BlendMuscle(HumanBodyBones.LeftLowerArm, 0, leftForearmStretch);
            BlendMuscle(HumanBodyBones.RightUpperArm, 1, rightArmFrontBack);
            BlendMuscle(HumanBodyBones.RightUpperArm, 0, rightArmDownUp);
            BlendMuscle(HumanBodyBones.RightUpperArm, 2, rightArmInOut);
            BlendMuscle(HumanBodyBones.RightLowerArm, 0, rightForearmStretch);

            poseHandler.SetHumanPose(ref humanPose);
        }

        void BlendMuscle(HumanBodyBones bone, int dof, float target)
        {
            if (Mathf.Approximately(target, 0f) || poseWeight <= 0f)
                return;

            int muscleIndex = HumanTrait.MuscleFromBone((int)bone, dof);
            if (muscleIndex < 0 || muscleIndex >= humanPose.muscles.Length)
                return;

            target = Mathf.Clamp(target, -1f, 1f);
            float current = humanPose.muscles[muscleIndex];
            float delta = (target - current) * poseWeight * poseStrength;
            humanPose.muscles[muscleIndex] = Mathf.Clamp(current + delta, -1f, 1f);
        }

        void TryCreateHandler()
        {
            if (animator == null)
                animator = AvatarAnimatorUtility.GetLocomotionAnimator(gameObject);

            if (animator == null)
                return;

            if (!animator.isInitialized && animator.runtimeAnimatorController != null)
                animator.Rebind();

            Avatar avatar = animator.avatar;
            if (avatar == null || !avatar.isValid || !avatar.isHuman)
                return;

            poseHandler?.Dispose();
            poseHandler = new HumanPoseHandler(avatar, animator.transform);
        }
    }
}
