# OD-049 session-prep handoff (2026-08-05)

**Purpose:** pin every fact the OD-049 live round needs so the session is a
checklist, not an exploration. Covers the Dead Rail session id, the replay
file mapping, the exact M1 command (with `-SessionId`), the M2 command, and
the P1/P2 hazard runbook. Companion docs: the
[`offset-discovery-m1-m2-choreography.md`](../offset-discovery-m1-m2-choreography.md)
runbook (phase sequence + timing budget) and
[`offset-discovery-workflow.md`](../offset-discovery-workflow.md) Phase 0
(freshness gate).

## Repository state

- Branch `main`, head `558772a` (docs(ops): stale-publish blocker — host
  freshness guard + runbook rule), working tree clean, in sync with origin.
- Live-run prerequisites from the 2026-08-05 P0 pre-flight: host freshly
  republished (Aug 5) and verified serving trajectory data; rendezvous
  refreshed; od-048 fail-closed smoke green.

## Pinned session facts

| Fact | Value |
|---|---|
| Session id (Dead Rail) | `019fb86c-c8e7-7004-9df6-a574f5a7835b` |
| Map / duration | Dead Rail, `duration_ticks 2,713,761,600` ≈ **271.4 s** |
| Battle time (UTC) | 2026-07-29T17:35:16Z |
| Game version | 11.19.0 (decoder `wotb-11.x-strict`, run status 2/complete) |
| Ground truth samples | 33,281 position samples, 14 participants |
| Viewpoint | Churchill_I (251 samples; trajectory endpoint serves it first) |
| Source artifact sha256 | `59c3b92eb2217dcc43e9824ea497656678813ae810bd63abc6b3ba9d307fbb08` (1,100,265 bytes) |

## Replay file mapping

| Role | File |
|---|---|
| Launch-dir replay (matches artifact hash) | `.data\launch\a9aed0467d7843efb06bb3319bb52ded.wotbreplay` |
| What the launch script picks | Newest **original** `.wotbreplay` in `%LOCALAPPDATA%\wotblitz\DAVAProject\replays\` (top-level only; skips GUID leftovers and `wotbtreader-staging\`), or `-ReplayPath` |

**Verification step (do not skip):** after import, confirm
`./treader.cmd sessions` shows `019fb86c-…` (or `dotnet` CLI equivalent) and
that `GET /api/v1/game/discover/trajectory/{id}` returns the Churchill_I
series. If the newest game-folder replay is NOT the Dead Rail battle, pass
`-ReplayPath` to the launch script explicitly — the driver's `-SessionId`
pins the *ground truth*, but the *played battle* must be the same one or the
correlation has no signal.

## The live sequence (one launch, one window)

### Phase 0 — Pre-flight (offline, ~5 min)

1. **Fresh host:** run `serve.cmd` (it republishes) — never invoke
   `.build\publish\WotBTreader.Host.Web.exe` directly (Jul-31-class stale
   publish blocker). The OD launch path now fail-closes
   (`FAILED_host_stale_build`) if `bin\Release` is older than the newest
   `src\*.cs`.
2. **Trajectory check:** `GET /api/v1/game/discover/trajectory/019fb86c-c8e7-7004-9df6-a574f5a7835b`
   → 200 with 14 entities, viewpoint Churchill_I, real x/y/z samples.
3. **Rendezvous:** `%LocalAPPDATA%\WotBTreader\rendezvous\web.json` present
   (the driver discovers the host through it).

### Host start/stop cycle (verified 2026-08-05)

**Start (three interchangeable routes):**

- `serve.cmd` — republishes and starts the host in the foreground (Ctrl+C
  stops it). This is the route that guarantees a fresh host.
- `launch-offline-replay-for-od.ps1` — starts Host.Web itself with the
  research lease (`OfflineReplayEvidenceLifetimeSeconds=120`,
  `LifecycleEvidenceTimeoutSeconds=120`) and **auto-stops any stale host**
  via `Stop-OdProcesses` first; the canonical route for a live round.
- `dotnet run --project src/WotBTreader.Host.Web -c Release` — dev route;
  requires a fresh `bin\Release` build (the launch script's stale-build
  guard enforces this for the OD path).

**Stop (verified clean on the pre-flight host, pid 54436):**

- Ctrl+C in the `serve.cmd` window, or
- `Stop-OdProcesses` semantics (as implemented in
  `scripts/launch-offline-replay-for-od.ps1` line 108):
  `Stop-Process` on `WotBTreader.Host.Web` and any `dotnet.exe` whose
  command line matches `Host\.Web`, then a 2s settle. The next launch
  script run performs this automatically, so a forgotten host is never a
  blocker.

**Verified post-stop state (2026-08-05):** no `WotBTreader.Host.Web`, no
`dotnet.exe` running Host.Web, no `wotblitz` processes; port 9182 closed.
The stale `web.json` rendezvous capability remains on disk but is inert —
the next host start overwrites it, and the driver reads the fresh file.

### Phase 1 — Launch the offline replay

```powershell
powershell -File scripts/launch-offline-replay-for-od.ps1
```

(Add `-ReplayPath .data\launch\a9aed0467d7843efb06bb3319bb52ded.wotbreplay`
if the newest game-folder replay is not Dead Rail.) Managed launch → game up
→ `click-watch-offline.ps1` dismisses the dialog on `START_REPLAY_LOCAL` →
gate flips `OfflineReplayVerified`.

### Phase 1.5 — Pre-arm x32dbg NOW (during the load-settle window)

```powershell
powershell -File scripts/pre-arm-debugger.ps1 -AutoAttach
```

Do this immediately after launch, inside the driver's 15s `-StageDelaySeconds`
cover. **Never** let M2's lazy `-AutoWriteTrace` pre-arm do it: a mid-monitor
attach-pause stalls the replay while M1 is collecting samples, and a lazy
pre-arm burns up to 15s of the green window.

### Phase 2 — M1: monitor + correlate (the pinned command)

```powershell
powershell -File scripts/od-048-monitor-correlate-session.ps1 `
  -SessionId 019fb86c-c8e7-7004-9df6-a574f5a7835b `
  -MaxReadRounds 70 `
  -ResultPath .data\od-048-live.json
```

- `-SessionId` pins the ground truth (never let it auto-pick).
- `-MaxReadRounds 70` is the first-attempt budget: rounds run ~3s, not 2s;
  the 90 default lands the final correlate PAST battle end (271s) and
  collapses the green window to zero. Watch the `round=N/M series=… samples=…`
  log lines and budget on the **observed** rate; if rounds run ~2s, 80 is safe.
- **Start before battle start.** The driver warns
  `anchor_captured_after_verified` if it anchors after the battle began; if it
  starts late, pass `-ReplayStartWallTimeUtc` from the Start marker.
- Exit codes: 0 complete + report written; 1 preflight; 2 staging; 3 monitor;
  4 correlate; 5 report write.

**M2 success gate:** verdict `family-complete` (clean x/y/z triple) or ≥ 2
members. On `family_mapping_failed no_families_from_survivors` — **stop**; do
not burn the write-trace attempt on no family.

### Phase 3 — M2: write-trace in the SAME launch, IMMEDIATELY (the green window)

```powershell
powershell -File scripts/x64dbg-write-trace.ps1 `
  -FamilyFile .data\od-048-live.json `
  -AutoWriteTrace `
  -TraceSeconds 25 `
  -ResultPath .data\od-wt-live.txt
```

- Budget: `-MaxReadRounds 70` → verdict ~230s → **~31s green window** →
  `-TraceSeconds 25` (see the choreography §4 table).
- Must start **within seconds** of the verdict — the window is the battle
  tail. **The §7 human-reaction gap is still open:** hand-reacting costs
  5–30s of a ~31s window. Preferred: run the `-AutoWriteTraceOnVerdict`
  wrapper (the choreography's §7 item) once built; if reacting by hand, have
  the M2 command pre-typed in a ready terminal.
- Fail-closed exits: 7 = replay paused at start (press SPACE in game and
  rerun within the window); 8 = family stale (liveness re-read failed —
  never relaunch between M1 and M2).

**Success gate:** ≥ 1 hit with `family-hit` → `{rip}`-named evidence file in
`%TEMP%\od-wt-hits\` disassembles to the writing instruction (e.g.
`movss [reg+0x28], xmm0`) → member displacement.

## P1 / P2 hazard runbook

| # | Hazard | Guard | Operator action |
|---|---|---|---|
| P1-1 | Stale host build/publish 404s newer endpoints | Freshness gate (Phase 0-1) + launch-script `FAILED_host_stale_build` | Run `serve.cmd` (republish) before every session |
| P1-2 | Debugger attach mid-monitor stalls replay | Pre-arm in Phase 1.5 (load window) | Never let `-AutoWriteTrace` pre-arm lazily |
| P1-3 | M1 anchors after battle start (wrong wall anchor) | Driver WARNING + edge-aligned survivor demotion | Start M1 before battle start; else `-ReplayStartWallTimeUtc` |
| P1-4 | Wrong replay played vs pinned ground truth | Sessions + trajectory 200 check | Pass `-ReplayPath` if the newest folder replay is not Dead Rail |
| P2-1 | Rounds run slow (large staged set) → correlate past battle end | Watch `round=N/M`; budget 3s/round | `-MaxReadRounds 70` or shrink staged set (`-StageTopN 2`, `-ScanTolerance 8`) |
| P2-2 | No family from survivors | `family_mapping_failed` + M2 stop rule | Stop; recheck staging; do not burn the trace attempt |
| P2-3 | Replay paused at M2 start | Write-trace play-state probe, exit 7 | Press SPACE; rerun within the window |
| P2-4 | Mid-window pause | Advisory `WARN_replay_paused_mid_window` | Resume (SPACE) to keep capturing |
| P2-5 | Family stale (fresh launch / reallocation) | Liveness re-read, exit 8 | Never relaunch between M1 and M2 |
| P2-6 | Gate loss mid-window | Write-trace polls gate; stops early (exit 5) | Accept partial hits as evidence |
| P2-7 | > 4 family members (multi-copy family) | DR0–DR3 cap, first 4 armed | Prefer a `complete` 3-member family via `Select-BestFamily` |
| P2-8 | Battle ends before the trace deadline | `-TraceSeconds` budgeted under battle end | Read `family-no-hit` as a timing negative, not a field negative |

**Guardrails (reaffirmed):** do not rewind the replay for M2 (DAVA viewer is
seek-forward-only); do not relaunch between M1 and M2; do not run
`-SkipLivenessCheck`; `roll-replay-time-increased.ps1` is a memory-scan roll,
not a replay rewind.

## Stop rules

- Descope if: no surviving family in the first live round, or a
  `family-no-hit` repeats on the one retry with a smaller staged set.
- Record the outcome in the append-only
  [`offset-discovery-ledger.md`](../offset-discovery-ledger.md) using the
  ledger vocabulary (`CandidateFound`, `Partial`, `NoSignal`, `Blocked`,
  `Superseded`, or `Verified`).

## Assumptions and unknowns

- The newest original replay in the game replays folder is the Dead Rail
  battle (imported Jul 31 as artifact `59c3b92e…`); the launch script's
  "newest" pick has not been re-verified against the folder's current
  contents since Jul 31.
- First live M1 run is the first real exercise of the staging scans, the
  auto-scaled tolerance, and the battle-time budget — timing estimates are
  budget-model numbers until observed.
- `-AutoWriteTraceOnVerdict` (choreography §7) is **not built**; a manual
  M2 start is the fallback and risks losing part of the window.

## Recommended next steps

1. Build `-AutoWriteTraceOnVerdict` into od-048 (the only remaining piece
   between this handoff and a fully automated one-launch M1→M2 round).
2. Verify the game replays folder's newest file before the session and pin
   `-ReplayPath` in the session runbook.
3. Run the live round with this checklist; append the outcome (exit codes,
   verdict, timing observations) to the ledger and to a follow-up handoff.
