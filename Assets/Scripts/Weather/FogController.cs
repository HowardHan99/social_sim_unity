using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Weather
{
    /// <summary>
    /// Scene fog for foggy-weather worlds, driven by the World Building "Weather" panel
    /// (SessionReviewManager) and rebuilt from saved scenarios by
    /// WorldBuildingScenarioRestorer. Uses the built-in pipeline's RenderSettings fog, so
    /// no third-party assets are needed.
    ///
    /// Created on demand via <see cref="EnsureInstance"/> and persists across scene loads,
    /// but the fog itself resets on every scene load: weather belongs to the built world,
    /// so a clean scene comes up clear and the scenario restorer re-applies fog when its
    /// save says so.
    ///
    /// Built-in pipeline quirks handled here:
    /// - The skybox ignores fog, so skybox-clearing perspective cameras get a solid
    ///   fog-colored background while fog is active (restored once it clears).
    /// - Orthographic cameras (the World Building top-down map) render with fog disabled —
    ///   at map height the whole world would fog out and editing would be impossible.
    /// </summary>
    public class FogController : MonoBehaviour
    {
        private static FogController instance;

        public static FogController Instance => instance;

        /// <summary>The controller, created (and kept across scene loads) on first use.</summary>
        public static FogController EnsureInstance()
        {
            if (instance == null)
            {
                var go = new GameObject("FogController");
                go.AddComponent<FogController>();
                DontDestroyOnLoad(go);
            }
            return instance;
        }

        [Tooltip("Sky/fog tint while fog is active.")]
        public Color fogColor = new Color(0.73f, 0.76f, 0.79f);

        [Tooltip("Seconds for fog to fade fully in or out.")]
        public float fadeDuration = 2.5f;

        [Tooltip("ExponentialSquared densities for the light / medium / heavy presets.")]
        public float[] densityPresets = { 0.012f, 0.03f, 0.06f };

        [Tooltip("Active density preset (0 = light).")]
        public int presetIndex = 1;

        /// <summary>Whether fog is on (or fading in). Saved into World Building scenarios.</summary>
        public bool FogActive { get; private set; }

        private bool applied;               // we currently own RenderSettings + camera state
        private float currentDensity;

        private bool origFogEnabled;
        private FogMode origFogMode;
        private float origFogDensity;
        private Color origFogColor;

        private struct CameraState
        {
            public CameraClearFlags clearFlags;
            public Color background;
        }
        private readonly Dictionary<Camera, CameraState> touchedCameras = new Dictionary<Camera, CameraState>();

        private void Awake()
        {
            if (instance != null && instance != this)
            {
                Destroy(gameObject);
                return;
            }
            instance = this;
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private void OnEnable()
        {
            Camera.onPreRender += HandleCameraPreRender;
            Camera.onPostRender += HandleCameraPostRender;
        }

        private void OnDisable()
        {
            Camera.onPreRender -= HandleCameraPreRender;
            Camera.onPostRender -= HandleCameraPostRender;
        }

        private void OnDestroy()
        {
            if (instance == this)
            {
                SceneManager.sceneLoaded -= OnSceneLoaded;
                instance = null;
            }
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (mode != LoadSceneMode.Single)
                return;
            // Weather is part of the built world: a freshly loaded scene starts clear (its
            // own pristine RenderSettings), and the scenario restorer turns fog back on when
            // the loaded save asks for it. The old scene's captured state died with it.
            touchedCameras.Clear();
            applied = false;
            FogActive = false;
            currentDensity = 0f;
        }

        /// <summary>
        /// Turns fog on/off. Fades over <see cref="fadeDuration"/> unless
        /// <paramref name="immediate"/>, which snaps (used by scenario restore so a foggy
        /// world starts foggy instead of forming in front of the participant).
        /// </summary>
        public void SetFog(bool on, bool immediate = false)
        {
            FogActive = on;
            if (on)
            {
                EnsureApplied();
                if (immediate)
                    currentDensity = TargetDensity();
            }
            else if (immediate)
            {
                currentDensity = 0f;
                if (applied)
                    Restore();
            }
            Debug.Log($"[FogController] Fog {(on ? "ON" : "OFF")} (preset {presetIndex + 1}, density {TargetDensity():0.###}{(immediate ? ", immediate" : "")})");
        }

        /// <summary>Selects a density preset; an active fog fades to the new density.</summary>
        public void SetPreset(int index)
        {
            int count = densityPresets != null ? densityPresets.Length : 0;
            presetIndex = count > 0 ? Mathf.Clamp(index, 0, count - 1) : index;
        }

        private void Update()
        {
            if (!applied)
                return;

            float target = FogActive ? TargetDensity() : 0f;
            float rate = Mathf.Max(TargetDensity(), 0.005f) / Mathf.Max(fadeDuration, 0.05f);
            currentDensity = Mathf.MoveTowards(currentDensity, target, rate * Time.unscaledDeltaTime);
            RenderSettings.fogDensity = currentDensity;

            if (FogActive)
                SweepCameras();

            if (!FogActive && currentDensity <= 0f)
                Restore();
        }

        private float TargetDensity()
        {
            if (densityPresets == null || densityPresets.Length == 0)
                return 0.03f;
            presetIndex = Mathf.Clamp(presetIndex, 0, densityPresets.Length - 1);
            return densityPresets[presetIndex];
        }

        private void EnsureApplied()
        {
            if (applied)
                return;
            origFogEnabled = RenderSettings.fog;
            origFogMode = RenderSettings.fogMode;
            origFogDensity = RenderSettings.fogDensity;
            origFogColor = RenderSettings.fogColor;

            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogColor = fogColor;
            RenderSettings.fogDensity = currentDensity;
            applied = true;
            SweepCameras();
        }

        /// <summary>
        /// The skybox ignores fog in the built-in pipeline, so give every skybox-clearing
        /// perspective camera a solid fog-colored background while fog is active. Runs every
        /// frame while fog is on because review cameras (F1-F5) and scene switches create
        /// cameras late. Orthographic cameras keep their look — they render fog-free.
        /// </summary>
        private void SweepCameras()
        {
            foreach (var cam in Camera.allCameras)
            {
                if (cam == null || cam.orthographic || touchedCameras.ContainsKey(cam))
                    continue;
                if (cam.clearFlags != CameraClearFlags.Skybox)
                    continue;
                touchedCameras[cam] = new CameraState { clearFlags = cam.clearFlags, background = cam.backgroundColor };
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = fogColor;
            }
        }

        private void Restore()
        {
            RenderSettings.fog = origFogEnabled;
            RenderSettings.fogMode = origFogMode;
            RenderSettings.fogDensity = origFogDensity;
            RenderSettings.fogColor = origFogColor;

            foreach (var kv in touchedCameras)
            {
                if (kv.Key == null)
                    continue;
                kv.Key.clearFlags = kv.Value.clearFlags;
                kv.Key.backgroundColor = kv.Value.background;
            }
            touchedCameras.Clear();
            applied = false;
        }

        // The World Building map view must stay readable while fog is on: from map height an
        // ExponentialSquared fog whites out the entire ground. Fog is suppressed around each
        // orthographic camera's render instead of per-camera settings because the built-in
        // pipeline only has the global RenderSettings toggle.
        private void HandleCameraPreRender(Camera cam)
        {
            if (applied && cam != null && cam.orthographic)
                RenderSettings.fog = false;
        }

        private void HandleCameraPostRender(Camera cam)
        {
            if (applied && cam != null && cam.orthographic)
                RenderSettings.fog = true;
        }
    }
}
