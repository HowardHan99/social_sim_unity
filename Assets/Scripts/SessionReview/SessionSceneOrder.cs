namespace SessionReview
{
    /// <summary>
    /// Deterministic per-session-id ordering of the session scene list (the numbered
    /// "1. / 2. / 3." rows in onboarding). The SAME session id always produces the
    /// SAME order — reopening onboarding, retyping the id, or restarting Unity all
    /// reproduce it — while different ids get independently shuffled orders. That is
    /// study counterbalancing keyed to the participant: each participant sees the
    /// scenes in a fixed random order that is theirs alone.
    /// </summary>
    public static class SessionSceneOrder
    {
        /// <summary>
        /// A permutation of [0, count): <c>result[displayPosition] = originalSceneIndex</c>,
        /// shuffled by a stable seed derived from <paramref name="sessionId"/>. An empty
        /// or null id (and counts &lt;= 1) fall back to the natural 0,1,2,... order, so an
        /// unassigned session just shows the list as authored.
        /// </summary>
        public static int[] Permutation(string sessionId, int count)
        {
            if (count < 0)
                count = 0;

            var order = new int[count];
            for (int i = 0; i < count; i++)
                order[i] = i;

            if (count <= 1 || string.IsNullOrEmpty(sessionId))
                return order;

            // Fisher-Yates seeded from the id. We hash the id ourselves (FNV-1a)
            // rather than string.GetHashCode() because that is not guaranteed stable
            // across processes/platforms -- and "same id, same order" is the whole
            // point.
            uint state = StableSeed(sessionId);
            for (int i = count - 1; i > 0; i--)
            {
                state = NextRandom(state);
                int j = (int)(state % (uint)(i + 1));
                int tmp = order[i];
                order[i] = order[j];
                order[j] = tmp;
            }
            return order;
        }

        // FNV-1a 32-bit: deterministic across runs and platforms.
        private static uint StableSeed(string s)
        {
            uint hash = 2166136261u;
            foreach (char c in s)
            {
                hash ^= c;
                hash *= 16777619u;
            }
            return hash == 0u ? 1u : hash; // xorshift must not start at 0
        }

        // xorshift32: tiny deterministic PRNG.
        private static uint NextRandom(uint state)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            return state;
        }
    }
}
