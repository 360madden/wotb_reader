# CAM-001 v7 live-run evidence — W2S projection cross-check (PRE-STAGED)

**Status: PRE-STAGED (2026-08-11)** — the template below is the fill-in
contract for the approved CAM-001 v7 live session. The validator
(`scripts/python/verify-camera-projection.py`) is BUILT and self-tested; the
v7 evidence collector (`scripts/invoke-camera-state-verify.ps1`) is BUILT
and parse-checked. What remains is ONE approved live launch (Oasis Palms,
mid-replay) to produce v7 round evidence, then the verdict branch below.

The session turns the CAM-004 `camera-state-consistent` verdict (GameCamera
posA `+0x38` is the true world camera; yaw cos/sin `+0x50/+0x54`, pitch
`+0x58`, basis `+0x80..0xA8`) into a W2S-relevant claim: if the memory
camera's yaw AND pitch aim at the tank, the decoded tank projects near
screen center under the overlay's analytic pinhole model, and the existing
`WorldToScreen` can render nameplates/beacons through the true camera
(modulo effective FOV, made robust by the 70/90/110° band sweep).

## Run (one command + the offline validator)

```text
# Launch the Oasis Palms replay to OK OfflineReplayVerified (launcher,
# battleSession= anchored), then mid-replay:
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/invoke-camera-state-verify.ps1 `
  -SessionId <decoded-session-guid> -ResultPath .data/cam001-v7-aggregate.json

python scripts/python/verify-camera-projection.py .data/cam001-v7-aggregate.json
```

Preconditions (same class as 087/088/089):
- The launcher reached `OK OfflineReplayVerified` with a launch-matched
  host-store session; the script consumes the SAME session id.
- No other host/game processes running (one guarded lease).
- Note CAM-003: the session-controller vftable FLIPS between launches
  (`base+0x325ad2c` vs `base+0x323d9bc`); the v6/v7 gate-free direct walk
  mitigates it — if the run reports `inconclusive`/identity failures,
  relaunch once (the 088/089 sessions each saw one CAM-003-blocked launch).

## Evidence to land in `.data/`

- `cam001-v7-aggregate.json` (schema `wotbtreader.cam001.camera-state-verify.v7`):
  per-round memory camera pose (posA `+0x38` + yaw `atan2(sin,cos)` + pitch
  `+0x58`), memory tank (when the resolver/walk resolved), decoded tank at
  the yaw-aligned time, aligned seconds, tank source.
- The validator output: per-round look-at angle, center distance across the
  FOV band, pitch diagnostic (`expectedPitchDeg` vs `memoryPitchDeg`), and
  the aggregate verdict (exit 0 = verified / 1 = failed / 2 = missing).

## Expected outcome (to be confirmed, never assumed)

`verify-camera-projection.py` exits 0 with per-round **look-at ≈ 0** (the
camera aims at the tank), **center distance small** (projected tank near
viewport center across 70/90/110°), and the **pitch diagnostic coherent**
(memory pitch ≈ pitch required to aim at the tank). The CAM-001 script
verdict is `camera-state-consistent` (chain length 3 + identity + finite
rounds + ≥ 1 yaw- and position-correlated round).

## Known static values (do not change without re-verifying)

| Item | Value |
|---|---|
| Target build | 11.19.0.10 |
| Executable SHA-256 | `1cda5c31919c9784a41bee7f3270ec1b4536b124c51e8b36f2221b381760307d` |
| Replay | Oasis Palms (the CAM-001 verified replay) |
| Camera member path | ReplayCameraController `base+0x326dd0c` / GameCamera `base+0x32dafa0` (identity gates pass) |
| Live pose | GameCamera position `+0x38`, yaw cos/sin `+0x50/+0x54`, pitch `+0x58`, basis `+0x80..0xA8` (CAM-001 v6 walk) |
| Prior verdict | CAM-001 `camera-state-consistent` (2026-08-11, CAM-004: GameCamera posA `+0x38` is the true world camera — 23.57 m third-person offset, 7/8 rounds) |
| CAM-003 caveat | session-controller vftable flips between launches; one relaunch allowed |

## Branching on the verdict

| Outcome | Action |
|---|---|
| **verified** (exit 0, look-at ≈ 0, pitch coherent) | W2S path proven: the overlay can render nameplates/beacons/POIs through the memory camera with the analytic pinhole model. Record `w2sProjectionVerified = true`; the overlay consumption seam (CAM-006 frame endpoint already serves the memory camera) is acceptance-tested. |
| **pitch convention fails** (`expectedPitchDeg` vs `memoryPitchDeg` consistently off / sign flipped) | The memory pitch `+0x58` needs a re-derivation or sign flip BEFORE the overlay swap — record the observed mapping, do NOT change the read surface without a new identity gate. |
| **look-at small but nonzero** (camera aims slightly above tank center) | Nameplate label offset input: record the offset magnitude; no surface change (rendering-only). |
| **CAM-003 flip** (identity fails on first launch) | Relaunch once (088/089 precedent); if it flips twice in a row, record and stop — do not broaden scanning. |

## After this session

- CAM-001 v7 closes the camera track's W2S acceptance gate; the overlay
  world-space features (nameplates, beacons, POI markers) consume the
  proven pose via the already-served frame endpoint.
- Next live gates in order: OD-RECOVERY-090 (L3 damage-dealt, Oasis
  attacker 3760577) + its Dead Rail Phase-4 repeat; yaw publication is
  READY (operator approval); the Phase-4 two-replay HP rule (Dead Rail
  victim 2549399) gates HP publication; item 7 (hardware atomicity) LAST.
