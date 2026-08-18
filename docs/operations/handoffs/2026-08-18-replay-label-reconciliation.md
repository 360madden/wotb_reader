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
| "Dead Rail" (the file formerly `deadrail-*.wotbreplay`, now `medvedkovo-20260802.wotbreplay`) | medvedkovo | → **medvedkovo** (the real Dead Rail map is `desert_train`, unused by any replay) |
| "Copperfield" (the 11.18.0 artifact) | karieri | → **karieri** |

Applied across tracked documentation, the published `memory-offsets` notes,
source comments, test fixtures, and scripts. Two handoffs renamed:
`…-od-recovery-102-oasis-batch-reverdict.md` → `…-savanna-batch-reverdict.md`,
and `…-deadrail-shell-swap-negative.md` → `…-medvedkovo-shell-swap-negative.md`.

The three "real Dead Rail map is `desert_train`" clarifications are kept — they
are correct (the on-disk file, formerly `deadrail-20260802.wotbreplay`, is
medvedkovo — not the `desert_train` "Dead Rail" map — and is now renamed
`medvedkovo-20260802.wotbreplay`).

## Hash citation resolved (2026-08-18)

The record twice cited the 11.18.0 artifact as sha `66703f50…`. The surviving
karieri copies re-verify to `f90ef17f…` (sha256 of the outer `.wotbreplay`,
same 829 216 B size); the inner `data.wotreplay` hashes to `612c30ea…` and
`battle_results.dat` to `7a7bbf00…`, so no current component carries
`66703f50`. The OD-094 ledger row and the FRESH39 handoff now cite `f90ef17f…`
(the verifiable content hash); `66703f50…` was the now-deleted `.data/launch`
copy's hash and cannot be re-verified.
