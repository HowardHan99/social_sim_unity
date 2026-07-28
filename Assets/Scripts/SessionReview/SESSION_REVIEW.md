# Session Review System

A self-contained post-trial review system for the social simulation. Records agent trajectories, control modes, and metrics during live trials, then provides a rewind/review mode with trajectory visualization, perspective switching, and a progress bar.

## Quick Start

1. **Add to scene**: Create an empty GameObject, add `SessionReviewManager` component. It auto-creates all other components on the same object.
2. **Run a trial**: The system auto-detects trial start/end via `Tasks.Base.onNewTask`. All agents (robot, PWD player, pedestrians) are tracked automatically.
3. **Review**: After a trial ends, press **T** to enter review mode. Press **T** or **Esc** to exit.

## Controls

| Key | Action |
|-----|--------|
| **Tab** | Switch the driven agent's view between first and third person (gameplay, all agents) |
| **T** | Enter/exit review mode (after trial ends) |
| **Space** | Play/pause rewind |
| **Left/Right Arrow** | Step backward/forward (0.1s) |
| **Home / End** | Jump to trial start/end |
| **+/-** | Speed up/down (0.25x, 0.5x, 1x, 2x, 4x) |
| **F1** | Robot first-person view |
| **F2** | PWD player first-person view |
| **F3** | Pedestrian over-shoulder view |
| **F4** | Top-down orthographic view |
| **F5** | Free camera view |
| **G** | Toggle ghost trails |
| **V** | Show/hide the robot's intent overlays (ROS control trajectory + robot goal marker) |
| **[ / ]** | Previous/next trial |
| **Ctrl +/-** | Zoom all overlay UI (fonts + layout) up/down — for large displays |
| **Ctrl 0** | Reset UI zoom to 100% |

### UI Scale (large displays)

All IMGUI overlays share a global user zoom (`ReviewUiScale`, persisted in PlayerPrefs).
Besides the hotkeys above, click the **"Aa NN%"** badge in the top-left corner to open a
slider with 100/125/150/200% presets. The zoom stacks with the existing responsive
behavior (panels still clamp to the screen and resolution-based auto-scaling still applies),
so layouts stay usable at any zoom. New overlays opt in by calling `ReviewUiScale.Apply()`
first thing in `OnGUI` and using `ReviewUiScale.Width/Height` instead of `Screen.width/height`;
manual hit-testing from `Input.mousePosition`/touches must convert via `ReviewUiScale.ScreenToGui()`.

### Robot Intent Overlays ([V])

`RosOverlayVisibility` is one switch over the two overlays that give away where the robot is
headed: the **control trajectory** (the ROS nav-plan line `PlanVisualizer` draws from the global
plan) and the **robot goal marker** (its flag cube/arrow, plus the "ROBOT GOAL" label and orange
highlight `RobotGoalObjectBinding` adds). Both are **hidden at every trial start** so a
participant never sees the robot's preset path, **[V]** flips them mid-run, and **entering review
turns them back on** — review is where they are wanted. The button strip sits in the bottom-left,
lifted above the replay progress bar; the Control Traj button only appears when the scene actually
has a `PlanVisualizer` (i.e. ROS is in the loop). It hides itself for the whole draw-trajectory
session (`IsDrawTrajectoryModeActive`), which is also what keeps it clear of the DRAW/ERASE gates
that occupy the same corner.

Two things are never hidden. The **player** goal is not covered at all — the human participant
needs it to know where to walk. And the **robot** goal is force-shown whenever
`VelocityController.ManualControlActive` is on: with a human driving the robot the goal is that
driver's own target, not a leaked answer, so the button greys out and reads "Robot Goal (driving)".
The control trajectory needs no such exception — `VelocityController` already suppresses the plan
line under `SuppressionReason.ManualControl`.

Discovery covers three roots, because the goal is drawn from more than one place: the runtime
`robotGoal` marker (everything under it is goal plumbing), a `CustomStartGoal.RobotGoalLocation`
scene node's own preview flags (matched by the `TargetFlagCube`/`TargetFlagArrow` names
`Base.SetTargetFlags` uses, since real scenery may be authored under that node), and
`RobotGoalObjectBinding.GoalUiRenderers`. A bound goal object is skipped everywhere — it is a door
or a bench, not goal UI.

Nothing is disconnected, only hidden: `PlanVisualizer` keeps computing the plan while
suppressed (so `LiveTrajectoryRecorder` still records it and the trial-start readiness check still
sees it), and only the goal marker's *renderers* are touched, never its transform — that is what
feeds ROS goal publishing, completion checks and metrics.

In review the live plan line is force-suppressed for the whole session and the plan is shown as a
recorded snapshot instead, so there the switch drives the Legend's **"ROS Nav Plan"** row; that row
(and "Show All"/"Hide All") can still override it per-review. Review entry turns the overlays back
on with one exception: for a trial where a human drove the robot, the recorded plan is only what
ROS *would have done*, so its row starts hidden and a reviewer opts in via the Legend row or the
Control Traj button.

**Hiding the plan during a trial does not cost you the review.** `PlanVisualizer.ProcessMessage`
assigns `renderPathPositions` and only then consults its suppression flags, and
`LiveTrajectoryRecorder.SamplePlanPath` reads exactly that — so a plan hidden all trial is still
recorded in full. This holds while a human drives the robot, too: the ROS backend keeps replanning
from the driven robot's actual pose and the recorder keeps sampling those snapshots
(`recordPlanDuringRobotManual`, on by default), so review can show what ROS would have done at any
replay moment — hidden by default there, as above. The one case review genuinely cannot recover is a trial where no plan ever
arrived (started via `allowStartWithoutRosBackend`, so ROS never published one). The two look
identical from the Legend, so the row reports which it is: **"ROS Nav Plan  [none recorded]"**
when `LiveTrajectoryRecorder.HasAnyPlanSnapshots` is false. `PlanVisualizer` itself is never the
missing piece — it ships inside `SEAN.prefab` → `Display.prefab` → `GlobalPlanVisualizer`, loaded
from Resources into every scene and active by default.

### Player Character Selection (wheelchair + walking avatars)

The onboarding panel's "PWD Player Character" card grid offers the built-in wheelchair
pair plus the `Resources/PlayerCharacters` prefabs that have preview art
(`PlayerCharacterLibrary.OptionsWithPreview`; thumbnails matched by name — separators
ignored, so `dogwalker.png` fits `Dog_Walker` — from `Resources/PlayerCharactersUI`).
A prefab without a thumbnail is hidden from the select pages until its art is dropped
into that folder, but stays in `Options` for World Building and `FindPrefab`. Walking
characters reuse the exact wheelchair player pipeline — spawned by
`RandomAvatar.SpawnPwdPlayer()` as `"PWDPlayer"` with `SFPWDAgent` +
`ManualWheelchairController` — so task sync, tracking, review, and overlays all work
unchanged. Walker-specific tweaks at spawn: the walking locomotion animator controller,
`SFPWDAgent.applyWheelchairColliderCenter = false` (keeps the standing capsule center),
normal pedestrian personal radius, and taller camera offsets. Selection is stored in
`SessionOnboardingSettings.SelectedPlayerCharacterId` ("" = wheelchair); picking a
different character in the current scene reloads it so the player respawns.

### Joystick Tuning Overlay ([U])

`JoystickTuningOverlay` self-bootstraps and **only appears in the practice scene** (any
scene with a `TestSceneFlowManager`): it opens automatically once driving starts and **U**
toggles it. In the study scene the panel never shows and the hotkey does nothing, so a
participant cannot open it mid-trial — but the tuned values keep being applied there, which
is the point of the per-session config. It has an input-device row plus **five** sliders,
kept deliberately small so a participant can adjust it themselves:

| Slider (panel label) | Meaning |
|---|---|
| **How fast it starts** | Drive sensitivity — how far the stick travels before full speed. Readout is the push fraction (`55% push`); lower = twitchier. |
| **How fast it turns** | Turn sensitivity, same readout. Separate from drive: the two rarely feel right at one setting. |
| **Top speed** | Top manual speed (m/s) for the character and the robot. |
| **Ignore small moves** | Deadzone — stick movement ignored around center, so a drifting stick doesn't make the character creep. |
| **Look-around speed** | Gamepad right-stick camera yaw (deg/s); pitch follows at ~60%. |

Both sensitivities mean "how far do I push for the full effect", so the panel also forces
the controllers to **position control** (`manualInertiaDrive = false`,
`angularDirectDrive = true`). Under the original acceleration model the stick only set how
*quickly* you reached top speed — holding it always ended at the same speed, which made the
sensitivity sliders feel inert.

One set of values applies to BOTH the player's `ManualWheelchairController` and the
robot's `VelocityController`, plus `GamepadCameraLook`. Nothing is overridden until a
slider is first moved; after that the values are re-applied to every live controller once
per second (this survives `ApplyJoystickResponseDefaults()` in the controllers' `Start()`,
scene loads, and respawns). "Reset Defaults" restores the defaults.

Camera look is intentionally gentle — fast free-look while driving is a motion-sickness
trigger. `GamepadCameraLook` rotates **only the first-person view** (the small top-right
panel, or the main view after the Tab first/third-person toggle); it never orbits the
third-person camera, because swinging the whole scene around the avatar is what caused
nausea. The stick input ramps in/out (`inputSmoothTime`) and the view eases home
exponentially (`recenterTime` — a slow drift, not a spring-back, since camera motion the
user did not ask for is itself a trigger). `ComfortMotionBlur`'s rotation-linked vignette
is on (`vignetteStrength = 0.3`) to cut the peripheral flow while turning.

**Per-session config**: the tuning is saved as
`SessionLogs/<sessionId>/joystick_config.json`, not global PlayerPrefs. Switching the
session id (onboarding page) hot-loads that session's saved tuning; a fresh session
inherits the current live values and gets its own file on the first change. So a
participant tunes the feel in the TestScene and it carries into their real study scene.

**Input device (two controllers at once)**: the top row (Auto / Gamepad / Stick) picks
which physical device drives, and is stored in the same per-session file. A gamepad and
the Logitech flight stick can stay plugged in together: **Auto** prefers the gamepad (the
primary study controller) and falls back to the stick, and the "Driving with: ..." line
names the device actually being read.

This works because `InputManager.asset` defines **per-slot axes `JoyNAxisK`** (N = joystick
slot 1..4, K = physical axis 0..4) alongside the original shared axes. The original axes
are all `joyNum: 0` ("any joystick"), so with two devices connected their inputs collide —
a resting flight-stick throttle would spin the camera while its stick tilt steered the
agent. `JoystickProfiles` now resolves every request to a *role* (steer / throttle / look)
and then to that role's axis **on the active device's slot only**; the other controller is
never read. Axis roles per device: gamepad steer = left stick X (0), throttle = left stick
Y (1), look = right stick (3/4); flight stick steer = twist (2), throttle = stick Y (1).
Look axes are also rest-calibrated, so a controller idling off-center can never drive the
camera by itself.

### Gamepad D-pad = W/A/S/D, RB = view swap

`GamepadHotkeys` exposes the **active** gamepad's D-pad as a second set of WASD keys — same
roles as the keyboard, nothing new to learn — plus one shoulder button for the view swap:

| Button | Same as | Action |
|---|---|---|
| D-pad Up | `W` | Forward |
| D-pad Down | `S` | Brake, then reverse (the double-tap arming works too) |
| D-pad Left | `A` | Turn left |
| D-pad Right | `D` | Turn right |
| **RB** | `Tab` | Swap the main view between with-avatar (third person) and without-avatar (first person); the other one drops to the mini panel |

Buttons are KeyCodes rather than axes, so the active device's are read as
`Joystick1Button0 + (slot-1)*20 + index`; `viewToggleButtonIndex` picks which (XInput:
4 = LB, 5 = RB, 0 = A, 1 = B).

Unity's legacy Input cannot synthesize key events, so `ManualWheelchairController` and
`VelocityController` OR these flags into their `ManualKeyHeld`/`ManualKeyDown` helpers. The
D-pad obeys the same ownership rule as the stick (`ManualUsesJoystick`), so the agent that
is *not* the human's active role never moves with it — the arrow-key remap stays intact.
Edge state is computed once per frame in the component, so several consumers can read the
same press. The D-pad arrives as physical axes 5/6, exposed per joystick slot in
`InputManager.asset` as `JoyNAxis5/6`; flip `invertDpadY` / `invertDpadX` if a pad reports
a direction with the opposite sign.

### TestScene Practice Flow

`TestSceneFlowManager` runs a lightweight practice loop with NO SEAN objects at all:
session id + character select → drive to the WHITE goal → control switches to a dummy
robot body (any robot-looking prefab; its scripts are auto-disabled and it is driven by
the same `ManualWheelchairController` as the player) → drive to the YELLOW goal → done
(R reloads). Scene checklist in the header comment of `TestSceneFlowManager.cs`: a
`RandomAvatar` spawner with `spawnPlayerOnAwake` unticked, four uniquely-named markers,
a robot body transform, baked NavMesh, scene added to Build Settings for the R-restart.

## Output Files

Each trial creates a timestamped folder under `SessionLogs/` (project root in Editor, `persistentDataPath` in builds), grouped one session -> scenes -> trials. The session id comes from the onboarding page's Session ID field (`ParticipantSession`, kept from the previous session until changed) and is also written into `trial_info.json` (`sessionId`):

```
SessionLogs/
  trials.json                                      <- cumulative index (all sessions)
  P01/                                             <- session id ("unassigned" when empty)
    sidewalkOutofStore/                            <- scene
      trial_001_20260308_151944/
        trial_info.json                            <- metadata, metrics, agent roster, sessionId
        trajectories_all.json                      <- all agents combined
        trajectory_base_link_robot.json            <- robot only
        trajectory_PWDAgent_pwdplayer.json         <- PWD player only
        trajectory_Pedestrian01_backgroundped.json <- individual pedestrian
        control_modes.ctrlmode                     <- timestamped control transitions (each agent's
                                                      mode at the trial start is included as an entry)
```

Trial folder naming and every file inside are unchanged; only the grouping folders are new. Legacy flat `SessionLogs/trial_xxx/` folders still load (the F12 panel scans recursively). ROI exports nest the same way under `SessionLogs/ReviewExports/<session>/<scene>/`.

## Saved World Building Scenarios

The whole built world can be saved as a reusable "scenario" — a self-contained folder with a
scene file plus an asset library — and rebuilt later. A real `.unity` file cannot be written
at runtime, so the scene file records the base scene plus the full delta on top of it:

- runtime-**placed** objects (palette spawns and Meshy GLB imports; they carry a
  `WorldBuildingPlacedObject` marker with their source),
- **moved/rescaled pre-existing** scene objects (furniture, task/goal markers, the robot —
  anything touched in World Building gets a `WorldBuildingSceneObjectBaseline` snapshot of
  its pristine pose; only actually-changed objects are recorded),
- **deleted** scene objects (World Building's delete deactivates; recorded as `deleted`).

Flow:

- **Save, any time**: the World Building overlay's **"Save World..."** button opens the
  "Save This Scene?" modal (camera snapshot + name) and returns to editing afterwards. This
  is the reliable way to save — it does not depend on switching scene or character.
- **Save, on switch or restart**: a built world is an *option*, never sticky state — anything
  that starts the next trial goes back to the **original** scene, and the edits come back only
  when their card is picked. So both **"Apply and Reload"** and **"Run Again"** reload the base
  scene when World Building changed the world, and the modal appears first so the work is not
  lost. *Save & Continue* writes the scenario then reloads; *Continue Without Saving* reloads
  and discards; *Cancel* goes back to editing without reloading. A world just saved or restored
  and left unmodified is not re-prompted (`WorldBuildingScenarioStore.RestoredSignature`) — it
  reloads straight away, since it is already a card. Scenes not in Build Settings cannot be
  reloaded, so they restart in place with the edits still standing (Console warning).
- **Load**: saved scenarios show up on the onboarding page as thumbnail cards under "Saved
  World Building Scenes", directly below the preset scene list and **scoped to the scene
  selected there** — a saved world is a variant of its base scene, so listing worlds from other
  scenes would offer a pick that silently changes the scene selection. Picking a different
  scene re-filters the cards and clears the selection. The list is deliberately *not* filtered
  by session id — a world built before the operator typed their id belongs to the previous one
  and hiding it would look like the save was lost; the owning session is shown on the card
  instead. The preset scene stays the default; picking a card loads the
  scenario's base scene, respawns the placed objects and replays the scene deltas, then syncs
  a restored robot goal/start back into the `CustomStartGoal` Locations so ROS navigates the
  restored layout. Clicking the selected card again deselects it.

Restoring is driven by `WorldBuildingScenarioRestorer`, a static `SceneManager.sceneLoaded`
hook that spawns a one-shot runner — deliberately NOT `SessionReviewManager.Start()`, so a
scene whose review manager is missing, inactive, or ordered differently still rebuilds the
world. Every step logs to the Console (`[WorldBuildingScenario]`), so a save/load that does
nothing says why.

Storage, one folder per scenario (`WorldBuildingScenarioStore`):

```
SessionLogs/
  P01/
    scenario/
      sidewalkNarrowroad 03-08 15_19_20260308_152012/
        scene.json       <- scene file: name, base sceneName, sessionId,
                            objects (prefab/GLB source + pose),
                            sceneObjectDeltas (hierarchy path + original pose + new pose / deleted)
        assets/          <- asset library: copied import files (Meshy GLBs), so the scenario
                            survives deletion of the originals and can be zipped/shared
        thumbnail.png    <- snapshot shown on the onboarding card
```

Palette objects are respawned from `Resources/WorldBuildingSpawns/<prefabName>` (shipped with
the build — not copied); imports load from `assets/` with the original absolute path as
fallback (a missing file logs a warning and is skipped). Scene-object deltas are matched by
hierarchy path; duplicate names are disambiguated by the recorded pristine pose. Old saves
with `scenario.json` (no asset library) still load.

**Robot and pedestrian goals.** Both goals AND starts are draggable in World Building and
round-trip through scenarios. Registered as editable at editor entry: the task's four
start/goal markers (`StartAndGoal/...`) AND the PWD spawner's named start/goal objects
(`RandomAvatar.startObjectName`/`goalObjectName`, resolved with the same lookup spawning
uses). All of them are marker-protected: a scenario only ever records their POSE — never a
"deleted" delta (their active state is engine-managed, e.g. the unused start/goal is
deactivated every scene load; deactivating a live goal on restore would break the trial).
The task's Start markers are engine-deactivated outside World Building; entering World
Building re-activates them (`ShowTaskStartMarkersForWorldBuilding`) so the start FLAG can be
dragged — the robot body is usually not on its start point after a run — and exit re-hides
exactly the ones it activated. After a restore (or on leaving World Building, for the live
PWD):

- a moved **robot** goal/start is written into the `CustomStartGoal` Locations
  (`SyncRestoredRobotMarkers` / `SyncMovedRobotMarkersIntoTask`), else `NewTask` would
  re-copy the old pose and ROS would navigate to the old goal. The start can come from the
  dragged Start flag or the dragged robot body; when both moved, the flag wins;
- a moved **pedestrian** start/goal is re-baked into the spawned PWD
  (`WorldBuildingScenarioRestorer.SyncPwdSpawnerWaypoints`): `SFPWDAgent.waypointStart/Goal`
  update (arrival detection + auto-nav re-targeted via `RestartNavigationCoroutine`, skipped
  in manual mode), and on restore the agent is also teleported to the moved start. A moved
  start also replaces the wheelchair controller's remembered spawn pose
  (`ManualWheelchairController.SetSpawnPose`) — trial restarts (`ResetToSpawn`, i.e. the
  in-place "Run Again" from World Building) teleport the agent to that pose, so without the
  refresh the pedestrian would restart at the scene's ORIGINAL start. In
  Player-controlled scenes the task's `playerGoal` marker needs no extra sync —
  `SyncPwdPlayerToTaskStartGoal` re-reads it at every trial start.

Markers without a renderer or collider cannot be clicked (selection needs visual bounds or a
collider); the standard flag/beacon markers are visible and selectable.

### Trajectory JSON Format

Each trajectory file uses the `StateRecording` schema (from Rerun data types):

```json
{
  "totalDuration": 45.2,
  "timelines": [
    {
      "objectId": "SEAN_Robots_P3DX_base_link_0000702C",
      "states": [
        {
          "objectId": "SEAN_Robots_P3DX_base_link_0000702C",
          "timestamp": 0.0,
          "position": { "x": 1.2, "y": 0.0, "z": 3.4 },
          "rotation": { "x": 0, "y": 0.7, "z": 0, "w": 0.7 },
          "scale": { "x": 1, "y": 1, "z": 1 },
          "properties": []
        }
      ]
    }
  ]
}
```

- **`timestamp`**: Seconds relative to recording start (not `Time.time`).
- **`position/rotation`**: World-space transform at that moment.
- **`properties`**: Reserved for future per-frame annotations (see below).

---

## Code Structure

```
Assets/Scripts/SessionReview/
  SessionReviewManager.cs     <- Singleton facade, input handling, UI status badge
  SessionTracker.cs           <- Detects trial start/end, identifies all agents
  LiveTrajectoryRecorder.cs   <- 10 Hz position/rotation sampling, interpolation
  ControlModeLog.cs           <- Timestamped control mode transitions (manual/auto/static)
  TrialDataArchive.cs         <- Persists TrialRecords, metrics, creates per-trial folders
  MultiAgentTrajectoryRenderer.cs  <- Draws trajectory lines with per-agent colors
  RewindController.cs         <- Playback scrubbing, camera perspective management
  MetricsOverlayUI.cs         <- IMGUI panel showing trial metrics
  UiScaleController.cs        <- Global UI zoom (ReviewUiScale) + top-left "Aa" control, Ctrl +/-/0
  ComfortMotionBlur.cs        <- Camera image-effect: rotation-driven motion blur + vignette
  Shaders/ComfortMotionBlur.shader  <- Corresponding HLSL shader (two-ring Gaussian kernel)
```

### Data Flow

```
Trial starts (Tasks.Base.onNewTask)
  -> SessionTracker.BeginTracking()
     -> discovers robot, PWD, pedestrians
     -> registers each with LiveTrajectoryRecorder.TrackAgent(id, transform)

During trial:
  -> LiveTrajectoryRecorder samples at 10 Hz
  -> ControlModeLog tracks mode transitions

Trial ends (task completes / timeout)
  -> SessionTracker.FinishCurrentTrial()
     -> fires TrialEnded event
  -> TrialDataArchive.OnTrialEnded()
     -> captures metrics, control summaries
     -> creates per-trial folder
     -> calls LiveTrajectoryRecorder.SaveTrialTrajectories()
     -> saves trial_info.json, control_modes.ctrlmode
     -> auto-saves ROI export (review_roi_export.json + roi_topdown.png)
        into the same trial folder (SaveTrialRoi; toggle: autoSaveRoiWithTrial)

User presses T:
  -> SessionReviewManager.EnterRewindMode()
     -> freezes simulation (Time.timeScale = 0)
     -> builds snapshot from LiveTrajectoryRecorder
     -> MultiAgentTrajectoryRenderer draws all lines
     -> RewindController manages playback + cameras
```

### Key Classes in Detail

#### `LiveTrajectoryRecorder` -- Core Trajectory Engine

This is the central data source for all trajectory operations.

| Method | Description |
|--------|-------------|
| `TrackAgent(string id, Transform t)` | Register an agent for tracking. Called by SessionTracker. |
| `BuildSnapshot() -> StateRecording` | Returns all recorded data as a StateRecording (for rendering). |
| `GetStateAtTime(float time) -> Dict<string, ObjectState>` | Interpolated state of every agent at a given time. Used by RewindController. |
| `SaveTrialTrajectories(string folder, TrialRecord trial)` | Exports per-agent + combined trajectory files filtered to the trial window. |

**Internal**: Uses binary search + linear interpolation for `GetStateAtTime`. Timestamps are relative to `recordingStartTime`.

#### `MultiAgentTrajectoryRenderer` -- Visualization

Renders `LineRenderer`-based trajectory lines with:
- **Role-based colors**: Robot = red, PWD = purple, pedestrians = rotating palette
- **Control mode gradients**: Robot lines shift between blue (manual) and cyan (auto). PWD lines shift between purple (manual), pink (auto), and grey (static).
- **Direction arrows**: On robot and PWD lines, small arrows indicate heading.
- **Start/end markers**: Spheres at trajectory endpoints.
- **Legend panel**: Bottom-left IMGUI showing agent name and color swatch.

#### `RewindController` -- Playback Engine

Handles scrubbing through time and moving the camera:
- Reads interpolated states from `LiveTrajectoryRecorder.GetStateAtTime()`
- Physically moves agent transforms to their recorded positions
- Manages 5 camera perspectives (robot FP, PWD FP, pedestrian over-shoulder, top-down, free)
- Ghost trails show partial trajectory up to current playback time

---

## Robot Trajectory -- Annotation Extension Points

The robot trajectory is the primary target for future annotation work. Here are the key touch points:

### 1. Where robot trajectory data lives

**Recording**: `LiveTrajectoryRecorder.SampleAll()` (line ~68) creates an `ObjectState` per frame:

```csharp
// LiveTrajectoryRecorder.cs — SampleAll()
timelines[kvp.Key].states.Add(new ObjectState
{
    objectId = kvp.Key,
    timestamp = timestamp,
    position = kvp.Value.position,
    rotation = kvp.Value.rotation,
    scale = kvp.Value.localScale,
    properties = new List<SerializedProperty>()   // <-- annotation slot
});
```

The `properties` list on each `ObjectState` is currently empty. This is the natural place to attach per-frame annotations (e.g., velocity, control mode, proximity metrics, behavior labels).

**Filtering**: `SaveTrialTrajectories()` filters by trial time window and saves per-agent files. The robot file is named `trajectory_{robotId}_robot.json`.

**Rendering**: `MultiAgentTrajectoryRenderer.ShowTrajectories()` iterates `trial.agentRoles` and matches `AgentRole.Robot` to assign `robotColor`, build control-mode gradients, and create direction arrows.

### 2. How to add per-frame annotations to the robot trajectory

**Option A: Use `ObjectState.properties` (no schema change)**

The Rerun `SerializedProperty` type already supports key-value pairs. You can populate `properties` during sampling:

```csharp
// In LiveTrajectoryRecorder.SampleAll(), after building the ObjectState:
var props = new List<SerializedProperty>();

// Example: attach current velocity
props.Add(new SerializedProperty {
    name = "velocity",
    value = kvp.Value.GetComponent<Rigidbody>()?.velocity.magnitude.ToString("F2") ?? "0"
});

// Example: attach current control mode
props.Add(new SerializedProperty {
    name = "controlMode",
    value = controlModeLog.GetModeAtTime(kvp.Key, Time.time).ToString()
});

state.properties = props;
```

These properties serialize directly into the trajectory JSON and are available at review time via `ObjectState.properties`.

**Option B: Separate annotation timeline (new data structure)**

For richer annotations (behavior labels, events, segments), create a parallel data structure:

```csharp
[Serializable]
public class TrajectoryAnnotation
{
    public float timestamp;
    public string label;        // e.g., "approaching", "yielding", "takeover"
    public Color color;         // for visualization
    public string metadata;     // JSON blob for arbitrary data
}

[Serializable]
public class AnnotatedTrajectory
{
    public string objectId;
    public List<TrajectoryAnnotation> annotations;
}
```

Save alongside the trajectory file as `annotations_robot.json`.

### 3. How to visualize annotations on the trajectory

**Color segments**: `MultiAgentTrajectoryRenderer.BuildControlModeGradient()` already builds a `Gradient` from timestamped entries. You can replicate this pattern for behavior annotations:

```csharp
// In MultiAgentTrajectoryRenderer, add a method like:
private Gradient BuildAnnotationGradient(string agentId, List<TrajectoryAnnotation> annotations, 
                                          float trialStart, float trialDuration)
{
    var gradient = new Gradient();
    var keys = new List<GradientColorKey>();
    foreach (var ann in annotations)
    {
        float t = Mathf.Clamp01((ann.timestamp - trialStart) / trialDuration);
        keys.Add(new GradientColorKey(ann.color, t));
    }
    // ... set gradient keys
    return gradient;
}
```

Then pass this gradient to `CreateTrajectoryLine()` instead of the control-mode gradient.

**Markers at specific points**: The existing `CreateMarker()` method can place spheres at annotation timestamps. Combine with the legend panel to show what each marker means.

**Tooltip on hover**: The progress bar in `RewindController.OnGUI()` already shows the current time. You can extend it to display the active annotation label at `currentTime`.

### 4. Key files to modify for robot trajectory annotation

| Goal | File | Method |
|------|------|--------|
| Attach per-frame data during recording | `LiveTrajectoryRecorder.cs` | `SampleAll()` |
| Save annotation data to disk | `LiveTrajectoryRecorder.cs` | `SaveTrialTrajectories()` |
| Load and apply annotation colors | `MultiAgentTrajectoryRenderer.cs` | `ShowTrajectories()` |
| Build color gradients from annotations | `MultiAgentTrajectoryRenderer.cs` | New method (see above) |
| Show annotation at current playback time | `RewindController.cs` | `OnGUI()` |
| Add annotation metadata to trial record | `TrialDataArchive.cs` | `OnTrialEnded()` |

### 5. Robot-specific identifiers

The robot is identified by `SessionTracker.GetObjectId(sean.robot.base_link)`. This typically resolves to the Rerun `TrackedObject.objectId` if present, otherwise `base_link.name`. The same ID is used consistently across:

- `TrialRecord.agentRoles` (with `AgentRole.Robot`)
- `LiveTrajectoryRecorder` timelines
- `ControlModeLog` entries (as `robotAgentId`)
- `RewindController.FindTransformForId()` cache
- Per-agent trajectory filename

---

## Comfort Motion Blur

`ComfortMotionBlur` is an `OnRenderImage` image effect attached to the rewind camera. It applies a screen-space Gaussian blur that scales with camera rotation speed, reducing perceived motion sickness during fast pans in session review.

The companion shader lives at `Shaders/ComfortMotionBlur.shader` (`Hidden/ComfortMotionBlur`). Attach `ComfortMotionBlur` to any camera — `RewindController` adds it automatically.

### How It Works

Each frame in `LateUpdate`, the script:

1. Measures the angle between the camera's current and previous rotation.
2. Divides by `deltaTime` to get a raw angular speed (°/s).
3. Applies a low-pass exponential filter (`angularSpeedSmoothTime`) to avoid blur spikes from discrete playback snapshots.
4. Maps the smoothed speed to a `[0, 1]` blur intensity using `rotationForMaxBlurDegreesPerSecond` as the ceiling.
5. Raises or lowers `currentBlurStrength` toward the target using asymmetric exponential smoothing (`blurRiseSpeed` / `blurFallSpeed`).

In `OnRenderImage`, the current strength is multiplied by `maxBlurStrength` and passed to the shader, which samples a 17-point two-ring Gaussian kernel with radius `blurRadiusPixels × texelSize × strength`.

### Parameter Reference

#### Blur Shape

| Parameter | Default | Range | Effect |
|-----------|---------|-------|--------|
| `maxBlurStrength` | `0.85` | 0 – 1 | Overall blur intensity ceiling. Scales the final kernel radius. `0` = no blur ever. |
| `blurRadiusPixels` | `15` | ≥ 1 | Kernel radius in pixels at full strength. `8` = subtle, `15` = medium, `20+` = strong. |

#### Blur Trigger

| Parameter | Default | Effect |
|-----------|---------|--------|
| `rotationForMaxBlurDegreesPerSecond` | `220` | Rotation speed (°/s) that produces 100% blur. **Lower = kicks in sooner.** At `220`, a slow pan barely triggers blur; a fast spin hits maximum. Set to `60–90` if you want blur to appear during gentle head turns. |

#### Rise / Fall Speed

| Parameter | Default | Effect |
|-----------|---------|--------|
| `blurRiseSpeed` | `10` | How fast blur ramps up when rotation accelerates. Higher = more responsive. |
| `blurFallSpeed` | `18` | How fast blur fades after rotation slows. **Keep this higher than `blurRiseSpeed`** to avoid a pulsing/stuttering feel during 10 Hz playback snapshots. |

#### Angular Speed Smoothing

| Parameter | Default | Effect |
|-----------|---------|--------|
| `angularSpeedSmoothTime` | `0.10` | Low-pass filter time constant (seconds) for the raw angular speed signal. |

> **Why this matters for session review**: `LiveTrajectoryRecorder` samples at ~10 Hz, so agent transforms jump in discrete steps. Without filtering, each jump reads as a huge instantaneous angular speed, slamming the blur to maximum and dropping it again — perceived as a flicker/stutter.
>
> - **Live rotation / VR headset**: use `0.02–0.04` s — fast enough that blur appears instantly with head movement.
> - **Session review playback**: use `0.08–0.12` s — spreads each 100 ms snapshot impulse across the full inter-frame interval, preventing 10 Hz pulsing.

#### Transition Boost

Triggered by `TriggerTransitionBlur()` whenever the camera teleports (perspective switch, timeline scrub). Adds a brief full-blur flash independent of rotation speed.

| Parameter | Default | Effect |
|-----------|---------|--------|
| `transitionBoostStrength` | `0.55` | Peak blur added on top of rotation blur during the boost window. |
| `transitionBoostDuration` | `0.15` | Duration of the boost in seconds. Keep below the snapshot interval (0.1 s) to avoid overlap with the next playback frame. |

#### Vignette (off by default)

A peripheral darkening overlay that tracks blur intensity. Disabled by default — enabling it on scenes with auto-exposure causes perceived whole-scene brightness flicker.

| Parameter | Default | Effect |
|-----------|---------|--------|
| `vignetteStrength` | `0` | Max darkness at the screen edge. `0` = off. Enable only if auto-exposure is disabled. |
| `vignetteRadius` | `0.55` | Distance from center where darkening begins (normalized, aspect-corrected). Smaller = more tunnel-vision. |
| `vignetteSoftness` | `0.45` | Width of the gradient falloff. Larger = softer edge. |

### Quick Tuning Guide

| Goal | What to change |
|------|----------------|
| More blur during rotation | Increase `blurRadiusPixels` (try 20–30) and/or `maxBlurStrength` |
| Blur kicks in earlier | Lower `rotationForMaxBlurDegreesPerSecond` (try 80–120) |
| Stutter/pulsing feel in review | Increase `angularSpeedSmoothTime` to `0.10–0.12`, increase `blurFallSpeed` to 18–25 |
| Blur lags behind head movement | Lower `angularSpeedSmoothTime` to `0.02–0.04` |
| Transition flash too long | Lower `transitionBoostDuration` |
| No blur at all | Set `maxBlurStrength = 0` |

### Public API

```csharp
// Call when the camera teleports (perspective switch, scrub jump) to suppress
// the spurious angular-speed spike that would otherwise produce false blur.
comfortMotionBlur.TriggerTransitionBlur();
```

`RewindController.SetPerspective()` calls this automatically on every perspective switch.

---

## Dependencies

- **SEAN framework**: `SEAN.SEAN.instance`, `Tasks.Base`, `Metrics`, `pedestrianBehavior.agents`
- **IVI**: `ManualWheelchairController`, `INavigable`, `SFPWDAgent`
- **Rerun data types only**: `StateRecording`, `ObjectStateTimeline`, `ObjectState`, `SerializedProperty`, `TrackedObject`. No runtime dependency on the Rerun recording plugin.
- **VelocityController**: `ManualControlActive` property (added) for robot control mode detection.
- **ManualWheelchairController**: `WaitingForStart` property (added) for PWD mode detection.
