using UnityEngine;

[RequireComponent(typeof(Camera))]
[DisallowMultipleComponent]
public class ComfortMotionBlur : MonoBehaviour
{
    [Header("Comfort Blur")]
    [SerializeField] private Shader blurShader;
    [SerializeField] [Range(0f, 1f)] private float maxBlurStrength = 0.85f;
    [Tooltip("Pixel radius of the blur kernel at maximum strength. 8 = subtle, 20 = strong.")]
    [SerializeField] [Min(1f)] private float blurRadiusPixels = 15f;
    [Tooltip("Rotation speed (°/s) that produces maximum blur. Lower = kicks in sooner.")]
    [SerializeField] private float rotationForMaxBlurDegreesPerSecond = 160f;
    [SerializeField] private float blurRiseSpeed = 10f;
    [SerializeField] private float blurFallSpeed = 18f;
    [SerializeField] private float transitionBoostStrength = 0.55f;
    [SerializeField] private float transitionBoostDuration = 0.15f;

    [Header("Angular Speed Smoothing")]
    [Tooltip("Low-pass filter time constant (seconds). "
           + "For live rotation use 0.02-0.04 (fast response). "
           + "For session-review playback at ~10 Hz use 0.08-0.12 to absorb per-snapshot jumps and prevent pulsing blur.")]
    [SerializeField] private float angularSpeedSmoothTime = 0.10f;

    [Header("Comfort Vignette")]
    [Tooltip("How dark the peripheral vignette gets at peak blur (0 = off). "
           + "Keep at 0 if the scene has auto-exposure or if users report brightness flickering — "
           + "peripheral darkening is perceived as whole-scene brightness change.")]
    [SerializeField] [Range(0f, 1f)] private float vignetteStrength = 0f;
    [Tooltip("How far from center the vignette starts — smaller value = more tunnel-vision")]
    [SerializeField] [Range(0.1f, 1f)] private float vignetteRadius = 0.55f;
    [Tooltip("How soft the vignette falloff edge is")]
    [SerializeField] [Range(0.01f, 1f)] private float vignetteSoftness = 0.45f;

    private Material blurMaterial;
    private Quaternion lastRotation;
    private bool hasLastRotation;
    private float transitionTimer;
    private float currentBlurStrength;
    private float smoothedAngularSpeed;

    void OnEnable()
    {
        hasLastRotation = false;
        currentBlurStrength = 0f;
        smoothedAngularSpeed = 0f;
        transitionTimer = transitionBoostDuration;
    }

    void LateUpdate()
    {
        if (!EnsureMaterial())
            return;

        float deltaTime = Mathf.Max(Time.unscaledDeltaTime, 0.0001f);

        // --- Angular velocity, low-pass filtered ---
        // During session-review playback the recorded state is applied in discrete
        // snapshots (~10 Hz), so the camera can jump several degrees in one frame.
        // Without smoothing that one frame reads as an enormous angular speed,
        // which slams the blur/vignette to maximum and then drops it — flicker.
        // The exponential filter spreads that impulse over ~angularSpeedSmoothTime
        // seconds so the blur ramps in gradually instead of flashing.
        float rotationBlur = 0f;
        if (hasLastRotation)
        {
            float angleDelta = Quaternion.Angle(lastRotation, transform.rotation);
            float rawAngularSpeed = angleDelta / deltaTime;

            float smoothTime = Mathf.Max(0.001f, angularSpeedSmoothTime);
            float blendFactor = 1f - Mathf.Exp(-deltaTime / smoothTime);
            smoothedAngularSpeed = Mathf.Lerp(smoothedAngularSpeed, rawAngularSpeed, blendFactor);

            // Speed-based blur with a very short smooth time (0.02-0.04s) so the
            // blur tracks rotation almost instantly — present during rotation but
            // with no perceptible lag between camera movement and blur onset.
            rotationBlur = Mathf.Clamp01(smoothedAngularSpeed / Mathf.Max(1f, rotationForMaxBlurDegreesPerSecond));
        }

        lastRotation = transform.rotation;
        hasLastRotation = true;

        if (transitionTimer > 0f)
            transitionTimer = Mathf.Max(0f, transitionTimer - Time.unscaledDeltaTime);

        float transitionBlur = transitionBoostDuration > 0f
            ? transitionBoostStrength * (transitionTimer / transitionBoostDuration)
            : 0f;

        float targetBlur = Mathf.Clamp01(Mathf.Max(rotationBlur, transitionBlur));
        // Exponential smoothing instead of MoveTowards — gives a natural ease-in/out
        // curve (fast start, gradual settle) rather than a mechanical linear ramp
        // that the brain notices as a distinct "event" and interprets as stuttering.
        float smoothSpeed = targetBlur > currentBlurStrength ? blurRiseSpeed : blurFallSpeed;
        float t = 1f - Mathf.Exp(-smoothSpeed * Time.unscaledDeltaTime);
        currentBlurStrength = Mathf.Lerp(currentBlurStrength, targetBlur, t);
    }

    void OnRenderImage(RenderTexture source, RenderTexture destination)
    {
        if (!EnsureMaterial())
        {
            Graphics.Blit(source, destination);
            return;
        }

        // Always use the material even at strength=0.
        // At radius=0 all 9 samples hit the same texel (weights sum to 1.0)
        // so it is a mathematically exact copy. Switching between this path and
        // Graphics.Blit caused a brightness pop because Unity's internal blit
        // shader handles color-space conversion differently from a custom shader.
        float scaledBlur = currentBlurStrength * maxBlurStrength;
        blurMaterial.SetFloat("_BlurStrength", scaledBlur);
        blurMaterial.SetFloat("_BlurRadius", blurRadiusPixels);
        blurMaterial.SetFloat("_VignetteStrength", vignetteStrength);
        blurMaterial.SetFloat("_VignetteRadius", vignetteRadius);
        blurMaterial.SetFloat("_VignetteSoftness", vignetteSoftness);
        Graphics.Blit(source, destination, blurMaterial);
    }

    /// <summary>
    /// Call when the camera teleports to a new position/rotation so that the
    /// stale lastRotation doesn't produce a spurious angular-speed spike.
    /// </summary>
    public void TriggerTransitionBlur()
    {
        transitionTimer = transitionBoostDuration;
        // Reset rotation tracking — the camera jumped, so the next frame's
        // angle delta is meaningless and would create a false blur spike.
        hasLastRotation = false;
        smoothedAngularSpeed = 0f;
    }

    private bool EnsureMaterial()
    {
        if (blurMaterial != null)
            return true;

        if (blurShader == null)
            blurShader = Shader.Find("Hidden/ComfortMotionBlur");

        if (blurShader == null || !blurShader.isSupported)
            return false;

        blurMaterial = new Material(blurShader)
        {
            hideFlags = HideFlags.HideAndDontSave
        };
        return true;
    }

    void OnDisable()
    {
        currentBlurStrength = 0f;
        smoothedAngularSpeed = 0f;
    }

    void OnDestroy()
    {
        if (blurMaterial != null)
        {
            Destroy(blurMaterial);
            blurMaterial = null;
        }
    }
}
