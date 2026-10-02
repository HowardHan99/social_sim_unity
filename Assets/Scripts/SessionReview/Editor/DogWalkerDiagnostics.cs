#if UNITY_EDITOR
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace SessionReview.Editor
{
    /// <summary>
    /// Temporary diagnostics for the invisible Dog_Walker spawn: dumps the resolved
    /// hierarchy, renderers, meshes and materials of the prefab and its source fbx
    /// assets to dogwalker_diag.txt in the project root after every domain reload.
    /// Delete this file once the Dog Walker renders correctly.
    /// Menu: SessionReview → Dump Dog Walker Diagnostics
    /// </summary>
    [InitializeOnLoad]
    public static class DogWalkerDiagnostics
    {
        const string OutputFile = "dogwalker_diag.txt";

        static readonly string[] AssetPaths =
        {
            "Assets/Resources/PlayerCharacters/Dog_Walker.prefab",
            "Assets/Resources/Prefabs/Community-informed Model/Dog Walker/Ch22_nonPBR@Holding Walk.fbx",
            "Assets/Resources/Prefabs/Community-informed Model/Dog Walker/cur.fbx",
            "Assets/Resources/PlayerCharacters/Phone_User.prefab",
            "Assets/Resources/PlayerCharacters/Walker_User.prefab",
            "Assets/Resources/Prefabs/Community-informed Model/Phone User/Female_Adult_05 1.fbx",
            "Assets/Resources/Prefabs/Community-informed Model/Walker User/man.fbx",
        };

        const string ColliderAuditFile = "character_collider_audit.txt";
        const string CharacterFolder = "Assets/Resources/PlayerCharacters";

        // SEAN background agents run on Base.RADIUS = 0.2; anything much wider than this makes the
        // character an invisible blob the robot / wheelchair cannot pass.
        const float HumanRadiusReference = 0.35f;

        static DogWalkerDiagnostics()
        {
            EditorApplication.delayCall += Run;
            EditorApplication.delayCall += RunColliderAudit;
        }

        [MenuItem("SessionReview/Dump Dog Walker Diagnostics")]
        public static void Run()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== Dog Walker diagnostics, " + System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ===");
            sb.AppendLine("Unity " + Application.unityVersion + ", project " + Path.GetFileName(Directory.GetCurrentDirectory()));

            foreach (string path in AssetPaths)
            {
                try
                {
                    DumpAsset(sb, path);
                }
                catch (System.Exception ex)
                {
                    sb.AppendLine("EXCEPTION dumping " + path + ": " + ex);
                }
            }

            File.WriteAllText(OutputFile, sb.ToString());
            Debug.Log("[DogWalkerDiagnostics] Wrote " + Path.GetFullPath(OutputFile));
        }

        /// <summary>
        /// Audits the physics collider of every World Building character prefab against the space its
        /// meshes actually occupy: a spawned character keeps the prefab's ROOT collider (RuntimeEditor
        /// only adds one when there is none), so a capsule fitted to a T-posed arm span, or to a
        /// character plus its prop/animal, becomes an invisible blob that blocks the robot.
        /// Menu: SessionReview → Dump Character Collider Audit
        /// </summary>
        [MenuItem("SessionReview/Dump Character Collider Audit")]
        public static void RunColliderAudit()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== Character collider audit, " + System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ===");
            sb.AppendLine("Bounds are mesh bind-pose bounds expressed in ROOT-local space (Y=0 is the ground the");
            sb.AppendLine("spawn pipeline drops the prefab onto). Reference human radius: " + HumanRadiusReference + " m.");

            foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { CharacterFolder }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                try
                {
                    DumpColliderAudit(sb, path);
                }
                catch (System.Exception ex)
                {
                    sb.AppendLine("EXCEPTION auditing " + path + ": " + ex);
                }
            }

            File.WriteAllText(ColliderAuditFile, sb.ToString());
            Debug.Log("[DogWalkerDiagnostics] Wrote " + Path.GetFullPath(ColliderAuditFile));
        }

        static void DumpColliderAudit(StringBuilder sb, string path)
        {
            sb.AppendLine();
            sb.AppendLine("--- " + Path.GetFileNameWithoutExtension(path) + " ---");

            var root = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (root == null)
            {
                sb.AppendLine("  LoadAssetAtPath<GameObject> returned NULL");
                return;
            }

            bool hasBounds = TryComputeLocalBounds(root.transform, root.transform, out Bounds bounds);
            if (hasBounds)
                sb.AppendLine("  mesh bounds: center=" + V(bounds.center) + " size=" + V(bounds.size)
                    + "  y=[" + F(bounds.min.y) + ", " + F(bounds.max.y) + "]"
                    + "  x=[" + F(bounds.min.x) + ", " + F(bounds.max.x) + "]"
                    + "  z=[" + F(bounds.min.z) + ", " + F(bounds.max.z) + "]");
            else
                sb.AppendLine("  mesh bounds: NONE (no renderers with a shared mesh)");

            // Per-subtree so a companion object (Dog_Walker's dog, Cyclist's bike, Walker_User's
            // frame) is visible as its own blob rather than being averaged into the human.
            for (int i = 0; i < root.transform.childCount; i++)
            {
                Transform child = root.transform.GetChild(i);
                if (TryComputeLocalBounds(root.transform, child, out Bounds childBounds))
                    sb.AppendLine("    subtree '" + child.name + "' center=" + V(childBounds.center)
                        + " size=" + V(childBounds.size) + " y=[" + F(childBounds.min.y) + ", " + F(childBounds.max.y) + "]");
            }

            var rb = root.GetComponent<Rigidbody>();
            if (rb != null)
                sb.AppendLine("  Rigidbody: kinematic=" + rb.isKinematic + " gravity=" + rb.useGravity
                    + " mass=" + rb.mass + " constraints=" + rb.constraints);

            Collider[] colliders = root.GetComponentsInChildren<Collider>(true);
            sb.AppendLine("  colliders: " + colliders.Length);
            foreach (Collider c in colliders)
            {
                bool onRoot = c.transform == root.transform;
                sb.Append("    " + c.GetType().Name + " on '" + c.transform.name + "'"
                    + (onRoot ? " [ROOT]" : " [child - disabled by the static spawn pipeline]")
                    + " enabled=" + c.enabled + " trigger=" + c.isTrigger);

                if (c is CapsuleCollider capsule)
                {
                    Vector3 scale = c.transform.lossyScale;
                    float radius = capsule.radius * Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.z));
                    float height = Mathf.Max(capsule.height * Mathf.Abs(scale.y), radius * 2f);
                    float bottom = capsule.center.y * scale.y - height * 0.5f;
                    float top = capsule.center.y * scale.y + height * 0.5f;
                    sb.AppendLine();
                    sb.AppendLine("      capsule center=" + V(capsule.center) + " radius=" + F(radius)
                        + " (diameter " + F(radius * 2f) + ") height=" + F(height) + " dir=" + capsule.direction);
                    sb.AppendLine("      capsule y=[" + F(bottom) + ", " + F(top) + "]");

                    if (!onRoot)
                        continue;

                    if (radius > HumanRadiusReference)
                        sb.AppendLine("      WARN too fat: " + F(radius * 2f) + " m wide (a walking human is ~0.5 m)");
                    if (Mathf.Abs(capsule.center.x) > 0.15f || Mathf.Abs(capsule.center.z) > 0.15f)
                        sb.AppendLine("      WARN off-centre: horizontal offset from the pivot is ("
                            + F(capsule.center.x) + ", " + F(capsule.center.z) + ") m");
                    if (hasBounds)
                    {
                        if (bottom < bounds.min.y - 0.05f)
                            sb.AppendLine("      WARN sunk: capsule reaches " + F(bounds.min.y - bottom) + " m below the mesh bottom");
                        if (top < bounds.max.y - 0.15f)
                            sb.AppendLine("      WARN short: top " + F(bounds.max.y - top) + " m of the character has no collider");
                        float suggestedHeight = Mathf.Max(0.2f, bounds.max.y - Mathf.Min(0f, bounds.min.y));
                        sb.AppendLine("      suggested: center=(0, " + F(suggestedHeight * 0.5f) + ", 0) radius=0.25 height="
                            + F(suggestedHeight));
                    }
                    continue;
                }

                if (c is BoxCollider box)
                    sb.AppendLine("  center=" + V(box.center) + " size=" + V(box.size));
                else if (c is SphereCollider sphere)
                    sb.AppendLine("  center=" + V(sphere.center) + " radius=" + F(sphere.radius));
                else
                    sb.AppendLine();
            }

            if (colliders.Length == 0)
                sb.AppendLine("    (none - RuntimeEditorManager.EnsureRootSelectionCollider will fit a box from the"
                    + " renderer bounds at spawn time)");
        }

        /// <summary>
        /// Bind-pose mesh bounds of <paramref name="subtree"/> expressed in <paramref name="root"/>'s
        /// local space. Reads shared meshes instead of Renderer.bounds because a prefab asset is not
        /// in a scene, so its renderers report nothing useful. Skinned meshes are bounded in their
        /// root-bone frame, which is how Unity itself derives SkinnedMeshRenderer.localBounds.
        /// </summary>
        static bool TryComputeLocalBounds(Transform root, Transform subtree, out Bounds bounds)
        {
            bounds = default;
            bool hasBounds = false;

            foreach (Renderer r in subtree.GetComponentsInChildren<Renderer>(true))
            {
                Bounds local;
                Transform frame;
                if (r is SkinnedMeshRenderer smr)
                {
                    if (smr.sharedMesh == null)
                        continue;
                    // localBounds is expressed in the ROOT BONE's space, which is also the frame
                    // Unity uses to derive SkinnedMeshRenderer.bounds. sharedMesh.bounds is in a
                    // different space entirely and produces 3 m tall humans if used here.
                    local = smr.localBounds;
                    frame = smr.rootBone != null ? smr.rootBone : smr.transform;
                }
                else
                {
                    var mf = r.GetComponent<MeshFilter>();
                    if (mf == null || mf.sharedMesh == null)
                        continue;
                    local = mf.sharedMesh.bounds;
                    frame = r.transform;
                }

                Matrix4x4 toRoot = root.worldToLocalMatrix * frame.localToWorldMatrix;
                Vector3 c = local.center;
                Vector3 e = local.extents;
                for (int sx = -1; sx <= 1; sx += 2)
                    for (int sy = -1; sy <= 1; sy += 2)
                        for (int sz = -1; sz <= 1; sz += 2)
                        {
                            Vector3 corner = toRoot.MultiplyPoint3x4(c + new Vector3(e.x * sx, e.y * sy, e.z * sz));
                            if (!hasBounds)
                            {
                                bounds = new Bounds(corner, Vector3.zero);
                                hasBounds = true;
                            }
                            else
                            {
                                bounds.Encapsulate(corner);
                            }
                        }
            }

            return hasBounds;
        }

        static string F(float v)
        {
            return v.ToString("0.###");
        }

        static string V(Vector3 v)
        {
            return "(" + F(v.x) + ", " + F(v.y) + ", " + F(v.z) + ")";
        }

        static void DumpAsset(StringBuilder sb, string path)
        {
            sb.AppendLine();
            sb.AppendLine("--- " + path + " ---");

            var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (go == null)
            {
                sb.AppendLine("  LoadAssetAtPath<GameObject> returned NULL");
            }
            else
            {
                sb.AppendLine("  Hierarchy:");
                DumpHierarchy(sb, go.transform, 2);

                Renderer[] renderers = go.GetComponentsInChildren<Renderer>(true);
                sb.AppendLine("  Renderer count: " + renderers.Length);
                foreach (Renderer r in renderers)
                    DumpRenderer(sb, go.transform, r);
            }

            Object[] subAssets = AssetDatabase.LoadAllAssetsAtPath(path);
            int meshes = 0, clips = 0, others = 0;
            long vertexTotal = 0;
            foreach (Object o in subAssets)
            {
                if (o is Mesh mesh)
                {
                    meshes++;
                    vertexTotal += mesh.vertexCount;
                    sb.AppendLine("  Mesh sub-asset: '" + mesh.name + "' verts=" + mesh.vertexCount + " bounds=" + mesh.bounds);
                }
                else if (o is AnimationClip)
                    clips++;
                else
                    others++;
            }
            sb.AppendLine("  Sub-assets: " + meshes + " mesh(es) (" + vertexTotal + " verts), "
                + clips + " clip(s), " + others + " other(s), total " + subAssets.Length);
        }

        static void DumpHierarchy(StringBuilder sb, Transform t, int indent)
        {
            sb.Append(new string(' ', indent * 2));
            sb.Append(t.name);
            sb.Append(t.gameObject.activeSelf ? "" : "  [INACTIVE]");
            sb.Append("  pos=" + t.localPosition + " scale=" + t.localScale);

            var components = t.GetComponents<Component>();
            foreach (Component c in components)
            {
                if (c == null)
                {
                    sb.Append("  [MISSING SCRIPT]");
                    continue;
                }
                if (c is Transform)
                    continue;
                sb.Append("  <" + c.GetType().Name + ">");
                if (c is Animator a)
                    sb.Append("(controller=" + (a.runtimeAnimatorController != null ? a.runtimeAnimatorController.name : "NULL")
                        + ", avatar=" + (a.avatar != null ? a.avatar.name : "NULL")
                        + (a.avatar != null && !a.avatar.isValid ? " INVALID" : "") + ")");
                if (c is IVI.AttachPropToHand aph)
                    sb.Append("(prop=" + (aph.prop != null ? aph.prop.name : "NULL")
                        + ", bone=" + aph.handBone + ", autoTag='" + aph.autoTagBoneName + "')");
                if (c is IVI.PhoneUserArmPose pap)
                    sb.Append("(animTarget='" + pap.debugAnimatorTarget + "')");
            }

            sb.AppendLine();
            for (int i = 0; i < t.childCount; i++)
                DumpHierarchy(sb, t.GetChild(i), indent + 1);
        }

        static void DumpRenderer(StringBuilder sb, Transform root, Renderer r)
        {
            string relative = AnimationUtility.CalculateTransformPath(r.transform, root);
            string meshInfo;
            if (r is SkinnedMeshRenderer smr)
                meshInfo = smr.sharedMesh != null
                    ? smr.sharedMesh.name + " verts=" + smr.sharedMesh.vertexCount
                    : "NULL sharedMesh";
            else
            {
                var mf = r.GetComponent<MeshFilter>();
                meshInfo = mf == null ? "(no MeshFilter)"
                    : mf.sharedMesh != null ? mf.sharedMesh.name + " verts=" + mf.sharedMesh.vertexCount
                    : "NULL sharedMesh";
            }

            var mats = new StringBuilder();
            foreach (Material m in r.sharedMaterials)
            {
                if (m == null)
                    mats.Append("NULL; ");
                else
                    mats.Append(m.name + " [" + (m.shader != null ? m.shader.name : "NO SHADER") + "]; ");
            }

            sb.AppendLine("    R '" + relative + "' type=" + r.GetType().Name
                + " enabled=" + r.enabled
                + " mesh=" + meshInfo
                + " mats=" + mats);
        }
    }
}
#endif
