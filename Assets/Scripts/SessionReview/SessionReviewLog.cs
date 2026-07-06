using UnityEngine;

namespace SessionReview
{
    /// <summary>
    /// Central switch for the verbose informational [SessionReview] Debug.Log output.
    /// Off by default so it does not spam the Console; warnings and errors still go
    /// straight to Debug.LogWarning/LogError so real problems stay visible.
    /// Flip <see cref="Enabled"/> (or call it from code) to turn the info logs back on.
    /// </summary>
    public static class SessionReviewLog
    {
        public static bool Enabled = false;

        public static void Log(string message, Object context = null)
        {
            if (!Enabled) return;
            if (context != null) Debug.Log(message, context);
            else Debug.Log(message);
        }
    }
}
