using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

public class SceneChange : MonoBehaviour
{
    [SerializeField] private string outdoorSceneName = "Outdoor";
    [SerializeField] private string warehouseSceneName = "Warehouse";
    
    // Flag to track which scene is currently active
    private static string currentSceneName;
    
    void Awake()
    {
        // Make this script persist between scenes
        DontDestroyOnLoad(this.gameObject);
        
        // Initialize current scene
        currentSceneName = SceneManager.GetActiveScene().name;
        
        // Ensure we start in the Outdoor scene
        if (currentSceneName != outdoorSceneName)
        {
            SceneManager.LoadScene(outdoorSceneName);
            currentSceneName = outdoorSceneName;
        }
    }

    // Function to switch to the Warehouse scene
    public void GoToWarehouse()
    {
        if (currentSceneName != warehouseSceneName)
        {
            Debug.Log("Switching to Warehouse scene");
            SceneManager.LoadScene(warehouseSceneName);
            currentSceneName = warehouseSceneName;
        }
    }
    
    // Function to switch to the Outdoor scene
    public void GoToOutdoor()
    {
        if (currentSceneName != outdoorSceneName)
        {
            Debug.Log("Switching to Outdoor scene");
            SceneManager.LoadScene(outdoorSceneName);
            currentSceneName = outdoorSceneName;
        }
    }
    
    // Update is called once per frame
    void Update()
    {
        // Press W to switch to Warehouse scene
        if (Input.GetKeyDown(KeyCode.W))
        {
            GoToWarehouse();
        }
        // Press O to switch to Outdoor scene (if you're in Warehouse)
        else if (Input.GetKeyDown(KeyCode.O))
        {
            GoToOutdoor();
        }
    }
}
