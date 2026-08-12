# 2026-08-11 — Live overlay end-to-end validation (X4 closure)

**Status: DONE — the live overlay render pass is validated end-to-end on the
verified camera seam.** The pipeline that shipped earlier (camera pose via the
CAM-013-verified W2S seam, L1 HP via the entity-base read, and the per-id
decoded-name join) was exercised live mid-battle and every layer verified
against an independent source. This was the camera lane's last remaining
live-gated item.

## Sessions

| Launch | Anchor | Session | Outcome |
|---|---|---|---|
| 1 | 00:09:36Z | `019ff34d-4e03-7669-9839-1dffd878215c` (battle 1) | Mid-battle frame: own-team 7 tanks, all named, real HP — then the CAM-003 controller flip between auto-loop battles (battle 2) |
| 2 | 00:17:32Z | `019ff354-9470-7be5-985f-71811ae754bf` (battle 1) | **Decisive capture** (`liveframe-v2-1.json`): full 7v7 roster, 14/14 exact joins, 13/15 real HP, chase camera verified |

Both launches: gate OK, monitor-2 placement live-verified, loopback host
`127.0.0.1:9182` with rendezvous capability. Launches 1→2 were a fresh
relaunch after the battle-2 controller flip (the fail-closed 409 behaved
exactly as designed — `failureStage: playback-controller-vtable`).

## The decisive capture (launch 2, battle 1, mid-battle)

`GET /api/v1/live/frame?sessionId=019ff354-9470-7be5-985f-71811ae754bf`
served a full frame at the 640x360 viewport. Cross-checked programmatically
against the host's independent decoded-roster source (`GET /sessions/{id}`):

| Check | Result |
|---|---|
| Frame tanks | 15 (team split 7v7 + 1 non-roster entity) |
| Name join | **14/14 exact** — playerName, tankName, clanTag, teamNumber all match the decoded roster; 0 mismatches |
| Non-roster entity | 3760682 stays unnamed (never guessed) — correct fail-closed |
| Roster completeness | all 14 roster ids present in the frame |
| HP | 13/15 real (maxHealth/currentHealth via the L1 entity-base read); zeros = 1 non-roster entity + 1 alive read gap |
| Camera | eye (yz-swap posA) **1.9 m from the viewpoint tank** (x/z sub-meter), yaw 112.4° pointing at the in-view enemy (0.8° off), pitch **−1.4° level = aim-point pitch (CAM-013)**, viewpoint tank projects **below the viewport bottom** — the chase framing |

The frame's `replayTimeSeconds` was `0` in this capture — root cause: the
`GET /live/frame` endpoint built the discover request WITHOUT the session
id, so the batch core's ONE G2 replay-clock snapshot never ran.
**FIXED and LIVE-VERIFIED 2026-08-12** (sessions `019ff36c` / `019ff370`):
the endpoint now forwards the session id into `LiveFrameReadRequest`, so
the same `ReplayClockSource` the `/discover/entity-regions` path uses
supplies the estimated replay time (fail-closed: unknown/stale session
still yields null → 0.0, never an error). Unit-verified by
`LiveFrame_NoSession_LeavesDiscoverRequestSessionless` + the
session-forwarding assertion in
`LiveFrame_IdentifiesOwnEntity_FromViewpointParticipant`.

**The live verification exposed — and closed — a pre-existing launcher
anchor-date bug (2026-08-12):** the first capture read
`replayTimeSeconds: 86517.9` = exactly 24.03 h. Root cause: the launcher
derives the anchor DATE from the blitz-log FILENAME
(`blitz-logs_20260811...` — the LOCAL date), but the marker's leading
HH:MM:SS is UTC and the game keeps writing to the log it opened at launch
— so ANY local-evening launch (19:43 local = 00:43 UTC next day at UTC-5;
observed marker `00:43:42 [info] 19:43:42 -5`) lands its marker in the
previous day's file, parsing the anchor 24 h stale and rolling the G2
estimate off by exactly a day. Fixed in
`launch-offline-replay-for-od.ps1`: after parsing the marker, roll the
date forward (bounded, ≤ 4 days) until it sits within 10 min of UTC now
(a marker older than that cannot be THIS launch's replay start); the
existing gate-moment fallback applies if still stale. Second launch:
anchor `2026-08-12T00:48:20Z`, frame read **`replayTimeSeconds: 115.5`**
(mid-battle, sane).

**The same capture verified the own-nameplate suppression marker live:**
`overlayFrameResponse.ownEntityId` (3760577) matched the decoded session's
`ViewpointParticipantId` → participant `EntityId` exactly on both launches;
joins stayed exact (11/11 and 10/10 named, 0 mismatches vs the independent
roster) and HP stayed real (11/11, 10/10).

## Finding — X2b bound superseded by live evidence

The X2b rehearsal (OD-RECOVERY-086) concluded enemy ids are NOT enumerated
(own-team only, 7/14, precision 1.000). The live session proves the
**movement-filter family is time-varying**: mid-battle frames of battle 1
enumerated own-team only (7 tanks), but the **battle-start frames enumerated
the full roster (7v7 + 1 non-roster = 16 at battle-2 start of launch 1; 15 at
the launch-2 capture)** — the X4 loop's per-tick re-enumeration caught all
entities at t=0, and the per-id join resolved every enemy id **exactly**
(14/14, 0 mismatches, team numbers correct). Enemy ids are joinable whenever
the enumeration returns them; the residual limitation is mid-battle enemy
coverage (enumeration-dependent), not join correctness. Own-nameplate
suppression remains gated on viewpoint-id resolution (unchanged).

## Fail-closed behaviors re-demonstrated live

- Unknown/missing session → anonymous names, never an error.
- Non-roster entity id → null name, never guessed.
- Controller flip between auto-loop battles → `409` with
  `failureStage: playback-controller-vtable` (CAM-003), never a wrong frame.

## Evidence

- `.data/liveframe-v2-1.json` — decisive capture (6202 bytes, full frame).
- `.data/liveframe-v2-2.json` — the 409 body (controller flip).
- Host cross-check: `GET /api/v1/sessions/019ff354-9470-7be5-985f-71811ae754bf`
  (independent decoded roster, 14 participants).

## Change surface

**No code changed this session.** The pipeline (camera seam, HP wiring, name
join) shipped earlier and was exercised + verified live. The X4 roadmap row's
"Remaining live-gated" item is now closed.

**Next:** operator-gated publication applies (HP then yaw — both packages
READY); item 7 (hardware atomicity) stays LAST; L3 damage-dealt needs a NEW
object family (avatar/player-stats), not the entity records.
