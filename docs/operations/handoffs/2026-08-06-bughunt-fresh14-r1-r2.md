# Handoff — FRESH14 solo-path bug hunt (rounds 1–2 of 10, INTERRUPTED)

Date: 2026-08-06 · Status: Findings documented, fixes NOT applied · Type: bug hunt (in progress)
Prior state: FRESH14 solo-survivor arming path shipped at `b80d28f` (write-trace
`-SoloAddress` mode, od-048 solo-family emission, autoloop plumbing, harness,
probe, fixtures, plan doc, roadmap note).

## Why this handoff exists

A 10-round bug hunt on the freshly-committed FRESH14 code was started. Rounds
1 and 2 completed with **11 findings (2 high, 1 medium, 5 low, 3 minor)**;
Round 3 aborted on the harness/probe surface. The user stopped the loop to
preserve progress. **No fixes have been applied** — the working tree is clean
at `b80d28f`. This handoff captures every finding so the next session can
apply them without re-hunting.

## Hunt scope

The FRESH14 solo-survivor arming path only (not the whole repo — that was
hunted to clean in the 20-round pass):

- `scripts/x64dbg-write-trace.ps1` — `-SoloAddress`/`-SoloAxis`/`-SoloScore`/
  `-SoloBandMinSeconds`/`-SoloBandMaxSeconds` params; `solo` input mode;
  synthesized single-member family; `Test-UsableFamily` ≥1-member relaxation;
  `Select-BestFamily` tiers; `Get-FamilyArmPlan`; liveness coverage.
- `scripts/od-048-monitor-correlate-session.ps1` — `Get-SurvivorBandWidth`;
  the solo-family emission block; the auto-trace ≥1-member gate
  (`$anyGe1MemberFamily`, `no_family_with_members`); report `solo`/
  `soloFamilyEmitted` serialization; verdict ordering.
- `tmpwotb-e2e/` — `test-solo-emission.ps1` (AST-extraction harness),
  `probe-autoloop-splat.ps1`, fixtures, `od-049-autoloop.ps1` splat plumbing.

## Findings — ROUND 1 (write-trace solo mode) — all PENDING

1. **`[double]::MinValue` sentinel is fragile (write-trace solo synth).**
   `if ($SoloBandMinSeconds -ne [double]::MinValue)` silently treats a
   caller-supplied `-1.7976931348623157E+308` as "not provided". Fix: use
   `$PSBoundParameters.ContainsKey('SoloBandMinSeconds')` (same for
   `SoloBandMaxSeconds`).
2. **One band bound, other not → unhelpful refusal.** `-SoloBandMinSeconds -10`
   without max yields `bandMax=$null` → band-unknown → generic
   `FAILED_family_selection no_family_clears_floors` exit 2. A live round
   misreads this as weak evidence when the fix is a missing argument. Fix:
   emit an explicit "band requires both min and max" diagnostic when exactly
   one of the two is bound.
3. **Both `-FamilyFile` and `-SoloAddress` set → family silently wins.**
   Mode resolution gives no warning. Fix: `Write-Wt
   'WARN_solo_ignored_family_file_takes_priority'`.
4. **`Test-FamilyScored` returns `$true` for an empty `members` array**
   (pre-existing, unclosed): `foreach` over `@()` never runs; combined with
   `Select-BestFamily` tier 3 (no member-count guard), an empty-members
   family from a file can be "selected" then die at `Get-FamilyArmPlan` with
   the misleading `FAILED_family_no_armed_members`. Not reachable via solo
   (always 1 member). Fix: `if ($members.Count -lt 1) { return $false }`.
5. **Unguarded `$bestSolo.axis/.sign/.shiftSeconds` in the od-048 emission.**
   Endpoint always emits them so low risk, but the hardened repo convention
   (rounds 8/9/14) is guarded `PSObject.Properties[...]` access. A wire
   regression would throw after the correlate window is spent.

## Findings — ROUND 2 (od-048 emission) — all PENDING

1. **HIGH — unguarded `axis`/`sign`/`shiftSeconds` in the solo-member
   synthesis (same crash class as the `score` access already fixed).** The
   emission only guards `score`; the synthesis then reads `$bestSolo.axis`,
   `$bestSolo.sign`, `$bestSolo.shiftSeconds` directly (and
   `axesCovered = @($bestSolo.axis)`). Under `Set-StrictMode -Version
   Latest`, a correlate result missing `axis` throws
   PropertyNotFoundException **after the correlate window is spent**. Fix:
   guard all three; if `axis` is missing, `continue` (skip the survivor);
   null-default `sign`/`shiftSeconds` (evidence-first).
2. **HIGH (consistency) — `family_solo_emitted address=…` puts the address
   on stdout.** This milestone's write-trace fix removed the address from its
   own stdout ("addresses never enter stdout"), but od-048's
   `Write-Od048 ('family_solo_emitted address=' + $bestSolo.address + …)`
   still prints it — and the autoloop wrapper pipes od-048's stdout
   (`*>&1 | ForEach-Object { Write-Log … }`) to the terminal + its log. The
   two halves of the same feature disagree on the privacy boundary. Fix: drop
   the address from the log line (keep axis/score/band; the address lives in
   the report file).
3. **MEDIUM — no "why no solo emitted" diagnostics.** If `$bestSolo` ends
   null (all strong survivors degenerate, bandless, below score floor, or
   already in families), the operator gets only the generic
   `family_mapping_failed` and cannot tell which filter rejected the
   survivors. Fix: when `$strongSurvivors.Count -gt 0 -and $null -eq
   $bestSolo`, log `solo_none_emitted survivors=N` with per-filter counts
   (score/band/already-member).
4. **LOW — negative band width passes the floor.** `Get-SurvivorBandWidth`
   returns `$maxB - $minB`; an inverted pair (host bug) yields a negative
   width, which is `≤` any floor and would emit a "tight" solo. C# scorer
   never emits inverted bands, so theoretical — but `if ($width -lt 0) {
   return $null }` makes the gate fail-closed.
5. **LOW — `soloFamilyEmitted` doc vs switch-off runs.** The report field
   comment says "IS armable by the auto-trace", but emission runs
   unconditionally, so a run without `-AutoWriteTraceOnVerdict` reports
   `soloFamilyEmitted: true` with nobody consuming it. Harmless — reword the
   comment to "would be armable" or note it.
6. **LOW (test-only) — harness brace-depth extraction.** The depth scanner
   counts braces inside string literals; works today (block braces are
   balanced per-line) but breaks silently if a future edit adds an
   unbalanced brace inside a string. Consider anchoring on a fixed closing
   line. Not a runtime bug.

## Verified clean (both rounds — no change needed)

- StrictMode safety of `PSObject.Properties['key']` lookups in
  `Get-MemberBandWidth`/`Test-FamilyScored` (returns null, no throw).
- Solo mode → writeSize 4 → liveness mapping in the write-trace.
- The ≥1-member relaxation introduces no new edge-aligned arm path (tier 3
  pre-existed without count checks).
- od-048 emitted-shape round-trip through the report writer
  (`shiftMinSeconds` → `shiftBandMinSeconds` serialization) is correct.
- Emission-before-verdict ordering: `family_mapping_failed` correctly does
  not fire when a solo family was appended (it tests `$families.Count -eq 0`
  after emission).
- `alreadyMember` check is `-ieq` case-insensitive and runs against the
  pre-append families (no self-duplicate).
- Report is written after emission, so the solo family is in the file before
  the auto-trace reads it.
- Auto-trace gate band check falls through `shiftBandMin/Max` →
  `shiftMin/Max` and reads the solo member correctly.
- `-AutoTraceMinMemberScore 0` semantics are consistent end-to-end (emission
  passes everything, the write-trace splat carries the same 0).
- `-AutoTraceMaxMemberBandSeconds 0` semantics consistent (band-unknown
  survivor skipped at emission when floor > 0; armed when floor 0).

## Status / next steps

1. **Apply Round 1 findings 1–5 and Round 2 findings 1–4** (the two HIGHs
   first: guarded axis/sign/shiftSeconds, address off the log line). Round 2
   items 5–6 are cosmetic/test-only — apply or whitelist with rationale.
2. **Re-run Round 3** (harness/probe/fixtures surface — aborted last time),
   then rounds 4–10 as the user's "repeat until no bugs remain" requested.
3. **Re-validate after each fix batch:** `test-solo-emission.ps1`,
   `probe-autoloop-splat.ps1`, the 8-case DryRun matrix (T1–T8), PS 5.1 +
   pwsh 7 parse, PSSA gate (baseline 40 warnings), ASCII.
4. After the loop is clean: full gate (`dotnet build` + tests) and a FRESH14
   live-round readiness note.

## Evidence

- HEAD `b80d28f` (FRESH14 milestone) — the hunted surface.
- Reviewer output from rounds 1–2 (this session) — findings verbatim above.
- Working tree clean; no fixes applied yet.
