using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

public class SceneChange : MonoBehaviour
{
    [SerializeField] private List<string> sceneNames = new List<string>();
    private static int currentSceneIndex = 0;

    void Awake()
    {
        DontDestroyOnLoad(this.gameObject);

        if (sceneNames.Count > 0)
        {
            string activeScene = SceneManager.GetActiveScene().name;

            // If we're not already in the first scene, load it
            if (activeScene != sceneNames[0])
            {
                currentSceneIndex = 0;
                SceneManager.LoadScene(sceneNames[currentSceneIndex]);
            }
            else
            {
                currentSceneIndex = 0;
            }
        }
    }

    public void GoToNextScene()
    {
        if (sceneNames.Count < 1) return;

        // Advance the index and wrap around if necessary
        currentSceneIndex = (currentSceneIndex + 1) % sceneNames.Count;
        SceneManager.LoadScene(sceneNames[currentSceneIndex]);
    }

    void Update()
    {
        // Hold Ctrl and press N to go to the next scene
        if ((Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl))
            && Input.GetKeyDown(KeyCode.N))
        {
            GoToNextScene();
        }
    }
}
