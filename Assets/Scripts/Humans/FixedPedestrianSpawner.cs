using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Spawns pedestrians at fixed positions, independent of the SEAN task system.
///
/// Usage:
///  1. Put this on an empty GameObject (NOT under HandcraftedSocialSituations/Agents,
///     whose children are destroyed each trial by Agents.Handcrafted.Clear()).
///  2. Right-click the component header -> "Add Spawn Point" (or add empty children
///     by hand). Move/rotate each point where a pedestrian should stand.
///  3. Optional: to make a pedestrian walk somewhere, right-click -> "Add Goal To
///     Selected Point" (or create a child named "Goal" under that point) and move it.
///     Points without a Goal spawn pedestrians that stand still.
///  4. Press Play.
/// </summary>
public class FixedPedestrianSpawner : MonoBehaviour
{
    const string GoalChildName = "Goal";
    const string ResourcesPrefabPath = "Prefabs/RocketboxRandomAnimatedAgent";

    [Tooltip("Pedestrian prefab. Leave empty to auto-load RocketboxRandomAnimatedAgent from Resources.")]
    public GameObject agentPrefab;

    [Tooltip("Snap each spawn point onto the NavMesh (within this distance). Set 0 to spawn exactly at the point.")]
    public float navMeshSnapDistance = 2f;

    IEnumerator Start()
    {
        if (agentPrefab == null)
        {
            agentPrefab = Resources.Load<GameObject>(ResourcesPrefabPath);
        }
        if (agentPrefab == null)
        {
            Debug.LogError("[FixedPedestrianSpawner] No agent prefab assigned and none found at Resources/" + ResourcesPrefabPath, this);
            yield break;
        }

        // Snapshot the spawn points before instantiating: the spawned agents are
        // parented under this same transform, and enumerating it while adding
        // children would keep yielding the new agents forever (editor freeze).
        var points = new List<Transform>();
        foreach (Transform point in transform) { points.Add(point); }

        var walkers = new List<KeyValuePair<GameObject, Transform>>();
        foreach (Transform point in points)
        {
            if (!point.gameObject.activeSelf) { continue; }

            Vector3 pos = SnapToNavMesh(point.position);
            // Keep pedestrians upright regardless of how the point is tilted
            Quaternion rot = Quaternion.Euler(0, point.eulerAngles.y, 0);
            GameObject instance = Instantiate(agentPrefab, pos, rot, transform);
            instance.name = "Pedestrian_" + point.name;

            Transform goal = point.Find(GoalChildName);
            if (goal != null && goal.gameObject.activeSelf)
            {
                walkers.Add(new KeyValuePair<GameObject, Transform>(instance, goal));
            }
        }

        // RandomAvatar builds the avatar in Awake, but the agent's own Start
        // (NavMeshPath, Rigidbody, animator setup) runs on the next frame.
        yield return null;

        foreach (var pair in walkers)
        {
            IVI.INavigable agent = pair.Key.GetComponentInChildren<IVI.INavigable>();
            if (agent == null)
            {
                Debug.LogWarning("[FixedPedestrianSpawner] No INavigable found under " + pair.Key.name, this);
                continue;
            }
            // INavigable.InitDest expands its NavMesh search forever when the goal
            // is nowhere near the NavMesh, freezing the editor — only hand it
            // positions we could verify ourselves.
            Vector3 goalPos;
            float searchDist = Mathf.Max(navMeshSnapDistance, 2f);
            if (!TrySnapToNavMesh(pair.Value.position, searchDist, out goalPos))
            {
                Debug.LogWarning("[FixedPedestrianSpawner] Goal of " + pair.Key.name
                    + " is not within " + searchDist + "m of the NavMesh, agent will stand still. "
                    + "Move the Goal onto walkable ground (or bake the NavMesh).", pair.Value);
                continue;
            }
            agent.InitDest(goalPos);
        }
    }

    Vector3 SnapToNavMesh(Vector3 position)
    {
        if (navMeshSnapDistance <= 0) { return position; }
        Vector3 snapped;
        return TrySnapToNavMesh(position, navMeshSnapDistance, out snapped) ? snapped : position;
    }

    bool TrySnapToNavMesh(Vector3 position, float maxDistance, out Vector3 snapped)
    {
        UnityEngine.AI.NavMeshHit hit;
        if (UnityEngine.AI.NavMesh.SamplePosition(position, out hit, maxDistance, UnityEngine.AI.NavMesh.AllAreas))
        {
            snapped = hit.position;
            return true;
        }
        snapped = position;
        return false;
    }

    [ContextMenu("Add Spawn Point")]
    void AddSpawnPoint()
    {
        var point = new GameObject("Point_" + transform.childCount);
        point.transform.SetParent(transform, false);
        point.transform.position = transform.position;
#if UNITY_EDITOR
        UnityEditor.Selection.activeGameObject = point;
        UnityEditor.EditorUtility.SetDirty(this);
#endif
    }

    [ContextMenu("Add Goal To Selected Point")]
    void AddGoalToSelectedPoint()
    {
#if UNITY_EDITOR
        Transform point = UnityEditor.Selection.activeTransform;
        if (point == null || point.parent != transform)
        {
            // Fall back to the last point without a goal
            foreach (Transform child in transform)
            {
                if (child.Find(GoalChildName) == null) { point = child; }
            }
        }
        if (point == null)
        {
            Debug.LogWarning("[FixedPedestrianSpawner] Select a spawn point first.", this);
            return;
        }
        if (point.Find(GoalChildName) != null)
        {
            Debug.LogWarning("[FixedPedestrianSpawner] " + point.name + " already has a Goal.", this);
            return;
        }
        var goal = new GameObject(GoalChildName);
        goal.transform.SetParent(point, false);
        goal.transform.position = point.position + point.forward * 3f;
        UnityEditor.Selection.activeGameObject = goal;
        UnityEditor.EditorUtility.SetDirty(this);
#endif
    }

    void OnDrawGizmos()
    {
        foreach (Transform point in transform)
        {
            if (!point.gameObject.activeSelf) { continue; }

            Transform goal = point.Find(GoalChildName);
            // Standing pedestrians drawn green, walkers cyan
            Gizmos.color = goal != null ? Color.cyan : Color.green;
            Gizmos.DrawWireSphere(point.position + Vector3.up * 0.9f, 0.25f);
            Gizmos.DrawLine(point.position, point.position + Vector3.up * 1.8f);
            // Facing direction
            Gizmos.DrawLine(point.position + Vector3.up * 0.9f,
                            point.position + Vector3.up * 0.9f + point.forward * 0.6f);

            if (goal != null && goal.gameObject.activeSelf)
            {
                Gizmos.DrawLine(point.position, goal.position);
                Gizmos.DrawWireCube(goal.position, new Vector3(0.3f, 0.3f, 0.3f));
            }
        }
    }
}
