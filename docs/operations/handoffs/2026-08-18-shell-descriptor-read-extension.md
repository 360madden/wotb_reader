# Penetration v0.3 — shell-state descriptor read extension (G1 item 2) shipped

**Date:** 2026-08-18 (UTC)
**Status:** the coordinator-owned `shell-state` anchor now also reads the
resolved `Shell` descriptor's kind/caliber/damage, implemented and tested.
**Not live-validated** — the controlled shell-swap correlation (the promotion
gate) is still pending. Nothing promoted.
**Blocker:** `BLK-0027` (narrowed — the read surface now decodes WHICH shell is
loaded; the live controlled shell-swap is next).
**Refines:** `2026-08-18-shell-state-read-surface.md`,
`2026-08-18-shell-identity-holder-writer.md`.

## What shipped

The G1.2 acceptance fields (kind `+0x114`, caliber `+0x118`,
damage.armor `+0x11c`, damage.devices `+0x120`) are now read live from the same
resolved `Shell` object the identity fingerprint already walks to, instead of
only being statically named:

- **Contracts** (`GameSessionContracts.cs`): four offset constants
  (`ShellKindOffset`/`ShellCaliberOffset`/`ShellDamageArmorOffset`/
  `ShellDamageDevicesOffset`) plus `ShellKind`/`ShellCaliber`/
  `ShellDamageArmor`/`ShellDamageDevices` result fields.
- **Coordinator** (`GameSessionCoordinator.cs`): step 6 of
  `RunShellStatePassAsync` reads the four fields off the resolved `shellId`
  after the identity dwords, and fails closed — a read miss → null verdict →
  `ShellStateMismatch`, a non-finite damage float → a zeroed `Mismatch` verdict
  so two identical mismatches stay two-pass stable.
- **ApiContracts + endpoint**: four response fields echoed; no raw region bytes
  leave the endpoint.
- **Driver** (`scripts/capture-pen-shell-state.ps1`): reports
  `(index, id0, id1, kind, caliber)` + damage per distinct state.
- **Tests**: coordinator positive test asserts the four fields and bumps the
  read-count assertion 20 → 28 (identity + owner + two passes × 13 reads);
  endpoint test echoes all four.

## Why this matters for the G1 item 2 gate

The prior surface proved the identity fingerprint flips on a swap but could not
decode *which* shell was loaded (the descriptor was statically named but
unread). Now the same capture reports the loaded shell's kind/caliber/damage,
so a controlled swap can be correlated to the actual shell, closing the
"descriptor-not-read" caveat in `pen-promotion-gates.md` G1.2 and the runbook.

## Validation

- GameIntegration.Tests: **392 passed / 6 opt-in skips** (the 7 shell-state +
  7 gun-aim tests pass).
- Core.Tests: 298 passed. `offset_check.py --check-schema`: PASS.
- `offline_check.py`: 0 broken links, blocker + ledger consistency intact.
- `Host.Web.Tests` still not run — the `WotBTreader.Host.Web` dev server
  (PID 26400) holds its build output; the endpoint C# compiles and the test is
  a direct mirror of the existing `ShellState` echo test.

## Next step

The G1.2 promotion gate needs the live controlled shell-swap (fire shell A,
switch, fire shell B) with two content-distinct positive repeats — an
owner-run scenario. Both available replays are swap-free
(`2026-08-18-medvedkovo-shell-swap-negative.md`). See
`docs/operations/pen-promotion-runbook.md`.
