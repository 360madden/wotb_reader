# Handoff — FRESH19 session prep (2026-08-06)

**Status:** READY for live. HEAD `888fb58` (fix(ops): FRESH19 — capability-401
retry, 90s shift sweep, game stop after campaign). Working tree clean.
Predecessor: `2026-08-06-fresh18-postmortem-fresh19-fixes.md` (full post-mortem).

## Environment state

- Frozen game from FRESH18 killed (`GAME_STOPPED`); a Host.Web may still linger
  on 9182 — the launch script kills stale hosts itself, but for a clean slate
  kill it first (stale-publish rule: **republish the host before every live
  session**).
- Replay: `.data\launch\a9aed0467d7843efb06bb3319bb52ded.wotbreplay` (Dead Rail,
  viewpoint = entity 2549401 mrkool1138 / Churchill I).

## The three fixes to verify live (this is the round's thesis)

| Fix | What to watch in the log | Old signature |
|---|---|---|
| Capability-401 retry (`Invoke-Api`, ≤5×2s) | **zero** `api_failed status=401` lines; at most a few `api_capability_retry` lines that recover | FRESH18: 3 hard 401s → holes → wide bands |
| Shift sweep 30→90 (`MaxTimeShiftSeconds`) | best result **not** edge-aligned; band does not touch ±90 | FRESH18: z rode the −30 edge at −18.5 |
| Game stop after campaign (`-KeepGame` opt-out) | `stopping game after campaign` in the autoloop tail; no frozen roster afterwards | FRESH18: frozen "Not Responding" roster from the replay-loop reload flake |

## Expected output per stage (chunked, no wasted session)

1. **Launch** — `resize_window ok=True from=* to=640x360`, single click, `watch_exit=0`, gate verified.
2. **Staging** — `tick_est≈5s` (the FRESH18 attendance fix), `staged=3000`, smoke `pause=True bpm=yes resume=True`.
3. **Correlate** — 173-class viewpoint results, but now with the best at a **non-edge shift** (predicted ≈ −20…−50s) and **band width < 15s**. That combination passes the band-floor gate → `strong_survivors≥1`.
4. **Arm + trace** — solo survivor armed, `scriptrun` memory-BP trace, first `odwt-*.bin` hit report.

## Success criterion (from the dry-run)

Score ≥ 0.9 at the non-edge shift with a narrow band. The dry-run's noise-free
ceiling was 1.000 @ shift 0; live float32 rounding shaves it — 0.9+ is strong.

## Failure modes and what they'd mean

- **Still edge-aligned at ±90** → the anchor needs an evidence-based attendance
  estimate (constant 50s is wrong per-replay; derive match-begin from decoded
  first-sample time or log markers) — next round, not a re-run.
- **Wide band even at the true shift** → 401 holes (fixed) or a tick-rate
  mismatch (constant shift can't fix a rate error — would need a rate dimension
  in the scorer).
- **Smoke exit 6** → auto-relaunch (campaign loop, max 3) — known
  x64dbg/WOW64 resume flake, not new.

## Operator notes

- Keep hands off the game window for the full ~6 min (the 640×360 resize means
  it can't cover your other windows).
- A brief freeze + "Not Responding" at the smoke (~55s, in live battle) is the
  debugger attaching — normal. A *permanent* freeze post-run is now prevented by
  the game-stop.
