# Penetration v0.3 — loaded-shell read-surface proposal (G1 item 2)

**Date:** 2026-08-18 (UTC)
**Status:** proposal — new coordinator-owned read surface, no live action, no
promotion. Follows the `pen-ownership-walk` precedent (proposal → lead review →
implementation + tests → read-only security posture → merge).

## Question

G1 item 2 requires proving the **loaded shell** through controlled transitions.
The static work located the runtime shell-index field and its container chain;
this proposal defines the minimal guarded read surface that observes it, and
corrects one hop the earlier handoffs over-claimed.

## Correction to the 2026-08-18 handoffs

The handoffs summarized the chain as `… → Shell → damage +0x11c`. The
`ProcessCurrentShells` decompile (`FUN_015ef402`, evidence
`.build/ghidra-evidence-ammo/functions-disasm.txt`) shows the terminal object
is **not** the `Shell` *descriptor* (vftable `0x31a1e14`, kind `+0x114`,
caliber `+0x118`, damage `+0x11c`). The object reached by the
`AmmoController` walk is a lightweight **shell identity holder** whose only
compared fields are two identity dwords at `+0x20`/`+0x24`:

```c
iVar7 = *(int *)(*(int *)(*(int *)(*(int *)(iVar7 + 0x20) + 0x1b0) + iVar6 * 4) + 0x1c);
if (*(int *)(iVar7 + 0x20) == *(int *)(*(int *)(target + 0x20)) &&
    *(int *)(iVar7 + 0x24) == *(int *)(*(int *)(target + 0x24))) { /* match */ }
```

So the "→ damage `+0x11c`" final hop is **unproven** and is dropped from this
proposal. Mapping the identity holder to the `Shell` descriptor (kind/caliber/
damage) is a separate, still-open link; it is not required for the G1 item 2
*gate* (see below).

## The read chain (byte-verified, hash-bound `1cda5c31…`)

Reaching the owner reuses the already-merged `pen-ownership-walk` scan (gated
AOB for the unique `VehicleGunRotator`, `rotator+0x10 → owner`). From the owner
(`AvatarGameLogic`):

1. `ammo = owner + 0x4B4` (AmmoController is **embedded**, not a pointer).
2. `index = [ammo + 0x38]` — the current-shell index (int32, `ProcessCurrentShells`).
3. `gunRef = [ammo + 0x40]`; `list = [gunRef + 0x20]`.
4. `begin = [list + 0x1b0]`, `end = [list + 0x1b4]`, `count = (end - begin) >> 2`.
5. `element = [begin + index * 4]` (index bounded by `count`).
6. `shellId = [element + 0x1c]`.
7. `identity0 = [shellId + 0x20]`, `identity1 = [shellId + 0x24]`.

Every hop is a coordinator-owned read; only `index`, `identity0`, and
`identity1` leave the coordinator (plus a `TwoPassStable` boolean). No address,
pointer, id, or raw bytes are returned.

## Why this satisfies the G1 item 2 gate

"Prove the loaded shell through controlled transitions" = show that a bounded
observable tracks the loaded shell and flips exactly when a controlled swap
flips. `index` is the observable that `ProcessCurrentShells` itself writes; the
`identity0/identity1` fingerprint distinguishes *which* shell is loaded. A
controlled swap that changes `index` and yields a distinct, stable identity
fingerprint proves the loaded-shell tracking without needing the descriptor
link (which the eventual damage model needs separately, and which remains an
honest `WeaponStateUnavailable` sub-reason until then).

## Proposed contract changes

- `EntityRecordRegionAnchor.ShellState = 5`.
- `EntityRecordRegionReadRequest` gains the offset constants above
  (`AmmoControllerEmbedOffset`, `AmmoCurrentShellIndexOffset`, `AmmoGunRefOffset`,
  `ShellRefListOffset`, `ShellVectorBeginOffset`, `ShellVectorEndOffset`,
  `ShellElementIdentityHolderOffset`, `ShellIdentityDword0Offset`,
  `ShellIdentityDword1Offset`).
- `Type10EntityPositionStatus` gains `ShellStateNotFound`, `ShellStateMismatch`,
  `ShellStateUnstable`.
- `EntityRecordRegionReadResult` gains `ShellStateIndex`, `ShellStateIdentity0`,
  `ShellStateIdentity1`, `ShellStateTwoPassStable`.

## Security posture (read-only framing)

Same as `pen-ownership-walk`: gate-verified session, exact-build identity check,
guarded reader lease, per-hop identity/fail-closed checks, two-pass stability,
aggregate-only output. The scan for the rotator is the already-audited
`pen-ownership-walk` scan; the new reads only extend the existing walk past the
owner into the embedded `AmmoController`. No new process, module, or build
binding; no raw memory leaves.

## Test plan

Mirror `GameSessionCoordinatorTests`'s `PenOwnershipWalk` cases: positive
resolved, no-rotator, index out of range, a null intermediate pointer (read
failure → fail closed), an unstable two-pass, and identity-field reads. All
synthetic (scripted reader + fake scan); no game install.

## Not in scope

- The `Shell` *descriptor* link (kind/caliber/damage) — still open, needs its
  own producer/identity trace.
- G1 item 5 (shot ray / turret yaw vs gun elevation) — the rotator's aim fields
  are not statically nameable and need a live probe first.
- Any promotion, badge change, or live action.
