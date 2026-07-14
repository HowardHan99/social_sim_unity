using System;
using System.Collections;
using System.IO;
using Siccity.GLTFUtility;
using UnityEngine;

/// <summary>
/// Loads a Meshy-downloaded GLB at runtime and hands it to the World Building editor.
/// Requires the GLTFUtility package (com.siccity.gltfutility).
/// </summary>
public static class MeshyGlbSceneImporter
{
    public static string GetMeshyModelsDirectory()
    {
        return Path.Combine(Application.persistentDataPath, "MeshyModels");
    }

    /// <summary>Same path layout as <see cref="GenerateModel"/> download (prompt → sanitized .glb).</summary>
    public static string GetSavedGlbPath(string promptOrName)
    {
        return Path.Combine(GetMeshyModelsDirectory(), SanitizeFileName(promptOrName) + ".glb");
    }

    public static IEnumerator ImportAndSpawn(
        string localGlbPath,
        string displayName,
        RuntimeEditorManager editor,
        Action<string> onError = null)
    {
        if (string.IsNullOrEmpty(localGlbPath) || !File.Exists(localGlbPath))
        {
            onError?.Invoke($"GLB file not found: {localGlbPath}");
            yield break;
        }

        if (editor == null)
        {
            onError?.Invoke("RuntimeEditorManager is not available.");
            yield break;
        }

        if (!editor.isEditorActive)
        {
            onError?.Invoke("Editor mode is not active — enter World Building first.");
            yield break;
        }

        // GLTFUtility's async loader uses WaitForSeconds internally, which never advances when
        // World Building / review modes freeze Time.timeScale. Use sync import instead.
        Debug.Log($"[GenerateModel] Importing GLB (sync): {localGlbPath} (Time.timeScale={Time.timeScale})");
        yield return null;

        GameObject loaded = null;
        try
        {
            loaded = Importer.LoadFromFile(localGlbPath, new ImportSettings(), out _);
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
            onError?.Invoke($"GLB import exception: {ex.Message}");
            yield break;
        }

        if (loaded == null)
        {
            onError?.Invoke("GLTFUtility returned null — file may be corrupt or unsupported.");
            yield break;
        }

        if (!string.IsNullOrWhiteSpace(displayName))
            loaded.name = displayName;

        editor.SpawnImportedInstance(loaded, displayName);
        Debug.Log($"[GenerateModel] Spawned imported model '{loaded.name}' in World Building.");
    }

    static string SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "meshy_model";
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name.Length > 80 ? name.Substring(0, 80) : name;
    }
}
