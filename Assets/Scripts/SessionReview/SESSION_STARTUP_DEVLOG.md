# Session Startup Dev Log

## Problem

We wanted this startup flow:

1. Scene is already loaded.
2. Robot, pedestrians, and camera handoff initialize in the background.
3. The experience pauses on the robot-ready view.
4. The user presses `Start Trial`.
5. Movement begins.

What actually happened in scenes like `sidewalkOutofStore` was:

1. Unity Play started the scene and `PedestrianControl/Graph` spawned correctly.
2. Clicking the onboarding `Start Session` button triggered a second scene load.
3. That second load re-ran `NavManager`, task warmup, and preview setup.
4. The second pass often left `Graph/Agents` empty and the UI stuck on `LOADING SESSION`.

## Root Cause

The key issue was not pedestrian pathing itself.

In scenes without a configured `SceneChange` component, onboarding still called:

`SceneManager.LoadScene(targetSceneName)`

Since `targetSceneName` was the current scene, pressing `Start Session` effectively reloaded the same scene. That caused the duplicate initialization pattern we saw in the logs.

## Fixes

### 1. Same-scene onboarding no longer reloads the scene

File: `SessionReview/SessionReviewManager.cs`

If onboarding targets the current scene and there is no `SceneChange`, we now:

- keep the current scene alive
- close onboarding
- enter the pre-trial ready prompt in-place

This is the change that removed the "second pile of logs" and stopped graph pedestrians from disappearing after pressing `Start Session`.

### 2. Pre-trial warmup is treated as preview, not a real trial

Files:

- `SessionReview/SessionReviewManager.cs`
- `SEAN/Tasks/Base.cs`

Warmup now uses `PrepareTaskPreview()` to:

- configure the task
- place robot/start/goal
- publish the goal
- allow normal camera handoff

But it does **not** start the actual trial loop until the user confirms.

### 3. Automatic task start is blocked while the ready prompt is active

File: `SEAN/Tasks/Base.cs`

`CheckNewTask()` and direct `StartNewTask()` calls now respect the pre-trial ready state exposed by `SessionReviewManager.BlocksAutomaticTrialStart`.

This prevents the base task system from starting itself behind the ready prompt.

### 4. Actual graph agents are counted from `Graph/Agents`

File: `IVI/Scripts/Navigation/NavManager.cs`

`allAgents` now comes from:

`agentsGO.GetComponentsInChildren<INavigable>(true)`

instead of scanning every `INavigable` in the scene. That makes the ready check depend on real graph pedestrians instead of unrelated agents like the robot or PWD.

### 5. Random avatar bootstrap was hardened

File: `SEAN/Scenario/Agents/RandomAvatar.cs`

The spawn path was made more defensive so background agents can still initialize when:

- the animator is on a child instead of the prefab root
- the random avatar pool needs rebuilding
- the avatar selection pool is temporarily exhausted

## Cleanup

The temporary `NavManager` debug logs used during investigation were removed once the root cause was confirmed.

An unused helper in `SEAN/Tasks/Base.cs` was also removed.

## Current Intended Flow

For same-scene onboarding:

1. Press Unity Play.
2. Existing scene systems initialize normally.
3. Onboarding stays on top.
4. Press `Start Session`.
5. No scene reload occurs.
6. Pre-trial ready prompt takes over.
7. Press `Start Trial`.
8. Trial begins.

For scene-switch onboarding:

1. Onboarding delegates to `SceneChange`.
2. Target scene loads.
3. Ready prompt appears in the loaded scene.
4. Trial begins only after `Start Trial`.

## Files Touched

- `SessionReview/SessionReviewManager.cs`
- `SEAN/Tasks/Base.cs`
- `IVI/Scripts/Navigation/NavManager.cs`
- `SEAN/Scenario/Agents/RandomAvatar.cs`
