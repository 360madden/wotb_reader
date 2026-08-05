# OD-049 launch-5 post-mortem + M2 family-mapping bug hunt (2026-08-05)

## Launch-5 outcome (FRESH5, 17:44–17:48 UTC)

The tolerance + anchor fixes landed in FRESH5 and worked end-to-end:
reads succeeded for the whole battle (**70 rounds, 210,000 samples**), the
final correlate scored **532 addresses** and produced **44 strong survivors**
(score ≥ 0.7, non-edge-aligned, all axis=x, shift=6s, band [6, 7.5]).

But the M2 step failed: `families=0`, `family_mapping_failed
no_families_from_survivors`, auto-write-trace SKIPPED. One battle was spent
to discover that the mid-battle family refinement silently disabled itself.

## Ground truth verified live (host still running)

The trajectory endpoint confirms ALL THREE axes move for the viewpoint
(entity 2549401: xspan=139.7, **yspan=10.7**, zspan=406.5), so y/z were
scorable — the all-axis-x result was a staging/refinement failure, not a
physics fact.

## Root causes (10-round hunt, 2 independent reviewers confirmed)

### Bug 1 (P0, root cause of families=0): refinement self-termination
`$familyRefined = $true` was set unconditionally after ANY successful
provisional correlate — including a pass that collected **zero** survivors.
At round 10 each series has only ~10 samples; over a ±30s sweep the
ambiguity band is wide and rides the sweep edges, so every candidate was
rejected and the pass logged `survivors=0`. The flag was set anyway, so no
later round ever retried → the ±16-byte neighbors of the 44 eventual
survivors were NEVER staged → y/z siblings never read → families=0.

### Bug 2 (P0, compounding): provisional edge-aligned filter too strict for short series
The provisional survivor loop applied the same `|band edge| ≥ 28s` filter as
the final audit. Short series have wide bands; at round 10 this guarantees a
0-survivor pass (compounding Bug 1). The provisional pass only SEEDS
neighbor staging — the final correlate and the family builder re-audit edge
alignment authoritatively.

### Bug 3 (P1): staging union cap starves y/z and backup entities
Scans add candidates until the GLOBAL union cap (3000). The viewpoint x
scan alone returns ~1500 exact matches; entity z and entities 2–3
contributed ZERO candidates. The y/z sibling fields (which hold different
values than x) can only appear from their own axis scans, so they were never
staged → families impossible regardless of refinement.

### Bug 4 (P1): C# `TryGetValueAtTick` clamped at window edges → fabricated matches
Out-of-window ticks returned the ENDPOINT sample value (a constant). Any
address parked at the tank's spawn position (head clamp) or end position
(tail clamp) scored fabricated matches — the fingerprint of the shift=-28.5
edge-aligned suspects. Fixed: lookup returns false outside the window;
out-of-window samples count in `TotalSamples` but never match.

### Bug 5 (P2): axis tie-break biased toward x
`matches > bestMatch` (strict) let the first-iterated axis (x of the first
entity) win every exact tie. Fixed: ties resolve to the NARROWER ambiguity
band.

## Fixes applied

| File | Change |
|------|--------|
| `scripts/od-048-monitor-correlate-session.ps1` | Refinement retries every `FamilyRefineRetryGapRounds` (5) instead of self-terminating on 0 survivors (`family_refine_deferred`); provisional pass drops the edge-aligned filter; staging per-scan budgets (viewpoint total = MaxStaged − reserve, backup scans capped at fair share) so y/z + backup entities contribute; report gains `resultsByAxis`/`strongByAxis` histograms + refinement `attempts`/`deferred`; auto-trace 2-member fallback requires ≥1 non-edge-aligned member |
| `src/WotBTreader.Core/Discovery/TrajectoryCorrelation.cs` | `TryGetValueAtTick` no longer clamps at window edges (returns false outside); tie-break by narrower ambiguity band; doc note on new match semantics |
| `tests/WotBTreader.Core.Tests/TrajectoryCorrelationScorerTests.cs` | +2 regression tests (parked-at-end, parked-at-spawn must NOT score) |

## Validation

- Core tests: 38/38 pass (incl. 2 new + 18 scorer)
- Host.Web tests: 113/113 pass
- Full solution: 412+ pass, 0 failed, 2 expected opt-in skips
- PS parse: clean on PS 5.1 AND pwsh 7; ASCII clean
- PSScriptAnalyzer gate: PASSED

## Next live run expectation

With the refinement retry + no-edge-filter provisional pass, the mid-battle
correlate should now find survivors by ~round 15–25, stage their ±16-byte
neighbors, and the final correlate should return y/z members → families →
`family-complete` verdict → auto write-trace fires. The staging budget fix
also directly stages y/z candidates from the scans themselves.

## Key lesson (teachable moment)

The M2 "one session maps the whole vector" design was silently dead since the
day the refinement flag was added: a 0-survivor first pass was treated as
success and permanently disabled the mechanism. The live run (one battle)
was the only way to see it. Evidence-first discipline held: nothing was
committed as "proven" — the report is immutable and the verdict stays
`evidence-strong` until a family-complete run exists.
