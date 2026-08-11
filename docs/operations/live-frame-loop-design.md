# Live per-frame HUD loop — design proposal (X4)

**Date:** 2026-08-11
**Status:** DESIGN — no code yet. This is the composition layer that turns
the approved seams (X3 roster enumeration, batch `entity-regions`, CAM-001
camera pose, G2 clock attestation) into the live overlay's per-frame read.
It deliberately does NOT introduce any new memory discovery — everything it
reads is already proven on replays and gated by the existing
`OfflineReplayVerified` seam. Live policy gating (X1) and the
hardware-atomicity proof (item 7) stay future and are explicitly out of
scope here.

## Why this exists

The overlay is a loopback web client that renders a frame per tick. Today
every frame is built from a **decoded replay projection**
(`ReplayFrameSource` + `OverlayFrame`): camera + tanks + HP + pips + kills,
all offline, all replay-clock-labeled. The live seam (replay playback with
live memory reads) has all the pieces for a *memory-backed* frame — but no
one has composed them into the per-frame loop:

- **Roster ids** — `POST /discover/entity-roster` (X3): the avatar-family
  ids enumerated from the game's own maps, ids only. This is the live
  counterpart to the decoded participants roster.
- **Per-entity ring records** — `POST /discover/entity-regions` (batch,
  ≤ 16 entities, ONE clock attestation per batch): the ring-record dump
  carries position (`+0x10`) and hull yaw (`+0x2C`, rehearsed 27/27 +
  35/35 against packet yaw).
- **Camera pose** — `POST /discover/camera-pose` (CAM-001/005): the
  GameCamera world pose with per-hop identity gates, gate-free w.r.t. the
  phase-flipping session controller (CAM-003/008).
- **Clock** — the batch's `sameDecodedClockProven` label (G2, ≤ 2 s
  bound) timestamps the frame in replay coordinates.

## Frame shape (proposal)

The live frame is NOT an `OverlayFrame`. `OverlayFrame` carries replay-only
fields (`OverlayEventPip` damage/death feed, `OverlayKill` kill feed, exact
HP ledger, scoreboard totals) that live mode cannot honestly fill — HP is
an L1 discovery target, turret/lock provably absent from the replay and
unread from the ring. Fabricating those would break the evidence-first
constraint. Instead:

```jsonc
// POST /api/v1/game/discover/live-frame  (proposal)
{
  "completedAtUtc": "...",
  "gameVersion": "11.19.0.10",
  "status": "resolved",                    // gate-level: resolved | pre-battle-inactive | ...
  "replayTimeSeconds": 150.0,              // ONE G2 clock label for the frame
  "sameDecodedClockProven": true,
  "camera": { "x": ..., "y": ..., "z": ..., "yawRadians": ..., "pitchRadians": ... },
  "roster": [
    {
      "entityId": 3760578,
      "status": "resolved",                // per-entity
      "x": ..., "y": ..., "z": ...,
      "yawRadians": ...,                   // hull facing from ring +0x2C (live-verified pending L2)
      "hp": null                           // honest null until L1 lands
    }
  ]
}
```

- `hp` is **null by design** until L1 (the HP live session) lands — the
  HUD renders an empty/unknown HP bar, never a fabricated one. The frame
  contract can gain `hpCurrent/hpMax` additively after L1 without a shape
  break.
- No pips/kills/scoreboard in the live frame: those are decode-projection
  features. Live nameplates render position + facing + (later) HP.
- **Privacy unchanged:** world positions + ids only; addresses die inside
  the coordinator (same contract as every read surface). No process id, no
  module base, no absolute address.

## The loop (proposal)

One tick = one round trip, composed **in the coordinator** under ONE scan
authorization and ONE guarded-reader lease:

1. **Enumerate once per battle, cache the roster.** `EnumerateEntitiesAsync`
   is cheap but not free (3-tree full walk). Call it when (a) no cached
   roster, or (b) the cached roster's `ReplaySessionInactive`/missing status
   indicates the battle changed. Per tick, reuse the cached ids. Roster
   drift mid-battle (spawns/despawns) is handled by re-enumerating on a
   per-entity `EntityNotFound` burst — recorded, not decided here.
2. **Batch-read the whole roster** through `entity-regions` semantics
   (ring-record anchor, region ≥ 0x40 to cover yaw `+0x2C`): one resolve
   pass, one read pass, ONE post-read clock snapshot. Per-entity statuses:
   an unresolved entity fails only itself (the frame keeps the others).
3. **Read the camera pose** under the same lease. The pose walk is
   independent of the entity maps (avatar vftable → camera controller →
   GameCamera), so it composes cleanly; the G2 snapshot from step 2 is the
   frame's time label (the pose's wall-clock proximity is bounded by the
   batch read-pass window — the `Measurement` the batch already carries).
4. **Assemble** the response in roster order; gate-level failures
   (`UnsupportedBuild`, `pre-battle-inactive`, revoked authorization) fail
   the whole frame so the HUD never renders a half-timed frame.

### Why ONE round trip

The overlay is a loopback client; three sequential HTTP calls per tick
(roster + regions + camera) triple the latency and — worse — give three
different moments with no single clock attestation spanning them. The
coordinator-composed `live-frame` endpoint keeps the trust boundary
client-side-agnostic: the client sends nothing but the capability header,
and the coordinator owns process identity, lease, and the single clock
label. This mirrors exactly how `entity-regions` already works (one
attestation per batch).

## Timing budget (to measure, not assumed)

The batch response already carries `Measurement` (first resolve → last
read + snapshot moment). The X2 rehearsal (OD-RECOVERY-086) measures the
whole-roster window on a real replay; the live loop inherits that number as
its per-frame budget. Target: the window must stay small enough that "one
coherent frame" is defensible (the item-7 atomicity argument's foundation).
If the measured window is too wide, the loop degrades to **render
last-good-frame + skip** rather than stretching the window — recorded as an
open question, not silently decided.

## Honest limits (recorded, not hidden)

| Field | Live status | Why |
|---|---|---|
| Position (x/y/z) | ✅ readable now | ring `+0x10`, batch surface proven |
| Hull yaw | ✅ readable now (L2 gate pending live verification) | ring `+0x2C`, rehearsed 27/27 + 35/35 |
| HP (current/max) | ⏳ L1 live session | int16 candidates pinned at entity-base `+0xB8`/`+0x11C`; `hp: null` until then |
| Turret facing / lock / targeted | ❌ absent | type-7 survey proved no replay carrier; ring does not expose it; future discovery |
| Aim-line | ✅ computable | `AimGeometry` hull-arc utility; honest weak necessary-condition only |
| Spotting model | ❌ out of scope | X5 policy-gated, replay god-view stays |

## Sequencing

1. **This design** (this doc) — PROPOSAL.
2. **Rehearsal first (live sessions, pre-staged order):** OD-RECOVERY-086
   batch rehearsal → X3 `-EnumerateLive` → L1 HP → L2 facing. Each closes a
   field this loop reads; the loop is not built on unproven reads.
3. **Implement `ReadLiveFrameAsync`** (coordinator) + `POST
   /discover/live-frame`. ✅ DONE (2026-08-11): the coordinator composes
   roster enumeration + ring-record batch + CAM-001 camera pose under **ONE
   guarded reader lease** (single sanctioned walker — the three public
   methods now delegate to shared private cores), one G2 clock attestation
   per frame, honest `hp: null`, ids-only privacy boundary; endpoint +
   contract + 25 new tests (10 Core decoder, 4 coordinator frame, 3
   endpoint, plus resolver/roster). The `LiveFrameSource` seam is DONE
   too: `LiveFrameProjector` (Application, pure) maps `LiveFrameReadResult`
   → the SAME `OverlayFrameProjection` shape; `GET /api/v1/live/frame`
   serves it projected to viewport pixels (the shared
   `ToOverlayFrameResponse` mapping — both sources serialize identically),
   and `TreaderApiClient` gained `GetLiveFrameAsync`. HP renders as the
   DTO's honest "unknown" (empty bar, no readout) until L1;
   pips/kills/scoreboard absent. 12 new tests (8 projector, 4 endpoint).
   Remaining: the overlay's live-mode UI switch (a ViewModel mode toggle —
   design decision, not code) and joining live ids to decoded roster names
   once X2b proves the id mapping.
4. **Measure the frame window** on the approved session; record it as the
   loop's budget (feeds item 7).
5. Then: L1 wiring (`hp` becomes real), the live overlay render pass, and
   the future X1 live gate (policy-gated, not part of this design).

## Relationship to the consolidation checklist

| Item | Status |
|---|---|
| 6 (live-mode alignment) | ✅ Batch surface + X3 enumeration done; THIS doc composes them per frame |
| 7 (hardware atomicity) | Prerequisites now exist (batch window measurement + this loop's budget); proof stays LAST, untouched |
