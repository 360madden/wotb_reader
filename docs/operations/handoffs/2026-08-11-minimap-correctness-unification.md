# Minimap correctness: unification, camera marker fix, texture gap (2026-08-11)

**Status:** ✅ committed and pushed (tree clean, full gate green, 925 tests).

## Why

The god-view minimap is the HUD's spatial anchor, and its math existed in two
places while two latent defects hid behind it. This workstream unified the
math into one tested contract, found and fixed a real rendering bug, and
investigated the map-texture path — which turned up an honest, documented gap.

## What landed

1. **One normalization contract** (`32eb275`): the overlay's `MinimapMath`
   and the CLI preview's private `Normalize` implemented the same
   world→minimap math separately. Moved to `ApiContracts.MinimapNormalizer`
   — the layer both the loopback overlay client and the host CLI may
   reference (Core is unreachable from the overlay by the enforced reference
   graph; the CLI gained the already-approved ApiContracts reference). The
   rasterizer consumes it (clamp/inset stays local); `MinimapMath` delegates.
   4 tests moved to the overlay test project.

2. **HUD minimap camera marker bug fixed** (`f9921c3`): the camera ring was
   drawn at raw world meters × panel pixels (`cameraX * 150`) while every
   dot used normalized 0..1 coords — a camera at world (-80, 67) placed the
   ring ~12,000 px off the 150-px panel, so the "where am I" marker was
   effectively invisible since the minimap shipped. Normalized through the
   shared contract in `BuildMinimap`; fail-closed (no boundary/no camera →
   no ring). The existing test had pinned the buggy raw pass-through; the
   regression now pins the corrected panel-center position.

3. **Minimap texture resolution gap pinned** (`a24ca54`): investigating the
   dot-vs-terrain alignment invariant surfaced that decoded map IDs are
   numeric arena identities (Oasis = `11`, Dead Rail = `7`) which
   `MapMinimapFolder` passes through unchanged — never matching the
   install's name-based minimap folders. This install also ships **no
   Oasis Palms or Dead Rail texture at all** (55 folders enumerated, no
   config references in decodable DVPLs), and variant folders
   (`desert_train_02`) are stripped too. The texture therefore fails closed
   for real replays (blank panel, dots only), and the alignment invariant
   remains unverified against a real texture. Pinned with
   `MinimapTextureFolderTests` (3 tests); roadmap V4 claim corrected
   honestly.

## Verified

- Full `scripts/validate.ps1` gate green: 925 passed, 3 skipped, 0 warnings
  (Architecture 19/19, Overlay 99/99, CLI 52/52, Web 144+3).
- Real-data renders: Oasis Palms previews with minimap inset (13 tank dots +
  camera crosshair) and a 12-frame contact sheet with per-cell time labels.

## Remaining

- **Arena-id → minimap-folder mapping**: blocked on data this install
  doesn't contain (no config references, no Oasis/Dead Rail textures).
  Possible leads: larger DVPL packs, the replay's own battle info, or an
  alternate resource root.
- The live CAM-001 v7 session → projection verdict (FOV convention + pitch
  sign) remains the single outstanding camera-track gate.
