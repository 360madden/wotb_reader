# FRESH24 — CPU-liveness=running: frozen-window hypothesis DEAD; value-liveness discriminator built

Date: 2026-08-06
Status: **decisive negative + next discriminator built; FRESH25 settles world-advance**

## FRESH24 live outcome

`od-049-autoloop -AttachSmokeOnFirstRound -StageViewpointOnly` (50 rounds):

- Launch stack clean (click, marker 17:34:05, staging tick 5.3s, full 3000);
  smoke passed first try; 50/50 rounds, zero 401s; 29 strong survivors.
- `family_solo_emitted axis=z members=4 score=0.92 span=275.2 band=31.0s` —
  consensus class selected and 4 addresses armed.
- **THE ANSWER: `window_cpu_delta_ms=25672 liveness=running`** — the game
  consumed **25.7s of CPU during the 25s window** (≈ one full core). The game
  was fully executing.
- **`family-verdict=family-no-hit hit_members=0`** — STILL zero writes, 4/4
  traces, with the game running and the decoded replay proving the tank moves
  through the window.

## What this settles

**The frozen-window hypothesis is DEAD** (a frozen debuggee burns ~0 CPU, not
25.7s). Every prior no-hit is not an attach/resume artifact. The consensus
class (span-275.4 z, 47-50/50 matches) genuinely receives NO writes during the
trace window while the game executes.

## The new leading hypothesis: the battle world is not advancing during the window

A running game that doesn't advance the battle renders (CPU burns) but writes
nothing: **paused replay viewer, roster/transition screen, or a packet-gap
pause**. The pixel play-state probe returned `unknown` (screen state unknown),
and the CPU check cannot distinguish "playing" from "rendering a paused
frame". The armed addresses' values track z through the SAMPLING rounds
(t≈3–148s) but the trace window (t≈180–205s) sees no writes — consistent with
the replay entering a non-advancing state between the rounds and the trace
(the correlate + wrapper gap ≈ 104s).

## Fix (built + validated offline, `x64dbg-write-trace.ps1`)

**Value-liveness discriminator**: `Read-FamilyValues` (Host read API, same
mechanism as `Test-FamilyLiveness`) snapshots the armed addresses' floats at
window start (right after scriptrun) and end; reports
`window_values_changed=true|false` + `window_max_value_delta` (threshold 0.5
units on any armed address) and serializes both into the `.family.json`
report. A playing replay moves the armed z by tens of units per 25s; a
paused/roster replay leaves them bit-identical.

- Parse pwsh 7 + PS 5.1: clean. PSSA hygiene: PASSED. ASCII: clean.

## FRESH25 — the decision tree

| window_cpu | window_values_changed | meaning | action |
|---|---|---|---|
| running | true | world advancing, armed addresses moved, still no writes | REAL negative on a live moving world → deep science: the correlated class is a derived/one-time-copied value, not a per-frame field; pivot to mid-battle trace (arm during rounds) or read-verify approach |
| running | false | world frozen (paused/roster) | mechanism state: fix the window placement (trace only while the replay provably plays; use the pixel probe or a per-frame counter) |
| frozen | - | attach/resume failed | attach-once refactor |

Also worth doing alongside: fix the pixel play-state probe (it returns
`unknown` under the 640×360 resize) for a direct replay-state read.
