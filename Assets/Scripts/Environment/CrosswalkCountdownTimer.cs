using System.Collections;
using TMPro;
using UnityEngine;

/// <summary>
/// Counts a TextMeshPro label down from <see cref="startValue"/> to
/// <see cref="endValue"/> when the scene starts, advancing once per
/// <see cref="secondsPerStep"/>. Attach to a GameObject that has a
/// TextMeshProUGUI / TextMeshPro component (e.g. the "Timer" under the
/// Crosswalk Light), or assign one explicitly via <see cref="label"/>.
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

    [Tooltip("Optional. If left empty, a TMP text component on this GameObject is used.")]
    [SerializeField] private TMP_Text label;

    private void Awake()
    {
        if (label == null)
            label = GetComponent<TMP_Text>();
    }

    private void Start()
    {
        if (label == null)
        {
            Debug.LogError($"[CrosswalkCountdownTimer] No TMP_Text found on '{name}'. Assign one in the Inspector.");
            return;
        }

        StartCoroutine(RunCountdown());
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
    }

    private IEnumerator WaitStep()
    {
        if (useUnscaledTime)
            yield return new WaitForSecondsRealtime(secondsPerStep);
        else
            yield return new WaitForSeconds(secondsPerStep);
    }
}
