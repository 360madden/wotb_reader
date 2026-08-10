# Handoff — 2026-08-10: HP-diffing victim qualification, verified against decoded replay data

## What changed

1. **Real-data finding (the important one):** queried `.data/treader.db`
   (the 11.19.0 decode runs) for kind-3 damage events per victim. **The
   player's own entity (`mrkool1138`) took ZERO damage in both 11.19.0
   replays** — Oasis Palms 0 events, Dead Rail 0 events; the viewpoint
   Churchill survives unhit. The pre-staged HP-diffing live plan assumed
   "one run on the Oasis Palms replay" with the player as the tracked
   entity, which would have handed the correlator an all-flat series and
   zero windows to match — a plan flaw caught offline, before any live
   session. (11.18.0 sessions do have player damage — Copperfield 19
   events / 9,681 dmg — but the walkable chains and resolver are
   11.19.0.10-bound.)

2. **`replay-delta-extractor.py` gained `--top-victims N`** — ranks the
   session's damage victims by hit count (hits, total damage, first/last
   hit replay-seconds, per-window bucket list at the given window size)
   so victim selection is one command. Verified against direct SQL on
   2026-08-10: all 9 hit windows for victim 3760578 (t = 904.5, 1009.3,
   1078.1, 1365.2, 1434.2, 1503.1, 1566.2, 1572.3, 1674.2 s) bucket
   exactly into the reported ten-second windows.

3. **Live plan amended** (`docs/operations/record-diffing-groundwork.md`):
   - Session flow step 2 now qualifies the victim from the decoded replay
     first ("do NOT default to the player's own entity").
   - New "Victim selection" subsection: the two-command qualification
     (top-victims → hp-delta), the ≥ 2 damage-window requirement, and the
     concrete Oasis Palms schedule.
   - **Oasis Palms victim 3760578** — 9 events / 4,028 damage across
     windows 900–910, 1000–1010, 1070–1080, 1360–1370, 1430–1440,
     1500–1510, 1560–1570, 1570–1580, 1670–1680 s (of the ~2798 s
     replay), plus 2–3 flat-window control dumps (~500 s, ~2500 s).
     Alternatives: 3760571 (7 hits), 3760574/3760575 (6 hits each;
     3760575 late, 2454–2740 s).
   - **Simulation reading:** the extractor's `--hp-delta` survival sim at
     `target=0` is the flat-window pass rate (0.9464 → 0.76/0.58/0.44
     survival over 5/10/15 rounds) — a single-target rolling delta
     campaign sheds the true HP field in any round whose window contains
     a hit. The per-window `HpDamageCorrelator` (already built, tested,
     Lenient mode) is the right tool; the plan already uses it.

## Status

- HP discovery offline side: **complete** (ground truth, correlator
  Strict/Lenient, compose proof, entity-base exposure, entity-record
  chain mechanism) and now with a **real-data-qualified live plan**.
- Remaining step is still the gated live session: one bounded
  `EntityRecordRegionReadRequest` addition + one session on Oasis Palms
  tracking victim **3760578**, dumps concentrated on the nine hit
  windows above. Second independent replay for the Phase-4 repeatability
  rule also qualified: **Dead Rail** victim **2549399** (18 events /
  4,647 dmg, 12 windows at 1140–1530s). Both victims ≥ 2 damage
  windows, so the two-replay verdict contract is fully pre-staged.
- Published tables untouched; resolver + read surface untouched;
  validator unchanged.

## Gates

- `python scripts/python/replay-delta-extractor.py --top-victims` and
  `--hp-delta` run clean on the real DB; numbers cross-checked against
  direct SQL.
- `scripts/validate.ps1` exit 0 (all 12 test projects green).
- `offline_check.py --refresh` — file tree clean.
