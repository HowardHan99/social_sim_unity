using UnityEngine;
using System.Collections;
using UnityEngine.Networking;
using System.IO;

public class TTSManager : MonoBehaviour
{
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

    private AudioClip savedAudioClip;

    // Call this method to convert text to speech
    public void ConvertTextToSpeech(string text)
    {
        StartCoroutine(GenerateTTS(text));
    }

    IEnumerator GenerateTTS(string text)
    {
        Debug.Log("Converting text to speech: " + text);

        // OpenAI TTS API endpoint
        string url = "https://api.openai.com/v1/audio/speech";

        // Create JSON request body
        string jsonData = "{\"model\": \"tts-1\", \"input\": \"" + EscapeJSON(text) + "\", \"voice\": \"alloy\"}";

        using (UnityWebRequest request = new UnityWebRequest(url, "POST"))
        {
            byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(jsonData);
            request.uploadHandler = new UploadHandlerRaw(bodyRaw);
            request.downloadHandler = new DownloadHandlerBuffer();

            request.SetRequestHeader("Authorization", "Bearer " + openAI_API_Key);
            request.SetRequestHeader("Content-Type", "application/json");

            yield return request.SendWebRequest();

            if (request.result == UnityWebRequest.Result.Success)
            {
                Debug.Log("TTS audio received successfully!");

                // Save the audio data
                byte[] audioData = request.downloadHandler.data;
                string audioPath = SaveAudioFile(audioData);

                // Load as AudioClip
                StartCoroutine(LoadAudioClip(audioPath));
            }
            else
            {
                Debug.LogError("Error generating TTS: " + request.error);
            }
        }
    }

    string SaveAudioFile(byte[] audioData)
    {
        string folderPath = Application.persistentDataPath + "/TTSAudio";

        if (!Directory.Exists(folderPath))
        {
            Directory.CreateDirectory(folderPath);
        }

        string dateTime = System.DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        string fileName = $"tts_audio_{dateTime}.mp3";
        string fullPath = Path.Combine(folderPath, fileName);

        File.WriteAllBytes(fullPath, audioData);
        Debug.Log("Audio saved at: " + fullPath);

        return fullPath;
    }

    IEnumerator LoadAudioClip(string path)
    {
        using (UnityWebRequest www = UnityWebRequestMultimedia.GetAudioClip("file://" + path, AudioType.MPEG))
        {
            yield return www.SendWebRequest();

            if (www.result == UnityWebRequest.Result.Success)
            {
                savedAudioClip = DownloadHandlerAudioClip.GetContent(www);
                Debug.Log("AudioClip loaded successfully! Duration: " + savedAudioClip.length + " seconds");
            }
            else
            {
                Debug.LogError("Error loading audio clip: " + www.error);
            }
        }
    }

    // Get the saved audio clip
    public AudioClip GetSavedAudioClip()
    {
        return savedAudioClip;
    }

    // Helper method to escape JSON strings
    string EscapeJSON(string str)
    {
        return str.Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
    }

    // TEST METHOD - Add a button in your scene to call this
    public void TestTTS()
    {
        Debug.Log("Test TTS");
        ConvertTextToSpeech("Hello! This is a test of the text to speech system.");
    }
}