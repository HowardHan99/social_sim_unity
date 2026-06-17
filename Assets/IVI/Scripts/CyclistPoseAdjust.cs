using System.Collections;
using UnityEngine;

namespace IVI
{
    /// <summary>
    /// Cyclist arm pose on top of animation.
    /// Hand position IK locks grip points; forearm body-facing angles position the elbows.
    /// Wrist rotation is left alone (no hand rotation IK).
    /// </summary>
    public class CyclistPoseAdjust : MonoBehaviour
    {
        [Header("Animation")]
        [Range(0.1f, 1f)] public float idleAnimationSpeed = 0.45f;

        [Header("Arms — upper body (degrees from character facing)")]
        public Vector3 rightShoulderEuler = new Vector3(0f, 0f, 8f);
        public Vector3 leftShoulderEuler = new Vector3(0f, 0f, -8f);
        public Vector3 rightUpperArmEuler = new Vector3(42f, 14f, -62f);
        public Vector3 leftUpperArmEuler = new Vector3(42f, -14f, 62f);

        [Header("Arms — biking shoulders only")]
        public Vector3 bikingRightShoulderEuler = new Vector3(0f, 0f, 8f);
        public Vector3 bikingLeftShoulderEuler = new Vector3(0f, 0f, -8f);
        public Vector3 bikingRightUpperArmEuler = new Vector3(42f, 14f, -62f);
        public Vector3 bikingLeftUpperArmEuler = new Vector3(42f, -14f, 62f);

        [Header("Handlebar hand IK (local to animator root)")]
        public Vector3 rightHandLocalPosition = new Vector3(0.26f, 0.10f, 0.40f);
        public Vector3 leftHandLocalPosition = new Vector3(-0.26f, 0.10f, 0.40f);

        [Range(0f, 1f)] public float handIkPositionWeight = 1f;
        [Range(0f, 1f)] public float handIkRotationWeight = 0f;

        [Header("Forearms — body-facing degrees (positions the elbow)")]
        [Tooltip("X = elbow fold depth. Y = elbow forward/back. Z = elbow flare out/in from torso.")]
        public Vector3 rightForearmEuler = new Vector3(108f, 5f, -35f);
        public Vector3 leftForearmEuler = new Vector3(108f, -5f, 35f);
        public Vector3 bikingRightForearmEuler = new Vector3(108f, 5f, -35f);
        public Vector3 bikingLeftForearmEuler = new Vector3(108f, -5f, 35f);
        [Tooltip("Apply forearm angles while idle.")]
        public bool bendForearmsWhenIdle = true;
        [Tooltip("Apply forearm angles while biking (off = animation drives elbow).")]
        public bool bendForearmsWhenBiking = false;

        [Header("Elbow IK hints (optional — leave weight at 0 when using forearm angles)")]
        [Tooltip("Meters to push each elbow away from the torso midline.")]
        public float elbowOutwardOffset = 0.38f;
        [Tooltip("Meters to push elbow hints slightly forward.")]
        public float elbowForwardOffset = 0.06f;
        [Tooltip("Meters to pull elbow hints back toward the shoulder.")]
        public float elbowBackwardOffset = 0.14f;
        [Range(0f, 1f)] public float elbowHintWeightIdle = 0f;
        [Range(0f, 1f)] public float elbowHintWeightBiking = 0f;

        [Header("Legs — spread on top of pedal animation")]
        [Range(0f, 45f)] public float legSpread = 28f;

        [Header("Debug (read-only)")]
        public bool debugReady;
        public bool debugIsBiking;
        public string debugAnimatorTarget;
        public int debugBonesAppliedLastFrame;

        CyclistPoseAdjustDriver driver;
        bool hasIsBikingParameter;

        void OnEnable()
        {
            StartCoroutine(InitWhenReady());
        }

        void OnDisable()
        {
            if (driver != null)
                driver.Unbind();
        }

        IEnumerator InitWhenReady()
        {
            debugReady = false;

            for (int i = 0; i < 90; i++)
            {
                if (TryEnsureDriver())
                    break;

                yield return null;
            }

            if (!debugReady)
                Debug.LogWarning("[CyclistPoseAdjust] Humanoid Animator not ready.", this);
        }

        bool TryEnsureDriver()
        {
            Animator animator = AvatarAnimatorUtility.GetLocomotionAnimator(gameObject);
            debugAnimatorTarget = animator != null ? animator.gameObject.name : "(none)";

            if (animator == null || animator.avatar == null || !animator.avatar.isHuman)
                return false;

            if (!animator.isInitialized && animator.runtimeAnimatorController != null)
                animator.Rebind();

            hasIsBikingParameter = HasAnimatorParameter(animator, "IsBiking");

            driver = animator.GetComponent<CyclistPoseAdjustDriver>();
            if (driver == null)
                driver = animator.gameObject.AddComponent<CyclistPoseAdjustDriver>();

            debugReady = driver.Bind(this, animator);
            return debugReady;
        }

        internal bool QueryIsBiking(Animator animator)
        {
            if (hasIsBikingParameter)
                return animator.GetBool("IsBiking");

            ManualWheelchairController movement = GetComponent<ManualWheelchairController>();
            return movement != null && movement.debugIsBiking;
        }

        internal void ApplyArmIk(Animator animator, bool isBiking)
        {
            ApplyHandIk(animator);
            ApplyElbowHints(animator, isBiking ? elbowHintWeightBiking : elbowHintWeightIdle);
        }

        void ApplyHandIk(Animator animator)
        {
            ApplyHandIkGoal(animator, AvatarIKGoal.RightHand, rightHandLocalPosition, handIkPositionWeight, handIkRotationWeight);
            ApplyHandIkGoal(animator, AvatarIKGoal.LeftHand, leftHandLocalPosition, handIkPositionWeight, handIkRotationWeight);
        }

        static void ApplyHandIkGoal(
            Animator animator,
            AvatarIKGoal goal,
            Vector3 localPosition,
            float positionWeight,
            float rotationWeight)
        {
            animator.SetIKPositionWeight(goal, positionWeight);
            animator.SetIKRotationWeight(goal, rotationWeight);

            if (positionWeight <= 0f)
                return;

            Transform root = animator.transform;
            animator.SetIKPosition(goal, root.TransformPoint(localPosition));
        }

        void ApplyElbowHints(Animator animator, float weight)
        {
            animator.SetIKHintPositionWeight(AvatarIKHint.RightElbow, weight);
            animator.SetIKHintPositionWeight(AvatarIKHint.LeftElbow, weight);

            if (weight <= 0f)
                return;

            Transform root = animator.transform;
            Vector3 outward = root.right * elbowOutwardOffset;
            Vector3 forward = root.forward * elbowForwardOffset;
            Vector3 backward = -root.forward * elbowBackwardOffset;

            ApplyElbowHint(animator, AvatarIKHint.RightElbow, HumanBodyBones.RightLowerArm, outward + forward + backward);
            ApplyElbowHint(animator, AvatarIKHint.LeftElbow, HumanBodyBones.LeftLowerArm, -outward + forward + backward);
        }

        static void ApplyElbowHint(Animator animator, AvatarIKHint hint, HumanBodyBones lowerArmBone, Vector3 worldOffset)
        {
            Transform elbow = CyclistPoseAdjustDriver.ResolveBone(animator, lowerArmBone);
            if (elbow == null)
                return;

            animator.SetIKHintPosition(hint, elbow.position + worldOffset);
        }

        internal void ApplyPose(Animator animator)
        {
            bool isBiking = QueryIsBiking(animator);
            debugIsBiking = isBiking;

            if (!isBiking)
                animator.speed = idleAnimationSpeed;

            int applied = 0;
            Quaternion bodyRotation = animator.transform.rotation;

            if (isBiking)
            {
                applied += FreezeArmWorld(animator, HumanBodyBones.RightShoulder, bodyRotation, bikingRightShoulderEuler);
                applied += FreezeArmWorld(animator, HumanBodyBones.LeftShoulder, bodyRotation, bikingLeftShoulderEuler);
                applied += FreezeArmWorld(animator, HumanBodyBones.RightUpperArm, bodyRotation, bikingRightUpperArmEuler);
                applied += FreezeArmWorld(animator, HumanBodyBones.LeftUpperArm, bodyRotation, bikingLeftUpperArmEuler);
            }
            else
            {
                applied += FreezeArmWorld(animator, HumanBodyBones.RightShoulder, bodyRotation, rightShoulderEuler);
                applied += FreezeArmWorld(animator, HumanBodyBones.LeftShoulder, bodyRotation, leftShoulderEuler);
                applied += FreezeArmWorld(animator, HumanBodyBones.RightUpperArm, bodyRotation, rightUpperArmEuler);
                applied += FreezeArmWorld(animator, HumanBodyBones.LeftUpperArm, bodyRotation, leftUpperArmEuler);
            }

            bool bendForearms = isBiking ? bendForearmsWhenBiking : bendForearmsWhenIdle;
            if (bendForearms)
            {
                Vector3 rightForearm = isBiking ? bikingRightForearmEuler : rightForearmEuler;
                Vector3 leftForearm = isBiking ? bikingLeftForearmEuler : leftForearmEuler;
                applied += FreezeArmWorld(animator, HumanBodyBones.RightLowerArm, bodyRotation, rightForearm);
                applied += FreezeArmWorld(animator, HumanBodyBones.LeftLowerArm, bodyRotation, leftForearm);
            }

            applied += SpreadLeg(animator, HumanBodyBones.LeftUpperLeg, -legSpread);
            applied += SpreadLeg(animator, HumanBodyBones.RightUpperLeg, legSpread);

            debugBonesAppliedLastFrame = applied;
        }

        static int FreezeArmWorld(Animator animator, HumanBodyBones bone, Quaternion bodyRotation, Vector3 eulerFromBody)
        {
            Transform t = CyclistPoseAdjustDriver.ResolveBone(animator, bone);
            if (t == null)
                return 0;

            t.rotation = bodyRotation * Quaternion.Euler(eulerFromBody);
            return 1;
        }

        static int SpreadLeg(Animator animator, HumanBodyBones bone, float zSpread)
        {
            if (Mathf.Abs(zSpread) < 0.01f)
                return 0;

            Transform t = CyclistPoseAdjustDriver.ResolveBone(animator, bone);
            if (t == null)
                return 0;

            t.localRotation = t.localRotation * Quaternion.Euler(0f, 0f, zSpread);
            return 1;
        }

        static bool HasAnimatorParameter(Animator animator, string parameterName)
        {
            foreach (AnimatorControllerParameter parameter in animator.parameters)
            {
                if (parameter.name == parameterName)
                    return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Lives on the Animator. Hand + elbow IK during animation, upper-arm pose after animation.
    /// </summary>
    [DefaultExecutionOrder(32000)]
    sealed class CyclistPoseAdjustDriver : MonoBehaviour
    {
        CyclistPoseAdjust source;
        Animator animator;
        Coroutine poseLoop;

        public bool Bind(CyclistPoseAdjust settings, Animator targetAnimator)
        {
            source = settings;
            animator = targetAnimator;

            if (poseLoop != null)
                StopCoroutine(poseLoop);

            poseLoop = StartCoroutine(ApplyAfterAnimationLoop());
            return animator != null;
        }

        public void Unbind()
        {
            if (poseLoop != null)
            {
                StopCoroutine(poseLoop);
                poseLoop = null;
            }

            source = null;
            animator = null;
        }

        void OnDestroy()
        {
            Unbind();
        }

        void OnAnimatorIK(int layerIndex)
        {
            if (source == null || animator == null)
                return;

            source.ApplyArmIk(animator, source.QueryIsBiking(animator));
        }

        IEnumerator ApplyAfterAnimationLoop()
        {
            while (enabled && source != null && animator != null)
            {
                yield return new WaitForEndOfFrame();
                if (source == null || animator == null)
                    yield break;

                source.ApplyPose(animator);
            }
        }

        internal static Transform ResolveBone(Animator targetAnimator, HumanBodyBones bone)
        {
            Transform t = targetAnimator.GetBoneTransform(bone);
            if (t != null)
                return t;

            string boneName = BoneNameFor(bone);
            if (string.IsNullOrEmpty(boneName))
                return null;

            foreach (Transform child in targetAnimator.transform.GetComponentsInChildren<Transform>(true))
            {
                if (child.name == boneName)
                    return child;
            }

            return null;
        }

        static string BoneNameFor(HumanBodyBones bone)
        {
            switch (bone)
            {
                case HumanBodyBones.RightShoulder: return "Bip01 R Clavicle";
                case HumanBodyBones.LeftShoulder: return "Bip01 L Clavicle";
                case HumanBodyBones.RightUpperArm: return "Bip01 R UpperArm";
                case HumanBodyBones.LeftUpperArm: return "Bip01 L UpperArm";
                case HumanBodyBones.RightLowerArm: return "Bip01 R Forearm";
                case HumanBodyBones.LeftLowerArm: return "Bip01 L Forearm";
                case HumanBodyBones.RightHand: return "Bip01 R Hand";
                case HumanBodyBones.LeftHand: return "Bip01 L Hand";
                case HumanBodyBones.RightUpperLeg: return "Bip01 R Thigh";
                case HumanBodyBones.LeftUpperLeg: return "Bip01 L Thigh";
                default: return null;
            }
        }
    }
}
