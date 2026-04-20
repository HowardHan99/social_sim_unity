# Session Review System

A self-contained post-trial review system for the social simulation. Records agent trajectories, control modes, and metrics during live trials, then provides a rewind/review mode with trajectory visualization, perspective switching, and a progress bar.

## Quick Start

1. **Add to scene**: Create an empty GameObject, add `SessionReviewManager` component. It auto-creates all other components on the same object.
2. **Run a trial**: The system auto-detects trial start/end via `Tasks.Base.onNewTask`. All agents (robot, PWD player, pedestrians) are tracked automatically.
3. **Review**: After a trial ends, press **Tab** to enter review mode. Press **Tab** or **Esc** to exit.

## Controls

| Key | Action |
|-----|--------|
| **Tab** | Enter/exit review mode (after trial ends) |
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
| **[ / ]** | Previous/next trial |

## Output Files

Each trial creates a timestamped folder under `SessionLogs/` (project root in Editor, `persistentDataPath` in builds):

```
SessionLogs/
  trials.json                                    <- cumulative index
  trial_001_20260308_151944/
    trial_info.json                              <- metadata, metrics, agent roster
    trajectories_all.json                        <- all agents combined
    trajectory_base_link_robot.json              <- robot only
    trajectory_PWDAgent_pwdplayer.json           <- PWD player only
    trajectory_Pedestrian01_backgroundped.json   <- individual pedestrian
    control_modes.ctrlmode                       <- timestamped control transitions
```

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

User presses Tab:
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
