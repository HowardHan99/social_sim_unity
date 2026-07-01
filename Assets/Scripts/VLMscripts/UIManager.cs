using UnityEngine;
using TMPro;
using System.Collections.Generic;
using UnityEngine.UI;
using System.Collections;
using System;

public class UIManager : MonoBehaviour
{
    public event Action ResponseWindowClosed;

    public GameObject popUpWindow; // Reference to the pop-up panel for the response content selection
    public GameObject responseWindow;

    public List<GameObject> signalButtons;

    public List<Toggle> optionToggles; // List of all toggles
    public TMP_Text promptDisplay; // To show the generated prompt

    public TMP_InputField responseInputField; // Editable text field for LLM response
    public Button confirmResponseButton; // Button to confirm the edited response
    public TTSManager ttsManager; // Reference to TTSManager

    private string prompt; // Store the generated prompt string
    private string finalResponse; // Store the confirmed response text

    private Camera tempReplayCamera; // Temporary camera for replay viewing
    private Camera mainCamera; // Reference to the main camera
    private Vector2 camRotation = Vector2.zero;
    private float flySpeed = 5f; // Speed for camera movement
    private float lookSpeed = 2f; // Speed for camera rotation
    private bool isUsingTempCamera = false;
    private bool isPositioningCamera = false; // True when user is finding a spot
    private bool isReplaying = false; // True when replay is active
    private bool isAwaitingVlmResponse;
    
    public GameObject instructionPanel; // Reference to InstructionPanel
    public TMP_InputField instructionInputField; // Reference to InputField (TMP) for showing instructions
    public Button confirmBN; // Reference to ConfirmBN button
    public Button vlmCaptureButton; // Dedicated VLM entry button shown only during VLM flow
    public CamCapture vlmCamCapture; // Optional direct hook so prompt confirmation can trigger capture

    [Header("Preset Voice Messages (OR quick-send)")]
    [Tooltip("Quick alternative to the VLM: send one of these fixed messages straight to text-to-speech without capturing an image.")]
    public List<string> presetVoiceMessages = new List<string>
    {
        "Please, give way.",
        "Excuse me.",
        "Attention, robot here."
    };
    [Tooltip("Optional. A styled Button placed inside the signal pop-up that is cloned once per preset message. It is hidden at runtime and used only as a template.")]
    public Button presetVoiceMessageTemplate;
    [Tooltip("Optional. Parent (ideally with a Vertical/Horizontal Layout Group) that holds the generated preset buttons. Defaults to the template's parent when left empty.")]
    public Transform presetVoiceMessageContainer;
    [Tooltip("Generate and cache the preset clips at startup so the first press plays without network latency.")]
    public bool prewarmPresetVoiceMessages = true;

    [Header("Generated Signal UI (used when no template is assigned)")]
    [Tooltip("When no template is assigned, build the whole signal UI from code and switch off the scene's hard-positioned UI.")]
    public bool autoGeneratePresetButtons = true;

    // Layout/styling kept in code (not serialized) so edits here always take effect at runtime.
    private const float presetPanelWidth = 560f;
    private readonly Vector2 presetButtonSize = new Vector2(360f, 46f);
    private const float presetButtonSpacing = 8f;
    private const float presetButtonFontSize = 23f;
    private const float presetHeaderFontSize = 27f;
    private readonly Color presetButtonColor = new Color(0.16f, 0.52f, 0.45f, 0.97f);    // teal quick-message chips
    private readonly Color presetConfirmColor = new Color(0.20f, 0.58f, 0.32f, 0.97f);   // green confirm
    private readonly Color presetExpanderColor = new Color(1f, 1f, 1f, 0.12f);           // subtle expander
    private readonly Color presetPanelBackdropColor = new Color(0f, 0f, 0f, 0.72f);
    private readonly Color presetTextColor = Color.white;
    private const string CollapsedExpanderText = "▸  Build a detailed message";
    private const string ExpandedExpanderText = "▾  Hide detailed options";

    private bool presetVoiceMessagesBuilt;
    private GameObject presetPanelInstance;     // generated root panel, when no template is used
    private GameObject detailedSectionInstance; // collapsible checkbox/LLM section
    private TMP_Text detailedExpanderLabel;
    private readonly List<Toggle> generatedToggles = new List<Toggle>();
    private static readonly string[] DetailedSignalOptions =
    {
        "Declare robot movement info",
        "Confirm detection of pedestrian",
        "Instruction for pedestrians to avoid accident"
    };

    public bool SuppressSignalButtons { get; set; }
    public bool UnpauseOnExitResponseWindow { get; set; } = true;
    public bool IsVlmSignalFlowActive { get; private set; }

    float elapsedTime = 0f;

    void Start()
    {
        HideAllVlmWindows();
        SetVlmCaptureButtonVisible(false);

        // Setup confirm button listener
        if (confirmResponseButton != null)
        {
            confirmResponseButton.onClick.AddListener(OnConfirmResponseButtonPressed);
        }

        BuildPresetVoiceMessageButtons();

        if (prewarmPresetVoiceMessages)
            PrewarmPresetVoiceMessages();
    }

    void Update()
    {
        elapsedTime += Time.unscaledDeltaTime; // Ensures the UI works even when the game is paused

        // The generated panel lives on the Canvas, so mirror the pop-up's visibility manually.
        if (presetPanelInstance != null)
        {
            bool showPresets = popUpWindow != null && popUpWindow.activeInHierarchy;
            if (presetPanelInstance.activeSelf != showPresets)
            {
                presetPanelInstance.SetActive(showPresets);
                if (showPresets)
                    CollapseDetailedSection(); // always open compact (quick messages only)
            }
        }

        // Show signal buttons only when the game is paused and the session is robot-focused
        if (PauseManager.Instance != null && !IsVlmSignalFlowActive)
        {
            bool robotFocusedPlayer = !SessionReview.SessionOnboardingSettings.HasCompletedOnboarding ||
                SessionReview.SessionOnboardingSettings.PlayerMode == SessionReview.OnboardingPlayerMode.Robot;
            bool reviewSessionActive = SessionReview.SessionReviewManager.Instance != null &&
                (SessionReview.SessionReviewManager.Instance.IsReviewModeActive ||
                 SessionReview.SessionReviewManager.Instance.IsWorldBuildingModeActive);
            bool shouldShow = PauseManager.Instance.isPaused &&
                !SuppressSignalButtons &&
                robotFocusedPlayer &&
                !reviewSessionActive;
            SetManagedSignalButtonsVisible(shouldShow);
        }

        // Handle temp camera movement when positioning
        if (isPositioningCamera && tempReplayCamera != null)
        {
            HandleTempCameraMovement();
            
            // Check for Enter key to confirm position and start replay
            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
            {
                StartReplayFromCurrentPosition();
            }
        }
    }

    void HandleTempCameraMovement()
    {
        // WASD movement (horizontal only, Y-axis locked at 0)
        float v = Input.GetKey(KeyCode.W) ? 1 : Input.GetKey(KeyCode.S) ? -1 : 0;
        float h = Input.GetKey(KeyCode.A) ? -1 : Input.GetKey(KeyCode.D) ? 1 : 0;

        // Move camera in local space
        tempReplayCamera.transform.Translate(Vector3.forward * flySpeed * v * Time.unscaledDeltaTime, Space.Self);
        tempReplayCamera.transform.Translate(Vector3.right * flySpeed * h * Time.unscaledDeltaTime, Space.Self);

        // Lock Y-axis to 0
        tempReplayCamera.transform.position = new Vector3(
            tempReplayCamera.transform.position.x,
            0f,
            tempReplayCamera.transform.position.z
        );

        // Mouse look (right mouse button)
        if (Input.GetMouseButtonDown(1))
        {
            // Initialize rotation to prevent snapping
            camRotation.y = tempReplayCamera.transform.localRotation.eulerAngles.y;
            camRotation.x = tempReplayCamera.transform.localRotation.eulerAngles.x;

            if (camRotation.y > 360)
                camRotation.y = 0;
        }

        if (Input.GetMouseButton(1))
        {
            float mouseX = Input.GetAxis("Mouse X");
            float mouseY = -Input.GetAxis("Mouse Y");

            camRotation.y += mouseX * lookSpeed;
            camRotation.x += mouseY * lookSpeed;

            // Clamp vertical rotation
            camRotation.x = Mathf.Clamp(camRotation.x, -90f, 90f);

            tempReplayCamera.transform.rotation = Quaternion.Euler(camRotation.x, camRotation.y, 0);
        }
    }


    public string GetPrompt() // Allow other scripts to access the prompt
    {
        Debug.Log(prompt);
        promptDisplay.text = prompt;
        return prompt;
    }

    public void DisplayLLMResponse(string response)
    {
        isAwaitingVlmResponse = false;

        if (responseWindow != null && !responseWindow.activeSelf)
        {
            responseWindow.SetActive(true);
        }

        if (popUpWindow != null && popUpWindow.activeSelf)
        {
            popUpWindow.SetActive(false);
        }

        if (responseInputField != null)
        {
            responseInputField.text = response;
            responseInputField.interactable = true; // Make sure it's editable

            // Select all text for easy editing
            responseInputField.Select();
            responseInputField.ActivateInputField();
        }

        if (confirmResponseButton != null)
        {
            confirmResponseButton.interactable = true;
        }
    }

    void OnConfirmResponseButtonPressed()
    {
        if (responseInputField != null && ttsManager != null)
        {
            finalResponse = responseInputField.text;
            Debug.Log("User confirmed response: " + finalResponse);

            if (SessionReview.SessionReviewManager.Instance != null)
                SessionReview.SessionReviewManager.Instance.AttachVLMReplayResponse(finalResponse);

            // Convert to speech
            ttsManager.ConvertTextToSpeech(finalResponse);

            // Optional: Disable the confirm button to prevent multiple clicks
            confirmResponseButton.interactable = false;
            StartCoroutine(ReEnableConfirmButton());
        }
    }

    IEnumerator ReEnableConfirmButton()
    {
        yield return new WaitForSecondsRealtime(2f);
        if (confirmResponseButton != null)
        {
            confirmResponseButton.interactable = true;
        }
    }

    public void OnConfirmButtonPressed()
    {
        if (isAwaitingVlmResponse)
        {
            Debug.LogWarning("[UIManager] Ignoring duplicate VLM prompt confirmation while waiting for a response.");
            return;
        }

        List<string> selectedOptions = new List<string>();

        // Check each toggle and get its text if it's turned on
        foreach (Toggle toggle in optionToggles)
        {
            Debug.Log("Toggle is active: " + toggle.gameObject.name);
            Debug.Log("Is On: " + toggle.isOn);
            if (toggle.isOn)
            {
                TMP_Text toggleText = toggle.GetComponentInChildren<TMP_Text>(); // Access the text inside the toggle
                Debug.Log(toggleText.text);
                if (toggleText != null)
                {
                    selectedOptions.Add(toggleText.text); // Add the toggle text to the selection
                }
            }
        }

        prompt = GeneratePromptString(selectedOptions); // Store the prompt
        Debug.Log(prompt);
        isAwaitingVlmResponse = true;

        StartCoroutine(HidePopUp());
        StartCoroutine(CaptureAfterPromptConfirmation());
    }

    string GeneratePromptString(List<string> options)
    {
        string basePrompt = "The delivery robot is navigating on a sidewalk. The image is a real-time capture from the robot's perspective. Understand the surrounding context and the robot's current movement or behavior, then internally choose the appropriate robot signal. Output only the brief spoken robot response for nearby pedestrians. The response should be natural, intuitive, friendly, and safety-aware.";

        if (options.Count > 0)
        {
            basePrompt += " Prioritize these response qualities: ";
            basePrompt += string.Join(", ", options) + ".";
        }

        basePrompt += " Keep it under 16 words. Do not include scene descriptions, movement labels, context summaries, or headings. Robot onomatopoeia like beep-beep can be included if it feels natural.";

        return basePrompt;
    }

    public void OnCamCapButtonPressed()
    {
        if (PauseManager.Instance != null)
        {
            PauseManager.Instance.PauseGame();
        }

        ShowPopUp();
    }

    public void BeginVlmSignalFlow()
    {
        HideAllVlmWindows();
        IsVlmSignalFlowActive = true;
        SuppressSignalButtons = true;
        SetManagedSignalButtonsVisible(false);
        SetVlmCaptureButtonVisible(true);

        if (confirmResponseButton != null)
        {
            confirmResponseButton.interactable = true;
        }

        if (PauseManager.Instance != null && !PauseManager.Instance.IsGamePaused())
        {
            PauseManager.Instance.PauseGame();
        }
    }

    public void ResetVlmUiToIdle()
    {
        HideAllVlmWindows();
        HideAllSignalButtons();
        SuppressSignalButtons = true;
        SetVlmCaptureButtonVisible(false);
    }

    public void ShowPopUp()
    {
        popUpWindow.SetActive(true); // Show the pop-up window
        responseWindow.SetActive(false);
        SetManagedSignalButtonsVisible(false);
        SetVlmCaptureButtonVisible(false);

        if (confirmResponseButton != null)
        {
            confirmResponseButton.interactable = true;
        }
    }


    private IEnumerator HidePopUp()
    {
        yield return null;
        popUpWindow.SetActive(false); // Hide the pop-up window
        responseWindow.SetActive(true);
        SetVlmCaptureButtonVisible(false);

        //reset toggle to off
        foreach (Toggle toggle in optionToggles)
        {
            toggle.isOn = false; // Set each toggle to off
        }
    }

    private IEnumerator CaptureAfterPromptConfirmation()
    {
        yield return null;

        if (vlmCamCapture == null)
        {
            vlmCamCapture = FindObjectOfType<CamCapture>();
        }

        if (vlmCamCapture == null)
        {
            Debug.LogError("[UIManager] Unable to find CamCapture for VLM flow.");
            yield break;
        }

        Debug.Log("[UIManager] Prompt confirmed. Triggering VLM capture.");
        vlmCamCapture.CaptureAndProcessImage();
    }

    /// <summary>
    /// Clones the preset-message template once per entry in <see cref="presetVoiceMessages"/>
    /// and wires each clone to send that fixed message straight to TTS (the "OR" quick-send option).
    /// </summary>
    private void BuildPresetVoiceMessageButtons()
    {
        if (presetVoiceMessagesBuilt)
            return;

        if (presetVoiceMessageTemplate != null)
            BuildPresetButtonsFromTemplate();
        else if (autoGeneratePresetButtons)
            BuildSignalUIProgrammatically();
        else
        {
            Debug.Log("[UIManager] No preset voice message template assigned and auto-generation disabled; skipping preset buttons.");
            return;
        }

        presetVoiceMessagesBuilt = true;
    }

    // Clone an Inspector-assigned button so the presets match a custom style.
    private void BuildPresetButtonsFromTemplate()
    {
        Transform parent = presetVoiceMessageContainer != null
            ? presetVoiceMessageContainer
            : presetVoiceMessageTemplate.transform.parent;

        // The template itself is only a blueprint, never an interactive button.
        presetVoiceMessageTemplate.gameObject.SetActive(false);

        foreach (string presetMessage in presetVoiceMessages)
        {
            if (string.IsNullOrWhiteSpace(presetMessage))
                continue;

            Button presetButton = Instantiate(presetVoiceMessageTemplate, parent);
            presetButton.gameObject.name = "PresetVoiceButton (" + presetMessage + ")";
            presetButton.gameObject.SetActive(true);

            TMP_Text label = presetButton.GetComponentInChildren<TMP_Text>(true);
            if (label != null)
                label.text = presetMessage;

            string capturedMessage = presetMessage; // avoid closure over the loop variable
            presetButton.onClick.RemoveAllListeners();
            presetButton.onClick.AddListener(() => SendPresetVoiceMessage(capturedMessage));
        }
    }

    // Build the whole signal UI from code (quick messages + collapsible detailed options) with an
    // automatic layout, and switch off the scene's hard-positioned UI so nothing is hand-placed.
    private void BuildSignalUIProgrammatically()
    {
        // Parent to the Canvas (not the awkwardly-scaled pop-up) so screen positioning is predictable.
        Canvas canvas = FindParentCanvas(popUpWindow != null ? popUpWindow.transform : transform);
        Transform parent = canvas != null ? canvas.transform : (popUpWindow != null ? popUpWindow.transform : transform);

        // --- Root panel: centered, dark backdrop, auto vertical layout that sizes to its content ---
        var root = new GameObject("GeneratedSignalPanel",
            typeof(RectTransform), typeof(Image), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
        root.transform.SetParent(parent, false);
        presetPanelInstance = root;

        RectTransform rootRect = root.GetComponent<RectTransform>();
        rootRect.anchorMin = new Vector2(0.5f, 0.5f);
        rootRect.anchorMax = new Vector2(0.5f, 0.5f);
        rootRect.pivot = new Vector2(0.5f, 0.5f);
        rootRect.sizeDelta = new Vector2(presetPanelWidth, 0f);
        rootRect.anchoredPosition = Vector2.zero;

        root.GetComponent<Image>().color = presetPanelBackdropColor;

        var rootLayout = root.GetComponent<VerticalLayoutGroup>();
        rootLayout.padding = new RectOffset(24, 24, 22, 22);
        rootLayout.spacing = presetButtonSpacing;
        rootLayout.childAlignment = TextAnchor.UpperCenter;
        rootLayout.childControlWidth = true;
        rootLayout.childControlHeight = true;
        rootLayout.childForceExpandWidth = true;
        rootLayout.childForceExpandHeight = false;

        var rootFitter = root.GetComponent<ContentSizeFitter>();
        rootFitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
        rootFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        // --- Primary: quick messages ---
        CreatePresetLabel(root.transform, "Send a quick message", presetHeaderFontSize, presetTextColor);

        foreach (string presetMessage in presetVoiceMessages)
        {
            if (string.IsNullOrWhiteSpace(presetMessage))
                continue;

            string capturedMessage = presetMessage;
            Button quickButton = CreatePresetButtonObject(root.transform, presetMessage, presetButtonColor);
            quickButton.onClick.AddListener(() => SendPresetVoiceMessage(capturedMessage));
        }

        // --- Expander: reveals the detailed (LLM) options only on demand ---
        Button expandButton = CreatePresetButtonObject(root.transform, CollapsedExpanderText, presetExpanderColor);
        detailedExpanderLabel = expandButton.GetComponentInChildren<TextMeshProUGUI>(true);
        expandButton.onClick.AddListener(ToggleDetailedSection);

        // --- Collapsible detailed section (checkboxes + Confirm), hidden by default ---
        var detailed = new GameObject("DetailedSignalOptions", typeof(RectTransform), typeof(VerticalLayoutGroup));
        detailed.transform.SetParent(root.transform, false);
        detailedSectionInstance = detailed;

        var detailedLayout = detailed.GetComponent<VerticalLayoutGroup>();
        detailedLayout.padding = new RectOffset(0, 0, 8, 0);
        detailedLayout.spacing = presetButtonSpacing;
        detailedLayout.childAlignment = TextAnchor.UpperCenter;
        detailedLayout.childControlWidth = true;
        detailedLayout.childControlHeight = true;
        detailedLayout.childForceExpandWidth = true;
        detailedLayout.childForceExpandHeight = false;

        CreatePresetLabel(detailed.transform, "Select what the robot should convey:", presetButtonFontSize, presetTextColor);

        generatedToggles.Clear();
        foreach (string option in DetailedSignalOptions)
            generatedToggles.Add(CreatePresetToggle(detailed.transform, option));

        // Reroute the existing VLM confirm logic to our generated toggles.
        if (optionToggles == null)
            optionToggles = new List<Toggle>();
        optionToggles.Clear();
        optionToggles.AddRange(generatedToggles);

        Button confirmButton = CreatePresetButtonObject(detailed.transform, "Confirm", presetConfirmColor);
        confirmButton.onClick.AddListener(OnConfirmButtonPressed);

        detailed.SetActive(false); // collapsed until the user expands it

        // --- Turn off the old hard-positioned scene UI inside the pop-up ---
        if (popUpWindow != null)
        {
            foreach (Transform child in popUpWindow.transform)
                child.gameObject.SetActive(false);
        }

        // It lives on the Canvas now, so match the pop-up's visibility explicitly.
        root.SetActive(popUpWindow != null && popUpWindow.activeInHierarchy);
    }

    private void ToggleDetailedSection()
    {
        if (detailedSectionInstance == null)
            return;

        bool show = !detailedSectionInstance.activeSelf;
        detailedSectionInstance.SetActive(show);
        if (detailedExpanderLabel != null)
            detailedExpanderLabel.text = show ? ExpandedExpanderText : CollapsedExpanderText;
    }

    private void CollapseDetailedSection()
    {
        if (detailedSectionInstance != null)
            detailedSectionInstance.SetActive(false);
        if (detailedExpanderLabel != null)
            detailedExpanderLabel.text = CollapsedExpanderText;
    }

    private static Canvas FindParentCanvas(Transform start)
    {
        for (Transform current = start; current != null; current = current.parent)
        {
            Canvas canvas = current.GetComponent<Canvas>();
            if (canvas != null)
                return canvas;
        }
        return null;
    }

    private Button CreatePresetButtonObject(Transform parent, string text, Color bgColor)
    {
        var buttonObj = new GameObject("Button (" + text + ")",
            typeof(RectTransform), typeof(Image), typeof(Button));
        buttonObj.transform.SetParent(parent, false);

        Image image = buttonObj.GetComponent<Image>();
        image.color = bgColor;

        Button button = buttonObj.GetComponent<Button>();
        button.targetGraphic = image;

        LayoutElement layoutElement = buttonObj.AddComponent<LayoutElement>();
        layoutElement.preferredHeight = presetButtonSize.y;
        layoutElement.minHeight = presetButtonSize.y;

        CreatePresetLabel(buttonObj.transform, text, presetButtonFontSize, presetTextColor);
        return button;
    }

    // Builds a checkbox-style Toggle from scratch; its label is read by OnConfirmButtonPressed.
    private Toggle CreatePresetToggle(Transform parent, string text)
    {
        var row = new GameObject("Toggle (" + text + ")",
            typeof(RectTransform), typeof(Toggle), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
        row.transform.SetParent(parent, false);

        var rowLayoutElement = row.GetComponent<LayoutElement>();
        rowLayoutElement.minHeight = presetButtonSize.y;
        rowLayoutElement.preferredHeight = presetButtonSize.y;

        var rowLayout = row.GetComponent<HorizontalLayoutGroup>();
        rowLayout.spacing = 12f;
        rowLayout.padding = new RectOffset(10, 10, 4, 4);
        rowLayout.childAlignment = TextAnchor.MiddleLeft;
        rowLayout.childControlWidth = true;
        rowLayout.childControlHeight = true;
        rowLayout.childForceExpandWidth = false;
        rowLayout.childForceExpandHeight = false;

        // Checkbox box
        var box = new GameObject("Box", typeof(RectTransform), typeof(Image), typeof(LayoutElement));
        box.transform.SetParent(row.transform, false);
        Image boxImage = box.GetComponent<Image>();
        boxImage.color = new Color(1f, 1f, 1f, 0.9f);
        var boxLayoutElement = box.GetComponent<LayoutElement>();
        boxLayoutElement.minWidth = 30f;
        boxLayoutElement.preferredWidth = 30f;
        boxLayoutElement.minHeight = 30f;
        boxLayoutElement.preferredHeight = 30f;

        // Checkmark (Toggle shows/hides this with isOn)
        var checkmark = new GameObject("Checkmark", typeof(RectTransform), typeof(Image));
        checkmark.transform.SetParent(box.transform, false);
        RectTransform checkRect = checkmark.GetComponent<RectTransform>();
        checkRect.anchorMin = new Vector2(0.18f, 0.18f);
        checkRect.anchorMax = new Vector2(0.82f, 0.82f);
        checkRect.offsetMin = Vector2.zero;
        checkRect.offsetMax = Vector2.zero;
        Image checkImage = checkmark.GetComponent<Image>();
        checkImage.color = presetButtonColor;

        // Label
        var labelObj = new GameObject("Label", typeof(RectTransform), typeof(LayoutElement));
        labelObj.transform.SetParent(row.transform, false);
        TextMeshProUGUI label = labelObj.AddComponent<TextMeshProUGUI>();
        label.text = text;
        label.color = presetTextColor;
        label.alignment = TextAlignmentOptions.Left;
        label.fontSize = presetButtonFontSize;
        labelObj.GetComponent<LayoutElement>().flexibleWidth = 1f;

        Toggle toggle = row.GetComponent<Toggle>();
        toggle.targetGraphic = boxImage;
        toggle.graphic = checkImage;
        toggle.isOn = false;

        return toggle;
    }

    private void CreatePresetLabel(Transform parent, string text, float fontSize, Color color)
    {
        var textObj = new GameObject("Label", typeof(RectTransform));
        textObj.transform.SetParent(parent, false);

        RectTransform textRect = textObj.GetComponent<RectTransform>();
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = Vector2.zero;
        textRect.offsetMax = Vector2.zero;

        TextMeshProUGUI label = textObj.AddComponent<TextMeshProUGUI>();
        label.text = text;
        label.color = color;
        label.alignment = TextAlignmentOptions.Center;
        label.fontSize = fontSize;
    }

    /// <summary>
    /// Pre-generates and caches each preset clip so the first press plays without network latency.
    /// </summary>
    public void PrewarmPresetVoiceMessages()
    {
        if (ttsManager == null)
            return;

        foreach (string presetMessage in presetVoiceMessages)
        {
            if (!string.IsNullOrWhiteSpace(presetMessage))
                ttsManager.ConvertTextToSpeech(presetMessage); // generates + caches, does not auto-play
        }
    }

    /// <summary>
    /// "OR" quick-send path: speak a fixed preset message via TTS without running the VLM,
    /// record it like a normal response, and close the signal flow.
    /// </summary>
    public void SendPresetVoiceMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        isAwaitingVlmResponse = false;
        prompt = string.Empty;
        finalResponse = message;

        if (popUpWindow != null)
            popUpWindow.SetActive(false);

        if (SessionReview.SessionReviewManager.Instance != null)
            SessionReview.SessionReviewManager.Instance.AttachVLMReplayResponse(message);

        if (ttsManager != null)
            ttsManager.PlaySpeech(message);
        else
            Debug.LogError("[UIManager] TTSManager missing; cannot play preset voice message.");

        // Close the response/signal flow so the orchestrator advances, mirroring the VLM path.
        ExitResponseWindow();
    }

    public void ExitResponseWindow()
    {
        isAwaitingVlmResponse = false;
        responseWindow.SetActive(false);
        popUpWindow.SetActive(false);
        IsVlmSignalFlowActive = false;
        HideAllSignalButtons();
        SetVlmCaptureButtonVisible(false);
        ResponseWindowClosed?.Invoke();

        if (UnpauseOnExitResponseWindow && PauseManager.Instance != null)
        {
            PauseManager.Instance.UnpauseGame();
        }
    }

    /// <summary>
    /// Starts the replay camera positioning mode - allows user to find a spot before replay starts
    /// </summary>
    public void OnReplayButtonPressed()
    {
        // Get the main camera
        mainCamera = Camera.main;
        if (mainCamera == null)
        {
            Debug.LogError("Main camera not found!");
            return;
        }

        // Pause the game
        if (PauseManager.Instance != null)
        {
            PauseManager.Instance.PauseGame();
        }

        // Create temporary camera and enter positioning mode
        CreateTempReplayCamera();
        isPositioningCamera = true;
        isReplaying = false;

        // Show instructions to the user
        ShowReplayInstructions("Position the camera to find a good viewing spot.\n\nWASD - Move | Right-Click - Look Around\n\nPress ENTER to start replay");
        
        Debug.Log("Positioning replay camera. Press ENTER when ready to start replay.");
    }

    /// <summary>
    /// Starts the actual replay after the user has positioned the camera
    /// </summary>
    void StartReplayFromCurrentPosition()
    {
        isPositioningCamera = false;
        isReplaying = false;

        // The old Ultimate Replay-backed rerun system has been retired.
        // Keep the camera positioning flow only as a temporary inspection view.
        ShowReplayInstructions("Legacy replay is disabled.\n\nPress SPACE to return to the main camera");

        if (PauseManager.Instance != null)
        {
            PauseManager.Instance.PauseGame();
        }

        StartCoroutine(SwitchBackToMainCamera(0f));

        Debug.LogWarning("Replay requested, but the legacy RerunManager path is disabled.");
    }

    /// <summary>
    /// Shows or updates replay instruction text
    /// </summary>
    void ShowReplayInstructions(string message, bool showConfirmButton = false)
    {
        if (instructionPanel != null)
        {
            instructionPanel.SetActive(true);
        }

        if (instructionInputField != null)
        {
            instructionInputField.text = message;
            instructionInputField.interactable = false; // Make it read-only for instructions
        }

        // Show/hide the confirm button based on parameter
        if (confirmBN != null)
        {
            confirmBN.gameObject.SetActive(showConfirmButton);
        }
    }

    /// <summary>
    /// Hides replay instruction UI
    /// </summary>
    void HideReplayInstructions()
    {
        if (instructionPanel != null)
        {
            instructionPanel.SetActive(false);
        }
    }

    void HideAllVlmWindows()
    {
        IsVlmSignalFlowActive = false;

        if (popUpWindow != null)
        {
            popUpWindow.SetActive(false);
        }

        if (responseWindow != null)
        {
            responseWindow.SetActive(false);
        }

        SetVlmCaptureButtonVisible(false);
    }

    void HideAllSignalButtons()
    {
        SetManagedSignalButtonsVisible(false);
    }

    void SetManagedSignalButtonsVisible(bool isVisible)
    {
        foreach (GameObject item in signalButtons)
        {
            if (item != null)
            {
                item.SetActive(isVisible);
            }
        }
    }

    void SetVlmCaptureButtonVisible(bool isVisible)
    {
        if (vlmCaptureButton != null)
        {
            vlmCaptureButton.gameObject.SetActive(isVisible);
        }
    }

    void CreateTempReplayCamera()
    {
        // Clean up any existing temp camera
        if (tempReplayCamera != null)
        {
            Destroy(tempReplayCamera.gameObject);
        }

        // Create new camera GameObject
        GameObject tempCamObj = new GameObject("TempReplayCamera");
        tempReplayCamera = tempCamObj.AddComponent<Camera>();

        // Position it 5 meters ahead of the main camera in XZ plane, but Y is fixed at 0
        Vector3 forwardXZ = new Vector3(mainCamera.transform.forward.x, 0, mainCamera.transform.forward.z).normalized;
        Vector3 offsetPosition = mainCamera.transform.position + (forwardXZ * 5f);
        offsetPosition.y = 0f; // Fix Y at 0
        
        tempReplayCamera.transform.position = offsetPosition;

        // Make it look at the main camera's position (at ground level)
        Vector3 lookTarget = new Vector3(mainCamera.transform.position.x, 0f, mainCamera.transform.position.z);
        tempReplayCamera.transform.LookAt(lookTarget);

        // Copy camera settings from main camera
        tempReplayCamera.fieldOfView = mainCamera.fieldOfView;
        tempReplayCamera.nearClipPlane = mainCamera.nearClipPlane;
        tempReplayCamera.farClipPlane = mainCamera.farClipPlane;

        // Enable the temp camera and disable the main camera
        mainCamera.enabled = false;
        tempReplayCamera.enabled = true;

        isUsingTempCamera = true;

        Debug.Log("Temporary replay camera created at position: " + offsetPosition);
    }

    IEnumerator SwitchBackToMainCamera(float duration)
    {
        // Wait for the replay duration
        yield return new WaitForSeconds(duration);

        isReplaying = false;

        // Pause the game and prompt user to take over
        if (PauseManager.Instance != null)
        {
            PauseManager.Instance.PauseGame();
        }

        // Show take over prompt
        ShowReplayInstructions("Replay finished!\n\nPress SPACE to take over and return to main camera");

        // Wait for user to press space to take over
        while (!Input.GetKeyDown(KeyCode.Space))
        {
            yield return null;
        }

        // Switch back to main camera
        if (mainCamera != null)
        {
            mainCamera.enabled = true;
        }

        // Destroy temp camera
        if (tempReplayCamera != null)
        {
            Destroy(tempReplayCamera.gameObject);
            tempReplayCamera = null;
        }

        isUsingTempCamera = false;
        isPositioningCamera = false;

        // Hide instructions
        HideReplayInstructions();

        // Unpause the game
        if (PauseManager.Instance != null)
        {
            PauseManager.Instance.UnpauseGame();
        }

        Debug.Log("User took over. Switched back to main camera.");
    }

}
