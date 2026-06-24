# RuntimeEditor NullReferenceException — Diagnosis

```
NullReferenceException: Object reference not set to an instance of an object
RuntimeEditor.HandleMouseInput () (at Assets/RuntimeEditor/RuntimeEditor.cs:306)
RuntimeEditor.Update () (at Assets/RuntimeEditor/RuntimeEditor.cs:76)
```

This happens after entering World Building from Session Review. The other computer works because some scene/local state there avoids the trigger — the code is fine, the trap is in the scene.

## The exact line that throws

[Assets/RuntimeEditor/RuntimeEditor.cs:306](Assets/RuntimeEditor/RuntimeEditor.cs#L306)
```csharp
Ray ray = mainCamera.ScreenPointToRay(Input.mousePosition);
```

The only nullable thing on this line is `mainCamera` (the `RuntimeEditor` instance's private field). `Input.mousePosition` is a struct and cannot NRE.

## Root cause — a "zombie" RuntimeEditor on the manager GameObject

The five sidewalk scenes each contain a GameObject literally named **`RuntimeEdit`** that has BOTH components attached:

- `RuntimeEditorManager` (script GUID `99aa9ff51d9985c48bf299277efe123d`)
- `RuntimeEditor` (script GUID `552ee8c61b7287140aea88df4da63c1a`) ← **this one should not be here**

Confirmed in:
- [Assets/Scenes/sidewalkCrossroad.unity:7085-7179](Assets/Scenes/sidewalkCrossroad.unity#L7085-L7179) — `m_Name: RuntimeEdit`, `m_IsActive: 0`, RuntimeEditor at `&320971309` with `m_Enabled: 1`
- [Assets/Scenes/sidewalkOutofStore.unity](Assets/Scenes/sidewalkOutofStore.unity)
- [Assets/Scenes/sidewalkCornerInteraction.unity](Assets/Scenes/sidewalkCornerInteraction.unity)
- [Assets/Scenes/sidewalkNarrowroad.unity](Assets/Scenes/sidewalkNarrowroad.unity)
- [Assets/Scenes/sidewalkNarrowroadsoft.unity](Assets/Scenes/sidewalkNarrowroadsoft.unity)

The `RuntimeEditor` component is the per-object gizmo handler. The design intent is that the **manager** adds it dynamically to a *selected* prop via [`RuntimeEditorManager.SelectObject`](Assets/RuntimeEditor/RuntimeEditorManager.cs#L751) and immediately calls `SetRaycastCamera(mainCamera)` on the new instance. The stray copy sitting on the manager's own GameObject is never selected, never receives a camera through that code path.

## The trigger chain (why World Building lights it up)

1. The `RuntimeEdit` GameObject sits in the scene **inactive** (`m_IsActive: 0`), so the zombie component is dormant.
2. User enters World Building. [`SessionReviewManager.EnsureRuntimeEditorReady`](Assets/Scripts/SessionReview/SessionReviewManager.cs#L1825-L1826) force-activates it:
   ```csharp
   if (!runtimeEditorManager.gameObject.activeInHierarchy)
       runtimeEditorManager.gameObject.SetActive(true);
   ```
3. Activating the GameObject runs `Awake`/`OnEnable`/`Start` on **both** scripts on that object — including the zombie `RuntimeEditor`.
4. The zombie's [`Start()`](Assets/RuntimeEditor/RuntimeEditor.cs#L52-L59) only falls back to `Camera.main`:
   ```csharp
   if (mainCamera == null) mainCamera = Camera.main;
   ```
   The inspector reference is empty (it's a per-object gizmo, no Camera field exposed), so this fallback is its only chance.
5. **At the same moment**, [`PrepareTopDownWorldBuildingCamera`](Assets/Scripts/SessionReview/SessionReviewManager.cs#L1661-L1665) disables the previous main camera so the world-building camera owns the screen:
   ```csharp
   if (worldBuildingPreviousMainCamera != null &&
       worldBuildingPreviousMainCamera != cameraToUse)
       worldBuildingPreviousMainCamera.enabled = false;
   ```
   `Camera.main` returns only **enabled** MainCamera-tagged cameras, so it now returns `null`.
6. The manager calls `SetEditorCamera(worldBuildingCamera, ...)` — but inside [`SetEditorCamera`](Assets/RuntimeEditor/RuntimeEditorManager.cs#L338-L358) it only forwards the camera to `currentEditor`, which is `null` because nothing is selected yet. The zombie on the manager GameObject is not in `allEditors` and is never touched.
7. Next frame: zombie's `Update()` → `HandleMouseInput()` → `mainCamera.ScreenPointToRay(...)` → **NRE every frame**.

## Why it works on the other computer

Same `.cs` files, but the *scene asset* or *local state* differs. Any of these would mask the bug:

- **The other scene file doesn't have the zombie `RuntimeEditor` component.** Most likely: it was deleted on the other PC and the scene save wasn't committed. Run `git status Assets/Scenes/` over there — if `sidewalkCrossroad.unity` is dirty, that's the proof. Otherwise check git log on the scene.
- **A different MainCamera stays enabled during world building** on the other PC (e.g. an extra editor camera, or the FirstPersonCam isn't the tagged MainCamera there) — so `Camera.main` fallback succeeds, the zombie picks up *a* camera, and nothing throws even though the zombie is still wrong.
- **Local prefab/avatar state differences** (you have uncommitted modifications in `Assets/ExternalAssets/Microsoft-Rocketbox`: deleted `Sports_Female_02.fbx`, modified avatar `.meta` files, untracked `Sports_Female_02.prefab`). If the avatar that carries `FirstPersonCam` fails to spawn or is replaced on this PC but not the other, the timing of who-is-Camera.main changes.
- **TagManager/Layer drift** — Layer 7 ("Gizmo") is the 8th entry in this PC's [ProjectSettings/TagManager.asset](ProjectSettings/TagManager.asset). If that file were dirty on either side this would also bite, but a quick check shows the layer exists, so this is not the cause here — listed only for completeness.

## How to confirm the diagnosis (do this first, ~30 seconds)

1. Open `sidewalkCrossroad.unity` (or whichever scene reproduces the crash).
2. In the Hierarchy, find the GameObject named **`RuntimeEdit`** (it's inactive by default — toggle "Show Inactive" / it's a root object).
3. Look at the Inspector. You will see **two** components: `Runtime Editor Manager` AND `Runtime Editor`. The second one is the bug.
4. Enter Play mode, kick off World Building, watch the console. The NRE appears the instant `RuntimeEdit` flips active.

## Fix (pick one)

### Fix A — Remove the zombie component (recommended)

In each affected scene, select the `RuntimeEdit` GameObject and **remove the `Runtime Editor` component** from the Inspector (right-click the component header → *Remove Component*). Keep the `Runtime Editor Manager`. Save the scene.

This is the right fix because the `RuntimeEditor` MonoBehaviour is meant to live on the *selected prop*, attached at runtime by the manager. Having one on the manager itself is just a leftover.

Affected scenes (all five):
- `Assets/Scenes/sidewalkCrossroad.unity`
- `Assets/Scenes/sidewalkOutofStore.unity`
- `Assets/Scenes/sidewalkCornerInteraction.unity`
- `Assets/Scenes/sidewalkNarrowroad.unity`
- `Assets/Scenes/sidewalkNarrowroadsoft.unity`

After fixing, commit the scene `.unity` files so both computers stay in sync.

### Fix B — Make the code defensive (belt and braces)

If you can't edit scenes right now, harden [`RuntimeEditor.HandleMouseInput`](Assets/RuntimeEditor/RuntimeEditor.cs#L297) so it bails when there's no camera:

```csharp
void HandleMouseInput()
{
    if (mainCamera == null) return;     // <-- add this
    if (IsClickOnUI() && Input.GetMouseButtonDown(0)) { ... return; }
    Ray ray = mainCamera.ScreenPointToRay(Input.mousePosition);
    ...
}
```

And in [`UpdateGizmoPositions`](Assets/RuntimeEditor/RuntimeEditor.cs#L204) if you ever extend it to use the camera.

Belt-and-braces, but treats the symptom rather than the cause. Fix A is the real one.

## Things this is NOT

- **Not a new Input System / legacy Input mismatch.** `ProjectSettings/ProjectSettings.asset` has `activeInputHandler: 2` (Both enabled). Legacy `Input.mousePosition` works.
- **Not the "Gizmo" layer missing.** Layer 7 is present in `ProjectSettings/TagManager.asset`.
- **Not branch drift.** `git log Gracie2..origin/main` and `git log origin/main..Gracie2` both return empty — Gracie2 is in sync with `main`. The two machines run identical code; the difference is local scene/uncommitted state.
- **Not `SEAN.Environment.topViewCamera` returning null.** That codepath errors with `"TopViewCamera not found under …"` and refuses to enter World Building. You got past that.

## Local state to clean up regardless

`git submodule status` shows uncommitted modifications in `Assets/ExternalAssets/Microsoft-Rocketbox`: deleted `Sports_Female_02.fbx`, modified avatar `.meta` files, untracked `Sports_Female_02.prefab` and a few materials. These are unrelated to the NRE itself but they are a likely reason the two computers diverge in avatar/camera spawning, which is in turn the most likely reason this same scene happens to find `Camera.main` on the other PC but not here.

Either:
- Reset the submodule: `git -C Assets/ExternalAssets/Microsoft-Rocketbox checkout .` (destroys local edits — confirm first that you don't need them), then `git submodule update --init --recursive`, **or**
- Commit/push the submodule changes so the other PC matches.
