# Batch N-entity read surface — design proposal (X2 pre-design)

**Date:** 2026-08-11
**Status:** DESIGN ADOPTED — items 1–2 IMPLEMENTED and test-pinned
(coordinator method + web endpoint); items 3–4 PRE-STAGED (rehearsal
driver + cross-check tool + read-pass measurement). The replay rehearsal
itself still needs one approved live session. This is the offline half of
consolidation item 6 (live-mode alignment) and the explicit prerequisite
for item 7 (hardware-atomicity proof), which "needs the batch read-surface
design (X2) and the per-frame read discipline to exist first."

## Why this exists

The live overlay (Phase 5, policy-gated) needs **per-frame positions for all
N tanks plus the camera**. Today the seam is single-entity:
`POST /api/v1/game/discover/entity-region` reads ONE entity's region per
round trip under one lease, with one replay-clock label. A live frame needs
up to 14 tanks — fourteen sequential round trips would be too slow and, more
importantly, would have **no single clock snapshot** spanning the frame.

Replays are the rehearsal ground (consolidation strategy): the batch surface
is designed and rehearsed on replays against decoded ground truth *before*
live mode needs it, exactly like every other memory read in this repo.

## Design goals

1. **One round trip for N entities** (bounded) with **one replay-clock
   attestation per batch** — the per-frame read gets a coherent timestamp,
   not N drifting ones.
2. **Same trust boundary as the single-read seam:** process identity from the
   scan authorization, `OfflineReplayVerified` gate (replay) / live policy
   gate (X1, later), guarded reader under one lease, **bytes only — no
   absolute addresses, no process id, no module base ever leave the
   coordinator** (mirrors `EntityRecordRegionReadResult`).
3. **Per-entity status, not whole-batch failure.** One unresolved entity must
   not kill a frame: each entity returns its own status, and the batch
   response is only as good as its worst *gate-level* failure (unverified
   process / build mismatch fail the whole batch; an individual unresolved
   entity fails only that entity).
4. **Fail-closed, bounded, replay-clock-labeled** — same discipline as the
   single-read seam: length clamp (1..4096 per entity), entity count clamp,
   total-byte clamp, G2 same-decoded-clock attestation with the ≤ 2 s bound.
5. **Batch shape must support the item-7 atomicity proof** — per-entity
   double-read and a verification window must be expressible in the contract
   (see Atomicity groundwork below), not bolted on later.

## The current seam (what the batch replaces for the frame path)

- `EntityRecordRegionReadRequest` (ApiContracts): `EntityId`,
  `RegionLength` (1..4096), optional `BattleSessionId` (replay-clock
  segments), `RegionAnchor` (`ring-record` default, `entity-tank-record`,
  `entity-base`).
- `GameSessionCoordinator.ReadEntityRegionAsync`: length clamp → scan
  authorization + `OfflineReplayVerified` gate → build-identity check
  (version + sha256 vs `Type10EntityPositionLayout.WotBlitz1119010`) →
  guarded reader → `ResolveEntityPositionAddressAsync` under the same lease →
  region read → replay-clock label + same-decoded-clock attestation (one
  call) → `EntityRecordRegionReadResult` (bytes + replay time + per-hop
  identity flags + `ConsistentDoubleRead` + `SameDecodedClockProven`).
- Endpoint: `POST /api/v1/game/discover/entity-region` (base64 bytes).

The single-read seam stays UNCHANGED (back-compat — L1–L4 event-bound dump
schedules use it; the batch is additive).

## Proposed contract (PROPOSAL — shape, not final)

### Request — `POST /api/v1/game/discover/entity-regions`

```jsonc
{
  "entities": [
    { "entityId": 3760578, "regionLength": 320, "regionAnchor": "entity-base" },
    { "entityId": 3760577, "regionLength": 320, "regionAnchor": "entity-base" }
    // ... up to 16
  ],
  "battleSessionId": "019fdff7-8dcf-7426-8547-9fb8cc3eb07b"
}
```

- `entities`: 1..16 entries (clamp, fail-closed outside).
- Per entity: same fields as the single-read request (reuse the validation
  rules: length 1..4096, anchor from the same enum, unknown anchor fails
  closed).
- **Total region bytes ≤ 16 KB** (16 × 4096 cap; the practical frame read is
  far smaller — 14 × 320 = 4.5 KB for entity-base dumps).
- `battleSessionId`: optional, same semantics — replay-clock segments attest
  same-decoded-clock alignment; omitted never claims the flag.

### Response

```jsonc
{
  "completedAtUtc": "...",
  "gameVersion": "11.19.0.10",
  "status": "resolved",                  // whole-batch gate-level status
  "sameDecodedClockProven": true,        // ONE attestation for the batch
  "replayTimeSeconds": 150.0,            // the batch's replay-clock label
  "regions": [
    {
      "entityId": 3760578,
      "status": "resolved",              // per-entity status
      "replayTimeSeconds": 150.0,
      "regionBase64": "...",             // bytes only
      "failureStage": null,
      "attempts": 1,
      "nodesVisited": 11,
      "moduleRooted": true,
      "entityIdentityRevalidated": true,
      "consistentDoubleRead": true
    }
    // one entry per requested entity, in request order
  ]
}
```

- Whole-batch `status`: `resolved` (all entities resolved), or a gate-level
  status that fails the whole batch: `unsupported-build`, `gate-not-satisfied`,
  `read-unavailable`, `pre-battle-inactive` (the `ReplaySessionInactive`
  retryable phase — callers retry through it, per consolidation item 3).
- Per-entity `status`: `resolved` | `entity-unresolved` (with `failureStage`)
  | `invalid-request` (bad anchor/length for that entity).
- `sameDecodedClockProven` is batch-level: **one** G2 snapshot labels the
  whole frame. `replayTimeSeconds` is the batch label; per-entity copies are
  convenience mirrors, never independently claimed.
- Privacy contract unchanged: `publicProcessAddressesOrRawBytes: false`,
  region bytes are session evidence, never published in aggregates.

## Read discipline (the coordinator-side design)

Ordering inside one batch, under ONE lease and ONE authorization:

1. **Gate + identity first:** scan authorization → `OfflineReplayVerified` →
   build-identity check (version + sha256) — any failure fails the whole
   batch before any read (never a partial frame on a gate violation).
2. **Resolve ALL entity addresses first** (per-entity
   `ResolveEntityPositionAddressAsync`): an unresolved entity is recorded
   with its `failureStage` and does not fail the batch. If the resolver
   returns the retryable `ReplaySessionInactive`, the WHOLE batch returns
   `pre-battle-inactive` (the frame can't be half-timed — phase is global).
3. **Read ALL regions** through the guarded reader, per-entity double-read
   where the payload is the read target (the position payload already
   double-reads; region dumps carry `ConsistentDoubleRead` per entity).
4. **One post-read G2 clock snapshot** labels the batch (≤ 2 s bound vs the
   read window). The pre-read wall clock is recorded alongside so the
   attestation bounds the whole batch, not just the last read.
5. **Assemble** per-entity results in request order + batch status.

The whole batch must complete inside the authorization lease; the read
cancellation token is linked to the authorization token exactly like the
single-read seam.

## Atomicity groundwork (item 7's hook)

The item-7 proof ("prove the position bytes are hardware-atomic, or design
the read discipline that makes atomicity unnecessary") gets its home here:

- **Per-entity `ConsistentDoubleRead`** already travels in the single-read
  result; the batch carries it per entity so the proof can distinguish
  "consistent across two reads" from "atomic in one".
- **The verification window** (the wall-clock span between double-reads, and
  between the batch's first and last read) becomes a measured quantity the
  batch response carries. **PRE-STAGED 2026-08-11:** the contract now ships
  `Measurement` (`BatchStartedAtUtc` = first resolve, `BatchEndedAtUtc` =
  last read, `ClockSnapshotAtUtc` = the G2 snapshot moment) so the rehearsal
  session measures the whole-roster read window WITHOUT a second session
  (the field had to exist before the run). **Per-entity double-read spans
  landed 2026-08-11 (item-7 Branch B step 1):** the region span AND the
  entity-base span are each read twice per attempt with a bounded retry
  (fail-closed `region-unstable-snapshot` / `entity-base-unstable-snapshot`
  exhaustion) — the per-span `SequenceEqual` is the stability witness (for
  ring records the leading time field sits inside the span). The flag still
  travels `ConsistentDoubleRead: false`; per-entity span MEASUREMENT fields
  and the flag flip remain in the owner-gated shared-contract proposal.
- The batch is also where **one-coherent-pass** semantics get designed: a
  single reader pass over all entities at ~the same wall time is the only
  shape that can later claim "all 14 tanks seen within X ms" — which is what
  a live frame needs and what the atomicity argument will rest on.

## Back-compat and sequencing

- The single-read endpoint and `EntityRecordRegionReadRequest/Result` are
  unchanged; L1–L4 event-bound schedules keep using them.
- The batch endpoint is additive. Implementation order (each a separate
  unit):
  1. ✅ **DONE 2026-08-11** — Coordinator method `ReadEntityRegionsAsync`
     + validation (count/length/anchor/total-byte clamps) + 7 unit tests
     (exact-build bytes in request order, one clock attestation per batch,
     one-unresolved-fails-only-itself, inactive-fails-whole-batch, invalid
     request before gate, missing gate, unsupported build).
  2. ✅ **DONE 2026-08-11** — Web endpoint `POST /discover/entity-regions`
     + 4 endpoint tests (batch response mapping + base64 + no-address
     leak, invalid anchor, empty entities, failure mapping).
  3. ✅ **PRE-STAGED 2026-08-11** — Replay rehearsal: dump all roster
     entities at replay-clock-labeled times and cross-check against the
     decoded frame (the X2 rehearsal). Driver
     `scripts/invoke-batch-rehearsal.ps1` (qualify roster → live batch dumps
     through the gated seam → verdict) + `scripts/python/
     batch-rehearsal-crosscheck.py` (roster mode; compare mode decodes each
     ring-record dump's float32 triple at +0x10 and matches it to the
     nearest decoded position_sample — proven on real data: 42/42 pairs
     PASS, corruption detected, exit codes 0/1/3). The session itself still
     needs one approved launch. **Measurement persistence corrected
     2026-08-14:** the driver now copies the endpoint's three timestamp-only
     `Measurement` fields into every dump and derives the read-pass duration
     plus post-read clock-snapshot lag. Missing, incomplete, reversed, or
     pre-end clock timestamps fail closed; four PowerShell tests pin the
     contract.
  4. ✅ **DONE 2026-08-11** — Measure the batch window + double-read spans →
     feed item 7. The batch response now carries the read-pass window +
     snapshot moment (`Measurement`); as of 2026-08-14 the rehearsal dump
     persists and validates it rather than discarding it. **The
     per-entity double-read spans themselves landed 2026-08-11 (item-7
     Branch B step 1)** — region + entity-base spans double-read with
     bounded retry and fail-closed exhaustion; per-entity span measurement
     fields + the `ConsistentDoubleRead` flip stay in the owner-gated
     shared-contract proposal.

## Open questions (recorded, not decided)

1. **Observation-promotion vs. resolver-endpoints-as-API** — deferred per the
   consolidation decision log; the batch endpoint is designed as a resolver
   endpoint, so either future decision can consume it.
2. **Live gate naming** — the batch contract carries no gate field (the host
   enforces `OfflineReplayVerified` today, the X1 live gate later); the
   contract is gate-agnostic by design.
3. **Entity-count bound** — 16 is a cap for safety, not a target; the frame
   read is 14. If the roster ever exceeds 16 (unlikely in WoTB), the bound
   is a one-line constant change with the total-byte clamp unchanged.
4. **Per-entity `ReplayTimeSeconds` mirrors** — kept for caller convenience;
   only the batch attestation is load-bearing. If a later review finds them
   confusing, they can be dropped without breaking the attestation.

## Relationship to the consolidation checklist

| Item | Status |
|---|---|
| 6 (live-mode alignment) | ✅ Design slice DONE (this doc) — rehearsal still needs one approved session |
| 7 (hardware atomicity) | Prerequisite now exists; the proof itself stays LAST, untouched |
