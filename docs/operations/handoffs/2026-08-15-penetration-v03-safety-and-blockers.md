# Penetration v0.3 safety slice and exact-input blockers

**Date:** 2026-08-15 (UTC)

**Status:** safe operability implemented; fidelity completion blocked by exact inputs

## Repository state

- Branch: `main`
- Baseline: `af418b0`
- Intended checkpoint: penetration v0.3 readiness, association, provenance,
  interval mechanics, diagnostics, evidence research, tests, and durable plans.
- Unrelated `?? .agents/skills/autorun/` was present before this work and is
  intentionally excluded.

## Owner request and outcome

The owner requested completion of the thirteen previously identified
penetration-UI weaknesses. The work began with an optimal evidence-gated plan
and used bounded specialists for architecture/session tracing, security,
unknown armor semantics, and weapon/aim source research.

The non-speculative portion is implemented. The repository does not claim that
all thirteen fidelity items are complete: exact armor/layers, loaded shell,
configured gun, exact shot ray, representative coverage, accuracy, and corpus
breadth remain blocked under BLK-0027. Real frames therefore render a neutral
v0.3 readiness reason instead of a colored nominal/manual/camera verdict.

## Implemented

- Added an invariant `PenetrationAssessment` envelope and stable readiness
  reasons across Core, Application, additive HTTP contracts, and WPF.
- WPF displays neutral readiness text, labels legacy hosts explicitly, and
  atomically clears an old colored badge when a live response becomes
  unavailable, stale, mismatched, or denied.
- Bound live penetration to a coordinator-owned exact managed artifact/session
  lease. WPF selection no longer selects the live decoded projection; a query
  session is assertion-only. The same opaque lease is validated after the
  projection and guarded read to close the launch-replacement race.
- Launch validation requires the exact requested session, matching decode-run
  ID, succeeded/completed run, no failure fields, and exact source artifact.
  Registration derives artifact identity from the suspended launch lease.
- Added strict physical-target-first enemy eligibility. A nearer ally blocks an
  enemy behind it; unknown own/target team and allied targets are neutral.
- Replaced probability language with the officially supported +/-5% interval:
  guaranteed penetration, uncertain/marginal, or guaranteed no penetration.
  No RNG distribution or exact probability is claimed.
- Added armor/weapon/aim input provenance. Real nominal armor, static/manual
  shell choice, and camera aim cannot produce v0.3 readiness.
- Isolated the scorer's primary cohort: only exact gun ray, exact loaded shell,
  and exact ordered-layer provenance qualify; legacy rows retain explicit
  confounds and remain diagnostics-only.
- Preserved collision `HardJointIndex`, added pure stable/mixed/missing key
  analysis, and added ordered multi-hit raycasts without assigning physical
  layer semantics.
- Added exact-build compatibility manifests and a redacted read-only
  `/api/v1/penetration/diagnostics` surface. Every consumed DVPL source is
  re-read/re-hashed before cache reuse; responses expose relative resource
  paths, hashes, counts, and stable issues only.
- Hardened the harness preflight to require both
  `OfflineReplayVerified` and `session.offline_replay_verified`.
- Removed PID disclosure from the game-start response and managed-launch
  lifecycle-timeout log.

## Evidence verdicts

### Armor/layers

Exact-build analysis rejected the tempting hard-joint mapping. The current
`PushNormalsArmorConfiguration` consumer rebuilds visualization surfaces and
emits `ArmorNodeMaterial`; the sole builder supplies an empty map. No
`armor_N` producer, millimeter unit, physical traversal, or ordered layer
accumulation exists. Do not join the sampled stable 1..16 key domain to XML.

### Weapon and aim

Exact-build anchors make `VehicleGun` and `VehicleGunRotator` the strongest
sibling owner candidates, with `AvatarGunAgent` acting as a bridge. Static
analysis does not prove loaded-shell/configured-gun fields or turret/gun/muzzle
transforms. The smallest next campaign is a fresh managed-offline unique-owner
census, shell A->B->A transition proof, stationary-hull yaw/elevation
discrimination, shot-ray geometry validation, and unchanged repetition on a
second content-distinct replay. No offset is promoted and no live action was
performed in this checkpoint.

## Validation

- Release solution build: PASS, 0 warnings / 0 errors.
- Focused/full changed-area suites before final gate: Core 288/288;
  Application 97/97; GameIntegration 351 passed with 6 expected opt-in skips;
  Host.Web 185 passed with 1 expected opt-in skip; GameHarness 42/42; WPF
  overlay 124/124; Bootstrap composition 10/10.
- Installed-game read-only hard-joint characterization: 3 sampled vehicles,
  14 parts, 3,424/3,424 triangles with stable integral per-triangle keys and no
  mixed/missing keys. This is format characterization only, not armor meaning.
- `dotnet format --verify-no-changes`: PASS.
- `git diff --check`: PASS.
- Full `scripts/validate.ps1`: PASS — 1,245 .NET tests passed with 8 expected
  local opt-in skips; Release build 0 warnings / 0 errors; repository/privacy,
  Codex policy 10/10, PowerShell analysis and smoke tests, offline pack,
  blocker/ledger consistency, and offset schema/chains checks all green.

## Remaining work

1. Security-review and owner-approve the bounded managed-offline weapon/aim
   capture contract before any new memory read.
2. Prove or reject unique `VehicleGun`/`VehicleGunRotator` ownership and exact
   semantics across two content-distinct replays.
3. Investigate one alternative authoritative armor/layer owner; do not repeat
   the rejected hard-joint visualization path.
4. Only after exact inputs exist, build the frozen corpus: at least 12 replays,
   500 eligible shots, and 30 rows per important cohort, then apply the
   coverage/accuracy/ricochet/interval gates in `penetration-v0.3-plan.md`.
5. Harden final rendezvous capability-file ACL/post-move verification as the
   adjacent security follow-up recorded in `next-10-actions.md`.

## Durable references

- `docs/operations/penetration-v0.3-plan.md`
- `docs/operations/blocker-log.md` — BLK-0027
- `docs/operations/product-roadmap.md` — Phase 6 fidelity correction
- `docs/operations/next-10-actions.md`
