# Handoff — FRESH20: FIRST strong verdict + auto-trace fires, timing gap found (2026-08-06)

**Campaign:** FRESH20 live (od-049-autoloop -AttachSmokeOnFirstRound -StageViewpointOnly),
session 1 of the re-baselined M1 budget (roadmap Descope gate, 2026-08-06).
HEAD before: `188da92`.

## The milestone

**`verdict=evidence-strong` — the first strong verdict in the campaign.** Five
non-edge viewpoint survivors, zero hard 401s, no crash, clean game-stop.

| Address | Axis | Score | Shift | Band | Span | Match |
|---|---|---|---|---|---|---|
| 0x227AC250 | y | 0.986 | +56.5 | [+56.5..+69.5] | 4.0 | 69/70 |
| 0x22E0A350 | z | 0.886 | −28.5 | [−42.0..−28.5] | 272.8 | 62/70 |
| 0x22CB83D0 | z | 0.871 | −29.5 | [−38.5..−29.5] | 274.0 | 61/70 |
| 0x238EE4D0 | z | 0.857 | −27.5 | [−42.5..−27.5] | 98.5 | 60/70 |
| 0x22BAD850 | z | 0.729 | −31.5 | [−40.5..−31.5] | 270.6 | 51/70 |

**What it proves:** the z survivors are genuine position-field candidates —
span ≈ 270 matches the decoded z trajectory span, 60–62/70 samples reproduce
it, and the **non-edge bands at shift ≈ −28s** confirm the FRESH18 prediction
(attendance was ~78s, not 50). The y@0.986 is the weak-discriminator class
(span 4.0, matches anywhere). The full corrected chain worked: staging gate →
tick 5.97s → **staged=3000** (full set) → smoke green on attempt 1 → 70 rounds,
**zero api_failed** → correlate → evidence-strong.

**Auto-trace fired and injected:** `gate=OfflineReplayVerified` precheck,
family liveness OK (armed=1), x32dbg attach, **`scriptload+scriptrun`
injected on the live family member** — the M2 machinery worked end-to-end for
the first time.

## The gap: trace timing (exit 5, not a science failure)

`STOP_gate=Denied` mid-window, hits=0. The battle ended **11s into the 25s
trace window** (marker 16:06:44 + ~60s attendance + 271s battle ≈ 16:12:15;
trace fired 16:12:04). The delay budget:

| Stage | Time | Note |
|---|---|---|
| 70 sampling rounds | ~160s | 70 × 2s + read overhead |
| Correlate + report | ~46s | dominated by ConvertTo-Json of the 140K-sample body |
| Auto-trace wrapper | ~58s | re-pre-arm (smoke's x32dbg does not survive detach → new launch + 15s window wait) + attach + inject |

**Fix set for FRESH21 (offline, no session):**
1. **Rounds 70 → 50** (autoloop + od-048 defaults) — saves ~40s of sampling.
2. **Adaptive trace window** — od-048 computes `TraceSeconds` from the battle
   tail at invoke time (`battleEndUtc − now − 15s margin`), capped 10–25s,
   instead of the fixed 25s.
3. Optional: shrink the correlate body (server-side downsample or slimmer
   serialization) to cut the ~46s JSON cost.

**Files:** `.data/od-049-fresh20-result.json` (evidence-strong), `.data/od-048-autotrace-20260806-121106.json`
(trace-failed exit 5). Game stopped by the campaign game-stop; leftover
debuggers exited.
