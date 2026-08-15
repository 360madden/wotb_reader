# Penetration v0.3 source verdict and capture contract

**Date:** 2026-08-15 (UTC)
**Status:** static source verdict complete; capture contract frozen as a
proposal; owner approval and serialized evidence capture remain open
**HEAD:** `06f608e feat(overlay): harden HUD runtime and game-window tracking`

## Worktree boundary

The HUD/Pen implementation is already pushed in the current HEAD. This slice
changed only Pen v0.3 planning/operations documentation and the offline index.
The following pre-existing paths remain intentionally untouched:

- `docs/operations/handoffs/2026-08-02-od-recovery-014-partial.md`
- `.agents/skills/autorun/`

No reset, clean, stash, commit, push, game-install write, replay write, or live
memory session was performed.

## Source verdict

The exact-build static evidence is a bounded no-go for immediate exact-input
wiring:

- `VehicleGun` and `VehicleGunRotator` are the strongest state-owner
  candidates under the avatar/game-logic family.
- `AvatarGunAgent` is a bridge candidate, not a proven state owner.
- Static evidence does not prove viewpoint ownership, configured-gun identity,
  loaded-shell identity, turret yaw/elevation, muzzle origin, or shot direction.
- The collision hard-joint visualization path remains rejected as an armor
  thickness/layer source. No `armor_N` join, explicit millimeter unit, or
  physical layer ordering was proven there.
- Replay shell signatures and CAM-013 camera direction remain diagnostics-only
  and cannot satisfy exact loaded-shell or shot-ray provenance.

This is an honest completed static verdict, not a positive runtime discovery.
No offset or shared exact-input contract was promoted.

## Contract result

Added `docs/operations/penetration-v03-managed-capture-contract.md` as a
frozen proposal for the next gated step. It defines:

- exact-build and managed-launch identity gates;
- four fixed phases: owner census, shell A→B→A transition, aim
  discrimination, and shot-ray join;
- coordinator-owned addresses/candidates with bounded read, round, duration,
  and cancellation limits;
- aggregate-only evidence with no raw bytes, pointers, PIDs, paths, tokens,
  replay data, or player identifiers;
- positive acceptance criteria requiring exact shell identity, independent
  turret/elevation behavior, shot-synchronous normalized rays, and a repeat on
  a second content-distinct replay;
- terminal no-go behavior for ambiguity, stale clocks, process replacement,
  non-finite values, or partial evidence.

The local security review is complete. Owner approval is still required before
any new managed-offline capture or memory-read implementation. No shared
`WeaponState`, `AimState`, or armor-layer contract was added prematurely.

## Validation

- `python scripts/python/offline_check.py --refresh`: PASS; 68 files and 119
  links checked, 0 broken; blocker numbering and ledger consistency passed.
- `scripts/validate.ps1`: PASS.
- Release build: 0 warnings, 0 errors.
- Full suite: 1,280 passed, 8 expected opt-in skips, 0 failed.
- Formatting, repository/privacy scan, architecture, policy, PowerShell,
  offline-pack, ledger, blocker, and offset checks passed.
- No live game process or memory read was started.

## Next gate

1. Owner approves the capture contract.
2. Run exactly one serialized managed-offline capture on the exact build.
3. Repeat only if the first result is positive and the process/session evidence
   remains valid; require a second content-distinct replay for promotion.
4. Promote only proven fields into additive contracts and exact ports. If the
   capture is negative or ambiguous, keep v0.3 neutral and move to the
   alternative authoritative armor/layer investigation.
5. Build the 12-replay/500-shot corpus only after exact inputs exist.
