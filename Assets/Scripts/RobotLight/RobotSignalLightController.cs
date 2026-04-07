using System;
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

    [Serializable]
    private class EmissiveRendererTarget
    {
        public Renderer renderer;
        public bool applyToAllMaterials = true;
        [Min(0)] public int materialIndex;
        public bool overrideEmissionSettings;
        public Color emissionOnColor = new Color(1f, 0.4f, 0f, 1f);
        [Min(0f)] public float emissionIntensity = 2f;
        public Color emissionOffColor = Color.black;
    }

    [Header("Light References")]
    [SerializeField] private Light[] leftLights;
    [SerializeField] private Light[] rightLights;

    [Header("Legacy Emission Renderers")]
    [SerializeField] private Renderer[] leftSignalRenderers;
    [SerializeField] private Renderer[] rightSignalRenderers;

    [Header("Flexible Emissive Targets")]
    [SerializeField] private EmissiveRendererTarget[] leftEmissionTargets;
    [SerializeField] private EmissiveRendererTarget[] rightEmissionTargets;
    [SerializeField] private bool enablePhysicalLights = true;
    [SerializeField] private bool autoEnableEmissionKeyword = true;

    [Header("Flash Settings")]
    [Min(1)]
    [SerializeField] private int flashCount = 3;
    [Min(0.05f)]
    [SerializeField] private float onDuration = 0.2f;
    [Min(0.05f)]
    [SerializeField] private float offDuration = 0.2f;
    [SerializeField] private bool useUnscaledTime;

    [Header("Default Emission")]
    [SerializeField] private Color emissionOnColor = new Color(1f, 0.4f, 0f, 1f);
    [Min(0f)]
    [SerializeField] private float emissionIntensity = 2f;

    private static readonly int EmissionColor = Shader.PropertyToID("_EmissionColor");

    private Coroutine activeFlashRoutine;
    private MaterialPropertyBlock propertyBlock;

    private void Awake()
    {
        propertyBlock = new MaterialPropertyBlock();

        if (autoEnableEmissionKeyword)
        {
            ConfigureEmissionMaterials();
        }

        SetAllSignals(false);
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
        SetLegacyRenderersEmission(leftSignalRenderers, isOn);
        SetFlexibleTargets(leftEmissionTargets, isOn);
    }

    private void SetRightSignals(bool isOn)
    {
        SetLights(rightLights, isOn);
        SetLegacyRenderersEmission(rightSignalRenderers, isOn);
        SetFlexibleTargets(rightEmissionTargets, isOn);
    }

    private void SetLights(Light[] lights, bool isOn)
    {
        if (!enablePhysicalLights || lights == null)
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

    private void SetLegacyRenderersEmission(Renderer[] renderers, bool isOn)
    {
        if (renderers == null)
        {
            return;
        }

        Color emissionColor = isOn ? emissionOnColor * emissionIntensity : Color.black;

        for (int i = 0; i < renderers.Length; i++)
        {
            ApplyRendererEmission(renderers[i], true, 0, emissionColor);
        }
    }

    private void SetFlexibleTargets(EmissiveRendererTarget[] targets, bool isOn)
    {
        if (targets == null)
        {
            return;
        }

        for (int i = 0; i < targets.Length; i++)
        {
            EmissiveRendererTarget target = targets[i];
            if (target == null || target.renderer == null)
            {
                continue;
            }

            Color targetOnColor = target.overrideEmissionSettings
                ? target.emissionOnColor * target.emissionIntensity
                : emissionOnColor * emissionIntensity;
            Color targetOffColor = target.overrideEmissionSettings
                ? target.emissionOffColor
                : Color.black;
            Color emissionColor = isOn ? targetOnColor : targetOffColor;

            ApplyRendererEmission(target.renderer, target.applyToAllMaterials, target.materialIndex, emissionColor);
        }
    }

    private void ApplyRendererEmission(Renderer rendererTarget, bool applyToAllMaterials, int materialIndex, Color emissionColor)
    {
        if (rendererTarget == null)
        {
            return;
        }

        Material[] sharedMaterials = rendererTarget.sharedMaterials;
        if (sharedMaterials == null || sharedMaterials.Length == 0)
        {
            return;
        }

        if (applyToAllMaterials)
        {
            for (int i = 0; i < sharedMaterials.Length; i++)
            {
                SetEmissionForMaterialIndex(rendererTarget, i, emissionColor);
            }

            return;
        }

        if (materialIndex < 0 || materialIndex >= sharedMaterials.Length)
        {
            Debug.LogWarning($"[RobotSignalLightController] Material index {materialIndex} is out of range on {rendererTarget.name}.", rendererTarget);
            return;
        }

        SetEmissionForMaterialIndex(rendererTarget, materialIndex, emissionColor);
    }

    private void SetEmissionForMaterialIndex(Renderer rendererTarget, int materialIndex, Color emissionColor)
    {
        rendererTarget.GetPropertyBlock(propertyBlock, materialIndex);
        propertyBlock.SetColor(EmissionColor, emissionColor);
        rendererTarget.SetPropertyBlock(propertyBlock, materialIndex);
    }

    private void ConfigureEmissionMaterials()
    {
        ConfigureLegacyRenderers(leftSignalRenderers);
        ConfigureLegacyRenderers(rightSignalRenderers);
        ConfigureFlexibleRenderers(leftEmissionTargets);
        ConfigureFlexibleRenderers(rightEmissionTargets);
    }

    private void ConfigureLegacyRenderers(Renderer[] renderers)
    {
        if (renderers == null)
        {
            return;
        }

        for (int i = 0; i < renderers.Length; i++)
        {
            EnableEmissionKeyword(renderers[i]);
        }
    }

    private void ConfigureFlexibleRenderers(EmissiveRendererTarget[] targets)
    {
        if (targets == null)
        {
            return;
        }

        for (int i = 0; i < targets.Length; i++)
        {
            if (targets[i] != null)
            {
                EnableEmissionKeyword(targets[i].renderer);
            }
        }
    }

    private void EnableEmissionKeyword(Renderer rendererTarget)
    {
        if (rendererTarget == null)
        {
            return;
        }

        Material[] materials = rendererTarget.materials;
        for (int i = 0; i < materials.Length; i++)
        {
            Material material = materials[i];
            if (material != null && material.HasProperty(EmissionColor))
            {
                material.EnableKeyword("_EMISSION");
            }
        }
    }
}
