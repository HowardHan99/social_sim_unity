using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.AI;

namespace IVI
{
    public class SFPWDAgent : SEAN.Scenario.Agents.Base
    {
        private const int OBSTACLE_ANGLE_BINS = 6;
        private const float PwdColliderCenterY = 0.78f;

        private SphereCollider perceptionSphere;

        [SerializeField] private List<GameObject> neighbors = new List<GameObject>();
        private HashSet<GameObject> obstacles = new HashSet<GameObject>();

        Dictionary<int, Vector3> closestPoints;

        private float robotRepulsion;
        private SEAN.Scenario.Robot robotAgent;

        public float pwdPersonalRadius = 2*RADIUS;

        [Header("Auto Speed Scaling (live scenario tuning)")]
        [Tooltip("Live speed multiplier for this pedestrian's autonomous (social-force) walking speed. Scales desired and max speed. 1 = default. Set live from the Agent Speed overlay.")]
        public float autoSpeedScale = 1.0f;

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

        [Header("Waypoint Navigation")]
        [Tooltip("When true, navigates between waypointStart and waypointGoal instead of using NavManager.")]
        public bool useWaypoints = false;
        public Vector3 waypointStart;
        public Vector3 waypointGoal;
        private bool headingToGoal = true;

        [Header("Robot Blocking Stop")]
        public bool stopForRobotWhenBlocked = true;
        public float robotBlockStopDistance = 1.3f;
        public float robotBlockClearDistance = 1.4f;
        public float robotBlockForwardAngleDeg = 70f;

        [Header("Robot Blocking Deadlock Break")]
        [Tooltip("After pausing robotBlockRunDelaySeconds for a blocking robot, bypass it: keep moving (social forces steer around) until the robot clears, instead of stopping again.")]
        public bool runAfterRobotBlockStop = true;
        public float robotBlockRunDelaySeconds = 2f;

        [Header("Debug Robot Blocking (Read-Only)")]
        public bool debugRobotBlocked;
        public float debugRobotBlockDistance;
        public float debugRobotBlockStoppedSeconds;
        public bool debugRobotBlockForceRunning;
        public float debugRobotBlockRunSeconds;
        public float debugRobotBlockRunDistance;

        private float robotBlockStopStartTime = -1f;
        private float robotBlockRunStartTime = -1f;
        private Vector3 robotBlockRunStartPosition;

        // Agent/robot repulsion captured by the last ComputeForce, used by ShouldYield
        private Vector3 lastAgentRepulsion;
        // Distance and speed of the nearest goal-ward neighbor, from the last ComputeForce
        private float lastBlockerDist = float.PositiveInfinity;
        private float lastBlockerSpeed;

        protected override void Start()
        {
            base.Start();

            if (collisionCapsule != null)
            {
                Vector3 center = collisionCapsule.center;
                center.y = PwdColliderCenterY;
                collisionCapsule.center = center;
            }

            if (SEAN.SEAN.instance != null)
            {
                robotAgent = SEAN.SEAN.instance.robot;
                var robot = robotAgent.gameObject;

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

            if (useWaypoints)
                Debug.Log($"[PWD] SFPWDAgent.Start: useWaypoints=true, start=({waypointStart.x:F1},{waypointStart.y:F1},{waypointStart.z:F1}), goal=({waypointGoal.x:F1},{waypointGoal.y:F1},{waypointGoal.z:F1})");
            else
                Debug.Log("[PWD] SFPWDAgent.Start: useWaypoints=false, using NavManager");
        }

        #region Waypoint Coroutine Override

        protected override IEnumerator Coroutine()
        {
            if (!useWaypoints)
            {
                IEnumerator baseRoutine = base.Coroutine();
                while (baseRoutine.MoveNext())
                    yield return baseRoutine.Current;
                yield break;
            }

            InitDest(headingToGoal ? waypointGoal : waypointStart);
            Debug.Log($"[PWD] Waypoint nav started -> {(headingToGoal ? "goal" : "start")}");

            while (true)
            {
                if (CloseEnough())
                {
                    headingToGoal = !headingToGoal;
                    Vector3 next = headingToGoal ? waypointGoal : waypointStart;
                    InitDest(next);
                    Debug.Log($"[PWD] Reached waypoint, heading to {(headingToGoal ? "goal" : "start")} at ({next.x:F1},{next.z:F1})");
                }
                else
                {
                    PlanNavigation();
                }

                yield return new WaitForSeconds(1f / plannerFPS);
            }
        }

        #endregion

        #region Coroutine Control (called by ManualWheelchairController)

        public void KillNavigationCoroutine()
        {
            StopAllCoroutines();
        }

        public void RestartNavigationCoroutine()
        {
            StopAllCoroutines();
            StartCoroutine(Coroutine());
        }

        #endregion

        protected override Vector3 UpdateVelocity()
        {
            if (ShouldStopForRobotBlock())
                return Vector3.zero;

            SEAN.Scenario.Agents.SocialForce totalForce = ComputeForce();
            var accel = totalForce.force / MASS;
            Vector3 nextVelocity = velocity + accel * Time.deltaTime;
            nextVelocity.y = 0;

            if (ShouldYield(nextVelocity))
                return Vector3.MoveTowards(velocity, Vector3.zero, yieldDeceleration * Time.deltaTime);

            if (nextVelocity.sqrMagnitude > 0)
            {
                nextVelocity = nextVelocity.normalized * Mathf.Min(nextVelocity.magnitude, Parameters.MAX_VEL * Mathf.Max(0f, autoSpeedScale));
            }
            return SmoothVelocity(nextVelocity);
        }

        // Yield (slow to a stop) when agent/robot repulsion would drive this agent
        // away from its goal; integrating the reversed force makes it pace back and
        // forth instead. Wall forces are excluded so they can still push the agent out.
        private bool ShouldYield(Vector3 nextVelocity)
        {
            if (yieldDeceleration <= 0f)
                return false;

            // The robot-block deadlock break must be allowed to move, otherwise
            // the stop -> run cycle degenerates into a permanent stop.
            if (debugRobotBlockForceRunning)
                return false;

            Vector3 goalVec = nearestGoalPoint - transform.position;
            goalVec.y = 0;
            float goalDist = goalVec.magnitude;
            if (goalDist <= 1e-2f)
                return false;

            // The goal is closer than whoever is pushing back: it can be reached
            // without contact, so keep walking.
            if (lastBlockerDist >= goalDist)
                return false;

            // A stationary blocker will never clear on its own; waiting would
            // last forever, so steer around it instead.
            if (lastBlockerSpeed <= 0.1f)
                return false;

            Vector3 goalDir = goalVec / goalDist;
            return Vector3.Dot(nextVelocity, goalDir) < 0f && Vector3.Dot(lastAgentRepulsion, goalDir) < 0f;
        }

        private Vector3 SmoothVelocity(Vector3 nextVelocity)
        {
            if (velocitySmoothingTime <= 0f)
                return nextVelocity;

            float alpha = 1f - Mathf.Exp(-Time.deltaTime / velocitySmoothingTime);
            return Vector3.Lerp(velocity, nextVelocity, alpha);
        }

        private bool ShouldStopForRobotBlock()
        {
            bool blocked = IsRobotBlockingRoute();
            if (!blocked)
            {
                ResetRobotBlockState();
                return false;
            }

            if (!runAfterRobotBlockStop)
                return true;

            // Bypassing: keep moving until the robot actually clears the route.
            // Re-imposing stop/run limits here degenerates into a permanent
            // stop-shuffle-stop cycle whenever the robot stays put.
            if (debugRobotBlockForceRunning)
            {
                UpdateRobotBlockRunDebug();
                return false;
            }

            if (robotBlockStopStartTime < 0f)
                robotBlockStopStartTime = Time.time;

            debugRobotBlockStoppedSeconds = Time.time - robotBlockStopStartTime;
            if (debugRobotBlockStoppedSeconds >= Mathf.Max(0f, robotBlockRunDelaySeconds))
            {
                StartRobotBlockForceRun();
                return false;
            }

            return true;
        }

        private bool IsRobotBlockingRoute()
        {
            if (!stopForRobotWhenBlocked || robotAgent == null || CloseEnough())
            {
                debugRobotBlocked = false;
                debugRobotBlockDistance = 0f;
                return false;
            }

            Vector3 toRobot = robotAgent.position - transform.position;
            toRobot.y = 0f;
            float robotDistance = toRobot.magnitude;
            debugRobotBlockDistance = robotDistance;
            float activeDistance = debugRobotBlocked || debugRobotBlockForceRunning
                ? Mathf.Max(robotBlockStopDistance, robotBlockClearDistance)
                : robotBlockStopDistance;

            if (robotDistance > activeDistance || robotDistance <= 1e-4f)
            {
                debugRobotBlocked = false;
                return false;
            }

            Vector3 toGoal = nearestGoalPoint - transform.position;
            toGoal.y = 0f;
            if (toGoal.sqrMagnitude <= 1e-4f)
            {
                debugRobotBlocked = false;
                return false;
            }

            // The goal is closer than the robot, so it can be reached without
            // ever contacting the robot: don't stop for it.
            if (robotDistance >= toGoal.magnitude)
            {
                debugRobotBlocked = false;
                return false;
            }

            Vector3 robotDir = toRobot / robotDistance;
            Vector3 goalDir = toGoal.normalized;
            Vector3 forward = transform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude <= 1e-4f)
                forward = goalDir;
            else
                forward.Normalize();

            float angleLimit = Mathf.Clamp(robotBlockForwardAngleDeg, 0f, 180f);
            bool inForwardCone = Vector3.Angle(forward, robotDir) <= angleLimit;
            bool inGoalCone = Vector3.Angle(goalDir, robotDir) <= angleLimit;
            bool inPathCorridor = IsRobotNearPathCorridor(toRobot, goalDir, activeDistance);

            // A robot off to the side (single wide cone) is not blocking: it must
            // be near the path corridor, or ahead both in facing and goal direction.
            debugRobotBlocked = inPathCorridor || (inForwardCone && inGoalCone);
            return debugRobotBlocked;
        }

        private void StartRobotBlockForceRun()
        {
            debugRobotBlockForceRunning = true;
            robotBlockRunStartTime = Time.time;
            robotBlockRunStartPosition = transform.position;
            UpdateRobotBlockRunDebug();
        }

        private void UpdateRobotBlockRunDebug()
        {
            if (robotBlockRunStartTime < 0f)
            {
                debugRobotBlockRunSeconds = 0f;
                debugRobotBlockRunDistance = 0f;
                return;
            }

            debugRobotBlockRunSeconds = Time.time - robotBlockRunStartTime;
            Vector3 runDelta = transform.position - robotBlockRunStartPosition;
            runDelta.y = 0f;
            debugRobotBlockRunDistance = runDelta.magnitude;
        }

        private void ResetRobotBlockState()
        {
            debugRobotBlocked = false;
            debugRobotBlockForceRunning = false;
            debugRobotBlockDistance = 0f;
            debugRobotBlockStoppedSeconds = 0f;
            debugRobotBlockRunSeconds = 0f;
            debugRobotBlockRunDistance = 0f;
            robotBlockStopStartTime = -1f;
            robotBlockRunStartTime = -1f;
        }

        private static bool IsRobotNearPathCorridor(Vector3 toRobot, Vector3 goalDir, float distanceLimit)
        {
            float alongPath = Vector3.Dot(toRobot, goalDir);
            if (alongPath <= 0f || alongPath > distanceLimit)
                return false;

            Vector3 lateral = toRobot - goalDir * alongPath;
            float corridorHalfWidth = Mathf.Max(0.35f, distanceLimit * 0.35f);
            return lateral.magnitude <= corridorHalfWidth;
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
                float maxForce = forceCapMultiple * MASS * Parameters.DESIRED_SPEED * Mathf.Max(1f, autoSpeedScale) / Parameters.T;
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
            var desiredVel = temp.normalized * Parameters.DESIRED_SPEED * Mathf.Max(0f, autoSpeedScale);
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
                    //Debug.DrawLine(transform.position, neighbor.gameObject.transform.position, Color.red);

                    Vector3 dir = Vector3.zero;
                    float overlap = 0;
                    float neighborDist = float.PositiveInfinity;
                    Vector3 neighborVel = Vector3.zero;
                    float dampenFactor = 0;

                    if (neighbor != null)
                    {
                        dir = transform.position - neighbor.transform.position;
                        dir.y = 0;
                        var pwdNeighbor = neighbor as SFPWDAgent;
                        float neighborRadius = pwdNeighbor != null ? pwdNeighbor.pwdPersonalRadius : RADIUS;
                        neighborDist = dir.magnitude;
                        overlap = (pwdPersonalRadius + neighborRadius) - neighborDist;
                        dir = dir.normalized;
                        dampenFactor = 1f;
                    }
                    else
                    {
                        SEAN.Scenario.Robot robot = n.GetComponent<SEAN.Scenario.Robot>();
                        dir = transform.position - robot.transform.position;
                        dir.y = 0;
                        neighborDist = dir.magnitude;
                        overlap = (pwdPersonalRadius + ROBOT_RADIUS) - neighborDist;
                        dir = dir.normalized;
                        neighbor = robot;
                        var robotRB = robot.GetComponentInChildren<Rigidbody>();
                        neighborVel = robotRB.velocity;
                        dampenFactor = robotRB.velocity.magnitude > 0.1f ? robotRepulsion : 1f;
                    }
                    Vector3 goalDir = (nearestGoalPoint - transform.position).normalized;
                    var neighborAvatar = neighbor.GetComponent<SEAN.Scenario.Agents.Base>();
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

            // --- Neighbor lines ---
            foreach (GameObject n in neighbors)
            {
                if (GO2Agent.ContainsKey(n))
                {
                    var neighbor = GO2Agent[n];
                    if (neighbor != null)
                    {
                        Gizmos.color = neighbor is SFPWDAgent ? Color.blue : Color.red;
                        Gizmos.DrawLine(transform.position, neighbor.transform.position);
                    }
                    else
                    {
                        SEAN.Scenario.Robot robot = n.GetComponent<SEAN.Scenario.Robot>();
                        if (robot != null)
                        {
                            Gizmos.color = Color.red;
                            Gizmos.DrawLine(transform.position, robot.transform.position);
                        }
                    }
                }
            }

            DrawObstacleDebug();
        }

        private void DrawObstacleDebug()
        {
            if (!Application.isPlaying) return;

            Vector3 agentPos = transform.position;
            Bounds agentBounds = default;
            var rend = GetComponentInChildren<Renderer>();
            if (rend != null) agentBounds = rend.bounds;

            // --- Show perception sphere ---
            Gizmos.color = new Color(1f, 1f, 0f, 0.08f);
            Gizmos.DrawWireSphere(agentPos, PERCEPTION_RADIUS);

            // --- Iterate all obstacles, highlight each one ---
            foreach (var obstacle in obstacles)
            {
                if (obstacle == null) continue;
                var boxCol = obstacle.GetComponent<BoxCollider>();
                if (boxCol == null) continue;

                var obsBounds = boxCol.bounds;
                bool heightFiltered = obsBounds.max.y < agentBounds.min.y + 0.1f
                                   || obsBounds.min.y > agentBounds.max.y;

                if (heightFiltered)
                {
                    Gizmos.color = new Color(0.5f, 0.5f, 0.5f, 0.25f);
                }
                else
                {
                    Gizmos.color = new Color(1f, 0.9f, 0f, 0.5f);
                }
                Gizmos.DrawWireCube(obsBounds.center, obsBounds.size);

#if UNITY_EDITOR
                var labelStyle = new GUIStyle(UnityEditor.EditorStyles.label);
                labelStyle.normal.textColor = heightFiltered ? Color.gray : Color.yellow;
                labelStyle.fontSize = 10;
                Vector3 closestPt = boxCol.ClosestPoint(agentPos);
                float dist = Vector3.Distance(agentPos, closestPt);
                string label = $"{obstacle.name}\nd={dist:F2}" + (heightFiltered ? " [FILTERED]" : "");
                UnityEditor.Handles.Label(obsBounds.center + Vector3.up * obsBounds.extents.y, label, labelStyle);
#endif
            }

            // --- Per-bin closest-point forces ---
            if (closestPoints == null || closestPoints.Count == 0) return;

            Vector3 totalWallForce = Vector3.zero;

            Color[] binColors = {
                Color.green, Color.cyan, new Color(1f, 0.5f, 0f),
                Color.magenta, new Color(0.5f, 1f, 0.5f), new Color(0.3f, 0.7f, 1f)
            };

            foreach (var kvp in closestPoints)
            {
                int bin = kvp.Key;
                Vector3 closestPoint = kvp.Value;
                Color binColor = binColors[bin % binColors.Length];

                Gizmos.color = binColor;
                Gizmos.DrawLine(agentPos + Vector3.up * 0.05f, new Vector3(closestPoint.x, agentPos.y, closestPoint.z) + Vector3.up * 0.05f);
                Gizmos.DrawSphere(new Vector3(closestPoint.x, agentPos.y, closestPoint.z) + Vector3.up * 0.05f, 0.05f);

                var wallNorm = agentPos - closestPoint;
                wallNorm.y = 0;
                float overlap = RADIUS - wallNorm.magnitude;
                Vector3 force = Parameters.WALL_A * Mathf.Exp(overlap / Parameters.WALL_B) * wallNorm;
                totalWallForce += force;

                float forceVizScale = 0.002f;
                Vector3 forceViz = force * forceVizScale;
                if (forceViz.magnitude > 5f) forceViz = forceViz.normalized * 5f;

                DrawArrow(agentPos + Vector3.up * 0.3f, forceViz, binColor);

#if UNITY_EDITOR
                var style = new GUIStyle(UnityEditor.EditorStyles.label);
                style.normal.textColor = binColor;
                style.fontSize = 9;
                float dist = wallNorm.magnitude;
                string info = $"bin{bin} d={dist:F2}\novlp={overlap:F2}\n|F|={force.magnitude:F1}";
                UnityEditor.Handles.Label(closestPoint + Vector3.up * 0.2f, info, style);
#endif
            }

            // --- Total wall force arrow ---
            {
                float forceVizScale = 0.002f;
                Vector3 totalViz = totalWallForce * forceVizScale;
                if (totalViz.magnitude > 5f) totalViz = totalViz.normalized * 5f;
                DrawArrow(agentPos + Vector3.up * 0.6f, totalViz, new Color(1f, 0f, 1f, 1f));
#if UNITY_EDITOR
                var style = new GUIStyle(UnityEditor.EditorStyles.boldLabel);
                style.normal.textColor = Color.magenta;
                style.fontSize = 11;
                UnityEditor.Handles.Label(agentPos + Vector3.up * 1.2f,
                    $"TOTAL WALL F: ({totalWallForce.x:F1}, {totalWallForce.z:F1})\n|F|={totalWallForce.magnitude:F1}\nObstacles: {obstacles.Count}  Bins: {closestPoints.Count}/{OBSTACLE_ANGLE_BINS}",
                    style);
#endif
            }
        }

        private static void DrawArrow(Vector3 from, Vector3 direction, Color color)
        {
            if (direction.sqrMagnitude < 0.0001f) return;
            Gizmos.color = color;
            Vector3 to = from + direction;
            Gizmos.DrawLine(from, to);
            float headSize = Mathf.Min(0.15f, direction.magnitude * 0.3f);
            Vector3 right = Quaternion.LookRotation(direction) * Quaternion.Euler(0, 150, 0) * Vector3.forward * headSize;
            Vector3 left  = Quaternion.LookRotation(direction) * Quaternion.Euler(0, -150, 0) * Vector3.forward * headSize;
            Gizmos.DrawLine(to, to + right);
            Gizmos.DrawLine(to, to + left);
        }
    }
}
