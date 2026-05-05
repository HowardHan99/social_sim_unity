using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Attach this to a GameObject to visualize a trajectory using a LineRenderer.
/// Managed by TrajectoryInputManager — you don't need to add it manually.
/// </summary>
[RequireComponent(typeof(LineRenderer))]
public class TrajectoryRenderer : MonoBehaviour
{
    private LineRenderer _line;
    private readonly List<Vector3> _points = new List<Vector3>();

    [Header("Visual Settings")]
    public Color lineColor = new Color(0.2f, 0.8f, 1f, 0.9f);
    public float lineWidth = 0.15f;
    public Material lineMaterial; // Assign a transparent/unlit material in Inspector (optional)

    private void Awake()
    {
        _line = GetComponent<LineRenderer>();
        ConfigureLine();
    }

    private void ConfigureLine()
    {
        _line.useWorldSpace = true;
        _line.startWidth = lineWidth;
        _line.endWidth = lineWidth;
        _line.numCornerVertices = 4;
        _line.numCapVertices = 4;
        _line.positionCount = 0;

        if (lineMaterial != null)
        {
            _line.material = lineMaterial;
        }
        else
        {
            // Fallback: create a simple unlit material
            _line.material = new Material(Shader.Find("Sprites/Default"));
        }

        _line.startColor = lineColor;
        _line.endColor = lineColor;
        _line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        _line.receiveShadows = false;
        _line.sortingOrder = 50;
    }

    /// <summary>Add a point to the live trajectory.</summary>
    public void AddPoint(Vector3 worldPoint)
    {
        // Avoid duplicates that are too close (reduces jitter)
        if (_points.Count > 0 && Vector3.Distance(_points[_points.Count - 1], worldPoint) < 0.05f)
            return;

        _points.Add(worldPoint);
        _line.positionCount = _points.Count;
        _line.SetPosition(_points.Count - 1, worldPoint);
    }

    /// <summary>Load and display an existing TrajectoryData.</summary>
    public void LoadFromData(TrajectoryData data, float heightOffset = 0.1f)
    {
        _points.Clear();
        foreach (var pt in data.points)
        {
            Vector3 p = pt.ToVector3();
            p.y += heightOffset;
            _points.Add(p);
        }
        _line.positionCount = _points.Count;
        _line.SetPositions(_points.ToArray());
    }

    /// <summary>Export current points as TrajectoryData.</summary>
    public TrajectoryData ExportData()
    {
        var data = new TrajectoryData();
        foreach (var p in _points)
            data.points.Add(new TrajectoryPoint(p));
        return data;
    }

    public List<Vector3> Points => _points;

    /// <summary>Replace all points (used after post-processing like smoothing).</summary>
    public void ReplacePoints(List<Vector3> newPoints)
    {
        _points.Clear();
        if (newPoints != null) _points.AddRange(newPoints);
        _line.positionCount = _points.Count;
        if (_points.Count > 0) _line.SetPositions(_points.ToArray());
    }

    public void ApplyVisualSettings()
    {
        if (_line == null)
            _line = GetComponent<LineRenderer>();

        ConfigureLine();
    }

    public void Clear()
    {
        _points.Clear();
        _line.positionCount = 0;
    }
}
