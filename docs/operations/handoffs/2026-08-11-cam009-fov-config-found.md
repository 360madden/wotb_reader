# CAM-009 — the game's numeric FOV is in the installed config (optionsGlobal/optionsDesktop)

- Date: 2026-08-11
- Status: committed; read-only discovery, no live session needed
- Supersedes: the "FOV is config/res-driven, the exe cannot yield the
  numeric FOV" open item in `record-diffing-groundwork.md` — the config IS
  readable (DVPL/LZ4, same files the product's `DvplReader` opens)

## The finding

Read-only DVPL decompression (LZ4 block format, 20-byte footer) of the
installed 11.19.0.10 data:

**`Data/optionsGlobal.yaml.dvpl`** — the engine camera tuning, whose property
keys match the exe strings found earlier (`default fov`, `camo fov`,
`showcase fov`, `Movement FOV offset (deg)`, `Movement FOV multiplier`):

| Key | min | max | value |
|---|---|---|---|
| `default fov` | 10 | 120 | **64** |
| `min fov` | 10 | 120 | 30 |
| `max fov` | 10 | 120 | 64 |
| `camo fov` | 10 | 120 | 64 |
| `showcase fov` | 10 | 120 | 64 |
| `Movement FOV multiplier` | 1.0 | 3.0 | 1.0 |
| `Movement FOV offset (deg)` | 0 | 30 | 1.0 |
| `Camera FOV mult` | 0.1 | 5.0 | 1.6 |
| **`horizontal to vertical radius coefficient`** | 0.1 | 10.0 | **0.73** |

A second camera-mode block uses `default fov` **60** (`limited rotation fov`
30–60).

**`Data/optionsDesktop.yaml.dvpl`** — the player-facing slider:
`Default fov` 40–60°, default **54°**; `Camera backward fov offset` **8°**
(the third-person camera reduces the FOV by 8°).

## Interpretation for the overlay

- The engine FOV values are **horizontal** (the explicit
  `horizontal to vertical radius coefficient` 0.73); the overlay's
  `WorldToScreen.Project` consumes VERTICAL FOV, so vertical ≈ 0.73 × value
  ≈ **47°** at the 64° battle default (≈ 44° at the 60° mode block).
- `verify-camera-projection.py`'s FOV band widened from 70–110° to
  **40/47/64/90°** so the sweep covers the config-backed candidates; the
  look-at check is FOV-independent, so the verdict does not hinge on the
  exact convention.
- The live CAM-007 session remains the tie-breaker for the exact convention
  (the pitch diagnostic + center-distance across the band settle whether 64
  is horizontal or vertical as encoded).

## Verified

- LZ4 block decoder (stdlib-only) round-trips DVPL files; CRC/size fields
  from the footer match the repo's `DvplReader` contract.
- Values above extracted from the on-disk `Data/*.yaml.dvpl` (read-only).

## Next steps

- CAM-007 live session: with the 40–90° band, the verdict pins the
  convention and gives the overlay its exact vertical FOV per mode.
- Optional product change: the frame endpoint's `fov` default (90°) should
  become the config-backed value (~47°) once the live verdict confirms it.
