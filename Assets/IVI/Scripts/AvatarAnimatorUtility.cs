using UnityEngine;

namespace IVI
{
    public static class AvatarAnimatorUtility
    {
        /// <summary>
        /// Prefer the humanoid character animator that drives visible skinned meshes.
        /// On nested prefabs (e.g. female wheelchair), a root-level animator may use the wrong
        /// avatar while the mesh and matching animator live on a deeper child.
        /// </summary>
        public static Animator GetLocomotionAnimator(GameObject root)
        {
            if (root == null)
                return null;

            Animator bestHuman = null;
            int bestDepth = -1;
            Animator fallback = null;

            foreach (var animator in root.GetComponentsInChildren<Animator>(true))
            {
                if (animator.avatar != null && animator.avatar.isHuman)
                {
                    if (!HasSkinnedMeshInSubtree(animator.transform)
                        && !HasSkinnedMeshAmongSiblings(animator.transform))
                        continue;

                    int depth = GetDepthFromRoot(animator.transform, root.transform);
                    if (depth > bestDepth)
                    {
                        bestDepth = depth;
                        bestHuman = animator;
                    }
                }

                if (fallback == null && animator.runtimeAnimatorController != null)
                    fallback = animator;
            }

            if (bestHuman != null)
                return bestHuman;

            foreach (var animator in root.GetComponentsInChildren<Animator>(true))
            {
                if (animator.avatar != null && animator.avatar.isHuman)
                    return animator;
            }

            return fallback ?? root.GetComponentInChildren<Animator>(true);
        }

        public static bool UsesCyclistPedalController(Animator animator)
        {
            return animator != null
                && animator.runtimeAnimatorController != null
                && animator.runtimeAnimatorController.name == "CyclistController";
        }

        public static void ApplyCyclistPedalSpeed(Animator animator, bool shouldIdle, float speed, float referenceSpeed)
        {
            if (!UsesCyclistPedalController(animator))
                return;

            animator.speed = shouldIdle
                ? 0f
                : Mathf.Max(speed / Mathf.Max(referenceSpeed, 0.01f), 0.5f);
        }

        private static bool HasSkinnedMeshInSubtree(Transform t)
        {
            return t.GetComponentInChildren<SkinnedMeshRenderer>(true) != null;
        }

        /// <summary>
        /// Sports_Female_02 keeps its mesh as a sibling of the skeleton root, not a child.
        /// </summary>
        private static bool HasSkinnedMeshAmongSiblings(Transform t)
        {
            if (t.parent == null)
                return false;

            foreach (Transform sibling in t.parent)
            {
                if (sibling.GetComponentInChildren<SkinnedMeshRenderer>(true) != null)
                    return true;
            }

            return false;
        }

        private static int GetDepthFromRoot(Transform t, Transform root)
        {
            int depth = 0;
            while (t != null && t != root)
            {
                depth++;
                t = t.parent;
            }
            return t == root ? depth : -1;
        }
    }
}
