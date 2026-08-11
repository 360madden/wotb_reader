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

## Root-cause isolation DONE (2026-08-11, session `019ff25b`, 3 probes)

A fingerprint session ran three read-only probes against the live walk.
**Result: the wrong-instance and wrong-field hypotheses are FALSIFIED;
the failure is a camera-MODE/state artifact of this launch.**

| Probe | Evidence | Conclusion |
|---|---|---|
| 1. GameCamera instance enumeration | process-wide vftable pattern scan (`base+0x32dafa0`, 10k-candidate cap) found **exactly ONE GameCamera instance** — and the walked chain reaches it (walked cameraState == the instance, class-OK) | **wrong-instance FALSIFIED** — there is no second/alt instance to land on |
| 2. Class identity cross-launch | CAM-004 verified run `cameraStateVftableHex 0x0365AFA0` vs today `0x03D8AFA0` — both = module base + RVA `0x32dafa0` (bases 0x380000 vs 0xAB0000 are pure ASLR) | class identity airtight; the read surface + offsets are the same object layout |
| 3. GameCamera object-window scan (`+0x00..0x200`, 128 floats) | posA `+0x38` = (−77.0, 92.2, 47.4) with tank at (−76.3, 40.9, 99.6): camera tracks the tank x exactly (−77 vs −76) but sits **51 m above / 52 m behind**; yaw 0.122 rad vs direction-to-tank 0.013 rad; pitch −5.8° vs pitch-to-tank −44.5°. No float triple in the whole window sits in the 1–50 m third-person band (only `+0x174` at 49.4 m, marginally outside) | **wrong-field FALSIFIED** — no near-tank pose exists anywhere in the object; posA/posB (+0x44) are the chase-interpolated pair but hold the HIGH pose |

**What this means:** the GameCamera object the chain reaches is real and
live (posA changes as the tank drives, tracks x exactly) but it holds a
NON-chase camera pose this launch — high above and behind the tank,
looking level. CAM-004's 23.57 m chase offset was measured on a launch
where the camera was in the chase state. The flipped-phase launch puts the
camera in a different state (orbit/spectator/high), so the v7 look-at
check correctly reports that the memory camera is NOT aimed at the tank.

The remaining discriminator (MODE vs wrong-pose, not yet run): capture the
rendered game window (the launcher shrinks it to a known size) and compare
the screen view against posA. If the screen shows the tank from high
above, posA is RIGHT and the mode is the cause — the W2S must consume the
high-camera pose as-is (the overlay still projects correctly; the tank is
just off-center). If the screen shows the chase view, posA is wrong for
this state and a field re-derivation is needed. Either way this is a
rendering-only decision; no resolver/offset-table change.


The session turns the CAM-004 `camera-state-consistent` verdict (GameCamera
posA `+0x38` is the true world camera; yaw cos/sin `+0x50/+0x54`, pitch
`+0x58`, basis `+0x80..0xB0`) into a W2S-relevant claim: if the memory
camera's yaw AND pitch aim at the tank, the decoded tank projects near
screen center under the overlay's analytic pinhole model, and the existing
`WorldToScreen` can render nameplates/beacons through the true camera
(modulo effective FOV, made robust by the 70/90/110° band sweep).

## Run (one command + the offline validator)

```text
# Launch the Oasis Palms replay to OK OfflineReplayVerified (launcher,
# battleSession= anchored), then mid-replay. Add -CaptureWindow to also
# capture the shrunk game window in-memory each round and persist ONLY
# derived sky/terrain scalars (never raw pixels) — the render-mode hint
# for the mode-vs-pose discriminator:
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/invoke-camera-state-verify.ps1 `
  -SessionId <decoded-session-guid> -ResultPath .data/cam001-v7-aggregate.json -CaptureWindow

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
viewport center across the FOV band), and the **pitch diagnostic coherent**
(memory pitch ≈ pitch required to aim at the tank). The CAM-001 script
verdict is `camera-state-consistent` (chain length 3 + identity + finite
rounds + ≥ 1 yaw- and position-correlated round).

**Validator corrections (2026-08-11, root-cause follow-up):**

1. The projection now uses the **MEMORY tank** (same wall time / memory
   space as the camera — the W2S overlay is inherently memory-space) as the
   PRIMARY target; the decoded tank at the yaw-aligned time is a
   cross-check only (`crossDecodedCenterByFov`). The old code projected the
   decoded tank, whose yaw-aligned time can be WRONG by the replay-clock
   skew (the 2026-08-11 runs aligned to 30–40 s while the reads were at
   ~180 s), which silently corrupted the look-at/center check.
2. Per-round `basis` (view-basis +0x80..0xB0, 12 floats) is persisted and
   the validator reports `cameraCoherent` — the memory-side half of the
   mode-vs-pose discriminator, independent of the chase-view assumption.
   **Layout VERIFIED 2026-08-11 on the v7b dumps:** stride-4 row-major
   3x4 view matrix — row0 +0x80, row1 +0x90, row2 +0xA0; row0 =
   (fx,-fy,-fz) of forward(yaw,pitch) (DAVA left-handed, dot 1.0000 across
   all 6 rounds); rows orthonormal with r0 x r1 = r2. The earlier
   contiguous/0x8C-gap guesses were wrong and are superseded.
3. With `-CaptureWindow`, per-round `screen` scalars (skyFraction /
   horizonRow / mean luminances) feed `renderMode` — **chase** (look-at
   ~0), **non-chase** (look-at large AND memory pitch far from
   pitch-to-tank; scene-independent, fires before the sky test), **high**
   (sky band visible), **unknown**. The sky-luminance branch alone is not
   scene-robust (Oasis dusk skies never pass the >0.5 row-luminance sky
   test — skyFraction stays 0–0.11), so the pitch-gap branch is the
   primary non-chase signal.

## CAM-010 — posA is stored (x, z, y): CAM-004's 23.57 m is superseded

Cross-session offline analysis (v7b `019ff26b` + v7c `019ff276` vs the
decoded store) proved the GameCamera position at `+0x38/+0x3C/+0x40` is
stored as **(x, z, y) — world Y and Z swapped**. yz-swapping posA puts it
**2.1–3.6 m from the decoded viewpoint tank on every round** (sub-meter
fit; the as-read distance was 113–206 m). CAM-004's 23.57 m equals
`√2·|tank.z − tank.y|` with z − y = 16.7 m — the same artifact, not a
third-person eye. The validator's `eye` is now `(posA.x, posA.z,
posA.y)`; v7c look-at measures 100° (tank behind camera), pitch-to-tank
−61°, verdict unchanged (non-chase honest-negative). Full record:
`docs/operations/handoffs/2026-08-11-cam010-yz-swap-position-convention.md`.
Remaining question: the orientation convention and the true render eye
(candidate: the ReplayCameraController `+0x28` ring).

## Known static values (do not change without re-verifying)

| Item | Value |
|---|---|
| Target build | 11.19.0.10 |
| Executable SHA-256 | `1cda5c31919c9784a41bee7f3270ec1b4536b124c51e8b36f2221b381760307d` |
| Replay | Oasis Palms (the CAM-001 verified replay) |
| Camera member path | ReplayCameraController `base+0x326dd0c` / GameCamera `base+0x32dafa0` (identity gates pass) |
| Live pose | GameCamera position `+0x38`, yaw cos/sin `+0x50/+0x54`, pitch `+0x58`, basis `+0x80..0xB0` stride-4 (CAM-001 v6 walk; basis layout verified 2026-08-11) |
| Prior verdict | CAM-001 `camera-state-consistent` (2026-08-11, CAM-004: GameCamera posA `+0x38` is the true world camera — 23.57 m third-person offset, 7/8 rounds) |
| CAM-003 caveat | session-controller vftable flips between launches; one relaunch allowed |

## Branching on the verdict

| Outcome | Action |
|---|---|
| **verified** (exit 0, look-at ≈ 0, pitch coherent) | W2S path proven: the overlay can render nameplates/beacons/POIs through the memory camera with the analytic pinhole model. Record `w2sProjectionVerified = true`; the overlay consumption seam (CAM-006 frame endpoint already serves the memory camera) is acceptance-tested. |
| **coherent but not aimed** (`cameraCoherent=true`, look-at large, `renderMode=non-chase`) | The walked GameCamera IS a real coherent camera in a NON-chase state (the 2026-08-11 shape). W2S still works — consume posA as-is, the tank simply projects off-center. Record the mode; the W2S seam is valid for the state as long as the basis is coherent. If the screen shows the chase view while posA reads non-chase, re-derive the pose field. |
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
