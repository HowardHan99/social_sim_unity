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

        // PWD PERSONAL RADIUS
        public float pwdPersonalRadius = 2 * RADIUS;

        // WHEELCHAIR-SPECIFIC VARIABLES
        public float desiredSpeed;

        // Wheelchair user wait before turning
        public bool isWaiting = false;
        private float waitTime = 3f; // Time in seconds to wait before turning
        private float currentWaitTime = 0f;
        private float rotationAngleOnBlock = 1f;
        private float maxStaticTime = 10f;
        private float timeBlocked = 0f;

        private float blockStartTime = -1f;
        private const float MAX_BLOCK_TIME = 10f;
        private bool forceUnblock = false;
        private float lastUnblockTime = -1f;
        private const float UNBLOCK_COOLDOWN = 10f; // Time before can be blocked again

        [Header("Debug Information")]
        [SerializeField] private List<GameObject> neighborsDisplay = new List<GameObject>();
        [SerializeField] private List<GameObject> blockDisplay = new List<GameObject>();

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

            // WHEELCHAIR SPECIFIC: Larger collision radius
            collisionCapsule.radius = RADIUS * 3;

            robotRepulsion = Random.value * (Parameters.ROBOT_REPULSION_DAMPENING_MAX - Parameters.ROBOT_REPULSION_DAMPENING_MIN) + Parameters.ROBOT_REPULSION_DAMPENING_MIN;

            // WHEELCHAIR SPECIFIC: Gaussian distributed desired speed
            float mean_speed_wheelchair = Parameters.DESIRED_SPEED_WHEELCHAIR; // Or any preferred mean value
            float stdDevSpeed = 0.2f; // Adjust the standard deviation as needed

            desiredSpeed = SampleGaussian(mean_speed_wheelchair, stdDevSpeed);
            desiredSpeed = Mathf.Clamp(desiredSpeed, Parameters.MIN_DESIRED_SPEED_WHEELCHAIR, Parameters.MAX_DESIRED_SPEED_WHEELCHAIR);
            velocity = new Vector3(desiredSpeed, 0, 0);
        }

        // WHEELCHAIR SPECIFIC METHODS
        private void ForceUnblock()
        {
            forceUnblock = true;
            lastUnblockTime = Time.time;
            blockStartTime = -1f;
            isWaiting = false;
            timeBlocked = 0f;
            blockDisplay.Clear();
            StopAllCoroutines();

            // Use a larger rotation angle
            float randomRotation = Random.Range(45f, 180f);
            transform.Rotate(0, randomRotation, 0);

            velocity = transform.forward * (Parameters.DESIRED_SPEED_WHEELCHAIR * 1.5f);
        }

        public bool IsPathBlocked()
        {
            // If we're in force unblock mode, ignore all blocking
            if (forceUnblock)
            {
                // Check if enough cooldown time has passed to allow blocking again
                if (Time.time - lastUnblockTime > UNBLOCK_COOLDOWN)
                {
                    forceUnblock = false;
                }
                else
                {
                    blockDisplay.Clear();
                    return false; // Continue ignoring blocks during cooldown
                }
            }

            bool isBlocked = false;
            // Check for blocking by wheelchairs
            foreach (GameObject neighbor in neighbors)
            {
                if (neighbor == null) continue;
                if (neighbor.GetComponent<SFPWDAgent>() != null)
                {
                    blockDisplay.Add(neighbor);
                    if (Vector3.Distance(transform.position, neighbor.transform.position) < 7 * RADIUS)
                    {
                        if (blockStartTime < 0)
                        {
                            blockStartTime = Time.time;
                        }
                        blockDisplay.Add(neighbor);
                        isBlocked = true;
                    }
                };

                if (Vector3.Distance(transform.position, neighbor.transform.position) < 3 * RADIUS) // adjust based on desired sensitivity
                {
                    if (blockStartTime < 0)
                    {
                        blockStartTime = Time.time;
                    }
                    blockDisplay.Add(neighbor);
                    isBlocked = true;
                }
            }

            // Check for blocking by obstacles
            foreach (GameObject obstacle in obstacles)
            {
                if (obstacle == null) continue;

                Collider obstacleCollider = obstacle.GetComponent<Collider>();
                if (obstacleCollider == null) continue;

                // Get closest point on obstacle
                Vector3 closestPoint = obstacleCollider.ClosestPoint(transform.position);
                Vector3 dirToObstacle = closestPoint - transform.position;
                dirToObstacle.y = 0;
                float distance = dirToObstacle.magnitude;
                float angle = Vector3.Angle(transform.forward, dirToObstacle);

                // Obstacles have the smallest thresholds
                if ((distance < 2f * RADIUS && angle < 90f) ||
                    (distance < 1.5f * RADIUS))
                {
                    if (blockStartTime < 0)
                    {
                        blockStartTime = Time.time;
                    }

                    blockDisplay.Add(obstacle);
                    isBlocked = true;
                }
            }

            // If blocked, update timeBlocked
            if (isBlocked)
            {
                timeBlocked += Time.deltaTime;

                // Check if we've exceeded maxStaticTime
                if (timeBlocked >= maxStaticTime)
                {
                    ForceUnblock();
                    return false;
                }
            }
            else
            {
                // Reset timers if not blocked
                blockStartTime = -1f;
                timeBlocked = 0f;
                blockDisplay.Clear();
            }

            return isBlocked;
        }

        void RotateOnSpot()
        {
            float rotationSpeed = 1;
            transform.Rotate(0, rotationSpeed * Time.deltaTime, 0);
            print("Rotation on spot performed. Velocity during rotation: " + velocity);
        }

        private IEnumerator WaitAndRotate()
        {
            isWaiting = true; // Set the waiting flag to true to prevent retriggering
            yield return new WaitForSeconds(waitTime); // Wait for the specified time
            RotateOnSpot(); // Perform the rotation after the wait
            isWaiting = false; // Reset the waiting flag
        }

        private float SampleGaussian(float mean, float stdDev)
        {
            float u1 = UnityEngine.Random.value; // Uniform(0,1] random doubles
            float u2 = UnityEngine.Random.value;
            float randStdNormal = Mathf.Sqrt(-2.0f * Mathf.Log(u1)) *
                                  Mathf.Sin(2.0f * Mathf.PI * u2); // Random normal(0,1)
            float randNormal = mean + stdDev * randStdNormal; // Random normal(mean,stdDev^2)
            return randNormal;
        }

        protected override Vector3 UpdateVelocity()
        {
            // WHEELCHAIR SPECIFIC: Check for blocking and handle waiting/rotation
            if (IsPathBlocked() && !forceUnblock)
            {
                velocity = Vector3.zero;  // Set velocity to zero when blocked
                if (!isWaiting)
                {  // Only trigger waiting if not already waiting
                    isWaiting = true;
                    StartCoroutine(WaitAndRotate());
                }
                return velocity;  // Return the zero velocity
            }
            else
            {
                isWaiting = false;  // Reset waiting state if not blocked
            }

            SEAN.Scenario.Agents.SocialForce totalForce = ComputeForce();
            var accel = totalForce.force / MASS;
            Vector3 nextVelocity = velocity + accel * Time.deltaTime;
            nextVelocity.y = 0;
            if (nextVelocity.sqrMagnitude > 0)
            {
                nextVelocity = nextVelocity.normalized * Mathf.Min(nextVelocity.magnitude, Parameters.MAX_VEL);
            }
            return nextVelocity;
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
                // WHEELCHAIR SPECIFIC: Track PWD neighbors for display
                if (other.gameObject.GetComponent<SFPWDAgent>() != null)
                {
                    neighborsDisplay.Add(other.gameObject);
                }
            }
            else if (other.gameObject.GetComponent<BoxCollider>() != null && other.gameObject.GetComponent<Renderer>() != null && !obstacles.Contains(other.gameObject))
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
            // WHEELCHAIR SPECIFIC: Use predictive force and combine with regular agent force
            SEAN.Scenario.Agents.SocialForce totalForce = ComputePredictiveAgentForce();
            totalForce.force += CalculateAgentForce().force * 0.3f;
            totalForce.force += CalculateGoalForce();
            totalForce.force += CalculateWallForce();

            #region Limit Backward/Lateral Motion

            var ang = Vector3.Dot(transform.forward, totalForce.force.normalized);
            if (ang < 0)
            {
                var projectOnForward = transform.forward * Vector3.Dot(transform.forward, totalForce.force);
                totalForce.force -= projectOnForward;
            }
            var projectOnRight = transform.right * Vector3.Dot(transform.right, totalForce.force);
            totalForce.force -= projectOnRight;
            totalForce.force += projectOnRight / Parameters.LATERAL_DAMPENING;

            #endregion

            return totalForce;
        }

        private Vector3 CalculateGoalForce()
        {
            var temp = nearestGoalPoint - transform.position;
            temp.y = 0;
            // WHEELCHAIR SPECIFIC: Use wheelchair desired speed
            var desiredVel = temp.normalized * Parameters.DESIRED_SPEED_WHEELCHAIR;
            return MASS * (desiredVel - velocity) / Parameters.T;
        }

        private SEAN.Scenario.Agents.SocialForce CalculateAgentForce()
        {
            SEAN.Scenario.Agents.SocialForce agentForce = new SEAN.Scenario.Agents.SocialForce();

            foreach (GameObject n in neighbors)
            {
                if (GO2Agent.ContainsKey(n))
                {
                    MonoBehaviour neighbor = GO2Agent[n];
                    Vector3 dir = Vector3.zero;
                    float overlap = 0;
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
                            // WHEELCHAIR SPECIFIC: Use PWD personal radius for wheelchair-wheelchair interaction
                            overlap = 2 * pwdPersonalRadius - dir.magnitude;
                        }
                        else
                        {
                            // WHEELCHAIR SPECIFIC: Use PWD personal radius for wheelchair-pedestrian interaction
                            overlap = (pwdPersonalRadius + RADIUS) - dir.magnitude;
                        }

                        dir = dir.normalized;
                        dampenFactor = 1f;
                    }
                    else // It's the robot
                    {
                        SEAN.Scenario.Robot robot = n.GetComponent<SEAN.Scenario.Robot>();
                        if (robot != null) // Check component exists
                        {
                            dir = transform.position - robot.transform.position;
                            dir.y = 0;
                            // WHEELCHAIR SPECIFIC: Use PWD personal radius for wheelchair-robot interaction
                            overlap = (pwdPersonalRadius + ROBOT_RADIUS) - dir.magnitude;
                            dir = dir.normalized;
                            neighbor = robot; // Keep reference for later use
                            var robotRB = robot.GetComponentInChildren<Rigidbody>();
                            dampenFactor = robotRB.velocity.magnitude > 0.1f ? robotRepulsion : 1f;
                        }
                        else
                        {
                            // Invalid entry in GO2Agent if neighbor is null and not Robot
                            continue;
                        }
                    }

                    // --- Keep the rest of the force calculation logic --- 
                    Vector3 goalDir = (nearestGoalPoint - transform.position).normalized;
                    var neighborAvatar = neighbor.GetComponent<SEAN.Scenario.Agents.Base>(); // Can be null for Robot
                    var neighborDir = (neighborAvatar == null ? neighbor.transform.forward : neighborAvatar.velocity) - velocity;

                    overlap += 0.5f;

                    agentForce.force += Parameters.A * Mathf.Exp(overlap / Parameters.B) * dir * dampenFactor;

                    bool inFront = Vector3.Dot(-dir, goalDir) >= 0.5;
                    bool approaching = Vector3.Dot(goalDir, neighborDir.normalized) < 0;
                    if (inFront && approaching)
                    {
                        var sideStepScale = Vector3.Dot(-dir, goalDir);
                        agentForce.force += sideStepScale * Parameters.A / 10 * SEAN.Util.Geometry.Tangent(goalDir) * dampenFactor;

                        agentForce.anyAgentInFront = inFront;
                        agentForce.anyAgentApproaching = approaching;
                    }
                }
            }

            return agentForce;
        }

        // WHEELCHAIR SPECIFIC: Predictive agent force calculation
        private SEAN.Scenario.Agents.SocialForce ComputePredictiveAgentForce()
        {
            SEAN.Scenario.Agents.SocialForce agentForce = new SEAN.Scenario.Agents.SocialForce();

            // Configuration parameters
            float timeHorizon = 2.0f;           // t_α: Anticipation time
            float deltaTime = Time.deltaTime;    // Δt: Simulation time step
            float dMin = RADIUS * 2;            // Minimum safe distance
            float dMid = RADIUS * 4;            // Medium safe distance
            float dMax = RADIUS * 6;            // Maximum influence distance

            // Step 1: Compute Desired Velocity - Use more stable velocity prediction
            Vector3 goalDir = (nearestGoalPoint - transform.position).normalized;
            Vector3 desiredVelocity = goalDir * Parameters.DESIRED_SPEED_WHEELCHAIR; // Use constant desired speed
            desiredVelocity.y = 0;

            // Apply velocity smoothing
            float smoothingFactor = 0.3f; // Adjust this value (0-1) to control smoothing
            desiredVelocity = Vector3.Lerp(velocity, desiredVelocity, smoothingFactor);

            // Step 2: Predict Own Future Position
            Vector3 myFuturePosition = transform.position + timeHorizon * desiredVelocity;
            VisualizePosition(myFuturePosition, Color.yellow);

            // Step 3: Detect Collisions and Compute Avoidance Force
            List<(GameObject agent, Vector3 futurePos, float collisionTime)> collisionSet = new List<(GameObject, Vector3, float)>();

            foreach (GameObject n in neighbors)
            {
                if (n == null || !GO2Agent.ContainsKey(n)) continue;

                MonoBehaviour neighbor = GO2Agent[n];
                if (neighbor == null) continue;

                // Get neighbor's velocity
                Vector3 neighborVelocity = Vector3.zero;
                float neighborRadius = RADIUS;

                if (neighbor is SEAN.Scenario.Agents.Base neighborAgent)
                {
                    neighborVelocity = neighborAgent.velocity;
                }
                else if (neighbor is SEAN.Scenario.Robot robot)
                {
                    var robotRB = robot.GetComponentInChildren<Rigidbody>();
                    if (robotRB != null) neighborVelocity = robotRB.velocity;
                    neighborRadius = ROBOT_RADIUS;
                }

                // Predict neighbor's future position
                Vector3 neighborFuturePosition = neighbor.transform.position + timeHorizon * neighborVelocity;
                VisualizePosition(neighborFuturePosition, new Color(0.5f, 0, 0.5f));

                // Calculate relative motion
                Vector3 relativePosition = neighborFuturePosition - myFuturePosition;
                Vector3 relativeVelocity = neighborVelocity - desiredVelocity;

                // Calculate time to collision (if it exists)
                float collisionTime = CalculateTimeToCollision(relativePosition, relativeVelocity, RADIUS + neighborRadius);

                if (collisionTime > 0 && collisionTime <= timeHorizon)
                {
                    collisionSet.Add((n, neighborFuturePosition, collisionTime));
                    // Visualize collision prediction
                    VisualizePosition(neighborFuturePosition, new Color(1f, 0, 0)); // Red for collision predicted
                    Debug.DrawLine(neighbor.transform.position + Vector3.up * 0.1f,
                                 neighborFuturePosition + Vector3.up * 0.1f,
                                 Color.red); // Red path for collision course

                    // Draw connection line between future positions
                    Debug.DrawLine(myFuturePosition + Vector3.up * 0.1f,
                                 neighborFuturePosition + Vector3.up * 0.1f,
                                 Color.red,
                                 deltaTime,
                                 false);
                }
                else
                {
                    // Visualize non-colliding neighbors
                    VisualizePosition(neighborFuturePosition, new Color(0.5f, 0, 0.5f)); // Purple for no collision
                    Debug.DrawLine(neighbor.transform.position + Vector3.up * 0.1f,
                                 neighborFuturePosition + Vector3.up * 0.1f,
                                 new Color(0.5f, 0, 0.5f)); // Purple path for safe course
                }
            }

            // Step 4: Compute Evasive Force
            Vector3 evasiveForce = Vector3.zero;
            foreach (var collision in collisionSet)
            {
                Vector3 futureRelativePos = myFuturePosition - collision.futurePos;
                float distance = futureRelativePos.magnitude;
                Vector3 direction = futureRelativePos.normalized;

                // Calculate force magnitude based on distance
                float magnitude = CalculateEvasiveForceMagnitude(distance, dMin, dMid, dMax);

                // Weight force by inverse of time to collision
                float timeWeight = 1.0f / Mathf.Max(collision.collisionTime, 0.1f);

                evasiveForce += direction * magnitude * timeWeight;
            }

            // Add evasive force to total force
            agentForce.force = evasiveForce;
            return agentForce;
        }

        // WHEELCHAIR SPECIFIC: Helper methods for predictive force calculation
        private float CalculateTimeToCollision(Vector3 relativePosition, Vector3 relativeVelocity, float combinedRadius)
        {
            // Quadratic equation coefficients: at² + bt + c = 0
            float a = Vector3.Dot(relativeVelocity, relativeVelocity);
            float b = 2.0f * Vector3.Dot(relativePosition, relativeVelocity);
            float c = Vector3.Dot(relativePosition, relativePosition) - combinedRadius * combinedRadius;

            // Solve quadratic equation
            float discriminant = b * b - 4 * a * c;
            if (discriminant < 0) return -1; // No collision

            float t1 = (-b - Mathf.Sqrt(discriminant)) / (2 * a);
            float t2 = (-b + Mathf.Sqrt(discriminant)) / (2 * a);

            // Return earliest positive time
            if (t1 > 0) return t1;
            if (t2 > 0) return t2;
            return -1; // No future collision
        }

        private float CalculateEvasiveForceMagnitude(float distance, float dMin, float dMid, float dMax)
        {
            if (distance < dMin)
            {
                // Strong repulsion for very close distances
                return Parameters.A * 2.0f;
            }
            else if (distance < dMid)
            {
                // Medium repulsion for intermediate distances
                return Parameters.A * (dMid - distance) / (dMid - dMin);
            }
            else if (distance < dMax)
            {
                // Weak repulsion for far distances
                return Parameters.A * 0.5f * (dMax - distance) / (dMax - dMid);
            }
            return 0;
        }

        private void VisualizePosition(Vector3 position, Color color)
        {
            Debug.DrawLine(position + Vector3.up * 0.1f, position + Vector3.up * 0.5f, color, Time.deltaTime);
            // Create a small sphere for better visualization
            Vector3[] points = new Vector3[8];
            for (int i = 0; i < 8; i++)
            {
                float angle = i * Mathf.PI / 4;
                points[i] = position + Vector3.up * 0.3f +
                            new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * 0.1f;
            }
            for (int i = 0; i < 7; i++)
            {
                Debug.DrawLine(points[i], points[i + 1], color, Time.deltaTime);
            }
            Debug.DrawLine(points[7], points[0], color, Time.deltaTime);
        }

        private Vector3 CalculateWallForce()
        {
            closestPoints = new Dictionary<int, Vector3>();
            Dictionary<int, float> closestPointMultipliers = new Dictionary<int, float>();

            foreach (var obstacle in obstacles)
            {
                var obsBounds = obstacle.GetComponent<BoxCollider>().bounds;
                var agentBounds = GetComponentInChildren<Renderer>().bounds;
                var boundVolume = (obsBounds.max.x - obsBounds.min.x) * (obsBounds.max.y - obsBounds.min.y) * (obsBounds.max.z - obsBounds.min.z);
                // WHEELCHAIR SPECIFIC: Modified height check
                var invalid = obsBounds.min.y > agentBounds.max.y;
                if (invalid)
                {
                    continue;
                }

                Vector3 closestPoint = obstacle.GetComponent<BoxCollider>().ClosestPoint(transform.position);
                int bin = (int)((Vector3.SignedAngle(transform.forward, closestPoint - transform.position, Vector3.up) + 180) / (360 / OBSTACLE_ANGLE_BINS)) % OBSTACLE_ANGLE_BINS;

                // WHEELCHAIR SPECIFIC: Get obstacle multiplier for custom obstacles
                float multiplier = GetObstacleMultiplier(obstacle);

                if (closestPoints.ContainsKey(bin))
                {
                    if ((closestPoints[bin] - transform.position).sqrMagnitude > (closestPoint - transform.position).sqrMagnitude)
                        closestPoints[bin] = closestPoint;
                    closestPointMultipliers[bin] = multiplier;
                }
                else
                {
                    closestPoints[bin] = closestPoint;
                    closestPointMultipliers[bin] = multiplier;
                }
            }

            var wallForce = Vector3.zero;
            foreach (var bin in closestPoints.Keys)
            {
                Vector3 closestPoint = closestPoints[bin];
                float multiplier = closestPointMultipliers[bin];

                var wallNorm = transform.position - closestPoint;
                wallNorm.y = 0;
                var overlap = RADIUS - wallNorm.magnitude;

                // WHEELCHAIR SPECIFIC: Use multiplier for wall force
                wallForce += multiplier * Parameters.WALL_A * Mathf.Exp(overlap / Parameters.WALL_B) * wallNorm.normalized;

                Debug.DrawLine(transform.position + Vector3.up, new Vector3() { x = closestPoint.x, y = transform.position.y, z = closestPoint.z } + Vector3.up, Color.green);
            }

            return wallForce;
        }

        // WHEELCHAIR SPECIFIC: Get obstacle multiplier for custom obstacles
        private static float GetObstacleMultiplier(GameObject obstacle)
        {
            //default multiplier set to none
            float multiplier = 1;

            // Check if the obstacle has the CustomObstacle component
            customObstacle customObstacle = obstacle.GetComponent<customObstacle>();
            if (customObstacle != null)
            {
                multiplier = customObstacle.disabledMultiplier;
            }

            return multiplier;
        }

        #endregion

        // WHEELCHAIR SPECIFIC: Enhanced gizmo drawing
        private void OnDrawGizmos()
        {
            if (!ShowDebug) { return; }
            Gizmos.color = new Color(1, 0, 0, 0.75F);
            Gizmos.DrawSphere(transform.position, 0.1f);
        }

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