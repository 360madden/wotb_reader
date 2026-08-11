# Live enemy-roster read — design proposal (X3 pre-design)

**Date:** 2026-08-11
**Status:** DESIGN — no code yet. Follows the adopted batch surface
(`docs/operations/batch-entity-read-design.md`); closes the one gap between
"replay rehearsal works" and "live frame works": **where do the entity ids
come from in live mode?**

## Why this exists

The batch surface (`POST /api/v1/game/discover/entity-regions`, ≤ 16
entities, one clock attestation per batch) is designed and rehearsed to serve
a whole frame. But the rehearsal gets its entity ids from the **decoded
replay's participants table** (`--roster` mode of
`batch-rehearsal-crosscheck.py`). Live mode has no decode — the ids must be
**enumerated from the game's own entity maps**, then handed to the existing
batch surface unchanged.

This is also the carrier for the enemy-track live ambitions: per-enemy ring
records (position + hull yaw) are the same per-entity read the batch surface
already performs, and turret/target/lock fields are future discovery targets
on the same per-entity surface.

## What the resolver proves today (unchanged)

The targeted walk (`Type10EntityPositionResolver.FindEntity`, replicated by
the camera script's `Walk-EntityPositionMemory`) is a **branch-pruned binary
search** for ONE entity id:

```
entities = connection + 0x04                       (inline member)
cached   = [entities + 0x48]                       (fast path; id at [+0x1c])
for root in [entities + 0x1c (primary),
             entities + 0x40 (tertiary),
             entities + 0x34 (secondary)]:         (alternative maps)
    sentinel = [root]
    node     = [sentinel + 0x04]
    while node:  (bounded, visited-set, nil flag @ node+0x0d == 1 ends)
        key   = int32 [node + 0x10]                (entity id)
        value = uint32 [node + 0x14]               (entity address)
        descend: entityId < key ? [node + 0x00] : [node + 0x08]
```

Node layout (0x18 bytes): `childLess +0x00`, (root link `+0x04`), `childGreater
+0x08`, nil flag `+0x0d`, key `+0x10`, value `+0x14`. Bounds: `MaxTreeNodes
1024` per tree, `MaxAttempts 3`, visited-set cycle guard.

**The gap:** the descent visits only one branch per node, so it returns at
most one entity. The roster needs **every** key→value pair in the maps.

## The enumeration (new, pure resolver function)

`Type10EntityPositionResolver.EnumerateEntities` — a **full traversal** of the
same three maps plus the cached slot, with the same layout constants:

1. **Cached slot:** if `[entities + 0x48]` is a valid pointer, read id at
   `[cached + 0x1c]` and emit (id → cached).
2. **Each tree root** (primary, tertiary, secondary, in order): read sentinel,
   then **stack-based full walk** from `[sentinel + 0x04]` visiting **both**
   children per node (this is the one structural difference from the search —
   the descent picks a branch, enumeration pushes `childLess` and
   `childGreater` both).
3. Per node: nil flag `+0x0d == 1` → skip (empty slot); else emit
   (key@0x10 → value@0x14) when the value is a plausible pointer.
4. **Dedupe by entity id** across the cached slot + all three trees (maps are
   alternatives, but the cache can hold any id and the same id may surface in
   more than one map; dedupe keeps the result set stable).
5. Same bounds as the search: visited-set per tree, `MaxTreeNodes` 1024,
   non-pointer node values fail that tree (recorded, not abort-all).

Result: `IReadOnlyList<EntityRosterEntry>` where `EntityRosterEntry = (int
EntityId, uint EntityAddress)` — addresses stay **inside the coordinator**,
never serialized (same privacy contract as every read surface).

## Filtering to the avatar family

The maps contain ALL entities — tanks, shells, effects, UI objects. The
resolver's identity gates are the filter:

- `movementFilter = [entity + 0x38]`; its vtable must be one of
  `[0x0325654c, 0x032565ac, 0x03442520]` (`UnsupportedMovementFilter` today).
- `avatarHelper = [movementFilter + 0x08]`; vtable in
  `[0x0325656c, 0x0325658c, 0x034424a4]`.

For the roster, apply the movement-filter vtable check per candidate and
keep only matches. Shells/effects either lack the filter shape or carry a
different vtable — this is the **same check** the resolver applies to the
viewpoint, so it needs no new discovery; the live session measures the
filter's precision (expected ≈ 14 tanks, no shells/effects leaking).

**Known open question (recorded, not decided):** whether the movement-filter
vtable set alone separates tanks from shells, or whether shells share a
filter family and need a second discriminator. The rehearsal session
enumerates + filters and the cross-check (decoded roster) measures exactly
this. No code before the measurement.

## Coordinator + endpoint shape (proposal)

Mirrors the batch surface's trust boundary exactly:

- `GameSessionCoordinator.EnumerateEntitiesAsync()` — scan authorization →
  `OfflineReplayVerified` → build-identity check (version + sha256) → guarded
  reader → full-tree enumeration → movement-filter filter → returns
  `RosterEnumerationResult` (**ids only**; addresses dropped at the boundary).
  No G2 clock attestation needed (the roster is a lookup, not a frame read;
  the caller's subsequent `entity-regions` batch carries the frame's clock
  label).
- Endpoint: `POST /api/v1/game/discover/entity-roster` → `{ entityIds: [...],
  count, filteredOut, status }`. Fail-closed statuses: `unsupported-build`,
  `gate-not-satisfied`, `read-unavailable`, `pre-battle-inactive`
  (retryable, same as the batch).
- The caller then feeds the ids into the **existing** `entity-regions` batch —
  no changes to the batch surface. Composition client-side:
  `entity-roster` → pick ≤ 16 ids → `entity-regions`.

## Privacy / bounds

- Ids are logical identifiers (same values as replay participants), already
  exposed in the batch request. **No absolute addresses, no module base, no
  process id ever leave the coordinator** — enumeration addresses are
  consumed inside the coordinator and the endpoint returns ids only.
- Bounds: 3 trees × 1024 nodes, visited-set guarded, one guarded-reader
  lease, bounded wall time (the full walk is ~3× the single-search node
  budget worst-case; expected real cost is trivial — the maps hold hundreds
  of entries, not thousands).
- Evidence-first: the endpoint is diagnostic-only, same status as
  `/discover/position-page` and `/discover/entity-regions`; no promotion
  claim rides on it.

## Sequencing

1. **This design** (this doc) — the shape above is a PROPOSAL.
2. **Implement** `EnumerateEntities` + coordinator method + endpoint + tests
   (pure resolver tests first: tree walk visits both branches, dedupe, bounds;
   coordinator tests mirror the batch's gate matrix). No live code before the
   measurement below.
3. **Extend the rehearsal driver** (`invoke-batch-rehearsal.ps1`) with an
   `-EnumerateLive` mode: enumerate → filter → batch-read the enumerated ids
   → cross-check against the decoded roster. This **measures the
   movement-filter precision** on real data and validates the full
   enumeration against the participants table.
4. **Live session** (approved, pre-staged order): the enumeration rehearsal
   rides the next approved session after OD-RECOVERY-086; it does not need a
   new gate, it composes with the batch rehearsal.
5. Only then: design the per-frame live loop (enumerate once per battle →
   batch per frame) and the turret/target discovery targets on the per-entity
   surface.

## Relationship to the enemy-track plan

| Capability | Replay today | Live (after this) |
|---|---|---|
| Enemy ids | decoded participants | **enumerated from maps** |
| Position (all enemies) | position_samples | batch ring reads |
| Hull yaw (all enemies) | canonicalized | ring `+0x2C` (yaw chain field, rehearsed 27/27 + 35/35) |
| HP | exact ledger | L1 HP discovery target |
| Turret / lock / targeted | provably absent (type-7 survey) | discovery targets on the per-entity surface |
| Aim-line | `AimGeometry` (tested) | same utility on live yaw |
