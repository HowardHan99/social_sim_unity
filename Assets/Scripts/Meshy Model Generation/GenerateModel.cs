using System;
using System.Collections;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

public class GenerateModel : MonoBehaviour
{
    private const string BaseUrl = "https://api.meshy.ai/openapi/v2/text-to-3d";

    [SerializeField] private string apiKey = "msy_Cvkw6oaN0bBM2tmq9lltFJhJyjd7ouLjMTZU";
    [SerializeField] private bool shouldRemesh = true;
    [SerializeField] private int targetPolycount = 30000;
    [SerializeField] private string poseMode = "a-pose";
    [SerializeField] private bool enablePbr = false; // RESET TO TRUE
    [SerializeField] private bool autoSize = true;
    [SerializeField] private float pollIntervalSeconds = 5f;
    [SerializeField] private float maxWaitSeconds = 1200f;
    [SerializeField] private int pollRetries = 3;
    [SerializeField] private int requestTimeoutSeconds = 30;
    [SerializeField] private bool downloadGlbOnComplete = true;

    public event Action<GenerateModelResult> Completed;

    public bool IsGenerating { get; private set; }

    MeshyTaskStatus lastPolledStatus;

    void Awake()
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            apiKey = Environment.GetEnvironmentVariable("MESHY_API_KEY");

        Debug.Log($"[GenerateModel] Awake on '{gameObject.name}'. API key {(string.IsNullOrWhiteSpace(apiKey) ? "MISSING" : "set")}.");
    }

    void OnDestroy()
    {
        if (IsGenerating)
            Debug.LogWarning("[GenerateModel] Destroyed while generating — pipeline aborted.");
        IsGenerating = false;
    }

    public void Generate(string prompt)
    {
        Debug.Log($"[GenerateModel] Generate() called. prompt=\"{prompt}\" IsGenerating={IsGenerating}");

        if (IsGenerating)
        {
            Debug.LogWarning("[GenerateModel] Already generating.");
            return;
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Debug.LogError("[GenerateModel] Set apiKey in the Inspector or MESHY_API_KEY env var.");
            Completed?.Invoke(GenerateModelResult.Failed(prompt, "Missing API key"));
            return;
        }

        if (string.IsNullOrWhiteSpace(prompt))
        {
            Debug.LogError("[GenerateModel] Prompt is empty.");
            Completed?.Invoke(GenerateModelResult.Failed(prompt, "Empty prompt"));
            return;
        }

        Debug.Log($"[GenerateModel] Starting pipeline for \"{prompt.Trim()}\" (Time.timeScale={Time.timeScale})");
        StartCoroutine(RunPreviewThenRefine(prompt.Trim()));
    }

    IEnumerator RunPreviewThenRefine(string promptText)
    {
        IsGenerating = true;
        Debug.Log("[GenerateModel] Pipeline started (preview → refine).");
        string previewTaskId = null;
        string refineTaskId = null;
        string glbUrl = null;
        string localPath = null;
        string error = null;

        yield return PostCreateTask("preview", BuildPreviewJson(promptText), id => previewTaskId = id);
        if (string.IsNullOrEmpty(previewTaskId))
        {
            error = "Preview task creation failed";
            goto done;
        }

        Debug.Log($"[GenerateModel] Preview task created: {previewTaskId}. Polling…");
        bool previewOk = false;
        yield return PollUntilFinished("preview", previewTaskId, ok => previewOk = ok);
        if (!previewOk)
        {
            error = "Preview task failed or timed out";
            goto done;
        }

        Debug.Log($"[GenerateModel] Preview succeeded. Creating refine task for {previewTaskId}…");
        yield return PostCreateTask("refine", BuildRefineJson(previewTaskId), id => refineTaskId = id);
        if (string.IsNullOrEmpty(refineTaskId))
        {
            error = "Refine task creation failed";
            goto done;
        }

        Debug.Log($"[GenerateModel] Refine task created: {refineTaskId}. Polling…");
        bool refineOk = false;
        yield return PollUntilFinished("refine", refineTaskId, ok => refineOk = ok);
        if (!refineOk)
        {
            error = "Refine task failed or timed out";
            goto done;
        }

        glbUrl = lastPolledStatus?.model_urls?.glb;
        if (string.IsNullOrEmpty(glbUrl))
        {
            error = "Refine succeeded but no GLB URL was returned";
            goto done;
        }

        if (downloadGlbOnComplete)
        {
            Debug.Log($"[GenerateModel] Downloading GLB from {glbUrl}");
            yield return DownloadGlb(glbUrl, SanitizeFileName(promptText), path => localPath = path);
            if (string.IsNullOrEmpty(localPath))
                error = "GLB download failed";
            else
                Debug.Log($"[GenerateModel] GLB saved to {localPath}");
        }
        else
        {
            Debug.Log("[GenerateModel] Skipping GLB download (downloadGlbOnComplete=false).");
        }

        done:
        IsGenerating = false;
        var result = string.IsNullOrEmpty(error)
            ? GenerateModelResult.Succeeded(promptText, previewTaskId, refineTaskId, glbUrl, localPath)
            : GenerateModelResult.Failed(promptText, error, previewTaskId, refineTaskId, glbUrl, localPath);
        Completed?.Invoke(result);
        if (result.Success)
            Debug.Log($"[GenerateModel] Pipeline complete. GLB URL: {result.GlbUrl} | Local: {result.LocalGlbPath}");
        else
            Debug.LogError($"[GenerateModel] Pipeline failed: {result.Error} (preview={result.PreviewTaskId}, refine={result.RefineTaskId})");
    }

    IEnumerator PostCreateTask(string stage, string jsonBody, Action<string> onTaskId)
    {
        Debug.Log($"[GenerateModel] POST {stage} → {BaseUrl}");
        byte[] bodyRaw = Encoding.UTF8.GetBytes(jsonBody);
        using (var request = new UnityWebRequest(BaseUrl, UnityWebRequest.kHttpVerbPOST))
        {
            request.uploadHandler = new UploadHandlerRaw(bodyRaw);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("Authorization", "Bearer " + apiKey);
            request.timeout = requestTimeoutSeconds;
            yield return request.SendWebRequest();

            string responseText = request.downloadHandler != null ? request.downloadHandler.text : string.Empty;
            if (request.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"[GenerateModel] Create task failed: {request.error}\n{responseText}");
                yield break;
            }

            var created = JsonUtility.FromJson<MeshyCreateResponse>(responseText);
            if (!string.IsNullOrEmpty(created?.result))
            {
                Debug.Log($"[GenerateModel] {stage} task id: {created.result}");
                onTaskId?.Invoke(created.result);
            }
            else
                Debug.LogError($"[GenerateModel] {stage}: no task id in response: {responseText}");
        }
    }

    IEnumerator PollUntilFinished(string stage, string taskId, Action<bool> onComplete)
    {
        float elapsed = 0f;
        while (elapsed < maxWaitSeconds)
        {
            MeshyTaskStatus status = null;
            for (int attempt = 1; attempt <= pollRetries; attempt++)
            {
                yield return FetchTaskStatus(taskId);
                status = lastPolledStatus;
                if (status != null)
                    break;

                Debug.LogWarning($"[GenerateModel] {stage} poll attempt {attempt}/{pollRetries} failed for {taskId} — retrying…");
                if (attempt < pollRetries)
                    yield return new WaitForSecondsRealtime(2f);
            }

            if (status == null)
            {
                Debug.LogError($"[GenerateModel] {stage} poll failed after {pollRetries} attempts for {taskId}");
                onComplete?.Invoke(false);
                yield break;
            }

            if (string.IsNullOrEmpty(status.status))
            {
                Debug.LogWarning($"[GenerateModel] {stage} {taskId}: empty status in response (progress={status.progress}%) — treating as IN_PROGRESS [{elapsed:F0}s elapsed]");
                status.status = "IN_PROGRESS";
            }

            if (status.status == "PENDING")
            {
                Debug.Log($"[GenerateModel] {stage} {taskId}: PENDING (0%) — queued, {status.preceding_tasks} task(s) ahead");
            }
            else if (status.status == "IN_PROGRESS" && status.progress >= 90)
            {
                Debug.Log($"[GenerateModel] {stage} {taskId}: IN_PROGRESS ({status.progress}%) — finalizing (textures/export); can sit at 99% for several minutes [{elapsed:F0}s elapsed]");
            }
            else
            {
                Debug.Log($"[GenerateModel] {stage} {taskId}: {status.status} ({status.progress}%) [{elapsed:F0}s elapsed]");
            }

            if (status.status == "SUCCEEDED")
            {
                onComplete?.Invoke(true);
                yield break;
            }

            if (status.status == "FAILED" || status.status == "CANCELED")
            {
                string err = status.task_error != null ? status.task_error.message : status.status;
                Debug.LogError($"[GenerateModel] {stage} {taskId} {status.status}: {err}");
                onComplete?.Invoke(false);
                yield break;
            }

            // Use realtime wait: World Building / review modes set Time.timeScale = 0,
            // which would freeze WaitForSeconds and stop polling forever.
            yield return new WaitForSecondsRealtime(pollIntervalSeconds);
            elapsed += pollIntervalSeconds;
        }

        Debug.LogError($"[GenerateModel] {stage} {taskId} timed out after {maxWaitSeconds}s.");
        onComplete?.Invoke(false);
    }

    IEnumerator FetchTaskStatus(string taskId)
    {
        using (var request = UnityWebRequest.Get(BaseUrl + "/" + taskId))
        {
            request.SetRequestHeader("Authorization", "Bearer " + apiKey);
            request.timeout = requestTimeoutSeconds;
            yield return request.SendWebRequest();

            if (request.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"[GenerateModel] Poll failed: {request.error} (HTTP {(long)request.responseCode})");
                lastPolledStatus = null;
                yield break;
            }

            string body = request.downloadHandler != null ? request.downloadHandler.text : string.Empty;
            lastPolledStatus = JsonUtility.FromJson<MeshyTaskStatus>(body);
            if (lastPolledStatus == null)
                Debug.LogError($"[GenerateModel] Could not parse poll response: {body}");
        }
    }

    IEnumerator DownloadGlb(string url, string baseName, Action<string> onPath)
    {
        using (var request = UnityWebRequest.Get(url))
        {
            request.timeout = requestTimeoutSeconds;
            yield return request.SendWebRequest();
            if (request.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"[GenerateModel] Download failed: {request.error}");
                yield break;
            }

            string dir = Path.Combine(Application.persistentDataPath, "MeshyModels");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, baseName + ".glb");
            File.WriteAllBytes(path, request.downloadHandler.data);
            onPath?.Invoke(path);
        }
    }

    string BuildPreviewJson(string promptText)
    {
        var sb = new StringBuilder(256);
        sb.Append("{\"mode\":\"preview\",\"prompt\":").Append(EscapeJson(promptText));
        sb.Append(",\"should_remesh\":").Append(shouldRemesh ? "true" : "false");
        sb.Append(",\"target_polycount\":").Append(targetPolycount);
        if (!string.IsNullOrEmpty(poseMode))
            sb.Append(",\"pose_mode\":").Append(EscapeJson(poseMode));
        sb.Append(",\"target_formats\":[\"glb\"]}");
        return sb.ToString();
    }

    string BuildRefineJson(string previewTaskId)
    {
        return "{\"mode\":\"refine\",\"preview_task_id\":" + EscapeJson(previewTaskId)
            + ",\"enable_pbr\":" + (enablePbr ? "true" : "false")
            + ",\"auto_size\":" + (autoSize ? "true" : "false")
            + ",\"target_formats\":[\"glb\"]}";
    }

    static string EscapeJson(string value)
    {
        if (value == null) return "null";
        var sb = new StringBuilder(value.Length + 8);
        sb.Append('"');
        foreach (char c in value)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default: sb.Append(c); break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    static string SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "meshy_model";
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name.Length > 80 ? name.Substring(0, 80) : name;
    }
}

public class GenerateModelResult
{
    public bool Success;
    public string Prompt;
    public string PreviewTaskId;
    public string RefineTaskId;
    public string GlbUrl;
    public string LocalGlbPath;
    public string Error;

    public static GenerateModelResult Succeeded(
        string prompt, string previewTaskId, string refineTaskId, string glbUrl, string localPath)
    {
        return new GenerateModelResult
        {
            Success = true,
            Prompt = prompt,
            PreviewTaskId = previewTaskId,
            RefineTaskId = refineTaskId,
            GlbUrl = glbUrl,
            LocalGlbPath = localPath
        };
    }

    public static GenerateModelResult Failed(
        string prompt, string error,
        string previewTaskId = null, string refineTaskId = null, string glbUrl = null, string localPath = null)
    {
        return new GenerateModelResult
        {
            Success = false,
            Prompt = prompt,
            Error = error,
            PreviewTaskId = previewTaskId,
            RefineTaskId = refineTaskId,
            GlbUrl = glbUrl,
            LocalGlbPath = localPath
        };
    }
}

[Serializable]
class MeshyCreateResponse { public string result; }

[Serializable]
class MeshyTaskStatus
{
    public string status;
    public int progress;
    public int preceding_tasks;
    public ModelUrls model_urls;
    public MeshyTaskError task_error;
}

[Serializable]
class MeshyTaskError { public string message; }

[Serializable]
class ModelUrls { public string glb; }
