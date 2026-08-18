# FRESH12 + FRESH13 — y-survivor audit (CORRECTED verdict) and band-width floor

Date: 2026-08-06 · Status: FRESH12 Closed (offline audit); FRESH13 Implemented + validated offline · Type: evidence audit + gate hardening

## Question

FRESH10 produced one strong y survivor: `0x1FC57238`, y@1.00 (69/69), shift
−7.5s, which the FRESH10 writeup demoted as "edge-riding / bad-anchor
signature." Is the wall anchor off (the shift rides the sweep edge because the
true alignment is beyond the bound), or is the address real evidence?

## Answer: the address is real evidence. The FRESH10 label was a misreading.

The decisive facts, from the FRESH10 report and the live host DB (session
`019fd4c1`, entity 2549406, medvedkovo):

| Address | Axis | Score | ObsSpan | Shift | Ambiguity band | EdgeAligned |
|---|---|---|---|---|---|---|
| `0x1FC57238` | y | 1.000 (69/69) | 2.8 | −7.5s | **[−10, −8] (2.5s, interior)** | **False** |
| `0x22FC05D8` (armed!) | y | 1.000 (69/69) | 4.0 | 0.0s | [−10, +30] (40s, degenerate) | True |
| 40 other y results | y | 0.986–1.0 | varied | varied | 20–60s (degenerate) | mostly True |

**How to read the ambiguity band** (from `TrajectoryCorrelation.cs`): the band
is the set of shifts that achieve the max match count. Band width ≈
tolerance / |local ground slope|. Entity 2549406's y moves only 10.9 units
over 278.5s (slope ~0.04 u/s) → width = 6.0 / 0.04 ≈ 150s → clamped to the
whole ±30 sweep. **A near-flat ground axis makes y@~1.0 cheap**: any address
whose series sits within tolerance of the y band matches at every shift, and
the scorer reports it at 1.0 with a full-sweep band.

`0x1FC57238` is the **only result in the run with a tight band (≤6s) AND not
edge-aligned**. A 2.5s interior band 18s inside the ±30 sweep means its
observed series reproduces the y shape at exactly one alignment — this is the
single most discriminating artifact the pipeline has produced. The FRESH10
"shift −7.5 sits on band edge [−10,−7.5]" phrasing confused the *ambiguity
band* (the −10 edge) with the *sweep edge* (±28 threshold). The address
survives every discriminator this pipeline has.

## Why FRESH10 armed the wrong family and hit zero

- The armed family was `0x22FC05D4` (x@0.20, noise) + `0x22FC05D8` (y@1.00) —
  and the y member's band is **degenerate** [−10,+30]: it matches at any
  shift, so its 1.0 proves nothing about being a written coordinate.
- The report's family members serialize **no band fields** (all `[0,0]` on the
  wire), so the discrimination information is invisible to the auto-trace gate.
- `0x1FC57238` wasn't even **in** a family: its ±16-byte neighbors scored
  below the family-seed floor, so the builder never grouped it, and the
  auto-trace can only arm family members. The best evidence in the run was
  structurally excluded.

So FRESH10's hits=0 is fully explained: the trace armed degenerate members, not
the genuine survivor. The FRESH11 score floor prevents *noise* members (x@0.20)
but a *degenerate* member still scores 1.0 and passes.

## Implication for the pipeline

- **Anchor: fine.** The tight interior band at −7.5s is a plausible
  load-latency offset, not a boundary-riding symptom.
- **The near-flat-y degeneracy is a real pipeline weakness.** 42 of 50 results
  were y-axis mostly because y is nearly flat for these entities. The y axis
  needs a discrimination-aware treatment (or the family gate needs a band
  width filter) or it keeps flooding the evidence with meaningless 1.0s.

## FRESH13 plan (next)

1. **Band-width floor in the family gate** (both od-048 and write-trace): a
   member whose ambiguity band covers more than ~1/3 of the sweep is
   degenerate regardless of score — refuse it. This kills the 30/50
   degenerate results and would have refused FRESH10's armed y member.
2. **Serialize shiftMin/shiftMax per family member** in the report and
   family JSON so the gate can apply the floor (currently `[0,0]`).
3. **Solo-survivor arming path**: allow a tight-band non-edge high-score
   address (like `0x1FC57238`) to be traced even without a byte-window
   family — it is the strongest evidence and currently un-armable.
4. Optional: raise the y-axis `MinMovingSpan`-style discrimination for
   near-flat ground axes in the C# scorer so they don't dominate results.

## FRESH13 — band-width floor (implemented 2026-08-06, `docs/operations/offset-discovery-ledger.md` FRESH13 entry)

Both gates now refuse a family whose member's ambiguity band is unknown or
wider than the floor (default 20s = 1/3 of the ±30s sweep):

- `x64dbg-write-trace.ps1`: `-MaxMemberBandSeconds` + `Get-MemberBandWidth`
  (accepts both wire pairs) + `Test-FamilyBanded`, wired into
  `Test-UsableFamily` and every `Select-BestFamily` tier; `.family.json`
  output carries the band per member.
- `od-048`: `-AutoTraceMaxMemberBandSeconds` + widest-band gate check
  (`degenerate_member_band` / `member_band_unknown` skip reasons),
  best-near-miss band in the skip log, floor passed through the splat so
  both gates agree.

Validated offline: the real FRESH10 report is refused (exit 2); a band-only
fixture (scores 0.98/1.0 pass the score floor, 40s band) is refused — the
band floor catches what the score floor cannot; bandless wire refused
(fail-closed); floor 0 arms bandless (parity); a synthetic family with
0x1FC57238-style metrics (2s interior band, non-edge) arms (exit 0).

**Caveat (honest):** 0x1FC57238 is a RESULT, not a family member, and both
gates require ≥2 members — the real FRESH10 report still cannot arm it. Its
metrics pass the new floor; arming it standalone requires the solo-survivor
path (next). A future live round must not misread a ≥2-member refusal as the
band floor failing.

## Evidence files

- FRESH10 M1 report: `.data/od-049-fresh10-result.json` (results, families)
- Auto-trace report: `.data/od-048-autotrace-20260805-215207.json` +
  `.family.json`
- Ground truth: live host DB `%LocalAppData%\WotBTreader\treader.db`, session
  `019fd4c1-fd84-7e3b-95a7-56f208d78c04`
- Ledger: `docs/operations/offset-discovery-ledger.md` (FRESH12 entry)
