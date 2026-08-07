# OD M2 — FRESH41: sample-grid hypothesis tested live and REFUTED (2026-08-07)

**Session outcome:** the changed hypothesis from OD-RECOVERY-048 (sharpen the
sample grid so the correlation score quantizes finer) was **implemented, run
live, and the data refutes it**. The finer grid worked mechanically — the
monitor ran **27 rounds (vs 15–16)** with **589 addresses scored and 15,314
total samples (≈2× FRESH40's 7,364)** — but the top correlation score held at
**0.846 (11/13)**, not the predicted 14/16. **No family ≥ 0.9 → auto-trace
SKIPPED → source-arm still never armed.** Clean run (`launch_exit=0`, game
stopped, no stale processes).

## The numbers (all five recent rounds, same replay, same axis class)

| Run | Grid | Rounds | Addresses | Samples | Top score | Verdict |
|---|---|---|---|---|---|---|
| FRESH39 a1 | 2.0s | — | — | — | 0.800 | no family |
| FRESH39 a2 | 2.0s | — | — | — | 0.867 | no family |
| FRESH39 a3 | 2.0s | — | — | — | 0.800 | no family |
| FRESH40 | 2.0s | 15 | 526 | 7,364 | 0.857 (6/7) | no family |
| **FRESH41** | **1.0s** | **27** | **589** | **15,314** | **0.846 (11/13)** | no family |

**The score quantization hypothesis is refuted by live evidence:** doubling the
sample count (7,364 → 15,314) and nearly doubling the rounds (15 → 27) held the
top score at ~0.85 (11/13 ≈ 6/7). The ~0.85 cap is the viewpoint axis's
**inherent run-to-run correlation**, not a sampling artifact.

## Why the top survivor failed despite 2× samples

The top three FRESH41 z survivors (0x23A56AD0 / 0x23AE6DD0 / 0x23B163D0, all
score 0.846) carry **~45s-wide ambiguity bands (6 → 51.5/52s)** — they match the
z trajectory at many shifts, i.e. low discrimination. FRESH37's hit (0.933,
axis=x) had a **6.5s-wide band** (55–61.5s). The scorer needs a *tight-band,
high-score* survivor; today's z staging produced wide-band ~0.85 copies, and the
x top (0x236E5510, 0.769) was far below floor. This is the same
run-to-run axis/address-class variance FRESH37's own handoff documented ("the
same config hit at score 1.0 — pure variance").

## Ledger rule applied — do NOT burn another identical live round

Two changed hypotheses have now been tested and both produced honest negatives
(proven invocation ×4 at 0.8–0.867; sample-grid ×1 at 0.846). Per the ledger's
"do not repeat without a changed hypothesis" rule, **no further live rounds on
this replay with the current scoring setup**. The next moves are offline:

1. **Offline score-distribution analysis**: aggregate all five rounds' strong
   survivors (scores, bands, axes, spans) to test whether the 0.9 solo floor
   sits inside the natural score distribution (~0.77–0.93) — if so, the floor
   itself (or the emission selection) needs an evidence-based retune, not
   another roll.
2. **Band-width vs score study**: FRESH37's hit paired 0.933 with a 6.5s band;
   today's 0.846 survivors carry 45s bands. A band-weighted emission selector
   (prefer tight-band survivors over raw score-max) is a plausible offline-testable
   change — the FRESH22 floor already exists for this class but the *selection*
   still ranks by score.
3. **Content gap stays**: `independentReplays` still 0 (only one 11.19.0 replay
   locally) — BLK-0019 unchanged.

## Files

- M1 report: `.data/od-049-fresh41-sourcearm-result.json` (runtime, gitignored)
- Driver log: `.data/od-049-fresh41-sourcearm.log` (runtime, gitignored)
- Ledger: `OD-RECOVERY-049` (index row + result section)
