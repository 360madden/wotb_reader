# Penetration v0.3 — loaded-shell read surface (G1 item 2) shipped

**Date:** 2026-08-18 (UTC)
**Status:** coordinator-owned `shell-state` entity-region anchor implemented,
tested, and merged; live correlation remains the promotion gate. Nothing
promoted.
**Blocker:** `BLK-0027` (narrowed — the read surface is ready; the live
controlled shell-swap correlation is next).

## What shipped

- **`EntityRecordRegionAnchor.ShellState`** (the 6th anchor) plus the
  coordinator resolution `ResolveShellStateAsync` /
  `RunShellStatePassAsync`: reuses the already-audited `pen-ownership-walk`
  rotator scan to reach the owner, then walks the embedded `AmmoController`
  (`owner + 0x4B4`) to the current-shell index (`+0x38`) and the resolved
  shell identity holder's two dwords (`+0x20`/`+0x24`). Two passes,
  fail-closed, aggregate-only — no address, pointer, id, or raw bytes leave
  the coordinator.
- **New statuses** `ShellStateNotFound` / `ShellStateMismatch` /
  `ShellStateUnstable`, **new result fields** `ShellStateIndex` /
  `ShellStateIdentity0` / `ShellStateIdentity1` / `ShellStateTwoPassStable`
  (Application + wire contracts), and the `shell-state` anchor string in the
  endpoint's parser.
- **Driver** `scripts/capture-pen-shell-state.ps1`: gate poll → artifact/
  decode-run binding → single read or `-PollSeconds` polling at 100 ms
  cadence, reporting distinct (index, identity0, identity1) tuples and the
  transition count. PS 5.1, ASCII, exit codes 0/1/2/3.
- **Tests**: 7 coordinator (positive, no-rotator, index out-of-range,
  identity mismatch, null owner, unequipped gun, read failure) + 1 endpoint
  (anchor parse + field echo + no-region-bytes). GameIntegration 385 passed /
  6 opt-in skips; Host.Web 192 passed / 1 opt-in skip.

## Correction to the earlier handoffs

The 2026-08-18 shell-index handoffs summarized the chain as ending at
`Shell → damage +0x11c`. The `ProcessCurrentShells` decompile shows the
terminal object is a **shell identity holder** (two compared dwords at
`+0x20`/`+0x24`), **not** the `Shell` *descriptor* (kind `+0x114`, caliber
`+0x118`, damage `+0x11c`, vftable `0x31a1e14`). The descriptor link is still
open and is recorded as such in `pen-shell-state-read-proposal.md`; it is not
required for the G1 item 2 *gate* (see below).

## Why this satisfies the G1 item 2 gate

"Prove the loaded shell through controlled transitions" = observe a bounded
signal that tracks the loaded shell and flips exactly on a controlled swap.
`ShellStateIndex` is the field `ProcessCurrentShells` itself writes; the
identity fingerprint distinguishes *which* shell is loaded. A controlled swap
that changes the index and yields a distinct, stable fingerprint proves
loaded-shell tracking without the descriptor link.

## Validation

- Full solution Release build: 0 warnings, 0 errors.
- Focused suites green (GameIntegration 385, Host.Web 192, both with the new
  tests).
- New script parses clean and is ASCII-only.

## Next step

Run `scripts/invoke-od-replay-chain.ps1` (or the launcher + this driver)
against an exact-build managed offline replay and observe the shell state.
The **promotion gate** still requires the live controlled shell-swap with two
content-distinct positive repeats, per `pen-promotion-gates.md` — nothing is
promoted by this change. The `Shell` descriptor (kind/caliber/damage) link and
G1 item 5 (shot ray) remain open.
