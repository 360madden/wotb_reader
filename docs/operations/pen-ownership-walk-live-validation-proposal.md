# Penetration v0.3 — ownership-walk live-validation phase (shared-contract proposal)

**Date:** 2026-08-16 (UTC)
**Status:** implemented and merged (2026-08-16) — additive `PenOwnershipWalk`
anchor shipped with coordinator + endpoint tests; the live run that consumes
it is still pending (next-10-actions item 1)
**Blocker:** `BLK-0027` (open — this phase is the live half of
next-10-actions item 1)
**Depends on:** `pen-ownership-walk-proof-protocol.md` (static chain, done)

## Goal

Confirm hypothesis H1 live: the single live `VehicleGunRotator`'s `+0x10`
points back to the `AvatarGameLogic` owner whose `+0x1fc` round-trips to the
same rotator, whose `+0x204` is a live `VehicleGun`, and whose `+0x04` is the
viewpoint entity (HP at `+0xB8`, already a Verified chain field). Nothing is
promoted; this is an investigation read, not a product port.

## Recommended shape — a new entity-region anchor

Follow the existing `EntityRecordRegionAnchor.AvatarStats` pattern rather than
touching the frozen capture contract: add one additive anchor that the
coordinator owns entirely. The caller never supplies an address, offset, or
plan; only aggregate booleans/counts leave.

Proposed deltas (all additive):

1. `EntityRecordRegionAnchor.PenOwnershipWalk = 4` — ignores `EntityId`, runs
   the existing gated vftable AOB scan for `moduleBase + 0x32eeb40`
   (`VehicleGunRotator`, exact 4-byte, alignment 4, ≤ 8 candidates), then the
   fixed five-read chain below.
2. `EntityRecordRegionReadRequest` gains `int? OwnershipCandidateIndex` (0..7,
   mirroring `AvatarCandidateIndex`) so a candidate can be selected when the
   scan is ambiguous.
3. `EntityRecordRegionReadResult` gains aggregate-only fields:
   - `int PenOwnershipRotatorCandidateCount`
   - `bool PenOwnershipOwnerPointerReadable`
   - `bool PenOwnershipForwardRoundTripConfirmed` (`[owner+0x1fc] == rotator`)
   - `bool PenOwnershipGunVtableConfirmed` (`[owner+0x204]` and
     `[[owner+0x204]] == moduleBase + 0x32dacf4`)
   - `bool PenOwnershipEntityHpPlausible` (`[[owner+0x04] + 0xB8]` is a
     finite in-range int16 consistent with the decoded viewpoint HP)
   - `bool PenOwnershipTwoPassStable`

No new raw-bytes field is needed; the existing `RegionBytes` stays for the
bounded span the coordinator already redacts. The new fields carry verdicts
and counts only — never addresses, ids, or pointers.

## Read plan (coordinator-owned, fail-closed)

1. AOB-scan Private|Mapped for the rotator vftable; require ≥ 1 candidate,
   else `PenOwnershipWalkNotFound`.
2. `[rotator + 0x10]` → owner; require owner points into Private|Mapped,
   else `OwnerPointerUnreadable`.
3. `[owner + 0x1fc]` == rotator (forward round-trip), else
   `ForwardRoundTripMismatch`.
4. `[owner + 0x204]` → gun; require `[gun + 0x0] == moduleBase + 0x32dacf4`,
   else `GunVtableMismatch`.
5. `[owner + 0x04]` → entity; require `[entity + 0xB8]` (int16) is finite and
   within the decoded viewpoint HP band, else `EntityHpImplausible`.
6. Repeat the whole chain on a second pass and require identical verdicts
   (`TwoPassStable`), else fail closed.

Every failure is a distinct `FailureStage`/status, never a guessed success.
Raw pointers stay inside the coordinator.

## Security / privacy notes for the read-only audit

- The phase reads five fixed-offset words from already-live heap objects; no
  new region beyond the existing bounded lease is exposed.
- No absolute address, module base, process id, entity id, or raw pointer is
  returned; only the aggregate fields above.
- The anchor is loopback- and capability-gated like `AvatarStats`, and the
  coordinator re-validates `OfflineReplayVerified`, artifact/session
  association, decode run, process identity, and exact build first.
- Fail-closed ordering means a torn or hostile read can only lower the
  verdict, never fabricate `Confirmed`.
- No log line may carry the resolved pointer values (counts/verdicts only).

## Test plan

Coordinator + endpoint tests mirroring the `AvatarStats` anchor: not-found,
ambiguous-candidate selection, each failure stage, positive round-trip,
two-pass instability → fail closed, and privacy (no address/id/pointer in the
response or log). Plus a DI pin and an architecture boundary check.

## Out of scope (later, owner-gated)

Flipping any `PenetrationCaptureEvidence` flag (e.g. a future
`OwnerChainProven`) is a SEPARATE shared-contract change for after this
investigation passes, and it still does not enable shell/aim/ray fields or a
colored badge. The phase 2–4 semantic field derivation remains after this
validation per the plan ordering.

## Decision and audit record (2026-08-16)

The additive-anchor option was implemented (the smaller, isolated delta above),
with one hardening over the original draft: before walking the rotator's
pointers the coordinator re-reads the object's own vftable under the same
guarded lease (`pen-walk-identity-read` / `pen-walk-identity-mismatch`),
mirroring the `avatar-stats` anchor's "never dereference off an unauthenticated
object" discipline.

Security/privacy audit (lead-performed; no dedicated agent spawn available in
this environment):

- **Loopback/capability:** no new endpoint, listener, or credential file; the
  anchor rides the existing loopback `ReadEntityRegionAsync` surface and the
  existing gated scan authorization, and the coordinator re-validates the gate
  (`IsScanAuthorizationCurrent`) immediately after the walk.
- **Privacy:** the response carries verdict booleans/counts only; `RegionBytes`
  is `null` for this anchor, no address/pointer/id is returned, and the new
  path logs nothing (BLK-0018 discipline).
- **Mutation:** read-only; no write or input path is touched.
- **Fail-closed:** scan miss → `NotFound`; identity re-read miss/mismatch →
  `ReadFailed`/`Mismatch`; any chain read miss or round-trip/gun-vftable/HP
  disagreement → `Mismatch`; two-pass disagreement → `Unstable`. A torn or
  hostile read can only lower the verdict.

One exact-build managed offline replay is still required to actually run the
anchor and confirm H1 live; that live run remains the open gate on item 1.
