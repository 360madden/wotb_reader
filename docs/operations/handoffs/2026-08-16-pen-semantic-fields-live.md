# Penetration v0.3 — semantic-field live snapshot (first replay)

**Date:** 2026-08-16 (UTC)
**Status:** first-replay live snapshot positive for published marker +
reload enum + hull-independent marker yaw bins; nothing promoted
**Blocker:** `BLK-0027` (open — second content-distinct replay, elevation
isolation, shot join, and loaded shell remain)

## What changed

Additive `EntityRecordRegionAnchor.PenSemanticFields` on the existing
entity-region endpoint (no new public route). After the ownership walk
resolves, the coordinator two-pass-reads:

- published gun-marker at `rotator+0x50` (7 float32)
- VehicleGun reload/state from `+0x3C` (20 bytes)
- entity-base hull yaw at `+0x50`

`RegionBytes` stays null. The response carries only walk booleans, finite /
unit / range / stability flags, the reload enum, and investigation
yaw/pitch diagnostics. Script
`scripts/capture-pen-semantic-fields.ps1` samples the anchor and prints
counts plus 16-sector yaw bins (no addresses or raw coordinates).

## What ran

One exact-build managed offline replay reached `OfflineReplayVerified`
(battle session `01a00cf2-358b-75b4-8186-579afba06758`). Two capture
passes ran in-session:

| Pass | Samples | Walk | Reload in 0..9 | Marker finite/unit/stable | Marker yaw bins | Hull yaw bins | Independent windows |
|---|---:|---:|---:|---:|---:|---:|---:|
| 1 | 16 | 16 | 16 (enums 0,9) | 16/16/16 | 1 | 1 | 0 |
| 2 | 40 | 40 | 40 (enums 0,3) | 40/40/40 | 4 | 1 | 8 |

Pass 2 is the T1 window: hull yaw stayed in one 16-sector bin while the
published marker direction crossed four bins, with eight same-hull /
changed-marker steps. Reload enum changed (`0`/`3`/`9`) and is therefore
not a loaded-shell identity.

## Honest limits

- One replay only. A second content-distinct positive is required.
- Elevation was not isolated (no pitch-only window).
- No decoded shot join. The marker is a live client gun-marker, not a
  proven muzzle origin.
- CAM-013 was not used as a success criterion.
- Nothing was promoted. The badge stays `NotReady`.

## Validation

- Coordinator: 11 focused tests (walk + two new semantic-field cases).
- Host.Web: 1 new endpoint parse/echo test.
- Live: `OfflineReplayVerified` + 56/56 snapshots, 0 sample errors.
- Game and host were stopped after the reads.

## Next step

Repeat the same script on a second content-distinct replay. Then add
pitch-only isolation and a decoded shot join before any G2 contract.
