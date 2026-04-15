using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;

public class CamCapture : MonoBehaviour
{
    private const string GeminiVisionModel = "gemini-2.0-flash";
    private const string GeminiApiBaseUrl = "https://generativelanguage.googleapis.com/v1beta/models/";

    public Camera cam;
    public Button captureButton;
    public FreeCamera freeCamera;

    private int imageIndex;
    private string savedFilePath;
    private string customPrompt;
    public UIManager uiManager;
    private bool captureButtonOverride;
    private bool isVlmRequestInFlight;
    private float lastCaptureRequestTime = -10f;

    private const float DuplicateCaptureCooldownSeconds = 1f;

    private string geminiApiKey;

    private void Awake()
    {
        geminiApiKey = GeminiApiKeyLoader.Load();
    }

    private void Start()
    {
        if (cam == null)
            cam = GetComponent<Camera>();

        RefreshCaptureButtonVisibility();
    }

    private void Update()
    {
        RefreshCaptureButtonVisibility();
    }

    public void CaptureAndProcessImage()
    {
        if (Time.unscaledTime - lastCaptureRequestTime < DuplicateCaptureCooldownSeconds)
        {
            Debug.LogWarning("[CamCapture] Ignoring duplicate VLM capture triggered too quickly.");
            return;
        }

        if (isVlmRequestInFlight)
        {
            Debug.LogWarning("[CamCapture] Ignoring duplicate VLM capture while a request is already in flight.");
            return;
        }

        if (!CanShowCaptureButton())
            return;

        captureButtonOverride = false;
        RefreshCaptureButtonVisibility();
        lastCaptureRequestTime = Time.unscaledTime;

        Texture2D image = CaptureImage();
        string filePath = SaveImage(image);
        Debug.Log("[CamCapture] Image captured and saved at: " + filePath);

        StartCoroutine(SendImageToGemini(filePath));
    }

    public void CaptureAndSaveImage()
    {
        if (!CanShowCaptureButton())
            return;

        captureButtonOverride = false;
        RefreshCaptureButtonVisibility();

        Texture2D image = CaptureImage();
        savedFilePath = SaveImage(image);
        Debug.Log("[CamCapture] Image captured and saved at: " + savedFilePath);
    }

    public void SendToOpenAI()
    {
        if (isVlmRequestInFlight)
        {
            Debug.LogWarning("[CamCapture] Ignoring duplicate VLM send while a request is already in flight.");
            return;
        }

        if (uiManager != null)
            customPrompt = uiManager.GetPrompt();

        if (!string.IsNullOrEmpty(savedFilePath) && !string.IsNullOrEmpty(customPrompt))
        {
            StartCoroutine(SendImageToGemini(savedFilePath));
        }
        else
        {
            Debug.LogError("[CamCapture] No image or prompt available.");
        }
    }

    private Texture2D CaptureImage()
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

    private string SaveImage(Texture2D image)
    {
        string folderPath = Path.Combine(Application.persistentDataPath, "CapturedImages");
        if (!Directory.Exists(folderPath))
            Directory.CreateDirectory(folderPath);

        string dateTime = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        string fileName = $"image_{dateTime}_{imageIndex}.png";
        string fullPath = Path.Combine(folderPath, fileName);

        byte[] bytes = image.EncodeToPNG();
        File.WriteAllBytes(fullPath, bytes);
        Destroy(image);

        Debug.Log("[CamCapture] Saved image: " + fullPath);
        imageIndex++;

        return fullPath;
    }

    private IEnumerator SendImageToGemini(string filePath)
    {
        if (isVlmRequestInFlight)
        {
            Debug.LogWarning("[CamCapture] Duplicate Gemini VLM request suppressed.");
            yield break;
        }

        isVlmRequestInFlight = true;

        if (string.IsNullOrWhiteSpace(geminiApiKey))
        {
            Debug.LogError("[CamCapture] Gemini API key is missing.");
            isVlmRequestInFlight = false;
            yield break;
        }

        if (!File.Exists(filePath))
        {
            Debug.LogError("[CamCapture] Image file not found: " + filePath);
            isVlmRequestInFlight = false;
            yield break;
        }

        string movementStatus = freeCamera != null ? freeCamera.GetMovementStatus() : "Movement status unavailable.";
        customPrompt = uiManager != null ? uiManager.GetPrompt() : string.Empty;

        byte[] imageBytes = File.ReadAllBytes(filePath);
        string mimeType = GetMimeType(filePath);
        string requestJson = BuildGeminiVisionRequest(customPrompt, movementStatus, imageBytes, mimeType);
        string imageName = Path.GetFileName(filePath);
        string url = GeminiApiBaseUrl + GeminiVisionModel + ":generateContent?key=" + geminiApiKey;

        using (UnityWebRequest request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST))
        {
            byte[] bodyRaw = Encoding.UTF8.GetBytes(requestJson);
            request.uploadHandler = new UploadHandlerRaw(bodyRaw);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");

            yield return request.SendWebRequest();

            string rawResponse = request.downloadHandler != null ? request.downloadHandler.text : string.Empty;
            if (request.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError("[CamCapture] Gemini VLM request failed: " + request.error + "\nResponse: " + rawResponse);
                SaveResponseToLog(rawResponse, string.Empty, imageName, movementStatus, false);
                isVlmRequestInFlight = false;
                yield break;
            }

            string parsedResponse = ParseGeminiResponse(rawResponse);
            Debug.Log("[CamCapture] Gemini VLM response: " + parsedResponse);

            if (uiManager != null)
                uiManager.DisplayLLMResponse(parsedResponse);

            SaveResponseToLog(rawResponse, parsedResponse, imageName, movementStatus, true);
        }

        isVlmRequestInFlight = false;
    }

    private string BuildGeminiVisionRequest(string prompt, string movementStatus, byte[] imageBytes, string mimeType)
    {
        string escapedPrompt = EscapeJson(BuildPrompt(prompt, movementStatus));
        string base64Image = Convert.ToBase64String(imageBytes);

        return "{"
            + "\"contents\":[{"
            + "\"parts\":["
            + "{"
            + "\"text\":\"" + escapedPrompt + "\""
            + "},"
            + "{"
            + "\"inline_data\":{"
            + "\"mime_type\":\"" + mimeType + "\","
            + "\"data\":\"" + base64Image + "\""
            + "}"
            + "}"
            + "]"
            + "}]"
            + "}";
    }

    private string BuildPrompt(string prompt, string movementStatus)
    {
        string effectivePrompt = string.IsNullOrWhiteSpace(prompt)
            ? "Describe the scene briefly and generate a natural spoken safety message for nearby pedestrians."
            : prompt.Trim();

        return effectivePrompt + "\nMovement Status: " + movementStatus;
    }

    private string ParseGeminiResponse(string jsonResponse)
    {
        try
        {
            GeminiGenerateContentResponse response = JsonUtility.FromJson<GeminiGenerateContentResponse>(jsonResponse);
            if (response != null && response.candidates != null)
            {
                foreach (GeminiCandidate candidate in response.candidates)
                {
                    if (candidate == null || candidate.content == null || candidate.content.parts == null)
                        continue;

                    List<string> textParts = new List<string>();
                    foreach (GeminiPart part in candidate.content.parts)
                    {
                        if (!string.IsNullOrWhiteSpace(part.text))
                            textParts.Add(part.text.Trim());
                    }

                    if (textParts.Count > 0)
                        return string.Join("\n", textParts).Trim();
                }
            }

            GeminiErrorEnvelope errorEnvelope = JsonUtility.FromJson<GeminiErrorEnvelope>(jsonResponse);
            if (errorEnvelope != null && errorEnvelope.error != null && !string.IsNullOrWhiteSpace(errorEnvelope.error.message))
                return "Gemini error: " + errorEnvelope.error.message;
        }
        catch (Exception exception)
        {
            Debug.LogError("[CamCapture] Error parsing Gemini response: " + exception.Message);
        }

        return "Unable to parse Gemini response.";
    }

    private void SaveResponseToLog(string rawResponse, string parsedResponse, string imageName, string movementStatus, bool isSuccess)
    {
        string logFilePath = Path.Combine(Application.persistentDataPath, "ResponseLog.txt");

        using (StreamWriter writer = new StreamWriter(logFilePath, true))
        {
            writer.WriteLine("Timestamp: " + DateTime.Now.ToString("o"));
            writer.WriteLine("Provider: Google Gemini");
            writer.WriteLine("Model: " + GeminiVisionModel);
            writer.WriteLine("Success: " + isSuccess);
            writer.WriteLine("Image Name: " + imageName);
            writer.WriteLine("Movement Status: " + movementStatus);
            writer.WriteLine("User Prompt: " + customPrompt);
            writer.WriteLine("Parsed Response: " + parsedResponse);
            writer.WriteLine("Raw Response: " + rawResponse);
            writer.WriteLine("----------");
        }

        Debug.Log("[CamCapture] Response saved to log file: " + logFilePath);
    }

    private void RefreshCaptureButtonVisibility()
    {
        if (captureButton == null)
            return;

        bool shouldShow = CanShowCaptureButton();
        if (captureButton.gameObject.activeSelf != shouldShow)
            captureButton.gameObject.SetActive(shouldShow);
    }

    private bool CanShowCaptureButton()
    {
        if (captureButtonOverride)
            return true;

        if (uiManager != null)
        {
            if (uiManager.IsVlmSignalFlowActive)
                return true;

            return false;
        }

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

    private string GetMimeType(string filePath)
    {
        string extension = Path.GetExtension(filePath).ToLowerInvariant();
        switch (extension)
        {
            case ".jpg":
            case ".jpeg":
                return "image/jpeg";
            case ".png":
                return "image/png";
            case ".webp":
                return "image/webp";
            default:
                return "application/octet-stream";
        }
    }

    private string EscapeJson(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        return value
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\n", "\\n")
            .Replace("\r", "\\r")
            .Replace("\t", "\\t");
    }
}

[Serializable]
public class GeminiGenerateContentResponse
{
    public GeminiCandidate[] candidates;
}

[Serializable]
public class GeminiCandidate
{
    public GeminiContent content;
}

[Serializable]
public class GeminiContent
{
    public GeminiPart[] parts;
}

[Serializable]
public class GeminiPart
{
    public string text;
    public GeminiInlineData inline_data;
    public GeminiInlineData inlineData;
}

[Serializable]
public class GeminiInlineData
{
    public string mime_type;
    public string data;
}

[Serializable]
public class GeminiErrorEnvelope
{
    public GeminiError error;
}

[Serializable]
public class GeminiError
{
    public int code;
    public string message;
    public string status;
}

public static class GeminiApiKeyLoader
{
    private const string KeyFileName = "geminiapikey.txt";
    private const string GeminiApiEnvVar = "GEMINI_API_KEY";
    private const string DotEnvFileName = ".env";

    public static string Load()
    {
        string envKey = Environment.GetEnvironmentVariable(GeminiApiEnvVar);
        if (!string.IsNullOrWhiteSpace(envKey))
            return envKey.Trim();

        string[] dotEnvPaths =
        {
            Path.Combine(Application.dataPath, "Scripts", DotEnvFileName),
            Path.Combine(Application.dataPath, DotEnvFileName)
        };

        foreach (string dotEnvPath in dotEnvPaths)
        {
            string dotEnvKey = LoadFromDotEnv(dotEnvPath, GeminiApiEnvVar);
            if (!string.IsNullOrWhiteSpace(dotEnvKey))
                return dotEnvKey;
        }

        string[] candidatePaths =
        {
            Path.Combine(Application.persistentDataPath, KeyFileName),
            Path.Combine(Application.dataPath, "Scripts", "VLMscripts", KeyFileName),
            Path.Combine(Application.dataPath, "Resources", KeyFileName)
        };

        foreach (string path in candidatePaths)
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                string key = File.ReadAllText(path).Trim();
                if (!string.IsNullOrWhiteSpace(key))
                    return key;
            }
        }

        return string.Empty;
    }

    private static string LoadFromDotEnv(string path, string variableName)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return string.Empty;

        foreach (string rawLine in File.ReadAllLines(path))
        {
            if (string.IsNullOrWhiteSpace(rawLine))
                continue;

            string line = rawLine.Trim();
            if (line.StartsWith("#"))
                continue;

            int separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0)
                continue;

            string key = line.Substring(0, separatorIndex).Trim();
            if (!string.Equals(key, variableName, StringComparison.Ordinal))
                continue;

            string value = line.Substring(separatorIndex + 1).Trim();
            if (value.Length >= 2 &&
                ((value.StartsWith("\"") && value.EndsWith("\"")) ||
                 (value.StartsWith("'") && value.EndsWith("'"))))
            {
                value = value.Substring(1, value.Length - 2);
            }

            return value;
        }

        return string.Empty;
    }
}
