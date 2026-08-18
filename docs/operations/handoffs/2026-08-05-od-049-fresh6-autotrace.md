# OD-049 FRESH6: M2 family mapping works; auto-trace arg-splat + selection fixed

Date: 2026-08-05 · Campaign: OD-049 · Session: `019fd32e-b167-78fc-97c9-47417a2e9081`
Replay: medvedkovo (surviving battle B), replay start 18:28:22Z, battle end ~18:32:53Z
Report: `.data/od-049-fresh-result.json` (immutable; written 2026-08-05T18:31:22Z)

## What FRESH6 proved (the bug-hunt payoff)

The 10-round bug hunt (commit `d14d083`) landed, and the next live launch
confirmed every fix end-to-end:

- `family_refined round=10 survivors=25 neighbors_added=169` — FRESH5 scored 0
  survivors at round 10 and self-terminated; FRESH6 retried and staged the
  ±16-byte neighbors. Monitored set grew 3000 → 3169.
- Final correlate: `results=1129`, `resultsByAxis {x:1062, y:30, z:37}`,
  `strongByAxis {x:4, y:1, z:3}` — y/z members are finally being scored
  (FRESH5 was 100% x).
- `families=188` (FRESH5: 0). **8 strong survivors (score ≥ 0.7, non-edge)**
  at the true ~6 s load-latency shift.
- Verdict `evidence-strong`; auto-write-trace **invoked in-process** (choreography 7
  worked — the same-launch glue did not burn a session).

## Finding: the live struct is `[x][z]` adjacent — not `[x][y][z]`

Entity 2549405 (tank A, participant `019fd32e-b172-…`): x span 88 m + z span 212 m,
both score 1.0 at shift 6–6.5 s, edge=False, 70/70 samples. The pairs sit at
**4-byte offsets** — z at +4, not +8:

| address | off | axis | score | shift |
|---------|-----|------|-------|-------|
| 0x29D957DC | +0 | x | 1.000 | 6.0 |
| 0x29D957E0 | +4 | z | 1.000 | 6.5 |
| 0x2BD8C450 | +0 | x | 1.000 | 6.5 |
| 0x2BD8C454 | +4 | z | 1.000 | 6.5 |
| 0x2BD8C57C | +0 | x | 1.000 | 6.0 |
| 0x2BD8C584 | +8 | z | 1.000 | 6.0 |

This is a **2D ground-position vector** (minimap-style `[x][z]`), not a 3D
world vector. World-y lives elsewhere: entity 2549401 (tank B) scored y-only
survivors at `0x1FA6B438/0x1FA6B638` (49–53 m y movement, score 0.8) with no
x/z within 16 bytes.

**Consequence:** the `family-complete` verdict (clean x/y/z triple) is
*unreachable for this struct type* — the `[x][z]` pair IS the usable evidence.
The gate already accepts 2-member families, and the axis-count ranking below
now picks them. The write-trace on x+z still reveals the write sites and the
surrounding struct layout; the operator learns whether the y field sits at
+8/+12 or is a separate allocation from the traced code.

## Bugs found + fixed this pass

### 1. P0 — array-splat misbinding broke the live auto-trace
Live FRESH6 log: `auto_write_trace THREW Cannot convert … parameter
'TraceSeconds'. Cannot convert value "…od-049-fresh-result.json"` — the
`-Name value` array splat misaligned after the `-AutoWriteTrace` switch and
shoved the FamilyFile path into `-TraceSeconds`. Reproduced in a minimal
repro (array splat throws; hashtable splat binds correctly). **Fix:** od-048
now invokes with a **hashtable splat** (`& $wtScript @wtArgs`), immune to the
shift. Dry-run against the FRESH6 report: `HASHTABLE OK exit=0`.

### 2. P1 — family selection armed the wrong (decoy) family
`Select-BestFamily` (x64dbg-write-trace.ps1) ranked by **summed** member score
with no edge filter, so:
- the 5-member **all-edge** decoy `0x233CCC08` (shift 28–30, scores ~0.45,
  sum 2.20) beat the real x/z pair (2.00) — fabricated alignment;
- even after the all-edge filter, the 5-member weak x-run `0x2387CA48`
  (scores ~0.4, sum 2.16) still beat the perfect 1.0/1.0 x/z pair (2.00).

**Fix (two rules):**
1. `Test-UsableFamily` gate — ≥2 members and ≥1 non-edge-aligned member;
   all-edge families are excluded (mirrors the od-048 M2 stop rule).
2. Rank = **distinct axis count desc, then mean member score desc** (not sum).
   A family reproducing multiple components of one entity is coordinate-vector
   evidence; a same-axis run is a copy buffer, and member count must not mask
   quality.

**Validation:** dry-run now arms `bph 0x29D957DC,w,4` + `bph 0x29D957E0,w,4` —
the genuine sibling pair (family `axes=x,z`, 2 members, both score 1.0).

## Gate status

- Parse clean (PS 5.1 + pwsh 7); ASCII-clean; PSScriptAnalyzer gate PASSED.
- No C# changes this pass (C# side unchanged since `d14d083`; no test rerun
  needed — full suite passed at `d14d083`).

## Next steps

1. **FRESH7 live** — the full M1→M2 auto-trace end-to-end: with the splat fix,
   the write-trace runs *in the same launch* and arms the real x/z pair while
   the battle tail window is still green. Expect `auto_write_trace exit=0`
   + `od-048-autotrace-*.json` with per-member hit sites.
2. **M2 write-trace analysis** — the traced RIPs at 0x29D957DC/0x29D957E0 map
   the position write sites → instruction-level layout of the `[x][z]` struct
   and (from the ±4 window reads) whether y is a sibling field.
3. If the auto-trace fires but yields no hits: verify `SkipPlayProbe`/liveness
   (exit 8 = stale family), and the play-state probe's green-window timing.
4. Roadmap: mark M1 "family mapping proven live" and update the choreography
   doc with the observed end-to-end timing (gate → stage ~15 s → refine round
   10 → correlate round 70 → auto-trace).
