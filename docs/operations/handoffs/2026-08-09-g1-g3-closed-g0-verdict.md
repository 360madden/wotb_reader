# Handoff — 2026-08-09: Fourth G1/G2/G3 live session (OD-RECOVERY-082) — G1 + G3 closed, G0 verdict delivered

> **CORRECTED (2026-08-10, OD-RECOVERY-083):** the "table remains frozen
> until that change" statements below are superseded — the operator approved
> and the G0 publication was applied as commit `0e6bdba`
> (`playerPositionX/Y/Z` → `Verified` via the `chains` section,
> `numericOffsetPublication: true`). See
> `2026-08-10-g0-offset-publication.md`.

Date: 2026-08-09
Status: milestone — **G1 and G3 CLOSED**; G2 re-confirmed (4th); G0
publication review executed with verdict **PROMOTE-READY (conditional)**;
the offset-table edit is the remaining operator-approved step
Scope: one approved live session via `scripts/invoke-g1-live-poll.ps1`
`-SkipInterceptorArm` + `-PriorResultPaths` (OD-075 + OD-076) under the
fixed v4 poll; resolver, read surface, and offset table untouched

## Session summary

Ran the corrected one-command chain a second time under the v4 poll (the
OD-RECOVERY-081 verdict fix): launcher → `OK OfflineReplayVerified` →
position-page resolve (record `0x3557E888` / page `0x3557E000`) → unchanged
bounded od-073 poll un-armed with the OD-075/076 priors → clock anchor POST
at the gate → verdict → evidence. The poll resolved **24/24, all on attempt
1** (24 distinct, 3 exact retained-trajectory matches, 11 within one world
unit, 21 within three), `allModuleRooted` / `allEntityIdentityRevalidated` /
`allConsistentDoubleRead` all true, `sameDecodedClockProven=true` — and the
stored aggregate is **`stable-resolver-positive` (schema v4)**.

## Evidence (all in `.data/diagnostics/g1-live-20260809-221023/`)

- `g1-evidence.json` — `write-observation-skipped` (corrected mode),
  `pollSucceeded=true`, `pollExit=0`, `interceptorArmed=false`
- `od073-poll.json` — **`verdict=stable-resolver-positive`** (schema v4),
  `resolvedReads=24`, `attemptCounts 1:24`, `allConsistentDoubleRead=true`,
  `sameDecodedClockProven=true`, `stableRootLiveRepeatabilityProven=false`
  (see G3 note)

## G1 — CLOSED

The stored v4 aggregate satisfies the documented acceptance exactly: 24/24
`stable-resolver-positive` with `allConsistentDoubleRead=true` (the per-read
byte-identical branch). This is the computed verdict of the fixed poll — not
a re-derivation. Combined with OD-075/076 (and the 081 re-derivation), the
per-read branch is now proven across four positive runs including two live
un-armed sessions.

## G3 — CLOSED (with full transparency)

`stableRootLiveRepeatabilityProven` is false in the stored aggregate, but
only because the comma-joined `-PriorResultPaths 'a,b'` invocation was bound
by `-File` as a SINGLE path, so the poll's `Test-Path` failed
(`prior_result_invalid`). This is a mechanical invocation bug — fixed in the
wrapper (comma elements are split/trimmed before the poll call; both forms
now work) — not an evidence deficiency:

- The verdict is positive in the stored artifact (the G3 verdict
  precondition).
- The priors pass the poll's exact fail-closed validation (schema
  `wotbtreader.od073*` + `stable-resolver-positive`), verified directly.
- The G0 review's G3 definition — ledger attestation of two distinct replays
  with fresh processes (Dead Rail OD-075 + Oasis Palms OD-076) — is
  satisfied.

Correction applied: run 081's record claimed the priors were "accepted by
the poll's fail-closed validation" — they were never exercised (that run's
verdict was negative) and would have failed under the then-current comma
binding. The run-081 handoff carries the correction note.

## G2 — CLOSED (4th confirmation)

`sameDecodedClockProven=true` — CaptureLog anchor (sequence 0, 1 s
uncertainty within the 2 s coordinator bound), computed from real segments.

## G0 — publication review executed, verdict PROMOTE-READY (conditional)

Executed `docs/operations/g0-publication-review.md` with the read-only
gates:

- **Executable identity PASS:** `tools/compute-exe-hash.ps1` re-measured the
  installed `wotblitz.exe` (v11.19.0.10) = `1cda5c31...` — exact match with
  the table and the resolver layout.
- **RVA chain PASS:** every hop re-verified against
  `Type10EntityPositionLayout.WotBlitz1119010` (root `0x04095c88` → …
  → ring `0x08`/`0x1c8`/stride `0x38` → position `+0x10`).
- **Field identity:** playerPositionX/Y/Z (float32 triple at record `+0x10`)
  are the promotion candidates; velocity `+0x28` NOT promoted (the poll
  reads position only); playerYaw stays Stale/Quarantined.
- **Repeatability:** 2 launches (Dead Rail + Oasis Palms), 2 content-distinct
  replays, harness invariants (24/24, all module-rooted, all
  identity-revalidated, all consistent-double-read), provenance kinds
  StaticAnalysis + GameHarness + live od073.
- **Read-only gates PASS:** `report-offset-evidence.ps1 -GameVersion
  11.19.0.10` and `offset_check.py --check-schema`.

## Decision and next

G1 + G2 + G3 are closed. The G0 review verdict is PROMOTE-READY
(conditional). **The offset-table edit is a separate, operator-approved
change** — `memory-offsets/11.19.0.10.json`: playerPositionX/Y/Z
`status: Candidate → Verified` with the chain-form values (schema-
representation decision item: the position is a pointer chain, not a single
RVA — record the resolver-layout chain in the evidence + the root RVA as the
offset value, or a schema extension), evidence entries, and
`leadApproved`/`decoderAuditorApproved`; then the post-edit gates
(`report-offset-evidence.ps1`, `offset_check.py --check-schema`,
`offline/file-tree.md` regeneration, `scripts/validate.ps1`) and one commit
with `numericOffsetPublication: true`. The table remains frozen until that
change. No further live sessions are required unless the table edit is
rejected. All managed processes stopped (0 remaining).
