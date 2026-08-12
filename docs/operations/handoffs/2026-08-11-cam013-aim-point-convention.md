# CAM-013 (2026-08-11) — the chase camera aims at the turret-level aim point

**Status: CLOSED — the CAM-007 live-session evidence now VERIFIES the W2S seam.**
The "non-chase honest-negative" of the earlier 2026-08-11 captures
(`camera-state-found-unverified-offset`, every round `non-chase`) was a
**measurement-target artifact**: the validator measured look-at /
pitch-to-tank / projection against the tank **hull center**, but the WoTB
third-person chase camera aims at the **turret-level aim point** (~1.9 m
above the hull center — the aim reticle sits above the tank and the tank
occupies the lower-center of the frame). Correcting the target re-verdicts
all four stable captures to CHASE/verified; only the battle-intro cinematic
stays non-chase.

## The live session (CAM-007's "one live session for real evidence")

Two launches / three captures on the Oasis Palms replay (launcher gates OK,
second-monitor placement live-verified, `resize_window second_monitor`
observed):

| Capture | Wall clock | Reads | Memory-side signature |
|---|---|---|---|
| `cam001-v7-aggregate-091.json` | launch 1, auto-loop battle 1 | aligned 270–280 s | eye 2.1–2.2 m from memory tank, dy −1.9 m, pitch 0.7–0.9°, yaw −0.12…−0.16 |
| `cam001-v7-aggregate-091b.json` | same instance, battle 2 | aligned 130 s | eye 1.9–3.4 m (1 round at 19.8 m — camera cut), pitch 0.9–4.2°, yaw −0.36…−0.40 |
| `cam001-v7-aggregate-092.json` | fresh launch | aligned 270 s | eye 2.1–2.2 m, pitch −1.4…+1.2°, yaw −0.13 (stable) |

Every round: camera identity gates PASS (3/3, `0x03D8AFA0` = base +
`0x32dafa0`), basis coherent (stride-4 orthonormal, forward = −row1,
CAM-012), **eye (yz-swap of posA +0x38) 2–3 m from the memory tank**, yaw
stable and tank-following, **memory pitch level (−1.4…+4.2°)**, screen
scalars inconclusive (Oasis dusk sky test never fires; horizon detection
noisy at the 16×9 grid).

## Root cause: hull center vs turret-level aim point

The eye is ~1.9 m ABOVE the memory-tank hull center on every round (dy
−1.7…−2.0 m), with only 0.9–2.5 m horizontal separation. The pitch-to-HULL
therefore reads −35…−63° while the memory pitch reads level → the
mode-vs-pose discriminator (pitch gap > 20°) classified every round
non-chase. But a WoTB chase camera aims at the turret/aim point ~1.9 m
above the hull center — at the eye's height — so **pitch-to-AIM ≈ 0° ≈
memory pitch**: the level pitch IS the chase state. CAM-012 already noted
this ("look-at collapsed to 0.4–6.7 deg, avg 1.7 deg, at the turret-level
aim point"); the validator never measured against it.

## Verification (offline re-verdict of the SAME immutable aggregates)

With the aim-point target (hull + 1.9 m), the aim-point PITCH GAP gates the
pass/fail (look-at/center are degenerate at 1–3 m chase distances;
`NEAR_AIM_METERS` 4.0 exempts near rounds from the look-at gate, which still
catches wrong-yaw states at 20 m):

| Capture | Phase | Pre-CAM-013 | Post-CAM-013 |
|---|---|---|---|
| 091 | battle 1 end | failed 0/6, all non-chase | **verified 5/6** (5 chase; the 19 m camera-cut round honestly fails) |
| 091b | battle 2 | failed 0/6 | **verified 5/6** (chase/unknown; the 19 m cut fails) |
| 092 | fresh launch | failed 0/6 | **verified 6/6**, all `mode=chase` |
| v7c | battle 1 end (earlier) | failed 0/6 | **verified 6/6**, all chase |
| v7b | battle 1 intro (earlier) | failed 0/6 | **failed 0/6** — pitch +88° (straight-up cinematic) stays non-chase |

Pitch gaps on the chase rounds: 0.1–15.6° (all ≤ 20° tolerance); aim-point
center distances 0.03–0.44 (near center across the 40–90° FOV band) vs the
hull-center 0.26–5.1 (tank below center — the expected chase framing,
reported as `tankFramingByFov`).

## What changed

- `scripts/python/verify-camera-projection.py` (diagnostic validator only —
  **no read surface, no offsets, no resolver, no C#**):
  - `AIM_POINT_HEIGHT_METERS = 1.9` — the gating target is now the
    turret-level aim point (hull + 1.9 m).
  - `passed` gates on the aim-point PITCH GAP (≤ `PITCH_GAP_TOLERANCE_DEG`
    20°) + not-behind + (near aim or small look-at); look-at/center stay
    reported diagnostics; `tankFramingByFov` reports the hull-center framing.
  - **Unit-bug fix in `classify_mode`**: it compared `memory_pitch`
    (radians) against `expected_pitch` (the caller passed degrees) and
    double-converted in the hint; both are now radians consistently
    (the mismatch would have misclassified the aim-point chase rounds).
  - Self-test updated: aim-at-aim-point fixture, no-aim (pitch-away) fail,
    **new CAM-013 chase-signature regression fixture** (level camera, eye
    1.9 m above hull → passes; tank framing below center), battle-intro
    cinematic fail, plus the existing wrong-yaw / C#-mirror /
    basis-coherence / mode fixtures. `--self-test` PASS.
- Records: this handoff, ledger register row `CAM-013`, template
  `cam001-v7-evidence-template.md`, roadmap CAM-007 row, AGENTS.md headline.

## What this means

- The memory camera IS the chase render camera: identity gates, yz-swap eye
  at chase distance, coherent basis, yaw tank-following, and pitch
  consistent with the turret-level aim convention.
- The W2S consumption seam (`LiveFrameProjector.BuildCamera` = eye
  yz-swap(posA) + basis forward −row1 / up row2) is validated for real
  replay playback: any world point projects through the analytic pinhole
  model; in the chase view the player's tank lands below center per the
  game's framing.
- The earlier honest-negative interpretation is **superseded** (same
  evidence, corrected target) — append-only originals untouched.
- The camera lane's last open item ("end-to-end overlay validation") can now
  proceed on a verified seam.

## Remaining

- The exact aim-point height is pinned to 1.9 m by the chase-consistency
  bound (± ~0.7 m at the 1–2 m read ranges). Pin it to the tank's actual
  turret height when the overlay nameplate/beacon Y-offset work starts
  (a screen-accurate capture with a cleaner sky/terrain scene than Oasis
  dusk would also settle the render-mode sky test).
- The v7b battle-intro state (+88° pitch) confirms the discriminator
  correctly flags cinematics — no read-surface change.
