using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

// ── Data Model ───────────────────────────────────────────────────────────────

[Serializable]
public class TrajectoryPoint
{
    public float x, y, z;
    public TrajectoryPoint(Vector3 v) { x = v.x; y = v.y; z = v.z; }
    public Vector3 ToVector3() => new Vector3(x, y, z);
}

[Serializable]
public class TrajectoryData
{
    public string id;
    public string timestamp;
    public List<TrajectoryPoint> points = new List<TrajectoryPoint>();

    public TrajectoryData()
    {
        id        = Guid.NewGuid().ToString();
        timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
    }
}

[Serializable]
public class TrajectoryCollection
{
    public List<TrajectoryData> trajectories = new List<TrajectoryData>();
}

// ── IO Utility ───────────────────────────────────────────────────────────────

public static class TrajectoryIO
{
    private static string SaveDir => Application.persistentDataPath;

    /// <summary>
    /// Save a session collection to a new uniquely-named file.
    /// Each call produces a separate file — one per session.
    /// </summary>
    public static void SaveNewSession(TrajectoryCollection collection)
    {
        string stamp    = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
        string filePath = Path.Combine(SaveDir, $"trajectory_{stamp}.json");
        string json     = JsonUtility.ToJson(collection, prettyPrint: true);
        File.WriteAllText(filePath, json);
        Debug.Log($"[TrajectoryIO] Session saved to {filePath}");
    }

    /// <summary>Returns all session files, newest first.</summary>
    public static string[] GetAllSessionFiles()
    {
        if (!Directory.Exists(SaveDir)) return new string[0];
        string[] files = Directory.GetFiles(SaveDir, "trajectory_*.json");
        Array.Sort(files, (a, b) => string.Compare(b, a, StringComparison.Ordinal));
        return files;
    }

    /// <summary>Deletes all saved trajectory session files.</summary>
    public static void ClearAllSessions()
    {
        if (!Directory.Exists(SaveDir))
            return;

        string[] files = Directory.GetFiles(SaveDir, "trajectory_*.json");
        foreach (string filePath in files)
        {
            try
            {
                File.Delete(filePath);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[TrajectoryIO] Failed to delete {filePath}: {ex.Message}");
            }
        }

        if (files.Length > 0)
            Debug.Log($"[TrajectoryIO] Cleared {files.Length} saved trajectory session(s).");
    }

    /// <summary>Load a session collection from a specific file path.</summary>
    public static TrajectoryCollection LoadFromPath(string filePath)
    {
        if (!File.Exists(filePath))
        {
            Debug.LogWarning($"[TrajectoryIO] File not found: {filePath}");
            return new TrajectoryCollection();
        }
        return JsonUtility.FromJson<TrajectoryCollection>(File.ReadAllText(filePath));
    }
}
