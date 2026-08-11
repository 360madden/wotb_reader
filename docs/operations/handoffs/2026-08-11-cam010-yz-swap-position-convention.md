# CAM-010 — GameCamera posA is stored (x, z, y): CAM-004's 23.57 m verdict is an artifact

Date: 2026-08-11. Binary: wotblitz.exe 11.19.0.10 (hash `1cda5c31…1760307d`).
Replays: Oasis Palms (sessions `019ff276` [v7c] and `019ff26b` [v7b], both
launched by the canonical launcher; CAM-003 flipped session-controller
phase on both). Read-only offline analysis of the persisted v7b/v7c
aggregates against the decoded store. Nothing promoted; resolver and read
surface untouched.

## Finding

**The GameCamera position at `+0x38/+0x3C/+0x40` is stored as (x, z, y) —
the world Y and Z are swapped relative to the tank/entity position space.**
Applying the yz-swap to posA puts the camera **2.1–3.6 m from the decoded
viewpoint tank** on every round of both sessions (sub-meter fit), while the
as-read distance was 113–206 m.

## Evidence (per-round, both sessions)

| session | rounds | as-read vs tank | yz-swap vs tank | camera yaw vs tank yaw |
|---|---|---|---|---|
| v7c (`019ff276`) | 6/6 | 206.0 m (all) | **2.1 m (all)** | 101° gap (all) |
| v7b (`019ff26b`) | 4/6 | 113.7–130.3 m | **2.1–3.6 m** | 154–177° gap |
| v7b (mid-turn) | 2/6 | 123.8–125.1 m | 21.0–21.4 m | 152–176° gap |

The two 21 m rounds are read-time jitter: the camera and `/entity-position`
reads in a round happen up to ~1 s of replay time apart, and the tank was
turning (yaw 2.186→2.024 rad) during those rounds. The 2.1 m rounds had a
stationary tank. Residual math: v7c offset = `sqrt(1.03² + 146.7² +
144.6²)` with `tank.z − tank.y = 146.5`; v7b round 0 = `sqrt(2)·92.4` with
`z − y = 92.4` — the as-read distance is exactly the artifact
`sqrt(dx² + 2·(tank.z − tank.y)²)`.

## CAM-004's 23.57 m was the same artifact

CAM-004 (`cam001-camera-state-verify-20260811-095640.json`, schema v6)
measured `thirdPersonOffsetNorm = 23.57 m` as the as-read distance between
posA and the memory tank on the GOOD session-controller phase. Under the
yz-swap finding that distance equals `√2·|tank.z − tank.y|` at the read
moment, i.e. `z − y = 16.7 m` — the tank happened to be at a spot with
`z − y ≈ 16.7 m` for 7/8 rounds (stationary tank), producing a stable
"23.57 m third-person offset" that was misinterpreted as the chase camera
distance. The other failed v1–v6 runs (114.9 / 226.2 / 269.3 m) are the
same artifact at other tank positions. **The GameCamera posA is the
viewpoint-tank position (±2 m) with Y/Z swapped — it is NOT a third-person
eye 23.57 m behind the tank.**

## What this means

1. **CAM-004 verdict superseded.** "GameCamera posA is the true world
   camera at 23.57 m third-person offset" is incorrect as stated. Correct
   reading: posA (yz-swapped) tracks the viewpoint tank to within ~2 m —
   the camera rides ON/AT the tank in these sessions.
2. **The W2S consumption seam must yz-swap world→camera-space.** The
   overlay consumes the memory pose; when projecting world points
   (tank/entity positions) the world→camera transform swaps (y, z) — or
   equivalently the world eye is `(posA.x, posA.z, posA.y)`. The
   orientation fields (yaw cos/sin `+0x50/+0x54`, pitch `+0x58`, basis
   `+0x80..0xB0`) are self-consistent with each other; their world-space
   mapping is the remaining open question (see below).
3. **The camera is NOT yaw-locked to the tank** on these sessions
   (101–177° gap), consistent with the previously-isolated non-chase
   camera STATE on the flipped phase. The chase state (camera aimed at the
   tank) was only ever seen on CAM-004's good-phase launch; the real chase
   eye distance was never actually measured (23.57 m was the artifact).

## Open question (next live unit)

Where is the true render eye, and what is the orientation convention?
Candidates: (a) posA is the eye and the replay camera genuinely rides the
tank in these sessions (then W2S = yz-swap(posA) + basis as stored);
(b) the true eye lives in the ReplayCameraController `+0x28` ring or
another controller field (the ring entries are per-frame 16-byte records
written by vtable slot 4). Discriminator probe: dump the controller region
+ ring on one launch (any phase) and search for a position triple that
(1) is not the tank, and (2) is consistent with a chase eye ~5–30 m behind
the tank along the camera forward, or with the game's actual render view.
The `-CaptureWindow` screen scalars stay diagnostic-only (no raw pixels).

## Files touched

- `scripts/python/verify-camera-projection.py` — world eye = yz-swap of
  the stored posA (CAM-010), fixtures rewritten to the stored (x, z, y)
  convention; `lookAtAngleDeg`/`expectedPitchDeg`/center now measure true
  world geometry (v7c: look-at 47°→100°, pitch-to-tank −45°→−61°, tank
  behind camera; verdict unchanged: non-chase honest-negative).
- `docs/operations/cam001-v7-evidence-template.md` — CAM-010 section.
- `docs/operations/offset-discovery-ledger.md` — CAM-010 entry,
  Next-planned.
- `docs/operations/handoffs/2026-08-11-cam004-camera-state-consistent.md`
  — SUPERSEDED notice appended.
- `AGENTS.md` — active-workstream paragraph corrected.
- `docs/operations/product-roadmap.md` — camera row corrected.
