# Penetration v0.3 managed-offline capture contract

**Date:** 2026-08-15 (UTC)
**Status:** FROZEN IMPLEMENTATION — pure evaluator and coordinator-owned
aggregate adapter implemented; owner approval and one serialized exact-input
capture remain required
**Roadmap:** Phase 6 G1/G2; blocker `BLK-0027`
**Scope:** exact configured-gun, loaded-shell, and shot-synchronous gun-ray
source discovery only

## Decision boundary

The exact-build static verdict is a no-go for direct product wiring. The
11.19.0.10 executable contains RTTI anchors for `VehicleGun`,
`VehicleGunRotator`, and `AvatarGunAgent`, but the static evidence does not
prove which live objects belong to the viewpoint vehicle or which fields carry
the configured gun, loaded shell, turret/gun transforms, muzzle origin, or shot
direction. `AvatarGunAgent` is retained as a bridge candidate, not as a state
owner.

The next evidence action is therefore one narrow, managed-offline capture. It
must prove or reject the candidates without adding a caller-controlled memory
surface. No exact input is published, cached, or wired into the penetration
badge until the acceptance criteria below pass on two content-distinct replay
sessions.

## Exact-build binding

The capture is valid only for the currently published exact build:

- executable: `wotblitz.exe`
- product version: `11.19.0.10`
- executable SHA-256:
  `1cda5c31919c9784a41bee7f3270ec1b4536b124c51e8b36f2221b381760307d`
- static candidate identities: `VehicleGun`, `VehicleGunRotator`,
  `AvatarGunAgent`

A build/version/hash mismatch is a terminal no-go, not a reason to broaden a
scan or fall back to a nearby client.

## Lifecycle and authorization gates

The coordinator must require all of these before any candidate read:

1. `verificationState=OfflineReplayVerified`.
2. `reasonCode=session.offline_replay_verified`.
3. A current coordinator-owned managed launch lease bound to the exact game
   process, source artifact, decode run, and requested battle session.
4. A succeeded/completed decode run for that exact artifact, with no failure
   fields and an exact-build-compatible replay.
5. The game process and module identity still match the launch lease at every
   phase boundary.
6. A single serialized capture; no second game process, online match, or
   parallel live read is permitted.

The caller supplies only an opaque managed association/session assertion and
operator phase intent. It never supplies a PID, process handle, absolute
address, module base, RVA, vtable address, pointer chain, raw bytes, or game
path.

## Bounded operation shape

The capture has four fixed phases. The coordinator owns the phase order and
all read locations; callers cannot add phases or widen bounds.

| Phase | Purpose | Allowed evidence leaving coordinator |
|---|---|---|
| Owner census | Find the viewpoint-owned `VehicleGun` and `VehicleGunRotator` candidates through the exact-build identity gates | candidate count, unique/ambiguous status, stable identity booleans |
| Shell transition | Observe controlled shell A→B→A changes and map each state to an exact installed manifest identity | transition outcome, identity-match counts, ambiguity/rejection reason |
| Aim discrimination | Hold the hull stationary while independently moving turret yaw and gun elevation | finite/change/independence counters and bounded error bins; no pose values |
| Shot-ray join | Correlate a shot-synchronous muzzle origin/direction with decoded target/impact evidence | eligible/joined/normalized counters, bounded timing bucket, stable reason counts |

Safety bounds are server constants, not request parameters:

- at most 4 owner candidates per phase;
- at most 64 observation rounds per phase;
- at most 300 seconds for the complete capture;
- at most 4 KB per individual guarded read and 16 KB per bounded batch;
- no arbitrary scan, write, debugger attach, code injection, or shell-handler
  launch;
- cancellation on lease expiry, process replacement, build drift, invalid
  identity, non-finite values, or any bound violation.

Raw observation bytes are transient coordinator data only. They are decoded
in memory, discarded after the aggregate is built, and never enter logs,
replay stores, committed fixtures, or the response.

## Acceptance criteria

A candidate capture is **positive** only when every criterion passes:

### 1. Ownership

- exactly one viewpoint-owned `VehicleGun` candidate and exactly one
  viewpoint-owned `VehicleGunRotator` candidate survive all identity gates;
- the candidate relationships remain stable through the phase and across the
  second content-distinct replay;
- any extra or ambiguous candidate is a no-go.

### 2. Configured gun and loaded shell

- controlled shell A→B→A transitions resolve to exact installed shell
  manifest identities, not just changing numeric values;
- the configured gun identity remains joined to the viewpoint vehicle and its
  installed shell table;
- transitions are observed before, during, and after the controlled change;
- a static/manual shell selector, replay effect-entity id, or inferred stock
  shell is not accepted as observed state.

### 3. Turret and gun aim

- with hull movement held below the predeclared tolerance, turret yaw changes
  independently of hull yaw;
- gun elevation changes independently of turret yaw and hull yaw;
- the selected fields remain finite, normalized or physically bounded, and
  stable across repeated samples;
- camera pose is recorded only as a diagnostic cross-check and cannot satisfy
  this criterion.

### 4. Shot-synchronous ray

- the candidate produces a finite muzzle origin and normalized direction at a
  shot-synchronous observation point;
- the ray joins the decoded attacker, target, and impact within the declared
  replay-clock window;
- the ray geometry is consistent across a bounded sample set and repeats on a
  second content-distinct replay;
- post-shot-only changes, attacker→victim center-line, and CAM-013 camera
  direction are rejected as substitutes.

A partial result remains `NotReady` and records a stable no-go reason. It must
never enable a colored penetration badge or enter the primary corpus.

## Privacy and security review

### Threats addressed

- **Caller-controlled memory expansion:** prevented by fixed phase enum,
  server-owned candidates/locations, and immutable byte/round/time limits.
- **Wrong-process or online capture:** prevented by the exact managed lease,
  lifecycle reason code, process replacement checks, and offline-only gate.
- **Address/identity leakage:** prevented by coordinator-owned addresses and
  aggregate-only output. PIDs, pointers, module bases, raw bytes, account
  identifiers, player names, full paths, URLs, and tokens are prohibited.
- **Stale or mixed replay evidence:** prevented by exact artifact/decode-run
  association, one session per capture, clock-attested shot joins, and
  fail-closed phase transitions.
- **Speculative promotion:** prevented by explicit `NotReady` output until
  both replay repeats satisfy every criterion and an owner approves the
  evidence package.
- **Mutation or code execution:** prevented by read-only guarded reads; the
  capture does not attach a debugger, write memory, inject code, launch an
  arbitrary executable, or execute replay data.

### Review checklist

- [x] No new public endpoint or shared DTO is proposed without owner review.
- [x] Existing `OfflineReplayVerified` and reason-code gates are mandatory.
- [x] Process identity, build identity, and launch lease are coordinator-owned.
- [x] Read sizes, candidate count, rounds, duration, and cancellation are
      bounded by constants.
- [x] Response and logs are aggregate-only and privacy-safe.
- [x] Ambiguity, stale clocks, failed identity, and non-finite values fail
      closed.
- [x] No raw replay bytes, private captures, game assets, or runtime dumps are
      committed.
- [ ] Owner explicitly approves the contract for one serialized capture.
- [ ] A second content-distinct replay repeat closes the evidence gate.

## Implementation rule after approval

The pure `PenetrationCaptureEvaluator` now lives in Core. It accepts only the
privacy-safe aggregate shape described above, enforces all bounds and gate
reasons, and refuses promotion until a content-distinct repeat passes. It has
no IO, Win32, memory, logging, or persistence dependency.

The coordinator-owned `IPenetrationCapture` port now accepts only a decode-run
identity plus the fixed phase intent. GameIntegration validates the current
offline authorization, managed artifact/session association, completed decode
run, and exact executable identity before invoking an internal aggregate-only
source seam. The default source is deliberately neutral until exact semantic
fields are proven; no caller-controlled address, module base, pointer, or raw
observation can enter the source. The adapter serializes capture attempts,
limits the complete operation to 300 seconds, and retains only one in-memory
positive witness for the required content-distinct repeat. A restart or
promotion clears that witness, so it cannot become durable evidence by accident.

Do not add `WeaponState`, `AimState`, or armor-layer shared contracts yet.
After owner approval, replace the neutral source with the smallest proven
exact-build evidence implementation and run the approved capture. Only proven
fields may then be promoted into additive Core/Application/API ports;
unsupported fields remain explicit readiness reasons.

The existing v0.3 neutral readiness and provenance gates remain unchanged until
that promotion review.
