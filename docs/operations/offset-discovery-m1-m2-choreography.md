# M1→M2 same-launch choreography (live runbook)

**Date:** 2026-08-05
**Owner:** offset-discovery track
**Parents:** [`offset-discovery-strategy-v4.md`](offset-discovery-strategy-v4.md),
[`offset-discovery-roadmap.md`](offset-discovery-roadmap.md)
**Status:** spec — validated offline (P0 pre-flight 2026-08-05); live round pending

## 1. The hard constraint that shapes everything: no rewind, no hot-swap

The WoT Blitz DAVA replay viewer is **seek-forward only** (architecture doc: "the
timeline slider can seek forward to any point but cannot go backward"), and the
in-game replay browser **flushes and reinitializes the scenario** when a replay
is selected — a soft restart, not a hot-swap (research/replay-loading-mechanisms).
`roll-replay-time-increased.ps1` is a **memory-scan roll** (snapshot → compare
`increased` with operator Space pulses), NOT a replay-time rewind.

**Consequence:** the M2 write-trace green window cannot come from "roll the replay
back and re-trace the same segment". It must be the **tail of the first playback**:
M1's final correlate produces the family verdict while the battle is still playing,
and the write-trace arms on those family addresses in the SAME process and SAME
playback for the remaining battle seconds. This is why the write-trace's liveness
check (exit 8) exists — it rejects a stale family from a fresh launch, so M2 can
only ever run inside the M1 launch's own window.

## 2. Why one launch can do both M1 and M2

- M1's monitor loop re-reads the staged addresses every 2s; at round 10 it runs a
  provisional correlate and stages the ±16-byte neighbors (family refinement), so
  the final correlate at monitor end returns the family (x/y/z triple).
- The monitor ends early on **rounds-exhausted** (MaxReadRounds) or
  **battle-ended** (anchor + duration + MaxTimeShift + 10). The battle is 271s
  (Dead Rail), so the final correlate must fire with battle time left. The green
  window is the difference (see §4 for the budget — rounds cost ~3s, not 2s, so
  `MaxReadRounds 70` is the first-attempt default, not the 90 default). The
  write-trace's `-TraceSeconds` is capped to fit the remaining window.
- The gate stays `OfflineReplayVerified` through the window (the liveness
  heartbeat rolls the evidence expiry while the identity matches), so the
  write-trace's gate precheck and poll succeed.

## 3. Operator sequence (one launch, one process)

### Phase 0 — Pre-flight (offline, done 2026-08-05)

- **Republish the host** (`serve.cmd` does this; the Jul-31 publish predated the
  trajectory/correlate endpoints and was the P0 blocker — a stale publish wastes a
  CAP-2 session).
- Host up on `http://127.0.0.1:9182`; rendezvous `web.json` present under
  `%LocalAPPDATA%\WotBTreader\rendezvous\`.
- `GET /api/v1/game/discover/trajectory/{battleSessionId}` returns the Dead Rail
  session with real samples (verified 200, 14 entities, viewpoint Churchill_I,
  251 samples).
- od-048 fail-closed smoke: no game → `FAILED_gate_never_verified`, exit 1, no
  report (verified).

### Phase 1 — Launch the offline replay

```powershell
scripts/launch-offline-replay-for-od.ps1
```

Managed launch → game up → `click-watch-offline.ps1` dismisses the dialog on the
`START_REPLAY_LOCAL` marker → gate flips `OfflineReplayVerified`. Start the M1
driver **before the battle reaches battle start** (the driver warns
`anchor_captured_after_verified` otherwise; pass `-ReplayStartWallTimeUtc` from the
Start marker if it starts late).

### Phase 1.5 — Pre-arm x32dbg NOW (not lazily at Phase 3)

```powershell
scripts/pre-arm-debugger.ps1 -AutoAttach
```

Do this **immediately after launch, during the load-settle window** (the driver's
15s `-StageDelaySeconds` is a natural cover). The write-trace's `-AutoWriteTrace`
would lazily pre-arm if missing, but `Invoke-AutoPreArm` waits up to **15s for the
debugger window** — that burn comes straight out of the green window. Pre-arming
early also means the debugger **attach-pause lands during loading, not mid-battle**
(a mid-monitor attach stalls the replay while M1 is collecting samples).

### Phase 2 — M1: monitor + correlate (same launch)

```powershell
scripts/od-048-monitor-correlate-session.ps1 `
  -SessionId 019fb86c-c8e7-7004-9df6-a574f5a7835b `
  -MaxReadRounds 70 `
  -ResultPath .data\od-048-live.json
```

Driver waits for the verified gate, stages (viewpoint + top movers), monitors every
2s, refines the family at round 10, and at rounds-exhausted runs the final
correlate. `-MaxReadRounds 70` is the conservative first-attempt budget (see §4:
rounds run 3s, not 2s — 90 default can land the correlate PAST battle end).
Report lands at `.data\od-048-live.json` with `verdict`, `results`,
`families` (baseAddress/spanBytes/axesCovered/complete/members[address,
offsetBytes, axis, sign, score, edgeAligned]).

**Success gate for M2:** verdict `family-complete` (a clean x/y/z triple) or at
least a family with ≥ 2 members. If `family_mapping_failed no_families_from_survivors`
— stop per the M2 stop rule; do not burn the write-trace attempt on no family.

### Phase 3 — M2: write-trace in the SAME launch, IMMEDIATELY (the green window)

```powershell
scripts/x64dbg-write-trace.ps1 `
  -FamilyFile .data\od-048-live.json `
  -AutoWriteTrace `
  -TraceSeconds 25 `
  -ResultPath .data\od-wt-live.txt
```

Must start **within seconds** of the M1 verdict (the window is the battle tail) —
see §6 for why this should be automated before the live round. The driver:

1. Pre-arms x32dbg if missing (`pre-arm-debugger.ps1 -AutoAttach`).
2. Gate-prechecks `OfflineReplayVerified`.
3. Play-state probe: requires the HUD icon `playing`. If it reads `paused` the
   driver **waits up to `-PlayProbeTimeoutSeconds`** for the replay to resume
   (press SPACE in the game during that wait) before failing closed with exit 7 —
   rerunning is only needed after a hard exit 7.
4. **Liveness:** re-reads the armed family addresses through the guarded Host read
   API — same process, so they must still be live (exit 8 if the family went stale,
   e.g. a fresh launch happened between M1 and M2).
5. Arms 4-byte hardware write breakpoints (`bph <addr>,w,4`) on the members
   (DR0–DR3 cap 4; a complete family is exactly 3).
6. Holds the window (`-TraceSeconds`, advisory mid-window play-state probe),
   capturing `{rip}` on every write to the member addresses (fast-resume keeps the
   replay playing).
7. Writes hits to `<ResultPath>` and the per-member evidence to
   `<ResultPath>.family.json` with `family-hit` / `family-no-hit`.

**Success gate:** ≥ 1 hit with `family-hit` → the `{rip}`-named evidence file in
`%TEMP%\od-wt-hits\` disassembles to the writing instruction (e.g.
`movss [reg+0x28], xmm0`) → member displacement.

### Phase 4 — Evidence

- Disassemble the first `odwt-<addr>-<rip>.bin` in the hits dir → the instruction
  → the member's offset expression.
- Record per the workflow Phase 5 (M3 repeatability: 2 launches × 2 replays).

## 4. Timing budget (Dead Rail, 271.4s)

| `MaxReadRounds` | Verdict ~ (rounds at 3s) | Green window ~ | Recommend `-TraceSeconds` |
|---|---|---|---|
| 60 | 200s | 61s | 55 |
| **70 (first live attempt)** | **230s** | **31s** | **25** |
| 80 | 260s | ~1s — TOO LATE | — |
| 90 (default) | ~290s — PAST BATTLE END | none | — |

Budget model: `green_window ≈ battleSeconds − (StageDelay + rounds×3 + correlate +
traceStartOverhead)`, with `StageDelay = 15`, `correlate ≈ 5`, `traceStartOverhead
≈ 10` (prechecks + liveness + arm).

> **The rounds are NOT 2s each.** The monitor sleeps `-ReadIntervalSeconds` (2s)
> per round but also reads up to `-MaxStaged` (3000) addresses in 6×500-chunks ON
> TOP — with a large staged set a round runs 2.5–3s+, so 90 rounds can reach
> 225–270s and land the final correlate at/near battle end (271s), collapsing the
> window to ~0. The table above budgets rounds conservatively at **3s**; the
> 2026-08-04 prep docs assumed 2s, which is only true for tiny staged sets.

**Tuning rule:** watch the `round=N/M series=... samples=...` log lines early in
M1 and confirm the actual per-round wall time. Budget on the OBSERVED rate, not
this table; if rounds run ~2s, 80 is safe, but for a first live attempt start
conservative (`-MaxReadRounds 70` → thin ~30s window) or shrink the staged set
(`-StageTopN 2`, `-ScanTolerance 8`) to keep rounds fast. The monitor must exit on
**rounds-exhausted**, not battle-ended — the final correlate needs battle time
left. A `family-no-hit` verdict from a too-small window is a timing negative, not
a field negative; record it and retry once with a smaller staged set.

## 5. Edge cases and fail-closed behavior (prevention, not hope)

| Hazard | Guard | Operator action |
|---|---|---|
| Stale publish (endpoints missing) | P0 republish + trajectory 200 check | Republish before every live session |
| Rounds run slow (large staged set) | Watch `round=N/M` log lines; budget at 3s/round | `-MaxReadRounds 70` or shrink staged set |
| Debugger attach mid-monitor stalls the replay | Pre-arm in Phase 1.5 (load window) | Never let `-AutoWriteTrace` pre-arm lazily |
| M1 starts after battle start (wrong anchor) | Driver `anchor_captured_after_verified` WARNING; edge-aligned survivor demotion | Start M1 before battle start, or pass `-ReplayStartWallTimeUtc` |
| No family from survivors | `family_mapping_failed` + M2 stop rule | Stop; recheck staging; do not burn an attempt |
| Replay paused at M2 start | Write-trace play-state probe, exit 7 | Press SPACE in game; rerun within the window |
| Mid-window pause | Advisory `WARN_replay_paused_mid_window` | Resume (SPACE) to keep capturing |
| Family stale (fresh launch / reallocation) | Liveness re-read, exit 8 | Never relaunch between M1 and M2 |
| Gate loss mid-window | Write-trace polls gate; stops early (exit 5) | Accept partial hits as evidence |
| > 4 family members (multi-copy family) | DR0–DR3 cap, first 4 armed, rest reported unarmed | Prefer a `complete` 3-member family in `Select-BestFamily` |
| Battle ends before the trace deadline | `-TraceSeconds` budgeted under battle end; no-hit verdict is honest | Read `family-no-hit` as a negative result, not a bug |

## 6. What NOT to do (guardrails reaffirmed)

- Do **not** try to rewind the replay for M2 — impossible in the DAVA viewer.
- Do **not** relaunch the game between M1 and M2 — the liveness check (exit 8)
  exists to reject exactly that.
- Do **not** run M2 with `-SkipLivenessCheck` to "save time" — a stale family
  produces a clean-looking no-hit window that reads as evidence. It is not.
- Do **not** run `roll-replay-time-increased.ps1` expecting a replay rewind — it
  is a memory-scan survivor roll for other campaigns (OD-018/OD-045 fallback
  paths), not the M1→M2 glue.

## 7. The human-reaction gap (must be closed before the live round)

"Start the write-trace within seconds of the verdict" is not reliably doable by
hand: reading the report and typing a command costs 5–30s of a ~60s window. The
robust fix is to **chain M1 → M2 in one invocation**: a thin wrapper (or an
`od-048 -AutoWriteTraceOnVerdict` switch) that, on a `family-complete`/family
verdict, computes the remaining battle budget from `duration_ticks` and
immediately invokes the write-trace with the same `-FamilyFile`. Until that
wrapper exists, the live round should be run with the operator's hands already on
the second command, and the window assumed to be ~30s not ~60s. Recommend building
the wrapper before spending the first CAP-2 session.

## 8. Validation status

- P0 pre-flight (2026-08-05): host republished, trajectory 200 with real samples,
  rendezvous refreshed, od-048 fail-closed smoke exit 1 — all green.
- Write-trace: family mode + `-AutoWriteTrace` built and offline-validated
  (PS 5.1 parse, PSSA gate 0 warnings, 13-check helper simulation, DryRun smoke in
  both modes, exit codes 2/2/2/0) — see the roadmap M2 row and handoff Amendment 5.
- The C# report contract the write-trace parses is pinned by
  `CompleteFamilyReportMatchesTheWriteTraceParseContract` (committed `69c0717`).
