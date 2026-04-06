using System.Collections;
using UnityEngine;

public class RobotSignalLightController : MonoBehaviour
{
    private enum SignalSide
    {
        Left,
        Right,
        Both
    }

    [Header("Light References")]
    [SerializeField] private Light[] leftLights;
    [SerializeField] private Light[] rightLights;
    [SerializeField] private Renderer[] leftSignalRenderers;
    [SerializeField] private Renderer[] rightSignalRenderers;

    [Header("Flash Settings")]
    [Min(1)]
    [SerializeField] private int flashCount = 3;
    [Min(0.05f)]
    [SerializeField] private float onDuration = 0.2f;
    [Min(0.05f)]
    [SerializeField] private float offDuration = 0.2f;
    [SerializeField] private bool useUnscaledTime;

    // [Header("Optional Keyboard Test")]
    // [SerializeField] private bool enableKeyboardTest = true;

    [Header("Renderer Emission")]
    [SerializeField] private Color emissionOnColor = new Color(1f, 0.4f, 0f, 1f);
    [Min(0f)]
    [SerializeField] private float emissionIntensity = 2f;

    private static readonly int EmissionColor = Shader.PropertyToID("_EmissionColor");

    private Coroutine activeFlashRoutine;
    private MaterialPropertyBlock propertyBlock;

    private void Awake()
    {
        propertyBlock = new MaterialPropertyBlock();
        SetAllSignals(false);
    }

    private void Update()
    {
        // if (!enableKeyboardTest)
        // {
        //     return;
        // }

        // if (Input.GetKeyDown(KeyCode.LeftArrow) || Input.GetKeyDown(KeyCode.Q))
        // {
        //     FlashLeft();
        // }

        // if (Input.GetKeyDown(KeyCode.RightArrow) || Input.GetKeyDown(KeyCode.E))
        // {
        //     FlashRight();
        // }

        // if (Input.GetKeyDown(KeyCode.UpArrow) || Input.GetKeyDown(KeyCode.W))
        // {
        //     FlashBoth();
        // }
    }

    public void FlashLeft()
    {
        StartFlash(SignalSide.Left);
    }

    public void FlashRight()
    {
        StartFlash(SignalSide.Right);
    }

    public void FlashBoth()
    {
        StartFlash(SignalSide.Both);
    }

    public void StopSignal()
    {
        if (activeFlashRoutine != null)
        {
            StopCoroutine(activeFlashRoutine);
            activeFlashRoutine = null;
        }

        SetAllSignals(false);
    }

    private void StartFlash(SignalSide side)
    {
        if (activeFlashRoutine != null)
        {
            StopCoroutine(activeFlashRoutine);
        }

        activeFlashRoutine = StartCoroutine(FlashRoutine(side));
    }

    private IEnumerator FlashRoutine(SignalSide side)
    {
        SetAllSignals(false);

        for (int i = 0; i < flashCount; i++)
        {
            SetSideState(side, true);
            yield return Wait(onDuration);

            SetSideState(side, false);
            yield return Wait(offDuration);
        }

        activeFlashRoutine = null;
    }

    private object Wait(float duration)
    {
        if (useUnscaledTime)
        {
            return new WaitForSecondsRealtime(duration);
        }

        return new WaitForSeconds(duration);
    }

    private void SetSideState(SignalSide side, bool isOn)
    {
        switch (side)
        {
            case SignalSide.Left:
                SetLeftSignals(isOn);
                break;
            case SignalSide.Right:
                SetRightSignals(isOn);
                break;
            case SignalSide.Both:
                SetLeftSignals(isOn);
                SetRightSignals(isOn);
                break;
        }
    }

    private void SetAllSignals(bool isOn)
    {
        SetLeftSignals(isOn);
        SetRightSignals(isOn);
    }

    private void SetLeftSignals(bool isOn)
    {
        SetLights(leftLights, isOn);
        SetRenderersEmission(leftSignalRenderers, isOn);
    }

    private void SetRightSignals(bool isOn)
    {
        SetLights(rightLights, isOn);
        SetRenderersEmission(rightSignalRenderers, isOn);
    }

    private void SetLights(Light[] lights, bool isOn)
    {
        if (lights == null)
        {
            return;
        }

        for (int i = 0; i < lights.Length; i++)
        {
            if (lights[i] != null)
            {
                lights[i].enabled = isOn;
            }
        }
    }

    private void SetRenderersEmission(Renderer[] renderers, bool isOn)
    {
        if (renderers == null)
        {
            return;
        }

        Color emissionColor = isOn ? emissionOnColor * emissionIntensity : Color.black;

        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer rendererTarget = renderers[i];
            if (rendererTarget == null)
            {
                continue;
            }

            rendererTarget.GetPropertyBlock(propertyBlock);
            propertyBlock.SetColor(EmissionColor, emissionColor);
            rendererTarget.SetPropertyBlock(propertyBlock);
        }
    }
}