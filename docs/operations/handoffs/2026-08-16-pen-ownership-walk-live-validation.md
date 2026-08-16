# Penetration v0.3 — ownership-walk live-validation anchor (implemented + audited)

**Date:** 2026-08-16 (UTC)
**Status:** additive `PenOwnershipWalk` anchor implemented, tested, and
lead-security-audited; the live run that consumes it is still pending
**Blocker:** `BLK-0027` (open — the live run must still confirm H1 before the
phase 2–4 shell/aim/ray field derivation)

## What changed

The shared-contract proposal in
[`pen-ownership-walk-live-validation-proposal.md`](pen-ownership-walk-live-validation-proposal.md)
was implemented as the additive anchor option (the smaller, isolated delta):

- `EntityRecordRegionAnchor.PenOwnershipWalk = 4` + the hash-bound weapon
  constants (`VehicleGunRotator` vftable `0x32eeb40`, `VehicleGun` vftable
  `0x32dacf4`, and the `+0x10`/`+0x1fc`/`+0x204`/`+0x04`/`+0xB8` chain
  offsets) on `EntityRecordRegionReadRequest`.
- `Type10EntityPositionStatus` gains `PenOwnershipWalkNotFound` /
  `PenOwnershipWalkMismatch` / `PenOwnershipWalkUnstable`.
- The coordinator's `ResolvePenOwnershipWalkAsync` runs the existing gated
  vftable AOB scan for the unique rotator (≤ 8 candidates), then the fixed
  five-read chain twice, fail-closed. `RegionBytes` stays `null`; only
  aggregate booleans/counts leave the coordinator.
- Endpoint parse (`"pen-ownership-walk"`), `OwnershipCandidateIndex`, and six
  aggregate response fields.

## Hardening over the draft

Before dereferencing any rotator pointer the coordinator re-reads the object's
own vftable under the same guarded lease (`pen-walk-identity-read` /
`pen-walk-identity-mismatch`), mirroring the `avatar-stats` anchor's "never
dereference off an unauthenticated object" discipline.

## Security/privacy audit (lead-performed)

No dedicated agent spawn is available in this environment, so the audit was
performed by the lead rather than a `security_auditor` role; the findings are
recorded in the proposal doc. Lanes:

- **Loopback/capability:** no new endpoint, listener, or credential file; the
  anchor rides the existing loopback `ReadEntityRegionAsync` surface and the
  existing gated scan authorization, and the gate is re-validated
  (`IsScanAuthorizationCurrent`) immediately after the walk.
- **Privacy:** verdict booleans/counts only; `RegionBytes` is `null` for this
  anchor; no address/pointer/id returned; the new path logs nothing
  (BLK-0018 discipline).
- **Mutation:** read-only; no write or input path touched.
- **Fail-closed:** scan miss → `NotFound`; identity re-read miss/mismatch →
  `ReadFailed`/`Mismatch`; any chain read miss or round-trip/gun-vftable/HP
  disagreement → `Mismatch`; two-pass disagreement → `Unstable`. A torn or
  hostile read can only lower the verdict, never fabricate `Confirmed`.

## Tests

- 9 coordinator tests (positive two-pass, not-found, round-trip mismatch, gun
  vftable mismatch, read failure, identity mismatch, two-pass instability,
  candidate selection, invalid index) + 1 endpoint test (anchor parse,
  candidate-index forwarding, aggregate echo, `RegionBase64` null).
- Full `scripts/validate.ps1` gate green (0 warnings/errors; GameIntegration
  374 passed / 6 skipped, Host.Web 190 passed / 1 skipped).

## Remaining (not started)

Run the anchor on one exact-build managed offline replay
(`OfflineReplayVerified`) to confirm H1 live, then adjudicate. Only after that
does the phase 2–4 semantic field derivation (configured gun, loaded shell,
turret yaw, gun elevation, muzzle ray) begin per the plan ordering.
