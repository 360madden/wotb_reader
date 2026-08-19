# Penetration v0.3 — promotion-gate runbook (G1 items 2 and 5)

**Date:** 2026-08-18 (UTC)
**Status:** owner-run scenario specification — no live action taken here. This is
the concrete, command-by-command runbook that turns the acceptance criteria in
`pen-promotion-gates.md` into the two controlled battles that flip G1 items 2
(weapon state) and 5 (shot ray).
**Blocker:** `BLK-0027` (open until the two gates close).

## Why this exists

Both remaining G1 verdicts are **live-behavioral**: a passive replay cannot
supply a *known transition order*, and neither available replay swaps shells or
traverses the turret in a controlled way (recorded
`2026-08-18-medvedkovo-shell-swap-negative.md`). The read surfaces are shipped —
`shell-state` (G1 item 2, live-validated) and `gun-aim` (G1 item 5, merged
`4cf7a78`) — so the only missing input is a **freshly recorded controlled
battle**. That is an owner-run step; this runbook makes it safe and repeatable.

## What is already proven (do not re-derive)

- The ownership chain `AvatarGameLogic +0x1fc → VehicleGunRotator`,
  `+0x204 → VehicleGun`, `rotator+0x10 → owner`, `+0x04 → entity` is
  **live-proven** (`OD-PEN-OWNERSHIP-WALK`, 2026-08-16).
- The `shell-state` anchor resolves live on two replays (Churchill I savanna
  and medvedkovo): `index=0, identity0=5 (status/tier), identity1=71 (id)`,
  two-pass stable, 0 transitions (no swap in either).
- The `gun-aim` anchor is merged + tested; it reads the two `Update` inputs
  (`+0xe0`/`+0xe4`) and the gun-marker aim struct (`+0x28..0x40`).

## Shared launch + capture flow (both gates)

1. **Record** the controlled battle (below). The game auto-records it to
   `%LOCALAPPDATA%\wotblitz\DAVAProject\replays\*.wotbreplay`. Note the newest
   file (by timestamp) as the target.
2. **Launch** that recording through the canonical launcher (this imports,
   decodes, and reaches the `OfflineReplayVerified` gate):

   ```powershell
   scripts\launch-offline-replay-for-od.ps1 -ReplayPath <the-newest-replay.wotbreplay>
   ```

3. **Capture** during playback. Start the driver after the launch begins; it
   polls the gate itself and binds the artifact to its decode run:

   ```powershell
   # G1.2 weapon state:
   scripts\capture-pen-shell-state.ps1 -PollSeconds <battle-length-seconds>
   # G1.5 shot ray:
   scripts\capture-pen-shot-ray.ps1 -PollSeconds <battle-length-seconds>
   ```

   Both report distinct states and a transition count. Capture the whole
   battle so the pre-transition control window is included.

4. **Correlate** the reported transitions against the known order you recorded
   in step 1, then apply the gate's stop/pass conditions below.

## G1.2 — controlled shell-swap (weapon state)

**Record:** load shell **A**, fire it a few times; switch to a **different**
shell **B**, fire it a few times; stay on B for a final control window. Write
down the order (A then B) and which shell each is.

**Observe:** `capture-pen-shell-state.ps1` prints
`(index, identity0, identity1, kind, caliber)` tuples (plus damage). The
current-shell index is the field `ProcessCurrentShells` itself writes; the
`identity0/identity1` fingerprint distinguishes *which* shell, and the
`kind`/`caliber`/`damage` descriptor fields decode it (`Shell+0x114/+0x118/
+0x11c/+0x120`, read from the same resolved `Shell` object).

**Pass:** the fingerprint + descriptor (index, identity, kind, caliber) flip
exactly when the swap flips, is two-pass stable before and after, and the
pre-swap control window is stable.
**Stop / fail closed:** no flip on the swap, the fingerprint/descriptor changes
without a swap, or the control window moves.

## G1.5 — controlled turret traverse + gun elevation (shot ray)

**Record** two distinct maneuvers with the **hull stationary** (never drive):

1. **Turret traverse:** rotate the aim left→right→left only (no hull movement).
2. **Gun elevation:** raise and lower the aim only (no hull or turret yaw).

Write down the maneuver order and the rough aim direction at each phase.

**Observe:** run BOTH drivers side by side (or back to back) over the same
maneuver window:

- `capture-pen-shot-ray.ps1` prints `(input0, input1, direction)` tuples — the
  two ROTATOR inputs are the candidate (turret yaw, gun elevation) pair, and
  the direction is the gun-marker aim ray.
- `capture-pen-gun-angle.ps1` prints `(turretYaw, gunPitch)` tuples — the
  NAMED axes read from `CurrentGunAnglesComponent` (turretYaw `+0x10`,
  gunPitch `+0x14`).

Correlate the two: whichever rotator input (`input0` vs `input1`) tracks
`turretYaw` is the yaw field; whichever tracks `gunPitch` is the elevation
field. This correlation is what NAMES the rotator `+0xe0/+0xe4` pair.

**Discriminator:** hull yaw (`ring +0x30`, `Verified`) is read by the existing
live-frame surface, not the `gun-aim` anchor. The hull is stationary by
construction, so hull yaw must be constant across the whole window — confirm it
does not respond to the turret traverse (a formal read is available via
`POST /api/v1/game/discover/live-frame`).

**Pass:** the named `turretYaw` tracks the turret traverse (independent of hull
yaw), `gunPitch` tracks the elevation (stays put during the yaw-only move),
exactly one rotator input tracks each named axis (so `+0xe0`/`+0xe4` get
named), and the aim direction matches the known gun direction — **without** the
CAM-013 camera pose as the ray source.
**Stop / fail closed:** no clean yaw/elevation separation, hull yaw responds to
the traverse, or the only matching direction is the camera's.

**Honest note on "shot-synchronous":** the aim struct is the gun-marker *aim*
ray (per-frame), so "shot-synchronous" here means "the gun's direction, not the
camera's" (the gate's own parenthetical), not a fire-time snapshot. Strict
fire-time synchrony would additionally correlate against decoded `ShotImpact`
events and is not claimed by this surface.

## Repeatability rule (both gates)

Each gate needs **two content-distinct positive repeats** (two distinct battles
/ fresh processes), matching the Phase-4 `twoReplayRepeatability` discipline.
A single positive run records evidence but does not flip the gate.

## Safety / constraints

- Game install stays read-only; only the game's own auto-recorded replays are
  used, and they are launched through the managed pipeline (never modified or
  redistributed).
- The capture drivers print only aggregate values (index/fingerprint or aim
  floats); they log no tokens, paths, hashes, or account ids.
- Verify the game records the battle before assuming it is available (training
  room vs random battle recording is game-version-dependent; use a real battle
  if training-room replays are not saved).

## Open decision (owner)

The recording mode: **real random/co-op battle** (guaranteed to record) vs
**training room** (more controllable, but recording is unverified for this
build). This runbook defaults to a real battle with the controlled actions done
in-battle; confirm the recording mode once.
