using System.Collections.Generic;
using UnityEngine;
using IVI;
using SEAN.Control;

namespace SessionReview
{
    /// <summary>
    /// Live, bottom-of-screen speed HUD shown only while a trial is running (hidden
    /// during review, world-building, onboarding and the trial-start prompt). Lists the
    /// robot and each PWD pedestrian with their current speed, and gives every agent its
    /// own slider that scales that agent's speed live so different scenarios can be set up
    /// from the start.
    ///
    /// Self-bootstraps at runtime so no scene wiring is needed; it also works if attached
    /// to a GameObject manually (duplicates destroy themselves).
    /// </summary>
    public class AgentSpeedOverlay : MonoBehaviour
    {
        private static AgentSpeedOverlay instance;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (instance != null) return;
            var go = new GameObject("AgentSpeedOverlay");
            go.AddComponent<AgentSpeedOverlay>();
            DontDestroyOnLoad(go);
        }

        public KeyCode toggleKey = KeyCode.F8;

        [Tooltip("Lowest speed multiplier the sliders allow.")]
        public float minScale = 0.1f;
        [Tooltip("Highest speed multiplier the sliders allow.")]
        public float maxScale = 3.0f;

        private const float RescanInterval = 1.0f;
        private const float SpeedSmoothing = 6.0f;

        private readonly List<Tracked> tracked = new List<Tracked>();
        private float nextRescanTime;
        private bool visible = true;
        private GUIStyle titleStyle;
        private GUIStyle rowStyle;
        private GUIStyle scaleStyle;

        private class Tracked
        {
            public string label;
            public Transform tform;
            public VelocityController robot;              // set for the robot
            public SFPWDAgent pwdAgent;                   // set for a PWD (auto speed)
            public ManualWheelchairController pwdManual;  // set for a PWD (manual speed)
            public Vector3 lastPos;
            public float speed;   // smoothed planar speed (m/s)
            public float scale;   // slider value (speed multiplier)
        }

        void Awake()
        {
            if (instance != null && instance != this)
            {
                Destroy(gameObject);
                return;
            }
            instance = this;
        }

        void Update()
        {
            if (Input.GetKeyDown(toggleKey)) visible = !visible;

            if (Time.unscaledTime >= nextRescanTime || AnyTrackedMissing())
            {
                Rescan();
                nextRescanTime = Time.unscaledTime + RescanInterval;
            }

            float dt = Time.deltaTime;
            if (dt <= 1e-5f) return;
            float lerp = 1f - Mathf.Exp(-SpeedSmoothing * dt);
            foreach (var t in tracked)
            {
                if (t.tform == null) continue;
                Vector3 p = t.tform.position;
                Vector3 delta = p - t.lastPos;
                delta.y = 0f;
                t.speed = Mathf.Lerp(t.speed, delta.magnitude / dt, lerp);
                t.lastPos = p;
            }
        }

        private bool AnyTrackedMissing()
        {
            if (tracked.Count == 0) return true;
            foreach (var t in tracked)
                if (t.tform == null) return true;
            return false;
        }

        private void Rescan()
        {
            // Carry slider values across rescans (and scene reloads) by transform.
            var priorScale = new Dictionary<Transform, float>();
            foreach (var t in tracked)
                if (t.tform != null) priorScale[t.tform] = t.scale;

            tracked.Clear();

            var robot = FindObjectOfType<VelocityController>();
            if (robot != null)
            {
                // base_link is the moving rigidbody transform; fall back to the
                // controller's own transform if the robot rig isn't resolved yet.
                var sean = SEAN.SEAN.instance;
                Transform robotTform = (sean != null && sean.robot != null && sean.robot.base_link != null)
                    ? sean.robot.base_link.transform
                    : robot.transform;
                var tr = new Tracked
                {
                    label = "Robot",
                    tform = robotTform,
                    robot = robot,
                    lastPos = robotTform.position,
                    scale = priorScale.TryGetValue(robotTform, out var s) ? s : robot.speedScale
                };
                ApplyScale(tr);
                tracked.Add(tr);
            }

            var pwds = FindObjectsOfType<ManualWheelchairController>();
            foreach (var pwd in pwds)
            {
                if (pwd == null) continue;
                var agent = pwd.GetComponent<SFPWDAgent>();
                float init = priorScale.TryGetValue(pwd.transform, out var s)
                    ? s
                    : (agent != null ? agent.autoSpeedScale : pwd.speedScale);
                var tr = new Tracked
                {
                    label = pwds.Length > 1 ? pwd.gameObject.name : "PWD",
                    tform = pwd.transform,
                    pwdAgent = agent,
                    pwdManual = pwd,
                    lastPos = pwd.transform.position,
                    scale = init
                };
                ApplyScale(tr);
                tracked.Add(tr);
            }
        }

        private void ApplyScale(Tracked t)
        {
            t.scale = Mathf.Clamp(t.scale, minScale, maxScale);
            if (t.robot != null) t.robot.speedScale = t.scale;
            if (t.pwdAgent != null) t.pwdAgent.autoSpeedScale = t.scale;
            if (t.pwdManual != null) t.pwdManual.speedScale = t.scale;
        }

        private bool ShouldHide()
        {
            var srm = SessionReviewManager.Instance;
            return srm != null && (srm.IsReviewModeActive
                                   || srm.IsWorldBuildingModeActive
                                   || srm.IsOnboardingActive
                                   || srm.BlocksAutomaticTrialStart);
        }

        void OnGUI()
        {
            if (!visible || tracked.Count == 0 || ShouldHide()) return;

            if (titleStyle == null)
            {
                titleStyle = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft, fontSize = 15 };
                rowStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleLeft, fontSize = 14 };
                scaleStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleRight, fontStyle = FontStyle.Bold, fontSize = 14 };
            }

            const float margin = 16f;   // gap from screen edges
            const float pad = 14f;      // inner padding
            const float gap = 12f;      // gap between columns
            const float rowH = 34f;
            const float headerH = 28f;
            const float labelW = 150f;  // "Robot: 0.00 m/s"
            const float scaleW = 52f;   // "x1.00"

            float barW = Mathf.Min(400f, Screen.width - 2f * margin);
            float barH = pad * 2f + headerH + tracked.Count * rowH;
            float x = Screen.width - barW - margin;   // bottom-right corner
            float y = Screen.height - barH - margin;

            GUI.Box(new Rect(x, y, barW, barH), GUIContent.none);

            float rx = x + pad;
            float rw = barW - 2f * pad;
            GUI.Label(new Rect(rx, y + pad, rw, headerH),
                $"Agent Speed   ([{toggleKey}] hide)", titleStyle);

            float sliderX = rx + labelW + gap;
            float sliderW = rw - labelW - gap - scaleW - gap;
            float rowY = y + pad + headerH;

            for (int i = 0; i < tracked.Count; i++)
            {
                var t = tracked[i];
                GUI.Label(new Rect(rx, rowY, labelW, rowH), $"{t.label}: {t.speed:F2} m/s", rowStyle);

                float newScale = GUI.HorizontalSlider(
                    new Rect(sliderX, rowY + rowH * 0.5f - 4f, sliderW, 18f),
                    t.scale, minScale, maxScale);
                if (!Mathf.Approximately(newScale, t.scale))
                {
                    t.scale = newScale;
                    ApplyScale(t);
                }

                GUI.Label(new Rect(sliderX + sliderW + gap, rowY, scaleW, rowH), $"x{t.scale:F2}", scaleStyle);
                rowY += rowH;
            }
        }
    }
}
