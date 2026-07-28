using UnityEngine;

/// <summary>
/// Mirrors the auto (non-player) agent onto the researcher's second monitor, so a
/// session can be watched — and taken over — without stealing the participant's
/// screen.
///
/// Which agent counts as "auto" follows the onboarding role, not the control mode:
/// the human drives one role and the other one runs itself, so
///   - PlayerMode == Robot (default) → mirror the PWD (social-force SFPWDAgent),
///   - PlayerMode == Human           → mirror the SEAN robot (ROS /cmd_vel).
/// The mirrored rig gets its third-person view full-screen on display 1 ("Display 2"
/// in the Unity UI) plus the matching first-person mini in the top-right corner.
///
/// Taking over needs no extra wiring — it is the existing per-agent toggle, and the
/// non-player role is already remapped to the arrow keys by
/// SessionReviewManager.ApplyStartupControlDefaults so the two agents never move
/// together:
///   LeftShift  → SEAN robot  manual ⇄ ROS   (VelocityController)
///   RightShift → PWD avatar  manual ⇄ auto  (ManualWheelchairController)
///
/// Self-bootstraps like AgentViewToggle (no scene wiring). The layout is re-asserted
/// every frame because SessionReviewManager rewrites camera enabled/targetDisplay
/// wholesale on every role or trial transition (ActivatePwdCameraAsMain,
/// RestoreRobotGameplayCameras); those stomps heal a frame later. Runs after
/// AgentViewToggle (execution order 6000) so display-1 assignment wins.
///
/// Editor note: Display.Activate() is a standalone-player API. In the Editor the
/// mirror only shows up if a second Game view window is opened and set to
/// "Display 2".
/// </summary>
[DefaultExecutionOrder(6100)]
public class ResearcherDisplay : MonoBehaviour
{
    [Tooltip("Unity display index for the researcher view. 1 = 'Display 2'.")]
    public int displayIndex = 1;

    [Tooltip("Turn the whole mirror off (releases any camera it holds).")]
    public bool mirrorEnabled = true;

    [Tooltip("Also mirror the agent's first-person view as a top-right mini panel.")]
    public bool showFirstPersonMini = true;

    private static readonly Rect fullScreenRect = new Rect(0f, 0f, 1f, 1f);
    private static readonly Rect firstPersonMiniRect = new Rect(0.72f, 0.72f, 0.25f, 0.25f);

    // Cameras currently held on the researcher display, each with the targetDisplay
    // it had when taken. Releasing restores that value so the camera goes back
    // exactly where its owner (SessionReviewManager) expects to find it.
    private Camera heldMain;
    private int heldMainDisplay;
    private Camera heldMini;
    private int heldMiniDisplay;

    // TestScene runs its own single-screen camera flow (TestSceneFlowManager owns
    // ActivatePlayerCameras/ActivateRobotCamera), and its practice robot is a player
    // rig with a WheelchairCameraSmoothing camera — mirroring there would yank the
    // participant's own view onto the second monitor. Re-evaluated per scene.
    private bool sceneOwnsItsCameras;

    private bool displaysActivated;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        if (FindObjectOfType<ResearcherDisplay>() != null)
            return;

        var go = new GameObject("ResearcherDisplay");
        DontDestroyOnLoad(go);
        go.AddComponent<ResearcherDisplay>();
    }

    private void OnEnable()
    {
        UnityEngine.SceneManagement.SceneManager.sceneLoaded += OnSceneLoaded;
        ActivateExtraDisplays();
        RefreshSceneOwnership();
    }

    private void OnDisable()
    {
        UnityEngine.SceneManagement.SceneManager.sceneLoaded -= OnSceneLoaded;
        Release();
    }

    private void OnSceneLoaded(UnityEngine.SceneManagement.Scene scene, UnityEngine.SceneManagement.LoadSceneMode mode)
    {
        heldMain = null;
        heldMini = null;
        RefreshSceneOwnership();
    }

    private void RefreshSceneOwnership()
    {
        sceneOwnsItsCameras = FindObjectOfType<SessionReview.TestSceneFlowManager>() != null;
    }

    /// <summary>
    /// Display.displays[0] is always on; every attached monitor past it has to be
    /// activated once before a camera targeting it renders anything.
    /// </summary>
    private void ActivateExtraDisplays()
    {
        if (displaysActivated)
            return;

        displaysActivated = true;
        for (int i = 1; i < Display.displays.Length; i++)
            Display.displays[i].Activate();

        Debug.Log($"[ResearcherDisplay] {Display.displays.Length} display(s) connected; " +
                  $"auto agent mirrored to display {displayIndex} (\"Display {displayIndex + 1}\")");
    }

    private void LateUpdate()
    {
        if (!mirrorEnabled || sceneOwnsItsCameras || IsBlocked())
        {
            Release();
            return;
        }

        Camera main, mini;
        ResolveAutoAgentCameras(out main, out mini);
        if (!showFirstPersonMini)
            mini = null;

        // Two independent slots: a camera that is not claimable yet (still serving as
        // somebody's display-0 view) simply leaves its slot empty and is retried next
        // frame, without disturbing the other slot.
        UpdateSlot(ref heldMain, ref heldMainDisplay, main, fullScreenRect);
        UpdateSlot(ref heldMini, ref heldMiniDisplay, heldMain != null ? mini : null, firstPersonMiniRect);

        // Composite the panel over the full-screen view within its own rect.
        if (heldMini != null && heldMini.depth <= heldMain.depth)
            heldMini.depth = heldMain.depth + 10f;
    }

    private void UpdateSlot(ref Camera held, ref int previousDisplay, Camera wanted, Rect rect)
    {
        if (wanted != held)
        {
            ReleaseOne(ref held, previousDisplay);
            if (wanted != null && IsAvailable(wanted))
            {
                held = wanted;
                previousDisplay = wanted.targetDisplay;
            }
        }

        if (held != null)
            Show(held, rect);
    }

    private static bool IsBlocked()
    {
        var review = SessionReview.SessionReviewManager.Instance;
        return review != null && (review.IsReviewModeActive || review.IsWorldBuildingModeActive);
    }

    /// <summary>
    /// A camera that is live on display 0 is somebody's main view (onboarding, the
    /// participant's own rig); never pull one of those onto the second monitor.
    /// </summary>
    private static bool IsAvailable(Camera cam)
    {
        return !(cam.enabled && cam.targetDisplay == 0);
    }

    private void Show(Camera cam, Rect rect)
    {
        if (!cam.gameObject.activeSelf)
            cam.gameObject.SetActive(true);
        cam.targetDisplay = displayIndex;
        cam.rect = rect;
        if (!cam.enabled)
            cam.enabled = true;
    }

    /// <summary>
    /// Hands a camera back only if it is still parked on the researcher display —
    /// if SessionReviewManager has already claimed it for display 0 (role switch),
    /// leaving it alone is what keeps the participant's view intact.
    /// </summary>
    private void Release()
    {
        ReleaseOne(ref heldMain, heldMainDisplay);
        ReleaseOne(ref heldMini, heldMiniDisplay);
    }

    private void ReleaseOne(ref Camera cam, int previousDisplay)
    {
        if (cam != null && cam.targetDisplay == displayIndex)
        {
            cam.enabled = false;
            cam.targetDisplay = previousDisplay;
        }
        cam = null;
    }

    private static void ResolveAutoAgentCameras(out Camera main, out Camera mini)
    {
        main = null;
        mini = null;

        if (SessionReview.SessionOnboardingSettings.PlayerMode == SessionReview.OnboardingPlayerMode.Human)
        {
            // Human drives the PWD, so the robot is the one running itself.
            var sean = SEAN.SEAN.instance;
            var robot = sean != null ? sean.robot : null;
            if (robot == null)
                return;

            main = robot.camera_third;
            mini = robot.camera_first;
            return;
        }

        // Human drives the robot: mirror the PWDPlayer rig. Its third-person camera
        // is the one carrying WheelchairCameraSmoothing (it un-parents itself to
        // orbit, so a name/hierarchy lookup would miss it).
        foreach (var smoothing in FindObjectsOfType<IVI.WheelchairCameraSmoothing>(true))
        {
            var cam = smoothing != null ? smoothing.GetComponent<Camera>() : null;
            if (cam != null)
            {
                main = cam;
                break;
            }
        }

        if (main == null)
            return;

        foreach (var cam in FindObjectsOfType<Camera>(true))
        {
            if (cam.name == "PWDFirstPersonCamera")
            {
                mini = cam;
                break;
            }
        }
    }
}
