#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace SessionReview.Editor
{
    /// <summary>
    /// One-shot builder for the Phone_User texting pose. Runtime pose scripting failed
    /// twice (bone-euler offsets are rig-dependent; HumanPoseHandler.SetHumanPose never
    /// reaches a skeleton with a bound Animator — verified by the runtime probe), so the
    /// pose is baked as an ANIMATION instead: a one-frame humanoid clip carrying muscle
    /// curves for the arms only, played on an arms-masked override layer above a plain
    /// HumanoidIdle base layer. Muscle curves are rig-independent, animator-native, show
    /// up from frame 0 (so a prop spawned while Time.timeScale is 0 still poses), and the
    /// mask keeps the layer away from the face bones entirely.
    ///
    /// Auto-runs after a domain reload while the controller asset is missing.
    /// Menu: SessionReview → Rebuild Phone User Pose (force).
    /// </summary>
    [InitializeOnLoad]
    public static class PhoneUserPoseBaker
    {
        const string Folder = "Assets/Resources/Prefabs/Community-informed Model/Phone User";
        const string ClipPath = Folder + "/PhoneTextingPose.anim";
        const string MaskPath = Folder + "/PhoneArmsOnlyMask.mask";
        const string ControllerPath = Folder + "/PhoneUserPoseController.controller";
        const string PrefabPath = "Assets/Resources/PlayerCharacters/Phone_User.prefab";
        const string IdleFbxPath = "Assets/ExternalAssets/StandardAssets/Characters/ThirdPersonCharacter/Animation/HumanoidIdle.fbx";

        static PhoneUserPoseBaker()
        {
            EditorApplication.delayCall += RunIfMissing;
        }

        static void RunIfMissing()
        {
            if (ControllerLooksHealthy())
                return;
            Run();
        }

        [MenuItem("SessionReview/Rebuild Phone User Pose (force)")]
        public static void Run()
        {
            try
            {
                Build();
            }
            catch (System.Exception ex)
            {
                Debug.LogError("[PhoneUserPoseBaker] Build failed: " + ex);
            }
        }

        static void Build()
        {
            AnimationClip idleClip = LoadIdleClip();
            if (idleClip == null)
            {
                Debug.LogError("[PhoneUserPoseBaker] HumanoidIdle clip not found at " + IdleFbxPath + "; aborting.");
                return;
            }

            // --- 1-frame texting pose, humanoid muscle space (-1..1). Convention:
            // "Arm Down-Up" -1 = arm at the side; "Forearm Stretch" -1 = elbow fully
            // bent; "Forearm Twist In-Out" -1 = forearm rolled inward (palm to body).
            var clip = new AnimationClip { name = "PhoneTextingPose" };
            foreach (string side in new[] { "Left", "Right" })
            {
                SetMuscle(clip, side + " Shoulder Down-Up", 0f);
                SetMuscle(clip, side + " Shoulder Front-Back", -0.1f);
                SetMuscle(clip, side + " Arm Down-Up", -0.55f);
                SetMuscle(clip, side + " Arm Front-Back", -0.35f);
                SetMuscle(clip, side + " Arm Twist In-Out", 0f);
                SetMuscle(clip, side + " Forearm Stretch", -0.7f);
                SetMuscle(clip, side + " Forearm Twist In-Out", -0.3f);
                SetMuscle(clip, side + " Hand Down-Up", -0.15f);
                SetMuscle(clip, side + " Hand In-Out", 0f);
            }
            ReplaceAsset(clip, ClipPath);

            // --- Arms-only mask. Fingers, head and everything else stay with the base
            // layer / bind pose — the face bones must never be touched by this layer.
            var mask = new AvatarMask { name = "PhoneArmsOnlyMask" };
            for (var part = AvatarMaskBodyPart.Root; part < AvatarMaskBodyPart.LastBodyPart; part++)
            {
                mask.SetHumanoidBodyPartActive(part,
                    part == AvatarMaskBodyPart.LeftArm || part == AvatarMaskBodyPart.RightArm);
            }
            ReplaceAsset(mask, MaskPath);

            // --- Controller: base = looping HumanoidIdle, layer 1 = masked pose.
            if (File.Exists(ControllerPath))
                AssetDatabase.DeleteAsset(ControllerPath);
            AnimatorController controller = AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);
            AnimatorControllerLayer[] layers = controller.layers;
            AnimatorState idleState = layers[0].stateMachine.AddState("HumanoidIdle");
            idleState.motion = idleClip;
            layers[0].stateMachine.defaultState = idleState;

            controller.AddLayer("PhonePose");
            layers = controller.layers;
            layers[1].avatarMask = mask;
            layers[1].defaultWeight = 1f;
            controller.layers = layers;
            layers = controller.layers;
            AnimatorState poseState = layers[1].stateMachine.AddState("PhoneTextingPose");
            poseState.motion = clip;
            layers[1].stateMachine.defaultState = poseState;

            AssetDatabase.SaveAssets();

            // --- Point the prefab's nested rig animator at the new controller.
            GameObject prefabRoot = PrefabUtility.LoadPrefabContents(PrefabPath);
            try
            {
                Animator animator = prefabRoot.GetComponentInChildren<Animator>(true);
                if (animator == null)
                {
                    Debug.LogError("[PhoneUserPoseBaker] No Animator found inside " + PrefabPath);
                    return;
                }

                animator.runtimeAnimatorController = controller;
                PrefabUtility.SaveAsPrefabAsset(prefabRoot, PrefabPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(prefabRoot);
            }

            Debug.Log("[PhoneUserPoseBaker] Built " + ClipPath + ", " + MaskPath + ", " + ControllerPath
                + " and rewired " + PrefabPath + " to PhoneUserPoseController.");
        }

        static AnimationClip LoadIdleClip()
        {
            foreach (Object o in AssetDatabase.LoadAllAssetsAtPath(IdleFbxPath))
            {
                if (o is AnimationClip clip && !clip.name.StartsWith("__preview"))
                    return clip;
            }

            return null;
        }

        static bool ControllerLooksHealthy()
        {
            AnimatorController controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
            if (controller == null || controller.layers == null || controller.layers.Length < 2)
                return false;
            if (controller.layers[0].stateMachine == null ||
                controller.layers[0].stateMachine.states == null ||
                controller.layers[0].stateMachine.states.Length == 0)
                return false;
            if (controller.layers[1].stateMachine == null ||
                controller.layers[1].stateMachine.states == null ||
                controller.layers[1].stateMachine.states.Length == 0)
                return false;
            if (controller.layers[1].avatarMask == null || controller.layers[1].defaultWeight < 0.99f)
                return false;
            if (AssetDatabase.LoadAssetAtPath<AnimationClip>(ClipPath) == null)
                return false;
            if (AssetDatabase.LoadAssetAtPath<AvatarMask>(MaskPath) == null)
                return false;
            return true;
        }

        static void SetMuscle(AnimationClip clip, string muscle, float value)
        {
            clip.SetCurve(string.Empty, typeof(Animator), muscle, AnimationCurve.Constant(0f, 1f, value));
        }

        static void ReplaceAsset(Object asset, string path)
        {
            if (File.Exists(path))
                AssetDatabase.DeleteAsset(path);
            AssetDatabase.CreateAsset(asset, path);
        }
    }
}
#endif
