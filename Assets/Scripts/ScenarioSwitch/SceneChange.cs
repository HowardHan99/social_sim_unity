using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

public class SceneChange : MonoBehaviour
{
    [SerializeField] private List<string> sceneNames = new List<string>();
    private static int currentSceneIndex = 0;

    public int CurrentSceneIndex => currentSceneIndex;
    public int SceneCount => sceneNames.Count;
    public string CurrentSceneName => sceneNames.Count > 0 ? sceneNames[currentSceneIndex] : string.Empty;
    public IReadOnlyList<string> SceneNames => sceneNames;

    void Awake()
    {
        DontDestroyOnLoad(this.gameObject);

        if (sceneNames.Count > 0)
        {
            string activeScene = SceneManager.GetActiveScene().name;

            // Sync the index to whichever scene is currently open so any scene
            // in the list can be played directly without being redirected away.
            int found = sceneNames.IndexOf(activeScene);
            currentSceneIndex = found >= 0 ? found : 0;
        }
    }

    public void GoToNextScene()
    {
        if (sceneNames.Count < 1) return;
        currentSceneIndex = (currentSceneIndex + 1) % sceneNames.Count;
        SceneManager.LoadScene(sceneNames[currentSceneIndex]);
    }

    public void GoToPreviousScene()
    {
        if (sceneNames.Count < 1) return;
        currentSceneIndex = (currentSceneIndex - 1 + sceneNames.Count) % sceneNames.Count;
        SceneManager.LoadScene(sceneNames[currentSceneIndex]);
    }

    public void LoadSceneAtIndex(int index)
    {
        if (index < 0 || index >= sceneNames.Count) return;
        currentSceneIndex = index;
        SceneManager.LoadScene(sceneNames[currentSceneIndex]);
    }

    void Update()
    {
        bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);

        if (ctrl && Input.GetKeyDown(KeyCode.N))
            GoToNextScene();

        if (ctrl && Input.GetKeyDown(KeyCode.P))
            GoToPreviousScene();
    }
}
