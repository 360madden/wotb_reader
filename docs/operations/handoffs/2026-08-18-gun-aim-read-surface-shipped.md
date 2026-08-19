# Penetration v0.3 — gun-aim read surface (G1 item 5) shipped

**Date:** 2026-08-18 (UTC)
**Status:** coordinator-owned `gun-aim` entity-region anchor implemented, tested,
and merged (`4cf7a78`). **Not live-validated** — the controlled turret/gun
traverse correlation (the promotion gate) is still pending. Nothing promoted.
**Blocker:** `BLK-0027` (narrowed — the read surface is ready; the live
controlled turret/gun traverse is next).

## What shipped

- **`EntityRecordRegionAnchor.GunAim`** (the 7th anchor) plus the coordinator
  resolution `ResolveGunAimAsync` / `RunGunAimPassAsync`: reuses the
  already-audited `pen-ownership-walk` rotator scan (`moduleBase + 0x32eeb40`),
  re-gates the rotator's vftable, confirms the owner round-trip
  (`[rotator+0x10] → owner`, `[owner+0x1fc] == rotator`), then reads the two
  per-frame `Update` aim inputs (`+0xe0`/`+0xe4`) and the gun-marker aim struct
  (`+0x28..0x40`: hit xyz, normalized direction, distance). Two passes,
  fail-closed, aggregate-only — no address, pointer, id, or raw bytes leave the
  coordinator.
- **New statuses** `GunAimNotFound` / `GunAimMismatch` / `GunAimUnstable`,
  **new result fields** `GunAimRotatorCandidateCount` /
  `GunAimOwnerRoundTripConfirmed` / `GunAimInput0`/`GunAimInput1` /
  `GunAimHitX`/`Y`/`Z` / `GunAimDirX`/`Y`/`Z` / `GunAimDistance` /
  `GunAimTwoPassStable` (Application + wire contracts), and the `gun-aim`
  anchor string in the endpoint's parser.
- **Driver** `scripts/capture-pen-shot-ray.ps1`: gate poll → artifact/
  decode-run binding → single read or `-PollSeconds` polling at 100 ms cadence,
  reporting distinct `(input0, input1, direction)` tuples and the transition
  count. PS 5.1, ASCII, exit codes 0/1/2/3.
- **Tests**: 7 coordinator (positive, no-rotator, candidate out-of-range,
  identity mismatch, round-trip mismatch, non-finite float, read failure) +
  1 endpoint (anchor parse + field echo + no-region-bytes).

## Why this satisfies the G1 item 5 gate (once correlated)

G1 item 5 = "shot-synchronous muzzle origin + gun direction". The two `Update`
inputs are the candidate turret-yaw / gun-elevation pair; the aim struct's
normalized direction is the gun-marker ray direction (the gun's, not the
camera's). The gate names which input is which, with **hull yaw** (`ring
+0x30`, already `Verified`) held by the existing live-frame surface as the
discriminator — this anchor deliberately does **not** re-read hull yaw (a
controlled traverse keeps the hull stationary, so the already-verified read
cannot corrupt the discriminator, and the additive surface stays one object
wide). See `docs/operations/pen-shot-ray-read-proposal.md`.

Honest limits: the aim struct is the gun-marker *aim* ray (per-frame, not a
fire-time snapshot) — "shot-synchronous" here means "gun direction, not
camera". The muzzle *origin* is reconstructable as `hit − dir · distance` but
is a correlation check, not a direct read (no `VehicleGun` world-position field
is named yet).

## Validation

- Full-solution Release build: the GameIntegration/ApiContracts/Core projects
  compile clean (0 warnings, 0 errors). `Host.Web` compiles with **zero CS
  errors**, but its build output is locked by a running dev server, so the
  full-solution build and `Host.Web.Tests` could not run (see gap below).
- Focused suites green: GameIntegration **392 passed / 6 opt-in skips**
  (the 7 new GunAim tests pass; the positive test's read-count assertion
  confirms 21 reads = identity + owner + round-trip + 2 × 9 floats), Core 298
  passed, Architecture 20 passed (reference graph intact).
- New script parses clean (PS 5.1 AST) and is ASCII-only.
- `offline_check.py`: 74 files, 120 links, 0 broken; blocker numbering + ledger
  consistency intact.

## One honest gap

`Host.Web.Tests` (the new `EntityRegion_GunAimAnchor_…` endpoint test) was not
run: a `WotBTreader.Host.Web` dev server (PID 26400) holds its build output, so
the test project fails at the file-copy step. The endpoint C# itself compiles
clean, and the test is a direct mirror of the existing `ShellState` endpoint
test; it will run green once the server is stopped (which the full milestone
gate needs anyway).

## Next step

The **promotion gate** (G1.5) requires the live controlled turret/gun traverse
with two content-distinct positive repeats, per `pen-promotion-gates.md` — an
owner-run scenario. The read surface and capture driver are ready; nothing is
promoted until the correlation runs. See
`docs/operations/pen-promotion-runbook.md` for the step-by-step scenario.
