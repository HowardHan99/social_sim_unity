#if UNITY_EDITOR
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace SessionReview.Editor
{
    /// <summary>
    /// Rebuilds Assets/Resources/Prefabs/Rocketbox/Wheelchair_Female 1.prefab from scratch,
    /// mirroring the male prefab's structure instead of patching the hand-assembled one.
    /// Menu: SessionReview → Rebuild Female Wheelchair Prefab.
    ///
    /// Why a rebuild: the female prefab was a patchwork -- the rider lived inside a nested
    /// prefab whose bones had been hand-scaled/posed (so a correctly-bound humanoid Animator
    /// writes meter-scale poses onto a rig whose skinned bind poses expect different units,
    /// and the mesh collapses out of view), the raw fbx was in there a second time, the
    /// Animator was bolted onto a nested instance root, and child positions were baked
    /// world coordinates. The male works because it is one importer-consistent hierarchy.
    ///
    /// Result structure (root keeps its components, guid, and root fileID, so scene
    /// references to the prefab stay valid):
    ///   Wheelchair_Female 1  (ManualWheelchairController, Rigidbody, Capsule, objectId)
    ///     rig                (the female fbx model instance; its importer Animator keeps
    ///                         its own humanoid Avatar; controller = the male's seated
    ///                         Wheelchair.controller -- the clip is Humanoid, so it
    ///                         retargets to the female skeleton)
    ///     model              (chair mesh, copied from the male with its local TRS)
    ///     wheelchairCamera   (copied from the male; the female never had one)
    ///
    /// The runtime path already supports the Animator living one level below the root
    /// (Base.Start / RandomAvatar.GetAvatarAnimator / AgentControlTuning.FindAnimator).
    /// On success this deletes the now-obsolete FemaleWheelchairPrefabFixer menu item.
    /// </summary>
    public static class FemaleWheelchairPrefabRebuilder
    {
        const string FemalePrefabPath = "Assets/Resources/Prefabs/Rocketbox/Wheelchair_Female 1.prefab";
        const string MalePrefabPath = "Assets/Resources/Prefabs/Rocketbox/Wheelchair_male_01.prefab";
        const string FemaleFbxPath = "Assets/Resources/Prefabs/Rocketbox/wheelchairuser-female/Wheelchair (1).fbx";
        const string SeatedControllerPath = "Assets/Resources/Prefabs/Rocketbox/wheelchair-male/Wheelchair.controller";
        const string ObsoleteFixerPath = "Assets/Scripts/SessionReview/Editor/FemaleWheelchairPrefabFixer.cs";

        [MenuItem("SessionReview/Rebuild Female Wheelchair Prefab")]
        public static void Rebuild()
        {
            var femaleFbx = AssetDatabase.LoadAssetAtPath<GameObject>(FemaleFbxPath);
            if (femaleFbx == null) { Report($"Female fbx not found at {FemaleFbxPath}. Nothing changed.", true); return; }

            Avatar femaleAvatar = AssetDatabase.LoadAllAssetsAtPath(FemaleFbxPath)
                .OfType<Avatar>().FirstOrDefault(a => a != null && a.isValid && a.isHuman);
            if (femaleAvatar == null)
            {
                Report($"No valid humanoid Avatar inside {FemaleFbxPath}. Set its Rig to "
                       + "Humanoid / Create From This Model, Apply, and run this again.", true);
                return;
            }

            var controller = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(SeatedControllerPath);
            if (controller == null) { Report($"Seated controller not found at {SeatedControllerPath}. Nothing changed.", true); return; }

            GameObject maleRoot = PrefabUtility.LoadPrefabContents(MalePrefabPath);
            GameObject femaleRoot = PrefabUtility.LoadPrefabContents(FemalePrefabPath);
            try
            {
                for (int i = femaleRoot.transform.childCount - 1; i >= 0; i--)
                    Object.DestroyImmediate(femaleRoot.transform.GetChild(i).gameObject);
                foreach (var stray in femaleRoot.GetComponents<Animator>())
                    Object.DestroyImmediate(stray);

                var rig = (GameObject)PrefabUtility.InstantiatePrefab(femaleFbx, femaleRoot.transform);
                rig.name = "rig";
                rig.transform.localPosition = Vector3.zero;
                rig.transform.localRotation = Quaternion.identity;

                Animator rigAnimator = rig.GetComponent<Animator>();
                if (rigAnimator == null)
                    rigAnimator = rig.AddComponent<Animator>();
                if (rigAnimator.avatar == null || !rigAnimator.avatar.isHuman)
                    rigAnimator.avatar = femaleAvatar;
                rigAnimator.runtimeAnimatorController = controller;
                rigAnimator.applyRootMotion = false;
                rigAnimator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

                if (!CopyChild(maleRoot, "model", femaleRoot)) { Report("Male prefab has no 'model' (chair) child. Nothing changed.", true); return; }
                if (!CopyChild(maleRoot, "wheelchairCamera", femaleRoot)) { Report("Male prefab has no 'wheelchairCamera' child. Nothing changed.", true); return; }

                var maleCapsule = maleRoot.GetComponent<CapsuleCollider>();
                var femaleCapsule = femaleRoot.GetComponent<CapsuleCollider>();
                if (femaleCapsule == null) femaleCapsule = femaleRoot.AddComponent<CapsuleCollider>();
                if (maleCapsule != null)
                {
                    femaleCapsule.radius = maleCapsule.radius;
                    femaleCapsule.height = maleCapsule.height;
                    femaleCapsule.direction = maleCapsule.direction;
                    femaleCapsule.center = maleCapsule.center;
                }

                PrefabUtility.SaveAsPrefabAsset(femaleRoot, FemalePrefabPath);
                AssetDatabase.SaveAssets();
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(maleRoot);
                PrefabUtility.UnloadPrefabContents(femaleRoot);
            }

            if (AssetDatabase.LoadAssetAtPath<Object>(ObsoleteFixerPath) != null)
                AssetDatabase.DeleteAsset(ObsoleteFixerPath);

            Report("Rebuilt from the male template: rig (female fbx, own humanoid avatar, "
                   + "male's seated controller) + chair + wheelchairCamera. The old patchwork "
                   + "children are gone; the obsolete Fix menu item was removed.", false);
        }

        /// <summary>Plain-GameObject copy (chair / camera are not prefab instances in the male).</summary>
        static bool CopyChild(GameObject srcRoot, string childName, GameObject dstRoot)
        {
            Transform src = srcRoot.transform.Find(childName);
            if (src == null)
                return false;

            GameObject copy = Object.Instantiate(src.gameObject, dstRoot.transform);
            copy.name = childName;
            copy.transform.localPosition = src.localPosition;
            copy.transform.localRotation = src.localRotation;
            copy.transform.localScale = src.localScale;
            return true;
        }

        static void Report(string message, bool isProblem)
        {
            if (isProblem) Debug.LogError("[RebuildFemaleWheelchair] " + message);
            else Debug.Log("[RebuildFemaleWheelchair] " + message);
            EditorUtility.DisplayDialog("Female Wheelchair Rebuild", message, "OK");
        }
    }
}
#endif
