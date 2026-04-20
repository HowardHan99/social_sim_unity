using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

[DefaultExecutionOrder(-10000)]
public sealed class AudioListenerSingletonEnforcer : MonoBehaviour
{
    private const string BootstrapObjectName = "__AudioListenerSingletonEnforcer";

    private static AudioListenerSingletonEnforcer instance;
    private static bool isEnsuring;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Bootstrap()
    {
        if (instance != null)
            return;

        var bootstrapObject = new GameObject(BootstrapObjectName);
        DontDestroyOnLoad(bootstrapObject);
        instance = bootstrapObject.AddComponent<AudioListenerSingletonEnforcer>();
        EnsureSingleAudioListener();
    }

    private void OnEnable()
    {
        SceneManager.sceneLoaded += OnSceneLoaded;
        SceneManager.activeSceneChanged += OnActiveSceneChanged;
        EnsureSingleAudioListener();
    }

    private void OnDisable()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        SceneManager.activeSceneChanged -= OnActiveSceneChanged;
    }

    private void LateUpdate()
    {
        EnsureSingleAudioListener();
    }

    private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        EnsureSingleAudioListener();
    }

    private static void OnActiveSceneChanged(Scene previousScene, Scene nextScene)
    {
        EnsureSingleAudioListener();
    }

    public static void EnsureSingleAudioListener()
    {
        if (isEnsuring)
            return;

        isEnsuring = true;

        try
        {
            List<AudioListener> listeners = FindSceneListeners();
            AudioListener primary = SelectPrimaryListener(listeners);

            if (primary == null)
                return;

            for (int i = 0; i < listeners.Count; i++)
            {
                AudioListener listener = listeners[i];
                if (listener == null)
                    continue;

                bool shouldEnable = listener == primary && listener.gameObject.activeInHierarchy;
                if (listener.enabled != shouldEnable)
                    listener.enabled = shouldEnable;
            }
        }
        finally
        {
            isEnsuring = false;
        }
    }

    private static List<AudioListener> FindSceneListeners()
    {
        AudioListener[] allListeners = FindObjectsOfType<AudioListener>(true);
        List<AudioListener> sceneListeners = new List<AudioListener>(allListeners.Length);

        for (int i = 0; i < allListeners.Length; i++)
        {
            AudioListener listener = allListeners[i];
            if (listener == null)
                continue;

            if (!listener.gameObject.scene.IsValid())
                continue;

            sceneListeners.Add(listener);
        }

        return sceneListeners;
    }

    private static AudioListener SelectPrimaryListener(List<AudioListener> listeners)
    {
        Camera mainCamera = Camera.main;
        if (mainCamera != null)
        {
            AudioListener mainCameraListener = mainCamera.GetComponent<AudioListener>();
            if (mainCameraListener != null)
            {
                if (!listeners.Contains(mainCameraListener))
                    listeners.Add(mainCameraListener);

                return mainCameraListener;
            }
        }

        for (int i = 0; i < listeners.Count; i++)
        {
            AudioListener listener = listeners[i];
            if (IsActiveListener(listener) && listener.enabled)
                return listener;
        }

        for (int i = 0; i < listeners.Count; i++)
        {
            AudioListener listener = listeners[i];
            if (IsActiveListener(listener))
                return listener;
        }

        if (mainCamera != null)
        {
            AudioListener listener = mainCamera.gameObject.AddComponent<AudioListener>();
            listeners.Add(listener);
            return listener;
        }

        Camera[] allCameras = FindObjectsOfType<Camera>(true);
        for (int i = 0; i < allCameras.Length; i++)
        {
            Camera camera = allCameras[i];
            if (camera == null || !camera.gameObject.scene.IsValid())
                continue;

            AudioListener listener = camera.GetComponent<AudioListener>();
            if (listener == null)
                listener = camera.gameObject.AddComponent<AudioListener>();

            if (!listeners.Contains(listener))
                listeners.Add(listener);

            return listener;
        }

        GameObject fallbackObject = GameObject.Find(BootstrapObjectName);
        if (fallbackObject == null)
        {
            fallbackObject = new GameObject(BootstrapObjectName);
            DontDestroyOnLoad(fallbackObject);
        }

        AudioListener fallbackListener = fallbackObject.GetComponent<AudioListener>();
        if (fallbackListener == null)
            fallbackListener = fallbackObject.AddComponent<AudioListener>();

        if (!listeners.Contains(fallbackListener))
            listeners.Add(fallbackListener);

        return fallbackListener;
    }

    private static bool IsActiveListener(AudioListener listener)
    {
        return listener != null &&
               listener.gameObject != null &&
               listener.gameObject.scene.IsValid() &&
               listener.gameObject.activeInHierarchy;
    }
}
