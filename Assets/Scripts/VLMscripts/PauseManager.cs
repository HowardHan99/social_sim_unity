using UnityEngine;

public class PauseManager : MonoBehaviour
{
    public static PauseManager Instance { get; private set; }

    private bool isPaused = false;

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
}
