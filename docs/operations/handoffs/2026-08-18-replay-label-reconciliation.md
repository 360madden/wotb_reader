# Replay label reconciliation — map/tank/arena ground truth (2026-08-18)

**Purpose:** the offline-discovery record referenced replays by nicknames that
did not match the replays' own `meta.json`. This handoff is the authoritative
ground-truth table and the record of the correction.

## Ground truth (read from each replay's `meta.json`)

| Replay (content sha256) | size | version | map | mapId (arena) | tank | player | compDesc |
|---|---|---|---|---|---|---|---|
| `0fae5612…` | 1 045 525 B | 11.19.0 | **savanna** | **11** | GB08_Churchill_I | mrkool1138 | 2897 |
| `59c3b92e…` | 1 100 265 B | 11.19.0 | **medvedkovo** | **7** | GB08_Churchill_I | mrkool1138 | 2897 |
| `f90ef17f…` | 829 216 B | 11.18.0 | **karieri** | **4** | GB08_Churchill_I | mrkool1138 | 2897 |

All three are the same player and the same `GB08_Churchill_I` loadout
(`vehicleCompDescriptor 2897`). The tank column is therefore **already
correct** everywhere the record said "Churchill I".

## Mislabels corrected

| Record nickname | Actual map | Correction |
|---|---|---|
| "Oasis" / "Oasis Palms" | savanna | → **savanna** |
| "Dead Rail" (the `deadrail-*.wotbreplay` file) | medvedkovo | → **medvedkovo** (the real Dead Rail map is `desert_train`, unused by any replay) |
| "Copperfield" (the 11.18.0 artifact) | karieri | → **karieri** |

Applied across tracked documentation, the published `memory-offsets` notes,
source comments, test fixtures, and scripts. Two handoffs renamed:
`…-od-recovery-102-oasis-batch-reverdict.md` → `…-savanna-batch-reverdict.md`,
and `…-deadrail-shell-swap-negative.md` → `…-medvedkovo-shell-swap-negative.md`.

The three "real Dead Rail map is `desert_train`" clarifications are kept — they
are correct (the mislabeled on-disk filename `deadrail-20260802.wotbreplay` is
medvedkovo, not the `desert_train` "Dead Rail" map).

## Open item (not a map/tank/arena mislabel)

The record twice cites the 11.18.0 artifact as sha `66703f50…`. The surviving
11.18.0 copies on disk hash to `f90ef17f…` (same 829 216 B size). The
`.data/launch` copy that `66703f50…` referred to was deleted, so the old hash
cannot be re-verified; the surviving karieri copies are the ground truth.
