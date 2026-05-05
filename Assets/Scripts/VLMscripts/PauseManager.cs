using UnityEngine;

public class PauseManager : MonoBehaviour
{
    public static PauseManager Instance { get; private set; }
    public bool isPaused = false; // this is the public variable that can be accessed from other scripts

    private void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }
        else
        {
            Destroy(gameObject);
        }
    }

    public void PauseGame()
    {
        isPaused = true;
        Time.timeScale = 0f; // Stops all physics-based and time-dependent updates
        AudioListener.pause = true; // Pause all game audio
    }

    public void UnpauseGame()
    {
        isPaused = false;
        Time.timeScale = 1f; // Resume physics-based and time-dependent updates
        AudioListener.pause = false; // Resume game audio
    }

    public bool IsGamePaused()
    {
        return isPaused;
    }

    private void Update()
    {
        // Toggle pause state when space key is pressed
        if (Input.GetKeyDown(KeyCode.Space))
        {
            if (RuntimeEditorManager.Instance != null && RuntimeEditorManager.Instance.isEditorActive)
                return;

            // Don't hijack Space during session review — SessionReviewManager
            // owns play/pause there, and letting PauseManager flip Time.timeScale
            // un-freezes live physics and makes the robot drift.
            var reviewManager = SessionReview.SessionReviewManager.Instance;
            if (reviewManager != null && reviewManager.IsReviewUiActive)
                return;

            if (isPaused)
            {
                UnpauseGame();
            }
            else
            {
                PauseGame();
            }
        }
    }
}
