# Preliminary Findings section — Overleaf package

Drafted 2 Aug 2026 from the 24 Jul study review, the P1–P3 qualitative coding
artifact, and the logged trajectory corpus.

## Files

| File | What it is |
|---|---|
| `findings.tex` | The section itself. Drop into your template with `\input{findings}`. |
| `main.tex` | Minimal two-column wrapper so `findings.tex` compiles standalone. Delete once you paste into the real template. |
| `figures/fig_robot_control.pdf` | Fig. 1 — paired per-participant contrasts + route agreement. Generated. |
| `figures/fig_paths_tall.jpg` | Fig. 2 — all robot routes, narrow road + out of store. From the trajectory artifact. |
| `figures/fig_participants_corner.jpg` | Fig. 3 — blind corner, one panel per participant (P1–P7). From the trajectory artifact. |
| `figures/fig_worldbuilding.pdf` | Fig. 4 — what participants added to make scenes harder. Generated. |
| `figures/fig_paths_wide.jpg` | Spare: corner + crossroad route maps. Not referenced by `findings.tex`. |
| `figures/fig_participants_crossroad.jpg` | Spare: crossroad per-participant panels. Not referenced. |

`findings.tex` needs only `graphicx` and `booktabs`, and uses `figure*`, so the
host document class must be two-column.

## Where the numbers come from

Everything quantitative was recomputed from the trial-level CSVs rather than
copied from the artifact:

- `SessionLogs/_analysis_P123/robot_social_metrics.csv` — 108 trials
- `SessionLogs/_analysis_P123/human_disruption.csv` — 239 agent-trial rows
- `SessionLogs/_analysis_P123/participant_overlap.csv` — corridor IoU
- `SessionLogs/_analysis_P123/overlap_manual_vs_auto.csv` — path deviation
- `SessionLogs/_analysis_worldbuilding/wb_objects.csv` — 110 placed objects

Tests are exact two-sided sign tests over the seven participants' paired means.
With n=7 the floor is p=0.016; they establish direction consistency only.

## Two things to decide before this goes out

**1. Sample scopes differ per stream and the text says so.** Interview themes =
P1–P3. Trajectories = P1–P7 (108 trials). World-building = P1–P10 (110 objects).
If P4+ interviews get transcribed, §5.1 needs updating.

**2. The human-disruption comparison is dropped, on purpose.** The per-trial
control logs show the wheelchair agent is participant-driven for 95% of trial
time in autonomous-robot trials but under the scripted social-force controller
for 61% of trial time in human-driven trials — the participant has moved into
the robot. So any auto-vs-manual contrast on wheelchair-side measures (time to
goal, path efficiency, speed dip near the robot) compares a human-driven
wheelchair against a scripted one. The trajectory artifact's line that "a
human-driven robot disrupts the participant more than the policy does" is not
supported by that data. §5.9 states this as a validity threat instead. Fixing it
needs either a second live participant or a replayed wheelchair trajectory held
constant across conditions.

## Participant pronouns

The section uses they/them for all participants, which is both standard
anonymization practice and avoids carrying gender attributions from the coding
document into print. Pronouns inside quotations are verbatim and untouched.
