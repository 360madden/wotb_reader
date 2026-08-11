# CAM-003 — session-controller phase drift: entity-position blocked today

Date: 2026-08-11. Binary: wotblitz.exe 11.19.0.10 (hash `1cda5c31…1760307d`,
layout hash re-validated by the endpoints). Evidence-negative milestone:
the camera pose remains verified, but the memory-tank-position cross-check
is blocked by a game-phase/controller-arrangement drift vs the
08-09 sessions. Read-only probes; nothing promoted, resolver/read surface
untouched.

## What happened

Repeated approved launches (clean `OK OfflineReplayVerified` every time)
+ CAM-001 v5/v6 runs confirmed the camera chain live again (identity gates
pass: ReplayCameraController `base+0x326dd0c`, GameCamera
`base+0x32dafa0`), but the position correlation never computed:
`/discover/entity-position` and `/discover/position-page` both return
`UnsupportedSessionController / session-controller-vtable` on every call,
at gate+8s, gate+55s (the od-073-proven window), and mid-replay.

## Root cause — phase-fragile resolver gates + a third session controller

1. The resolver (`Type10EntityPositionResolver`) validates the controller
   chain against hard-coded vftables verified for one arrangement:
   AppController `0x0323d61c`, SessionController `0x0323d9bc`,
   AccountController `0x0323eae4`, PlaybackController `0x03253aa4`
   (layout `Type10EntityPositionLayout.WotBlitz1119010`).
2. Today's game runs a **third session-controller vftable** (RVA
   `0x325ad2c`) during replay playback — neither the resolver's
   `0x323d9bc` nor the previously-recorded replay variant `0x323d9f0`.
   Live chain walk (module-rooted, app vftable verified): app matches,
   `[session+0x118]` (account) is garbage, `[account+0x128]` (playback) is
   garbage → the layout member offsets do not apply to this variant.
3. The direct manual walk (no vtable gates) fails at the same place, and —
   the decisive control — the **unchanged od-073 poll fails 12/12 today**
   (`UnsupportedSessionController`), the same script that scored 24/24 on
   2026-08-09 (`withinOneWorldUnit: 12`, `withinThreeWorldUnits: 21`,
   `stable-resolver-positive`). The 08-09 resolvable arrangement is simply
   not reproducible today.
4. Plausible driver: the game's DLC update this morning
   (`DAVAProject/dlc_manager*` logs at 04:39-04:44) changed the replay-flow
   controller arrangement. The exe hash still matches the layout; the
   runtime object graph does not.

## CAM-001 v6 (committed with this record)

- Default `-StageDelaySeconds` 55 (the od-073-proven read window).
- `Walk-EntityPositionMemory`: direct replication of the resolver chain
  (gameCore → app → session → account → playback → connection → entities →
  cached/tree → entity → movementFilter → avatar-helper ring → position
  record) with NO vtable gates, double-read stability, using the layout
  constants. Falls back automatically when `/entity-position` is gated.
- Memory-vs-decoded calibration field (`memoryDecodedDelta`) computed at
  the yaw-aligned decoded time when a memory tank position is available.
- Schema `wotbtreader.cam001.camera-state-verify.v6`.

The walk is inert when the controller arrangement doesn't match (returns
null → the aggregate records `direct-walk-failed`); it will work when the
game returns to a layout-compatible arrangement.

## Also recorded (weak-signal warnings)

- The yaw alignment against the decoded frame timeline was LOOSE in the v6
  sessions (best delta ~0.081 rad — coincidence-level against a varying
  curve), so `yawCorrelated` should not be read as a tight match from
  those runs. The tight 0.0027 rad alignment stands only from the
  04:59 align-probe.
- The decoded frame yaw timeline is genuinely varying (0 → 0.127 → −0.397
  → 0.564 → 1.641 → … → 2.036 rad over 0-280 s), so a tight memory match
  is meaningful when it occurs.

## Next steps

- **Map the `0x325ad2c` controller layout empirically** (scan the live
  session object for fields holding objects with the known account /
  playback vftables) so the direct walk can use variant-aware offsets.
- Or validate the camera via projection (memory camera yaw == decoded
  camera yaw is proven; project decoded tank positions through the memory
  camera and check W2S) — no memory tank read needed.
- Or wait for the game's arrangement to flip back (DLC/server dependent).
