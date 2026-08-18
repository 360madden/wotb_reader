# Handoff — FRESH18 post-mortem + FRESH19 fixes (2026-08-06)

**Campaign:** FRESH18 live (od-049-autoloop -AttachSmokeOnFirstRound -StageViewpointOnly),
medvedkovo replay `a9aed046…`, commit prior to this handoff: `8e669a2`.

## What FRESH18 proved (the launch stack is now reliable)

| Step | Result |
|---|---|
| resize_window | ✅ 1040x807 → 640x360 at (0,0), first try |
| click | ✅ single SendInput click, blob 2153px, `watch_exit=0` |
| gate + marker | ✅ `marker_found`, gate verified |
| staging gate | ✅ `elapsed_s=55.1 tick_est=50813139` (5.08s — the FRESH18 fix, was 56s) |
| attach-smoke | ✅ `pause=True bpm=yes resume=True`, compensation 6s |
| exit | 0, game left running |

## The correlate verdict (still refused, but now precisely characterized)

- **y axis: 0.986 @ shift 0 (69/70) but band [−19.5..+30] — 49.5s wide.** This is the
  weak-discriminator class: height on a flat map matches the trajectory over a huge
  shift range, so the gate (correctly) refuses it.
- **z axis: span 275, best −18.5 riding the −30 sweep edge.** The true shift is
  **beyond** the old ±30s sweep. Because y's wide band *includes* −19.5, all axes
  agree the true shift is ≈ −20…−30s — i.e. the real match-begin was
  marker + ~70–80s, **not** marker + 50. Attendance is per-replay; 50s was an
  under-estimate for this replay.
- **173 viewpoint results** (was 12 in FRESH15j) — staging quality is fixed. The
  field-class addresses are in the set; the scorer just can't reach the true shift.

## Bug 1 (root-caused): 3 mid-run 401 capability failures

`RendezvousPublisher.PublishAsync` calls **`security.Rotate()` on every ≥15s
publish** — the capability token changes every publish cycle, so any driver
holding a token older than one cycle 401s with `web.local_capability_required`
even while the host is healthy. The existing per-round `Refresh-Rendezvous`
re-read loses the race between rotation (in-memory) and the next publish write
(≤15s later). The 3 dropped rounds left holes in every series, widening the
ambiguity bands.

**Fix (od-048 `Invoke-Api`):** on 401, re-read the rendezvous record and retry
the same call with the fresh token — up to 5 attempts, 2s apart. Non-401 errors
still fail through immediately (fail-closed preserved).

**Validation:** `probe-capability-retry.ps1` proves the mechanism against the
live host on pwsh 7.6 + PS 5.1: stale token → 401 (FRESH18's exact failure
reproduced), re-read retry → gate passed (400 from validation, not 401).

## Bug 2 (root-caused): sweep can't reach the true shift

`-MaxTimeShiftSeconds` default was 30; the true shift here is ≈ −20…−30s+.
Every result was pinned at the edge → `evidence-edge-aligned` → refused by the
band-floor gate.

**Fix:** default 30 → **90** (od-048 + autoloop passthrough). The scorer can now
reach the true shift and the band-floor gate judges it honestly.

## Bug 3 (root-caused): frozen "Not Responding" roster after the run

The offline replay viewer **auto-loops** after the battle ends. The second
LoadGameScene reload hits the **OD-044-class flake** (game reaches LoadGameScene
then dies/freezes ~2s later — here it froze at the roster screen instead of
exiting). Evidence: blitz log silent from 14:23:50 (packet-gap warning = smoke
moment), battle played through sampling (spans 33–275 prove movement), battle
ended ~10:28:17, screenshot at ~10:28 shows the frozen roster.

**Fix (autoloop):** stop the game after the campaign loop concludes (the
auto-trace, if any, already ran in-process inside M1 before this point).
`-KeepGame` opts out.

## FRESH19 prediction

With the widened sweep, the already-staged field-class addresses should report a
**narrow, non-edge band at the true shift** (≈ −20…−50s) → strong survivor →
solo arming → auto-trace. Success criterion: score ≥ 0.9 at the non-edge shift
with band width < 15s, then `odwt-*.bin` hit report. The remaining live-only
unknowns: whether the solo arming finds the true field among the decoys, and the
write-trace's memory-BP hits.

## Files changed

- `scripts/od-048-monitor-correlate-session.ps1` — 401 retry in `Invoke-Api`,
  `MaxTimeShiftSeconds` default 30 → 90
- `tmpwotb-e2e/od-049-autoloop.ps1` — `MaxTimeShiftSeconds` passthrough,
  `-KeepGame`, game stop after campaign
- `tmpwotb-e2e/probe-capability-retry.ps1` — new: 401-retry mechanism probe

**Validated:** parse pwsh 7.6 + PS 5.1 ✅, PSSA gate at baseline (0 findings in
edited files) ✅, ASCII clean ✅, capability-retry probe PASS both engines ✅.
Frozen game killed after the run (cleanup, `GAME_STOPPED`).
