# Viewpoint-first discovery pivot (2026-08-06)

**Track:** offset discovery · **Parent:** `offset-discovery-strategy-v4.md` /
`offset-discovery-roadmap.md` · **Status:** implemented offline, awaiting the
next live replay validation (FRESH15).

## 1. What the active workflow actually does (before this pivot)

The OD-048 pipeline (`scripts/od-048-monitor-correlate-session.ps1`) runs
three phases in one game launch:

1. **Stage** — fetch the decoded trajectory; stage the **viewpoint entity
   first, then the top `StageTopN-1` movers** (default 3 entities). For each
   staged entity, scan the game process once per axis (x/y/z) for Float values
   near the ground-truth sample nearest the expected replay tick
   (tolerance 0.001, union capped at `MaxStaged`). Retry up to 3 attempts
   under a battle-time budget.
2. **Monitor** — re-read the staged set every 2 s via
   `POST /api/v1/game/discover/read`. At round `FamilyRefineAfterRounds`
   (default 10), a **provisional correlate** picks top survivors and adds
   their ±16-byte neighbors to the staged set so one session can map the
   sibling x/y/z components (**family refinement**).
3. **Correlate** — at rounds-exhausted,
   `POST /api/v1/game/discover/correlate` scores every address's value series
   against the decoded trajectories (sign flips, ±30 s shift sweep) and
   returns `results` (each carrying `entityId`, `axis`, `score`,
   `shiftMin/MaxSeconds`) plus `families` (grouped coordinate triples).
   The driver demotes edge-riding shifts to suspect, emits a **solo family**
   from the best lone tight-band survivor (FRESH14), applies score ≥ 0.9 +
   band ≤ 20 s floors, and — on a usable family — immediately invokes
   `x64dbg-write-trace.ps1 -AutoWriteTrace` in the same process.

**Key facts learned from live rounds (FRESH10/12/13):**

- The server scores each address against the **best-matching** entity
  trajectory. Addresses staged from viewpoint ground truth can come back
  matched to a **different entity** (decoys tracking teammate movement).
- The strongest artifact produced was a lone **viewpoint y** survivor
  (`0x1FC57238`, score ≈ 1.0, interior band [−10, −7.5] ≈ 2.5 s) that was
  structurally excluded from every family; FRESH14's solo path made it
  armable, but the pipeline still staged and prioritized other entities and
  still waited on family assembly before tracing.

## 2. What must change for viewpoint-only discovery

Per the pivot request, the discovery must:

1. **Stage only the viewpoint player** — never other entities.
2. **Require only ONE discriminating coordinate** — no complete XYZ family.
3. **Not perform separate full-memory searches for all three axes as a
   prerequisite** — a survivor on any one axis triggers the trace.
4. **Not delay writer tracing to assemble XYZ neighbors** — no family
   refinement mid-battle in viewpoint-only mode.
5. **Not begin by hunting a static pointer chain** — the writer trace
   discovers the object first; the resolver is chosen afterward from the
   evidence.
6. **Exclude alternate-entity/axis matches** — a viewpoint survivor must be a
   *viewpoint* match, not a decoy tracking a teammate.
7. **Trace immediately** once one viewpoint coordinate clears the strong
   evidence gates (score, observation count, movement span, narrow non-edge
   shift band, entity separation, live-address validation).
8. At the writer hit, capture instruction/module RVA, destination address,
   registers, base-register + displacement, and nearby object memory — then
   inspect nearby fields locally to identify the remaining coordinates, and
   finally choose the resolver (module-rooted pointer path, stable object
   relationship, or code signature) from the evidence.

## 3. What was implemented (smallest robust changes)

**`scripts/od-048-monitor-correlate-session.ps1`** — new `-StageViewpointOnly`
switch:

- **Staging:** selects ONLY the `IsViewpoint=true` entity; hard-fails
  (exit 2, `FAILED_no_viewpoint_entity`) when the trajectory has none. The
  viewpoint's x/y/z scans are retained (three chances for one survivor; the
  old cost driver — extra entities × attempts — is gone).
- **Refinement skipped:** the mid-battle provisional correlate +
  ±16-byte neighbor staging is disabled under the switch (no XYZ assembly,
  no correlate-call budget burned; the full series goes to the single final
  correlate).
- **Viewpoint results filter** (`Select-ViewpointResults`): after the final
  correlate, results are restricted to the viewpoint `entityId` BEFORE the
  shift audit — alternate-entity decoys (even at higher score) are excluded
  from every downstream gate.
- **Viewpoint families filter** (`Test-FamilyAllViewpoint`): server-built
  families whose members include addresses outside the viewpoint result set
  are excluded; the solo family is viewpoint-only by construction.
- **Report:** new `viewpointOnly` + `viewpointEntityId` fields.

**`tmpwotb-e2e/od-049-autoloop.ps1`** — `-StageViewpointOnly` pass-through
(hashtable splat, switch added conditionally).

**Evidence gates (unchanged, applied to the viewpoint-scoped set):**
correlation score ≥ 0.9 (`AutoTraceMinMemberScore`), ambiguity band ≤ 20 s
(`AutoTraceMaxMemberBandSeconds`), non-edge alignment, observation count +
movement span from the monitor series (the correlate's `totalSamples`/`span`),
entity separation (the new viewpoint filter), and live-address validation
(the series were read from the live process; the write-trace re-validates
liveness before arming).

## 4. What still requires a live replay validation (FRESH15)

1. `-StageViewpointOnly` actually stages only the viewpoint player (staging
   log shows 1 entity) and the scan finds candidates.
2. `attach_smoke` still passes mid-battle under viewpoint-only staging.
3. The final correlate's viewpoint filter keeps the genuine viewpoint
   survivors and excludes decoys (`viewpoint_only results=N/M` log line).
4. The solo path arms the **viewpoint** survivor (not a teammate's) and the
   auto-trace produces `family-hit` + `odwt-*.bin` write-site proof.
5. The writer evidence (instruction RVA, destination, registers, base +
   displacement, nearby memory) identifies the probable position/transform
   object base; then the local ±N-byte field read maps the sibling
   coordinates.
6. Resolver classification: module-rooted pointer path vs. stable object
   relationship vs. code signature.

## 5. Validation performed (offline)

- `tmpwotb-e2e/test-viewpoint-filter.ps1` harness — AST-extracted REAL
  functions + VERBATIM staging block: entity filter, family filter, empty-set
  handling, viewpoint-only staging, default staging parity, no-viewpoint
  fail-closed (exit 2 in a subprocess).
- Parse gate on PS 5.1 + pwsh 7; PSSA gate (47 warnings = prior tracked
  baseline incl. test-solo-emission.ps1 committed at b80d28f; zero new from
  this change); no-game DryRun with the switch (fail-closed at preflight);
  autoloop splat probe.

**Review fixes applied (same day):** (1) `Test-FamilyAllViewpoint` now guards
`$Family.members` under StrictMode (fail-closed `$false` instead of a crash);
(2) the solo-emission ranking loop now guards `axis`/`sign`/`shiftSeconds`
(bug-hunt R2 HIGH: the member synth read them unguarded); (3) the
`family_solo_emitted` log line no longer prints the address (R2 HIGH privacy
fix) — the evidence payload keeps it. `$familyStaged`/`$familyNeighborAdded`
verified top-level-initialized, so the refinement-skip path is StrictMode-safe.

**Design interpretation (explicit):** "do not perform separate full-memory
searches for all three axes" is read as **don't require all three** — the
viewpoint entity's x/y/z scans are retained as three chances for one survivor;
no single-axis-only scan knob exists. Flag if literal single-axis scanning is
wanted.
