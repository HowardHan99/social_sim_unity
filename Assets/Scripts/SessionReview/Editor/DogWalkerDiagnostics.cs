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

        static DogWalkerDiagnostics()
        {
            EditorApplication.delayCall += Run;
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
