# Penetration v0.3 capture evaluator and armor triage

**Date:** 2026-08-15 (UTC)
**Status:** offline implementation complete; exact-input capture remains owner-
gated and unproven
**HEAD:** `06f608e feat(overlay): harden HUD runtime and game-window tracking`

## Project-management decision

The source verdict and privacy contract were already ready, but a live read
would still have been premature without a coordinator-owned promotion gate. The
practical next slice was therefore implemented offline first: make the safety
contract executable and testable, then narrow the armor blocker instead of
widening memory access on a hypothesis.

## Implemented

- Added pure Core evaluator:
  `src/WotBTreader.Core/Overlay/PenetrationCaptureContract.cs`
- Added 10 synthetic tests:
  `tests/WotBTreader.Core.Tests/PenetrationCaptureContractTests.cs`
- The evaluator enforces:
  - `OfflineReplayVerified` plus the exact
    `session.offline_replay_verified` reason code;
  - current managed association, exact artifact/decode/process identity, and
    exact-build equality;
  - fixed duration, candidate, round, individual-read, batch-read, and
    aggregate-count bounds;
  - unique/stable owner evidence;
  - shell A→B→A and exact installed-identity evidence;
  - independent turret yaw and gun elevation evidence;
  - finite, normalized, target-joined shot-ray evidence;
  - rejection of camera fallback, post-shot-only evidence, raw retention, and
    same-content repeats;
  - positive-first-run status that still requires a content-distinct repeat;
  - promotion only after two complete content-distinct positive runs.
- The evaluator has no IO, Win32, memory, logging, persistence, or public HTTP
  surface. It consumes aggregate facts only.
- Added `pen-v03-alternative-armor-owner-triage.md`. The adjacent
  `ArmorComponent`/`ArmorConfiguration` families have RTTI identity but no
  documented producer/read path proving physical thickness, units, layer order,
  and shell interaction. The hard-joint visualization path remains rejected.

## Deliberate non-actions

- No `WeaponState`, `AimState`, or armor-layer runtime contract was promoted.
- No managed capture adapter or memory read was started; that remains gated on
  explicit approval of the frozen proposal and exact-build offline lifecycle.
- No exact ports, colored v0.3 badge, or 12-replay corpus was created from
  unsupported evidence.
- No game install, replay data, raw memory bytes, screenshots, or private logs
  were modified or committed.

## Validation

- Focused capture evaluator tests: **10 passed**.
- Full Core suite: **298 passed**.
- `dotnet format WotBTreader.sln --verify-no-changes --no-restore`: PASS.
- Full `scripts/validate.ps1`: PASS.
- Full suite: **1,290 passed**, **8 expected opt-in skips**, 0 failed.
- Release build: 0 warnings, 0 errors.
- Repository/privacy, architecture, policy, PowerShell, offline-link, ledger,
  blocker, and offset checks passed.

## Next decision

The code is ready for the coordinator-owned adapter, but the adapter must not
accept caller-supplied addresses or widen the read surface. The next live
milestone is one serialized owner-approved capture; a second content-distinct
replay is required before any exact field promotion. If the capture is
ambiguous or negative, retain neutral readiness and either fund the deeper
static armor producer trace or close that lane as not feasible.
