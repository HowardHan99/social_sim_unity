using UnityEngine;

/// <summary>
/// Tab switches the full-screen view between first person and third person for
/// whichever agent the human is currently driving:
///   - Player rigs (wheelchair/walker/scooter "PWDPlayer" avatars, the practice
///     robot): the third-person follow camera (WheelchairCameraSmoothing) swaps
///     screen rect + depth with the rig's first-person camera, so the other view
///     stays visible in the top-right mini panel.
///   - SEAN robot: camera_third is the full-screen view and camera_first sits in
///     the top-right mini panel (mirroring the player layout); Tab swaps the two.
/// Rigs that have no first-person camera yet (e.g. the TestScene practice robot)
/// get one created on the fly as a top-right mini.
/// Self-bootstraps like GamepadCameraLook (no scene wiring). The layout is
/// re-asserted every frame, so role switches that rewrite camera depths
/// (ActivatePwdCameraAsMain and friends) are healed a frame later. Inactive
/// while session review / world building is up or an IMGUI text field has focus.
/// </summary>
[DefaultExecutionOrder(6000)]
public class AgentViewToggle : MonoBehaviour
{
    public KeyCode toggleKey = KeyCode.Tab;

    private bool firstPerson;

    // The currently managed player-style pair, with the screen roles (rect +
    // depth) each camera had when the pair was discovered. Toggling assigns the
    // two stored roles to the two cameras; it never derives them from current
    // values, so external depth bumps cannot corrupt the layout.
    private Camera pairThird;
    private Camera pairFirst;
    private Rect mainRect;
    private float mainDepth;
    private Rect miniRect;
    private float miniDepth;

    // Robot roles. The full-screen view MUST sit below every mini (overhead,
    // first/third, rear all ship at depth 0 with the main view at -1 on the
    // P3DX); raising the main view to the minis' depth hides them, so the main
    // role uses the LOWER of the two authored depths. The mini rect is the one
    // authored on camera_first when it ships as a mini (P3DX top-right corner);
    // robots whose first camera is full-screen (Jackal) get the standard slot.
    private Camera robotThird;
    private float robotMainDepth;
    private Rect robotMiniRect;
    private static readonly Rect robotMainRect = new Rect(0f, 0f, 1f, 1f);
    private static readonly Rect defaultRobotMiniRect = new Rect(0.72f, 0.72f, 0.25f, 0.25f);

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        if (FindObjectOfType<AgentViewToggle>() != null)
            return;

        var go = new GameObject("AgentViewToggle");
        DontDestroyOnLoad(go);
        go.AddComponent<AgentViewToggle>();
    }

    private void OnEnable()
    {
        UnityEngine.SceneManagement.SceneManager.sceneLoaded += OnSceneLoaded;
    }

    private void OnDisable()
    {
        UnityEngine.SceneManagement.SceneManager.sceneLoaded -= OnSceneLoaded;
    }

    private void OnSceneLoaded(UnityEngine.SceneManagement.Scene scene, UnityEngine.SceneManagement.LoadSceneMode mode)
    {
        firstPerson = false;
        pairThird = null;
        pairFirst = null;
        robotThird = null;
    }

    private void Update()
    {
        if (IsBlocked())
            return;

        // RB on the gamepad does the same swap, so a driver holding the pad never needs the
        // keyboard. Unaffected by IMGUI focus (it is not a typing key).
        if (SEAN.Input.GamepadHotkeys.ViewTogglePressed)
        {
            firstPerson = !firstPerson;
            return;
        }

        // Tab moves focus between IMGUI text fields (session id input etc.);
        // never treat it as a camera toggle while one has keyboard focus.
        if (GUIUtility.keyboardControl != 0)
            return;

        if (Input.GetKeyDown(toggleKey))
            firstPerson = !firstPerson;
    }

    private void LateUpdate()
    {
        if (IsBlocked())
            return;

        ApplyPlayerPairLayout();
        ApplyRobotLayout();
    }

    private static bool IsBlocked()
    {
        var review = SessionReview.SessionReviewManager.Instance;
        return review != null && (review.IsReviewModeActive || review.IsWorldBuildingModeActive);
    }

    // ── Player-style rigs (third-person follow camera + first-person camera) ──

    private void ApplyPlayerPairLayout()
    {
        Camera third = FindDrivenThirdPersonCamera();
        if (third == null)
            return;

        if (third != pairThird)
        {
            Camera first = FindFirstPersonPartner(third);
            if (first == null)
                return;

            pairThird = third;
            pairFirst = first;
            // Both cameras spawn in their canonical roles: follow camera
            // full-screen, first-person as the top-right mini.
            mainRect = third.rect;
            mainDepth = third.depth;
            miniRect = first.rect;
            miniDepth = first.depth;
        }

        if (pairFirst == null)
        {
            // Partner was destroyed (e.g. rig respawn): rediscover next frame.
            pairThird = null;
            return;
        }

        if (!pairFirst.enabled)
            pairFirst.enabled = true;

        if (firstPerson)
        {
            pairFirst.rect = mainRect;
            pairFirst.depth = mainDepth;
            pairThird.rect = miniRect;
            pairThird.depth = miniDepth;
        }
        else
        {
            pairThird.rect = mainRect;
            pairThird.depth = mainDepth;
            pairFirst.rect = miniRect;
            pairFirst.depth = miniDepth;
        }
    }

    private static Camera FindDrivenThirdPersonCamera()
    {
        foreach (var smoothing in FindObjectsOfType<IVI.WheelchairCameraSmoothing>())
        {
            if (smoothing == null)
                continue;

            var cam = smoothing.GetComponent<Camera>();
            if (cam != null && cam.enabled && cam.targetDisplay == 0)
                return cam;
        }
        return null;
    }

    private Camera FindFirstPersonPartner(Camera third)
    {
        var smoothing = third.GetComponent<IVI.WheelchairCameraSmoothing>();
        Transform rigRoot = smoothing != null ? smoothing.FollowAvatarRoot : null;
        if (rigRoot == null)
            return null;

        foreach (var level in FindObjectsOfType<IVI.FirstPersonCameraLevel>(true))
        {
            if (level == null || level.FollowTarget != rigRoot)
                continue;

            var cam = level.GetComponent<Camera>();
            if (cam != null)
                return cam;
        }

        return CreateFirstPersonCamera(third, smoothing, rigRoot);
    }

    /// <summary>
    /// Rigs like the TestScene practice robot only have the follow camera, so a
    /// first-person camera is created as the top-right mini (mirroring the
    /// layout AttachPlayerMiniScreens gives player characters).
    /// </summary>
    private static Camera CreateFirstPersonCamera(Camera third, IVI.WheelchairCameraSmoothing smoothing, Transform rigRoot)
    {
        var go = new GameObject("AutoFirstPersonCamera");
        go.transform.SetParent(rigRoot, false);

        var cam = go.AddComponent<Camera>();
        cam.fieldOfView = third.fieldOfView;
        cam.nearClipPlane = 0.1f;
        cam.farClipPlane = third.farClipPlane;
        cam.clearFlags = third.clearFlags;
        cam.cullingMask = third.cullingMask;
        cam.targetDisplay = third.targetDisplay;
        cam.rect = new Rect(0.72f, 0.72f, 0.25f, 0.25f);
        cam.depth = third.depth + 20f;

        var level = go.AddComponent<IVI.FirstPersonCameraLevel>();
        level.eyeOffset = new Vector3(0f, smoothing.lookAtHeight, 0.3f);

        CreateRearMini(third, rigRoot);
        return cam;
    }

    /// <summary>
    /// Bottom-right rear-view mini for rigs the SEAN Robot component doesn't
    /// manage (Robot.Start builds this for session robots). Always on; not part
    /// of the Tab swap.
    /// </summary>
    private static void CreateRearMini(Camera third, Transform rigRoot)
    {
        if (rigRoot.Find("AutoRearCamera") != null)
            return;

        var go = new GameObject("AutoRearCamera");
        go.transform.SetParent(rigRoot, false);
        go.transform.localPosition = new Vector3(0.2f, 0.6f, -0.35f);
        go.transform.localRotation = Quaternion.Euler(15f, 180f, 0f);

        var cam = go.AddComponent<Camera>();
        cam.fieldOfView = 70f;
        cam.nearClipPlane = 0.05f;
        cam.farClipPlane = third.farClipPlane;
        cam.clearFlags = third.clearFlags;
        cam.cullingMask = third.cullingMask;
        cam.targetDisplay = third.targetDisplay;
        cam.rect = new Rect(0.72f, 0.03f, 0.25f, 0.25f);
        cam.depth = third.depth + 20f;
    }

    // ── SEAN robot (camera_third / camera_first, both full-screen) ──

    private void ApplyRobotLayout()
    {
        var sean = SEAN.SEAN.instance;
        var robot = sean != null ? sean.robot : null;
        if (robot == null || robot.camera_third == null || robot.camera_first == null)
            return;

        Camera third = robot.camera_third;
        Camera first = robot.camera_first;
        if (!third.enabled || !first.enabled || third.targetDisplay != 0 || first.targetDisplay != 0)
            return;

        if (third != robotThird)
        {
            robotThird = third;
            robotMainDepth = Mathf.Min(third.depth, first.depth);
            robotMiniRect = first.rect.width < 0.99f ? first.rect : defaultRobotMiniRect;
        }

        Camera main = firstPerson ? first : third;
        Camera mini = firstPerson ? third : first;
        main.rect = robotMainRect;
        main.depth = robotMainDepth;
        mini.rect = robotMiniRect;
        mini.depth = robotMainDepth + 1f;
    }
}
