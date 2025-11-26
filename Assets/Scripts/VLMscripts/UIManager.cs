using UnityEngine;
using TMPro;
using System.Collections.Generic;
using UnityEngine.UI;
using System.Collections;

public class UIManager : MonoBehaviour
{
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

    float elapsedTime = 0f;

    void Start()
    {
        // Setup confirm button listener
        if (confirmResponseButton != null)
        {
            confirmResponseButton.onClick.AddListener(OnConfirmResponseButtonPressed);
        }
    }

    void Update()
    {
        elapsedTime += Time.unscaledDeltaTime; // Ensures the UI works even when the game is paused

        // Show signal buttons only when the game is paused
        if (PauseManager.Instance != null)
        {
            bool shouldShow = PauseManager.Instance.isPaused;
            foreach (GameObject item in signalButtons)
            {
                if (item.activeSelf != shouldShow)
                {
                    item.SetActive(shouldShow);
                }
            }
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
        if (responseInputField != null)
        {
            responseInputField.text = response;
            responseInputField.interactable = true; // Make sure it's editable

            // Select all text for easy editing
            responseInputField.Select();
            responseInputField.ActivateInputField();
        }
    }

    void OnConfirmResponseButtonPressed()
    {
        if (responseInputField != null && ttsManager != null)
        {
            finalResponse = responseInputField.text;
            Debug.Log("User confirmed response: " + finalResponse);

            // Convert to speech
            ttsManager.ConvertTextToSpeech(finalResponse);

            // Optional: Disable the confirm button to prevent multiple clicks
            confirmResponseButton.interactable = false;
            StartCoroutine(ReEnableConfirmButton());
        }
    }

    IEnumerator ReEnableConfirmButton()
    {
        yield return new WaitForSeconds(2f);
        confirmResponseButton.interactable = true;
    }

    public void OnConfirmButtonPressed()
    {
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

        StartCoroutine(HidePopUp());
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
        PauseManager.Instance.PauseGame();
        ShowPopUp();
    }

    public void ShowPopUp()
    {
        popUpWindow.SetActive(true); // Show the pop-up window
        //hide previous buttons
        foreach (GameObject item in signalButtons)
        {
            item.SetActive(false);
        }
    }


    private IEnumerator HidePopUp()
    {
        yield return null;
        popUpWindow.SetActive(false); // Hide the pop-up window
        responseWindow.SetActive(true);

        //show previous buttons
        foreach (GameObject item in signalButtons)
        {
            item.SetActive(true);
        }
        //reset toggle to off
        foreach (Toggle toggle in optionToggles)
        {
            toggle.isOn = false; // Set each toggle to off
        }
    }

    public void ExitResponseWindow()
    {
        responseWindow.SetActive(false);
        PauseManager.Instance.UnpauseGame();
    }

}