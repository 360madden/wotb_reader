# Penetration prototype `v0.2.0-alpha` baseline

**Date:** 2026-08-14 (UTC)

**Status:** version assigned; validated prototype baseline

## Decision

`v0.2.0-alpha` is the first explicitly versioned armor-penetration UI
prototype. `Directory.Build.props` owns the solution-wide .NET version as
`VersionPrefix=0.2.0` plus `VersionSuffix=alpha`; the annotated Git tag of the
same name identifies the immutable baseline.

The earlier `v0.1.0-alpha` tag remains the historical architecture-hardening
release. It predates the penetration feature and is not reused for this
prototype.

## Included feature boundary

- installed-data armor, gun, shell, and collision-mesh extraction;
- collision raycast with the struck surface normal;
- incidence, normalization, ricochet/overmatch, range-adjusted penetration,
  and banded verdict math;
- enemy/alive target selection and fail-closed `Unknown` behavior;
- colored and numeric WPF badge on the aimed nameplate with reticle fallback;
- manual stock-gun shell selection through the sidebar or `Q`;
- replay and live-frame consumption of the CAM-013 chase-camera aim;
- the PN-4 scorer and two content-distinct live replay validations.

The baseline feature implementation is commit `1a899eb`. The version commit
and `v0.2.0-alpha` tag include the later repository hardening through
`2edabee` plus this version declaration and release record.

## Evidence at assignment

- medvedkovo: 150 G2-proven aim samples; classified band accuracy improved
  from 69.565% to 72.727%, and ricochet precision from 66.667% to 80.000%.
- savanna / Churchill: 161 G2-proven aim samples; classified band accuracy
  improved from 38.889% to 46.667%, removing six false center-line ricochet
  predictions.
- Both managed launches reached `OfflineReplayVerified`; unsupported inputs
  remained `Unknown` rather than becoming guessed verdicts.
- The final PN correctness audit fixed cache identity, unsupported-part,
  mesh-miss, path validation, shell-identity, selection-notification, request
  loop, and initial-frame defects before this version was assigned.

## Honest alpha limits

- Thickness is nominal per supported part; accessible install data cannot map
  every collision face to an exact thickness.
- Unsupported side, rear, deck, and mesh-miss cases fail closed.
- Loaded-shell identity is not decoded; the selected stock-gun shell is a
  manual product input.
- The verdict is deterministic and does not claim the exact per-shot
  penetration RNG outcome.
- Exact turret/gun lock-on discovery remains optional model-fidelity research.

## Iteration policy

- `0.2.x-alpha`: fail-closed corrections, operability work, coverage evidence,
  and compatible UX hardening within this feature contract.
- `0.3.0-alpha`: a material capability or model expansion, such as proven
  runtime turret orientation or a provenance-safe armor-fidelity source.
- `1.0.0`: reserved for an owner-approved product contract, completed release
  smoke, and evidence thresholds that no longer carry an alpha qualifier.

## Next

The immediate gate is the owner ship review plus the five-step manual HUD
smoke in `2026-08-14-pn4-second-replay-regression.md`. Subsequent work should
branch from or compare against the `v0.2.0-alpha` tag so changes to accuracy,
coverage, and failure behavior remain attributable.

## Assignment validation

- MSBuild evaluates the overlay project version as `0.2.0-alpha` with
  `VersionPrefix=0.2.0` and `VersionSuffix=alpha`.
- `scripts/validate.ps1`: passed.
- Release build: 0 warnings, 0 errors.
- Tests: 1,206 passed, 7 local opt-in skips, 0 failed.
- Completion/replay-selection/camera/batch PowerShell suites: 14/14, 16/16,
  4/4, and 9/9.
- Repository scan, PSScriptAnalyzer hygiene, offline pack freshness and links,
  blocker/ledger consistency, and offset schema/chains validation: passed.
