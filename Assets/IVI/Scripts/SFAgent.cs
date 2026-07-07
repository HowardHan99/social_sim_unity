using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.AI;

namespace IVI
{
    public class SFAgent : SEAN.Scenario.Agents.Base
    {

        private const int OBSTACLE_ANGLE_BINS = 6;

        private SphereCollider perceptionSphere;

        //NEIGHBORS
        [SerializeField] private List<GameObject> neighbors = new List<GameObject>();
        private HashSet<GameObject> obstacles = new HashSet<GameObject>();

        // Neighbor Computed Values
        Dictionary<int, Vector3> closestPoints;

        // IMPORTANT: GO2Agent dictionary is now shared from Base.cs
        // Previously each agent type had its own static dictionary which prevented
        // different agent types from finding each other in the dictionary

        //ROBOT REPULSION
        private float robotRepulsion;

        [Header("Oscillation Tuning (live)")]
        [Tooltip("Padding (m) added to the radii overlap in agent/robot repulsion. Large values push the repulsion/goal balance point far from the robot and cause back-and-forth pacing (was hardcoded 0.5).")]
        public float personalSpacePad = 0.15f;
        [Tooltip("Cap on the total social force as a multiple of the max goal force (MASS * DESIRED_SPEED / T). <= 0 disables.")]
        public float forceCapMultiple = 3f;
        [Tooltip("Velocity low-pass time constant (s) so a single frame's force spike cannot flip the walking direction. <= 0 disables.")]
        public float velocitySmoothingTime = 0.1f;
        [Tooltip("When agent/robot repulsion would push this agent away from its goal, decay speed to zero at this rate (m/s^2) instead of walking backwards. <= 0 disables.")]
        public float yieldDeceleration = 2f;
        [Tooltip("Within this distance (m) of the final destination, agent/robot repulsion fades out linearly so goals placed next to the robot stay reachable. <= 0 disables.")]
        public float goalPriorityRadius = 2f;

        // Agent/robot repulsion captured by the last ComputeForce, used by ShouldYield
        private Vector3 lastAgentRepulsion;
        // Distance and speed of the nearest goal-ward neighbor, from the last ComputeForce
        private float lastBlockerDist = float.PositiveInfinity;
        private float lastBlockerSpeed;

        protected override void Start()
        {
            base.Start();

            if (SEAN.SEAN.instance != null)
            {
                var robot = SEAN.SEAN.instance.robot.gameObject;

                if (!GO2Agent.ContainsKey(robot))
                {
                    GO2Agent.Add(robot, null);
                }
                neighbors.Add(robot);
            }

            GO2Agent.Add(gameObject, this);

            perceptionSphere = gameObject.AddComponent<SphereCollider>();
            perceptionSphere.isTrigger = true;
            perceptionSphere.radius = PERCEPTION_RADIUS;

            robotRepulsion = Random.value * (Parameters.ROBOT_REPULSION_DAMPENING_MAX - Parameters.ROBOT_REPULSION_DAMPENING_MIN) + Parameters.ROBOT_REPULSION_DAMPENING_MIN;
        }

        protected override Vector3 UpdateVelocity()
        {

            SEAN.Scenario.Agents.SocialForce totalForce = ComputeForce();
            var accel = totalForce.force / MASS;
            Vector3 nextVelocity = velocity + accel * Time.deltaTime;
            nextVelocity.y = 0;

            if (ShouldYield(nextVelocity))
            {
                return Vector3.MoveTowards(velocity, Vector3.zero, yieldDeceleration * Time.deltaTime);
            }

            if (nextVelocity.sqrMagnitude > 0)
            {
                nextVelocity = nextVelocity.normalized * Mathf.Min(nextVelocity.magnitude, Parameters.MAX_VEL);
            }
            return SmoothVelocity(nextVelocity);
        }

        // Yield (slow to a stop) when agent/robot repulsion would drive this agent
        // away from its goal; integrating the reversed force makes it pace back and
        // forth instead. Wall forces are excluded so they can still push the agent out.
        private bool ShouldYield(Vector3 nextVelocity)
        {
            if (yieldDeceleration <= 0f)
            {
                return false;
            }
            Vector3 goalVec = nearestGoalPoint - transform.position;
            goalVec.y = 0;
            float goalDist = goalVec.magnitude;
            if (goalDist <= 1e-2f)
            {
                return false;
            }
            // The goal is closer than whoever is pushing back: it can be reached
            // without contact, so keep walking.
            if (lastBlockerDist >= goalDist)
            {
                return false;
            }
            // A stationary blocker will never clear on its own; waiting would
            // last forever, so steer around it instead.
            if (lastBlockerSpeed <= 0.1f)
            {
                return false;
            }
            Vector3 goalDir = goalVec / goalDist;
            return Vector3.Dot(nextVelocity, goalDir) < 0f && Vector3.Dot(lastAgentRepulsion, goalDir) < 0f;
        }

        private Vector3 SmoothVelocity(Vector3 nextVelocity)
        {
            if (velocitySmoothingTime <= 0f)
            {
                return nextVelocity;
            }
            float alpha = 1f - Mathf.Exp(-Time.deltaTime / velocitySmoothingTime);
            return Vector3.Lerp(velocity, nextVelocity, alpha);
        }

        void OnTriggerEnter(Collider other)
        {
            if (other.isTrigger)
            {
                return;
            }
            if (other.GetComponentInChildren<IVI.INavigable>() != null && !neighbors.Contains(other.gameObject))
            {
                neighbors.Add(other.gameObject);
            }
            else if (other.gameObject.GetComponent<BoxCollider>() != null &&/* other.gameObject.GetComponent<Renderer>() != null &&*/ !obstacles.Contains(other.gameObject))
            {
                obstacles.Add(other.gameObject);
            }
        }

        void OnTriggerExit(Collider other)
        {
            if (other.isTrigger)
            {
                return;
            }

            if (neighbors.Contains(other.gameObject))
            {
                neighbors.Remove(other.gameObject);
            }
            if (obstacles.Contains(other.gameObject))
            {
                obstacles.Remove(other.gameObject);
            }
        }

        #region Forces

        private SEAN.Scenario.Agents.SocialForce ComputeForce()
        {
            SEAN.Scenario.Agents.SocialForce totalForce = CalculateAgentForce();
            lastAgentRepulsion = totalForce.force;
            //print("AgentForce: '" + totalForce.force + "'");
            totalForce.force += CalculateGoalForce();
            //print("AgentForce + GoalForce: '" + totalForce.force + "'");
            totalForce.force += CalculateWallForce();
            //print("AgentForce + GoalForce + WallForce: '" + totalForce.force + "'");

            #region Limit Backward/Lateral Motion

            var ang = Vector3.Dot(transform.forward, totalForce.force.normalized);
            if (ang < 0)
            {
                var projectOnForward = transform.forward * Vector3.Dot(transform.forward, totalForce.force);
                totalForce.force -= projectOnForward;
                //totalForce.force += projectOnForward / (Parameters.BACKWARD_DAMPENING * Mathf.Abs(ang));
            }
            var projectOnRight = transform.right * Vector3.Dot(transform.right, totalForce.force);
            totalForce.force -= projectOnRight;
            totalForce.force += projectOnRight / Parameters.LATERAL_DAMPENING;
            //ang = Mathf.Abs(Vector3.Dot(transform.right, totalForce.force.normalized));
            //if (ang > 0.5f)
            //{
            //    var projectOnRight = transform.right * Vector3.Dot(transform.right, totalForce.force);
            //    totalForce.force -= projectOnRight;
            //    totalForce.force += projectOnRight / (Parameters.LATERAL_DAMPENING * 2 * (ang - 0.5f));
            //}

            #endregion

            if (forceCapMultiple > 0)
            {
                float maxForce = forceCapMultiple * MASS * Parameters.DESIRED_SPEED / Parameters.T;
                if (totalForce.force.magnitude > maxForce)
                {
                    totalForce.force = totalForce.force.normalized * maxForce;
                }
            }

            return totalForce;
        }

        private Vector3 CalculateGoalForce()
        {
            var temp = nearestGoalPoint - transform.position;
            temp.y = 0;
            var desiredVel = temp.normalized * Parameters.DESIRED_SPEED;
            return MASS * (desiredVel - velocity) / Parameters.T;
        }

        private SEAN.Scenario.Agents.SocialForce CalculateAgentForce()
        {
            SEAN.Scenario.Agents.SocialForce agentForce = new SEAN.Scenario.Agents.SocialForce();

            lastBlockerDist = float.PositiveInfinity;
            lastBlockerSpeed = 0f;
            float destDist = SEAN.Util.Geometry.GroundPlaneDist(destPos, transform.position);
            float goalProximityScale = goalPriorityRadius > 0 ? Mathf.Clamp01(destDist / goalPriorityRadius) : 1f;

            foreach (GameObject n in neighbors)
            {
                if (GO2Agent.ContainsKey(n))
                {
                    MonoBehaviour neighbor = GO2Agent[n];
                    Vector3 dir = Vector3.zero;
                    float overlap = 0;
                    float neighborDist = float.PositiveInfinity;
                    Vector3 neighborVel = Vector3.zero;
                    float dampenFactor = 1f; // Initialize dampenFactor
                    float neighborRadius = RADIUS; // Default neighbor radius

                    // Check if the neighbor is a PWD agent
                    SFPWDAgent pwdNeighbor = null;
                    if (neighbor != null)
                    {
                        pwdNeighbor = neighbor as SFPWDAgent;
                    }

                    if (neighbor != null) // It's another agent
                    {
                        dir = transform.position - neighbor.transform.position;
                        dir.y = 0;

                        if (pwdNeighbor != null)
                        {
                            // Neighbor is PWD Agent: Use its specific larger radius
                            // Ensure SFPWDAgent has a public float pwdPersonalRadius;
                            neighborRadius = pwdNeighbor.pwdPersonalRadius; 
                        }
                        else
                        {
                            // Neighbor is a standard agent (or unknown type deriving from Base)
                            // We could potentially check other specific agent types here if needed
                            neighborRadius = RADIUS; // Assuming other agents use the standard RADIUS
                        }
                        
                        // Calculate overlap using this agent's RADIUS and the determined neighborRadius
                        neighborDist = dir.magnitude;
                        overlap = (RADIUS + neighborRadius) - neighborDist;
                        dir = dir.normalized;
                        // dampenFactor = 1f; // Already initialized
                    }
                    else // It's the robot
                    {
                        SEAN.Scenario.Robot robot = n.GetComponent<SEAN.Scenario.Robot>();
                        if (robot != null) // Check component exists
                        {
                            dir = transform.position - robot.transform.position;
                            dir.y = 0;
                            neighborDist = dir.magnitude;
                            overlap = (RADIUS + ROBOT_RADIUS) - neighborDist;
                            dir = dir.normalized;
                            neighbor = robot; // Keep reference for later use
                            var robotRB = robot.GetComponentInChildren<Rigidbody>();
                            neighborVel = robotRB.velocity;
                            dampenFactor = robotRB.velocity.magnitude > 0.1f ? robotRepulsion : 1f;
                        } else {
                            // Invalid entry in GO2Agent if neighbor is null and not Robot
                            continue;
                        }
                    }

                    // --- Keep the rest of the force calculation logic --- 
                    Vector3 goalDir = (nearestGoalPoint - transform.position).normalized;
                    var neighborAvatar = neighbor.GetComponent<SEAN.Scenario.Agents.Base>(); // Can be null for Robot
                    if (neighborAvatar != null)
                    {
                        neighborVel = neighborAvatar.velocity;
                    }
                    var neighborDir = neighborVel - velocity;

                    overlap += personalSpacePad;
                    //if (neighborAvatar.velocity.magnitude == 0)
                    //    overlap += 1f;

                    agentForce.force += Parameters.A * Mathf.Exp(overlap / Parameters.B) * dir * dampenFactor * goalProximityScale;

                    //var neighborDir = neighborAvatar != null && neighborAvatar.path.Count == 0 ? neighbor.transform.forward : neighborAvatar.path[0] - neighborAvatar.transform.position;
                    bool inFront = Vector3.Dot(-dir, goalDir) >= 0.5;
                    if (inFront && neighborDist < lastBlockerDist)
                    {
                        lastBlockerDist = neighborDist;
                        lastBlockerSpeed = neighborVel.magnitude;
                    }
                    bool approaching = Vector3.Dot(goalDir, neighborDir.normalized) < 0;
                    if (inFront && approaching)
                    {
                        //Debug.DrawLine(transform.position, neighbor.gameObject.transform.position, Color.red);
                        var sideStepScale = Vector3.Dot(-dir, goalDir);
                        agentForce.force += sideStepScale * Parameters.A / 10 * SEAN.Util.Geometry.Tangent(goalDir) * dampenFactor;

                        agentForce.anyAgentInFront = inFront;
                        agentForce.anyAgentApproaching = approaching;
                    }
                }
            }

            return agentForce;
        }

        private Vector3 CalculateWallForce()
        {
            closestPoints = new Dictionary<int, Vector3>();
            foreach (var obstacle in obstacles)
            {
                var obsBounds = obstacle.GetComponent<BoxCollider>().bounds;
                var agentBounds = GetComponentInChildren<Renderer>().bounds;
                var boundVolume = (obsBounds.max.x - obsBounds.min.x) * (obsBounds.max.y - obsBounds.min.y) * (obsBounds.max.z - obsBounds.min.z);
                var invalid = obsBounds.max.y < agentBounds.min.y + 0.1f || obsBounds.min.y > agentBounds.max.y;
                if (invalid)
                {
                    continue;
                }

                Vector3 closestPoint = obstacle.GetComponent<BoxCollider>().ClosestPoint(transform.position);
                int bin = (int)((Vector3.SignedAngle(transform.forward, closestPoint - transform.position, Vector3.up) + 180) / (360 / OBSTACLE_ANGLE_BINS)) % OBSTACLE_ANGLE_BINS;

                if (closestPoints.ContainsKey(bin))
                {
                    if ((closestPoints[bin] - transform.position).sqrMagnitude > (closestPoint - transform.position).sqrMagnitude)
                        closestPoints[bin] = closestPoint;
                }
                else
                {
                    closestPoints[bin] = closestPoint;
                }
            }

            var wallForce = Vector3.zero;
            foreach (var closestPoint in closestPoints.Values)
            {
                //Debug.DrawLine(transform.position + Vector3.up, new Vector3() { x = closestPoint.x, y = transform.position.y, z = closestPoint.z } + Vector3.up, Color.green);

                var wallNorm = transform.position - closestPoint;
                wallNorm.y = 0;
                var overlap = RADIUS - wallNorm.magnitude;

                wallForce += Parameters.WALL_A * Mathf.Exp(overlap / Parameters.WALL_B) * wallNorm;

                //var tangent = new Vector3(-wallNorm.z, 0, wallNorm.x);
                //wallForce += Parameters.WALL_KAPPA * (overlap > 0f ? overlap : 0) * Vector3.Dot(GetVelocity(), tangent) * tangent;
            }

            return wallForce;
        }

        #endregion


        protected override void OnDrawGizmosSelected()
        {
            base.OnDrawGizmosSelected();
            if (!ShowDebug) { return; }
            // Display the explosion radius when selected
            
            // Draw lines to neighbors
            foreach (GameObject n in neighbors) 
            {
                if (GO2Agent.ContainsKey(n))
                {
                    var neighbor = GO2Agent[n];
                    if (neighbor != null)
                    {
                        // Check if the neighbor is an SFPWDAgent
                        if (neighbor is SFPWDAgent)
                        {
                            Gizmos.color = Color.blue; // Blue line for PWD agents
                        }
                        else
                        {
                            Gizmos.color = Color.red; // Red line for other agents/robot
                        }
                        Gizmos.DrawLine(transform.position, neighbor.transform.position);
                    }
                    else // Special case for robot if GO2Agent stores null for it
                    {
                        SEAN.Scenario.Robot robot = n.GetComponent<SEAN.Scenario.Robot>();
                        if (robot != null)
                        {
                             Gizmos.color = Color.red; // Keep robot line red
                             Gizmos.DrawLine(transform.position, robot.transform.position);
                        }
                    }
                }
            }

            // Green lines to walls (existing logic)
            Gizmos.color = new Color(0, 1, 0, 0.5F);
            if (closestPoints != null)
            {
                foreach (var closestPoint in closestPoints.Values) // Use .Values for Dictionary
                {
                    Gizmos.DrawLine(transform.position, closestPoint);
                }
            }
        }
    }
}
