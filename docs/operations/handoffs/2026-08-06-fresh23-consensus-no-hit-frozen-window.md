# FRESH23 — consensus class armed, still family-no-hit; CPU-liveness discriminator built

Date: 2026-08-06
Status: **discriminator built + validated offline; FRESH24 settles frozen-vs-real**

## FRESH23 live outcome

`od-049-autoloop -AttachSmokeOnFirstRound -StageViewpointOnly` (50 rounds):

- Launch stack clean (click round 1, marker 17:21:41, staging tick 5.9s, full
  3000 staged); smoke passed first try; 50/50 rounds, zero 401s; 775 addresses
  scored.
- **The FRESH23 span-first fix worked exactly as designed:**
  `family_solo_emitted axis=z members=4 score=0.92 span=275.4 band=27.0s` —
  the consensus class (span 275.4) was selected and **4 addresses armed**
  (0x23537B10, 0x2354E210, 0x22BA8BD0, 0x22B5EB90 — all z, shift 0).
- Trace ran end-to-end (gate, liveness ok, attach pid=0xB584,
  scriptload+scriptrun, 25s window, released_detach, exit 0).
- **`family-verdict=family-no-hit hit_members=0` — again zero writes**, while
  the decoded replay proves the tank was moving through the window (battle
  t≈145–170s; z 230.5→182.5 across that span).

## Why this is now the frozen-window hypothesis, not a selection error

- **3/3 consecutive traces have produced zero hits** (FRESH20: battle ended
  mid-window; FRESH22: partial copy — selection; FRESH23: consensus class,
  correct selection — still zero).
- The known WOW64 attach-freeze (~1/3 of runs) is real, and **the trace never
  verifies the game resumed** after its attach+scriptrun — the SMOKE does
  (`resume_settle_rounds=1 verified=True`), the trace does not.
- If the trace's `run` fails to resume, the entire 25s "window" is a frozen
  game: zero hits are guaranteed regardless of what the addresses are. Every
  family-no-hit so far is consistent with that.

## Fix (built + validated offline, `x64dbg-write-trace.ps1`)

**CPU-liveness discriminator**: sample the wotblitz process
`TotalProcessorTime` right after `scriptrun` and right after the window; report
`window_cpu_delta_ms` + `window_liveness=running|frozen` (threshold ≥50ms of
CPU per second of window). A running game burns ~1–2 cores at 60fps; a frozen
debuggee burns ~0. The liveness is also serialized into the `.family.json`
report (`windowLiveness`, `windowCpuDeltaMs`) and the verdict log line.

- Parse pwsh 7 + PS 5.1: clean. PSSA hygiene: PASSED (no findings on the
  edited file). ASCII: clean.

## FRESH24 — the decisive round

Same campaign; the trace report now says `liveness=running` or
`liveness=frozen`:

- **frozen** → every prior no-hit was a mechanism artifact → fix the
  resume/attach path (attach-once: keep the smoke's verified session through
  the correlate so the trace reuses a known-good resume; or verify resume
  after scriptrun before counting the window).
- **running** → the no-hits are REAL: the span-275.4 consensus addresses are
  never written while the tank moves → the correlate is matching a computed
  or one-time-copied value, not a per-frame field → the science question
  deepens (read the address directly and watch its value change against the
  decoded trajectory in real time; the field may be derived, not stored).

Either way the next round is informative and the discriminator removes the
guesswork.
