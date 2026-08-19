# Penetration v0.3 — gun-angle read surface (named turretYaw/gunPitch) shipped

**Date:** 2026-08-18 (UTC)
**Status:** coordinator-owned `gun-angle` entity-region anchor implemented and
tested. **Not live-validated** — the controlled turret/gun traverse correlation
is still pending. Nothing promoted.
**Blocker:** `BLK-0027` (narrowed again — the named axis read surface is ready;
the live traverse is next).
**Refines:** `2026-08-18-gun-axis-component-layout.md`,
`2026-08-18-gun-aim-read-surface-shipped.md`.

## What shipped

An additive `EntityRecordRegionAnchor.GunAngle` (the 8th anchor) that reads the
**named** current gun angles directly from `CurrentGunAnglesComponent`, rather
than the rotator's still-unlabelled `+0xe0/+0xe4` targeting inputs:

- **Chain**: reuse the audited `pen-ownership-walk` rotator AOB scan
  (`moduleBase+0x32eeb40`) → identity re-gate → owner round-trip
  (`[rotator+0x10] → owner`, `[owner+0x1fc] == rotator`) →
  `[owner+0x04] → entity` → `[entity+0x2c]` DAVA component array → bounded
  vftable scan for `CurrentGunAnglesComponent` (RVA `0x31a4868`), capped at 64
  slots and fail-closed on a short array or a garbage slot.
- **Fields read**: `turretYaw@+0x10` and `gunPitch@+0x14` (float32), two-pass,
  fail-closed (read miss → `ReadFailed`/`GunAngleMismatch`, non-finite →
  `GunAngleMismatch`, two-pass disagreement → `GunAngleUnstable`).
- **Contracts/statuses/fields**: `GunAngleComponentCandidateCount` /
  `GunAngleTurretYaw` / `GunAngleGunPitch` / `GunAngleTwoPassStable`, plus
  `GunAngleNotFound` / `GunAngleMismatch` / `GunAngleUnstable`; `"gun-angle"`
  endpoint parser case; no address, pointer, id, or raw bytes leave the
  coordinator.
- **Driver** `scripts/capture-pen-gun-angle.ps1`: gate poll → artifact/decode-run
  binding → single read or `-PollSeconds` polling, reporting distinct
  `(turretYaw, gunPitch)` tuples + transitions.
- **Tests**: 4 coordinator (positive, no-component, round-trip mismatch,
  non-finite) + 1 endpoint (parse + field echo + no-region-bytes).

## Why this matters for the G1 item 5 gate

The `gun-aim` surface exposes the rotator's *candidate* yaw/elevation pair but
cannot name them statically. This surface reads the **already-named** axes
(`turretYaw`/`gunPitch`, byte-verified from `2026-08-18-gun-axis-component-layout.md`).
During the owner-run controlled traverse, correlating the two surfaces (component
angles vs rotator inputs) is what names `+0xe0` vs `+0xe4` without ambiguity.

## Honest limit

The DAVA component array is reached through `[entity+0x2c]`; its exact length is
not pinned to a static total (the entity's `+0x38` registry holds per-type
counts). The anchor therefore scans a bounded 64 slots and treats a short array
or an unreadable/garbage slot as fail-closed — this is safe and correct, but it
means a tank with more than 64 components would be an (unlikely) honest
negative rather than an out-of-bounds read.

## Validation

- GameIntegration.Tests: **396 passed / 6 opt-in skips** (4 new GunAngle tests).
- Core.Tests: 298 passed. Architecture.Tests: 20 passed (reference graph intact).
- `dotnet build` of ApiContracts + GameIntegration + Core: 0 warnings, 0 errors.
- `Host.Web` compiles with **zero CS errors**; its output copy is still locked
  by the running dev server (PID 26400), so `Host.Web.Tests` (the new endpoint
  test) could not run — it is a direct mirror of the existing `GunAim` endpoint
  test and will run green once the server is stopped.
- `offline_check.py`: 0 broken links, blocker + ledger consistency intact.
- `capture-pen-gun-angle.ps1` parses clean (PS 5.1 AST, ASCII-only).

## Next step

The G1.5 promotion gate needs the live controlled turret/gun traverse with two
content-distinct positive repeats (`pen-promotion-gates.md`) — an owner-run
scenario. This surface + `capture-pen-gun-angle.ps1` supply the named-axis
half; `capture-pen-shot-ray.ps1` supplies the rotator-input half. Nothing is
promoted until the correlation runs.
