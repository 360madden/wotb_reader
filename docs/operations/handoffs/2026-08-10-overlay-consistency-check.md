# Handoff — 2026-08-10: headless overlay consistency check (both replays)

**Status:** done, committed, pushed. Gate green.

## What and why

Every overlay feature was verified ad-hoc on Oasis Palms, but nothing walked
the whole overlay contract on **both** replays and failed loudly when a field
went bad. This adds a reusable headless checker that proves the pipeline on
real data and can be re-run after any future change to the frame path.

## The tool

`scripts/python/overlay-consistency-check.py` — read-only, takes `--host`,
`--db`, and optional `--sessions` (defaults to every session in the DB).
Walks each session at a 1 s step against the running web host and validates,
per frame:

- HTTP 200 + JSON parses; camera pose fields finite when present.
- Every tank: finite world X/Z; finite screen X/Y when `inViewport`;
  `hpFraction` in [0, 1]; `alive` boolean; **no resurrection** (a destroyed
  tank stays dead).
- Minimap math: each tank normalized against the `/maps/boundaries` extent
  must land in [−0.02, 1.02] — the exact math the HUD minimap uses.
- Pips carry finite screen positions.
- **Kill log invariant**: the frame's `kills` array is an append-only battle
  log — a repeated kill must match the earlier entry (same victim, same
  time), new kills append in time order with a fresh victim, and a full
  battle must have kills. (First draft wrongly assumed kills appear only at
  the kill instant — they repeat on every later frame by design.)

Exit code 1 on any failure, with the first 12 errors per session printed.

## Verification

- **Oasis Palms** (`019fee20-9315-70b7-a92c-379f41f69532`): PASS.
- **Dead Rail** (`019fee20-a2a5-7a04-8612-9cee3aaf7b1f`): PASS.
- Each walked 320 frames (t=0..319 at 1 s), every contract check green, kill
  logs 8 and 7 entries respectively, stable across every subsequent frame.
- The checker is self-tested: its first run caught its own wrong assumption
  (kill repeats flagged as double-deaths), so the current checks are known to
  detect real violations.
- `python -m py_compile` OK; `scripts/python/offline_check.py` green.

## Notes for next

- Re-run this after any change to `ReplayFrameSource`, the projector, the
  frame endpoint, or the minimap boundary math — it is the fastest
  regression net for the whole overlay data path.
- It only exercises the API contract, not the WPF render layer; the pure
  helpers in `W2sHudView` remain the render-side test seam.
