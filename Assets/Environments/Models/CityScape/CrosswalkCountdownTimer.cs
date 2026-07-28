using System.Collections;
using TMPro;
using UnityEngine;

/// <summary>
/// Counts a TextMeshPro label down from <see cref="startValue"/> to
/// <see cref="endValue"/>, advancing once per <see cref="secondsPerStep"/>.
///
/// The countdown does NOT run on scene load. It waits for a trigger:
///   • the Start Trial action (any path: key, button, or gamepad), if
///     <see cref="startOnTrialStart"/> is enabled, and/or
///   • pressing <see cref="startKey"/> (default K), and/or
///   • a script calling <see cref="StartCountdown"/> (e.g. from a UI Button).
///
/// Attach to a GameObject that has a TextMeshProUGUI / TextMeshPro component
/// (e.g. the "Timer" under the Crosswalk Light), or assign one via
/// <see cref="label"/>.
/// </summary>
public class CrosswalkCountdownTimer : MonoBehaviour
{
    [Header("Countdown")]
    [SerializeField] private int startValue = 10;
    [SerializeField] private int endValue = 1;
    [SerializeField] private float secondsPerStep = 1f;
    [SerializeField] private bool loop = false;

    [Tooltip("Use unscaled time so the countdown keeps running while the game is paused (Time.timeScale = 0).")]
    [SerializeField] private bool useUnscaledTime = true;

    [Header("Start Trigger")]
    [Tooltip("Begin automatically the moment a trial starts (the Start Trial button / Enter / gamepad).")]
    [SerializeField] private bool startOnTrialStart = true;

    [Tooltip("Also begin when this key is pressed. Set to None to disable the key trigger.")]
    [SerializeField] private KeyCode startKey = KeyCode.K;

    [Tooltip("Hide the label until the countdown starts. Otherwise it shows the start value while idle.")]
    [SerializeField] private bool hideUntilStarted = true;

    [Tooltip("Optional. If left empty, a TMP text component on this GameObject is used.")]
    [SerializeField] private TMP_Text label;

    private Coroutine countdownRoutine;

    private void Awake()
    {
        if (label == null)
            label = GetComponent<TMP_Text>();
    }

    private void OnEnable()
    {
        if (startOnTrialStart)
            SessionReview.SessionOnboardingSettings.TrialStarted += StartCountdown;
    }

    private void OnDisable()
    {
        SessionReview.SessionOnboardingSettings.TrialStarted -= StartCountdown;
    }

    private void Start()
    {
        if (label == null)
        {
            Debug.LogError($"[CrosswalkCountdownTimer] No TMP_Text found on '{name}'. Assign one in the Inspector.");
            return;
        }

        // Idle state: blank (or show the starting number) until a trigger fires.
        label.text = hideUntilStarted ? string.Empty : startValue.ToString();
    }

    private void Update()
    {
        if (startKey != KeyCode.None && Input.GetKeyDown(startKey))
            StartCountdown();
    }

    /// <summary>
    /// Begins (or restarts) the countdown now. Safe to wire to a UI Button
    /// onClick or call from other scripts. Ignored if no label is available.
    /// </summary>
    public void StartCountdown()
    {
        if (label == null)
        {
            Debug.LogError($"[CrosswalkCountdownTimer] No TMP_Text found on '{name}'. Assign one in the Inspector.");
            return;
        }

        if (countdownRoutine != null)
            StopCoroutine(countdownRoutine);

        countdownRoutine = StartCoroutine(RunCountdown());
    }

    /// <summary>Stops the countdown and clears the label.</summary>
    public void StopCountdown()
    {
        if (countdownRoutine != null)
        {
            StopCoroutine(countdownRoutine);
            countdownRoutine = null;
        }

        if (label != null)
            label.text = string.Empty;
    }

    private IEnumerator RunCountdown()
    {
        int step = startValue >= endValue ? -1 : 1;

        do
        {
            for (int value = startValue; step < 0 ? value >= endValue : value <= endValue; value += step)
            {
                label.text = value.ToString();
                yield return WaitStep();
            }
        } while (loop);

        label.text = string.Empty;
        countdownRoutine = null;
    }

    private IEnumerator WaitStep()
    {
        if (useUnscaledTime)
            yield return new WaitForSecondsRealtime(secondsPerStep);
        else
            yield return new WaitForSeconds(secondsPerStep);
    }
}
