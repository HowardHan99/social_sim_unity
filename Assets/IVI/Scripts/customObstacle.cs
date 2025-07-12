using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class customObstacle : MonoBehaviour
{
    // Define repulsive force multipliers for different agent groups
    [Header("Repulsive Force Multipliers")]
    public float ableBodiedMultiplier = 0.1f;
    public float disabledMultiplier = 5f;
    public float defaultMultiplier = 1.0f; // For any other agent groups

    // Optionally, you can add more properties
    // public float strollerMultiplier = 1.2f;
}
