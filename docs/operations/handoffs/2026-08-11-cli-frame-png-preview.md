# CLI overlay-frame PNG preview (2026-08-11)

**Status:** ✅ committed and pushed (tree clean, full gate green).

## Why

The overlay camera track is code-complete but every HUD-layout iteration
needed a live game session to *see* the frame. `overlay-frame` only dumped
JSON, so layout/occlusion problems (nameplate overlaps, beacon collisions,
panel placement) were invisible offline. This adds a schematic PNG render so
the projected frame can be eyeballed against any decoded replay, no game.

## What landed

- **`Host.Cli/Rendering/PngEncoder.cs`** — minimal PNG (RGBA8) encoder: IHDR,
  one IDAT (zlib: 0x78 0x9C + DeflateStream + adler32), IEND, correct CRC32
  per chunk. Pure BCL — no image library, portable like the CLI itself.
- **`Host.Cli/Rendering/BitmapFont5x7.cs`** — 5x5 bitmap font (A-Z, 0-9,
  punctuation) for schematic labels; unknown chars render blank.
- **`Host.Cli/Rendering/FrameRasterizer.cs`** — deterministic RGBA rasterizer:
  dark viewport + center crosshair, beacon diamond markers + labels (color
  parsed from `#RRGGBB`), event pips, nameplate panels (team border, HP bar,
  label; dead = grey). Draw order mirrors the overlay (beacons → pips →
  nameplates) so overlaps are visible.
- **CLI wiring** — `overlay-frame <time> --session <guid> [--fov --width
  --height] [--png <path>]`; writes the PNG and reports `pngPath` in the JSON
  envelope. `CliInvocation.OptionRequiresValue` gained `png`.
- **Tests (10 new)** — PngEncoder: structural validity (signature, chunk
  order, per-chunk CRC, zlib round-trip + adler32), determinism, dimension
  validation; FrameRasterizer: background/crosshair, nameplate panel/border/
  HP bar/label pixels, dead-grey, beacon marker+label, off-screen skip; CLI:
  `--png` writes a valid-signature file with `pngPath`, empty `--png` path is
  rejected. CLI suite 35 → 45.

## Verified

- `dotnet test tests/WotBTreader.Host.Cli.Tests` — 45/45.
- Full `scripts/validate.ps1` gate green (901 passed, 3 skips, 0 warnings).
- Offline file-tree regenerated (5 new files).

## Remaining

- The live CAM-001 v7 session → projection verdict (FOV convention + pitch
  sign) remains the single outstanding camera-track gate; the `--png` preview
  is the offline way to iterate the HUD meanwhile.
- Possible follow-ups: `--png` could later accept a minimap inset, or a small
  `--frames <start>:<end>:<step>` strip for motion preview.
