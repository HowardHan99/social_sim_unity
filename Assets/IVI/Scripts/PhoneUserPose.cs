using UnityEngine;

namespace IVI
{
    /// <summary>
    /// Adds a phone-looking pose on top of locomotion animation via HumanPose muscles.
    /// Works with Optimize Game Objects on Rocketbox Bip01 humanoid rigs.
    /// Tune muscle offsets in Play mode on the Phone User prefab instance.
    /// </summary>
    [DefaultExecutionOrder(10000)]
    public class PhoneUserPose : MonoBehaviour
    {
        [Header("Look down (muscle delta, roughly -1..1)")]
        [Range(-1f, 1f)] public float spineFrontBack = 0.18f;
        [Range(-1f, 1f)] public float chestFrontBack = 0.22f;
        [Range(-1f, 1f)] public float upperChestFrontBack = 0.25f;
        [Range(-1f, 1f)] public float neckNodDown = 0.35f;
        [Range(-1f, 1f)] public float headNodDown = 0.55f;

        [Header("Arms inward / phone hold (muscle delta)")]
        [Range(-1f, 1f)] public float leftArmFrontBack = -0.15f;
        [Range(-1f, 1f)] public float leftArmDownUp = -0.1f;
        [Range(-1f, 1f)] public float leftForearmStretch = 0.25f;
        [Range(-1f, 1f)] public float rightArmFrontBack = 0.35f;
        [Range(-1f, 1f)] public float rightArmDownUp = -0.25f;
        [Range(-1f, 1f)] public float rightForearmStretch = 0.45f;

        [Header("Optional")]
        [Range(0f, 1f)] public float poseWeight = 1f;

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
            debugHandlerReady = false;
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

            int applied = 0;
            applied += ApplyMuscle(HumanBodyBones.Spine, 0, spineFrontBack);
            applied += ApplyMuscle(HumanBodyBones.Chest, 0, chestFrontBack);
            applied += ApplyMuscle(HumanBodyBones.UpperChest, 0, upperChestFrontBack);
            applied += ApplyMuscle(HumanBodyBones.Neck, 0, neckNodDown);
            applied += ApplyMuscle(HumanBodyBones.Head, 0, headNodDown);

            applied += ApplyMuscle(HumanBodyBones.LeftUpperArm, 1, leftArmFrontBack);
            applied += ApplyMuscle(HumanBodyBones.LeftUpperArm, 0, leftArmDownUp);
            applied += ApplyMuscle(HumanBodyBones.LeftLowerArm, 0, leftForearmStretch);
            applied += ApplyMuscle(HumanBodyBones.RightUpperArm, 1, rightArmFrontBack);
            applied += ApplyMuscle(HumanBodyBones.RightUpperArm, 0, rightArmDownUp);
            applied += ApplyMuscle(HumanBodyBones.RightLowerArm, 0, rightForearmStretch);

            debugMusclesApplied = applied;
            poseHandler.SetHumanPose(ref humanPose);
        }

        int ApplyMuscle(HumanBodyBones bone, int dof, float delta)
        {
            if (Mathf.Approximately(delta, 0f) || poseWeight <= 0f)
                return 0;

            int muscleIndex = HumanTrait.MuscleFromBone((int)bone, dof);
            if (muscleIndex < 0 || muscleIndex >= humanPose.muscles.Length)
                return 0;

            humanPose.muscles[muscleIndex] = Mathf.Clamp(
                humanPose.muscles[muscleIndex] + delta * poseWeight,
                -1f,
                1f);
            return 1;
        }

        void TryCreateHandler()
        {
            if (animator == null)
                animator = AvatarAnimatorUtility.GetLocomotionAnimator(gameObject);

            debugAnimatorTarget = animator != null ? animator.gameObject.name : "(none)";

            if (animator == null)
            {
                debugHandlerReady = false;
                return;
            }

            if (!animator.isInitialized && animator.runtimeAnimatorController != null)
                animator.Rebind();

            Avatar avatar = animator.avatar;
            if (avatar == null || !avatar.isValid || !avatar.isHuman)
            {
                debugHandlerReady = false;
                return;
            }

            poseHandler?.Dispose();
            poseHandler = new HumanPoseHandler(avatar, animator.transform);
            debugHandlerReady = poseHandler != null;
        }
    }
}
