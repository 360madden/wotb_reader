# OD-049 session-prep handoff (2026-08-05)

**Purpose:** pin every fact the OD-049 live round needs so the session is a
checklist, not an exploration. Covers the medvedkovo session id, the replay
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
| Session id (medvedkovo, `.data` reference) | `019fb86c-c8e7-7004-9df6-a574f5a7835b` |
| Session id (LIVE host, per-import) | **auto-pick** — the OD launch host serves `%LocalAppData%\WotBTreader\treader.db`, and every import creates a NEW medvedkovo session id (e.g. `019fd261-721b-7171-a15e-7cfa6675931c` on 08-05). Hardcoding a `.data` session id 404s the trajectory fetch (live-round blocker, 2026-08-05). |
| Map / duration | medvedkovo, `duration_ticks 2,713,761,600` ≈ **271.4 s** |
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

**Verification step (do not skip):** after import, confirm the host's
`GET /api/v1/sessions?limit=1` newest item is the medvedkovo battle just
launched (a NEW per-import session id — the `.data` reference id
`019fb86c-…` will NOT be present on the live host, which serves
`%LocalAppData%\WotBTreader\treader.db`) and that
`GET /api/v1/game/discover/trajectory/{newestId}` returns the Churchill_I
series. If the newest game-folder replay is NOT the medvedkovo battle, pass
`-ReplayPath` to the launch script explicitly — the *played battle* must be
the one auto-picked, or the correlation has no signal.

## The live sequence (one launch, one window)

### Phase 0 — Pre-flight (offline, ~5 min)

1. **Fresh host:** run `serve.cmd` (it republishes) — never invoke
   `.build\publish\WotBTreader.Host.Web.exe` directly (Jul-31-class stale
   publish blocker). The OD launch path now fail-closes
   (`FAILED_host_stale_build`) if `bin\Release` is older than the newest
   `src\*.cs`.
2. **Trajectory check (live host):** `GET /api/v1/sessions?limit=1` → newest
   item is medvedkovo (a per-import id), then `GET
   /api/v1/game/discover/trajectory/{newestId}` → 200 with 14 entities,
   viewpoint Churchill_I, real x/y/z samples. (The `.data` reference id
   `019fb86c-…` is NOT served by the OD launch host — see the live-session
   note above.)
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
if the newest game-folder replay is not medvedkovo.) Managed launch → game up
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

### Phase 2 — M1: monitor + correlate (auto-pick the LIVE session)

```powershell
powershell -File scripts/od-048-monitor-correlate-session.ps1 `
  -MaxReadRounds 70 `
  -AutoWriteTraceOnVerdict `
  -ResultPath .data\od-048-live.json
```

- **No `-SessionId`.** The OD launch host serves `%LocalAppData%\WotBTreader`
  (the launch script does not set `Paths__ApplicationDataRoot`), and every
  import creates a NEW session id — the driver auto-picks the host's newest
  session (`/api/v1/sessions`, field `session.battleSessionId`), which is
  exactly the battle just launched. A hardcoded `.data` id (like the old
  `019fb86c-…`) 404s the trajectory fetch (live-round blocker, 2026-08-05).
  `-SessionId` remains available for pinned replay of a specific session.
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
| P1-4 | Wrong replay played vs pinned ground truth | Sessions + trajectory 200 check | Pass `-ReplayPath` if the newest folder replay is not medvedkovo |
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

- The newest original replay in the game replays folder is the medvedkovo
  battle (imported Jul 31 as artifact `59c3b92e…`); the launch script's
  "newest" pick has not been re-verified against the folder's current
  contents since Jul 31.
- First live M1 run is the first real exercise of the staging scans, the
  auto-scaled tolerance, and the battle-time budget — timing estimates are
  budget-model numbers until observed.
- `-AutoWriteTraceOnVerdict` (choreography §7) is **not built**; a manual
  M2 start is the fallback and risks losing part of the window.

## Recommended next steps

1. ✅ **DONE (2026-08-05):** `-AutoWriteTraceOnVerdict` built into od-048
   (same-process write-trace invocation on a usable-family verdict; separate
   `od-048-autotrace-*.json` report; M1 exit stays 0). The live round is now a
   single command (auto-picks the host's newest session):
   `od-048-monitor-correlate-session.ps1 -MaxReadRounds 70
   -AutoWriteTraceOnVerdict -ResultPath .data\od-048-live.json`.
2. ✅ **DONE (2026-08-06):** `-AttachSmokeOnFirstRound` internal chunk added —
   after the first monitor round proves the game readable, od-048 runs
   `x64dbg-write-trace.ps1 -AttachSmoke` against the LIVE game (hex-pid
   attach → pause → verify → optional bpm arm/clear → detach → verify
   resume) and writes `od-048-attach-smoke-*.json`. A red smoke aborts the
   campaign with **exit 6** before the correlate + trace window is spent,
   so FRESH10 is one launch with a fail-closed pre-flight, not multiple
   launches. Add `-AttachSmokeOnFirstRound` to the live-round command above.
3. Verify the game replays folder's newest file before the session and pin
   `-ReplayPath` in the session runbook.
4. Run the live round with this checklist; append the outcome (exit codes,
   verdict, timing observations) to the ledger and to a follow-up handoff.

## Live-round execution log (2026-08-05) — first attempts: BLOCKED, root cause pinned

Three launches were made on 2026-08-05; all three burned their battle window
before M1 could stage. This section records the evidence so the next session
starts from a known state.

| # | Launch | What happened | Root cause | Fix landed |
|---|---|---|---|---|
| 1 | `od-049-launch.log` | `click-watch-offline.ps1` crashed in-process with
  `The variable '$_' cannot be retrieved`; dialog never dismissed; replay
  auto-started after ~8s and played to end while gate waited | **PS 5.1
  here-string interpolation bug**: the C# `Add-Type` block used an
  interpolating `@"…"@` here-string, so PowerShell evaluated
  `$($_.Exception.Message)` (added by the PSSA-triage commit `7e2738b`)
  at script top-level where `$_` is undefined — thrown at `Add-Type` time,
  before any `watch_offline:` output | Commit `ec1586d`: single-quoted the
  C# here-string AND replaced the invalid PS-in-C# line with plain C#
  (`e.Message`); verified standalone + in-process |
| 2 | `od-049-launch2.log` | Gate verified, clicker worked; M1 404'd the
  trajectory fetch | Two compounding issues: (a) the launch script starts the
  host WITHOUT `Paths__ApplicationDataRoot`, so it serves
  `%LocalAppData%\WotBTreader\treader.db` (99 sessions), NOT `.data`
  `treader.db` (19 sessions) — the pinned id `019fb86c-…` lives only in
  `.data`; (b) every import creates a NEW session id, so the pinned id is
  stale by construction | Commit `fa44c98`: driver auto-picks the host's
  newest session via `/api/v1/sessions` (field `session.battleSessionId`,
  NOT the driver's old `/api/v1/read/sessions` + `session.id`); runbook
  updated — session id is HOST-derived, never hardcoded |
| 3 | `od-049-launch3` | Gate verified; M1B (`od-049-m1b.log`) staged into a
  DEAD battle: attempt 1 → **400** `discover.gate_not_satisfied`, attempts 2-3
  → **401** (evidence revoked), then `FAILED_staging_scan`, exit 2 | **Timing
  race (the master blocker).** The battle window is only **~107 s**
  (blitz-log `blitz-logs_20260805101329.txt`: `LoadGameScene begins 10:13:44`
  → `onLeaveWorld 10:15:31`), but the launch→gate→M1-start cycle took minutes
  (launch3 wrapper relaunched the game, ~2-3 min boot + click + gate); M1B's
  preflight anchored at ~10:16 local, ~1 min AFTER battle end. The 400/401 are
  consequences of staging into a finished battle, not auth bugs. The x32dbg
  `-AutoAttach` (fired right at M1 start) was NOT the killer this time —
  battle-end evidence proves it | No code fix needed; see the retry plan below |

**Key measured facts (from `blitz-logs_20260805101329.txt`):** battle start
`LoadGameScene ends 10:13:44`, first `onLeaveWorld 10:15:31` — a **107 s**
window from LoadGameScene to end. The ground-truth duration_ticks
2,713,761,600 ≈ 271 s is the full replay wall length (includes pre-battle
load); the *playable* window that M1 can sample is the ~107 s battle.

### Retry plan for the next live round (timing-first)

1. **Launch ONCE, then reuse.** `launch-offline-replay-for-od.ps1` plays the
   replay exactly ONCE per launch (no auto-loop). The game stays up after the
   battle ends and returns to the hangar/Watch-Offline dialog; re-click the
   same replay via `click-watch-offline.ps1`-style flow or a second launch
   script invocation WITHOUT stopping the game/host — do not re-boot.
2. **M1 must start before `LoadGameScene`.** The current `-StageDelaySeconds
   15` plus the wrapper's post-gate handoff costs ~20-30 s of the 107 s
   window. For the next round: start M1 immediately on gate flip and reduce
   `-StageDelaySeconds` to 2-3 (staging scans the battle head; the tolerance
   auto-scales).
3. **Reconsider Phase 1.5 pre-arm.** The choreography's "pre-arm during the
   load window" guidance was written against a slower launch; with a 107 s
   window, `-AutoAttach` right before M1 risks stalling samples. Options:
   (a) pre-arm x32dbg WITHOUT pausing (arm breakpoints on already-known
   addresses, no attach-pause) during Phase 1; (b) skip pre-arm entirely and
   let `-AutoWriteTrace` arm the write-trace immediately post-verdict — the
   green window is the battle tail (~31 s), and the auto-trace path is
   same-process so its own arm is fast. **The live-round evidence now
   contradicts Phase 1.5's "pre-arm NOW" — update the choreography before the
   next session.**
4. **Session id:** auto-pick (already fixed in `fa44c98`) — never hardcode.
5. **Battle-end budget:** if M1 cannot stage inside the window, treat the run
   as a timing negative (`Blocked`), NOT a field negative; the staging scans
   and correlate are unexercised until a launch lands M1 inside the window.

### Launch-4 execution log (2026-08-05, chained driver) — new evidence

The chained one-command launch (`launch` → `od-048` on gate flip) finally got
M1 **inside** the battle window — but the run still produced zero series.
Timeline from `blitz-logs_20260805103420.txt` + `od-049-m1c.log`:

| UTC | Event |
|---|---|
| 15:34:34 | `Start replay event` (battle 1 begins; `LoadGameScene ends 15:34:35`) |
| 15:36:22 | battle 1 tail `onLeaveWorld` (entities 2549395–2549408) |
| 15:37:20 | **M1C preflight anchors** — gate `OfflineReplayVerified`, but battle 1 is already ending (the chained start landed late) |
| 15:37:22 | staging scans start (elapsed 2.5 s); 3 entities, 9 axis scans, all 200 → **staged=3000** (MaxStaged cap) |
| 15:38:27 | `Start replay event` (battle 2 begins) — battle 1's vehicles `onLeaveWorld` |
| 15:38:27+ | monitor round 1 reads → **all 400 `discover.gate_not_satisfied`** |
| … | rounds 2–70 all 400, then 401 (gate fully `Denied evidence.monitor_unhealthy`) |
| 15:43:06 | battle 2 ends (`onLeaveWorld`); game exited shortly after |

**Diagnosis (two compounding causes):**

1. **Late anchor.** M1C anchored at 15:37:20 — battle 1 was already in its
   final minute. Its wall-clock anchor (`replayStartWallTimeUtc` = anchor
   time) was therefore ~3 minutes BEFORE the battle that was actually running
   when reads began, so the staging tick estimates were wrong for battle 2
   and the monitor could never align.
2. **Staging duration > remaining battle.** The 9 axis scans took ~65 s
   (15:37:22 → 15:38:27+). By the time the monitor started reading, battle 1
   had ended AND battle 2 had started — the gate revokes at the battle
   boundary (`onLeaveWorld` → evidence monitor flips unhealthy → gate
   `Denied`). **All reads hit the revocation window.** The 400s were NOT
   payload/address problems (a 2-address `/discover/read` repro succeeded
   while the gate was verified); they are the scanner's fail-closed
   `discover.gate_not_satisfied`.

**Key new fact — the game AUTO-LOOPS the replay.** `Start replay event`
fired twice (15:34:34 and 15:38:27) in one game session: after battle 1
ended, the viewer started battle 2 with the same replay, no operator input.
This means one game launch yields REPEATED battle windows — M1 can be
re-launched per loop iteration instead of relaunching the whole game, and
the next window starts ~10 s after the previous `onLeaveWorld`.

**The 400/401 answer for the driver:** `GameSessionCoordinator.GetScanAuthorization`
returns fail-closed whenever `_snapshot.State != OfflineReplayVerified`; the
gate flips at every battle boundary (evidence monitor sees the replay end).
The driver's own `monitor_stop gate-lost` logic exists but never fired
because the per-round gate poll raced the revocation — reads were already
being rejected before the poll observed the flip.

**Retry plan v2 (next session):**

1. **Anchor on the battle, not the gate.** Start M1 BEFORE the next loop
   iteration's `LoadGameScene` (i.e. during the ~10 s inter-battle gap), or
   pass `-ReplayStartWallTimeUtc` from the latest `Start replay event`
   marker so the anchor is the battle start, not the driver start.
2. **Shrink staging.** `-StageTopN 2` (viewpoint + 1) cuts the 9 scans to 6;
   `-ScanTolerance` tighter once the anchor is right. The staging scans are
   the dominant latency (65 s for 9 scans) and must finish before the battle
   boundary.
3. **Exploit the auto-loop.** One launch → many windows. On a failed attempt,
   re-run the driver during the next loop iteration without touching the
   game/host — no more full relaunch cycles between attempts.
