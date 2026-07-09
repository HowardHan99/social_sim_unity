using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace SessionReview
{
    /// <summary>
    /// One row for the World Building IMGUI spawn palette (prefab + display label + optional thumbnail).
    /// </summary>
    public sealed class WorldBuildingSpawnUiRow
    {
        public string SpawnId;
        public string DisplayName;
        public Texture2D Thumbnail;
    }

    /// <summary>
    /// Registers only prefabs under <c>Resources/WorldBuildingSpawns</c> that have a matching thumbnail in
    /// <c>Resources/WorldBuildingUI</c>, so other prefabs stored in that folder for unrelated systems are ignored.
    /// </summary>
    public static class WorldBuildingSpawnLibrary
    {
        private static List<SpawnableObject> _lastSpawnables = new List<SpawnableObject>();
        private static List<WorldBuildingSpawnUiRow> _lastUiRows = new List<WorldBuildingSpawnUiRow>();
        private static List<WorldBuildingSpawnUiRow> _lastObjectUiRows = new List<WorldBuildingSpawnUiRow>();
        private static List<WorldBuildingSpawnUiRow> _lastCharacterUiRows = new List<WorldBuildingSpawnUiRow>();

        public static IReadOnlyList<SpawnableObject> LastSpawnables => _lastSpawnables;
        public static IReadOnlyList<WorldBuildingSpawnUiRow> LastUiRows => _lastUiRows;
        public static IReadOnlyList<WorldBuildingSpawnUiRow> LastObjectUiRows => _lastObjectUiRows;
        public static IReadOnlyList<WorldBuildingSpawnUiRow> LastCharacterUiRows => _lastCharacterUiRows;

        public static void RefreshFromResources()
        {
            GameObject[] prefabs = Resources.LoadAll<GameObject>("WorldBuildingSpawns");
            if (prefabs == null || prefabs.Length == 0)
            {
                _lastSpawnables = new List<SpawnableObject>();
                _lastUiRows = new List<WorldBuildingSpawnUiRow>();
                _lastObjectUiRows = new List<WorldBuildingSpawnUiRow>();
                _lastCharacterUiRows = new List<WorldBuildingSpawnUiRow>();
                return;
            }

            Array.Sort(prefabs, (a, b) =>
                string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase));

            Texture2D[] textures = Resources.LoadAll<Texture2D>("WorldBuildingUI");
            if (textures == null)
                textures = Array.Empty<Texture2D>();

            _lastSpawnables = new List<SpawnableObject>();
            _lastUiRows = new List<WorldBuildingSpawnUiRow>();
            _lastObjectUiRows = new List<WorldBuildingSpawnUiRow>();
            _lastCharacterUiRows = new List<WorldBuildingSpawnUiRow>();

            for (int i = 0; i < prefabs.Length; i++)
            {
                GameObject prefab = prefabs[i];
                if (prefab == null)
                    continue;

                Texture2D thumbnail = ResolveThumbnail(prefab.name, textures);
                if (thumbnail == null)
                    continue;

                string id = _lastSpawnables.Count.ToString(CultureInfo.InvariantCulture);
                _lastSpawnables.Add(new SpawnableObject
                {
                    id = id,
                    prefab = prefab,
                    spawnButton = null
                });

                var uiRow = new WorldBuildingSpawnUiRow
                {
                    SpawnId = id,
                    DisplayName = HumanizePrefabName(prefab.name),
                    Thumbnail = thumbnail
                };
                _lastUiRows.Add(uiRow);

                if (IsCharacterSpawnPrefab(prefab.name))
                    _lastCharacterUiRows.Add(uiRow);
                else
                    _lastObjectUiRows.Add(uiRow);
            }
        }

        public static bool IsCharacterSpawnPrefab(string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName))
                return false;

            string n = prefabName.ToLowerInvariant();
            if (n.Contains("wheelchair"))
                return true;
            if (n.Contains("urs") && (n.Contains("user") || n.Contains("guest") || n.Contains("host")))
                return true;
            if (n.Contains("avatar") || n.Contains("pedestrian") || n.Contains("pwd"))
                return true;

            return false;
        }

        public static string HumanizePrefabName(string raw)
        {
            if (string.IsNullOrEmpty(raw))
                return "Object";

            string s = raw.Replace('_', ' ');
            s = Regex.Replace(s, @"\s+\(\d+\)\s*$", string.Empty);
            s = Regex.Replace(s, @"\s+0+(\d+)\s*$", " $1");
            return s.Trim();
        }

        static Texture2D ResolveThumbnail(string prefabName, Texture2D[] textures)
        {
            if (textures == null || textures.Length == 0 || string.IsNullOrEmpty(prefabName))
                return null;

            foreach (Texture2D t in textures)
            {
                if (t != null && string.Equals(t.name, prefabName, StringComparison.OrdinalIgnoreCase))
                    return t;
            }

            string collapsed = Regex.Replace(prefabName, @"_0*\d+$", string.Empty);
            foreach (Texture2D t in textures)
            {
                if (t != null && string.Equals(t.name, collapsed, StringComparison.OrdinalIgnoreCase))
                    return t;
            }

            foreach (Texture2D t in textures)
            {
                if (t == null) continue;
                if (prefabName.IndexOf(t.name, StringComparison.OrdinalIgnoreCase) >= 0)
                    return t;
                if (t.name.IndexOf(collapsed, StringComparison.OrdinalIgnoreCase) >= 0)
                    return t;
            }

            // Numbered UI assets e.g. 0_mailboxImg, 1_CardboxImg -> prefab Mailbox, Cardboard_Box
            string prefKey = AlphanumericKey(collapsed);
            if (prefKey.Length >= 4)
            {
                foreach (Texture2D t in textures)
                {
                    if (t == null) continue;
                    string texSemantic = SemanticKeyForThumbnailAsset(t.name);
                    if (texSemantic.Length < 4)
                        continue;
                    if (texSemantic.Contains(prefKey) || prefKey.Contains(texSemantic))
                        return t;
                }
            }

            string keyA = AlphanumericKey(prefabName);
            string keyC = AlphanumericKey(collapsed);
            Texture2D best = null;
            int bestScore = 0;
            foreach (Texture2D t in textures)
            {
                if (t == null) continue;
                string keyB = AlphanumericKey(t.name);
                int s1 = LongestCommonSubstringScore(keyA, keyB);
                int s2 = LongestCommonSubstringScore(keyC, keyB);
                int score = Mathf.Max(s1, s2);
                if (score >= 4 && score > bestScore)
                {
                    bestScore = score;
                    best = t;
                }
            }

            return best;
        }

        /// <summary>
        /// Strips leading "12_" style prefixes and optional "Img" suffix from texture asset names used for palette art.
        /// </summary>
        static string SemanticKeyForThumbnailAsset(string textureAssetName)
        {
            if (string.IsNullOrEmpty(textureAssetName))
                return string.Empty;

            string s = Regex.Replace(textureAssetName, @"^\d+\s*[_\-\s]\s*", string.Empty, RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"img$", string.Empty, RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"[_\-\s]+$", string.Empty);
            return AlphanumericKey(s);
        }

        static string AlphanumericKey(string s)
        {
            if (string.IsNullOrEmpty(s))
                return string.Empty;

            var sb = new StringBuilder(s.Length);
            foreach (char c in s.ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(c))
                    sb.Append(c);
            }

            return sb.ToString();
        }

        static int LongestCommonSubstringScore(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
                return 0;

            int best = 0;
            int maxI = Mathf.Min(a.Length, 48);
            int maxJ = Mathf.Min(b.Length, 48);
            for (int i = 0; i < maxI; i++)
            {
                for (int j = 0; j < maxJ; j++)
                {
                    int k = 0;
                    while (i + k < a.Length && j + k < b.Length && a[i + k] == b[j + k])
                        k++;
                    if (k > best)
                        best = k;
                }
            }

            return best;
        }
    }
}
