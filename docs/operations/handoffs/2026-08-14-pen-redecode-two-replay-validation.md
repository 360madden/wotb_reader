# PN — raw-descriptor root cause + two-replay offline validation

**Date:** 2026-08-14
**Feature:** Armor penetration chance HUD (PN, `docs/operations/pen-chance-design.md`)
**Type:** verification + dataset extension (no source changes)

## Summary

Closed the "why are most participants raw compact descriptors?" question and
extended the offline pen validation to **two content-distinct replays**.

### 1. Raw-descriptor root cause: stale data, not a live bug

The store's `participants.tank_id` was a mix of `nation:tank` strings and
raw integers (e.g. `'17'`, `'2897'`). Traced to the **decode time**, not the
scorer: the `WotbReplayDecoder.EnrichAsync` → `ResolveVehicleAsync` path
already carries the corrected country-id table (commit `57bf929`); the
sessions in the store were simply decoded **before** that fix landed, with
the old 0–8 enumeration that matched only `germany` (=1).

Re-decoding the ground-truth source artifacts with `reprocess` confirms the
fix end-to-end:

- **savanna** (`8565111466734423`) → **14/14** participants enriched
  across all nine nations (`germany:PzIV`, `ussr:T-34`, `usa:M6`,
  `japan:Chi_Nu`, `uk:GB08_Churchill_I`, `other:Akawara`, `european:Cz18_…`).
- **medvedkovo** (`4897419848989129`) → **13/14** enriched. The one holdout
  (`17425`) is a premium vehicle absent from the base `list.xml.dvpl` — a
  known DLC-vehicle limit of the base-manifest enrichment, not a table bug.

`PenOfflineScorer`'s raw-descriptor lane is therefore now a **legacy
fallback** for old sessions, not a requirement for new decodes.

### 2. Two-replay offline scorer result

Re-decoding medvedkovo also produced its first `ShotImpact` events (the old
decodes predated the subtype-8 attribution decode in `1fd76ad`): **86 shots,
86/86 attributed**. Running `GET /discover/pen-offline-score/{id}` on both:

| Session | Scored | Skipped | Classified | Band accuracy | Ricochet precision |
|---|---|---|---|---|---|
| savanna `019ffdcd` | 67 | 2 | 18 | **38.9%** (7/18) | 0/6 |
| medvedkovo `01a00028-ddb0…` | 78 | 8 | 23 | **69.6%** (16/23) | 4/6 |

The spread is the documented offline limit in action: savanna is a
Churchill-vs-IS-7 match-up whose steep glacis hits the center-line proxy
misreads (predicted ricochets actually penetrated), while medvedkovo's varied
roster lands more shots in the model's front-arc regime. The **pipeline**
now runs repeatably across two content-distinct replays; **model accuracy**
still varies with aim-source fidelity, so the live CAM-013 aim capture
remains the decisive PN-4 step.

## What was NOT changed

- No source changes — this is a data operation (`reprocess`) plus doc
  updates.
- The `17425` premium-vehicle enrichment gap is recorded, not fixed (it needs
  DLC-pack `list.xml` coverage, out of scope for this turn).

## Next

1. Live PN-4: capture the CAM-013 camera aim at shot time and feed the same
   `PenValidation.Score` core (replaces only the aim source).
2. Map the type-32 6-byte shell signature → shell kind to drop the
   stock-shell proxy (the dominant remaining confound on both sessions).
3. Add DLC-pack vehicle lists to `BuildManifestAsync` so premium tanks like
   `17425` enrich (also unblocks the 4 medvedkovo "victim data unavailable"
   skips).
