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
        private static Dictionary<string, Texture2D> _thumbnailByAssetName =
            new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
        private static bool _loggedRefreshSummary;

        public static IReadOnlyList<SpawnableObject> LastSpawnables => _lastSpawnables;
        public static IReadOnlyList<WorldBuildingSpawnUiRow> LastUiRows => _lastUiRows;
        public static IReadOnlyList<WorldBuildingSpawnUiRow> LastObjectUiRows => _lastObjectUiRows;
        public static IReadOnlyList<WorldBuildingSpawnUiRow> LastCharacterUiRows => _lastCharacterUiRows;

        public static void RefreshFromResources()
        {
            _loggedRefreshSummary = false;
            GameObject[] prefabs = Resources.LoadAll<GameObject>("WorldBuildingSpawns");
            if (prefabs == null || prefabs.Length == 0)
            {
                _lastSpawnables = new List<SpawnableObject>();
                _lastUiRows = new List<WorldBuildingSpawnUiRow>();
                _lastObjectUiRows = new List<WorldBuildingSpawnUiRow>();
                _lastCharacterUiRows = new List<WorldBuildingSpawnUiRow>();
                _thumbnailByAssetName.Clear();
                Debug.LogWarning("[WorldBuildingSpawnLibrary] No prefabs found under Resources/WorldBuildingSpawns.");
                return;
            }

            Array.Sort(prefabs, (a, b) =>
                string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase));

            Texture2D[] textures = LoadUiThumbnails();

            _lastSpawnables = new List<SpawnableObject>();
            _lastUiRows = new List<WorldBuildingSpawnUiRow>();
            _lastObjectUiRows = new List<WorldBuildingSpawnUiRow>();
            _lastCharacterUiRows = new List<WorldBuildingSpawnUiRow>();

            RegisterExplicitPalettePrefabs(textures);

            for (int i = 0; i < prefabs.Length; i++)
            {
                GameObject prefab = prefabs[i];
                if (prefab == null)
                    continue;

                if (IsPaletteEntryRegistered(prefab.name))
                    continue;

                if (!IsPaletteSpawnPrefab(prefab))
                    continue;

                Texture2D thumbnail = ResolveThumbnail(prefab.name, textures);
                RegisterPaletteEntry(prefab, prefab.name, thumbnail);
            }

            LogRefreshSummary(prefabs.Length, textures.Length);

            if (_lastObjectUiRows.Count == 0 && _lastCharacterUiRows.Count == 0)
            {
                Debug.LogWarning(
                    "[WorldBuildingSpawnLibrary] No palette entries registered. "
                    + prefabs.Length + " prefab(s) and " + textures.Length
                    + " UI texture(s) were scanned.");
            }
        }

        static void LogRefreshSummary(int prefabCount, int textureCount)
        {
            if (_loggedRefreshSummary)
                return;

            _loggedRefreshSummary = true;
            Debug.Log(
                "[WorldBuildingSpawnLibrary] Registered "
                + _lastObjectUiRows.Count + " object(s) and "
                + _lastCharacterUiRows.Count + " character(s) from "
                + prefabCount + " prefab(s) and "
                + textureCount + " UI texture(s).");
        }

        static Texture2D[] LoadUiThumbnails()
        {
            _thumbnailByAssetName.Clear();

            Texture2D[] textures = Resources.LoadAll<Texture2D>("WorldBuildingUI");
            if (textures != null)
            {
                for (int i = 0; i < textures.Length; i++)
                    AddUiThumbnail(_thumbnailByAssetName, textures[i]);
            }

            Sprite[] sprites = Resources.LoadAll<Sprite>("WorldBuildingUI");
            if (sprites != null)
            {
                for (int i = 0; i < sprites.Length; i++)
                {
                    Sprite sprite = sprites[i];
                    if (sprite != null)
                        AddUiThumbnail(_thumbnailByAssetName, sprite.texture, sprite.name);
                }
            }

            var list = new List<Texture2D>(_thumbnailByAssetName.Count);
            foreach (Texture2D texture in _thumbnailByAssetName.Values)
            {
                if (texture != null)
                    list.Add(texture);
            }

            return list.ToArray();
        }

        static void AddUiThumbnail(Dictionary<string, Texture2D> byName, Texture2D texture, string assetName = null)
        {
            if (texture == null)
                return;

            string key = string.IsNullOrEmpty(assetName) ? texture.name : assetName;
            if (string.IsNullOrEmpty(key) || byName.ContainsKey(key))
                return;

            byName[key] = texture;
        }

        static Texture2D LoadUiThumbnail(string resourcePath)
        {
            if (string.IsNullOrEmpty(resourcePath))
                return null;

            Texture2D texture = Resources.Load<Texture2D>(resourcePath);
            if (texture != null)
                return texture;

            Sprite sprite = Resources.Load<Sprite>(resourcePath);
            return sprite != null ? sprite.texture : null;
        }

        /// <summary>
        /// Prefabs that must appear in Add Objects even when automatic thumbnail matching fails.
        /// Thumbnail paths are tried in order under Resources/.
        /// </summary>
        static readonly (string prefabName, string[] thumbnailPaths)[] ExplicitPaletteEntries =
        {
            ("Mailbox", new[] { "WorldBuildingUI/Mailbox" }),
            ("Cardboard_Box", new[] { "WorldBuildingUI/Cardboard_Box" }),
            ("Fallen_Leaves", new[] { "WorldBuildingUI/Fallen_Leaves" }),
            ("Flowerbed", new[] { "WorldBuildingUI/Flowerbed" }),
            ("Flower_Pot", new[] { "WorldBuildingUI/Flower_pot", "WorldBuildingUI/Flower_Pot" }),
            ("Hatchway", new[] { "WorldBuildingUI/Hatchway" }),
            ("Road_Decal", new[] { "WorldBuildingUI/Road_Decal" }),
            ("Road_Sign", new[] { "WorldBuildingUI/Road_Sign" }),
            ("Road_\u0421one", new[] { "WorldBuildingUI/Road_Cone" }),
            ("Lamppost", null),
            ("Bike", new[] { "WorldBuildingUI/Bike" }),
            ("Scooter", new[] { "WorldBuildingUI/scooter", "WorldBuildingUI/Scooter" }),
            ("Trash_Bag", new[] { "WorldBuildingUI/Trash_Bag" }),
            ("Trash", new[] { "WorldBuildingUI/Trash" }),
            ("Bush", new[] { "WorldBuildingUI/Bush" }),
            ("TrashCan", new[] { "WorldBuildingUI/TrashCan", "WorldBuildingUI/Trash_Can" }),
            ("FireHydrant", new[] { "WorldBuildingUI/FireHydrant" }),
            ("ParkingMeter", new[] { "WorldBuildingUI/ParkingMeter" }),
            ("Wheelchair_male", new[] { "WorldBuildingUI/Wheelchair_male" }),
        };

        static readonly string[] ExactDisplayNamePrefabNames =
        {
            "Trash",
            "Bush",
            "TrashCan",
            "FireHydrant",
            "ParkingMeter",
        };

        static bool IsPaletteEntryRegistered(string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName))
                return false;

            for (int i = 0; i < _lastSpawnables.Count; i++)
            {
                SpawnableObject existing = _lastSpawnables[i];
                if (existing?.prefab != null &&
                    string.Equals(existing.prefab.name, prefabName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        static void RegisterExplicitPalettePrefabs(Texture2D[] textures)
        {
            for (int i = 0; i < ExplicitPaletteEntries.Length; i++)
            {
                (string prefabName, string[] thumbnailPaths) entry = ExplicitPaletteEntries[i];
                RegisterPalettePrefabIfMissing(entry.prefabName, entry.thumbnailPaths, textures);
            }
        }

        static void RegisterPalettePrefabIfMissing(string prefabName, string[] thumbnailPaths, Texture2D[] textures)
        {
            if (IsPaletteEntryRegistered(prefabName))
                return;

            GameObject prefab = Resources.Load<GameObject>("WorldBuildingSpawns/" + prefabName);
            if (prefab == null)
                return;

            if (!IsPaletteSpawnPrefab(prefab))
                return;

            Texture2D thumbnail = ResolveExplicitThumbnail(prefabName, thumbnailPaths, textures);
            RegisterPaletteEntry(prefab, prefabName, thumbnail);
        }

        static Texture2D ResolveExplicitThumbnail(string prefabName, string[] thumbnailPaths, Texture2D[] textures)
        {
            Texture2D thumbnail = ResolveThumbnail(prefabName, textures);
            if (thumbnail != null)
                return thumbnail;

            if (thumbnailPaths != null)
            {
                for (int i = 0; i < thumbnailPaths.Length; i++)
                {
                    string path = thumbnailPaths[i];
                    if (string.IsNullOrEmpty(path))
                        continue;

                    thumbnail = LoadUiThumbnail(path);
                    if (thumbnail != null)
                        return thumbnail;
                }
            }

            thumbnail = LoadUiThumbnail("WorldBuildingUI/" + prefabName);
            if (thumbnail != null)
                return thumbnail;

            if (UsesExactDisplayName(prefabName))
                return null;

            return LoadUiThumbnail("WorldBuildingUI/" + prefabName.ToLowerInvariant());
        }

        static bool UsesExactDisplayName(string prefabName)
        {
            for (int i = 0; i < ExactDisplayNamePrefabNames.Length; i++)
            {
                if (string.Equals(prefabName, ExactDisplayNamePrefabNames[i], StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        static void RegisterPaletteEntry(GameObject prefab, string prefabName, Texture2D thumbnail)
        {
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
                DisplayName = GetDisplayName(prefabName),
                Thumbnail = thumbnail
            };
            _lastUiRows.Add(uiRow);

            if (IsCharacterSpawnPrefab(prefabName))
                _lastCharacterUiRows.Add(uiRow);
            else
                _lastObjectUiRows.Add(uiRow);
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
            s = Regex.Replace(s, @"([a-z])([A-Z])", "$1 $2");
            s = Regex.Replace(s, @"\s+\(\d+\)\s*$", string.Empty);
            s = Regex.Replace(s, @"\s+0+(\d+)\s*$", " $1");
            return s.Trim();
        }

        static string GetDisplayName(string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName))
                return "Object";

            for (int i = 0; i < ExactDisplayNamePrefabNames.Length; i++)
            {
                if (string.Equals(prefabName, ExactDisplayNamePrefabNames[i], StringComparison.OrdinalIgnoreCase))
                    return ExactDisplayNamePrefabNames[i];
            }

            return HumanizePrefabName(prefabName);
        }

        /// <summary>
        /// Resources.LoadAll also returns imported fbx/glb roots (e.g. BikeModel, ScooterModel).
        /// Those are source meshes, not World Building palette prefabs.
        /// </summary>
        public static bool IsPaletteSpawnPrefab(GameObject candidate)
        {
            if (candidate == null)
                return false;

            string name = candidate.name;
            if (string.IsNullOrEmpty(name))
                return false;

            if (name.EndsWith("Model", StringComparison.OrdinalIgnoreCase))
                return false;

            // Skip raw glb mesh roots (e.g. trashcan.glb) when palette prefabs exist.
            if (IsSourceMeshAssetName(name))
                return false;

            if (IsExcludedPalettePrefabName(name))
                return false;

            return true;
        }

        static bool IsExcludedPalettePrefabName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return false;

            for (int i = 0; i < ExcludedPalettePrefabNames.Length; i++)
            {
                if (string.Equals(name, ExcludedPalettePrefabNames[i], StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Legacy Unity Render Streaming / teleop prefabs kept under WorldBuildingSpawns/Other prefab.
        /// </summary>
        static readonly string[] ExcludedPalettePrefabNames =
        {
            "guestPb",
            "Guest_URS",
            "Robot_URS",
            "Host_URS_WithWebAvatar",
        };

        static bool IsSourceMeshAssetName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return false;

            for (int i = 0; i < SourceMeshAssetNames.Length; i++)
            {
                // Case-sensitive: raw imported meshes are lowercase (trashcan.glb),
                // palette prefabs are PascalCase (TrashCan.prefab).
                if (string.Equals(name, SourceMeshAssetNames[i], StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        static readonly string[] SourceMeshAssetNames =
        {
            "trashcan",
            "firehydrant",
            "parkingmeter",
            "bikemodel",
            "scootermodel",
        };

        static Texture2D ResolveThumbnail(string prefabName, Texture2D[] textures)
        {
            if (string.IsNullOrEmpty(prefabName))
                return null;

            if (_thumbnailByAssetName.TryGetValue(prefabName, out Texture2D direct))
                return direct;

            if (textures == null || textures.Length == 0)
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
