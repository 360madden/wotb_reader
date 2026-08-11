# Handoff — resolver-path consolidation item 6 design: batch N-entity read surface (2026-08-11)

## Summary

Executed the offline half of consolidation item 6 (live-mode alignment):
designed the **batch N-entity region read surface** as a PROPOSAL
(`docs/operations/batch-entity-read-design.md`) — the explicit prerequisite
for item 7 (hardware-atomicity proof), which needs the batch read-surface
design to exist first. No shared contracts changed; no code written. The
single-read seam (`POST /discover/entity-region`) is untouched and stays
back-compat.

## What the design covers

- **Contract shape:** `POST /discover/entity-regions` — `entities[]` (1..16,
  total ≤ 16 KB, reusing the single-read length/anchor validation) +
  optional `battleSessionId`; response carries a whole-batch gate-level
  status, ONE `sameDecodedClockProven` attestation + one `replayTimeSeconds`
  label, and per-entity status/bytes so an unresolved entity never fails the
  frame.
- **Read discipline (coordinator-side):** gate + build identity first (any
  failure fails the whole batch before any read) → resolve ALL entity
  addresses (per-entity unresolved recorded with `failureStage`; a retryable
  `ReplaySessionInactive` fails the WHOLE batch — phase is global) → read
  all regions under one lease → ONE post-read G2 snapshot (≤ 2 s bound over
  the batch, pre-read wall clock recorded) → per-entity results in request
  order.
- **Item-7 hook:** per-entity `ConsistentDoubleRead` travels in the contract;
  the verification window (batch span + double-read span) is designed to be
  added additively under the item-7 evidence flag — nothing to measure until
  the proof starts, so it is deliberately NOT in the proposal yet.
- **Privacy:** unchanged — bytes only, no addresses/pid/module base leave
  the coordinator; `publicProcessAddressesOrRawBytes: false`.
- **Open questions recorded:** observation-promotion deferred (per decision
  log), gate stays host-enforced (contract is gate-agnostic for X1 later),
  entity bound 16 is a safety cap not a target, per-entity time mirrors are
  convenience-only.

## Files touched

- `docs/operations/batch-entity-read-design.md` (new — the proposal)
- `docs/operations/resolver-path-consolidation.md` (item 6 marked
  ✅ DESIGN DONE + cross-link)

## Verification

- Full `scripts/validate.ps1` gate green (925 passed, 3 local opt-in skips,
  0 warnings, 0 errors).
- Docs-only change; file-tree regenerated; no stray files.

## Remaining

- Item 6 rehearsal (dump all roster entities per frame at replay-clock-labeled
  times vs the decoded frame) needs one approved live session — the only
  remaining gate before item 7 (which stays LAST and untouched).
- Implementation of the batch endpoint (coordinator method → web endpoint →
  rehearsal → window measurement) is ordered in the design doc for when that
  session is approved.
