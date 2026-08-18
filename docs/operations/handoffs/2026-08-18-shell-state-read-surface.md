# Penetration v0.3 — loaded-shell read surface (G1 item 2) shipped

**Date:** 2026-08-18 (UTC)
**Status:** coordinator-owned `shell-state` entity-region anchor implemented,
tested, merged, and **live-validated** (2026-08-18). The controlled
shell-swap correlation remains the promotion gate. Nothing promoted.
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
`+0x118`, damage `+0x11c`, vftable `0x31a1e14`). The descriptor link was
subsequently resolved (2026-08-18) — see
`2026-08-18-shell-descriptor-link.md`; it is not required for the G1 item 2
*gate* (see below).

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

## Live validation (2026-08-18, exact-build managed offline replay)

One managed offline replay (Churchill I / savanna, `1cda5c31…`, battle
session `01a015b9-189f-7f42-a5d7-d3240ae64a99`) resolved the anchor live:

- `status=Resolved`, `index=0`, `identity0=5`, `identity1=71`,
  `two_pass_stable=true`.
- 87 samples over ~150 s: **1 distinct state, 0 transitions** — the loaded
  shell stayed `(0, 5, 71)` for the whole capture window.

The six-dereference chain resolves against the real game, so the read surface
is live-proven (the synthetic tests verified the logic; this proves the
offsets). **Decoded (2026-08-18, corrected by
`2026-08-18-shell-identity-holder-writer.md`):** `identity1` = `71` is the
component **id** (`Shell+0x24`); `identity0` = `5` is a per-component
**status/tier** discriminator (`Shell+0x20`), **not** `eShellKind` — the kind
lives at `Shell+0x114` and was never read live. (The earlier "identity0 = APCR"
reading here and in `2026-08-18-shell-descriptor-link.md` was a coincidence
and is retracted.)

**0 transitions means this replay did not exercise a shell swap**, so the
controlled-transition correlation still needs a replay whose player swaps
shells (or a freshly recorded controlled replay). The descriptor
(kind/caliber/damage) link and G1 item 5 (shot ray) remain open.

## Next step

The **promotion gate** requires the live controlled shell-swap with two
content-distinct positive repeats, per `pen-promotion-gates.md` — the current
replay did not swap shells (0 transitions), so a swap-bearing replay or a
controlled recorded scenario is the remaining input. Nothing is promoted.
