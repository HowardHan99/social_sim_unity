using UnityEngine;

namespace IVI
{
    public static class AvatarAnimatorUtility
    {
        /// <summary>
        /// Prefer the humanoid character animator (e.g. Ch22) over other child animators (e.g. the dog).
        /// </summary>
        public static Animator GetLocomotionAnimator(GameObject root)
        {
            if (root == null)
                return null;

            Animator fallback = null;
            foreach (var animator in root.GetComponentsInChildren<Animator>(true))
            {
                if (animator.avatar != null && animator.avatar.isHuman)
                    return animator;

                if (fallback == null && animator.runtimeAnimatorController != null)
                    fallback = animator;
            }

            return fallback ?? root.GetComponentInChildren<Animator>(true);
        }
    }
}
