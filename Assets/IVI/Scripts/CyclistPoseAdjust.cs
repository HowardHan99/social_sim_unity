using System.Collections;
using UnityEngine;

namespace IVI
{
    /// <summary>
    /// Slows cycling animation and overrides pose after the Animator finishes each frame.
    /// Arms are locked in world space (handlebar pose). Legs keep pedaling but spread wider.
    /// </summary>
    public class CyclistPoseAdjust : MonoBehaviour
    {
        [Header("Animation")]
        [Range(0.1f, 1f)] public float animationSpeed = 0.45f;

        [Header("Arms — frozen forward (degrees, relative to character facing)")]
        public Vector3 rightShoulderEuler = new Vector3(0f, 0f, 8f);
        public Vector3 leftShoulderEuler = new Vector3(0f, 0f, -8f);
        public Vector3 rightUpperArmEuler = new Vector3(55f, 12f, -22f);
        public Vector3 leftUpperArmEuler = new Vector3(55f, -12f, 22f);
        public Vector3 rightLowerArmEuler = new Vector3(32f, 0f, 0f);
        public Vector3 leftLowerArmEuler = new Vector3(32f, 0f, 0f);

        [Header("Legs — spread on top of pedal animation")]
        [Range(0f, 45f)] public float legSpread = 28f;

        [Header("Debug (read-only)")]
        public bool debugReady;
        public string debugAnimatorTarget;
        public int debugBonesAppliedLastFrame;

        CyclistPoseAdjustDriver driver;

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

            driver = animator.GetComponent<CyclistPoseAdjustDriver>();
            if (driver == null)
                driver = animator.gameObject.AddComponent<CyclistPoseAdjustDriver>();

            debugReady = driver.Bind(this, animator);
            return debugReady;
        }

        internal void ApplyPose(Animator animator)
        {
            animator.speed = animationSpeed;

            int applied = 0;
            Quaternion bodyRotation = animator.transform.rotation;

            applied += FreezeArmWorld(animator, HumanBodyBones.RightShoulder, bodyRotation, rightShoulderEuler);
            applied += FreezeArmWorld(animator, HumanBodyBones.LeftShoulder, bodyRotation, leftShoulderEuler);
            applied += FreezeArmWorld(animator, HumanBodyBones.RightUpperArm, bodyRotation, rightUpperArmEuler);
            applied += FreezeArmWorld(animator, HumanBodyBones.LeftUpperArm, bodyRotation, leftUpperArmEuler);
            applied += FreezeArmWorld(animator, HumanBodyBones.RightLowerArm, bodyRotation, rightLowerArmEuler);
            applied += FreezeArmWorld(animator, HumanBodyBones.LeftLowerArm, bodyRotation, leftLowerArmEuler);

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
    }

    /// <summary>
    /// Must live on the Animator GameObject. Applies pose after the Animator each frame.
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
                case HumanBodyBones.RightUpperLeg: return "Bip01 R Thigh";
                case HumanBodyBones.LeftUpperLeg: return "Bip01 L Thigh";
                default: return null;
            }
        }
    }
}
