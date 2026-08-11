# CAM-007 — W2S projection cross-check + v7 round evidence

- Date: 2026-08-11
- Status: committed; math unit-verified via synthetic fixtures (needs one
  live session to produce v7 round evidence)
- Supersedes: nothing (additive to CAM-005/006)

## What landed

The remaining offline validation piece of the camera track: a validator that
proves the overlay's world-to-screen math against the LIVE memory camera, and
the evidence collection to feed it.

| Piece | File | Notes |
|---|---|---|
| Validator | `scripts/python/verify-camera-projection.py` (new) | Mirrors `WorldToScreen.Project` exactly (Core/Overlay/WorldToScreen.cs); per-round checks: **look-at angle** (camera forward vs camera→tank direction — a third-person camera aims at the tank) and **center distance** (projected tank near viewport center across the 70/90/110° FOV band — the verdict must not depend on one FOV). Reports the **pitch diagnostic**: memory pitch vs the pitch required to aim at the tank, so a wrong pitch convention is visible in the evidence. Exit 0 = verified, 1 = failed, 2 = evidence missing |
| Self-test | same file | 3 synthetic fixtures: correct pose (look-at ~0, tank at center → pass), yaw 90° off (behind camera → fail), pitch 0 (tank below center, look-at exceeds tolerance → fail). CI-safe, no DB |
| Evidence schema | `scripts/invoke-camera-state-verify.ps1` → **v7** | Each round now persists: memory camera pose (posA + yaw `atan2(sin,cos)` + pitch), memory tank (when the resolver/walk resolved), decoded tank at the yaw-aligned time, aligned seconds, tank source. Old v6 aggregates → `evidence-missing` (exit 2) |
| Docs | `scripts/python/README.md` entry + this handoff | |

## Verified

- `python scripts/python/verify-camera-projection.py --self-test` → PASS
  (look-at, wrong-yaw, and no-pitch fixtures all behave as expected).
- Synthetic v7 aggregate (2 good rounds + 1 null round) → `verified`,
  ratio 1.0, per-round diagnostics printed; the null round is skipped.
- Legacy v6 aggregate → `evidence-missing` (exit 2), the honest state.
- `scripts/invoke-camera-state-verify.ps1` parse-check OK (PS 5.1).

## What the next live session will answer

The validator turns the CAM-004 `camera-state-consistent` verdict into a
W2S-relevant claim: if the memory camera's yaw AND pitch point at the tank,
the decoded tank projects near screen center and the overlay can render
nameplates/beacons through the true camera with the existing analytic
pinhole model (modulo the game's effective FOV, which the FOV-band sweep
makes robust against). Two open questions the evidence will settle:

1. **Pitch convention** — the CAM-001 script never correlated pitch; the
   `expectedPitchDeg` vs `memoryPitchDeg` diagnostic distinguishes "camera
   aims at the tank" from "pitch is the tank's hull slope".
2. **Look-at exactness** — whether the replay camera aims exactly at the
   tank center or slightly above (small nonzero look-at angle), which sets
   the nameplate label offset.

## Next steps

- **Live session (approved):** run the CAM-001 v7 script mid-replay, then
  `python scripts/python/verify-camera-projection.py <aggregate>` and branch
  on the verdict; if the pitch convention fails, the memory pitch offset
  (`+0x58`) needs a re-derivation or a sign flip before the overlay swap.
- **Overlay consumption:** the frame endpoint (CAM-006) already serves the
  memory camera; this validator is the acceptance test for that path.
