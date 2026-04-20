using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using SessionReview;

public class SignalUIManager : MonoBehaviour
{
    private enum SignalFlowType
    {
        None,
        Lighting,
        Vlm
    }

    [Header("Entry UI")]
    [SerializeField] private Button sendSignalButton;
    [SerializeField] private GameObject signalSelectionPanel;
    [SerializeField] private Button confirmSelectionButton;
    [SerializeField] private Button cancelSelectionButton;
    [SerializeField] private Toggle lightingSignalToggle;
    [SerializeField] private Toggle vlmSignalToggle;

    [Header("Lighting Flow")]
    [SerializeField] private List<GameObject> lightingFlowObjects = new List<GameObject>();
    [SerializeField] private Button lightingDoneButton;

    [Header("VLM Flow")]
    [SerializeField] private UIManager vlmUIManager;
    [SerializeField] private GameObject vlmCaptureButton;
    [SerializeField] private CamCapture vlmCamCapture;

    private readonly Queue<SignalFlowType> pendingFlows = new Queue<SignalFlowType>();
    private SignalFlowType activeFlow = SignalFlowType.None;
    private bool isSignalSequenceActive;

    private void Awake()
    {
        if (sendSignalButton != null)
            sendSignalButton.onClick.AddListener(OpenSignalSelection);

        if (confirmSelectionButton != null)
            confirmSelectionButton.onClick.AddListener(ConfirmSignalSelection);

        if (cancelSelectionButton != null)
            cancelSelectionButton.onClick.AddListener(CancelSignalSelection);

        if (lightingDoneButton != null)
            lightingDoneButton.onClick.AddListener(CompleteLightingFlow);

        if (vlmUIManager != null)
            vlmUIManager.ResponseWindowClosed += HandleVlmResponseClosed;

        SetSendSignalButtonVisible(true);
        SetSignalSelectionVisible(false);
        SetLightingFlowVisible(false);
        ApplyVoiceLabelToVlmSignalToggle();
    }

    private void Update()
    {
        if (isSignalSequenceActive)
            return;

        bool robotPlayer = !SessionOnboardingSettings.HasCompletedOnboarding ||
                           SessionOnboardingSettings.PlayerMode == OnboardingPlayerMode.Robot;
        if (sendSignalButton != null && sendSignalButton.gameObject.activeSelf != robotPlayer)
            SetSendSignalButtonVisible(robotPlayer);
    }

    private void ApplyVoiceLabelToVlmSignalToggle()
    {
        if (vlmSignalToggle == null) return;
        foreach (var tmp in vlmSignalToggle.GetComponentsInChildren<TextMeshProUGUI>(true))
        {
            if (tmp == null) continue;
            if (string.Equals(tmp.text.Trim(), "VLM", System.StringComparison.OrdinalIgnoreCase))
                tmp.text = "Voice";
        }
    }

    private void OnDestroy()
    {
        if (sendSignalButton != null)
            sendSignalButton.onClick.RemoveListener(OpenSignalSelection);

        if (confirmSelectionButton != null)
            confirmSelectionButton.onClick.RemoveListener(ConfirmSignalSelection);

        if (cancelSelectionButton != null)
            cancelSelectionButton.onClick.RemoveListener(CancelSignalSelection);

        if (lightingDoneButton != null)
            lightingDoneButton.onClick.RemoveListener(CompleteLightingFlow);

        if (vlmUIManager != null)
            vlmUIManager.ResponseWindowClosed -= HandleVlmResponseClosed;
    }

    public void OpenSignalSelection()
    {
        if (isSignalSequenceActive)
            return;

        isSignalSequenceActive = true;
        pendingFlows.Clear();
        activeFlow = SignalFlowType.None;

        if (PauseManager.Instance != null && !PauseManager.Instance.IsGamePaused())
            PauseManager.Instance.PauseGame();

        if (vlmUIManager != null)
        {
            vlmUIManager.SuppressSignalButtons = true;
            vlmUIManager.ResetVlmUiToIdle();
        }

        if (vlmCamCapture != null)
            vlmCamCapture.SetCaptureButtonOverride(false);

        SetSendSignalButtonVisible(false);
        SetVlmCaptureButtonVisible(false);
        SetSignalSelectionVisible(true);
        SetLightingFlowVisible(false);
    }

    public void ConfirmSignalSelection()
    {
        pendingFlows.Clear();

        if (lightingSignalToggle != null && lightingSignalToggle.isOn)
            pendingFlows.Enqueue(SignalFlowType.Lighting);

        if (vlmSignalToggle != null && vlmSignalToggle.isOn)
            pendingFlows.Enqueue(SignalFlowType.Vlm);

        if (pendingFlows.Count == 0)
        {
            Debug.LogWarning("[SignalUIManager] No signal system selected.");
            return;
        }

        SetSignalSelectionVisible(false);
        StartNextFlow();
    }

    public void CancelSignalSelection()
    {
        FinishSequence();
    }

    public void CompleteLightingFlow()
    {
        if (activeFlow != SignalFlowType.Lighting)
            return;

        if (SessionReview.SessionReviewManager.Instance != null)
            SessionReview.SessionReviewManager.Instance.RecordLightingAnnotation();

        SetLightingFlowVisible(false);
        StartNextFlow();
    }

    public void StartVlmFlow()
    {
        if (vlmUIManager == null)
        {
            Debug.LogError("[SignalUIManager] Missing UIManager reference for VLM flow.");
            StartNextFlow();
            return;
        }

        vlmUIManager.UnpauseOnExitResponseWindow = false;
        SetSendSignalButtonVisible(false);
        vlmUIManager.BeginVlmSignalFlow();
        vlmUIManager.SuppressSignalButtons = true;
        SetVlmCaptureButtonVisible(false);

        if (vlmCamCapture != null)
            vlmCamCapture.SetCaptureButtonOverride(false);

        vlmUIManager.OnCamCapButtonPressed();
    }

    private void HandleVlmResponseClosed()
    {
        if (activeFlow != SignalFlowType.Vlm)
            return;

        StartNextFlow();
    }

    private void StartNextFlow()
    {
        activeFlow = SignalFlowType.None;

        if (pendingFlows.Count == 0)
        {
            FinishSequence();
            return;
        }

        activeFlow = pendingFlows.Dequeue();

        switch (activeFlow)
        {
            case SignalFlowType.Lighting:
                SetLightingFlowVisible(true);
                break;
            case SignalFlowType.Vlm:
                StartVlmFlow();
                break;
        }
    }

    private void FinishSequence()
    {
        pendingFlows.Clear();
        activeFlow = SignalFlowType.None;
        isSignalSequenceActive = false;

        SetSignalSelectionVisible(false);
        SetLightingFlowVisible(false);
        ResetToggles();

        if (vlmUIManager != null)
        {
            vlmUIManager.SuppressSignalButtons = false;
            vlmUIManager.UnpauseOnExitResponseWindow = true;
            vlmUIManager.ResetVlmUiToIdle();
        }

        if (vlmCamCapture != null)
            vlmCamCapture.SetCaptureButtonOverride(false);

        SetSendSignalButtonVisible(true);
        SetVlmCaptureButtonVisible(false);

        if (PauseManager.Instance != null && PauseManager.Instance.IsGamePaused())
            PauseManager.Instance.UnpauseGame();
    }

    private void SetSignalSelectionVisible(bool isVisible)
    {
        if (signalSelectionPanel != null)
            signalSelectionPanel.SetActive(isVisible);
    }

    private void SetLightingFlowVisible(bool isVisible)
    {
        foreach (GameObject lightingObject in lightingFlowObjects)
        {
            if (lightingObject != null)
                lightingObject.SetActive(isVisible);
        }
    }

    private void ResetToggles()
    {
        if (lightingSignalToggle != null)
            lightingSignalToggle.isOn = false;

        if (vlmSignalToggle != null)
            vlmSignalToggle.isOn = false;
    }

    private void SetSendSignalButtonVisible(bool isVisible)
    {
        if (sendSignalButton != null)
            sendSignalButton.gameObject.SetActive(isVisible);
    }

    private void SetVlmCaptureButtonVisible(bool isVisible)
    {
        if (vlmCaptureButton != null)
            vlmCaptureButton.SetActive(isVisible);
    }

}
