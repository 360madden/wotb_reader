# Phase 0 foundations complete (F1 facing correlator, F2 overlay frame, F3 velocity/pitch/roll) — 2026-08-10

Status: OFFLINE COMPLETE. No live session, no product read-surface change —
all three workstreams are pure offline groundwork that unlocks the next
approval-gated steps (facing live session, replay-overlay Phase 1, velocity
`+0x28` memory correlation).

## F1 — facing (yaw) correlator + rehearsal (workstream A)

1. **`HeadingCorrelator`** (`src/WotBTreader.Core/Discovery/HeadingCorrelator.cs`):
   ranks 4-byte-aligned float32 fields whose wrap-aware delta matches the
   packet-derived yaw delta per replay-time window. TURN windows
   (|expected| > 0.02 rad) are the score denominator; stationary CONTROL
   windows form the flatness denominator (the packet yaw is exactly constant
   when stationary — proven earlier). The yaw lookup is **nearest-sample**
   (the dump's replay clock lands on the packet the state was sent at),
   fail-closed outside the sample span, ties to the earlier sample.
2. **Ground-truth plumbing**: `IYawGroundTruthProvider` (Application) +
   `SqliteYawGroundTruthProvider` (reads `position_samples.yaw`, migration
   5) + DI registration. Pure offline data.
3. **CLI `yaw-diff`**: `<snapshots> --session <guid> --victim <entity-id>
   [--tolerance <rad>]` — mirrors `hp-diff`; HIT requires score 1.0,
   flatness 1.0, ≥ 2 matched turn windows.
4. **Tests**: 8 `HeadingCorrelatorTests` (yaw field ranked first with
   drifting decoy, wrap-across-π, flatness demotion, magnitude-mismatch
   rejection, other-entity filtering, nearest-sample mid-window times,
   fail-closed outside the span, empty for missing entity). Core 137/137.
5. **Rehearsal on both 11.19.0 replays** — synthetic regions whose float32
   at the predicted ring-record yaw offset `+0x2C` carry the REAL packet
   yaw (time-drifting decoy at +0x20):

| Replay | Verdict | Offset | Score | Flatness | Matched / Controls |
|---|---|---|---|---|---|
| savanna | HIT | `+0x2C` | 1.0 | 1.0 | 8/8 turns, 0/9 control changed |
| medvedkovo | HIT | `+0x2C` | 1.0 | 1.0 | 8/8 turns, 0/9 control changed |

Construction rule (rehearsal-caught, same class as the HP flatness trap):
turn windows whose expected delta is below the 0.05 rad tolerance are
skipped as "unchanged" by design — pick rehearsal/live windows well above
tolerance (|expected| > 0.1 rad). The memory side is the remaining input:
the gated live region read dumps the ring record and the same correlator
confirms the +0x2C..+0x37 tail.

## F2 — overlay frame contract + ReplayFrameSource (workstream F)

1. **`Core/Overlay/OverlayFrameModels.cs`** (pure): `OverlayCamera`
   (viewpoint pos + yaw/pitch/roll), `OverlayTankState` (world pos, facing,
   HP fraction 0..1, alive, team, name/clan/tank/class, distance),
   `OverlayFrame` (time + camera + tanks).
2. **`IOverlayFrameSource`** (Application/Storage) + **`ReplayFrameSource`**
   (Application/Replay): builds frames from `GetProjectionAsync` —
   nearest-sample per entity (fail-closed: no sample at/before the frame
   time ⇒ tank omitted), HP fraction from canonical damage events (1.0 when
   no damage; exact max HP not in decoded data), alive from the Destroyed
   event, camera from the viewpoint entity. Registered in DI.
3. **Tests**: 5 `ReplayFrameSourceTests` — nearest-sample + roster + distance
   sort, fail-closed omission, HP arc + destroy, origin camera without
   viewpoint, missing-session guard. Application 37/37.
4. **The seam is the point**: the overlay renders only `OverlayFrame`, so a
   future `LiveFrameSource` behind the same interface is a data-source swap,
   not a rewrite (the roadmap's Phase-5 live overlay requirement).

## F3 — velocity + pitch/roll offline validation (workstream E)

1. **`scripts/python/velocity-pitch-validation.py`**: velocity series
   (finite difference, dt ≥ 50 ms only), yaw-vs-heading (reversals
   reported separately), windowed pitch-vs-slope, stationary-constancy
   checks, `--self-test` fixture. Results on both replays:

| Metric | savanna | medvedkovo |
|---|---|---|
| Yaw vs heading (incl. reversals) | 1634/1634 (100%) | 1307/1307 (100%) |
| Pitch = −slope | 155/155, residual −0.001 ± 1.3° | 113/113, residual −0.002 ± 0.8° |
| Max speed (dt ≥ 50 ms) | 13.0 m/s | 11.0 m/s |
| Roll range | [−0.401, +0.264] | [−0.166, +0.088] |

2. **Findings**: pitch is the vertical facing with a **flipped sign**
   (pitch ≈ −atan2(dY, dH)) — the ring-record rotation correlation must use
   the flipped-sign delta; velocity on dt ≥ 50 ms only (sub-ms duplicate
   packets fabricate ~22 m/s spikes); roll is stationary-constant and
   dynamic while moving (banking) — the third rotation axis.

## Docs

- `docs/operations/record-diffing-groundwork.md` — three new sections
  (facing correlator rehearsal, overlay frame contract, velocity/pitch/roll
  validation) with the evidence tables.
- `docs/operations/product-roadmap.md` — Phase 0 rows marked ✅ + completion
  note.

## Gate

`validate.ps1` exit 0 — all test suites green, PSSA 0 violations, offset
validator PASS. Tree clean at commit time.
