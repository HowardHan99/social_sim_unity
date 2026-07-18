using UnityEngine;

namespace SessionReview
{
    /// <summary>
    /// Participant/session identifier typed on the onboarding pages (main scene and
    /// TestScene). Persisted in PlayerPrefs, so it defaults to the previous session's
    /// value until the operator changes it. Stamped into every trial_info.json
    /// (TrialRecord.sessionId) so different participants' data can be told apart.
    /// </summary>
    public static class ParticipantSession
    {
        private const string PrefKey = "SEAN.ParticipantSessionId";
        private static string cached;

        public static string Id
        {
            get
            {
                if (cached == null)
                    cached = PlayerPrefs.GetString(PrefKey, string.Empty);
                return cached;
            }
            set
            {
                string trimmed = (value ?? string.Empty).Trim();
                if (cached != null && trimmed == cached)
                    return;
                cached = trimmed;
                PlayerPrefs.SetString(PrefKey, trimmed);
                PlayerPrefs.Save();
            }
        }
    }
}
