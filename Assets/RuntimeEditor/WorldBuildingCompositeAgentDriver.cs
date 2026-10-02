using UnityEngine;

/// <summary>
/// Translates an authored composite character rig (Cyclist = Bicycle + rider, Scooter
/// User, Phone User ...) that was placed as a moving World-Building agent.
///
/// Plain pedestrian prefabs keep their Animator on the same GameObject as the social-force
/// agent, so Base's root motion is what actually moves them. A composite rig cannot work
/// that way: its first Animator belongs to the VEHICLE (the bike), while the rider is a
/// sibling under the character root. Attaching the agent to that Animator drove the bike
/// away and left the rider standing (and overwrote the rig's authored controller). Instead
/// the agent sits on the ROOT — where root motion is necessarily off, because the Animator
/// is on a child — and this driver applies the agent's computed velocity to the root, so
/// the whole rig moves together. It mirrors what ManualWheelchairController does for the
/// PWD player in automatic mode.
///
/// It doubles as the runtime marker for "this is an authored rig": the spawn renames the
/// object to WB_Pedestrian_&lt;id&gt;, which destroys the name-based detection that
/// AgentControlTuning otherwise relies on for these prefabs.
/// </summary>
[DefaultExecutionOrder(100)]
public class WorldBuildingCompositeAgentDriver : MonoBehaviour
{
    [Tooltip("Riding rig (bike/scooter): its visual forward is a mesh axis, not transform.forward.")]
    public bool ridingRig;

    [Tooltip("Prefab this agent was spawned from. Stable identity for the shared drive-axis " +
             "calibration cache, which the WB_Pedestrian_<id> rename would otherwise destroy.")]
    public string sourcePrefabName;

    [Tooltip("Tallest curb/step (m) the character eases up onto.")]
    public float maxStepHeight = 0.35f;
    [Tooltip("Vertical speed (m/s) used to ease onto the detected ground height.")]
    public float groundFollowSpeed = 2f;

    private SEAN.Scenario.Agents.Base agent;
    private Transform animatedNode;
    private Vector3 animatedNodeLocalPosition;
    private float groundOffset;
    private bool hasGroundOffset;

    void Start()
    {
        // Most of these rigs carry a KINEMATIC rigidbody, so gravity never acts on them:
        // without this they would keep their spawn height while walking over curbs and end
        // up floating or sunk. Remember the pivot's height above the ground it was placed
        // on and preserve it, like ManualWheelchairController.FollowGround does.
        if (TryProbeGround(transform.position + Vector3.up * 1f, 5f, out float ground))
        {
            groundOffset = transform.position.y - ground;
            hasGroundOffset = true;
        }
    }

    void Awake()
    {
        agent = GetComponent<SEAN.Scenario.Agents.Base>();

        // A travelling clip (one that bakes forward translation into the rig, like the
        // bike's "jalan") would walk the model away from the root we are translating.
        // Pin the animated node's local offset so the clip animates in place — the same
        // trick BikeAnimateInPlace applies to the bike's bones, one level up. No-op for
        // in-place clips, whose node offset never changes anyway.
        Animator animator = SessionReview.AgentControlTuning.FindAnimator(gameObject);
        if (animator != null && animator.transform != transform)
        {
            animatedNode = animator.transform;
            animatedNodeLocalPosition = animatedNode.localPosition;
        }
    }

    void Update()
    {
        // While possessed, PossessedAgentController owns the transform and the overlay has
        // disabled the agent — velocity would be stale, so stay out of the way.
        if (agent == null || !agent.enabled)
            return;

        Vector3 velocity = agent.velocity;
        velocity.y = 0f;
        if (velocity.sqrMagnitude > 1e-6f)
        {
            transform.position += velocity * Time.deltaTime;
            FollowGround(Time.deltaTime);
        }
    }

    private void FollowGround(float deltaTime)
    {
        if (!hasGroundOffset || maxStepHeight <= 0f)
            return;

        float probeUp = maxStepHeight + 0.3f + Mathf.Max(0f, -groundOffset);
        if (!TryProbeGround(transform.position + Vector3.up * probeUp, probeUp + maxStepHeight + 0.6f, out float ground))
            return;

        float target = ground + groundOffset;
        if (Mathf.Abs(target - transform.position.y) < 0.01f)
            return;

        Vector3 p = transform.position;
        p.y = Mathf.MoveTowards(p.y, target, groundFollowSpeed * deltaTime);
        transform.position = p;
    }

    private bool TryProbeGround(Vector3 origin, float length, out float groundY)
    {
        groundY = 0f;
        float best = float.NegativeInfinity;
        foreach (RaycastHit hit in Physics.RaycastAll(origin, Vector3.down, length,
                     Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
        {
            if (hit.collider == null) continue;
            // Environment only: never stand on another agent or on our own colliders.
            if (hit.collider.attachedRigidbody != null) continue;
            if (hit.collider.transform.IsChildOf(transform)) continue;
            best = Mathf.Max(best, hit.point.y);
        }

        if (float.IsNegativeInfinity(best))
            return false;
        groundY = best;
        return true;
    }

    void LateUpdate()
    {
        // After the Animator has written its curves for this frame.
        if (animatedNode != null)
            animatedNode.localPosition = animatedNodeLocalPosition;
    }
}
