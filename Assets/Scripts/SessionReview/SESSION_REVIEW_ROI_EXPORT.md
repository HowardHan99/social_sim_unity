# Review ROI Export

This export is built on top of the existing Session Review system.

## Reused Data Sources

- `TrialRecord` from `TrialDataArchive`
- `StateRecording` from `LiveTrajectoryRecorder.BuildSnapshot()`
- active review-time trial window from `SessionReviewManager.EnterRewindMode(...)`
- object-id resolution from `RewindController`
- existing `SessionLogs` root folder

No separate runtime logger was introduced for ROI export.

## Automatic Save (no export needed)

`TrialDataArchive` writes the same ROI export (`review_roi_export.json` +
`roi_topdown.png`) into every trial's own save folder when the trial auto-saves
at trial end (and again from `FinalizeLatestTrial`, overwriting in place with
the extended window). Default `ReviewExportSettings` are used; both the toggle
(`autoSaveRoiWithTrial`) and the settings are editable on the `TrialDataArchive`
inspector. The manual review-time export below still exists for tuned
padding/offset exports and writes to `SessionLogs/ReviewExports/` as before.

## Review Flow

1. Finish a trial.
2. Enter review mode.
3. Press `E` or click `Export ROI`.
4. Start from the trajectory envelope.
5. Adjust (padding defaults to 12 m per side; sliders go up to 60 m so the ROI
   can cover surrounding context, not just the swept trajectory):
   - `Pad X`
   - `Pad Z`
   - `Offset X`
   - `Offset Z`
6. Optionally keep `Export aligned top-down PNG` enabled.
7. Click `Export Current ROI`.

## Output Location

Exports are saved under:

`SessionLogs/ReviewExports/`

Each export gets its own timestamped folder.

## Export Contents

`review_roi_export.json`

- trial metadata
- ROI bounds
- simplified collider/object list
- object names and hierarchy paths
- semantic type guess
- collider shape/type
- trajectory samples
- trajectory samples inside ROI
- start/end positions
- goal position

`roi_topdown.png` (optional)

- clean top-down image aligned to the exported ROI bounds
- dynamic actors (robot, player, pedestrians, goal UI) are hidden, so it is scene
  geometry only

`roi_topdown_trajectory.png` (optional)

- the same plate with every agent's trajectory drawn on top
- rendered once and shared with `roi_topdown.png`, so the two are pixel-aligned
- per agent: polyline, filled circle at start, filled square at end, and an `X`
  at the goal when the goal is a real task goal rather than an inferred one
- agent colours follow the same palette/order as `review_roi_viewer.py`, so the
  baked image and the viewer agree on which colour is which agent
- both PNGs are written whenever `Export top-down PNGs` is enabled, and both are
  described in the JSON (`image` and `trajectoryImage`)

## Current Goal Semantics

- `Robot`: uses current task robot goal when available
- `PWDPlayer`: uses current task player goal when available
- background pedestrians/PWDs: goal currently falls back to the trajectory end point and is marked as inferred

## Main Files

- `SessionReview/SessionReviewManager.cs`
- `SessionReview/ReviewRoiExporter.cs`
- `SessionReview/RewindController.cs`
- `SessionReview/TrialDataArchive.cs`
