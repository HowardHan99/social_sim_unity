#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace SessionReview.Editor
{
    /// <summary>
    /// Repairs Assets/Resources/Prefabs/Rocketbox/Wheelchair_Female 1.prefab so it behaves
    /// like the male one.
    /// Menu: SessionReview → Fix Female Wheelchair Prefab (Unity's top menu bar; an Editor
    /// menu, so it is there regardless of which scene is open).
    ///
    /// The prefab loads and spawns correctly -- the Editor log shows "Spawned PWDPlayer at
    /// (-37.0,-0.4,15.0)". Three separate things are wrong with the asset itself, found by
    /// diffing it against Wheelchair_male.prefab, which works:
    ///
    ///  1. NO ANIMATOR.  Male has one on its root; the female's exists in the YAML but Unity
    ///     does not load it (RandomAvatar logs "has no Animator" after Instantiate). Without
    ///     one the rig is stuck in its BIND POSE -- standing, legs straight down -- so the
    ///     figure does not fit the seated wheelchair and reads as a broken model. Fixing this
    ///     means giving the rig the avatar generated from ITS OWN fbx. An earlier attempt
    ///     assigned the male prefab's avatar and the character vanished: the male rig's bones
    ///     are mixamorig9:*, the female's are mixamorig:*, so that Avatar cannot bind and
    ///     retargeting throws the bones off-screen. The runtime controller does not matter --
    ///     RandomAvatar.SpawnPwdPlayer overwrites runtimeAnimatorController with
    ///     pwdAnimationController -- only the avatar has to be right.
    ///
    ///  2. THE RIDER IS IN THERE TWICE.  Once as an instance of Wheelchair (1).prefab (rider
    ///     WITH the chair parented under it) and once as the raw Wheelchair (1).fbx (rider
    ///     only). Two skinned copies 3 cm apart z-fight. The chair-bearing one is kept --
    ///     disabling that one would take the chair with it.
    ///
    ///  3. BAKED WORLD POSITIONS.  Children hold ~(-27.8, 0.7, 1.3) as their LOCAL offset
    ///     (the root CapsuleCollider centre is the same world coordinate -- the giveaway),
    ///     versus the male's children which all sit within a metre of the root. The agent
    ///     root reaches the spawn point but the geometry renders 28 m away.
    ///
    /// Ground contact is measured from the CHAIR (a static MeshRenderer), not from the
    /// skinned meshes: skinned bounds in the prefab-contents scene reflect the bind pose, and
    /// the rider's runtime pose comes from the animator, so the chair is the only reliable
    /// anchor for "this is where the floor is".
    ///
    /// REVERSIBLE. Nothing is deleted -- the duplicate rider is deactivated, so if the wrong
    /// copy was picked you can re-enable it in the Inspector and deactivate the other. The
    /// children keep their positions RELATIVE to each other (one shared delta).
    ///
    /// An Editor menu item rather than a hand-edit of the YAML because an open Editor
    /// re-serializes the asset from its in-memory copy and silently reverts text edits.
    ///
    /// Idempotent: re-running reports only what still needed changing.
    /// </summary>
    public static class FemaleWheelchairPrefabFixer
    {
        const string PrefabPath = "Assets/Resources/Prefabs/Rocketbox/Wheelchair_Female 1.prefab";
        const string FemaleFbxPath = "Assets/Resources/Prefabs/Rocketbox/wheelchairuser-female/Wheelchair (1).fbx";

        // A child further than this from the root is a baked world coordinate, not a
        // deliberate offset (a rider sits well under a metre from its own root).
        const float SuspiciousOffset = 3f;

        [MenuItem("SessionReview/Fix Female Wheelchair Prefab")]
        public static void FixFromMenu()
        {
            string path = ResolvePrefabPath();
            if (path == null)
            {
                Report($"Prefab not found at {PrefabPath} (and no rename of it found).", true);
                return;
            }

            GameObject root = PrefabUtility.LoadPrefabContents(path);
            var log = new List<string>();

            try
            {
                Transform rider = ResolveRider(root, log);
                if (rider == null)
                {
                    Report("No child with a SkinnedMeshRenderer -- this is not the wheelchair "
                           + "prefab. Nothing changed.", true);
                    return;
                }

                string animatorError = FixAnimator(rider, log);
                if (animatorError != null)
                {
                    Report(animatorError, true);
                    return;
                }

                RecenterChildren(root, log);
                FixCollider(root, log);

                if (log.Count == 0)
                {
                    Report("Everything already looks right -- nothing to do.", false);
                    return;
                }

                PrefabUtility.SaveAsPrefabAsset(root, path);
                AssetDatabase.SaveAssets();
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }

            Report("Fixed:\n  - " + string.Join("\n  - ", log)
                   + "\n\nNothing was deleted. The duplicate rider is only DEACTIVATED -- "
                   + "re-enable it in the Inspector if the wrong copy was picked.", false);
        }

        static void Report(string message, bool isProblem)
        {
            if (isProblem) Debug.LogError("[FixFemaleWheelchair] " + message);
            else Debug.Log("[FixFemaleWheelchair] " + message);
            EditorUtility.DisplayDialog("Female Wheelchair Prefab", message, "OK");
        }

        /// <summary>
        /// Picks the rider that carries the chair (has MeshRenderers under it) and
        /// deactivates any other skinned duplicate.
        /// </summary>
        static Transform ResolveRider(GameObject root, List<string> log)
        {
            var candidates = new List<Transform>();
            foreach (Transform child in root.transform)
            {
                if (child.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length > 0)
                    candidates.Add(child);
            }
            if (candidates.Count == 0)
                return null;

            // The chair is a plain mesh, the rider is skinned: the child with both is the
            // complete wheelchair user; a skinned-only sibling is the stray duplicate.
            Transform kept = candidates
                .OrderByDescending(c => c.GetComponentsInChildren<MeshRenderer>(true).Length)
                .First();

            foreach (Transform c in candidates)
            {
                if (c == kept || !c.gameObject.activeSelf) continue;
                c.gameObject.SetActive(false);
                log.Add($"deactivated duplicate rider '{c.name}' (no chair under it; "
                        + "it was z-fighting with the kept copy)");
            }
            return kept;
        }

        /// <summary>
        /// Gives the rig an Animator carrying the avatar generated from its OWN fbx.
        /// Returns an error string if that avatar cannot be found, rather than assigning
        /// something that will not bind.
        /// </summary>
        static string FixAnimator(Transform rider, List<string> log)
        {
            Avatar female = AssetDatabase.LoadAllAssetsAtPath(FemaleFbxPath)
                .OfType<Avatar>()
                .FirstOrDefault(a => a != null && a.isValid && a.isHuman);

            if (female == null)
            {
                return $"Could not find a valid humanoid Avatar in {FemaleFbxPath}.\n\n"
                       + "Select that fbx, set Rig > Animation Type = Humanoid, "
                       + "Avatar Definition = Create From This Model, press Apply, then "
                       + "run this again. Nothing was changed.";
            }

            Animator animator = rider.GetComponent<Animator>();
            if (animator == null)
            {
                animator = rider.gameObject.AddComponent<Animator>();
                log.Add($"added an Animator to '{rider.name}' (it had none -- this is why the "
                        + "figure was stuck standing in its bind pose)");
            }

            if (animator.avatar != female)
            {
                log.Add($"avatar '{(animator.avatar != null ? animator.avatar.name : "none")}' -> "
                        + $"'{female.name}' (the female rig's own; the previous one was built "
                        + "for a differently-named skeleton and could not bind)");
                animator.avatar = female;
            }

            // Root motion would walk the rig off the physics-driven agent root.
            animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            return null;
        }

        /// <summary>
        /// Shifts EVERY child by one shared delta so the CHAIR's footprint lands on the root:
        /// horizontally centred, wheels on the root's ground plane. One delta keeps the
        /// children's relative positions intact.
        /// </summary>
        static void RecenterChildren(GameObject root, List<string> log)
        {
            Bounds? anchor = GroundAnchor(root.transform);
            if (anchor == null)
                return;

            Vector3 rootPos = root.transform.position;
            Bounds b = anchor.Value;
            Vector3 delta = new Vector3(b.center.x - rootPos.x, b.min.y - rootPos.y, b.center.z - rootPos.z);
            if (delta.magnitude < SuspiciousOffset)
                return;

            foreach (Transform child in root.transform)
            {
                Vector3 before = child.localPosition;
                child.localPosition -= delta;
                log.Add($"moved '{child.name}' ({before.x:F2},{before.y:F2},{before.z:F2}) -> "
                        + $"({child.localPosition.x:F2},{child.localPosition.y:F2},{child.localPosition.z:F2})");
            }
            log.Add($"(one shared shift of {delta.magnitude:F1} m, anchored on the chair's "
                    + "wheels -- the children's positions relative to each other are unchanged)");
        }

        /// <summary>Baked world coordinate -> local. Runtime recomputes this in
        /// Agents.Base.Start(); it matters for World Building placement and the editor view.</summary>
        static void FixCollider(GameObject root, List<string> log)
        {
            var capsule = root.GetComponent<CapsuleCollider>();
            if (capsule == null || capsule.center.magnitude < SuspiciousOffset)
                return;

            Bounds? anchor = GroundAnchor(root.transform);
            if (anchor == null)
                return;

            Vector3 center = new Vector3(0f, anchor.Value.size.y * 0.5f, 0f);
            log.Add($"capsule centre ({capsule.center.x:F2},{capsule.center.y:F2},{capsule.center.z:F2})"
                    + $" -> ({center.x:F2},{center.y:F2},{center.z:F2}) (was a world coordinate)");
            capsule.center = center;
        }

        /// <summary>
        /// The chair: a static MeshRenderer whose bounds are trustworthy. Skinned bounds here
        /// are bind-pose bounds and the rider's real pose only exists at runtime, so the chair
        /// is the reliable "where is the floor" anchor. Falls back to every renderer.
        /// </summary>
        static Bounds? GroundAnchor(Transform t)
        {
            var meshes = t.GetComponentsInChildren<MeshRenderer>(true);
            if (meshes.Length > 0)
                return Combine(meshes);

            var all = t.GetComponentsInChildren<Renderer>(true);
            return all.Length > 0 ? Combine(all) : (Bounds?)null;
        }

        static Bounds Combine(IReadOnlyList<Renderer> renderers)
        {
            Bounds b = renderers[0].bounds;
            for (int i = 1; i < renderers.Count; i++)
                b.Encapsulate(renderers[i].bounds);
            return b;
        }

        /// <summary>
        /// The prefab has been renamed at least once (Wheelchair_female -> Wheelchair_Female),
        /// so fall back to a name search rather than failing on a casing change.
        /// </summary>
        static string ResolvePrefabPath()
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath) != null)
                return PrefabPath;

            foreach (string guid in AssetDatabase.FindAssets("Wheelchair_Female t:Prefab"))
            {
                string p = AssetDatabase.GUIDToAssetPath(guid);
                if (p.Contains("/Rocketbox/"))
                    return p;
            }
            return null;
        }
    }
}
#endif
