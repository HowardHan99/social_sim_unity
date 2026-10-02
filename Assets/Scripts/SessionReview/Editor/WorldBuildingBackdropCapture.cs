using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace SessionReview.EditorTools
{
    /// <summary>
    /// Renders top-down COLOR captures of the four world-building scenario scenes,
    /// using the exact world extents of the grayscale backdrops embedded in the
    /// placement-map artifact, so the images drop in without re-alignment.
    /// Output: SessionLogs/_analysis_worldbuilding/backdrops_color/&lt;scene&gt;.jpg (+ extents.json).
    /// </summary>
    public static class WorldBuildingBackdropCapture
    {
        private const float PixelsPerMeter = 24f;
        private const int MaxDim = 2048;
        private const float CameraHeight = 60f;

        // x0, x1, z0, z1 — must match the artifact DATA backdropExtent values.
        private static readonly (string scene, float x0, float x1, float z0, float z1)[] Targets =
        {
            ("sidewalkNarrowroad",        -38.07f, -17.91f, -19.68f,  14.84f),
            ("sidewalkCornerInteraction", -37.90f, -12.98f, -41.71f, -19.54f),
            ("sidewalkCrossroad",         -46.83f, -18.15f, -41.49f, -16.15f),
            ("sidewalkOutofStore",        -38.85f, -17.49f, -22.95f,   6.51f),
        };

        [MenuItem("SessionReview/Capture World Building Backdrops (Color)")]
        public static void CaptureAll()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorUtility.DisplayDialog("Backdrop capture", "Exit Play Mode first.", "OK");
                return;
            }
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return;

            string previousScenePath = EditorSceneManager.GetActiveScene().path;
            string outDir = Path.Combine("SessionLogs", "_analysis_worldbuilding", "backdrops_color");
            Directory.CreateDirectory(outDir);

            var extentsJson = new System.Text.StringBuilder();
            extentsJson.Append("{\n");

            try
            {
                for (int i = 0; i < Targets.Length; i++)
                {
                    var t = Targets[i];
                    string scenePath = "Assets/Scenes/" + t.scene + ".unity";
                    EditorUtility.DisplayProgressBar("Backdrop capture", t.scene, (float)i / Targets.Length);
                    EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
                    CaptureScene(t.x0, t.x1, t.z0, t.z1, Path.Combine(outDir, t.scene + ".jpg"));
                    extentsJson.AppendFormat(
                        "  \"{0}\": [{1}, {2}, {3}, {4}]{5}\n",
                        t.scene, t.x0, t.x1, t.z0, t.z1, i < Targets.Length - 1 ? "," : "");
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                if (!string.IsNullOrEmpty(previousScenePath))
                    EditorSceneManager.OpenScene(previousScenePath, OpenSceneMode.Single);
            }

            extentsJson.Append("}\n");
            File.WriteAllText(Path.Combine(outDir, "extents.json"), extentsJson.ToString());
            Debug.Log("[BackdropCapture] Wrote 4 color backdrops to " + outDir);
            EditorUtility.RevealInFinder(Path.Combine(outDir, Targets[0].scene + ".jpg"));
        }

        private static void CaptureScene(float x0, float x1, float z0, float z1, string outPath)
        {
            float wMeters = x1 - x0, hMeters = z1 - z0;
            float ppm = PixelsPerMeter;
            int w = Mathf.RoundToInt(wMeters * ppm);
            int h = Mathf.RoundToInt(hMeters * ppm);
            if (Mathf.Max(w, h) > MaxDim)
            {
                float k = MaxDim / (float)Mathf.Max(w, h);
                w = Mathf.RoundToInt(w * k);
                h = Mathf.RoundToInt(h * k);
            }

            bool hadFog = RenderSettings.fog;
            RenderSettings.fog = false;

            var go = new GameObject("~BackdropCaptureCamera");
            var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32);
            try
            {
                var cam = go.AddComponent<Camera>();
                // straight down, image up = +z, image right = +x (matches the artifact mapping)
                go.transform.position = new Vector3((x0 + x1) * 0.5f, CameraHeight, (z0 + z1) * 0.5f);
                go.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
                cam.orthographic = true;
                cam.orthographicSize = hMeters * 0.5f;
                cam.aspect = wMeters / hMeters;
                cam.nearClipPlane = 0.3f;
                cam.farClipPlane = 300f;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.32f, 0.33f, 0.34f, 1f);
                cam.cullingMask = ~0;
                cam.allowHDR = false;
                cam.allowMSAA = true;
                cam.targetTexture = rt;
                cam.Render();

                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                tex.Apply();
                RenderTexture.active = prev;

                File.WriteAllBytes(outPath, tex.EncodeToJPG(88));
                Object.DestroyImmediate(tex);
                Debug.Log("[BackdropCapture] " + outPath + " (" + w + "x" + h + ")");
            }
            finally
            {
                RenderSettings.fog = hadFog;
                Object.DestroyImmediate(go);
                rt.Release();
                Object.DestroyImmediate(rt);
            }
        }
    }
}
