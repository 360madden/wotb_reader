# Handoff — 2026-08-10: G0 offset-table publication (OD-RECOVERY-083) — playerPositionX/Y/Z published Verified

Date: 2026-08-10
Status: milestone — **the operator-approved G0 publication is applied**;
`numericOffsetPublication: true`; the offset table is no longer frozen
Scope: the operator-approved gate (`docs/operations/g0-operator-checklist.md`)
— apply `docs/operations/g0-offset-table-draft.md`, run the post-edit gates,
record OD-RECOVERY-083, ONE commit. No live session in this record.

## What was applied

- `memory-offsets/schema.json` — new top-level `chains` property (field →
  array of `{kind: rootRva|memberOffset|recordOffset, value, note}` hops);
  `schemaVersion` stays 1 (the runtime reader ignores the additive key).
- `memory-offsets/11.19.0.10.json`:
  - `confidence: "high"`, `discoveredAtUtc` = 2026-08-10T02:55:00Z, `notes`
    + G0 summary.
  - `fieldValidation.playerPositionX/Y/Z` → `status: "Verified"` with the 3
    verification evidence entries APPENDED to the existing OD-004…011
    history (not replaced), `independentProcessLaunches: 4`,
    `independentReplays: 2`, `harnessInvariantsPassed: true`,
    `leadApproved`/`decoderAuditorApproved: true` (operator set at apply).
    **`playerPositionY` got a NEW fieldValidation entry** (it was missing).
  - New `chains` section: X/Y/Z module-relative chains — root RVA
    `0x04095C88` = decimal **67722376**, final hops `recordOffset`
    0x10/0x14/0x18. The chain carries the branching semantics verified
    against the resolver traversal (conditional `CachedEntityOffset 0x48`
    fast path, alternative entity-map roots `[0x1C, 0x40, 0x34]`) plus the
    vtable identity RVAs in the evidence note.
  - `offsets` UNCHANGED — all 0. Chained fields stay 0 **by design**: the
    legacy observation path computes `moduleBase + field.Offset` and the
    ring record is battle-scoped heap; a non-zero value would make that
    path read a bogus address. The resolver reads position via its own
    hash-bound layout and is untouched.
- `offline/memory-offsets.md` — new "Chains (pointer-chain verification)"
  section.
- `scripts/python/offset_check.py` — chains validation was PRE-MERGED
  (commit `7814d27`); no code change this session.

## Post-edit gates (all green, in order)

1. `python scripts/python/offset_check.py --check-schema` — **PASS** "All
   offset files are valid" + `chains validated (3 field(s))`.
2. `tools/report-offset-evidence.ps1 -GameVersion 11.19.0.10` — ran clean
   (position fields still report offset 0 / Unknown there — the report
   counts non-zero offsets as known; the verification lives in
   `fieldValidation` + `chains`, as the draft documented).
3. `python scripts/python/offline_check.py --refresh` — 112/112 links,
   file-tree up to date.
4. `dotnet test … --filter "FullyQualifiedName~ChainedFields"` —
   `ChainedFields_AreExcludedFromObservationReads` **Passed** (chained
   field never read as `moduleBase + 0`; position stays null).
5. `scripts/validate.ps1` — **exit 0** (all test projects, repository scan
   828 tracked files, PSSA baseline 86, links 112/112, ledger 64/78).

## What was NOT done

- No resolver change, no read-surface change, no scan-breadth increase.
- NOT promoted: velocity (`+0x28`), `playerYaw` (stays
  Stale/Quarantined), `replayTime`, `playerHP`, `cameraPitch`,
  `aliveTankCount`.
- No absolute/heap address published anywhere — the chain is
  module-relative documentation + evidence, never a runtime read plan.

## Where things stand

G1 / G2 / G3 closed (OD-RECOVERY-082), G0 review executed (PROMOTE-READY),
publication applied (OD-RECOVERY-083). The legacy observation path still
emits position nulls (chained fields excluded — pinned by the regression
test). Post-publication contract, verification sequence, fail-closed
triggers, and rollback: `docs/operations/g0-post-publication-regression.md`.
No further live sessions are required unless a gate fails.
