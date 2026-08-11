# T1 — Turret-facing discovery session (design, PRE-STAGED 2026-08-11)

**Status: DESIGN — live-gated.** Turret rotation has NO replay ground truth
(three-way negative, `record-diffing-groundwork.md` §Turret-facing): type-10
carries hull rotation only, type-5 spawn broadcasts carry no rotation
floats, and the ring record + entity base carry hull rotation only. The
lag-correlation playbooks (087/088/089) cannot prove a turret field, so
this session is LIVE-BEHAVIORAL: capture the viewpoint tank's entity base
while its turret traverses and the hull stays stationary.

## The driver: the replay camera IS the turret input

In WoT Blitz replay playback, the viewed tank's turret is driven by the
**camera aim** — rotating the camera rotates the viewed tank's turret while
the recorded hull rotation (decoded packets) stays exactly as played. The
camera pose is already live-verified (CAM-001: GameCamera posA `+0x38`,
yaw cos/sin `+0x50/+0x54`, pitch `+0x58`, basis `+0x80..0xA8`), and the
batch surface reads entity-base regions under ONE replay-clock attestation.
So the session is a script composition, no new product code:

1. **User drives:** rotate the camera (free/arcade view) for ~30–60 s of
   continuous turret traverse while the hull stays put (drive straight or
   stop first; avoid hull turns during the traverse).
2. **Driver captures** at ~1 Hz, each step under the SAME lease + ONE G2
   clock attestation:
   - the **camera pose** (`CameraPoseReadResponse`: yaw/pitch/pos) — the
     turret driver reference,
   - the **entity-base region** of the viewpoint tank (batch surface,
     `EntityBaseRegionLength` ≥ 0x200 to sweep past the measured layout
     into unexplored entity-base territory; 4 KB cap allows up to ~0x1000),
   - the **decoded hull rotation** at the attested replay time (offline
     join after the session).
3. **Offline analysis** (`scripts/` scratch, evidence-first):
   - verify the hull stayed put: decoded hull yaw + memory `+0x50` constant
     (≤ 0.05 rad) across the traverse,
   - for every 4-byte offset in the region, classify the value series:
     **rotation-like floats** (in [−π, π], continuous) that CHANGE during
     the traverse are turret/aim candidates;
   - the decisive discriminator: a candidate must correlate with the
     **camera yaw** (the replay turret driver) — NOT the constant hull yaw —
     with the same per-dump lag machinery as 088/089 (wrap-aware,
     bounded lag, median + spread reported),
   - **int32 target-id candidates**: fields that hold a ROSTER entity id
     (join against `participants`) at any traverse step — lock-on /
     auto-aim state, if the replay simulates it (see open questions).

## Discriminator (turret vs hull copy vs decoy)

| Field class | Behavior during traverse | Verdict |
|---|---|---|
| Hull copy (known: ring `+0x28/2C/30`, entity-base `+0x48/4C/50`, pos `+0x3C/40/44`) | constant (hull stationary) | excluded |
| **Turret yaw** | rotation-like, changes, tracks the CAMERA yaw (wrap-aware, bounded lag) | **HIT — the overlay gets turret-facing** |
| Independent aim (pitch-only traverse, gun elevation) | rotation-like, tracks camera PITCH while yaw constant | HIT (gun elevation) |
| Zero/constant-filled decoy | never matches a turning camera series | demoted (flatness) |
| target-id (lock-on) | int32 ∈ roster ids, changes when the user re-targets | HIT (lock-on state, if simulated) |

Fixture discipline from 089 applies: a zero-filled offset can degenerate-match
a stationary 0.0 series — the camera is turning during the traverse, so a
matching field must TRACK the camera series, and control steps (camera held
still) must stay flat.

## Evidence contract (fill on completion)

- `.data/t1-turret-snapshots-<stamp>.json` — steps: replay time (attested,
  `sameDecodedClockProven=true`), camera yaw/pitch, entity-base region
  (base64), viewpoint entity id, hull-yaw memory value `+0x50`.
- Offline verdict: hull-stationary check (decoded + memory), the candidate
  offset list with per-dump lag scores vs camera yaw, and the target-id
  scan.
- Record under an OD-RECOVERY id in the ledger + this section filled; no
  offset table, resolver, or read-surface changes; chain-field edits ONLY
  with the live proof.

## Branch table

| Outcome | Action |
|---|---|
| Turret yaw found (tracks camera, hull constant) | Record offset + score; the overlay gains turret-facing (and "turret aimed at me" = turret-bearing-to-player check). Phase-4 repeat on the second replay before any publication. |
| Gun-elevation/pitch candidate only | Record; turret yaw not in the entity base → sweep sibling structures (the transform object's `+0x38..0x58` rotation region, hash-bound note) in a follow-up. |
| No rotation candidate | Entity base has no turret rotation → the turret lives deeper (transform/sibling); record the honest negative, do NOT guess. |
| Lock-on id found | Record; note whether the replay simulates auto-aim at all (open question) before claiming live-game lock-on. |

## Open questions (answered by the evidence, never assumed)

1. Does replay playback simulate the viewed tank's auto-aim / lock-on state,
   or is it input-only (frozen in replays)? The target-id scan answers this.
2. Does the turret track the camera yaw 1:1, or with a per-entity
   turret-offset (hull + turret-relative)? The camera-correlation residual
   reveals which.
3. Is the turret rotation in the entity base at all, or only in the
   transform/sibling? The region sweep + branch table settles it.

## After this session

- Turret-facing HIT unlocks the nameplate "turret aim line" overlay feature
  (the hull-aim-line is already computable today).
- Next live gates in order remain: CAM-001 v7, OD-RECOVERY-090 + repeat,
  the HP Phase-4 rule, yaw publication (READY, operator approval), item 7
  LAST.
