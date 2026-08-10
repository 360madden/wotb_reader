# Handoff — 2026-08-10: L2 facing driver wired + correlator dead-band fix + two-replay yaw rehearsal

**Branch:** `main` — gate green after, tree clean.

## The facing (L2) driver is now code-ready

`scripts/invoke-facing-session.ps1` (new) mirrors the L1 HP driver end-to-end:

1. **QUALIFY** — `replay-delta-extractor.py --yaw-dump [--session]` emits one
   dump pair per TURN SEGMENT (cumulative packet-yaw change ≥ 0.1 rad, merge
   while direction sign is consistent, close on reversal/sample-gap), plus the
   ready-to-paste `yaw-diff` command. Exits 2 unless ≥ 2 turn segments.
2. **DUMP** — with `-LiveAcquire` the driver POSTs
   `/api/v1/game/discover/entity-region` at every scheduled replay-clock time
   with **`regionAnchor=ring-record`** (the yaw candidate lives in the ring
   tail +0x2C..+0x37), plus `-ControlTimes`; fail-closes on
   `sameDecodedClockProven=false`; writes the hp-diff snapshots schema with
   strictly increasing times. No host → exit 3 with the contract.
3. **VERDICT** — `wotbtreader-cli yaw-diff <snapshots> --session <id>
   --victim <entity>`; `-FailOnNoHit` exits 1 on a non-HIT.

## Real bug found in the correlator: the dead band

The first full-schedule rehearsal scored **0.9 (27/30) even with the exact
yaw field at +0x2C** — and the +0x20 decoy "won" only on the offset-ascending
tiebreak. Exact replication showed all 30 windows matching; the misses were
not misses.

**Root cause:** `HeadingCorrelator` classified a window as a TURN when
|expected delta| > 0.02 rad, but the matcher **skips** any window whose
observed |delta| ≤ 0.05 rad tolerance as "unchanged". Windows with expected
delta in (0.02, 0.05] — residual rotations between adjacent turns and at the
control→turn boundary — can never match by construction, yet still counted in
the score denominator. A perfect field could never reach 1.0.

**Fix:** the TURN boundary is now the match tolerance itself
(|expected| > `toleranceRadians`); everything at or below it is a CONTROL
window (flatness denominator). The score now counts only provable turns.
`ControlDeltaThresholdRadians` (0.02) is gone. New regression test
`Correlate_DeadBandWindow_IsControlNotUnmatchableTurn` pins the behavior;
all 9 HeadingCorrelator tests pass.

## Two-replay rehearsal with the real driver schedule

Synthetic ring-record dumps (256 B) with the real nearest-sample packet yaw
at **+0x2C** and a constant (non-tracking) decoy at +0x20:

| Replay | Verdict | Offset | Score | Flatness | Matched |
|---|---|---|---|---|---|
| Oasis Palms | HIT | `+0x2C` | 1.0 | 1.0 | 27/27 |
| Dead Rail | HIT | `+0x2C` | 1.0 | 1.0 | 35/35 |

The Phase-4 two-replay agreement now holds for the facing track at the
synthetic level, matching the HP (14/14) and damage-dealt (5/5) tracks.
`-FailOnNoHit` verified: constant-field fixture exits 1 cleanly.

**Rehearsal gotcha recorded:** a decoy that is a CONSTANT OFFSET of the real
yaw (yaw + 1.7) reproduces every delta and legitimately ties the true field —
the correlator then breaks the tie by offset ascending. Rehearsal decoys must
NOT track yaw; the live session needs no decoy at all.

## Files changed

- `scripts/invoke-facing-session.ps1` — NEW: the L2 facing driver (qualify →
  gated region dump → yaw-diff verdict).
- `scripts/python/replay-delta-extractor.py` — NEW `yaw_dump_schedule()` +
  `--yaw-dump` mode (turn-segment picker, merge-while-same-sign, dump pair
  per segment).
- `src/WotBTreader.Core/Discovery/HeadingCorrelator.cs` — dead-band fix
  (TURN boundary = match tolerance).
- `tests/WotBTreader.Core.Tests/HeadingCorrelatorTests.cs` — dead-band
  regression test (9 tests total).
- `docs/operations/record-diffing-groundwork.md` — correlator semantics,
  rehearsal table (27/27, 35/35), L2 live-session plan driver reference.
- `docs/operations/product-roadmap.md` — F1/Phase-0 numbers.
- `offline/file-tree.md` — regenerated.

## Next

The L2 live session is now code-ready (same gate as L1): the moment the
operator approves, start the web host on the verified Oasis replay and run
`invoke-facing-session.ps1 -SessionId 019fecb0-... -LiveAcquire
-ControlTimes 20,240`. Both L1 (tank-record anchor, HP) and L2 (ring-record
anchor, yaw) share the same seam — one approved live window can cover both
sessions.
