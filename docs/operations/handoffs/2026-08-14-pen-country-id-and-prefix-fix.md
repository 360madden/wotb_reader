# Pen-chance — country-id + nation-prefix resolution fix (2026-08-14)

**Phase 6** (`docs/operations/pen-chance-design.md`). Offline, no launch.
Continues `2026-08-13-pen-shotimpact-attacker-attribution-decoded.md`.

## What shipped

The pen badge's armor/shell/mesh resolution was silently failing for the
REAL decoded pipeline (every nation except germany). Two defects, both fixed:

1. **Wrong country-id table.** The vehicle compact descriptor is
   `descriptor = (vehicleTypeId << 8) | countryId` with
   `countryId = (index << 4) | 1` in the game's Country enum order
   (germany=1, ussr=17, usa=33, china=49, france=65, uk=81, japan=97,
   european=113, other=129). The enrichment enumerated nations 0–8, which
   matched only germany (=1) — every other nation's descriptor missed, so
   its participant fell back to a raw numeric `tank_id` and armor/mesh/name
   resolution failed. `InstalledGameMetadataProvider.Nations` now carries the
   real country ids.
2. **`nation:tank` prefix not split.** The enrichment's `VehicleId` is
   `nation:tank` (e.g. `germany:PzIV`), but `PenetrationDataService` treated
   the whole string as the tank file name, so even correctly-enriched tanks
   never matched an install path. `ResolveNationAsync` now splits the prefix
   (and verifies the file), and the callers pass the BARE tank name to the
   armor/mesh/shell path builders.

## Evidence

- Replay descriptors (ground-truth 11.19.0.10 session): germany=1
  (PzIV `0`/Nashorn `46`), uk=81 (GB08_Churchill_I `11` → `2897`,
  GB63_TOG_II `210` → `53841`), and the observed 0xN1 spacing across the
  roster (1/17/33/81/97/113/129).
- Opt-in (real install): `1057` → `usa:M4_Sherman` and `2897` →
  `uk:GB08_Churchill_I` both resolve (the latter is the viewpoint tank in
  all 284 decoded sessions).
- 338 GameIntegration tests green (incl. a new nation-prefixed resolution
  test), full `validate.ps1` gate exit 0.

## Remaining (unchanged)

Live PN-4 (CAM-013 aim at shot time) — the center-line proxy still cannot
validate the ricochet rule (incidence < 45° offline). `pen-score` CLI
wiring, single-launch OD cluster, `ConsistentDoubleRead` owner approval,
L4 replayTime + T1 turret-facing.
