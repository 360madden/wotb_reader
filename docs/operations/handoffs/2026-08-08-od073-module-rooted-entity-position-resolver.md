# Handoff - OD-073 module-rooted entity-position resolver (2026-08-08)

## Outcome

The current 11.19.0.10 executable now has a hash-bound, module-rooted candidate
for continuously polling a decoded replay entity's newest retained position.
The work closes the architecture gap left by OD-071/072's reliable event-based
capture: the poll no longer needs a debugger event or a caller-provided process
address.

Static evidence passes 47/47 strict checks against executable SHA-256
`1cda5c31919c9784a41bee7f3270ec1b4536b124c51e8b36f2221b381760307d`.
The frozen ownership path is:

1. `[wotblitz.exe + 0x04054780] -> AppContextImpl`;
2. `[AppContext + 0x4C] -> BWApp`;
3. `[BWApp + 0x24] -> BWServerConnection`;
4. connection `+0x04` -> embedded `BWEntities`;
5. cache `+0x48`, then three bounded map trees -> matching replay entity ID;
6. `[entity+0x38]` -> vtable-checked AvatarFilter;
7. filter `+0x08` -> vtable-checked AvatarFilterHelper;
8. helper `+0x1C8` -> current index in an 8-entry, `0x38`-stride ring;
9. position -> three Float32 members at record `+0x18`.

This is entirely re-derived current-build evidence. The old community module
root and `entity+0x68/+0x6C/+0x70` position claim remain refuted.

## Implementation

- `TraceEntityRegistryPosition.java` is the strict hash-bound verifier. Its
  fresh report must say `verdict=resolver-layout-proven` with zero failed checks.
- `Type10EntityPositionResolver` is a pure Core resolver. It caps traversal at
  1,024 nodes and three attempts, detects cycles, validates pointer arithmetic,
  checks entity/filter/helper identities, double-collects the full current ring
  record, and revalidates the root chain before returning finite XYZ.
- `GuardedMemoryReader` runs the resolver through one identity-bound, read-only
  process lease. Every memory operation retains cancellation and process
  identity validation.
- `GameSessionCoordinator` owns the process, module base, exact version/hash,
  and layout. Unsupported builds do not create a memory reader; authorization
  revocation cancels and discards the result.
- `POST /api/v1/game/discover/entity-position` accepts only the decoded replay
  entity ID. It returns no process address or raw bytes.
- `scripts/od-073-entity-position-poll.ps1` selects the decoded viewpoint,
  performs a bounded live series, compares it with retained decoded trajectory
  samples, and writes aggregate-only evidence. The aggregate contains no entity
  ID, coordinate, process address, raw byte, capability, replay path, player
  name, or account data.

## Evidence boundary

Proven now:

- exact-build static module root and ownership/member relationships;
- bounded entity lookup and ring-read behavior in synthetic tests;
- unsupported-build and missing-gate denial before native reader creation;
- server ownership of every process/address/layout decision;
- live authorization revocation discards an in-flight result;
- privacy-safe public projection and aggregate runner.

Not proven yet:

- a successful live ring read in the game;
- movement and decoded-trajectory consistency through this polling path;
- stable-root repeatability across fresh processes/content-distinct replays;
- hardware atomicity or exact decoded-clock alignment;
- a publishable single offset or offset-table promotion.

The full record is read twice with a stable index and full identity-chain
revalidation. That is consistency evidence only. It must not be described as a
hardware-atomic snapshot.

## Validation

- Ghidra strict verifier: 47 passed, 0 failed.
- Core resolver focused tests: 10 passed.
- GameIntegration authorization/coordinator focused tests: 4 passed.
- Host endpoint projection focused test: 1 passed.
- Windows PowerShell 5.1 parser/ASCII check: passed.
- Repository PowerShell hygiene gate: passed.
- Full repository gate: passed after the final source/doc/index changes.
  Release build had 0 warnings/errors; 646 tests passed with the two expected
  installed-game opt-in skips; repository scan, script hygiene, offline links,
  file-tree freshness, blocker numbering, and ledger consistency all passed.

## Durable workflow policy

Community offsets are candidate families, not reusable addresses. Preserve
names and ownership/member relationships as search clues, then:

1. re-derive every address/displacement against the current executable;
2. bind the result to exact version/hash and strict static checks;
3. place the complete layout in server-owned policy;
4. expose only the semantic input needed by the caller;
5. prove bounded behavior synthetically;
6. spend live budget only on the remaining semantic question;
7. preserve aggregate evidence and stop on either a positive or honest negative.

Do not fall back to broad scans, stale historical triples, caller-supplied
addresses, or debugger capture merely because the first polling result is
negative.

## Next admissible action

After the full repository gate and a fresh Host publish, run one bounded OD-073
poll during a positively verified offline replay. A positive result requires:

- all requested reads resolved;
- at least two distinct positions;
- consistency with retained decoded viewpoint trajectory samples;
- module-root, entity-identity, and consistent-double-read flags true;
- hardware-atomic and same-decoded-clock flags false.

If positive, repeat the unchanged resolver once on the other content-distinct
replay and a fresh process. If negative or inconclusive, preserve the aggregate
and return offline. Do not update `memory-offsets/11.19.0.10.json` yet.
