# Handoff — 2026-08-10: L3 damage-dealt driver path re-verified after the tank-record anchor fix

**Branch:** `main` — gate green after, tree clean.

## Status: L3 is code-ready — no new driver needed

The damage-dealt live session shares the L1 driver: `invoke-hp-diffing-session.ps1`
already supports `-Track damage-dealt` (qualification via the extractor's
`--damage-dealt` mode, `dealt_dump_schedule` event-bound pairs, `hp-diff
--direction increment` verdict). The only remaining work was to re-verify
the full path AFTER the L1 tank-record anchor correction (commit `140b7b5`),
since the earlier 5/5 rehearsals predated it.

## Verified end-to-end through the driver (offline, real timelines)

Synthetic tank-record region dumps with the damage-dealt int32 rising by the
exact cumulative damage at each real hit tick (+0x48 planted, constant
non-tracking decoy at +0x20):

| Replay | Target | Verdict | Offset | Score / Flatness | Matched |
|---|---|---|---|---|---|
| savanna | 3760577 | HIT | `+0x48` | 1.0 / 1.0 | 5/5 |
| medvedkovo | 2549401 | HIT | `+0x48` | 1.0 / 1.0 | 5/5 |

Exit paths for the damage-dealt track, all confirmed:

- `-LiveAcquire` without a web host → `rendezvous_unavailable` (fail-closed).
- `-FailOnNoHit` on a constant-field fixture → exit 1, clean.

## Files changed

- `docs/operations/record-diffing-groundwork.md` — post-anchor driver
  re-verification note in the damage-dealt rehearsal section.
- `docs/operations/product-roadmap.md` — L3 row reflects the wired driver +
  two-replay HIT.

## Next

Same gate as L1/L2: when the operator approves a live window, L1 (HP,
tank-record anchor), L2 (yaw, ring-record anchor), and L3 (damage-dealt,
tank-record anchor) all run through the same region seam. One approved
session on the savanna replay can cover L1+L2+L3 in a single web-host run
(the L1 and L3 targets are different entities — 3760578 vs 3760577 — so
the drivers run separately but against the same verified replay).
