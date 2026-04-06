using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;

public class CamCapture : MonoBehaviour
{
    public Camera cam;
    public Button captureButton;
    public FreeCamera freeCamera; // Reference to movement data

    private int imageIndex = 0; // Counter for naming images

    private string savedFilePath;
    private string customPrompt;
    public UIManager uiManager;
    private bool captureButtonOverride;

    private string openAI_API_Key;

    private void Awake()
    {
        // Reads the API key from a file called "openai_key.txt" in the persistent data path
        string keyPath = Path.Combine(Application.persistentDataPath, "openaiapikey.txt");
        if (File.Exists(keyPath))
        {
            openAI_API_Key = File.ReadAllText(keyPath).Trim();
        }
        else
        {
            openAI_API_Key = "";
        }
    } 
    void Start()
    {
        if (cam == null)
        {
            cam = GetComponent<Camera>();
        }

        RefreshCaptureButtonVisibility();
    }

    void Update()
    {
        RefreshCaptureButtonVisibility();
    }

    public void CaptureAndProcessImage()
    {
        if (!CanShowCaptureButton())
            return;

        captureButtonOverride = false;
        RefreshCaptureButtonVisibility();

        Texture2D image = CaptureImage();
        string filePath = SaveImage(image);
        Debug.Log("Image captured and saved at: " + filePath);

        StartCoroutine(UploadImageToOpenAI(filePath)); // Upload image after capturing
    }

    public void CaptureAndSaveImage()
    {
        if (!CanShowCaptureButton())
            return;

        captureButtonOverride = false;
        RefreshCaptureButtonVisibility();

        Texture2D image = CaptureImage();
        savedFilePath = SaveImage(image); // Save the image and store the file path
        Debug.Log("Image captured and saved at: " + savedFilePath);
    }

    public void SendToOpenAI()
    {
        if (!string.IsNullOrEmpty(savedFilePath) && !string.IsNullOrEmpty(customPrompt))
        {
            StartCoroutine(UploadImageToOpenAI(savedFilePath));
        }
        else
        {
            Debug.LogError("No image or prompt available.");
        }
    }

    Texture2D CaptureImage()
    {
        RenderTexture renderTexture = new RenderTexture(cam.pixelWidth, cam.pixelHeight, 24);
        cam.targetTexture = renderTexture;
        cam.Render();

        RenderTexture.active = renderTexture;
        Texture2D image = new Texture2D(cam.pixelWidth, cam.pixelHeight, TextureFormat.RGB24, false);
        image.ReadPixels(new Rect(0, 0, cam.pixelWidth, cam.pixelHeight), 0, 0);
        image.Apply();

        cam.targetTexture = null;
        RenderTexture.active = null;
        Destroy(renderTexture);

        return image;
    }

    string SaveImage(Texture2D image)
    {
        string folderPath = Application.persistentDataPath + "/CapturedImages";

        // Ensure directory exists
        if (!Directory.Exists(folderPath))
        {
            Directory.CreateDirectory(folderPath);
        }

        // Generate unique filename
        string dateTime = System.DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        string fileName = $"image_{dateTime}_{imageIndex}.png";
        string fullPath = Path.Combine(folderPath, fileName);


        // Save image as PNG
        byte[] bytes = image.EncodeToPNG();
        File.WriteAllBytes(fullPath, bytes);
        Debug.Log("Saved image: " + fullPath);

        imageIndex++; // Increment image index for next save

        return fullPath;
    }

    // Upload the image to OpenAI file API
    IEnumerator UploadImageToOpenAI(string filePath)
    {
        Debug.Log("Uploading image to OpenAI...");

        // Read image as bytes
        byte[] imageBytes = File.ReadAllBytes(filePath);

        // Create a multipart form data request
        WWWForm form = new WWWForm();
        form.AddBinaryData("file", imageBytes, "image.png", "image/png");

        // Set the 'purpose' field to 'vision' for image analysis
        form.AddField("purpose", "vision");  // This is for analyzing images

        using (UnityWebRequest request = UnityWebRequest.Post("https://api.openai.com/v1/files", form))
        {
            request.SetRequestHeader("Authorization", "Bearer " + openAI_API_Key);

            yield return request.SendWebRequest();

            if (request.result == UnityWebRequest.Result.Success)
            {
                Debug.Log("Image uploaded successfully. Response: " + request.downloadHandler.text);
                // Parse the file ID from the response to use in the next step
                var jsonResponse = JsonUtility.FromJson<UploadResponse>(request.downloadHandler.text);
                string fileId = jsonResponse.id;
                // Now send the fileId to use in the analysis task
                StartCoroutine(SendImageFileToOpenAI(fileId, Path.GetFileName(filePath))); // Analyze the image using the fileId
            }
            else
            {
                Debug.LogError("Error uploading image: " + request.error + "\nResponse: " + request.downloadHandler.text);
            }
        }
    }


    // Analyze the uploaded image using the file ID
    IEnumerator SendImageFileToOpenAI(string fileId, string imageName)
    {
        Debug.Log("Sending image file to OpenAI...");

        string movementStatus = freeCamera.GetMovementStatus();
        Debug.Log(movementStatus);
        customPrompt = uiManager.GetPrompt();

        string jsonData = "{\"model\": \"gpt-4o-mini\", \"messages\": [{\"role\": \"user\", \"content\": \"" + customPrompt + " Movement Status: " + movementStatus + "\", \"type\": \"file\", \"file\": \"file:" + fileId + "\"}]}";

        using (UnityWebRequest request = new UnityWebRequest("https://api.openai.com/v1/chat/completions", "POST"))
        {
            byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(jsonData);
            request.uploadHandler = new UploadHandlerRaw(bodyRaw);
            request.downloadHandler = new DownloadHandlerBuffer();

            request.SetRequestHeader("Authorization", "Bearer " + openAI_API_Key);
            request.SetRequestHeader("Content-Type", "application/json");

            yield return request.SendWebRequest();

            if (request.result == UnityWebRequest.Result.Success)
            {
                string response = request.downloadHandler.text;
                Debug.Log("OpenAI Response: " + request.downloadHandler.text);

                // NEW: Parse and extract the actual message content
                string messageContent = ParseOpenAIResponse(response);

                // NEW: Display the response in the editable UI field
                if (uiManager != null)
                {
                    uiManager.DisplayLLMResponse(messageContent);
                }

                // Save the response to a log file
                SaveResponseToLog(response, imageName, movementStatus);
            }
            else
            {
                Debug.LogError("Error sending image: " + request.error + "\nResponse: " + request.downloadHandler.text);
            }
        }
    }

    string ParseOpenAIResponse(string jsonResponse)
    {
        try
        {
            Debug.Log("Attempting to parse response...");

            // Find the `"content":` key (allowing spaces after colon)
            int keyIndex = jsonResponse.IndexOf("\"content\":");
            if (keyIndex == -1)
            {
                Debug.LogError("Could not find 'content' field in response");
                return jsonResponse; // Fallback: return whole JSON
            }

            // Move to the character after `"content":`
            int i = keyIndex + "\"content\":".Length;

            // Skip whitespace
            while (i < jsonResponse.Length && char.IsWhiteSpace(jsonResponse[i]))
            {
                i++;
            }

            // Expect starting quote for the string
            if (i >= jsonResponse.Length || jsonResponse[i] != '\"')
            {
                Debug.LogError("Content field is not a JSON string");
                return jsonResponse;
            }

            int contentStart = i + 1;

            // Find the closing quote, handling escape sequences
            int contentEnd = contentStart;
            bool isEscaped = false;

            for (int j = contentStart; j < jsonResponse.Length; j++)
            {
                char c = jsonResponse[j];

                if (c == '\\' && !isEscaped)
                {
                    isEscaped = true;
                    continue;
                }

                if (c == '\"' && !isEscaped)
                {
                    contentEnd = j;
                    break;
                }

                isEscaped = false;
            }

            if (contentEnd <= contentStart)
            {
                Debug.LogError("Failed to locate end of content string");
                return jsonResponse;
            }

            string content = jsonResponse.Substring(contentStart, contentEnd - contentStart);

            // Unescape common sequences
            content = content
                .Replace("\\n", "\n")
                .Replace("\\r", "\r")
                .Replace("\\\"", "\"")
                .Replace("\\\\", "\\");

            Debug.Log("Successfully parsed content: " + content);
            return content;
        }
        catch (System.Exception e)
        {
            Debug.LogError("Error parsing OpenAI response: " + e.Message);
            Debug.LogError("Full response: " + jsonResponse);
            return "Error parsing response. Check console for details.";
        }
    }

    // Save responses to a log file
    void SaveResponseToLog(string response, string imageName, string movementStatus)
    {
        string logFilePath = Application.persistentDataPath + "/ResponseLog.txt";

        // Append the response to the log file
        using (StreamWriter writer = new StreamWriter(logFilePath, true))
        {
            writer.WriteLine("Timestamp: " + System.DateTime.Now);
            writer.WriteLine("Image Name: " + imageName);
            writer.WriteLine("Movement Status: " + movementStatus);
            writer.WriteLine("User Prompt: " + customPrompt);
            writer.WriteLine("Response: " + response);
            writer.WriteLine("----------");
        }

        Debug.Log("Response saved to log file: " + logFilePath);
    }

    void RefreshCaptureButtonVisibility()
    {
        if (captureButton == null)
            return;

        bool shouldShow = CanShowCaptureButton();
        if (captureButton.gameObject.activeSelf != shouldShow)
            captureButton.gameObject.SetActive(shouldShow);
    }

    bool CanShowCaptureButton()
    {
        if (captureButtonOverride)
            return true;

        if (uiManager != null && !uiManager.IsVlmSignalFlowActive)
            return false;

        var reviewManager = SessionReview.SessionReviewManager.Instance;
        if (reviewManager != null)
            return reviewManager.IsLiveTrialRunning;

        return true;
    }

    public void SetCaptureButtonOverride(bool isVisible)
    {
        captureButtonOverride = isVisible;
        RefreshCaptureButtonVisibility();
    }
}


// Create a class to parse the response (JSON) from the file upload API
[System.Serializable]
public class UploadResponse
{
    public string id;
    public string objectType;
    public string created;
}
