# Live roster → decoded-name join — design (X2b follow-up)

**Date:** 2026-08-11
**Status: IMPLEMENTED + LIVE-VERIFIED (enemy ids included) 2026-08-11.** The
X2b rehearsal outcome (2026-08-11, OD-RECOVERY-086): PARTIAL, team-based —
7/14 ids (precision 1.000, recall 0.500, 0 extra), all found = team 1 (the
player's own team), all missing = team 2 (enemies) — which suggested the
blanket join was invalid and enemy ids stay unnamed until a second
discriminator exists. **SUPERSEDED by live evidence (X4-E2E, 2026-08-11):**
the movement-filter family is TIME-VARYING — the X4 loop's per-tick
re-enumeration caught the FULL 7v7 roster at battle start, and the per-id
join resolved every enemy id EXACTLY (14/14, 0 mismatches vs the host's
independent decoded roster; playerName/tankName/clanTag/teamNumber all
exact; the 1 non-roster entity stayed unnamed, never guessed). Enemy ids
are joinable whenever the enumeration returns them; the residual limitation
is mid-battle enemy COVERAGE (enumeration-dependent — mid-battle frames
returned own-team 7), not join correctness. Implemented exactly within the
per-id bound:
`LiveFrameProjector.Project` takes an optional per-id participant map,
`GET /api/v1/live/frame?sessionId=` loads the decoded roster with the same
first-match convention as `ReplayFrameSource`, and the overlay forwards its
selected session id in live mode. Fail-closed everywhere: an id not in the
map, an omitted/unknown session, or a failed load degrades to anonymous
names — never guessed, never an error on the live frame. **Own-nameplate
suppression refinement (step 4's second half) is DONE (2026-08-11):** the
live X4-E2E session proved the viewpoint id is resolvable
(`ViewpointParticipantId` → participant `EntityId` — the capture's own
tank 3760577 = mrkool1138, eye 1.9 m), and the refinement shipped:
`OverlayFrameResponse.OwnEntityId` (additive, nullable), the live endpoint
resolves it from the decoded session (fail-closed: null when no
session/viewpoint/matching participant), and the overlay suppresses exactly
that one nameplate while the distance heuristic (< 1.0 m) stays as the
replay/unknown fallback — the CAM-001 chase eye sits at the turret-level
aim point ~1.9 m above the hull center, beyond that heuristic. The own
tank still flows to the minimap; suppression is nameplate-only. **The "honest
self marker" half shipped 2026-08-12:** when the own tank projects OFF the
viewport (chase camera cuts, death-cam pans, the battle-intro cinematic),
the HUD draws a clamped edge chevron pointing back at the hull
(`OwnMarkerItem`/`OwnMarkerMath` + `OwnMarkers` in the ViewModel, pure
clamp/angle helpers, 8 tests: 5 math + 3 VM — off-viewport marker,
on-viewport omission, replay/unknown-id omission; the chevron renders in
`W2sHudView`). Fail-closed: no marker when the id is unknown, the tank is
on-screen, or the projection is missing — never guessed.

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
camera sits behind it (third-person), so the replay self-filter
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
   exactly (7/7 found, precision 1.000, 0 extras); enemy ids were not
   found in that mid-battle frame. Per the fail-closed rules the per-id
   join was implemented for the ids the frame carries. **SUPERSEDED
   (X4-E2E, 2026-08-11):** the enumeration is time-varying — battle-start
   frames carry the FULL roster (7v7), and the same per-id join resolved
   every enemy id exactly (14/14, 0 mismatches). The enemy discriminator
   gate is closed by live evidence; residual open question is mid-battle
   enemy coverage only.
2. `LiveFrameProjector.Project` gains the optional participant map
   — **DONE 2026-08-11** (pure, testable: named tanks, unnamed
   fail-closed, no-map fallback — 3 new `LiveFrameProjectorTests`).
3. `GET /api/v1/live/frame?sessionId=` handler: load participants,
   pass the map — **DONE 2026-08-11** (same first-match convention as
   `ReplayFrameSource`; a missing/unknown session degrades to anonymous.
   Tests: named flow + unknown-session fail-closed — 2 new
   `ReadApiEndpointsTests`).
4. Overlay: pass `_selectedSession` id in live mode — **DONE 2026-08-11**
   (1 new `MainViewModelTests`). Own-nameplate suppression (step 4's
   second half) — **DONE 2026-08-11**: the viewpoint id is resolvable
   (proven live by X4-E2E), the live endpoint resolves
   `Session.ViewpointParticipantId` → participant `EntityId` and carries it
   as `OverlayFrameResponse.OwnEntityId` (additive, nullable, fail-closed
   null), and the ViewModel skips that one nameplate
   (`tank.EntityId == frame.OwnEntityId || DistanceMeters < 1.0`). New
   tests: `LiveFrame_IdentifiesOwnEntity_FromViewpointParticipant` (endpoint
   flow + join intact + unknown-session fail-closed null) and
   `RefreshOverlayFrameAsync_LiveModeSuppressesOwnNameplateByIdentity`
   (1.9 m own tank suppressed by id, enemy nameplate kept, own tank still
   on the minimap).
5. L1 (HP) and L2 (facing) ride the same per-entity surface; the join is
   orthogonal to them.

## Relationship to the roadmap

| Item | Status |
|---|---|
| X2b (enumeration) | implemented; rehearsal wired — the proof gate for THIS design |
| X4 (live frame + seam) | implemented (one lease, projection, toggle, measurement) |
| L1 HP / L2 facing | live-gated discovery targets; unaffected by the join |
| Own nameplate | rides the join (this design step 4) |
