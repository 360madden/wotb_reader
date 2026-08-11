# Live roster → decoded-name join — design (X2b follow-up)

**Date:** 2026-08-11
**Status:** DESIGN — no code. **X2b rehearsal outcome recorded 2026-08-11
(OD-RECOVERY-086): PARTIAL, team-based** — 7/14 ids (precision 1.000,
recall 0.500, 0 extra), all found = team 1 (the player's own team), all
missing = team 2 (enemies). Per this design's own gate, the result is NOT
an exact set match, so the **blanket join is invalidated**: the per-id
best-effort join remains VALID for the own-team ids the enumeration DOES
return (they map exactly, 0 extras) — enemy ids stay unnamed until the X4
loop re-enumerates per tick or adds a second discriminator for enemy
avatars. Nothing may be coded beyond the per-id best-effort join before
that enemy-id proof (evidence-first).

## Problem

The live frame (`POST /discover/live-frame`, rendered via `GET
/api/v1/live/frame`) serves tanks with **entity ids only** — no player
name, clan, team, or tank name. The X4 `LiveFrameProjector` maps them to
`ProjectedTank` with `PlayerName/TankName/ClanTag/TeamNumber: null`, so
live nameplates render as `Tank 3760578`. The decoded replay projection
(`ReplayFrameSource`) already resolves every roster entity id to a
`Participant` (name, clan tag, team, tank name/class) — the join is the
missing link that turns anonymous live nameplates into named ones.

## The assumption this rides on

`Participant.EntityId` (decoded) and the enumerated avatar-family ids
(live) are the **same id space** — the game's entity ids as stored in the
type-10 stream and as read from the ring maps. X2b's rehearsal measures
exactly this: matched/missing/extra between `-EnumerateLive`'s roster and
the decoded participants roster. **If the match is not exact, this design
is invalid and must be revisited** — a partial match means the join is
only ever best-effort per id, never a blanket rename.

## Where the join lives

Server-side, in the **live-frame read surface**, not in the overlay:

- `GET /api/v1/live/frame` gains an **optional** `sessionId` query param
  (a decoded battle session whose participants provide the roster).
- The handler loads that session's decoded participants (the existing
  `ISessionQueryRepository` projection path — one cacheable load per
  session, same as `ReplayFrameSource`), builds `entityId → Participant`,
  and passes the map into `LiveFrameProjector.Project` as a new optional
  argument.
- The projector maps each live tank's `EntityId` → participant and fills
  `PlayerName/ClanTag/TeamNumber/TankName` (tank class → the existing
  `TankClass.ToString()` convention).

No session id → current behavior (null names). The overlay passes the
session id it is already bound to in replay mode (`_selectedSession`).

## Honest fail-closed rules

| Case | Behavior |
|---|---|
| `sessionId` omitted | names stay null (today's behavior) |
| id in live roster but not in decoded participants | unnamed (`Tank {id}`) — never guessed |
| id in decoded participants but not in live roster | not rendered (the live frame is the source of truth for what exists) |
| participants without `EntityId` | can never join — documented, not fixed |
| duplicate `EntityId` across participants | first match wins (same convention as `ReplayFrameSource`'s roster dict) |
| decoded roster stale vs the live battle | the id match still applies per-id; names are per-id, not per-slot |

## Own-nameplate refinement (rides the same join)

Live mode renders the viewpoint tank's own nameplate because the CAM-001
camera sits ~23.57 m behind it (third-person), so the replay self-filter
(`DistanceMeters < 1.0`) never catches it. With the join, the host can
identify the viewpoint tank (the decoded session's
`ViewpointParticipantId` → its `EntityId`) and the overlay can suppress
that one nameplate — the honest "self" marker. This is a render-path
change in the ViewModel, gated on the same join being proven.

## Why not join in the overlay

The overlay is a loopback web client with no parser/storage refs
(architecture boundary). The decoded participants live server-side; the
join is a pure server-side cross-reference. Client-side joins would
duplicate the roster lookup and break the boundary.

## Sequencing

1. **X2b `-EnumerateLive` rehearsal** (OD-RECOVERY-086): proves the id
   mapping. Result (2026-08-11): **team-based PARTIAL** — own-team ids map
   exactly (7/7 found, precision 1.000, 0 extras); enemy ids are NOT
   enumerated. Per the fail-closed rules the per-id join may be
   implemented for the ids the frame carries (own team only today); the
   full/enemy join stays blocked on an enemy discriminator (the X4
   re-enumerate-per-tick or second vtable gate).
2. `LiveFrameProjector.Project` gains the optional participant map
   (pure, testable: named tanks, unnamed fail-closed, no-map fallback).
3. `GET /api/v1/live/frame?sessionId=` handler: load participants,
   pass the map. Tests: named flow, no-map flow, stale/missing id flow.
4. Overlay: pass `_selectedSession` id in live mode; own-nameplate
   suppression once the viewpoint id is resolvable.
5. L1 (HP) and L2 (facing) ride the same per-entity surface; the join is
   orthogonal to them.

## Relationship to the roadmap

| Item | Status |
|---|---|
| X2b (enumeration) | implemented; rehearsal wired — the proof gate for THIS design |
| X4 (live frame + seam) | implemented (one lease, projection, toggle, measurement) |
| L1 HP / L2 facing | live-gated discovery targets; unaffected by the join |
| Own nameplate | rides the join (this design step 4) |
