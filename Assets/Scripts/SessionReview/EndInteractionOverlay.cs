using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace SessionReview
{
    /// <summary>
    /// Operator-side "End Interaction" button, rendered on the secondary display
    /// (Display 2) so the control stays out of the participant's main view. Visibility
    /// and the click action are owned by SessionReviewManager — this is only the
    /// Display-2 face of the same button.
    ///
    /// Mirrors the AgentSpeedOverlay conventions: uGUI Canvas with targetDisplay
    /// (Unity's OnGUI can only render on Display 1), self-bootstraps so no scene
    /// wiring is needed. In builds without a second display this canvas stays hidden
    /// and SessionReviewManager keeps drawing its original Display-1 IMGUI button
    /// instead, so the button exists on exactly one display in every setup.
    ///
    /// Editor note: like ResearcherDisplay/AgentSpeedOverlay, the Editor never
    /// reports a second display; the button appears in a Game view window set to
    /// "Display 2".
    /// </summary>
    public class EndInteractionOverlay : MonoBehaviour
    {
        private static EndInteractionOverlay instance;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (instance != null) return;
            var go = new GameObject("EndInteractionOverlay");
            go.AddComponent<EndInteractionOverlay>();
            DontDestroyOnLoad(go);
        }

        [Tooltip("Display the button renders on. 0 = main display, 1 = Display 2 (default).")]
        public int targetDisplay = 1;

        /// <summary>
        /// True when this overlay owns the End Interaction button: the target display is
        /// available (or in the Editor, where the "Display 2" Game-view convention
        /// applies). SessionReviewManager suppresses its Display-1 IMGUI button then.
        /// </summary>
        public static bool HandlesEndInteractionButton =>
            instance != null && instance.SecondDisplayAvailable;

        private bool SecondDisplayAvailable
        {
#if UNITY_EDITOR
            // The Editor always reports one display even with a "Display 2" Game view
            // open, so keep targeting it (the ResearcherDisplay convention).
            get { return true; }
#else
            get { return Display.displays.Length > targetDisplay; }
#endif
        }

        private Canvas canvas;
        private Text label;

        void Awake()
        {
            if (instance != null && instance != this)
            {
                Destroy(gameObject);
                return;
            }
            instance = this;
        }

        void OnDestroy()
        {
            if (instance == this)
                instance = null;
        }

        void Update()
        {
            EnsureUi();

            var srm = SessionReviewManager.Instance;
            bool show = SecondDisplayAvailable && srm != null && srm.ShouldShowEndInteractionButton;
            if (canvas.gameObject.activeSelf != show)
                canvas.gameObject.SetActive(show);
            if (!show)
                return;

            if (label.text != srm.EndInteractionButtonLabel)
                label.text = srm.EndInteractionButtonLabel;
            if (canvas.targetDisplay != targetDisplay)
                canvas.targetDisplay = targetDisplay;
        }

        private void EnsureUi()
        {
            if (canvas != null) return;

            EnsureEventSystem();

            var canvasGo = new GameObject("EndInteractionCanvas",
                typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGo.transform.SetParent(transform, false);

            canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.targetDisplay = targetDisplay;
            canvas.sortingOrder = 500;

            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            // Button docked bottom-center of the operator display.
            var buttonRt = NewRect("EndInteractionButton", canvasGo.transform);
            buttonRt.anchorMin = buttonRt.anchorMax = new Vector2(0.5f, 0f);
            buttonRt.pivot = new Vector2(0.5f, 0f);
            buttonRt.anchoredPosition = new Vector2(0f, 32f);
            buttonRt.sizeDelta = new Vector2(320f, 64f);

            var image = buttonRt.gameObject.AddComponent<Image>();
            image.color = new Color(0.16f, 0.16f, 0.16f, 0.92f);

            var button = buttonRt.gameObject.AddComponent<Button>();
            var colors = button.colors;
            colors.highlightedColor = new Color(0.3f, 0.3f, 0.3f, 1f);
            colors.pressedColor = new Color(0.55f, 0.2f, 0.2f, 1f);
            button.colors = colors;
            button.onClick.AddListener(() => SessionReviewManager.Instance?.EndCurrentInteraction());

            var labelRt = NewRect("Label", buttonRt);
            labelRt.anchorMin = Vector2.zero;
            labelRt.anchorMax = Vector2.one;
            labelRt.offsetMin = Vector2.zero;
            labelRt.offsetMax = Vector2.zero;
            label = labelRt.gameObject.AddComponent<Text>();
            label.font = LoadUiFont();
            label.fontSize = 26;
            label.alignment = TextAnchor.MiddleCenter;
            label.color = Color.white;
            label.text = "End Interaction";
            label.raycastTarget = false;

            canvas.gameObject.SetActive(false);
        }

        private static RectTransform NewRect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        private static void EnsureEventSystem()
        {
            if (EventSystem.current != null) return;
            if (FindObjectOfType<EventSystem>() != null) return;
            var es = new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
            DontDestroyOnLoad(es);
        }

        private static Font LoadUiFont()
        {
            Font f = Resources.GetBuiltinResource<Font>("Arial.ttf");
            if (f == null) f = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (f == null) f = Font.CreateDynamicFontFromOSFont("Arial", 14);
            return f;
        }
    }
}
