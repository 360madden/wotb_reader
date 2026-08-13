# Pen-chance HUD — PN-3 shipped end-to-end (2026-08-13)

**Phase 6** (`docs/operations/pen-chance-design.md`). Offline, no launch.

## What shipped (3 commits)

- `4ab6d6d` — install armor/shell/gun data parsers (`PenetrationDataParser`
  + `ArmorGroup`/`VehicleArmorProfile`/`ShellProfile`/`GunShellProfile`
  contracts + `ParseStockGunShellName`), 10 synthetic-fixture tests.
- `3657d37` — `PenetrationBadge`/`StruckFace`/`PenetrationAim.ResolveBadge`
  (+ shared `SelectStruckFace`), 17 tests. A struck face whose nominal armor
  is 0 (unknown) fails closed to `Unknown` — never a fabricated verdict.
- `960b2e3` — the full wiring: `IOverlayPenetrationData` +
  `PenetrationContext.NominalArmor` (Application), `PenetrationDataService`
  (GameIntegration, reads the install read-only via `DvplReader`), the badge
  threaded through `ReplayFrameSource` → `OverlayFrameProjector` →
  `OverlayFrameResponse` → the WPF HUD's reticle-centered green/yellow/red
  badge with the numeric readout. DI: `IOverlayPenetrationData` registered in
  `AddGameIntegration`, consumed optionally in `AddWotBTreaderApplication`.

## Honest limits (recorded, never hidden)

- **Front-only armor** — the vehicle XML declares the FRONT via
  `primaryArmor` (front = thickest named group); side/rear are not declared,
  so they stay 0 = unknown and the badge fails them closed.
- **Stock AP shell** — the loaded shell is not decodable; the viewer's stock
  gun's first shell (guns.xml `piercingPower` pair + shells.xml
  caliber/normalization/ricochet) is the default.
- **Nominal thickness** — no plate slope/normal; the `.scg` collision
  geometry (PN-5) is the open accuracy gap.

## Verified

Full `scripts/validate.ps1` gate green: 1120 tests passed (3 local opt-in
skips), 0 warnings, 0 errors; format/analyzers/build/scan/PSScriptAnalyzer/
offline-pack/offset-schema all pass.

## Remaining (PN-4 is the next proof)

PN-4 (score the model vs decoded shots) is gated on two offline prerequisites:
1. **Plate-slope `.scg` parser** (`Data/3d/Tanks/CollisionMeshes/{nation}-{tank}.scg.dvpl`,
   SCPG `PolygonGroup` KeyedArchive binary) — the cheap hull-facing proxy was
   already shown too coarse (uniform 0–80° incidence on a real 121-damage
   session).
2. **Type-8 flag-byte / type-32 decode-lane surface** — disambiguate
   bounce-vs-absorb per shot (the evidence offsets are archive-relative, so
   this is a decoder-side change, not a file seek).
