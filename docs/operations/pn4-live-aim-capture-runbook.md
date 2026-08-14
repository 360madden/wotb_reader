# PN-4 live aim-capture runbook (the decisive pen proof)

**Status: EXECUTED — PN-4 live proof passed on 2026-08-14. This runbook
remains the repeatable regression procedure for future replays.**
**Date:** 2026-08-14
**Roadmap:** Phase 6 (`docs/operations/product-roadmap.md`, row PN-4)

## Goal (one sentence)

Capture the CAM-013 chase-camera aim at each shot instant during a live replay
playback, then feed those aims into the already-shipped `PenValidation.Score`
core so the pen model is validated against the TRUE aim — the one thing the
decoded replay stream cannot provide (no turret/gun aim exists offline).

## Why this was the remaining step

The pen badge + model are **IMPLEMENTED** (PN-1/2/3/5). Before this run, the
offline scorer (`PenOfflineScorer` + `GET/POST
/discover/pen-offline-score/{id}`) had scored three content-distinct replays
at 38.9% / 69.6% / 71.9% band accuracy, but every offline shot used the
attacker→victim **center-line** proxy, which provably never reached the ≥70°
ricochet regime (all 67 Oasis shots <45°). The mesh raycast and face
classification were verified correct; the residual error was aim-source
fidelity, not the model. The executed CAM-013 run supplied the missing true
angle and closed this validation step.

## Executed result (2026-08-14)

The managed Dead Rail run reached `OfflineReplayVerified` after the canonical
window move/resize and Watch Offline click. The capture recorded 150
G2-proven samples from 9.4–278.9 replay seconds and stopped on
`Denied / evidence.replay_completed` after writing its output. Against the
same 78-shot decoded session, the center-line baseline was 69.565% band
accuracy and 66.667% ricochet precision (4/6); the CAM-013 override report was
72.727% and 80.000% (4/5).

The procedure was then repeated on the content-distinct Oasis/Churchill
session: 161 samples from 7.2–287.4 seconds, 67 shots / 2 skipped; baseline
38.889% band accuracy with six predicted ricochets versus CAM-013 46.667% and
zero predicted ricochets. Both runs stopped on the durable replay-completion
state. These are PASS results for the aim-source validation, not a claim of
perfect game prediction; the remaining Unknown side/rear cases, manual shell
selection, and deterministic RNG limits stay as documented. The durable
evidence is `handoffs/2026-08-14-pn4-live-aim-validation.md` and
`handoffs/2026-08-14-pn4-second-replay-regression.md`.

## Prerequisites (all already true)

- Host.Web Release build (the aim-override endpoint landed 2026-08-14).
- A decoded session with `ShotImpact` events + `attackerEntityId` (any of the
  re-decoded ground-truth sessions: `019ffdcd`, `01a00028-…`, `01a0007f`).
- The CAM-013 chase-camera pose live-verified (it already drives the live
  badge's aim via `LiveFrameProjector.BuildCamera`).
- The G2 clock anchor (`sameDecodedClockProven`) so the live frame's
  replay clock is the SAME clock as the decoded `ShotImpact.ReplayTime`.

## The capture loop (the only new runtime artifact)

During playback, poll the existing endpoints at ~10 Hz and record one
`AimSample` per poll. The aim ray MUST be reconstructed the same way the live
badge does (`LiveFrameProjector.BuildCamera`, the CAM-010/CAM-012 verified
path) — do not re-derive it:

1. `POST /discover/live-frame` → the live frame's `ReplayTimeSeconds` (the
   G2-anchored clock) and the projected camera pose.
2. `POST /discover/camera-pose` → the raw CAM-013 pose: `X/Y/Z` and `Basis[0..8]`.
3. Reconstruct the aim ray in the decoded-world convention (X/Z horizontal,
   Y up — the SAME convention `AimRay` documents):
   - **eye** = `(pose.X, pose.Z, pose.Y)` — the yz-swap (CAM-010).
   - **forward** = `(-basis[3], -basis[4], -basis[5])`, normalized (forward =
     −row1, CAM-012).
4. Record `AimSample { ReplayTimeTicks = liveFrameClock.Ticks, Aim = (eye, forward) }`.
5. Accumulate until the battle ends (the existing completion signal /
   teardown statuses from the OD lane).

Only the VIEWPOINT tank's own shots consume these overrides; every other shot
keeps the center-line proxy (the scorer enforces this, fail-closed).

## The feed (now turnkey — POST, optional body)

After the battle, POST the accumulated aims to the scorer endpoint:

```
POST /api/v1/game/discover/pen-offline-score/{battleSessionId}
Content-Type: application/json
X-WotBTreader-Capability: <fresh rendezvous capability>

{
  "aimOverrides": [
    {
      "replayTimeTicks": 1234567890123,
      "originX": 102.4, "originY": 3.1, "originZ": 174.2,
      "directionX": 0.71, "directionY": 0.0, "directionZ": 0.70
    }
  ]
}
```

The endpoint maps each entry to `AimSample` (the scorer re-normalizes any
non-unit direction; a degenerate ray falls back to the center-line proxy).
`GET`-style invocation with an empty body (`{}`) keeps the center-line proxy —
the offline behavior is unchanged. No aim overrides + no body = center-line.

## Pass criteria (record in the handoff)

- Compare `ricochetPrecision` with the offline center-line result whenever
  both reports have a non-zero denominator. If the true aim removes every
  predicted ricochet, report the metric as not applicable (`0/0`) rather than
  treating it as an improvement or a regression.
- The predicted-ricochet outliers that penetrated offline (87.5° Oasis /
  72.5° Copperfield) are re-scored with the true incidence and either agree,
  are removed as center-line artifacts, or localize a real model term
  (plate/incidence) — never silently counted as a correct prediction.
- Band accuracy is reported per-shot with margins so any remaining disagreement
  points at a specific term (loaded shell, side/rear nominal armor) rather than
  aim fidelity.

## What this does NOT need

- No T1 turret/gun discovery (the chase camera already aims at the turret-level
  aim point; T1 stays for the exact gun lock-on, a separate PN-5 lane).
- No loaded-shell decode (still CLOSED — the 6-byte signature is an
  effect-entity id; the stock-shell proxy remains for the manual selector).
- No new memory offsets or read-surface changes (the camera pose seam is
  already live-verified).

## Evidence contract

- A handoff with the per-shot table (predicted vs decoded outcome, incidence,
  effective armor vs pen) and the resulting `ricochetPrecision` +
  `bandAccuracy` versus the offline center-line baseline.
- No fabrication: skipped shots carry their reason; a shot without an aim
  override at-or-before its time keeps the center-line proxy.
