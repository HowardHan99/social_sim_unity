using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace SessionReview
{
    public enum WorldBuildingSpawnSourceType
    {
        Prefab,
        MeshyGlb
    }

    /// <summary>
    /// One row for the World Building IMGUI spawn palette (prefab + display label + optional thumbnail).
    /// </summary>
    public sealed class WorldBuildingSpawnUiRow
    {
        public string SpawnId;
        public string DisplayName;
        public Texture2D Thumbnail;
        public WorldBuildingSpawnSourceType SourceType;
        public string ImportGlbPath;

        /// <summary>
        /// True for rigged character prefabs whose palette card offers both spawn modes:
        /// Static (passive prop) and Walking (SFAgent wandering the NavMesh). Imported GLB
        /// models have no rig, so they stay single-mode.
        /// </summary>
        public bool SupportsAgentModes;

        public bool IsImportedGlb =>
            SourceType == WorldBuildingSpawnSourceType.MeshyGlb &&
            !string.IsNullOrEmpty(ImportGlbPath);
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
        private static bool _builtFromResources;
        private static bool _loggedMissingPrefabs;

        public static IReadOnlyList<SpawnableObject> LastSpawnables => _lastSpawnables;
        public static IReadOnlyList<WorldBuildingSpawnUiRow> LastUiRows => _lastUiRows;
        public static IReadOnlyList<WorldBuildingSpawnUiRow> LastObjectUiRows => _lastObjectUiRows;
        public static IReadOnlyList<WorldBuildingSpawnUiRow> LastCharacterUiRows => _lastCharacterUiRows;

        /// <summary>
        /// Builds the palette from Resources and the local Meshy GLB cache. Callers sit on OnGUI
        /// paths that run several times per frame, so re-scanning every event would spam the
        /// console and re-walk every asset. Pass <paramref name="force"/> after generating a new
        /// Meshy model to refresh the disk-backed rows.
        /// </summary>
        public static void RefreshFromResources(bool force = false)
        {
            if (_builtFromResources && !force)
                return;

            Texture2D[] textures = LoadUiThumbnails();
            _lastSpawnables = new List<SpawnableObject>();
            _lastUiRows = new List<WorldBuildingSpawnUiRow>();
            _lastObjectUiRows = new List<WorldBuildingSpawnUiRow>();
            _lastCharacterUiRows = new List<WorldBuildingSpawnUiRow>();

            GameObject[] prefabs = Resources.LoadAll<GameObject>("WorldBuildingSpawns");
            int prefabCount = prefabs != null ? prefabs.Length : 0;
            if (prefabCount == 0)
            {
                if (!_loggedMissingPrefabs)
                {
                    _loggedMissingPrefabs = true;
                    Debug.LogWarning("[WorldBuildingSpawnLibrary] No prefabs found under Resources/WorldBuildingSpawns.");
                }
            }
            else
            {
                Array.Sort(prefabs, (a, b) =>
                    string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase));

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
            }

            RegisterWalkingPlayerCharacters();
            RegisterDynamicPedestrian(textures);
            int meshyCount = RegisterMeshyGeneratedModels();

            _builtFromResources = true;
            LogRefreshSummary(prefabCount, textures.Length, meshyCount);

            if (_lastObjectUiRows.Count == 0 && _lastCharacterUiRows.Count == 0)
            {
                Debug.LogWarning(
                    "[WorldBuildingSpawnLibrary] No palette entries registered. "
                    + prefabCount + " prefab(s), " + textures.Length
                    + " UI texture(s), and " + meshyCount + " Meshy model(s) were scanned.");
            }
        }

        static void LogRefreshSummary(int prefabCount, int textureCount, int meshyCount)
        {
            if (_loggedRefreshSummary)
                return;

            _loggedRefreshSummary = true;
            Debug.Log(
                "[WorldBuildingSpawnLibrary] Registered "
                + _lastObjectUiRows.Count + " object(s) and "
                + _lastCharacterUiRows.Count + " character(s) from "
                + prefabCount + " prefab(s) and "
                + textureCount + " UI texture(s), plus "
                + meshyCount + " Meshy model(s).");
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

            bool isCharacter = IsCharacterSpawnPrefab(prefabName);
            var uiRow = new WorldBuildingSpawnUiRow
            {
                SpawnId = id,
                DisplayName = GetDisplayName(prefabName),
                Thumbnail = thumbnail,
                SourceType = WorldBuildingSpawnSourceType.Prefab,
                SupportsAgentModes = isCharacter
            };
            _lastUiRows.Add(uiRow);

            if (isCharacter)
                _lastCharacterUiRows.Add(uiRow);
            else
                _lastObjectUiRows.Add(uiRow);
        }

        static int RegisterMeshyGeneratedModels()
        {
            string dir = MeshyGlbSceneImporter.GetMeshyModelsDirectory();
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                return 0;

            string[] files;
            try
            {
                files = Directory.GetFiles(dir, "*.glb", SearchOption.TopDirectoryOnly);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[WorldBuildingSpawnLibrary] Could not scan Meshy models at '{dir}': {ex.Message}");
                return 0;
            }

            Array.Sort(files, (a, b) =>
                string.Compare(Path.GetFileNameWithoutExtension(a), Path.GetFileNameWithoutExtension(b),
                    StringComparison.OrdinalIgnoreCase));

            int registered = 0;
            for (int i = 0; i < files.Length; i++)
            {
                string path = files[i];
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                    continue;

                string displayName = HumanizePrefabName(Path.GetFileNameWithoutExtension(path));
                var uiRow = new WorldBuildingSpawnUiRow
                {
                    SpawnId = "meshy:" + path,
                    DisplayName = displayName,
                    Thumbnail = null,
                    SourceType = WorldBuildingSpawnSourceType.MeshyGlb,
                    ImportGlbPath = path
                };

                _lastUiRows.Add(uiRow);
                if (IsCharacterGeneratedModel(displayName))
                    _lastCharacterUiRows.Add(uiRow);
                else
                    _lastObjectUiRows.Add(uiRow);
                registered++;
            }

            return registered;
        }

        /// <summary>
        /// Finds a palette entry that a free-text prompt is describing ("a red fire hydrant"
        /// —Fire Hydrant), so World Building can place an existing prefab instead of paying
        /// for an AI generation. Returns null when nothing matches confidently —the caller
        /// should generate in that case.
        /// </summary>
        public static WorldBuildingSpawnUiRow FindBestMatch(string prompt)
        {
            string[] promptTokens = TokenizePrompt(prompt);
            if (promptTokens.Length == 0)
                return null;

            string promptKey = string.Concat(promptTokens);
            WorldBuildingSpawnUiRow best = null;
            int bestScore = 0;

            for (int i = 0; i < _lastUiRows.Count; i++)
            {
                WorldBuildingSpawnUiRow row = _lastUiRows[i];
                if (row == null || string.IsNullOrEmpty(row.DisplayName))
                    continue;

                // Palette names are already canonical —running them through the synonym map
                // too would fold "Road Sign" into a doubled "roadroadsign" key.
                string[] rowTokens = Tokenize(row.DisplayName, applySynonyms: false);
                if (rowTokens.Length == 0)
                    continue;

                int score = ScorePromptMatch(promptTokens, promptKey, rowTokens);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = row;
                }
            }

            return bestScore >= MinPromptMatchScore ? best : null;
        }

        private const int MinPromptMatchScore = 100;

        /// <summary>
        /// Tiers are weighted by how many characters of the matched name were accounted for,
        /// so a longer, more specific name wins: "a bench next to a trash can" must resolve to
        /// TrashCan, not to the shorter Trash that is also present in the sentence.
        /// </summary>
        static int ScorePromptMatch(string[] promptTokens, string promptKey, string[] rowTokens)
        {
            string rowKey = string.Concat(rowTokens);

            // "trash can" vs prefab "TrashCan": token counts differ but the collapsed keys agree.
            if (string.Equals(promptKey, rowKey, StringComparison.Ordinal))
                return 1000 + rowKey.Length;

            // The prompt says everything the name says: "a big red fire hydrant" -> Fire Hydrant.
            if (AllTokensPresent(rowTokens, promptTokens) ||
                (rowKey.Length >= 4 && promptKey.Contains(rowKey)))
                return 500 + 8 * rowKey.Length;

            // The prompt is a fragment of the name: "hydrant" -> FireHydrant.
            if (AllTokensPresent(promptTokens, rowTokens) ||
                (promptKey.Length >= 4 && rowKey.Contains(promptKey)))
                return 300 + 8 * promptKey.Length;

            // Fall back to shared distinctive words ("traffic cone" -> Road Cone). Short words
            // like "car" are too weak on their own to outrank an actual generation.
            int overlap = 0;
            for (int i = 0; i < rowTokens.Length; i++)
            {
                if (rowTokens[i].Length >= 4 && Array.IndexOf(promptTokens, rowTokens[i]) >= 0)
                    overlap++;
            }

            return overlap > 0 ? 100 + 10 * overlap : 0;
        }

        static bool AllTokensPresent(string[] needles, string[] haystack)
        {
            for (int i = 0; i < needles.Length; i++)
            {
                if (Array.IndexOf(haystack, needles[i]) < 0)
                    return false;
            }

            return needles.Length > 0;
        }

        static string[] TokenizePrompt(string text)
        {
            return Tokenize(text, applySynonyms: true);
        }

        /// <summary>
        /// Lowercases, splits on non-alphanumerics, drops filler words and singularizes.
        /// With <paramref name="applySynonyms"/> it also maps everyday words onto palette
        /// vocabulary, so "Add 2 garbage bins please" and "TrashCan" meet in the middle.
        /// </summary>
        static string[] Tokenize(string text, bool applySynonyms)
        {
            if (string.IsNullOrWhiteSpace(text))
                return Array.Empty<string>();

            string cleaned = Regex.Replace(text.ToLowerInvariant(), @"[^a-z0-9]+", " ");
            string[] raw = cleaned.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

            var tokens = new List<string>(raw.Length);
            for (int i = 0; i < raw.Length; i++)
            {
                string token = Singularize(raw[i]);
                if (token.Length == 0 || IsPromptStopWord(token))
                    continue;

                if (applySynonyms && PromptSynonyms.TryGetValue(token, out string canonical))
                    token = canonical;

                if (!tokens.Contains(token))
                    tokens.Add(token);
            }

            return tokens.ToArray();
        }

        static string Singularize(string token)
        {
            if (token.Length > 4 && token.EndsWith("ies", StringComparison.Ordinal))
                return token.Substring(0, token.Length - 3) + "y";
            if (token.Length > 4 && token.EndsWith("es", StringComparison.Ordinal))
                return token.Substring(0, token.Length - 2);
            if (token.Length > 3 && token.EndsWith("s", StringComparison.Ordinal) &&
                !token.EndsWith("ss", StringComparison.Ordinal))
                return token.Substring(0, token.Length - 1);

            return token;
        }

        static bool IsPromptStopWord(string token)
        {
            for (int i = 0; i < PromptStopWords.Length; i++)
            {
                if (string.Equals(token, PromptStopWords[i], StringComparison.Ordinal))
                    return true;
            }

            // Bare numbers and colors describe the request, not which prefab it is.
            return token.Length == 1 || IsAllDigits(token);
        }

        static bool IsAllDigits(string token)
        {
            for (int i = 0; i < token.Length; i++)
            {
                if (!char.IsDigit(token[i]))
                    return false;
            }

            return token.Length > 0;
        }

        static readonly string[] PromptStopWords =
        {
            "a", "an", "the", "some", "any", "this", "that", "these", "those",
            "add", "place", "put", "create", "generate", "make", "build", "spawn", "give", "want", "need",
            "please", "me", "my", "new", "of", "with", "and", "for", "in", "on", "at", "to", "into",
            "model", "object", "asset", "prefab", "mesh", "3d", "realistic", "detailed", "simple", "small",
            "big", "large", "tall", "short", "old", "modern", "nice", "good",
            "red", "blue", "green", "yellow", "black", "white", "gray", "grey", "brown", "orange", "purple",
        };

        /// <summary>
        /// Everyday words mapped onto the vocabulary the palette prefabs actually use. Only
        /// word-for-word swaps belong here —a prompt that is a fragment of a name ("hydrant",
        /// "cone", "box") already matches through the containment tiers.
        /// </summary>
        static readonly Dictionary<string, string> PromptSynonyms = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "bicycle", "bike" },
            { "cycle", "bike" },
            { "garbage", "trash" },
            { "rubbish", "trash" },
            { "waste", "trash" },
            { "litter", "trash" },
            { "bin", "can" },
            { "streetlight", "lamppost" },
            { "streetlamp", "lamppost" },
            { "lamp", "lamppost" },
            { "shrub", "bush" },
            { "hedge", "bush" },
            { "carton", "box" },
            { "crate", "box" },
            { "package", "box" },
            { "parcel", "box" },
            { "signpost", "sign" },
            { "letterbox", "mailbox" },
            { "postbox", "mailbox" },
            { "moped", "scooter" },
            { "vespa", "scooter" },
            { "planter", "pot" },
            { "leaf", "leave" },
        };

        /// <summary>
        /// Walking player characters (Resources/PlayerCharacters) double as World Building
        /// characters, so scenes can be populated with the same avatars the player can pick.
        /// Thumbnails come from Resources/PlayerCharactersUI via PlayerCharacterLibrary.
        /// </summary>
        static void RegisterWalkingPlayerCharacters()
        {
            IReadOnlyList<PlayerCharacterOption> options = PlayerCharacterLibrary.Options;
            for (int i = 0; i < options.Count; i++)
            {
                PlayerCharacterOption option = options[i];
                if (option?.Prefab == null || IsPaletteEntryRegistered(option.Prefab.name))
                    continue;

                RegisterPaletteEntry(option.Prefab, option.Prefab.name, option.Thumbnail);
            }
        }

        /// <summary>
        /// Resources path of the autonomous pedestrian agent. RandomAvatar builds an SFAgent
        /// (IVI.INavigable) on Awake; placed via World Building it walks the baked NavMesh.
        /// </summary>
        const string DynamicPedestrianPrefabPath = "Prefabs/RocketboxRandomAnimatedAgent";

        /// <summary>Root prefab name of the dynamic (walking) pedestrian palette entry.</summary>
        public const string DynamicPedestrianPrefabName = "RocketboxRandomAnimatedAgent";

        const string DynamicPedestrianDisplayName = "Pedestrian";

        /// <summary>Marker label for a pedestrian placed in Static mode.</summary>
        public const string StaticPedestrianDisplayName = "Pedestrian (Static)";

        /// <summary>
        /// Virtual palette name recorded on a static pedestrian placement. Not a real prefab:
        /// both spawn modes share the RocketboxRandomAnimatedAgent prefab, whose plain name in
        /// old saves means "walking", so scenario save/restore needs a distinct name to re-spawn
        /// the static one through the passive-prop pipeline.
        /// </summary>
        public const string StaticPedestrianPaletteName = DynamicPedestrianPrefabName + "_Static";

        /// <summary>
        /// Adds the random pedestrian as an Add-Characters entry. Like every character card it
        /// offers Static and Walking; Walking keeps the navigation controllers RandomAvatar
        /// builds (SFAgent) —RuntimeEditorManager branches on
        /// <see cref="IsDynamicPedestrianPrefab"/> to skip the passive-prop pipeline and attach
        /// WorldBuildingWanderPedestrian —while Static goes through the passive-prop pipeline,
        /// which strips the SFAgent so the avatar just stands where it is placed.
        /// </summary>
        static void RegisterDynamicPedestrian(Texture2D[] textures)
        {
            GameObject prefab = Resources.Load<GameObject>(DynamicPedestrianPrefabPath);
            if (prefab == null)
            {
                Debug.LogWarning("[WorldBuildingSpawnLibrary] Dynamic pedestrian prefab not found at Resources/"
                    + DynamicPedestrianPrefabPath + "; the walking pedestrian will be missing from Add Characters.");
                return;
            }

            if (IsPaletteEntryRegistered(prefab.name))
                return;

            // Exact-path only (no fuzzy matching) so this never borrows an unrelated object icon;
            // drop a Resources/WorldBuildingUI/Pedestrian texture to give it art.
            Texture2D thumbnail = LoadUiThumbnail("WorldBuildingUI/Pedestrian");
            RegisterForcedCharacterEntry(prefab, DynamicPedestrianDisplayName, thumbnail);
        }

        /// <summary>
        /// Registers a palette entry forced into the character list with an explicit display
        /// name, for character prefabs whose name would not pass <see cref="IsCharacterSpawnPrefab"/>.
        /// </summary>
        static void RegisterForcedCharacterEntry(GameObject prefab, string displayName, Texture2D thumbnail)
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
                DisplayName = displayName,
                Thumbnail = thumbnail,
                SourceType = WorldBuildingSpawnSourceType.Prefab,
                SupportsAgentModes = true
            };
            _lastUiRows.Add(uiRow);
            _lastCharacterUiRows.Add(uiRow);
        }

        /// <summary>
        /// True for the autonomous walking pedestrian, which must keep its navigation
        /// controllers (i.e. must NOT be turned into a passive prop) when placed.
        /// </summary>
        public static bool IsDynamicPedestrianPrefab(string prefabName)
        {
            return !string.IsNullOrEmpty(prefabName)
                && string.Equals(prefabName, DynamicPedestrianPrefabName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// True for the virtual palette name a static pedestrian placement is saved under
        /// (see <see cref="StaticPedestrianPaletteName"/>).
        /// </summary>
        public static bool IsStaticPedestrianPaletteName(string prefabName)
        {
            return !string.IsNullOrEmpty(prefabName)
                && string.Equals(prefabName, StaticPedestrianPaletteName, StringComparison.OrdinalIgnoreCase);
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

            // Anything under Resources/PlayerCharacters is a character regardless of name.
            return PlayerCharacterLibrary.FindPrefab(prefabName) != null;
        }

        public static bool IsCharacterGeneratedModel(string displayNameOrPrompt)
        {
            if (string.IsNullOrWhiteSpace(displayNameOrPrompt))
                return false;

            string n = displayNameOrPrompt.ToLowerInvariant();
            if (n.Contains("wheelchair"))
                return true;

            string[] tokens = Tokenize(displayNameOrPrompt, applySynonyms: false);
            for (int i = 0; i < tokens.Length; i++)
            {
                for (int j = 0; j < GeneratedCharacterTokens.Length; j++)
                {
                    if (string.Equals(tokens[i], GeneratedCharacterTokens[j], StringComparison.Ordinal))
                        return true;
                }
            }

            return false;
        }

        static readonly string[] GeneratedCharacterTokens =
        {
            "character",
            "avatar",
            "person",
            "people",
            "human",
            "pedestrian",
            "pwd",
            "man",
            "woman",
            "boy",
            "girl",
            "male",
            "female",
            "walker",
            "passenger",
            "user",
            "guest",
            "host"
        };

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
