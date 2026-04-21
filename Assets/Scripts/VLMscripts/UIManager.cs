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
    }

    void Update()
    {
        elapsedTime += Time.unscaledDeltaTime; // Ensures the UI works even when the game is paused

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
        string basePrompt = "The delivery robot is navigating on a sidewalk. The image is a real-time capture from the robot's perspective. Generate a brief, natural, and friendly spoken message that the robot would say to nearby pedestrians. The message should be conversational and intuitive to prevent accidents and guarantee pedestrian safety.";

        if (options.Count > 0)
        {
            basePrompt += " The message should include the following information: ";
            basePrompt += string.Join(", ", options) + ".";
        }

        basePrompt += " Keep it under 30 words and speak naturally as if the robot is politely communicating with people around it.";

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
