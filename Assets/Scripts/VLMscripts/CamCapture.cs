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

    private string openAI_API_Key = "";

    void Start()
    {
        if (cam == null)
        {
            cam = GetComponent<Camera>();
        }
        
        // Load API key from file
        try
        {
            string filePath = Path.Combine(Application.dataPath, "openaiapikey.txt");
            if (File.Exists(filePath))
            {
                openAI_API_Key = File.ReadAllText(filePath).Trim();
                Debug.Log("API key loaded successfully");
            }
            else
            {
                Debug.LogError("API key file not found at: " + filePath);
            }
        }
        catch (System.Exception e)
        {
            Debug.LogError("Error loading API key: " + e.Message);
        }
    }

    public void CaptureAndProcessImage()
    {
        Texture2D image = CaptureImage();
        string filePath = SaveImage(image);
        Debug.Log("Image captured and saved at: " + filePath);

        StartCoroutine(UploadImageToOpenAI(filePath)); // Upload image after capturing
    }

    public void CaptureAndSaveImage()
    {
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
        //string jsonData = "{\"model\": \"gpt-4o-mini\", \"messages\": [{\"role\": \"user\", \"content\": \"The robot is moving. " + movementStatus + ". Based on this sidewalk scene, generate a simple and intuitive safety signal for pedestrians with limited mobility.\"}], \"type\": \"file\", \"file\": \"file:" + fileId + "\"}";


        // Create the request body with file_id from the upload response
        //string jsonData = "{\"model\": \"gpt-4o-mini\", \"messages\": [{\"role\": \"user\", \"content\": \"The robot is moving" + movementStatus + "Based on this sidewalk scene captured from the delievery robot perspective, play the robots' role and generate a simple and intuitive safety reminding message as the robot signaling for pedestrians with limited mobility to prevent any accident based on the scenario.\", \"type\": \"file\", \"file\": \"file:" + fileId + "\"}]}";
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

                // Save the response to a log file
                SaveResponseToLog(response, imageName, movementStatus);
            }
            else
            {
                Debug.LogError("Error sending image: " + request.error + "\nResponse: " + request.downloadHandler.text);
            }
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
}


// Create a class to parse the response (JSON) from the file upload API
[System.Serializable]
public class UploadResponse
{
    public string id;
    public string objectType;
    public string created;
}