using System;
using System.Collections.Generic;
using UnityEngine;

[Serializable]
public class Vector3Data
{
    public float x;
    public float y;
    public float z;

    public Vector3 ToVector3()
    {
        return new Vector3(x, y, z);
    }
}

[Serializable]
public class QuaternionData
{
    public float x;
    public float y;
    public float z;
    public float w;

    public Quaternion ToQuaternion()
    {
        return new Quaternion(x, y, z, w);
    }
}

[Serializable]
public class StateData
{
    public string objectId;
    public float timestamp;
    public Vector3Data position;
    public QuaternionData rotation;
    public Vector3Data scale;
}

[Serializable]
public class TimelineData
{
    public string objectId;
    public List<StateData> states;
}

[Serializable]
public class TrajectoryLogData
{
    public float totalDuration;
    public List<TimelineData> timelines;
}
