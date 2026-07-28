using System.Collections;
using UnityEngine;

namespace IVI
{
    /// <summary>
    /// Runtime helper for the Phone_User character. The texting pose itself lives in the
    /// ANIMATOR — PhoneUserPoseController plays a HumanoidIdle base layer plus a 1-frame
    /// arms-only muscle-curve pose layer (built by the PhoneUserPoseBaker editor script).
    ///
    /// Runtime pose scripting was abandoned twice here: direct bone-euler offsets are
    /// rig-dependent (threw the Bip01 arms out sideways), and HumanPoseHandler.SetHumanPose
    /// fights the bound Animator and mangles the humanoid-mapped face bones (jaw/eyes).
    /// Baked muscle curves on a masked layer have neither problem.
    ///
    /// This component only makes the animator reliable as a World-Building prop:
    /// - World Building spawns props while Time.timeScale is 0; the Animator would then
    ///   never evaluate and the model stands in fbx bind pose (T-pose) until unpause.
    ///   Pumping the graph once with Animator.Update(0) shows frame 0 immediately.
    /// - Skinned bounds on these re-exported rigs are unreliable, so renderer-bounds
    ///   culling can freeze the pose depending on the camera; force AlwaysAnimate.
    /// </summary>
    public class PhoneUserArmPose : MonoBehaviour
    {
        [Header("Debug (read-only)")]
        public string debugAnimatorTarget;

        IEnumerator Start()
        {
            for (int i = 0; i < 90; i++)
            {
                Animator animator = AvatarAnimatorUtility.GetLocomotionAnimator(gameObject);
                if (animator != null && animator.avatar != null && animator.avatar.isHuman)
                {
                    debugAnimatorTarget = animator.gameObject.name;
                    animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                    // Non-zero step: Update(0) is skipped without evaluating (verified via
                    // the runtime diag — the skeleton stayed in bind pose), so a zero pump
                    // left paused-spawned props T-posed anyway.
                    Transform bindProbe = animator.GetBoneTransform(HumanBodyBones.RightHand);
                    Vector3 beforePump = bindProbe != null ? bindProbe.position : Vector3.zero;
                    animator.Update(0.001f);
                    // Frame 0 of the idle differs hugely from the bind T-pose, so an
                    // unmoved hand after a forced evaluation means the avatar failed to
                    // bind to this hierarchy (e.g. the model root was renamed away from
                    // the name recorded in the Avatar's skeleton) — the exact silent
                    // failure that kept Phone_User T-posed through three controllers.
                    if (bindProbe != null && (bindProbe.position - beforePump).sqrMagnitude < 1e-10f)
                        Debug.LogWarning("[PhoneUserArmPose] Animator evaluated but the skeleton did not move — "
                            + "avatar/hierarchy binding is broken on '" + animator.gameObject.name
                            + "' (root renamed away from the Avatar's recorded skeleton name?).", this);
                    yield break;
                }

                yield return null;
            }

            Debug.LogWarning("[PhoneUserArmPose] No humanoid Animator found under '" + name + "'.", this);
        }
    }
}
