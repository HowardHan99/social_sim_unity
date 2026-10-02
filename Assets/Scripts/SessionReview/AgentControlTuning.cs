using System.Collections.Generic;
using UnityEngine;

namespace SessionReview
{
    /// <summary>
    /// Shared runtime rules for authored agent prefabs whose visual forward/animation
    /// setup does not match the plain pedestrian controller assumptions.
    /// </summary>
    public static class AgentControlTuning
    {
        private static readonly Dictionary<string, Vector3> DriveAxisCache = new Dictionary<string, Vector3>();

        public static Animator FindAnimator(GameObject root)
        {
            if (root == null) return null;
            return root.GetComponent<Animator>() ?? root.GetComponentInChildren<Animator>(true);
        }

        public static bool ShouldPreserveAuthoredAnimatorController(GameObject instance, GameObject prefab, Animator animator)
        {
            if (ContainsKnownAuthoredName(instance) || ContainsKnownAuthoredName(prefab))
                return true;
            if (instance != null &&
                (instance.GetComponentInChildren<IVI.PhoneUserArmPose>(true) != null ||
                 instance.GetComponentInChildren<IVI.BikeAnimateInPlace>(true) != null ||
                 instance.GetComponentInChildren<WorldBuildingCompositeAgentDriver>(true) != null))
                return true;
            return ContainsKnownAuthoredName(animator != null ? animator.gameObject : null) ||
                   ContainsKnownAuthoredController(animator);
        }

        /// <summary>
        /// Whether root motion can actually translate this rig. The background-pedestrian
        /// pipeline moves agents with root motion, so a rig that fails this test would
        /// compute a walking velocity and never move — it stands where it was placed
        /// (blocking a road, in the case that surfaced this). Such rigs must be driven
        /// from the agent's velocity instead (WorldBuildingCompositeAgentDriver).
        ///
        /// Two ways it fails in this project's assets:
        /// - No SkinnedMeshRenderer anywhere: the "character" is a rigid mesh (Cane User is
        ///   a static photogrammetry scan), so no clip can deform or move it.
        /// - A humanoid Avatar that does not bind to this hierarchy — the same silent
        ///   failure PhoneUserArmPose warns about; GetBoneTransform then returns null.
        /// </summary>
        /// <summary>
        /// Whether the shared humanoid walk controller can actually drive this rig. Only a
        /// humanoid Avatar that binds to this hierarchy can play those clips: the community
        /// rigs include Generic (non-humanoid) skinned models — the Phone User's woman, the
        /// Scooter rider, the Dog Walker's dog — where assigning it silently animates
        /// nothing, which also means no root motion and therefore no movement.
        /// </summary>
        public static bool CanRetargetSharedWalkController(Animator animator)
        {
            return CanRootMotionDrive(animator) &&
                   animator.avatar != null &&
                   animator.isHuman &&
                   animator.GetBoneTransform(HumanBodyBones.Hips) != null;
        }

        public static bool CanRootMotionDrive(Animator animator)
        {
            if (animator == null || animator.runtimeAnimatorController == null)
                return false;
            if (animator.GetComponentInChildren<SkinnedMeshRenderer>(true) == null)
                return false;
            if (animator.isHuman && animator.GetBoneTransform(HumanBodyBones.Hips) == null)
                return false;
            return true;
        }

        public static bool ShouldUseVisualDriveFrame(GameObject root, Animator animator = null)
        {
            if (root == null) return false;

            // Recorded at spawn time. World-Building renames placed agents to
            // WB_Pedestrian_<id>, which erases the prefab name that identifies a
            // Scooter/Phone rig (their Animator lives inside a nested FBX, so the
            // animator-name check below cannot see it either).
            var driver = root.GetComponentInChildren<WorldBuildingCompositeAgentDriver>(true);
            if (driver != null)
                return driver.ridingRig;

            if (NameContains(root.name, "bike") ||
                NameContains(root.name, "bicycle") ||
                NameContains(root.name, "cyclist") ||
                NameContains(root.name, "scooter"))
                return true;

            if (animator != null &&
                (NameContains(animator.gameObject.name, "bike") ||
                 NameContains(animator.gameObject.name, "bicycle") ||
                 NameContains(animator.gameObject.name, "cyclist") ||
                 NameContains(animator.gameObject.name, "scooter")))
                return true;

            return root.GetComponentInChildren<IVI.BikeAnimateInPlace>(true) != null;
        }

        public static Vector3 ResolveDriveLocalAxis(Transform root)
        {
            if (root == null) return Vector3.forward;

            Vector3 fresh = EstimateDriveLocalAxis(root);
            string key = DriveAxisCacheKey(root.gameObject);
            if ((!DriveAxisCache.TryGetValue(key, out Vector3 cached) || cached.sqrMagnitude <= 0.5f) &&
                TryLoadPersistedAxis(key, out Vector3 persisted))
            {
                cached = persisted;
                DriveAxisCache[key] = persisted;
            }
            if (cached.sqrMagnitude > 0.5f)
            {
                float align = Mathf.Abs(Vector3.Dot(cached.normalized, fresh));
                if (align > 0.85f || align < 0.15f)
                    return cached.normalized;
            }

            DriveAxisCache[key] = fresh.normalized;
            return fresh.normalized;
        }

        /// <param name="persist">
        /// True for an explicit flip/rotate correction made by a person: it is written to
        /// PlayerPrefs so the same rig starts with the corrected axis in every later
        /// session. Automatic estimates stay in-memory only.
        /// </param>
        public static void CacheDriveLocalAxis(GameObject root, Vector3 localAxis, bool persist = false)
        {
            if (root == null || localAxis.sqrMagnitude < 0.25f) return;
            string key = DriveAxisCacheKey(root);
            Vector3 axis = localAxis.normalized;
            DriveAxisCache[key] = axis;

            // WB pedestrians are named per-instance (WB_Pedestrian_<id>) unless their
            // driver carries a stable source-prefab key; persisting the throwaway names
            // would only litter PlayerPrefs with entries no later spawn can match.
            if (persist && !string.IsNullOrEmpty(key) &&
                !key.StartsWith("WB_Pedestrian_", System.StringComparison.Ordinal))
            {
                PlayerPrefs.SetString(AxisPrefPrefix + key, FormatAxis(axis));
                PlayerPrefs.Save();
            }
        }

        public static float EstimateDriveYaw(Transform root)
        {
            if (root == null) return 0f;
            Animator animator = FindAnimator(root.gameObject);
            if (!ShouldUseVisualDriveFrame(root.gameObject, animator))
                return root.eulerAngles.y;

            Vector3 world = root.TransformDirection(ResolveDriveLocalAxis(root));
            world.y = 0f;
            if (world.sqrMagnitude < 1e-8f)
                return root.eulerAngles.y;
            return Mathf.Atan2(world.x, world.z) * Mathf.Rad2Deg;
        }

        public static void UpdateLocomotionAnimator(
            Animator animator,
            Transform root,
            Vector3 worldVelocity,
            float signedForwardSpeed,
            bool scaleStandardAnimatorSpeed,
            float yawDegrees)
        {
            if (animator == null) return;

            float speed = Mathf.Abs(signedForwardSpeed);
            if (worldVelocity.sqrMagnitude > speed * speed)
                speed = worldVelocity.magnitude;

            bool hasStandardForward = HasParameter(animator, "Forward", AnimatorControllerParameterType.Float);
            bool hasStandardStrafe = HasParameter(animator, "Strafe", AnimatorControllerParameterType.Float);
            bool hasStandardIdle = HasParameter(animator, "Idling", AnimatorControllerParameterType.Bool);

            if (hasStandardIdle)
                animator.SetBool("Idling", speed < 0.1f);

            if (hasStandardForward || hasStandardStrafe)
            {
                Vector3 local = Quaternion.Euler(0f, -yawDegrees, 0f) * worldVelocity;
                if (hasStandardForward)
                    animator.SetFloat("Forward", local.z / 0.6f);
                if (hasStandardStrafe)
                    animator.SetFloat("Strafe", local.x / 0.6f);
            }

            if (HasParameter(animator, "isIdling", AnimatorControllerParameterType.Bool))
                animator.SetBool("isIdling", speed < 0.1f);
            if (HasParameter(animator, "IsBiking", AnimatorControllerParameterType.Bool))
                animator.SetBool("IsBiking", speed >= 0.1f);

            bool standardLocomotion = hasStandardForward && hasStandardStrafe;
            if (scaleStandardAnimatorSpeed && standardLocomotion && !ContainsKnownAuthoredController(animator))
                animator.speed = speed > 0.1f ? speed : 1f;
        }

        public static bool HasParameter(Animator animator, string name, AnimatorControllerParameterType type)
        {
            if (animator == null || string.IsNullOrEmpty(name)) return false;
            foreach (AnimatorControllerParameter parameter in animator.parameters)
            {
                if (parameter.type == type && parameter.name == name)
                    return true;
            }
            return false;
        }

        private static bool ContainsKnownAuthoredName(GameObject go)
        {
            if (go == null) return false;
            return NameContains(go.name, "phone") ||
                   NameContains(go.name, "bike") ||
                   NameContains(go.name, "bicycle") ||
                   NameContains(go.name, "cyclist") ||
                   NameContains(go.name, "scooter");
        }

        private static bool ContainsKnownAuthoredController(Animator animator)
        {
            if (animator == null || animator.runtimeAnimatorController == null) return false;
            string name = animator.runtimeAnimatorController.name;
            return NameContains(name, "phone") ||
                   NameContains(name, "bike") ||
                   NameContains(name, "cyclist");
        }

        private static bool NameContains(string value, string needle)
        {
            return !string.IsNullOrEmpty(value) &&
                   value.IndexOf(needle, System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string DriveAxisCacheKey(GameObject go)
        {
            if (go == null) return string.Empty;

            // A composite WB agent is renamed to WB_Pedestrian_<id> at spawn, but its
            // driver remembers which prefab it came from: keying by that shares one
            // calibration between every spawn of the same character AND the ridden
            // player copy (the player character id IS the prefab name).
            var driver = go.GetComponentInChildren<WorldBuildingCompositeAgentDriver>(true);
            if (driver != null && !string.IsNullOrEmpty(driver.sourcePrefabName))
                return "Char:" + driver.sourcePrefabName;

            string n = go.name;
            int clone = n.IndexOf("(Clone)", System.StringComparison.Ordinal);
            if (clone >= 0) n = n.Substring(0, clone);
            n = n.Trim();

            // Every selected player character spawns under the fixed name "PWDPlayer",
            // so an unscoped key would hand one character's axis to the next: after a
            // Cyclist run, the Scooter inherited the bike's sideways axis and drove on
            // a 90-degree heading with a side-on camera. Scope the key per character.
            if (n == "PWDPlayer")
            {
                string id = SessionOnboardingSettings.SelectedPlayerCharacterId;
                if (!string.IsNullOrEmpty(id))
                    return "Char:" + id;
            }
            return n;
        }

        private const string AxisPrefPrefix = "SEAN.AgentControlTuning.DriveAxis.";

        private static string FormatAxis(Vector3 axis)
        {
            return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "{0:R};{1:R};{2:R}", axis.x, axis.y, axis.z);
        }

        private static bool TryLoadPersistedAxis(string key, out Vector3 axis)
        {
            axis = Vector3.zero;
            if (string.IsNullOrEmpty(key))
                return false;

            string raw = PlayerPrefs.GetString(AxisPrefPrefix + key, string.Empty);
            if (string.IsNullOrEmpty(raw))
                return false;

            string[] parts = raw.Split(';');
            if (parts.Length != 3)
                return false;

            var style = System.Globalization.NumberStyles.Float;
            var culture = System.Globalization.CultureInfo.InvariantCulture;
            if (!float.TryParse(parts[0], style, culture, out float x) ||
                !float.TryParse(parts[1], style, culture, out float y) ||
                !float.TryParse(parts[2], style, culture, out float z))
                return false;

            axis = new Vector3(x, y, z);
            return axis.sqrMagnitude > 0.5f;
        }

        private static Vector3 EstimateDriveLocalAxis(Transform root)
        {
            Bounds local = default;
            bool has = false;

            foreach (MeshFilter mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf == null || mf.sharedMesh == null) continue;
                EncapsulateLocalBounds(ref local, ref has,
                    root.worldToLocalMatrix * mf.transform.localToWorldMatrix, mf.sharedMesh.bounds);
            }

            foreach (SkinnedMeshRenderer smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr == null || smr.sharedMesh == null) continue;
                EncapsulateLocalBounds(ref local, ref has,
                    root.worldToLocalMatrix * smr.transform.localToWorldMatrix, smr.sharedMesh.bounds);
            }

            Vector3 upLocal = root.InverseTransformDirection(Vector3.up);
            int upIndex = 0;
            for (int i = 1; i < 3; i++)
                if (Mathf.Abs(upLocal[i]) > Mathf.Abs(upLocal[upIndex]))
                    upIndex = i;

            if (!has)
                return upIndex == 2 ? Vector3.right : Vector3.forward;

            int bestIndex = upIndex == 0 ? 1 : 0;
            float bestExtent = -1f;
            for (int i = 0; i < 3; i++)
            {
                if (i == upIndex) continue;
                if (local.extents[i] > bestExtent)
                {
                    bestExtent = local.extents[i];
                    bestIndex = i;
                }
            }

            Vector3 axis = Vector3.zero;
            axis[bestIndex] = 1f;
            return axis.sqrMagnitude > 0.25f ? axis.normalized : Vector3.forward;
        }

        private static void EncapsulateLocalBounds(ref Bounds bounds, ref bool has, Matrix4x4 toRoot, Bounds meshBounds)
        {
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = meshBounds.center + Vector3.Scale(meshBounds.extents,
                    new Vector3((i & 1) == 0 ? -1f : 1f,
                                (i & 2) == 0 ? -1f : 1f,
                                (i & 4) == 0 ? -1f : 1f));
                Vector3 p = toRoot.MultiplyPoint3x4(corner);
                if (!has)
                {
                    bounds = new Bounds(p, Vector3.zero);
                    has = true;
                }
                else
                {
                    bounds.Encapsulate(p);
                }
            }
        }
    }
}
