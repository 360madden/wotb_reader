# Handoff - OD-074 replay-root live narrowing (2026-08-09)

## Outcome

OD-073's original polling root was corrected and the replay-owned entity path
was reached in a positively verified offline replay. Continuous player-location
polling is still not proven: the current resolver stops safely at an
unverified vehicle-helper subtype before reading the movement ring.

The exact-build static verifier now passes 67/67 checks. The corrected root is:

1. `[wotblitz.exe + 0x04095C88] -> GameCore`;
2. `[GameCore+0x0C] -> AppController`;
3. `[AppController+0x124] -> SessionController`;
4. `[SessionController+0x118] -> AccountController`;
5. `[AccountController+0x128] -> active PlaybackController`;
6. `[PlaybackController+0x120] -> replay BWServerConnection`;
7. connection `+0x04` -> embedded replay `BWEntities`.

The `GameCore` constructor publishes its long-lived stack object through the
module-global slot. AppController, SessionController, AccountController, and
PlaybackController are independently vtable-checked. The old main
`BWApp` connection and the inferred `AppContext+0x118` owner are both refuted.

## Live evidence

Four bounded aggregate-only checks were completed under
`OfflineReplayVerified`; no private values are copied here:

1. The original main-connection root returned `EntityNotFound` for 24/24
   requests. It is structurally real but does not own replay entities.
2. The inferred `AppContext+0x118` root returned
   `UnsupportedAccountController` for 24/24 requests. The vtable gate correctly
   refuted that owner before entity traversal.
3. The corrected GameCore replay root found the requested entity in the
   primary replay map for 24/24 requests, then stopped at
   `movement-filter-vtable`.
4. Static analysis proved that KineticsFilter, WGVehicleFilter2, and
   AvatarFilter share the same position-apply slot. After exact allowlisting,
   the corrected root again found the entity in the primary map for 24/24
   requests, then stopped at `avatar-helper-vtable`.

All four runs returned honest negative/inconclusive verdicts. The two corrected
root runs prove the module-rooted replay ownership and entity-ID lookup in one
fresh managed process. They do not prove a valid helper layout, position-ring
read, movement, decoded-trajectory agreement, cross-process repeatability, or
an offset suitable for publication. All game, Host, helper, and debugger
processes were stopped after the final run.

## Current implementation boundary

`Type10EntityPositionResolver` now uses the corrected owner chain and validates
the AppController and SessionController types in addition to the existing
AccountController and PlaybackController checks. Its movement-filter allowlist
contains only the three vtables whose slot 2 points to the exact verified
position-apply function. Its helper allowlist remains deliberately narrow.

`TraceEntityRegistryPosition.java` pins the constructor stores, controller
vtables, callback registrations, replay connection, entity maps, filter-family
slots, and ring semantics to the exact executable hash. The utility
`FindFunctionReferences.java` records code/data references and nearby symbols
for future hash-bound ownership tracing; its reports stay under the ignored
build evidence directory.

The first offline follow-up is fruitful but not yet allowlist-grade. A fresh
run identifies module RVA `0x0325658C` as
`WGVehicleFilterHelper::vftable`, with references from the helper constructor
at RVA `0x010139B0` (the vtable store is at `0x010139F1`) and another function
at RVA `0x0101D100`. Separately inspected factory code at RVA `0x01069BE0`
constructs that helper and assigns it to `filter+0x08`. These facts name the
right subtype and ownership edge. They do not yet prove its position-store
slot or that its ring layout matches either already allowlisted helper.

The OD-073 runner now records aggregate status, failure-stage, entity-source,
attempt, and node-count summaries without persisting entity IDs, coordinates,
addresses, raw bytes, capabilities, paths, or player/account data. Its
rendezvous ACL check validates the protected owner-only directory and the
inherited file ACL.

## Validation

- `TraceEntityRegistryPosition.java`: 67 passed, 0 failed, verdict
  `replay-resolver-layout-proven`.
- `FindFunctionReferences.java`: compiled and ran against the existing
  hash-verified project; the fresh ignored report named
  `WGVehicleFilterHelper::vftable` and its two references.
- Full `scripts/validate.ps1`: passed after the final source, documentation,
  and file-tree changes.
- Release build: 0 warnings, 0 errors.
- Tests: 649 passed; 2 expected installed-game opt-in skips.
- Repository/privacy scan, PowerShell 5.1 and PowerShell 7 rule smoke tests,
  PSScriptAnalyzer hygiene, offline links/file-tree freshness, blocker
  numbering, and ledger consistency: passed.

## Durable decision

Do not run another live poll merely by adding the newly named helper vtable to
an allowlist. Continue from the proved `WGVehicleFilterHelper` constructor and
`filter+0x08` assignment to identify its position-store slot and determine
whether its ring index/stride/position layout is identical to the already
verified helper family. Add exact static checks and focused synthetic tests.
Only that proven-layout change can justify one further bounded live replay
check.

Do not restore either refuted root, broaden to arbitrary vtables, expose a
caller-supplied address, resume broad scanning, or change
`memory-offsets/11.19.0.10.json`. Player-location polling and offset publication
remain unproven.
