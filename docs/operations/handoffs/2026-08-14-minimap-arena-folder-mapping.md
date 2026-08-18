# Minimap arena-id to texture-folder mapping — implemented and install-verified

**Date:** 2026-08-14 (UTC)
**Roadmap:** Phase 4 V4
**Base commit:** `1a899eb` (`feat(pn): ship live-validated penetration prototype`)

## Result

The V4 texture-resolution gap is closed. Numeric replay arena IDs no longer
flow directly into the name-based minimap directory probe. The exact-build
installed metadata already parsed the required mapping from `maps.yaml.dvpl`;
the application contract now preserves that relative scene path, and the web
service consumes it before reading the existing DVPL WebP texture.

Arena `1` was verified read-only against the configured 11.19.0.10 install:
metadata resolved the Karelia/Rockfield scene path, the service selected the
name-based `karelia` minimap folder, the DVPL payload decoded, and a valid PNG
was returned. No game file was modified or copied into the repository.

## Changes

- `MapMetadata.SceneResourcePath` additively preserves the parsed map
  `localName` evidence; both canonical and numeric lookup keys return the same
  immutable metadata record.
- `MinimapTextureService` now uses `IInstalledGameMetadataProvider`, retries a
  stale exact-build context once, and keeps the PNG cache executable-hash
  bound.
- Scene-path normalization removes the numeric prefix and locale suffix while
  preserving numeric variants such as `desert_train_02`.
- Raw numeric IDs never fall back to a guessed folder, and invalid or traversal
  components fail closed to the existing dots-only minimap.
- Focused tests cover the pure folder contract and the complete synthetic
  numeric-id -> metadata -> variant folder -> WebP -> PNG service path.
- The opt-in installed-game check covers the real arena-1 mapping and PNG
  decode without exposing or mutating the installation.

## Validation

- GameIntegration focused suite: 344 passed, 5 local opt-in skips.
- Host.Web minimap focus: 4 passed, 1 local opt-in skip.
- Installed-game map metadata check: 1/1 passed read-only.
- Installed-game numeric minimap check: 1/1 passed read-only.
- Full `scripts/validate.ps1`: 1,205 tests passed with 7 opt-in skips;
  Release build 0 warnings/0 errors; repository scan, PSScriptAnalyzer, Pester
  7/7 + 16/16, offline pack, blocker/ledger consistency, and 8-chain schema
  validation passed. The first run stopped only when the newly staged handoff
  made `offline/file-tree.md` stale; the tree was refreshed and the complete
  gate passed on the final staged state.

## Honest limits

- This exact install contains no savanna (arena `11`) or medvedkovo (arena
  `7`) texture, so the two current ground-truth replays still render the
  intentional dots-only panel.
- The texture transport is install-verified, but dot-versus-terrain alignment
  still needs a decoded replay for a texture-bearing map; it remains on the
  wait-list rather than being inferred from a service-only check.

## Next

The higher-ranked top-10 work remains launch- or owner-gated. The next
independent offline item is the launcher pre-flight reorder: consult the
persisted completion marker before the CLI version probe.
