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

    [Header("Optional Named Child Discovery")]
    [SerializeField] private Transform[] leftDiscoveryRoots;
    [SerializeField] private Transform[] rightDiscoveryRoots;
    [SerializeField] private string[] supportedLightObjectNames = { "light", "light-front", "light-back" };
    [SerializeField] private string[] leftBranchObjectNames = { "left", "Left" };
    [SerializeField] private string[] rightBranchObjectNames = { "right", "Right" };
    [SerializeField] private bool includeAllChildRenderersUnderMatchedObjects = true;

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
    private Coroutine activeReviewFlashRoutine;
    private MaterialPropertyBlock propertyBlock;
    private Light[] discoveredLeftLights;
    private Light[] discoveredRightLights;
    private Renderer[] discoveredLeftRenderers;
    private Renderer[] discoveredRightRenderers;

    private void Awake()
    {
        propertyBlock = new MaterialPropertyBlock();
        RefreshDiscoveredTargets();

        if (autoEnableEmissionKeyword)
        {
            ConfigureEmissionMaterials();
        }

        SetAllSignals(false);
    }

    private void OnValidate()
    {
        RefreshDiscoveredTargets();
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
        Debug.Log($"[RobotSignalLightController] StopSignal called on {name}.");

        if (activeFlashRoutine != null)
        {
            StopCoroutine(activeFlashRoutine);
            activeFlashRoutine = null;
        }

        if (activeReviewFlashRoutine != null)
        {
            StopCoroutine(activeReviewFlashRoutine);
            activeReviewFlashRoutine = null;
        }

        SetAllSignals(false);
    }

    public void SetReviewSignalState(bool leftOn, bool rightOn)
    {
        Debug.Log($"[RobotSignalLightController] SetReviewSignalState called on {name}: leftOn={leftOn}, rightOn={rightOn}.");
        PrepareReplayTargets();

        if (activeFlashRoutine != null)
        {
            StopCoroutine(activeFlashRoutine);
            activeFlashRoutine = null;
        }

        SetLeftSignals(leftOn);
        SetRightSignals(rightOn);
    }

    public void ClearReviewSignalState()
    {
        Debug.Log($"[RobotSignalLightController] ClearReviewSignalState called on {name}.");
        SetReviewSignalState(false, false);
    }

    public void PlayReviewFlashLeft()
    {
        Debug.Log($"[RobotSignalLightController] PlayReviewFlashLeft called on {name}.");
        StartReviewFlash(SignalSide.Left);
    }

    public void PlayReviewFlashRight()
    {
        Debug.Log($"[RobotSignalLightController] PlayReviewFlashRight called on {name}.");
        StartReviewFlash(SignalSide.Right);
    }

    public void PlayReviewFlashBoth()
    {
        Debug.Log($"[RobotSignalLightController] PlayReviewFlashBoth called on {name}.");
        StartReviewFlash(SignalSide.Both);
    }

    private void StartFlash(SignalSide side)
    {
        if (activeFlashRoutine != null)
        {
            StopCoroutine(activeFlashRoutine);
        }

        activeFlashRoutine = StartCoroutine(FlashRoutine(side));
    }

    private void StartReviewFlash(SignalSide side)
    {
        PrepareReplayTargets();
        Debug.Log(
            $"[RobotSignalLightController] StartReviewFlash called on {name}: side={side}, flashCount={flashCount}, onDuration={onDuration:F2}, offDuration={offDuration:F2}, " +
            $"enablePhysicalLights={enablePhysicalLights}, leftLightCount={CountNonNull(leftLights) + CountNonNull(discoveredLeftLights)}, " +
            $"rightLightCount={CountNonNull(rightLights) + CountNonNull(discoveredRightLights)}, " +
            $"leftRendererCount={CountNonNull(leftSignalRenderers) + CountNonNull(discoveredLeftRenderers) + CountNonNull(leftEmissionTargets)}, " +
            $"rightRendererCount={CountNonNull(rightSignalRenderers) + CountNonNull(discoveredRightRenderers) + CountNonNull(rightEmissionTargets)}.");

        if (activeReviewFlashRoutine != null)
        {
            StopCoroutine(activeReviewFlashRoutine);
        }

        activeReviewFlashRoutine = StartCoroutine(ReviewFlashRoutine(side));
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

    private IEnumerator ReviewFlashRoutine(SignalSide side)
    {
        Debug.Log($"[RobotSignalLightController] ReviewFlashRoutine started on {name}: side={side}.");
        SetAllSignals(false);

        for (int i = 0; i < flashCount; i++)
        {
            Debug.Log($"[RobotSignalLightController] ReviewFlashRoutine pulse {i + 1}/{flashCount} ON for {side} on {name}.");
            SetSideState(side, true);
            yield return new WaitForSecondsRealtime(onDuration);

            Debug.Log($"[RobotSignalLightController] ReviewFlashRoutine pulse {i + 1}/{flashCount} OFF for {side} on {name}.");
            SetSideState(side, false);
            yield return new WaitForSecondsRealtime(offDuration);
        }

        Debug.Log($"[RobotSignalLightController] ReviewFlashRoutine finished on {name}: side={side}.");
        activeReviewFlashRoutine = null;
    }

    private void PrepareReplayTargets()
    {
        RefreshDiscoveredTargets();
        if (autoEnableEmissionKeyword)
            ConfigureEmissionMaterials();
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
        SetLights(discoveredLeftLights, isOn);
        SetLegacyRenderersEmission(leftSignalRenderers, isOn);
        SetLegacyRenderersEmission(discoveredLeftRenderers, isOn);
        SetFlexibleTargets(leftEmissionTargets, isOn);
    }

    private void SetRightSignals(bool isOn)
    {
        SetLights(rightLights, isOn);
        SetLights(discoveredRightLights, isOn);
        SetLegacyRenderersEmission(rightSignalRenderers, isOn);
        SetLegacyRenderersEmission(discoveredRightRenderers, isOn);
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
                if (isOn && !lights[i].gameObject.activeSelf)
                    lights[i].gameObject.SetActive(true);
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

    private static int CountNonNull<T>(T[] values) where T : class
    {
        if (values == null)
            return 0;

        int count = 0;
        for (int i = 0; i < values.Length; i++)
        {
            if (values[i] != null)
                count++;
        }

        return count;
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

        Material[] runtimeMaterials = rendererTarget.materials;
        if (runtimeMaterials != null && materialIndex >= 0 && materialIndex < runtimeMaterials.Length)
        {
            Material runtimeMaterial = runtimeMaterials[materialIndex];
            if (runtimeMaterial != null && runtimeMaterial.HasProperty(EmissionColor))
            {
                runtimeMaterial.EnableKeyword("_EMISSION");
                runtimeMaterial.SetColor(EmissionColor, emissionColor);
            }
        }
    }

    private void ConfigureEmissionMaterials()
    {
        ConfigureLegacyRenderers(leftSignalRenderers);
        ConfigureLegacyRenderers(rightSignalRenderers);
        ConfigureLegacyRenderers(discoveredLeftRenderers);
        ConfigureLegacyRenderers(discoveredRightRenderers);
        ConfigureFlexibleRenderers(leftEmissionTargets);
        ConfigureFlexibleRenderers(rightEmissionTargets);
    }

    private void RefreshDiscoveredTargets()
    {
        Transform[] effectiveLeftRoots = GetEffectiveRoots(leftDiscoveryRoots);
        Transform[] effectiveRightRoots = GetEffectiveRoots(rightDiscoveryRoots);

        discoveredLeftLights = CollectComponentsForSide<Light>(effectiveLeftRoots, leftBranchObjectNames);
        discoveredRightLights = CollectComponentsForSide<Light>(effectiveRightRoots, rightBranchObjectNames);
        discoveredLeftRenderers = CollectComponentsForSide<Renderer>(effectiveLeftRoots, leftBranchObjectNames);
        discoveredRightRenderers = CollectComponentsForSide<Renderer>(effectiveRightRoots, rightBranchObjectNames);
    }

    private Transform[] GetEffectiveRoots(Transform[] configuredRoots)
    {
        if (configuredRoots != null && configuredRoots.Length > 0)
        {
            return configuredRoots;
        }

        return new[] { transform };
    }

    private T[] CollectComponentsForSide<T>(Transform[] roots, string[] sideBranchNames) where T : Component
    {
        if (roots == null || roots.Length == 0 || supportedLightObjectNames == null || supportedLightObjectNames.Length == 0)
        {
            return Array.Empty<T>();
        }

        var results = new System.Collections.Generic.List<T>();

        for (int i = 0; i < roots.Length; i++)
        {
            Transform root = roots[i];
            if (root == null)
            {
                continue;
            }

            CollectComponentsRecursive(root, sideBranchNames, results);
        }

        return results.ToArray();
    }

    private void CollectComponentsRecursive<T>(Transform current, string[] sideBranchNames, System.Collections.Generic.List<T> results) where T : Component
    {
        if (current == null)
        {
            return;
        }

        if (IsSupportedLightObjectName(current.name))
        {
            CollectSideComponentsFromMatchedLightObject(current, sideBranchNames, results);
        }

        for (int i = 0; i < current.childCount; i++)
        {
            CollectComponentsRecursive(current.GetChild(i), sideBranchNames, results);
        }
    }

    private void CollectSideComponentsFromMatchedLightObject<T>(Transform lightObjectRoot, string[] sideBranchNames, System.Collections.Generic.List<T> results) where T : Component
    {
        if (lightObjectRoot == null)
        {
            return;
        }

        bool foundSideBranch = false;

        for (int i = 0; i < lightObjectRoot.childCount; i++)
        {
            Transform child = lightObjectRoot.GetChild(i);
            if (!IsSideBranchObjectName(child.name, sideBranchNames))
            {
                continue;
            }

            foundSideBranch = true;
            AddComponentsFromTransform(child, results);
        }

        if (!foundSideBranch)
        {
            AddComponentsFromTransform(lightObjectRoot, results);
        }
    }

    private void AddComponentsFromTransform<T>(Transform source, System.Collections.Generic.List<T> results) where T : Component
    {
        if (source == null)
        {
            return;
        }

        T[] components = includeAllChildRenderersUnderMatchedObjects
            ? source.GetComponentsInChildren<T>(true)
            : source.GetComponents<T>();

        for (int i = 0; i < components.Length; i++)
        {
            T component = components[i];
            if (component != null && !results.Contains(component))
            {
                results.Add(component);
            }
        }
    }

    private bool IsSupportedLightObjectName(string objectName)
    {
        if (string.IsNullOrWhiteSpace(objectName) || supportedLightObjectNames == null)
        {
            return false;
        }

        for (int i = 0; i < supportedLightObjectNames.Length; i++)
        {
            string supportedName = supportedLightObjectNames[i];
            if (string.IsNullOrWhiteSpace(supportedName))
            {
                continue;
            }

            if (string.Equals(objectName, supportedName, StringComparison.OrdinalIgnoreCase) ||
                objectName.StartsWith(supportedName + " ", StringComparison.OrdinalIgnoreCase) ||
                objectName.StartsWith(supportedName + "(", StringComparison.OrdinalIgnoreCase) ||
                objectName.IndexOf(supportedName, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    private bool IsSideBranchObjectName(string objectName, string[] supportedSideNames)
    {
        if (string.IsNullOrWhiteSpace(objectName) || supportedSideNames == null)
        {
            return false;
        }

        for (int i = 0; i < supportedSideNames.Length; i++)
        {
            string supportedName = supportedSideNames[i];
            if (string.IsNullOrWhiteSpace(supportedName))
            {
                continue;
            }

            if (string.Equals(objectName, supportedName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
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
