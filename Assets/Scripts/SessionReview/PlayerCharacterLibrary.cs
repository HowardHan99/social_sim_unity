using System;
using System.Collections.Generic;
using UnityEngine;

namespace SessionReview
{
    /// <summary>
    /// One selectable player character (walking avatar) for onboarding / TestScene.
    /// Id is the prefab name inside Resources/PlayerCharacters.
    /// </summary>
    public sealed class PlayerCharacterOption
    {
        public string Id;
        public string DisplayName;
        public GameObject Prefab;
        public Texture2D Thumbnail;
    }

    /// <summary>
    /// Enumerates walking player character prefabs dropped into
    /// <c>Resources/PlayerCharacters</c> (thumbnails, optional, from
    /// <c>Resources/PlayerCharactersUI</c> matched by prefab name). Unlike
    /// <see cref="WorldBuildingSpawnLibrary"/>, a prefab without a thumbnail is still
    /// listed so a freshly dropped avatar is selectable before its preview art exists.
    /// The built-in wheelchair pair is NOT listed here; it stays the default choice
    /// (SelectedPlayerCharacterId empty).
    /// </summary>
    public static class PlayerCharacterLibrary
    {
        private const string PrefabFolder = "PlayerCharacters";
        private const string ThumbnailFolder = "PlayerCharactersUI";

        private static List<PlayerCharacterOption> _options;

        public static IReadOnlyList<PlayerCharacterOption> Options
        {
            get
            {
                if (_options == null)
                    Refresh();
                return _options;
            }
        }

        public static void Refresh()
        {
            _options = new List<PlayerCharacterOption>();

            GameObject[] prefabs = Resources.LoadAll<GameObject>(PrefabFolder);
            if (prefabs == null || prefabs.Length == 0)
                return;

            Array.Sort(prefabs, (a, b) =>
                string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase));

            Texture2D[] textures = Resources.LoadAll<Texture2D>(ThumbnailFolder);
            if (textures == null)
                textures = Array.Empty<Texture2D>();

            foreach (GameObject prefab in prefabs)
            {
                if (prefab == null)
                    continue;

                _options.Add(new PlayerCharacterOption
                {
                    Id = prefab.name,
                    DisplayName = WorldBuildingSpawnLibrary.HumanizePrefabName(prefab.name),
                    Prefab = prefab,
                    Thumbnail = FindThumbnail(prefab.name, textures)
                });
            }
        }

        public static GameObject FindPrefab(string id)
        {
            if (string.IsNullOrEmpty(id))
                return null;

            foreach (PlayerCharacterOption option in Options)
            {
                if (option != null && string.Equals(option.Id, id, StringComparison.OrdinalIgnoreCase))
                    return option.Prefab;
            }
            return null;
        }

        private static Texture2D FindThumbnail(string prefabName, Texture2D[] textures)
        {
            if (textures == null || string.IsNullOrEmpty(prefabName))
                return null;

            foreach (Texture2D t in textures)
            {
                if (t != null && string.Equals(t.name, prefabName, StringComparison.OrdinalIgnoreCase))
                    return t;
            }

            foreach (Texture2D t in textures)
            {
                if (t == null) continue;
                if (t.name.IndexOf(prefabName, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    prefabName.IndexOf(t.name, StringComparison.OrdinalIgnoreCase) >= 0)
                    return t;
            }

            return null;
        }
    }
}
