using System.Collections;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Makes a World-Building-placed <c>RocketboxRandomAnimatedAgent</c> walk around the baked
/// NavMesh like a SEAN background pedestrian. <see cref="SEAN.Scenario.Agents.RandomAvatar"/>
/// builds the avatar child and its <c>IVI.INavigable</c> driver (SFAgent) on Awake; this
/// component grabs that driver, sends it to a random NavMesh point, and re-targets whenever it
/// arrives (the same wander loop as <c>RandomABNavAgentManager</c>).
///
/// Requires a baked NavMesh in the active scene. The standalone RuntimeEditor scene has none,
/// so the pedestrian stands still there; the sidewalk / SEAN gameplay scenes where World
/// Building actually runs do have one.
/// </summary>
[DisallowMultipleComponent]
public class WorldBuildingWanderPedestrian : MonoBehaviour
{
    IVI.INavigable agent;
    bool active;

    IEnumerator Start()
    {
        // RandomAvatar builds the avatar + SFAgent in Awake, but the agent's own Start
        // (NavMeshAgent, Rigidbody, animator params) runs on the next frame -- wait for it.
        yield return null;

        agent = GetComponentInChildren<IVI.INavigable>();
        if (agent == null)
        {
            Debug.LogWarning("[WorldBuildingWanderPedestrian] No IVI.INavigable found under '"
                + name + "'; the pedestrian will stand still.", this);
            yield break;
        }

        if (!HasBakedNavMesh())
        {
            Debug.LogWarning("[WorldBuildingWanderPedestrian] No baked NavMesh in the active scene; '"
                + name + "' cannot walk. Place it in a scene with a baked NavMesh.", this);
            yield break;
        }

        active = RetargetToRandomPoint();

        // Placed while a trial is already running: SessionTracker's roster was built at
        // trial start, so report in or this pedestrian is never recorded and stands
        // frozen during review playback.
        var tracker = FindObjectOfType<SessionReview.SessionTracker>();
        if (tracker != null)
            tracker.RegisterLateWorldBuildingPedestrian();
    }

    void Update()
    {
        if (!active || agent == null)
            return;

        // Re-issue a fresh random destination on arrival for perpetual wandering.
        if (agent.CloseEnough())
            active = RetargetToRandomPoint();
    }

    bool RetargetToRandomPoint()
    {
        try
        {
            // Navmesh.RandomVector always returns a point on the baked NavMesh, so InitDest's
            // NavMesh sampling never runs away searching for an unreachable goal.
            agent.InitDest(SEAN.Util.Navmesh.RandomVector());
            return true;
        }
        catch (System.Exception e)
        {
            Debug.LogWarning("[WorldBuildingWanderPedestrian] Could not pick a NavMesh destination for '"
                + name + "': " + e.Message, this);
            return false;
        }
    }

    static bool HasBakedNavMesh()
    {
        NavMeshTriangulation t = NavMesh.CalculateTriangulation();
        return t.indices != null && t.indices.Length > 0;
    }
}
