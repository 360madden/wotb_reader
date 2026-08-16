# Penetration UI v0.3 fidelity and operability plan

**Date:** 2026-08-14 (UTC)
**Status:** safety/operability implemented; exact-input and corpus gates blocked
**Baseline:** `v0.2.0-alpha` (`403b9ca`)

## Objective

Replace the current nominal, manually configured penetration prototype with a
provenance-complete managed-offline-replay indicator whose coverage, accuracy,
calibration, failure reasons, session binding, build compatibility, and enemy
scoping are measured rather than assumed.

This plan owns the thirteen accepted weaknesses. Completion is not satisfied
by retaining the current nominal armor, stock-gun/manual-shell, CAM-013 proxy,
or silent `Unknown` behavior under new labels.

The supported product boundary remains managed offline replay playback.
Arbitrary online-battle operation is outside scope and remains denied by the
existing authorization boundary.

## Completion thresholds

The final immutable validation corpus must meet all of these gates:

- at least 12 content-distinct replays and 500 eligible shots;
- at least 30 eligible rows for every claimed important shell/face cohort;
- at least 90% unconditional determinate coverage overall, with no planned
  scenario cohort below 80%;
- at least 90% classified band accuracy, with a 95% confidence lower bound of
  at least 85%;
- at least 90% ricochet precision and recall;
- probability expected calibration error no greater than 5%, if and only if
  the RNG distribution is independently proven; otherwise interval-boundary
  correctness replaces this gate;
- exact accounting of every skipped and indeterminate row by a stable reason;
- zero missing/confounded inputs in the primary accuracy cohort.

If the RNG range can be proven but its distribution cannot, the product must
remain a deterministic penetration indicator or display a bounded probability
interval. It must not claim an exact penetration chance.

## Dependency-ordered execution

### G1 - foundational source viability

Run four separate evidence investigations. Each returns a go/no-go verdict,
ranked hypotheses, rejected hypotheses, exact-build anchors, a minimal guarded
capture protocol, and a hard stop condition.

1. Exact armor and ordered layers: locate a provenance-safe plate/layer model
   with struck plate identity, thickness, geometry, order, and shell-family
   interaction. The already-inspected XML and three-part physics collision mesh
   are not sufficient.
2. Weapon state: prove the configured gun and loaded shell through controlled
   transitions. Replay packets do not contain this state.
3. Shot ray: prove shot-synchronous muzzle origin and gun direction, including
   turret yaw and gun elevation. CAM-013 remains a validation reference, not an
   admissible exact-input fallback.
4. RNG: prove the penetration randomization distribution independently of the
   known range before producing an exact probability. **Current primary-source
   verdict (2026-08-14): no-go for an exact percentage.** Wargaming's current
   Armor Penetration Mechanics page specifies the +/-5% penetration spread but
   does not specify a distribution or equal likelihood. Unless stronger
   primary evidence is found, v0.3 must implement guaranteed/uncertain/
   impossible interval semantics and remove exact-chance claims.

No downstream implementation may convert a failed discovery gate into guessed
coverage.

### G2 - proposed shared contracts

These are proposals only until the G1 evidence fixes their provenance and
freshness semantics. The lead reviews the final shapes before editing shared
contracts.

- `ArmorSurfaceModel`: exact-build/source identity, ordered surface/layer IDs,
  geometry, thickness, material/interaction kind, and confidence/provenance.
- `WeaponState`: configured gun, loaded shell, source, observation clock,
  freshness, and verification state.
- `AimState`: muzzle origin, normalized direction, turret/gun transforms,
  source, observation clock, uncertainty, and verification state.
- `PenetrationAssessment`: readiness/status reason, claimed model version,
  ordered impacts, shell inputs, probability or interval, band, diagnostics,
  and complete provenance.
- `PenetrationReadinessReason`: stable additive reasons for no aim, no target,
  missing/stale session, unknown team, weapon, armor or build, mesh miss,
  unsupported layer, stale clock, and invalid inputs.
- `ManagedReplayAssociation`: exact imported artifact/session binding plus
  managed-launch identity and freshness; never a heuristic latest-session
  selection.

Any guarded memory-read expansion receives a read-only security audit before
implementation. API changes remain additive while `v0.2.0-alpha` consumers
exist.

### G3 - safety and operability

Implement the non-speculative improvements before expanding colored verdicts:

- carry explicit readiness and reason state through Core, Application, API,
  and WPF; render neutral diagnostics while prohibiting colored badges for
  non-ready states;
- automatically associate a fresh managed offline replay with its exact
  decoded session and fail closed on absent, stale, or mismatched association;
- require proven own-team and target-team identities before enemy eligibility;
- add exact-build compatibility manifests, source hashes, cache invalidation,
  vehicle-resolution coverage reports, and unmatched-ID diagnostics.

### G4 - exact model

After G1 and G2 pass, implement ordered finite-surface traversal, exact
thickness, layered/spaced interactions, tracks/screens where proven, exact
weapon state, shot-synchronous aim, normalization, ricochet/overmatch, range
loss, and the proven probability or interval semantics. Unsupported mechanics
remain explicit readiness reasons and are not included in determinate coverage.

### G5 - evidence and scorer

Every `ShotEvidence` row must identify the exact game build, gun, shell,
synchronized shot ray, ordered armor impacts, decoded outcome, clock
uncertainty, and all input provenance. Only rows with no confounded or missing
field enter the primary metric. Partial rows remain visible in separate
coverage and diagnostic reports.

### G6 - breadth, review, and release

Build the representative corpus, run cohort and calibration reports, conduct
evidence and correctness reviews, run focused and installed-game tests, perform
the documented owner HUD smoke, pass `scripts/validate.ps1`, update the roadmap
and immutable handoff history, and create a focused local milestone commit.

## Thirteen-item completion map

| Item | Required result | Primary gate |
|---|---|---|
| 1 | Exact plate/layer thickness and provenance; no nominal fallback counted | G1/G4 |
| 2 | Fresh configured-gun and loaded-shell state | G1/G4 |
| 3 | Coverage thresholds and exact unknown-reason accounting | G3/G6 |
| 4 | Accuracy, ricochet, and calibration thresholds | G5/G6 |
| 5 | Shot-synchronous muzzle/gun ray; no camera fallback counted | G1/G4 |
| 6 | Proven probability distribution or honestly bounded/renamed semantics | G1/G4 |
| 7 | Ordered layered armor behavior for every claimed layer type | G1/G4 |
| 8 | Representative immutable validation corpus | G5/G6 |
| 9 | Provenance-complete, unconfounded primary scorer cohort | G5 |
| 10 | Neutral, explicit failure/readiness UI and API reasons | G2/G3 |
| 11 | Automatic exact managed-replay/session association | G2/G3 |
| 12 | Exact-build manifests, drift detection, and resolution diagnostics | G3 |
| 13 | Strict proven-enemy filtering; unknown team never receives a verdict | G3 |

## Rejected shortcuts

- Do not count guessed nominal armor as expanded coverage.
- Do not treat a manual shell selection as observed weapon state.
- Do not label CAM-013 camera direction as a shot-synchronous muzzle ray.
- Do not call a deterministic margin band an exact probability.
- Do not import or redistribute third-party armor data without an explicit
  provenance, licensing, exact-build, and privacy decision.
- Do not broaden the feature into online operation.
- Do not spend approved live launches until the offline discriminators and
  capture contracts are ready.

## Execution status — 2026-08-15 UTC

The safe, non-speculative portion of v0.3 is implemented and locally verified:

- items 6, 10, 11, 12, and 13 are implemented: the model exposes guaranteed /
  uncertain / impossible interval bands rather than an invented probability;
  every unavailable state has a neutral readiness reason; live frames bind to
  the exact managed artifact/session association rather than WPF selection;
  consumed sources are re-hashed into an exact-build manifest with a redacted
  diagnostics endpoint; and unknown/allied physical targets fail closed;
- item 9's primary cohort is provenance-gated. Nominal armor, manual/static
  shell selection, and camera aim remain visible only as confounded diagnostic
  rows and cannot inflate primary accuracy;
- the collision parser now preserves the optional hard-joint attribute and a
  pure analyzer reports stable/mixed/missing triangle keys without assigning
  armor meaning. Real-install read-only characterization found integral stable
  keys across the sampled vehicles, but the exact-build code trace falsified
  their use as a current `armor_N`/millimeter source.

Items 1, 2, 3, 4, 5, 7, and 8 are not complete and must not be relabeled:

- exact per-face thickness and ordered physical layers have no proven source;
- exact loaded shell/configured gun and shot-synchronous gun ray have viable
  exact-build owner candidates (`VehicleGun` and `VehicleGunRotator`) but no
  proven semantic fields; they require one bounded managed-offline capture
  followed by content-distinct repeatability before any product wiring;
- without those inputs, the 12-replay / 500-shot representative primary corpus
  and its coverage/accuracy thresholds cannot be produced honestly.

The product therefore renders v0.3 as `NotReady` on real data instead of
retaining the v0.2 nominal/manual/camera verdict under a stronger label.

### G1 exact-input source verdict and capture boundary — 2026-08-15

The exact-build static investigation is complete as a bounded **no-go for
immediate wiring**. RTTI identifies `VehicleGun`, `VehicleGunRotator`, and
`AvatarGunAgent`, but static evidence does not prove viewpoint ownership,
configured-gun identity, loaded-shell identity, turret/gun transforms, muzzle
origin, or shot direction. The rejected hard-joint visualization path remains
closed and is not revisited.

The next step is the owner-approved exact-build capture described by the
frozen, privacy-reviewed managed-offline contract:
[`penetration-v03-managed-capture-contract.md`](penetration-v03-managed-capture-contract.md).
The coordinator-owned `IPenetrationCapture` adapter is now implemented. It
accepts only a decode-run identity and fixed phase intent, rechecks the
`OfflineReplayVerified` lifecycle, managed artifact/session association,
completed decode run, module identity, and exact executable build, then passes
only a bounded aggregate to the pure `PenetrationCaptureEvaluator`. Its
production source is deliberately neutral until semantic fields are proven;
no exact-input port or colored v0.3 verdict is enabled. Owner approval is
still required before a live source implementation or capture. The separate
armor-owner triage is recorded in
[`pen-v03-alternative-armor-owner-triage.md`](pen-v03-alternative-armor-owner-triage.md)
and remains a no-go until a producer proves physical layer semantics.

### Owner-census source implemented — 2026-08-16

With owner approval granted, the contract's "smallest proven exact-build
evidence implementation" is now in place. The three weapon-family vftable RVAs
are derived hash-bound (`FindVftableViaCol.java`, SHA-256 `1cda5c31…`):
`VehicleGun` `0x32dacf4`, `VehicleGunRotator` `0x32eeb40`, `AvatarGunAgent`
`0x324dae8`. `ExactBuildOwnerCensusCaptureEvidenceSource` performs the Phase-1
owner census via the gated vftable AOB scan (two passes, aggregate-only,
privacy-safe counts) and is the registered `IPenetrationCaptureEvidenceSource`;
the shell/aim/ray phases remain unproven. `POST
/api/v1/game/discover/pen-capture` triggers the serialized capture from an
opaque `decodeRunId`. The live census capture, the viewpoint-ownership walk,
and the shell/aim/ray field proofs remain outstanding; no exact input is
promoted or wired into the badge yet.

### Ownership walk static derivation — 2026-08-16

The census counts distinct objects (primary vftable dword once per object; the
`+0x8`/`+0xC` slots are secondary sub-vtables). The static half of the walk is
now derived hash-bound (`pen-ownership-walk-proof-protocol.md`): the
`AvatarGameLogic` object owns `VehicleGun` at `+0x204` and `VehicleGunRotator`
at `+0x1fc` (rotator is avatar-only via the `+0x200` marker), with the
rotator's `+0x10` back-pointer and the inherited `+0x04` entity link. The
bounded five-read live-validation protocol (unique rotator → owner → forward
round-trip → gun vtable → entity HP, two passes, fail closed) remains to be
run before the phase 2–4 semantic field offsets (configured gun, loaded shell,
turret yaw, gun elevation, muzzle ray) are derived.
