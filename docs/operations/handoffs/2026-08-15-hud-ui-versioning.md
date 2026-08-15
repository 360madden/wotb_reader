# HUD UI versioning baseline

**Date:** 2026-08-15 (UTC)

**Status:** version label and semantic-versioning rules implemented; visual smoke test remains open

## Repository state

- Branch: `main`
- Product baseline: `0.2.0-alpha`
- HUD UI baseline: `0.1.0-alpha`
- No loopback/API contract change was made.
- Existing unrelated dirty paths remain untouched: the modified older handoff and
  untracked `.agents/skills/autorun/` directory.

## Implemented

- Added an independent HUD presentation version label: `HUD UI v0.1.0-alpha`.
- Displayed the label in the WPF HUD sidebar below the connection status.
- Documented the version boundary and bump rules in
  `docs/architecture/overview.md`:
  - major for incompatible HUD interaction/rendering changes;
  - minor for additive HUD surfaces;
  - patch for visual/accessibility/correctness fixes;
  - alpha remains appropriate while visual smoke and evidence-gated product
    work are incomplete.
- Added a regression test proving the display version remains independent of
  the product and penetration feature versions.

## Validation

- WPF overlay tests: **139 passed**.
- Release solution build: **0 warnings, 0 errors**.
- `dotnet format WotBTreader.sln --verify-no-changes --no-restore`: PASS.
- `git diff --check`: PASS.

## Remaining

1. Run a supervised Windows visual smoke test and record the HUD UI version in
   the result.
2. Bump the HUD UI version in the same change as each visible layout,
   rendering, or interaction surface change.
3. Keep colored penetration results blocked by BLK-0027 until exact inputs and
   the representative corpus are proven.
