# Handoff — 2026-08-10: O4 evidence investigation + O5 heading-delta mode

**Branch:** `main` — clean tree before this phase, gate green after.

## What landed

### 1. O4 — reframed with evidence (no code, honest negative)
The roadmap premise ("capture-zone/base decode from battle_results.dat")
was investigated end-to-end and **disproven**: WoTB replays contain no
capture-zone geometry. Capture zones are map-static game data.

Evidence produced (all recorded in `offline/replay-format.md`):

- **Full `battle_results.dat` top-level walk** (both 11.19.0 replays, pickle
  unwrapped at offset 16 — the envelope is `(8-byte arena id, protobuf)`):
  a 24-row field table covering 1/2/3/4/5/8/11/137/150/181/182/183/
  184/185/201/301/302/303/999. Notable: field 8.3 = **client map-config id**
  (3235 savanna / 2277 medvedkovo — NOT the DB `map_id` 11/7); fields 11/150
  carry 3:2-aspect map-space descriptor pairs; 302/303 are team records
  whose semantics are unproven (303.1/303.2 are NOT scores/victory points).
- **Type-31** (6 777 packets): 4-byte float, **combat-only** (t≈71–275 s),
  ~30 Hz; NOT distance-to-nearest-enemy (correlation tested, none); toggles
  between ~27.009 (repeated default) and 6.7–8.8 minima. Semantics unknown.
- **Type-35** (2 814 packets): 1 byte per 0.1 s tick, **mod-256 tick counter**.
- **Type-39** (16 984 packets): 28 bytes = 7 float32, **per-frame ~60 Hz**,
  smooth drift, matches NO entity position; settles on fixed anchors
  (spawn corner t≈1.7–68 s, then a victory point t≈245–281 s on savanna) —
  a **camera/attention point**, not zone geometry. Recorded as the live
  camera/VP-track candidate: the packet camera may be cross-validable
  against the `FUN_00d29ea0(0)` object's `+0x60` matrix.

**Consequence:** objective markers ride the O3 beacon layer + future
map-static coordinate data, not any replay file. The roadmap O4 row is
updated to reflect the reframe; the replay-format doc records the structure
evidence so no future agent re-derives it.

### 2. O5 — `--heading-delta` extractor mode (shipped)
`scripts/python/replay-delta-extractor.py` gained a movement-gated,
wrap-aware heading/turn mode:

- `pick_yaw_session()` — selects the newest 11.19 session **with** packet
  yaw (the default `pick_session` can land on an older duplicate decode
  whose yaw column is NULL, since the yaw decoder shipped after first import).
- `unwrap_radians()` + `heading_delta_series()` — per-window deltas of BOTH
  the motion heading (atan2(dx,dz)) and the packet yaw
  (`position_samples.yaw`, migration 5), normalized to [-π, π]; yaw endpoints
  are interpolated in **unwrapped** space so ~1s-sample windows never lose
  coverage; `seam_crossings` counts adjacent raw pairs with |Δ| > π (the
  ±180°-seam case naive deltas get wrong by ~2π).
- Output: turn distribution (radians + degrees) for both series, the seam
  count, and a recommended yaw-delta target/tolerance for the live pilot.

Validation (two-replay rule):

| Replay | Windows | Yaw Δ median | Yaw Δ max | Seam crossings |
|---|---|---|---|---|
| savanna | 1 644 | 0.011° | 47.1° | 0 |
| medvedkovo | 1 728 | 2.92° | 118.2° | **5** |

Semantics check: yaw (facing) max 47° vs motion-heading max 180° on savanna —
the motion heading flips on reversal while facing turns smoothly, exactly
the yaw-vs-velocity distinction the facing campaign relies on. Synthetic
tests prove the seam detector fires on a 179→-177° crossing (1 crossing,
wrapped Δ = +4° not -356°) and stays 0 on a continuous drift (mean Δ = 4.0°
for 1°/s over 4 s windows).

## Files changed

- `offline/replay-format.md` — battle_results full walk table + negative
  finding; type-31/35/39 structure evidence table.
- `docs/operations/product-roadmap.md` — O4 reframed (evidence), O5 marked
  done with the validation numbers.
- `scripts/python/replay-delta-extractor.py` — O5 mode.

## Validation

- `python scripts/python/replay-delta-extractor.py --heading-delta` on both
  yaw-bearing sessions (numbers above).
- `--self-test` passes; default and `--movement` modes unchanged.
- `python scripts/python/offline_check.py --refresh` — links 39 files / 112
  links 0 broken; blocker numbering BLK-0001..0026 contiguous; ledger 65
  sections consistent. Exit 0.

## Assumptions / unknowns

- Type-39 semantics (camera/attention point) are inferred from behavior
  (per-frame cadence, smooth drift, settled anchors, no entity match), not
  proven against game internals — recorded as a candidate, not a fact.
- 302/303 team records: count varies (2 vs 4), values are battle-specific —
  decoded to evidence, semantics deliberately left unproven.
- Type-31 semantics remain open (not distance-to-enemy; combat-only).

## Recommended next steps

1. **L2 facing live session** (ring-record dump vs `position_samples.yaw`)
   stays behind the approval gate — the O5 yaw-delta series is now the
   rehearsed pilot target for it.
2. Optional: a Ghidra pass on the type-39 write site (find which function
   fills the 7-float scene point) to confirm the camera hypothesis statically
   before any live VP-matrix session.
3. Map-static base coordinates, when available, feed the O3 beacon layer for
   true objective markers (documented as the O4 successor).
