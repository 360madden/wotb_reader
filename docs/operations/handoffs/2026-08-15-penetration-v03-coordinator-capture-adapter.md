# Penetration v0.3 coordinator-owned capture adapter

**Date:** 2026-08-15 (UTC)
**Status:** offline adapter implemented; production exact-input source remains neutral and owner-gated
**HEAD:** `06f608e feat(overlay): harden HUD runtime and game-window tracking`
**Blocker:** `BLK-0027`

## Project-management decision

The pure capture evaluator and privacy contract were ready, but no exact
configured-gun, loaded-shell, or shot-ray semantic field had been proven. The
practical next slice was therefore the smallest coordinator-owned adapter,
not a speculative memory scan or a new weapon/aim shared contract.

## Implemented

- Added the public application port `IPenetrationCapture` with a request that
  carries only a `DecodeRunId` and the one fixed phase intent.
- Made `GameSessionCoordinator` implement the port and serialize capture
  attempts with a bounded gate.
- Before any source call, the coordinator now requires:
  - `OfflineReplayVerified` and the exact
    `session.offline_replay_verified` reason code;
  - a current managed launch with a bound battle session;
  - a completed successful decode run with no failure fields;
  - decode-run/source-artifact/session identity equality with the managed
    launch; and
  - matching decoded/process build identity plus a currently resolved module.
- Added a fixed 300-second cancellation bound and revalidation after the
  source returns. Caller-controlled PIDs, handles, paths, module bases, RVAs,
  pointer chains, read sizes, and raw bytes are not accepted by the port.
- Added an internal aggregate-only source seam. Its context is coordinator
  generated and its result contains only the bounded facts consumed by
  `PenetrationCaptureEvaluator`.
- Registered the adapter as a composition-root port and kept it on the same
  singleton coordinator as the existing session, observer, scanner, and
  association ports.
- The production source intentionally returns neutral evidence until the
  exact semantic source is proven. A first positive synthetic aggregate is
  held only in memory; a content-hash-distinct second positive aggregate is
  required for promotion, after which the witness is cleared.
- Added synthetic coordinator coverage for missing authorization, exact-build
  refusal without a source read, evaluator bounds rejection, cancellation,
  and two-content-distinct repeat promotion.

## Deliberate non-actions

- No `WeaponState`, `AimState`, armor-layer port, colored badge, HTTP endpoint,
  raw capture store, or persistent promotion record was added.
- No arbitrary scan or unproven `VehicleGun`/`VehicleGunRotator` field was read.
- No game process, install file, replay bytes, memory dump, screenshot, or
  private runtime data was touched or committed.

## Validation

- Focused GameIntegration coordinator tests: **108 passed**.
- Full GameIntegration suite: **357 passed**, **6 expected opt-in skips**.
- Full validation gate: **1,295 passed**, **8 expected opt-in skips**, 0 failed.
- Release build: **0 warnings, 0 errors**.
- Formatting, architecture/reference graph, privacy/repository scan, agent
  policy, PowerShell, offline links/index freshness, ledger, blocker, and
  offset schema/chains checks: passed.

## Next decision

The next live milestone remains owner approval followed by one serialized
exact-build managed-offline capture using a source implementation that emits
only the aggregate contract. A negative or ambiguous source result must remain
`NotReady`; only two content-distinct positive repeats can open the exact-input
promotion review. Armor remains a separate producer-trace decision and the
rejected hard-joint visualization path stays closed.
