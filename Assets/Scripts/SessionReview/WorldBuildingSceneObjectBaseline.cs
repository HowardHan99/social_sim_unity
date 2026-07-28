using System.Collections.Generic;
using UnityEngine;

namespace SessionReview
{
    /// <summary>
    /// Attached to a PRE-EXISTING scene object the moment World Building makes it editable
    /// (selection, moveable-binding, or a gizmo move). Remembers the object's original pose
    /// and hierarchy path so <see cref="WorldBuildingScenarioStore"/> can save the whole
    /// scene as a delta over the base .unity scene: only objects whose pose changed (or
    /// that were deleted) are recorded, and on reload the same object is found again by
    /// path + original pose. Runtime-spawned objects use
    /// <see cref="WorldBuildingPlacedObject"/> instead.
    /// </summary>
    public class WorldBuildingSceneObjectBaseline : MonoBehaviour
    {
        public Vector3 originalPosition;
        public Quaternion originalRotation = Quaternion.identity;
        public Vector3 originalScale = Vector3.one;
        public string scenePath;

        /// <summary>
        /// True for task start/goal markers and PWD spawner markers. Their ACTIVE state is
        /// engine-managed (initStartAndGoal deactivates the unused start/goal every scene
        /// load), so scenario saving must record only their POSE — never a "deleted" delta,
        /// which would hide a live goal on restore.
        /// </summary>
        public bool isProtectedMarker;

        public void Capture()
        {
            Capture(transform.position, transform.rotation);
        }

        public void Capture(Vector3 position, Quaternion rotation)
        {
            originalPosition = position;
            originalRotation = rotation;
            originalScale = transform.localScale;
            scenePath = BuildScenePath(transform);
        }

        public bool HasMoved()
        {
            return (transform.position - originalPosition).sqrMagnitude > 1e-6f
                || Quaternion.Angle(originalRotation, transform.rotation) > 0.25f
                || (transform.localScale - originalScale).sqrMagnitude > 1e-6f;
        }

        /// <summary>Root-to-leaf hierarchy path ("Env/Props/Bench"). Names repeat in scenes,
        /// so restore additionally disambiguates by the stored original pose.</summary>
        public static string BuildScenePath(Transform t)
        {
            var names = new List<string>();
            for (Transform cur = t; cur != null; cur = cur.parent)
                names.Add(cur.name);
            names.Reverse();
            return string.Join("/", names);
        }
    }
}
