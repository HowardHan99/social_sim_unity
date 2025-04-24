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
    //public CamCapture camCapture; // Reference to CamCapture.cs

    private string prompt; // Store the generated prompt string

    float elapsedTime = 0f;

    void Update()
    {
        elapsedTime += Time.unscaledDeltaTime; // Ensures the UI works even when the game is paused
    }


    public string GetPrompt() // Allow other scripts to access the prompt
    {
        //OnConfirmButtonPressed();
        Debug.Log(prompt);
        promptDisplay.text = prompt;
        return prompt;
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
        //promptDisplay.text = "Generated Prompt:\n" + prompt; // Show the prompt on UI
    }

    string GeneratePromptString(List<string> options)
    {
        string basePrompt = "The delivery robot is navigating on a sidewalk. The image is a real-time capture from the robot's perspective. The robot signal message should be intuitive, clear, and brief to prevent accidents and guarantee pedestrian safety.";

        if (options.Count > 0)
        {
            basePrompt += " The signal message should include the following information: ";
            basePrompt += string.Join(", ", options) + ".";
        }

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
        //yield return new WaitForSeconds(5f);
        yield return null;
        popUpWindow.SetActive(false); // Show the pop-up window
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
