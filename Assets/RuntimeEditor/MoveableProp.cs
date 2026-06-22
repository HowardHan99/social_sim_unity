using UnityEngine;

/// <summary>
/// Marker that makes a prop draggable in the runtime editor through the SAME pipeline as a mailbox:
/// <see cref="RuntimeEditorManager.PrepareEditableObjects"/> discovers every object carrying this
/// component when editor mode starts and registers it into the editable list — exactly how it discovers
/// objects via <c>TrackedObstacle</c>, but with no ROS coupling.
///
/// Attach this (in a prefab or in the scene) to anything you want to be moveable. A <see cref="Collider"/>
/// is required so the object can be clicked/raycast-selected, matching how the mailbox carries a BoxCollider.
/// </summary>
[RequireComponent(typeof(Collider))]
[DisallowMultipleComponent]
public class MoveableProp : MonoBehaviour
{
}
