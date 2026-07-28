using UnityEngine;
using UnityEngine.SceneManagement;
using IVI;
using SEAN.Control;

namespace SessionReview
{
    /// <summary>
    /// Lightweight practice flow for the TestScene -- no SEAN rig, no task/trial stack:
    ///   1. Pick a character (wheelchair male/female or a walking avatar from
    ///      Resources/PlayerCharacters) and a session id; the character spawns at
    ///      <see cref="pwdStart"/> in manual mode.
    ///   2. Drive it to the WHITE goal (<see cref="pwdEnd"/>). Reaching it is only
    ///      acknowledged -- driving never stops; press "Try Robot Now" when ready.
    ///   3. Control switches to <see cref="robotBody"/> -- any robot-looking prefab
    ///      (e.g. Robot_URS) driven by the SAME ManualWheelchairController as the player.
    ///      Its unrelated scripts are auto-disabled and rigidbodies made kinematic.
    ///   4. Drive it to the YELLOW goal (<see cref="robotEnd"/>); reaching it is
    ///      acknowledged but you keep driving. R reloads; End Practice continues to the study.
    ///
    /// Scene requirements: a RandomAvatar spawner (isPwdPlayer ticked, spawnPlayerOnAwake
    /// UNticked), four ground markers with unique names, a robot body transform, and a
    /// baked NavMesh. No SEAN object, no SessionReviewManager needed.
    /// </summary>
    public class TestSceneFlowManager : MonoBehaviour
    {
        [Header("Markers (drag scene objects; names must be unique in the scene)")]
        public Transform pwdStart;
        [Tooltip("WHITE goal the human player drives to.")]
        public Transform pwdEnd;
        public Transform robotStart;
        [Tooltip("YELLOW goal the robot drives to.")]
        public Transform robotEnd;

        [Header("Flow")]
        [Tooltip("Ground distance (m) that counts as arrival at a goal.")]
        public float arriveDistance = 1.0f;
        public KeyCode restartKey = KeyCode.R;
        [Tooltip("Scene loaded by the End Practice button. Its onboarding continues with the same session id and selected character. Must be in Build Settings.")]
        public string sessionSceneName = "sidewalkNarrowroad";

        [Header("Wiring")]
        [Tooltip("Optional: a RandomAvatar spawner (isPwdPlayer ticked, spawnPlayerOnAwake unticked). Left empty, the same RocketboxRandomAnimatedAgent prefab the main scenes use is auto-loaded from Resources, so characters stay identical across scenes.")]
        public SEAN.Scenario.Agents.RandomAvatar playerSpawner;
        [Tooltip("Any robot-looking scene object (e.g. a Robot_URS instance). Its scripts are auto-disabled; it is driven like the player character.")]
        public Transform robotBody;
        [Tooltip("Optional camera for the robot phase; when empty a follow camera is created on the robot.")]
        public Camera robotViewCamera;

        [Header("Robot Driving Feel")]
        [Tooltip("Max forward speed (m/s) for the practice robot.")]
        public float robotMoveSpeed = 0.8f;
        [Tooltip("Max turn rate (deg/s) for the practice robot.")]
        public float robotTurnSpeed = 120f;

        // No terminal phase: reaching a goal never ends the practice, it only updates the
        // banner. The operator leaves via "End Practice -> Session" or R to restart.
        private enum Phase { SelectCharacter, DrivePlayer, DriveRobot }
        private Phase phase = Phase.SelectCharacter;

        /// <summary>True while the character-select page (with its Session ID text field) is up.</summary>
        public bool IsCharacterSelectOpen => phase == Phase.SelectCharacter;

        /// <summary>
        /// True once control has handed over to the robot (the "Try Robot Now" phase). GoalBeacon
        /// reads this to show the robot goal and hide the pedestrian one, and vice versa before it.
        /// </summary>
        public bool IsDrivingRobot => phase == Phase.DriveRobot;

        // Arrival is acknowledged (banner) but never freezes control, so participants can
        // keep driving past the goal to practice.
        private bool playerReachedGoal;
        private bool robotReachedGoal;

        private GameObject pwdPlayer;
        private ManualWheelchairController pwdController;
        private ManualWheelchairController robotController;
        private Camera robotCam;
        private string statusMessage = string.Empty;
        private string sessionIdInput = string.Empty;

        private GUIStyle bannerStyle;
        private GUIStyle cardLabelStyle;
        private GUIStyle hintStyle;
        private GUIStyle sessionFieldStyle;
        private Texture2D maleWheelchairThumb;
        private Texture2D femaleWheelchairThumb;

        void Start()
        {
            PlayerCharacterLibrary.Refresh();
            sessionIdInput = ParticipantSession.Id;

            // Same preview art the main onboarding uses, so the pages look alike.
            maleWheelchairThumb = Resources.Load<Texture2D>("PlayerCharactersUI/male_wheelchair_user");
            femaleWheelchairThumb = Resources.Load<Texture2D>("PlayerCharactersUI/female-wheelchair");

            EnsurePlayerSpawner();
            PrepareRobotBody();

            // Neutralize any leftover robot teleop (a stray SEAN rig / Robot prefab with a
            // VelocityController). It reads LeftShift+WASD/joystick and would drive some
            // other object, fighting the practice controls. The practice robot uses a
            // ManualWheelchairController instead, so no VelocityController is ever wanted here.
            DisableStrayVelocityControllers();
        }

        private void DisableStrayVelocityControllers()
        {
            foreach (var vc in FindObjectsOfType<VelocityController>(true))
            {
                if (vc == null) continue;
                vc.SetManualControlActive(false);
                vc.enabled = false;
                Debug.Log($"[TestSceneFlow] Disabled stray VelocityController on '{vc.gameObject.name}'.");
            }
        }

        /// <summary>
        /// Characters in the TestScene must match the normal scenes exactly, so when no
        /// spawner is wired in the Inspector, the SAME RocketboxRandomAnimatedAgent prefab
        /// the main scenes use is instantiated from Resources (it carries the animation
        /// controllers and wheelchair prefab references SpawnPwdPlayer needs). It is
        /// created under an inactive host so Awake sees spawnPlayerOnAwake = false.
        /// </summary>
        private void EnsurePlayerSpawner()
        {
            if (playerSpawner != null)
                return;

            var prefab = Resources.Load<GameObject>("Prefabs/RocketboxRandomAnimatedAgent");
            if (prefab == null)
            {
                Debug.LogError("[TestSceneFlow] Resources/Prefabs/RocketboxRandomAnimatedAgent not found; assign playerSpawner manually.", this);
                return;
            }

            var host = new GameObject("PlayerSpawnerHost");
            host.SetActive(false);
            Vector3 pos = pwdStart != null ? pwdStart.position : transform.position;
            Quaternion rot = pwdStart != null ? Quaternion.Euler(0f, pwdStart.eulerAngles.y, 0f) : Quaternion.identity;
            GameObject instance = Instantiate(prefab, pos, rot, host.transform);

            playerSpawner = instance.GetComponent<SEAN.Scenario.Agents.RandomAvatar>();
            if (playerSpawner == null)
            {
                Debug.LogError("[TestSceneFlow] Spawner prefab has no RandomAvatar component.", this);
                Destroy(host);
                return;
            }

            playerSpawner.isPwdPlayer = true;
            playerSpawner.spawnPlayerOnAwake = false;
            host.SetActive(true); // Awake runs now and defers to SpawnPwdPlayerNow()
        }

        void Update()
        {
            switch (phase)
            {
                case Phase.DrivePlayer:
                    // Acknowledge arrival for the banner, but never freeze -- the person
                    // keeps driving and advances to the robot with "Try Robot Now".
                    playerReachedGoal = pwdPlayer != null && pwdEnd != null &&
                        GroundDistance(pwdPlayer.transform.position, pwdEnd.position) <= arriveDistance;
                    if (Input.GetKeyDown(restartKey))
                        RestartScene();
                    break;

                case Phase.DriveRobot:
                    robotReachedGoal = robotBody != null && robotEnd != null &&
                        GroundDistance(robotBody.position, robotEnd.position) <= arriveDistance;
                    if (Input.GetKeyDown(restartKey))
                        RestartScene();
                    break;
            }
        }

        #region Robot body preparation

        /// <summary>
        /// Turns whatever robot prefab was dropped into the scene into a passive drivable
        /// dummy: every unrelated MonoBehaviour is disabled (RenderStreaming/URS/SEAN
        /// scripts would error or fight the controls), rigidbodies go kinematic, and a
        /// disabled ManualWheelchairController is armed for the robot phase.
        /// </summary>
        private void PrepareRobotBody()
        {
            if (robotBody == null)
            {
                Debug.LogWarning("[TestSceneFlow] No robotBody assigned; the robot phase will be skipped.", this);
                return;
            }

            // A prefab ASSET dragged from the Project window is not a scene object --
            // mutating it corrupts the asset and SetParent on it throws. Instantiate it
            // into the scene and drive the instance instead.
            if (!robotBody.gameObject.scene.IsValid())
            {
                Vector3 pos = robotStart != null ? robotStart.position : transform.position;
                Quaternion rot = robotStart != null
                    ? Quaternion.Euler(0f, robotStart.eulerAngles.y, 0f)
                    : Quaternion.identity;
                robotBody = Instantiate(robotBody.gameObject, pos, rot).transform;
                robotBody.name = "PracticeRobot";
                Debug.Log("[TestSceneFlow] Robot Body was a prefab asset; instantiated it into the scene as 'PracticeRobot'.");
            }

            foreach (var mb in robotBody.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb == null) continue; // missing-script slots
                if (mb is ManualWheelchairController || mb is WheelchairCameraSmoothing) continue;
                string ns = mb.GetType().Namespace ?? string.Empty;
                // Keep text/UI renderers so labels on the prop still show.
                if (ns.StartsWith("TMPro") || ns.StartsWith("UnityEngine")) continue;
                mb.enabled = false;
            }

            foreach (var rb in robotBody.GetComponentsInChildren<Rigidbody>(true))
                rb.isKinematic = true;

            foreach (var cam in robotBody.GetComponentsInChildren<Camera>(true))
                cam.enabled = false;

            if (robotStart != null)
            {
                robotBody.position = robotStart.position;
                robotBody.rotation = Quaternion.Euler(0f, robotStart.eulerAngles.y, 0f);
            }

            robotController = robotBody.GetComponent<ManualWheelchairController>();
            if (robotController == null)
                robotController = robotBody.gameObject.AddComponent<ManualWheelchairController>();

            robotController.enabled = false; // enabled (and Start()s) when the robot phase begins
            robotController.useWASD = true;
            robotController.manualUseArrowKeys = false;
            robotController.startInManualMode = true;
            robotController.toggleModeKey = KeyCode.None; // no auto mode: there is no SF agent on the robot
            robotController.moveSpeed = robotMoveSpeed;
            if (robotController.rotationSpeed > 0f && robotTurnSpeed > 0f)
            {
                float turnScale = robotTurnSpeed / robotController.rotationSpeed;
                robotController.inertiaAngularAcceleration *= turnScale;
                robotController.inertiaAngularCoastDeceleration *= turnScale;
                robotController.manualAngularAcceleration *= turnScale;
                robotController.rotationSpeed = robotTurnSpeed;
            }
        }

        #endregion

        #region Phase transitions

        private void SpawnSelectedCharacter(string characterId, SEAN.Scenario.Agents.PwdGender gender)
        {
            if (playerSpawner == null)
                return;

            ParticipantSession.Id = sessionIdInput;
            sessionIdInput = ParticipantSession.Id; // re-read trimmed value

            // Release the Session ID text field's keyboard focus, otherwise hotkeys
            // guarded by GUIUtility.keyboardControl (U tuning panel, O onboarding)
            // stay dead for the rest of the run.
            GUIUtility.keyboardControl = 0;

            SessionOnboardingSettings.SetPlayerCharacterSelection(characterId, gender);

            // Spawn at/toward our markers: the spawner resolves them by scene-object name.
            if (pwdStart != null) playerSpawner.startObjectName = pwdStart.name;
            if (pwdEnd != null) playerSpawner.goalObjectName = pwdEnd.name;

            playerSpawner.gameObject.SetActive(true);
            playerSpawner.SpawnPwdPlayerNow();

            pwdPlayer = GameObject.Find("PWDPlayer");
            if (pwdPlayer == null)
            {
                statusMessage = "Spawn failed -- check the Console.";
                return;
            }

            pwdController = pwdPlayer.GetComponent<ManualWheelchairController>();
            if (pwdController != null)
            {
                // Not yet initialized (InitAfterBase runs next frame), so this just arms
                // manual mode for startup.
                pwdController.manualUseArrowKeys = false;
                pwdController.ApplyStartupControlMode(true);
            }

            ActivatePlayerCameras();
            phase = Phase.DrivePlayer;
            statusMessage = string.Empty;

            // This is the tuning scene: put the joystick tuning panel up right away
            // ([U] still hides it).
            JoystickTuningOverlay.Show();
        }

        private void BeginRobotPhase()
        {
            if (robotBody == null || robotController == null)
            {
                statusMessage = "No robot body assigned on TestSceneFlowManager.";
                return;
            }

            // Exactly one controller may read the shared WASD/joystick input. Disable every
            // other driving script (the player's, any duplicate, any stray robot teleop),
            // otherwise leftovers keep consuming input and both bodies move together.
            foreach (var mwc in FindObjectsOfType<ManualWheelchairController>(true))
            {
                if (mwc == null || mwc == robotController) continue;
                mwc.enabled = false;
            }
            DisableStrayVelocityControllers();
            pwdController = pwdPlayer != null ? pwdPlayer.GetComponent<ManualWheelchairController>() : pwdController;

            if (robotStart != null)
            {
                robotBody.position = robotStart.position;
                robotBody.rotation = Quaternion.Euler(0f, robotStart.eulerAngles.y, 0f);
            }

            // Enabling runs the controller's Start(): it captures the spawn pose and,
            // with startInManualMode, goes straight to manual driving.
            robotController.enabled = true;

            ActivateRobotCamera();
            robotReachedGoal = false;
            phase = Phase.DriveRobot;
        }

        private void RestartScene()
        {
            Scene active = SceneManager.GetActiveScene();
            if (Application.CanStreamedLevelBeLoaded(active.name))
            {
                SceneManager.LoadScene(active.name);
            }
            else
            {
                statusMessage = "Cannot restart: add this scene to File > Build Settings.";
                Debug.LogWarning($"[TestSceneFlow] Scene '{active.name}' is not in Build Settings; runtime reload unavailable.");
            }
        }

        /// <summary>
        /// Hands over to the real session scene. Session id (ParticipantSession, with its
        /// joystick config file) and the selected character (SessionOnboardingSettings
        /// statics) carry over, so the main onboarding comes up pre-filled.
        /// </summary>
        private void EndPracticeToSession()
        {
            if (string.IsNullOrEmpty(sessionSceneName))
            {
                statusMessage = "No sessionSceneName configured on TestSceneFlowManager.";
                return;
            }

            if (!Application.CanStreamedLevelBeLoaded(sessionSceneName))
            {
                statusMessage = $"Cannot load '{sessionSceneName}': add it to File > Build Settings.";
                Debug.LogWarning($"[TestSceneFlow] Scene '{sessionSceneName}' is not in Build Settings.");
                return;
            }

            SceneManager.LoadScene(sessionSceneName);
        }

        #endregion

        #region Cameras

        private bool IsPlayerCamera(Camera cam)
        {
            if (cam == null || cam == robotCam) return false;
            string n = cam.name;
            if (n == "wheelchairCamera" || n == "PWDThirdPersonCamera" ||
                n == "PWDFirstPersonCamera" || n == "PWDOverheadCamera")
                return true;
            return cam.GetComponent<WheelchairCameraSmoothing>() != null;
        }

        private void ActivatePlayerCameras()
        {
            // WheelchairCameraSmoothing un-parents itself, so search the whole scene.
            Camera pwdCam = null;
            foreach (var smoothing in FindObjectsOfType<WheelchairCameraSmoothing>(true))
            {
                var cam = smoothing != null ? smoothing.GetComponent<Camera>() : null;
                if (cam != null && cam != robotCam) { pwdCam = cam; break; }
            }
            if (pwdCam == null && pwdPlayer != null)
                pwdCam = pwdPlayer.GetComponentInChildren<Camera>(true);
            if (pwdCam == null)
            {
                Debug.LogWarning("[TestSceneFlow] No player camera found.");
                return;
            }

            pwdCam.targetDisplay = 0;
            pwdCam.enabled = true;
            pwdCam.gameObject.SetActive(true);

            foreach (Camera cam in FindObjectsOfType<Camera>(true))
            {
                if (cam == pwdCam) continue;
                if (cam.name == "PWDOverheadCamera" || cam.name == "PWDFirstPersonCamera")
                {
                    // Keep the mini panels composited over the main player view.
                    cam.gameObject.SetActive(true);
                    cam.enabled = true;
                    cam.targetDisplay = 0;
                    if (cam.depth <= pwdCam.depth)
                        cam.depth = pwdCam.depth + 10f;
                    continue;
                }
                cam.enabled = false;
            }
        }

        private void ActivateRobotCamera()
        {
            foreach (Camera cam in FindObjectsOfType<Camera>(true))
            {
                if (IsPlayerCamera(cam))
                {
                    cam.enabled = false;
                    cam.targetDisplay = 1;
                }
            }

            if (robotCam == null && robotViewCamera != null)
                robotCam = robotViewCamera;
            if (robotCam == null)
                robotCam = CreateRobotFollowCamera();

            if (robotCam != null)
            {
                robotCam.gameObject.SetActive(true);
                robotCam.enabled = true;
                robotCam.targetDisplay = 0;
            }
            else if (Camera.main != null)
            {
                Camera.main.enabled = true;
            }
        }

        /// <summary>
        /// Third-person follow camera on the robot, mirroring the player camera setup
        /// (WheelchairCameraSmoothing un-parents itself and orbits smoothly). The robot's
        /// ManualWheelchairController enables the smoothing during its init.
        /// </summary>
        private Camera CreateRobotFollowCamera()
        {
            if (robotBody == null)
                return null;

            var camObj = new GameObject("RobotPracticeCamera");
            camObj.transform.SetParent(robotBody, false);
            Vector3 offset = new Vector3(0f, 2.2f, -2.6f);
            camObj.transform.position = robotBody.position + robotBody.rotation * offset;
            camObj.transform.LookAt(robotBody.position + Vector3.up * 0.5f);

            var cam = camObj.AddComponent<Camera>();
            cam.fieldOfView = 60f;
            cam.nearClipPlane = 0.1f;

            var smoothing = camObj.AddComponent<WheelchairCameraSmoothing>();
            smoothing.thirdPersonOffset = offset;
            return cam;
        }

        #endregion

        #region Helpers

        private static float GroundDistance(Vector3 a, Vector3 b)
        {
            Vector3 d = a - b;
            d.y = 0f;
            return d.magnitude;
        }

        #endregion

        #region GUI

        void OnGUI()
        {
            ReviewUiScale.Apply();

            if (bannerStyle == null)
            {
                bannerStyle = new GUIStyle(GUI.skin.box)
                {
                    fontSize = 18,
                    fontStyle = FontStyle.Bold,
                    alignment = TextAnchor.MiddleCenter,
                    wordWrap = true
                };
                cardLabelStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 15,
                    fontStyle = FontStyle.Bold,
                    alignment = TextAnchor.MiddleCenter
                };
                hintStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 13,
                    alignment = TextAnchor.MiddleCenter,
                    wordWrap = true
                };
                sessionFieldStyle = new GUIStyle(GUI.skin.textField)
                {
                    fontSize = 16,
                    alignment = TextAnchor.MiddleLeft,
                    padding = new RectOffset(10, 10, 6, 6)
                };
            }

            // Small session tag while practicing, so the operator can confirm who is running.
            if (phase != Phase.SelectCharacter && !string.IsNullOrEmpty(ParticipantSession.Id))
            {
                GUI.Label(new Rect((ReviewUiScale.Width - 300f) * 0.5f, 80f, 300f, 24f),
                    $"Session: {ParticipantSession.Id}", hintStyle);
            }

            switch (phase)
            {
                case Phase.SelectCharacter:
                    DrawCharacterSelect();
                    break;
                case Phase.DrivePlayer:
                    DrawBanner(playerReachedGoal
                        ? $"WHITE goal reached -- keep practicing as long as you like, then press \"Try Robot Now\".  ([{restartKey}] restart, [U] tuning)"
                        : $"Drive to the WHITE goal.  (W/S or joystick: speed, A/D: turn, H: stop, [{restartKey}] restart, [U] tuning)");
                    DrawFlowButtons();
                    break;
                case Phase.DriveRobot:
                    DrawBanner(robotReachedGoal
                        ? $"YELLOW goal reached -- practice complete. Keep driving, or press \"End Practice\".  ([{restartKey}] restart, [U] tuning)"
                        : $"Now drive the ROBOT to the YELLOW goal.  (W/S or joystick: speed, A/D: turn, H: stop, [{restartKey}] restart, [U] tuning)");
                    DrawFlowButtons();
                    break;
            }
        }

        // Small action row under the banner: skip straight to robot practice, and hand
        // over to the real session scene (links practice into the study flow).
        private void DrawFlowButtons()
        {
            const float buttonH = 34f;
            const float gapX = 12f;
            float tryW = 170f;
            float endW = 210f;

            bool showTryRobot = phase == Phase.DrivePlayer && robotBody != null;
            float total = (showTryRobot ? tryW + gapX : 0f) + endW;
            float x = (ReviewUiScale.Width - total) * 0.5f;
            float y = 110f;

            if (showTryRobot)
            {
                // Highlighted once the white goal has been reached, since that is the
                // natural moment to move on (nothing forces the switch).
                var prevBg = GUI.backgroundColor;
                if (playerReachedGoal) GUI.backgroundColor = new Color(0.3f, 0.6f, 0.35f);
                if (GUI.Button(new Rect(x, y, tryW, buttonH), "Try Robot Now"))
                    BeginRobotPhase();
                GUI.backgroundColor = prevBg;
                x += tryW + gapX;
            }

            if (GUI.Button(new Rect(x, y, endW, buttonH), "End Practice -> Session"))
                EndPracticeToSession();

            if (!string.IsNullOrEmpty(statusMessage))
            {
                GUI.Label(new Rect((ReviewUiScale.Width - 600f) * 0.5f, y + buttonH + 6f, 600f, 24f),
                    statusMessage, hintStyle);
            }
        }

        private void DrawBanner(string text)
        {
            float w = Mathf.Min(720f, ReviewUiScale.Width - 40f);
            Rect rect = new Rect((ReviewUiScale.Width - w) * 0.5f, 18f, w, 58f);
            GUI.Box(rect, text, bannerStyle);
        }

        private void DrawCharacterSelect()
        {
            const float cardW = 200f;
            const float cardH = 200f;
            const float gap = 16f;

            var options = PlayerCharacterLibrary.OptionsWithPreview;
            int cardCount = 2 + options.Count;
            int perRow = Mathf.Max(1, Mathf.Min(cardCount,
                Mathf.FloorToInt((ReviewUiScale.Width - 80f + gap) / (cardW + gap))));
            int rows = Mathf.CeilToInt(cardCount / (float)perRow);

            float gridW = perRow * (cardW + gap) - gap;
            float gridH = rows * (cardH + gap) - gap;
            float panelW = gridW + 48f;
            float panelH = gridH + 184f;
            float px = (ReviewUiScale.Width - panelW) * 0.5f;
            float py = (ReviewUiScale.Height - panelH) * 0.5f;

            GUI.Box(new Rect(px, py, panelW, panelH), GUIContent.none);
            GUI.Label(new Rect(px, py + 14f, panelW, 30f), "Practice Session", bannerStyle);
            GUI.Label(new Rect(px + 24f, py + 50f, panelW - 48f, 34f),
                "You will drive this character to the WHITE goal, then drive the robot to the YELLOW goal.",
                hintStyle);

            // Session id row: defaults to the previous session's value (PlayerPrefs)
            // until the operator edits it; saved when a character is picked.
            var idLabelStyle = new GUIStyle(hintStyle) { alignment = TextAnchor.MiddleLeft, fontSize = 15, fontStyle = FontStyle.Bold };
            GUI.Label(new Rect(px + 24f, py + 90f, 100f, 36f), "Session ID", idLabelStyle);
            sessionIdInput = GUI.TextField(new Rect(px + 130f, py + 90f, 240f, 36f),
                sessionIdInput ?? string.Empty, 64, sessionFieldStyle);
            GUI.Label(new Rect(px + 386f, py + 90f, panelW - 386f - 24f, 36f),
                "kept from the previous session until changed",
                new GUIStyle(hintStyle) { alignment = TextAnchor.MiddleLeft });

            float gx = px + 24f;
            float gy = py + 142f;
            int index = 0;

            Rect NextRect()
            {
                int col = index % perRow;
                int row = index / perRow;
                index++;
                return new Rect(gx + col * (cardW + gap), gy + row * (cardH + gap), cardW, cardH);
            }

            if (DrawCharacterCard(NextRect(), "Wheelchair (Male)", maleWheelchairThumb))
                SpawnSelectedCharacter(string.Empty, SEAN.Scenario.Agents.PwdGender.Male);
            if (DrawCharacterCard(NextRect(), "Wheelchair (Female)", femaleWheelchairThumb))
                SpawnSelectedCharacter(string.Empty, SEAN.Scenario.Agents.PwdGender.Female);

            foreach (var option in options)
            {
                if (option == null) continue;
                if (DrawCharacterCard(NextRect(), option.DisplayName, option.Thumbnail))
                    SpawnSelectedCharacter(option.Id, SEAN.Scenario.Agents.PwdGender.Male);
            }

            if (!string.IsNullOrEmpty(statusMessage))
                GUI.Label(new Rect(px + 24f, py + panelH - 30f, panelW - 48f, 24f), statusMessage, hintStyle);
        }

        private bool DrawCharacterCard(Rect rect, string label, Texture2D thumbnail)
        {
            GUI.Box(rect, GUIContent.none);

            Rect imageRect = new Rect(rect.x + 10f, rect.y + 10f, rect.width - 20f, rect.height - 54f);
            if (thumbnail != null)
                GUI.DrawTexture(imageRect, thumbnail, ScaleMode.ScaleToFit, true);
            else
                GUI.Label(imageRect, "(no preview)", hintStyle);

            GUI.Label(new Rect(rect.x + 8f, rect.yMax - 40f, rect.width - 16f, 30f), label, cardLabelStyle);

            return GUI.Button(rect, GUIContent.none, GUIStyle.none);
        }

        #endregion
    }
}
