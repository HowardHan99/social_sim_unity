using System;
using System.Collections;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

public class TTSManager : MonoBehaviour
{
    private const string GeminiTtsModel = "gemini-2.5-flash-preview-tts";
    private const string GeminiApiBaseUrl = "https://generativelanguage.googleapis.com/v1beta/models/";
    private const int AudioSampleRate = 24000;
    private const int AudioChannels = 1;
    private const string DefaultVoiceName = "Kore";

    private string geminiApiKey;
    private AudioClip savedAudioClip;
    private AudioSource audioSource;
    private readonly System.Collections.Generic.Dictionary<string, AudioClip> clipCache =
        new System.Collections.Generic.Dictionary<string, AudioClip>();

    private void Awake()
    {
        geminiApiKey = GeminiApiKeyLoader.Load();
        audioSource = GetComponent<AudioSource>();
        if (audioSource == null)
            audioSource = gameObject.AddComponent<AudioSource>();

        ConfigureAudioSource();
    }

    public void ConvertTextToSpeech(string text)
    {
        StartCoroutine(GenerateTTS(text));
    }

    public void PlaySpeech(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        if (audioSource == null)
            audioSource = GetComponent<AudioSource>() ?? gameObject.AddComponent<AudioSource>();

        ConfigureAudioSource();

        if (clipCache.TryGetValue(text, out AudioClip cachedClip) && cachedClip != null)
        {
            audioSource.Stop();
            audioSource.clip = cachedClip;
            audioSource.Play();
            Debug.Log($"[TTSManager] Replaying cached speech. ignoreListenerPause={audioSource.ignoreListenerPause}, AudioListener.pause={AudioListener.pause}");
            return;
        }

        StartCoroutine(GenerateTTS(text, true));
    }

    private IEnumerator GenerateTTS(string text, bool playWhenReady = false)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            Debug.LogWarning("[TTSManager] Ignoring empty TTS request.");
            yield break;
        }

        if (string.IsNullOrWhiteSpace(geminiApiKey))
        {
            Debug.LogError("[TTSManager] Gemini API key is missing.");
            yield break;
        }

        Debug.Log("[TTSManager] Converting text to speech with Gemini.");

        string url = GeminiApiBaseUrl + GeminiTtsModel + ":generateContent?key=" + geminiApiKey;
        string jsonData = BuildTtsRequest(text);

        using (UnityWebRequest request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST))
        {
            byte[] bodyRaw = Encoding.UTF8.GetBytes(jsonData);
            request.uploadHandler = new UploadHandlerRaw(bodyRaw);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");

            yield return request.SendWebRequest();

            string rawResponse = request.downloadHandler != null ? request.downloadHandler.text : string.Empty;
            if (request.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError("[TTSManager] Error generating TTS: " + request.error + "\nResponse: " + rawResponse);
                yield break;
            }

            string base64Audio = ParseAudioData(rawResponse);
            if (string.IsNullOrWhiteSpace(base64Audio))
            {
                Debug.LogError("[TTSManager] Gemini TTS response did not include audio data.\nResponse: " + rawResponse);
                yield break;
            }

            byte[] pcmData = Convert.FromBase64String(base64Audio);
            savedAudioClip = CreateAudioClipFromPcm(pcmData);
            SaveAudioFile(pcmData);

            if (savedAudioClip != null)
            {
                clipCache[text] = savedAudioClip;
                Debug.Log("[TTSManager] AudioClip created successfully. Duration: " + savedAudioClip.length + " seconds");
                if (playWhenReady)
                {
                    ConfigureAudioSource();
                    audioSource.Stop();
                    audioSource.clip = savedAudioClip;
                    audioSource.Play();
                    Debug.Log($"[TTSManager] Playing generated speech. ignoreListenerPause={audioSource.ignoreListenerPause}, AudioListener.pause={AudioListener.pause}");
                }
            }
        }
    }

    public void StopPlayback()
    {
        if (audioSource != null && audioSource.isPlaying)
            audioSource.Stop();
    }

    private void ConfigureAudioSource()
    {
        if (audioSource == null)
            return;

        audioSource.playOnAwake = false;
        audioSource.loop = false;
        audioSource.spatialBlend = 0f;
        audioSource.volume = 1f;
        audioSource.ignoreListenerPause = true;
    }

    public AudioClip GetSavedAudioClip()
    {
        return savedAudioClip;
    }

    public void TestTTS()
    {
        Debug.Log("[TTSManager] Test TTS");
        ConvertTextToSpeech("Hello! This is a test of the text to speech system.");
    }

    private string BuildTtsRequest(string text)
    {
        string escapedText = EscapeJson(text);

        return "{"
            + "\"contents\":[{"
            + "\"parts\":[{"
            + "\"text\":\"" + escapedText + "\""
            + "}]"
            + "}],"
            + "\"generationConfig\":{"
            + "\"responseModalities\":[\"AUDIO\"],"
            + "\"speechConfig\":{"
            + "\"voiceConfig\":{"
            + "\"prebuiltVoiceConfig\":{"
            + "\"voiceName\":\"" + DefaultVoiceName + "\""
            + "}"
            + "}"
            + "}"
            + "},"
            + "\"model\":\"" + GeminiTtsModel + "\""
            + "}";
    }

    private string ParseAudioData(string jsonResponse)
    {
        try
        {
            GeminiGenerateContentResponse response = JsonUtility.FromJson<GeminiGenerateContentResponse>(jsonResponse);
            if (response == null || response.candidates == null)
                return string.Empty;

            foreach (GeminiCandidate candidate in response.candidates)
            {
                if (candidate == null || candidate.content == null || candidate.content.parts == null)
                    continue;

                foreach (GeminiPart part in candidate.content.parts)
                {
                    if (part == null)
                        continue;

                    GeminiInlineData audioData = part.inlineData ?? part.inline_data;
                    if (audioData != null && !string.IsNullOrWhiteSpace(audioData.data))
                        return audioData.data;
                }
            }
        }
        catch (Exception exception)
        {
            Debug.LogError("[TTSManager] Error parsing Gemini TTS response: " + exception.Message);
        }

        return string.Empty;
    }

    private string SaveAudioFile(byte[] pcmData)
    {
        string folderPath = Path.Combine(Application.persistentDataPath, "TTSAudio");
        if (!Directory.Exists(folderPath))
            Directory.CreateDirectory(folderPath);

        string dateTime = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        string fileName = $"tts_audio_{dateTime}.wav";
        string fullPath = Path.Combine(folderPath, fileName);

        byte[] wavBytes = ConvertPcmToWav(pcmData, AudioSampleRate, AudioChannels, 16);
        File.WriteAllBytes(fullPath, wavBytes);
        Debug.Log("[TTSManager] Audio saved at: " + fullPath);

        return fullPath;
    }

    private AudioClip CreateAudioClipFromPcm(byte[] pcmData)
    {
        if (pcmData == null || pcmData.Length < 2)
            return null;

        int sampleCount = pcmData.Length / 2;
        float[] audioSamples = new float[sampleCount];

        for (int i = 0; i < sampleCount; i++)
        {
            short sample = BitConverter.ToInt16(pcmData, i * 2);
            audioSamples[i] = sample / 32768f;
        }

        AudioClip clip = AudioClip.Create("GeminiTTS", sampleCount, AudioChannels, AudioSampleRate, false);
        clip.SetData(audioSamples, 0);
        return clip;
    }

    private byte[] ConvertPcmToWav(byte[] pcmData, int sampleRate, short channels, short bitsPerSample)
    {
        int byteRate = sampleRate * channels * bitsPerSample / 8;
        short blockAlign = (short)(channels * bitsPerSample / 8);

        using (MemoryStream stream = new MemoryStream())
        using (BinaryWriter writer = new BinaryWriter(stream))
        {
            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + pcmData.Length);
            writer.Write(Encoding.ASCII.GetBytes("WAVE"));
            writer.Write(Encoding.ASCII.GetBytes("fmt "));
            writer.Write(16);
            writer.Write((short)1);
            writer.Write(channels);
            writer.Write(sampleRate);
            writer.Write(byteRate);
            writer.Write(blockAlign);
            writer.Write(bitsPerSample);
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write(pcmData.Length);
            writer.Write(pcmData);
            writer.Flush();
            return stream.ToArray();
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
