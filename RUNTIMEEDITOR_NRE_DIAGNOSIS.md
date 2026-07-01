# RuntimeEditor NullReferenceException — Diagnosis

```
NullReferenceException: Object reference not set to an instance of an object
RuntimeEditor.HandleMouseInput () (at Assets/RuntimeEditor/RuntimeEditor.cs:306)
RuntimeEditor.Update () (at Assets/RuntimeEditor/RuntimeEditor.cs:76)
```

Happens on the **other** computer after entering World Building from Session Review.
Does **not** happen on this computer. Same repo, same committed scenes.

> **Status:** Re-verified on the working machine (2026-06-24). The original diagnosis
> got the *throwing line* right but the *differentiator* wrong. Corrected below, and a
> universal code fix has been applied (see "Fix" section). The earlier theory — "the
> working scene doesn't have the zombie component" — is **false**: both machines have
> identical scenes.

## The exact line that throws

[Assets/RuntimeEditor/RuntimeEditor.cs:306](Assets/RuntimeEditor/RuntimeEditor.cs#L306)
```csharp
Ray ray = mainCamera.ScreenPointToRay(Input.mousePosition);
```

The only nullable thing on this line is `mainCamera` (a private field on the `RuntimeEditor`
instance). `Input.mousePosition` is a struct and cannot NRE. So: **`mainCamera == null`.**

## The component that throws — a stray `RuntimeEditor` on the manager GameObject

Each of the five sidewalk scenes has a GameObject named **`RuntimeEdit`** carrying BOTH:

- `RuntimeEditorManager` (script GUID `99aa9ff51d9985c48bf299277efe123d`)
- `RuntimeEditor` (script GUID `552ee8c61b7287140aea88df4da63c1a`) ← the per-object gizmo handler, which shouldn't live here

The `RuntimeEditor` MonoBehaviour is meant to be added **at runtime** by the manager onto a
*selected prop* via [`RuntimeEditorManager.SelectObject`](Assets/RuntimeEditor/RuntimeEditorManager.cs#L751),
which immediately calls `SetRaycastCamera(mainCamera)` on the new instance. The copy sitting on
the manager's own GameObject is never selected, so it never receives a camera that way. Its only
chance to get one is its own `Start()` fallback to `Camera.main`.

> ⚠️ **This is present on BOTH computers — it is NOT the difference.**
> Verified on the working machine: [Assets/Scenes/sidewalkCrossroad.unity:7085-7179](Assets/Scenes/sidewalkCrossroad.unity#L7085-L7179)
> — GameObject `&320971306` `m_Name: RuntimeEdit`, `m_IsActive: 0`, with the stray
> `RuntimeEditor` at `&320971309`, `m_Enabled: 1`. The scene `.unity` files are identical
> across the two machines (`git log` on the scene shows the same commit; no local scene edits).
> So the stray component is necessary for the crash but is **not** what differs between the
> machines.

Confirmed present in all five (same layout):
- [Assets/Scenes/sidewalkCrossroad.unity](Assets/Scenes/sidewalkCrossroad.unity)
- [Assets/Scenes/sidewalkOutofStore.unity](Assets/Scenes/sidewalkOutofStore.unity)
- [Assets/Scenes/sidewalkCornerInteraction.unity](Assets/Scenes/sidewalkCornerInteraction.unity)
- [Assets/Scenes/sidewalkNarrowroad.unity](Assets/Scenes/sidewalkNarrowroad.unity)
- [Assets/Scenes/sidewalkNarrowroadsoft.unity](Assets/Scenes/sidewalkNarrowroadsoft.unity)

## The real condition for the crash: `Camera.main == null` when the stray `Start()` runs

The stray `RuntimeEditor` gets a camera exactly once, in [`Start()`](Assets/RuntimeEditor/RuntimeEditor.cs#L52-L59):
```csharp
void Start()
{
    if (mainCamera == null) mainCamera = Camera.main;   // runs ONCE, on first activation
    ...
}
```
`Start()` runs **once per component lifetime**, the first time the object becomes active. After
that `mainCamera` keeps whatever value it got — even if that camera is later disabled (a *disabled*
Camera reference is still non-null, so `ScreenPointToRay` still works).

So the crash requires **all** of:
1. The `RuntimeEdit` GameObject becomes active **for the first time** during World Building, AND
2. At the moment its `Start()` runs, `Camera.main` returns `null`.

`Camera.main` returns the first **enabled**, `MainCamera`-tagged camera. In the committed scene the
**only** `MainCamera`-tagged camera is `FirstPersonCam`
([Assets/Scenes/sidewalkCrossroad.unity:67872-67873](Assets/Scenes/sidewalkCrossroad.unity#L67872)).
`topViewCamera` (the world-building camera, [`Environment.topViewCamera`](Assets/Scripts/SEAN/Environment/Environment.cs#L24))
is **not** tagged `MainCamera` (it gets a `TopViewCamera` tag). So once `FirstPersonCam` is disabled
or destroyed and nothing else is `MainCamera`-tagged-and-enabled, `Camera.main` is `null`.

### The trigger chain in World Building

1. `RuntimeEdit` sits inactive in the scene (`m_IsActive: 0`), so the stray component is dormant and
   its `Start()` has never run.
2. Entering World Building, [`ActivateWorldBuildingView`](Assets/Scripts/SessionReview/SessionReviewManager.cs#L1486) does:
   - `worldBuildingCamera = topViewCamera` (line 1496)
   - [`PrepareTopDownWorldBuildingCamera`](Assets/Scripts/SessionReview/SessionReviewManager.cs#L1656-L1665):
     `worldBuildingPreviousMainCamera = Camera.main; ... worldBuildingPreviousMainCamera.enabled = false;`
     → disables the current `MainCamera` (e.g. `FirstPersonCam`).
   - [`EnsureRuntimeEditorReady`](Assets/Scripts/SessionReview/SessionReviewManager.cs#L1816-L1826):
     `runtimeEditorManager.gameObject.SetActive(true)` → **first activation of `RuntimeEdit`**, which
     schedules the stray `RuntimeEditor.Start()`.
   - `worldBuildingCamera.enabled = true` (line 1520) — but this camera is **not** `MainCamera`-tagged.
   - `SetEditorCamera(worldBuildingCamera, …)` (line 1523) only forwards the camera to `currentEditor`,
     which is `null` (nothing selected). The stray instance is **not** reached by this path.
3. Next frame the stray's `Start()` runs: `Camera.main` is `null` (the old main camera is disabled, the
   world-building camera isn't `MainCamera`-tagged) → `mainCamera = null`.
4. Every subsequent frame: `Update()` → `HandleMouseInput()` → `mainCamera.ScreenPointToRay(...)` → **NRE**.

## Why the two machines diverge (this is the actual "difference")

> Updated after operator feedback: **the same avatars were spawned on both machines**, so this is
> *not* an asset/Rocketbox-submodule difference. The divergence is in **runtime camera bookkeeping**,
> not in what assets exist.

There is not just one `MainCamera` in play. Besides the scene's `FirstPersonCam`, several **spawnable
prefabs carry their own `MainCamera`-tagged cameras**:

- [Assets/Resources/Prefabs/Cameras.prefab](Assets/Resources/Prefabs/Cameras.prefab) — **three** of them: `DownviewCamera`, `FollowCamera`, `FlyCamera`.
- [Assets/Resources/Prefabs/RocketboxSFRandom.prefab](Assets/Resources/Prefabs/RocketboxSFRandom.prefab) — a `Camera` tagged `MainCamera`.
- [Assets/Resources/SEAN/Sensors/ThirdPersonCameraParent.prefab](Assets/Resources/SEAN/Sensors/ThirdPersonCameraParent.prefab) — `MainCamera`-tagged.

`Camera.main` returns the **first enabled** `MainCamera`-tagged camera. With this many candidates,
whether it's `null` at one specific frame depends entirely on which cameras happen to be **enabled**
then — and Session Review enables/disables/destroys cameras aggressively along several conditional paths:

- [`PrepareTopDownWorldBuildingCamera`](Assets/Scripts/SessionReview/SessionReviewManager.cs#L1656) disables the current `Camera.main` (just one).
- [`ActivatePwdCameraAsMain`](Assets/Scripts/SessionReview/SessionReviewManager.cs#L2217-L2237) disables **all** cameras except the PWD camera.
- [`DestroyLegacyStandaloneMainCamera`](Assets/Scripts/SessionReview/SessionReviewManager.cs#L2172-L2188) **destroys** `Camera.main` if it isn't a "managed" gameplay camera.
- [`RestoreRobotGameplayCameras`](Assets/Scripts/SessionReview/SessionReviewManager.cs#L2156-L2169) re-enables robot cameras.

So the crash is a **latent ordering/state bug**, not an asset bug. It fires only when the particular
session path leaves **zero** enabled `MainCamera`-tagged cameras at the exact frame the stray
`RuntimeEditor.Start()` runs. The two machines take a *slightly* different camera path even with the same
avatars/scenes — driven by things like:

- **Which view/teardown ran in that session** (robot vs PWD, first-person vs follow/fly cam, whether
  `ActivatePwdCameraAsMain`/`DestroyLegacyStandaloneMainCamera` fired) — sensitive to UI clicks, focus,
  and timing.
- **Frame ordering** — `Start()` runs once on first activation; if `RuntimeEdit` happened to activate
  earlier in the session (e.g. the editor was toggled once) on the working machine, the stray cached a
  live camera while one was enabled and never sees the later `null`.
- **Unity version / Script Execution Order** differences between the two installs, which shift when
  `Start()` runs relative to the camera enable/disable/destroy calls.

Bottom line: it's a race against camera bookkeeping. We don't need to pin the exact trigger — the code
guard below makes the stray immune to it. If you *do* want the exact trigger, capture the camera state on
the broken machine (below) and diff it against this machine.

## How to capture the exact difference on the broken machine (~1 minute)

1. Temporarily add this in [`ActivateWorldBuildingView`](Assets/Scripts/SessionReview/SessionReviewManager.cs#L1513)
   right after `EnsureRuntimeEditorReady()` (do the same on this machine and diff the two logs):
   ```csharp
   foreach (var c in FindObjectsOfType<Camera>(true))
       Debug.Log($"[WB] cam={c.name} tag={c.tag} enabled={c.enabled} active={c.gameObject.activeInHierarchy}");
   Debug.Log($"[WB] Camera.main = {(Camera.main ? Camera.main.name : "NULL")}");
   ```
   On the broken machine `Camera.main` prints `NULL` (and no `MainCamera`-tagged camera is `enabled=True`);
   on this machine at least one is still enabled. That single line is the whole difference.
2. In the Hierarchy (Play mode, World Building entered), select `RuntimeEdit` and confirm the stray
   `Runtime Editor` component is the thing throwing.

## Fix

### Fix 1 — Code guard (applied; the universal one-commit fix)

Applied to [`RuntimeEditor.HandleMouseInput`](Assets/RuntimeEditor/RuntimeEditor.cs#L297) — it
re-acquires `Camera.main` if available and otherwise bails for the frame instead of NRE'ing:

```csharp
void HandleMouseInput()
{
    // No raycast camera (e.g. the main camera was disabled/destroyed during a camera
    // hand-off such as Session Review -> World Building). Try to re-acquire one, and if
    // there still isn't one, bail this frame instead of NRE'ing on mainCamera.
    if (mainCamera == null)
    {
        mainCamera = Camera.main;
        if (mainCamera == null) return;
    }
    ...
}
```

This is the recommended fix to ship because **one commit protects both machines** regardless of the
runtime camera state, and it self-heals once any `MainCamera` exists. `HandleMouseInput` is the only
reachable path to every `mainCamera` use (line 484 needs an active drag, which needs the line-306 click
first), so this single guard is complete. **Commit + push so the broken machine gets it on pull.**

### Fix 2 — Remove the stray `RuntimeEditor` component (architectural cleanup, optional but correct)

In each of the five scenes, select `RuntimeEdit` and **Remove Component → Runtime Editor** (keep
`Runtime Editor Manager`), then save. This removes the dormant component that has no business being there.
It is the "right" structural fix, but note it must be done in all five scenes and committed; Fix 1 already
stops the crash, so do this as hygiene, not as the urgent fix.

## Input methods & the "can't rotate into 3D" symptom

Operator report: in World Building you couldn't rotate the view into 3D — though it may simply be that
the per-frame NRE at entry made the whole mode unusable before you reached the rotation phase. Both are
worth understanding because this project runs **two input backends at once**.

- `ProjectSettings/ProjectSettings.asset` → `activeInputHandler: 2` (**Both**), committed and clean. So
  **both** `ENABLE_INPUT_SYSTEM` (new) and `ENABLE_LEGACY_INPUT_MANAGER` (legacy) are defined.
- The stray `RuntimeEditor` (and the gizmo R/T mode toggle) use **legacy** `Input` (`Input.mousePosition`,
  `Input.GetKeyDown`). The fact that the crash is a `NullReferenceException` and **not** an
  `InvalidOperationException` ("you are trying to read Input using UnityEngine.Input but switched to Input
  System package") **proves legacy input is enabled on the broken machine too** — so `activeInputHandler`
  is the same on both machines and is *not* the difference.
- The World-Building camera ([`SimpleCameraController`](Assets/RuntimeEditor/SimpleCameraController.cs)) is
  `#if ENABLE_INPUT_SYSTEM`-gated, so under "Both" it takes the **new Input System** path.

### How "rotate into 3D" is supposed to work

It is **hold Right Mouse Button + move mouse**:
[`IsCameraRotationAllowed()`](Assets/RuntimeEditor/SimpleCameraController.cs#L464-L473) →
`Mouse.current.rightButton.isPressed` → [`EnsurePerspectiveForFreeLook()`](Assets/RuntimeEditor/SimpleCameraController.cs#L379-L402)
flips the camera from top-down orthographic to perspective (the "3D" view). It is **not** a key press and
not the gizmo's R key — if you didn't hold RMB, nothing rotates by design.

### Ways rotation can independently fail (test these only after the NRE fix)

1. **Most likely: it never got that far.** The stray NRE throws every frame at entry. Unity catches it
   per-`Update`, so `SimpleCameraController` technically still runs — but the spam + the broken editor
   state make the mode effectively unusable. After Fix 1, re-test RMB-drag rotation first.
2. **`Mouse.current == null`** under the new Input System → `IsCameraRotationAllowed()` is always false →
   RMB-drag does nothing, silently. Happens if the new Input System didn't register a mouse device. Check
   for it; if so, this is a real input bug independent of the camera NRE.
3. **`SimpleCameraController.Start()` not yet run** when first rotating: it's `AddComponent`-ed at
   [SessionReviewManager.cs:1841](Assets/Scripts/SessionReview/SessionReviewManager.cs#L1841) and `Start()`
   builds the `InputAction`s ([lines 118-150](Assets/RuntimeEditor/SimpleCameraController.cs#L118-L150)). If
   look-input is read before `Start()`, `lookAction` is null → a *different* NRE in `GetInputLookRotation`.
4. **Stale `Library` / mismatched compile defines** between the two machines: if one machine compiled
   `SimpleCameraController` with the legacy branch and the other with the new branch (e.g. an out-of-date
   `Library` that never picked up the input-handler change), rotation input behaves differently. Fix by
   deleting `Library/` and reimporting so both compile with `ENABLE_INPUT_SYSTEM`.

Net: input backend is *not* the cause of the NRE, and is the same on both machines. The "can't rotate"
symptom is most likely a downstream effect of the NRE; verify after Fix 1, and if it persists, suspect
`Mouse.current` / new-Input-System device registration (#2) or a stale `Library` (#4).

## Things this is NOT

- **Not "the working scene lacks the stray component."** ❌ The original doc's headline cause. Disproven:
  the stray component is present and identical on the working machine. The scenes match across machines.
- **Not a new/legacy Input System mismatch.** `ProjectSettings.asset` has `activeInputHandler: 2` (Both).
- **Not a missing "Gizmo" layer.** Layer 7 ("Gizmo") is present in `ProjectSettings/TagManager.asset`.
- **Not branch drift.** Same commits, same scene files on both machines.
- **Not `topViewCamera` returning null.** That path refuses to enter World Building with a different error.

## Local state worth cleaning up

- **Other machine:** `Assets/ExternalAssets/Microsoft-Rocketbox` submodule is dirty (deleted
  `Sports_Female_02.fbx`, modified avatar `.meta`s, untracked `Sports_Female_02.prefab` + materials).
  This is the prime suspect for the avatar/camera spawn divergence. Either reset it
  (`git -C Assets/ExternalAssets/Microsoft-Rocketbox checkout .` then `git submodule update --init --recursive`)
  or commit/push it so both machines match.
- **This machine:** working tree has `Assets/Rerun` (submodule) and `Assets/Scripts/VLMscripts/UIManager.cs`
  modified, plus the Fix 1 edit to `Assets/RuntimeEditor/RuntimeEditor.cs`. `Microsoft-Rocketbox` is clean here.
