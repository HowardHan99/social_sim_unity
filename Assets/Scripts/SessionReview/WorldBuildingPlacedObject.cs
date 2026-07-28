using UnityEngine;

namespace SessionReview
{
    /// <summary>
    /// Marks an object placed at runtime by World Building (palette spawn or Meshy GLB
    /// import) so <see cref="WorldBuildingScenarioStore"/> can find, serialize and later
    /// re-create it when a saved scenario is reloaded. Exactly one of
    /// <see cref="paletteName"/> / <see cref="importGlbPath"/> is set.
    /// </summary>
    public class WorldBuildingPlacedObject : MonoBehaviour
    {
        [Tooltip("Prefab name under Resources/WorldBuildingSpawns for palette spawns; empty for GLB imports.")]
        public string paletteName;

        [Tooltip("Absolute path of the imported GLB file for Meshy imports; empty for palette spawns.")]
        public string importGlbPath;

        [Tooltip("Human-readable label (imports only; palette spawns use the prefab name).")]
        public string displayName;

        [Tooltip("True when this character was spawned as a moving agent (SFAgent + wander) rather than a static prop, so a saved scenario restores the same mode.")]
        public bool dynamicAgent;
    }
}
