# CAM-001 v7 live-run evidence — W2S projection cross-check (2026-08-11)

**Status: HONEST-NEGATIVE — 2 launches, same signature; recorded, no
scanning broadened.** Session `019ff23a-…` (launch 1) and
`019ff243-…` (launch 2, the one-relaunch allowance) both returned
`camera-state-found-unverified-offset`: the camera chain identity gates
PASS (3/3, vftables match, finite 6/6) but posA `+0x38` read ~120 m from
the viewpoint tank with pitch ≈ −1.5° while pitch-to-tank is ≈ −46° — the
memory camera is NOT aimed at the tank on either launch, so the v7
look-at/W2S acceptance cannot be confirmed. Tank source was verified
CORRECT both times (memory tank matches the decoded trajectory within
2.4 m at the same tick). Root cause unresolved (see the diagnosis below);
the template below remains the fill-in contract for a future session that
isolates the cause.

## Diagnosis (both launches, 2026-08-11)

| Evidence | Launch 1 `019ff23a` | Launch 2 `019ff243` |
|---|---|---|
| verdict | `camera-state-found-unverified-offset` | same |
| camera chain | 3/3, identity True | 3/3, identity True |
| vftable matches | ReplayCameraController + GameCamera both match | same |
| finite rounds | 6/6 | 6/6 |
| yaw-correlated rounds | 5/6 (0.0537 rad) | 1/6 |
| pos-correlated rounds | 0/6 | 0/6 |
| third-person offset norm | 123.16 m | 124.10 m |
| extra (posC) offset | 373.36 m | 332.52 m |
| tank source | entity-position (Resolved) | entity-position (Resolved) |
| memory↔decoded tank delta | — | 2.4 m at tick 1.81–1.85B (tank source CORRECT) |
| camera height vs tank | y≈130 vs y≈42 (88 m above) | same |
| memory pitch | ≈ −1.5° (level) | ≈ −1.5° (level) |
| pitch required to aim at tank | ≈ −46° | ≈ −46° |

**What passed:** the gate-free camera walk + identity gates are solid — the
chain resolves and the pose fields are finite and yaw-plausible. The
`0xAB0000` ASLR-probe base with a successful rescan was seen on BOTH
launches (the CAM-003 session-controller flip variant `base+0x325ad2c` was
live: `/discover/entity-position` returned `UnsupportedReplayController`
after launch 1's run, and launch 1's run itself silently resolved a
wrong tank position; launch 2 resolved the correct one).

**What failed:** the memory camera POSITION does not sit 1–30 m from the
tank on either launch (120+ m, ~88 m above, level pitch). CAM-004 measured
23.57 m with 7/8 position-correlated rounds on the same replay — so the
field layout is not the difference; the CAMERA OBJECT the walk lands on
(this launch class) appears not to be the active third-person camera, or
it is mid-transition. This is NOT resolved — candidates: (a) the flipped
session-controller phase changes which camera the member path reaches,
(b) a second GameCamera instance exists and the walk lands on it, (c) the
camera was in a non-third-person mode during the reads (no operator input
was applied, so the default view should be third-person). No guesses were
promoted; the read surface and offset table are untouched.

**Next step (isolate, do not guess):** one session that (1) captures the
same walk while ALSO dumping the camera vftable hex and a few sibling
pointers in the GameCamera region to fingerprint the instance, and (2)
checks whether CAM-004's launch had a different session-controller
variant. Only then re-attempt the v7 look-at check.


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
