# PN-4 live aim-capture runbook (the decisive pen proof)

**Status: PRE-STAGED — the code path is complete; this runbook is the turnkey
session plan. Execute when a live launch is approved.**
**Date:** 2026-08-14
**Roadmap:** Phase 6 (`docs/operations/product-roadmap.md`, row PN-4)

## Goal (one sentence)

Capture the CAM-013 chase-camera aim at each shot instant during a live replay
playback, then feed those aims into the already-shipped `PenValidation.Score`
core so the pen model is validated against the TRUE aim — the one thing the
decoded replay stream cannot provide (no turret/gun aim exists offline).

## Why this is the remaining step

The pen badge + model are **IMPLEMENTED** (PN-1/2/3/5). The offline scorer
(`PenOfflineScorer` + `GET/POST /discover/pen-offline-score/{id}`) has already
scored three content-distinct replays at 38.9% / 69.6% / 71.9% band accuracy —
but every offline shot uses the attacker→victim **center-line** proxy, which
provably never reaches the ≥70° ricochet regime (all 67 Oasis shots <45°). The
mesh raycast and face classification are verified correct; the residual error
is aim-source fidelity, not the model. The live CAM-013 aim is the only aim
source that reaches the real shot angle.

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

- The scored report's `ricochetPrecision` improves over the offline center-line
  result (Dead Rail's 4/6 is the offline best; a live aim should raise it).
- The predicted-ricochet outliers that penetrated offline (87.5° Oasis /
  72.5° Copperfield) are re-scored with the true incidence and either agree or
  localize a real model term (plate/incidence), not an aim artifact.
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
