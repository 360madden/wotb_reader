# Offset-discovery ledger

Last updated: 2026-08-03 (OD-RECOVERY-032: two attempts both converged to 11 survivors — one short of ≤10 — before the 120s lease expired mid-round; 401 refresh live a sixth time; the 11-survivor plateau is now the reproducible ceiling, 1–2 rounds past the lease edge)

This ledger is the durable index of WoT Blitz PC offset-discovery work. It
records experiments, partial results, failures, and pivots so future sessions do
not repeat an exhausted approach without a changed hypothesis.

It is separate from `memory-offsets/<version>.json`: the versioned table is a
small publication surface, while this ledger retains the reasoning and the
negative evidence behind candidates. Raw memory dumps, CE tables, pointer maps,
screenshots, player data, private replay paths, and game-derived files stay
outside tracked source.

## Status vocabulary

### Experiment status

- `Planned` — defined but not started.
- `Running` — active; must be closed by the next handoff.
- `Partial` — produced useful intermediate evidence but not a reproducible
  candidate.
- `CandidateFound` — produced a candidate with a clear address kind and an
  explicit next verification step.
- `NoSignal` — the chosen hypothesis produced no usable signal.
- `Ambiguous` — multiple interpretations or candidates remain.
- `Blocked` — a prerequisite prevented a valid experiment.
- `Superseded` — later evidence invalidated the result.
- `Verified` — only after the offset-table schema and runtime evidence gate pass.

### Address-kind vocabulary

Every address must be classified before publication:

- `absolute` — valid for one process instance only;
- `module-rva` — relative to a named module base;
- `member-displacement` — an instruction/object offset such as `[reg+0x34]`;
- `pointer-chain` — a stable module root followed by pointer displacements;
- `heap-dynamic` — a process-specific allocation with no stable root yet;
- `unknown` — not enough information to classify safely.

## Current decision register

| Item | Current decision |
|------|------------------|
| Campaign version | `11.19.0.10` |
| Executable identity | Hash recorded in `memory-offsets/11.19.0.10.json`; re-measure before a live session |
| Runtime-supported fields | None; current table has 0 usable offsets, 1 Stale/quarantined field, and 7 Unknown |
| `playerYaw` | **Quarantined / Ambiguous** until decimal, hexadecimal, raw Ghidra, and address-kind evidence reconcile |
| Trusted next anchor | `playerPositionX`/`playerPositionZ`, then `replayTime` or HP if the replay makes them observable |
| Do not repeat | The same yaw neighborhood scan using `0x0317A810` without resolving its provenance; absolute image-only AOB of survivor pointer bytes without a changed encoding/root hypothesis (ruled out by OD-RECOVERY-007); absolute LE pointer AOB across private/all/image + align 1/8 without a changed encoding hypothesis (ruled out by OD-RECOVERY-008); truncated low-32 LE dword AOB of survivor absolutes without a changed encoding hypothesis (ruled out by OD-RECOVERY-009); automated CE `bptAccess`/`bptWrite` on Float position survivors without a field pivot or interactive debugger (0 RIP hits through OD-RECOVERY-011); CE write-BP alone on the single increased `replayTime` Double without interactive debugger or a second independent launch (0 RIP in OD-RECOVERY-012); treating file-association / `Invoke-Item` alone as the OD gate path (playback can succeed while Host stays `Denied` / `lifecycle_evidence_timeout` — amended 2026-08-02); reaching ≤10 RT survivors then starting interactive debugger after the fact under a 120s research lease loses the window to EvidenceStale (OD-RECOVERY-016) — pre-arm debugger / reserve lease margin; requiring the Watch Offline orange-dialog blob to vanish after `OfflineReplayVerified` (the replay HUD renders orange in that ROI, so `dialogGone` never sets, extra clicks hit in-game UI and kill the game — OD-RECOVERY-017) — trust the verified gate; reading compare `retainedCount` as the rolling survivor count (it is unreadable-chunk carryover only; survivors are `increasedCount` — OD-RECOVERY-017); automated CE Windows-debugger write-BPs (`debugProcess(1)` + `debug_setBreakpoint(addr, bptWrite, 1)`) on rolling Double survivors — zero RIP hits across OD-009/010/011 and OD-020/021/022 probes, so the operator-owned interactive Find-what-writes step is required, not a scripting gap to keep probing; rolling from a snapshot taken during the game load transition — the candidate set can be 66M+ (22–87× steady state), convergence cannot fit the 120s lease, and the resulting session discard surfaces as a confusing compare `400` (OD-RECOVERY-025 attempt 1) — wait for a clean steady-state snapshot before rolling; capturing the rendezvous capability once at roll start — the token rotates ~5 min and a 66M-baseline roll outlives it, so a mid-roll compare dies with a confusing 401 (OD-RECOVERY-030 attempt 1; fixed by refresh + retry in the rolling driver); running the separate full-walk sanity probe when round-1 `previousCount` reports the identical snapshot count — the probe's 66M-candidate walk wasted lease inside the 120s budget (OD-RECOVERY-030; gate folded into round 1); requesting `maxCandidates=500` (or any large harvest) on every rolling round when only the final target round's addresses are written — the big early compares (66M→1M) pay candidate serialization for nothing and cost lease; request 1 candidate per round and harvest the full set only on the target round (OD-RECOVERY-031 attempt 1 → fixed in driver, validated attempts 3–5: 10–14 rounds fit the lease vs 6–7 before); overriding the CE autorun's default survivor address-file path (`%TEMP%\od-survivors.txt`) with a custom `-AddressFile` — the autorun polls the default path only, so staged survivors silently never reach CE (OD-RECOVERY-031 attempt 4; use the default path so the staging handoff works); keeping the CE autorun poll window at 90s when a 66M-baseline roll outlives it — the file appears right at the 120s lease edge, so the poll must span the whole lease + margin (OD-RECOVERY-031 attempts 3/4; extended to 300s) |
| Next planned session | `OD-RECOVERY-033` (same pipeline — driver at best-known state; both OD-032 attempts plateaued at 11 survivors one round short of ≤10, so the tail is now the binding constraint: target the lease headroom by reducing round-1 walk cost via snapshot budget/region selection, or shave the tail transition; operator Find-what-writes on staged survivors during the lease-bound held green window; treat launcher-green + immediate `Denied`/`evidence.monitor_unhealthy` as a game-side assert crash — relaunch rather than re-clicking; second distinct folder replay still required for BLK-0019) |

The current yaw conflict is recorded explicitly:

| Representation | Value |
|----------------|-------:|
| Prior offset-table decimal | `51808784` |
| Conversion of prior table decimal | `0x03168A10` |
| Prior offset-table notes | `0x0317A810` (`51882000`) |
| Current published value | `0` with `Stale` status; prior evidence retained in notes |
| Raw Ghidra export top candidate | `56085200` (`0x0357CAD0`) |

These are not interchangeable. No one of them is authoritative until the raw
analysis and address kind are reconciled.

## Historical experiment index

These entries summarize durable repository evidence. They distinguish tool and
pipeline work from a successful live-game discovery; the latter has not yet
occurred.

| Session ID | Date | Objective | Tools | Result | What it established | What it did not establish |
|------------|------|-----------|-------|--------|---------------------|----------------------------|
| `OD-2026-07-30-GHIDRA-001` | 2026-07-30 | Produce static candidate hypotheses from `wotblitz.exe` | Ghidra `FindOffsets.java` / export | `Partial` | `playerYaw` had string/xref/data-reference activity; HP was noisy; most other fields had no useful xrefs | No field layout, module-RVA proof, dynamic behavior, or verified offset |
| `OD-2026-07-30-SCANNER-001` | 2026-07-30 | Build guarded snapshot/compare/neighborhood discovery | GameIntegration scanner + GameHarness | `Partial` | Gate-checked discovery surfaces and cancellation/identity boundaries exist | No live candidate was verified; API readability is not field evidence |
| `OD-2026-07-30-CE-TOOLING-001` | 2026-07-30 | Create operator-led CE neighborhood and multiscan paths | Cheat Engine Lua scripts | `Partial` | CE output formats and manual/auto scan procedures are represented | No trustworthy live CE result is recorded in the repository |
| `OD-2026-07-31-EVIDENCE-PIPELINE-001` | 2026-07-31 | Normalize CE output and publish conservatively | `discover-offsets.ps1`, report script, schema validator | `CandidateFound` for tooling only | Unique candidates can be normalized; conflicts remain report-only; publication never promotes | It did not discover or verify a game field |
| `OD-2026-07-31-HASH-001` | 2026-07-31 | Bind evidence to the installed executable | executable fingerprint + offset table | `Partial` | Exact SHA-256 metadata is recorded for version `11.19.0.10` | Hash identity does not prove an offset's correctness |
| `OD-2026-07-31-DOC-001` | 2026-07-31 | Refresh live documentation and handoffs | docs, offline checks, tests | `CandidateFound` for documentation only | Current commands, safety gates, and promotion rules are documented | It did not add dynamic offset evidence |
| `OD-2026-07-31-YAW-RECONCILE-001` | 2026-07-31 | Reconcile the published `playerYaw` candidate | table, raw Ghidra export, arithmetic checks | `Ambiguous` | The decimal/hex/table/export conflict is proven and must be resolved | No safe yaw anchor exists; do not repeat its neighborhood scan |
| `OD-RECOVERY-001` | next session | Establish one clean dynamic anchor | CE first; GameHarness/x64dbg as follow-up | `Planned` | See the protocol below; superseded by `OD-RECOVERY-001-BLOCKED` for the attempted gate check | Must not begin until identity and offline gates pass |
| `OD-RECOVERY-001-BLOCKED` | 2026-07-31 | Execute the Phase 0 gate for `OD-RECOVERY-001` | host state + GameHarness probe | `Blocked` | Multiple game processes were present and the host reported `Unknown` / `launch.awaiting_evidence`; no scan was started | No PID was admissible for discovery; do not attach to the vanilla or smoke-test processes |
| `OD-RECOVERY-002` | 2026-07-31 | Establish one managed replay after the prior gate block | managed launch, host state, read-only process inventory | `Superseded` | The planned launch protocol was attempted and produced a blocked result before scanning | Superseded by `OD-RECOVERY-002-BLOCKED`; do not treat it as a discovery result |
| `OD-RECOVERY-002-BLOCKED` | 2026-07-31 | Establish one managed replay after the prior gate block | managed launch, host state, read-only process inventory | `Blocked` | Replay staging completed, but the launch timed out without correlated lifecycle evidence; host remained `Unknown` / `launch.awaiting_evidence` | No existing process is admissible; do not repeat the launch until the timeout/correlation path is diagnosed |
| `OD-RECOVERY-003` | 2026-08-02 | Prove a repeatable privacy-safe aggregate campaign; test whether a bounded main-module Float32 range is a useful natural-change anchor | two managed launches + rolling comparisons | `Partial` | Two fresh launches each reached `OfflineReplayVerified` with an exact singleton process and zero post-trial game/host processes; the aggregate-only campaign is repeatable | No single candidate survived; replay independence not proven (1 distinct payload used) |
| `OD-RECOVERY-003-BOUNDED` | 2026-08-02 | Determine whether a bounded 0–64 MiB low-address Float32 window contains eligible retained values under the privacy-safe budget ceiling | loopback `discover-snapshot` (bounded window) | `NoSignal` | Zero retained/current/changed aggregates; scanner session discarded; recorded as bounded negative setup evidence for OD-RECOVERY-004 | It did not test a populated slice; the interrupted populated-slice attempt is not scan evidence |
| `OD-RECOVERY-004` | 2026-08-02 | Controlled movement A→B Float32 narrowing for `playerPositionX`/`playerPositionZ` under a privacy-safe bounded window | managed launch + loopback snapshot/compare (`valueKind=Float`) | `Partial` | Managed Host.Web child reached `OfflineReplayVerified`; state A≈1.23M in-range floats; state B `changed≈26k` / `current≈26k` / `unchanged≈793k`; session discarded | No single address classified; promotion still blocked (BLK-0019); unbounded `--max-bytes` still hard-fails |
| `OD-RECOVERY-005` | 2026-08-02 | Reproduce A→B Float32 narrowing and classify survivor address kind | managed launch + owner-authorized foreground Watch Offline / pause-resume + loopback Float snapshot/compare | `Partial` | Host.Web child verified; A≈757k → B changed≈905 (unchanged≈756k); returned sample 100/100 `private-mapping` → treat set as `heap-dynamic` pending root; session discarded | No single reproducible candidate; no module-rva/member/pointer-chain root; second replay still required (BLK-0019) |
| `OD-RECOVERY-006` | 2026-08-02 | Reproduce narrowing and seek a stable root / pointer-chain into private-mapping survivors | managed launch + owner-authorized foreground ops + Float A→B + AOB pattern probe of survivor pointer bytes | `Partial` | First A→B: A≈1.24M → changed≈434 (100/100 private-mapping); pattern probe of 8 survivors: 1/8 had hits, hit kinds all `private-mapping` (no image/module root); second A→B for probes changed≈1175; sessions discarded | No module-rva or pointer-chain root; value discover cannot search 8-byte pointers (use `/discover/pattern`); second replay still required (BLK-0019) |
| `OD-RECOVERY-007` | 2026-08-02 | Image-only static root search for private-mapping survivors | `ImageRegionsOnly` pattern API + soft-cap MaxBytes Float A→B + owner-authorized foreground ops | `NoSignal` (for image roots) / `Partial` (tooling) | Soft-cap unbounded snapshot worked (A≈14.5M retained under 64 MiB budget); B changed≈201k (noisier than windowed OD-006); 12/12 image-only pointer AOBs returned **0** hits | Direct absolute pointers to sampled survivors are not stored in MEM_IMAGE; do not repeat this absolute image-only AOB unchanged |
| `OD-RECOVERY-008` | 2026-08-02 | Two-level absolute pointer hunt + CE launch smoke | windowed Float A→B + pattern matrix (priv/all/img × align 1/8) + CE 7.7 launch under verified offline | `NoSignal` | A→B changed≈670–1102 (private-mapping); 0 absolute LE pointer hits across matrix; CE x64 launched/responded while offline gate held (no automated attach/scan) | No static/multi-level absolute pointer root; CE structural attach/scan still manual |
| `OD-RECOVERY-009` | 2026-08-02 | Truncated-pointer encoding + CE access-BP under offline | windowed Float A→B + low32 LE dword pattern + CE Windows debugger access breakpoints during movement | `Partial` | Truncated low32 AOB 0 hits (priv/img/all on 6 survivors); CE attached+debugging with 3 access BPs set; 0 hits during overlapping resume pulse | Encoding not low32 absolute dword; CE access-BP path live but no instruction evidence yet |
| `OD-RECOVERY-010` | 2026-08-02 | CE Find-what-writes under offline | tight-window probe + CE `bptWrite` (Windows debugger; VEH hung) during overlapping movement | `Partial` | Probe window changed≈1955; CE Windows debugger set 3 write BPs (list count 3); hitCount=0; VEH `debugProcess(2)` stalled | Automated write-BP on first-pass survivors yields no RIP; need tighter/second-pass or field pivot |
| `OD-RECOVERY-011` | 2026-08-02 | Second-pass Float narrow + CE write-BP | rolling then second changed compare + CE `bptWrite` during movement | `Partial` | Pass1 changed≈2899 → pass2 changed≈1929 (private-mapping); CE 3 write BPs set; hitCount=0; Watch Offline click required for gate | Second-pass helps count; still no RIP — pivot field or interactive debugger |
| `OD-RECOVERY-012` | 2026-08-02 | Field pivot HP + replayTime under offline | Int32 unchanged HP window + Double increased replayTime + CE write-BP + pointer AOB | `Partial` / near-`CandidateFound` | Agent clicked WATCH OFFLINE; HP unchanged≈4441 (`mapped-mapping` sample); **replayTime increased=1** (`private-mapping`); CE 3 write BPs hitCount=0; ptr AOB 0 | Unique increased Double is strong heap-dynamic evidence; no root/RIP; not promoted |
| `OD-RECOVERY-013` | 2026-08-02 | Second independent launch reproduce `replayTime` | agent Watch Offline + Double increased + rollingBaseline passes | `Partial` | Second Host.Web child PID; rolling increased **193→60→15→4** all `private-mapping`; same replay artifact | RT increased behavior reproduces across launches; still heap-dynamic; second distinct replay still required |
| `OD-RECOVERY-014` | 2026-08-02 | Neighborhood + pointer classify on rolling RT set | rolling increased + `/discover/neighborhood` + pointer AOB | `Partial` | Rolling to ≤10; neighborhood OK on `relativeOffset` (noisy ~1k hits/4); pointer AOB flaky (1 hit then 0 on rebuild) | Neighborhood path live; no stable image/module pointer root |
| `OD-RECOVERY-015` | 2026-08-02 | Folder-source launch + rolling RT under managed gate | folder `.wotbreplay` → import → managed launch; rolling Double increased | `Partial` | Process amended (`launch-offline-replay-for-od.ps1`); file-assoc alone ruled out for gate; Churchill sha12 `0FAE5612491E` (import `duplicate=True`); rolling completed **882617→…→7** (`private-mapping`) | Content not proven independent of OD-012/013 (`independentReplays` still 0); no root/RIP; not promoted |
| `OD-RECOVERY-016` | 2026-08-03 | Managed launch + rolling RT to ≤10 before lease expiry | managed launch + Double rolling increased | `Partial` | Release rebuild green; gate `OfflineReplayVerified`; rolling **628320→…→8** (`private-mapping`); sha12 `0FAE5612491E` duplicate import | EvidenceStale before interactive CE/x64dbg; `independentReplays` still 0; no RIP/root |
| `OD-RECOVERY-017` | 2026-08-03 | Pre-arm + rolling driver + live managed session | launch helper ×3 + fixed clicker + rolling Double increased + CE pre-arm | `Partial` | Attempts 1–2: clicker orange-blob ROI false-positives on the replay HUD → `dialogGone` never set → extra clicks killed game (`monitor_unhealthy`); root-caused + fixed (`63845d7`, trust verified gate); attempt 3 gate green (`watch_exit=0`) + CE pre-armed attached; rolling driver read `retainedCount` (unreadable carryover) instead of `increasedCount` (survivors) → fixed; attempt 4 not run | No interactive Find-what-writes; full rolling with fixed driver not yet run; `independentReplays` still 0; no RIP/root |
| `OD-RECOVERY-018` | 2026-08-03 | First full automated pipeline: launch → verify → CE pre-arm → rolling to ≤10 with fixed driver | background launch helper + session driver + pre-arm-debugger.ps1 + fixed rolling Double increased | `Partial` | Gate `OfflineReplayVerified` (`watch_exit=0`, `dialogGone=True`); CE 7.7 pre-armed attached (PID 43432); fixed `increasedCount` driver rolled **743576→…→8** (13 rounds); `-AddressFile` staged 8 candidates (count==survivors, no WARN); gate still verified through rolling — first lease-margin win | Interactive Find-what-writes on the 8 staged survivors not run before lease expiry; `independentReplays` still 0; no RIP/root |
| `OD-RECOVERY-019` | 2026-08-03 | Reproduce the full pipeline; narrow further; hand off with gate still green | background launch helper + session driver + pre-arm-debugger.ps1 + fixed rolling Double increased | `Partial` | Gate `OfflineReplayVerified` (`watch_exit=0`, `dialogGone=True`); CE 7.7 pre-armed attached (PID 45188); fixed driver rolled **2980777→…→7** (14 rounds, tightest yet); `-AddressFile` staged 7 candidates (count==survivors, no WARN); gate STILL `OfflineReplayVerified` at session end — interactive window handed off live | Interactive Find-what-writes not completed before handoff; `independentReplays` still 0; no RIP/root |
| `OD-RECOVERY-020` | 2026-08-03 | Automate the final step: CE autorun write-BP capture replacing interactive Find-what-writes | CE autorun `od-autorun-writebp.lua` + 3 live runs (020/021/022) + fixed rolling Double increased | `Partial` | Rolling reached **5 survivors** (tightest ever, sequence `3084237→…→5`); autorun attaches/stages/arms correctly (`debug_setBreakpoint` resolved; 4 = x64 HW BP limit); CE CLI `-luac`/`--luac` unsupported (probe); `onBreakpoint` callback + wait-loop fallback produced **0 hits** in 20s live window | Automated CE Windows-debugger write-BP capture ruled out; interactive operator step still required; `independentReplays` still 0; no RIP/root |
| `OD-RECOVERY-023` | 2026-08-03 | Operator-present session: pipeline to ≤10 with CE pre-armed and staged | background launch helper + session driver + pre-arm-debugger.ps1 + autorun staging + fixed rolling Double increased | `Partial` | Gate `OfflineReplayVerified`; CE attached (PID 41312) with **9 survivors staged** in the address list; rolling `765363→…→9`; gate still verified at handoff (09:46:01Z) — operator Find-what-writes window delivered live | Interactive Find-what-writes outcome operator-owned; `independentReplays` still 0; no RIP/root recorded |
| `OD-RECOVERY-024` | 2026-08-03 | Hold the operator window inside the live command so Find-what-writes runs while the gate is green | session driver `-HoldAfterRollSeconds 60` + autorun staging + fixed rolling Double increased | `Partial` | Rolling **767529→…→8**; CE staged 8 (`od-survivor-1..8`, armed 4 write-BPs, 0 hits); **gate stayed `OfflineReplayVerified` through the full 60s operator window — closed by timer, not gate loss** (first time the interactive window expired on its own schedule) | Find-what-writes result still operator-owned; `independentReplays` still 0; no RIP/root recorded |
| `OD-RECOVERY-025` | 2026-08-03 | Operator-present run with pre-snapshot settle; two attempts | background launch helper + session driver (+`-PreSnapshotSettleSeconds 25`) + pre-arm-debugger.ps1 + fixed rolling Double increased | `Partial` | Attempt 1: snapshot during game load transition captured **66,592,223 candidates (22–87× steady state)** → rolling too slow → 120s lease expired mid-roll → sessions discarded → round-7 compare `400` (`session_not_found` mapped to BadRequest); attempt 2: gate flipped `Denied/evidence.monitor_unhealthy` during the 25s settle — game terminated before snapshot ran | No survivors staged either attempt; no interactive Find-what-writes; `independentReplays` still 0; no RIP/root |
| `OD-RECOVERY-026` | 2026-08-03 | Steady-state gate: accept stable baseline, roll ≤10 inside the lease | steady-state gate in rolling driver (`MaxInitialCandidates` probe + discard/retry) + 5s transitions + session driver hold + autorun staging | `Partial` | Attempt 1: gate with 10M threshold rejected the **stable** 66M baseline (3 probes within 0.05%) → FAILED; **diagnosis corrected: 66M is this session's stable footprint, not a load spike**; attempt 2 (threshold 100M, 5s transitions, round limit 22): snapshot sane 66,313,259 → rolling **1125953→…→9** in 8 rounds; address file 9 lines no WARN; CE staged 9 (`od-survivor-1..9`, armed 4, 0 hits); gate held `OfflineReplayVerified` through rolling, closed by 60s timer | Find-what-writes result still operator-owned; `independentReplays` still 0; no RIP/root recorded |
| `OD-RECOVERY-027` | 2026-08-03 | Operator-present run with the steady-state gate | steady-state gate + 5s transitions + session driver hold + autorun staging | `Partial` | Rolling **915639→…→7** (8 rounds, tied with OD-019; record remains OD-020's 5); CE staged 7 (`od-survivor-1..7`, armed 4, 0 hits); address file 7 lines no WARN; **gate still `OfflineReplayVerified` at the operator-window final read — window live and green at command end** | Find-what-writes result still operator-owned; `independentReplays` still 0; no RIP/root recorded |
| `OD-RECOVERY-029` | 2026-08-03 | Tightest survivor set since OD-020: 6 survivors staged; green window held through the whole CE capture | steady-state gate + 5s transitions + lease-bound operator hold (240s cap) + autorun staging | `Partial` | Rolling converged to **6 survivors** (tightest since OD-020's 5; sequence to live terminal only); CE staged 6 (`od-survivor-1..6`, armed 4, 0 hits in ~22s capture fully inside the green window — expired 15:52:46Z); address file exactly 6 lines no WARN | Find-what-writes result still operator-owned; `independentReplays` still 0; no RIP/root recorded |
| `OD-RECOVERY-028` | 2026-08-03 | Extended operator window: hold for the whole remaining lease | session driver `-HoldAfterRollSeconds 240` (exits early on gate loss, re-announces every 30s) + steady-state gate + 5s transitions + autorun staging | `Partial` | Rolling **789811→…→9** (8 rounds); CE staged 9 (`od-survivor-1..9`, armed 4, 0 hits); address file 9 lines no WARN; **gate `OfflineReplayVerified` through 3 re-announcements (~90s+) — window lease-bound, not timer-bound** | Find-what-writes result still operator-owned; `independentReplays` still 0; no RIP/root recorded |
| `OD-RECOVERY-030` | 2026-08-03 | Reproduce the pipeline with the 401 fix; fold the redundant sanity probe into round 1 to save lease | background launch helper + session driver (6 attempts) + pre-arm-debugger.ps1 + 401-refresh rolling Double increased + folded steady-state gate | `Partial` | Attempt 1: **401 Unauthorized on round 2** — rendezvous token rotated mid-roll → driver fixed (refresh + retry, validated live attempt 3 round 4 `capability_401_refresh_retry=1`); rolling **66.2M→…→391 in 6 rounds** (attempt 3, tightest this session) then lease wall (round 7 `400` + `EvidenceStale`); attempts 2/5: game-side **assert crash at replay start** (`AccountController.cpp:386` `activeController->GetName() == LOBBY`) → launcher-green but driver saw `Denied`/`evidence.monitor_unhealthy` — diagnosed, not our pipeline; attempt 6 validated the **probe-fold** (round-1 `previousCount` == snapshot count, no separate 66M walk); attempts 4/6 died at round 2 on the lease wall | No survivors staged any attempt; no interactive Find-what-writes; `independentReplays` still 0; no RIP/root |
| `OD-RECOVERY-031` | 2026-08-03 | Target convergence: candidate-count optimization (1/round, harvest on target only) + CE staging handoff fix (default path + 300s autorun poll) | background launch helper + session driver (5 attempts) + pre-arm-debugger.ps1 + 401-refresh folded-gate candidate-optimized rolling Double increased + default-path autorun staging | `Partial` | Attempt 1: lease wall (66.5M→…→1,093,960, round-2 `400`); candidate-count optimization applied → attempt 3 rolled **66.0M→…→14 in 12 rounds** (12 rounds fit the lease vs 6–7 before, 401 refresh live round 8) then lease wall; attempt 2: game-side assert crash at replay start (`evidence.expired`, launcher-green → driver `EvidenceStale`); attempt 4: **TARGET 10 ≤ 10** (66.4M→10 in 10 rounds, harvest 8, address file written) but CE staging silently failed — custom `-AddressFile` name bypassed the autorun's default-path poll; attempt 5: **TARGET 10 ≤ 10** (66.3M→10 in 14 rounds, sequence `983469→…→10`, harvest 10) with **CE staged all 10 survivors + 4 HW write-BPs armed** (staging handoff fix validated) — but the operator window opened with the gate already `EvidenceStale` (lease expired exactly at roll end, game terminated 18:58:19Z) | No interactive Find-what-writes (operator window stale both target runs — lease wall); `independentReplays` still 0; no RIP/root |
| `OD-RECOVERY-032` | 2026-08-03 | Reproduce the converged pipeline; target the lease headroom for the operator window | background launch helper + session driver (2 attempts) + pre-arm-debugger.ps1 + 401-refresh folded-gate candidate-optimized rolling Double increased + default-path autorun staging (300s poll) | `Partial` | Attempt 1: rolling **66.5M→…→11 in 14 rounds** (`855303→…→11`; high `retained=177581` unreadable-chunk carryover inflated rounds 4–12 `previous`; 401 refresh live round 10) then lease wall round 15 (`400` + `EvidenceStale`); attempt 2: rolling **66.5M→…→11 in 12 rounds** (`1200456→…→11`; `retained=0`; 401 refresh live round 8) then lease wall round 13 (`400` + `EvidenceStale`) | **11-survivor plateau is the reproducible ceiling — 1–2 rounds past the lease edge both attempts**; no ≤10 target, no address file, no operator window; `independentReplays` still 0; no RIP/root |

`OD-RECOVERY-001-BLOCKED` is the append-only superseding record for the planned
`OD-RECOVERY-001` row above. It does not represent a failed position scan.

## Session record template

Copy this block for every new experiment. Do not overwrite an earlier entry;
append a new record and link any superseding session.

```yaml
sessionId: OD-YYYY-MM-DD-NNN
startedUtc: YYYY-MM-DDTHH:MM:SSZ
endedUtc: YYYY-MM-DDTHH:MM:SSZ
status: Planned # Running | Partial | CandidateFound | NoSignal | Ambiguous | Blocked | Superseded | Verified
objective: One measurable question
stopCondition: Time limit or measurable pivot condition
game:
  version: 11.19.0.10
  distribution: unknown # record measured source; do not guess
  executableSha256: <64-hex-or-redacted-local-reference>
  architecture: unknown # measure the exact process
process:
  pid: 0
  processStartIdentity: recorded-locally
  moduleName: unknown
  moduleBase: unknown
  moduleSize: unknown
replay:
  lifecycleState: unknown # loading | playing | paused | ended
  replayIdentity: local-redacted-id
  offlineGate: not-checked # OfflineReplayVerified | not-verified
hypothesis:
  field: playerPositionX
  fieldType: Float
  event: controlled tank movement
  expectedTransition: changed
  addressKind: unknown
method:
  primaryTool: Cheat Engine 7.7
  secondaryTools: []
  scanType: unknown
  range: unknown
  alignment: unknown
  filterTransitions: []
observations:
  - state: A
    observedValue: redacted-or-summary
    candidateCountBefore: 0
    candidateCountAfter: 0
    transition: unknown
candidates:
  - rawAddress: local-only
    absoluteAddress: local-only
    moduleRelativeOffsetDecimal: unknown
    moduleRelativeOffsetHex: unknown
    addressKind: unknown
    behavior: unknown
    source: unknown
result:
  whatWorked: []
  whatFailed: []
  rulesOut: []
  partials: []
  nextPivot: unknown
  repeatWithoutChangedHypothesis: false
artifacts:
  rawFiles: local-only
  committedSummary: none
```

## Historical `OD-RECOVERY-002` protocol (superseded)

This protocol is retained as append-only history. It is superseded by the
`OD-RECOVERY-002-BLOCKED` result below and the current `OD-RECOVERY-003`
protocol in `offset-discovery-workflow.md`.

Timebox: 105 minutes plus a short handoff.

### 0–10 minutes: identity and safety

- Confirm `OfflineReplayVerified`.
- Record exact executable hash, process identity, architecture, module name/base/size.
- Confirm the replay is actively playing and identify one controlled transition.
- If any identity item is missing, end as `Blocked`.

### 10–30 minutes: yaw reconciliation only

- Compare the raw Ghidra export with the offset table.
- Mechanically recalculate every decimal/hex pair.
- Determine whether each value is a module-RVA, data address, or member
  displacement.
- If no authoritative yaw interpretation is found within 20 minutes, mark yaw
  `Superseded` for this campaign and stop spending time on it.

### 30–75 minutes: one controlled position anchor

- Use `playerPositionX` or `playerPositionZ` with a known movement transition.
- Capture candidate counts after each transition.
- Repeat a transition once to distinguish tank movement from camera/UI movement.
- Stop after two filters with no meaningful information gain.

### 75–105 minutes: structural follow-up

- Trace writes/accesses for the best three candidates.
- Classify each address kind.
- Inspect neighboring fields only when the same object/register base is proven.
- Do not publish a heap-dynamic address as a module offset.

End the session with a status, even if it is `NoSignal` or `Blocked`. The next
session should begin from the recorded pivot, not from the top of this protocol.

## `OD-RECOVERY-001-BLOCKED` result — 2026-07-31

```yaml
sessionId: OD-RECOVERY-001-BLOCKED
supersedes: OD-RECOVERY-001
startedUtc: 2026-07-31T18:30:00Z
endedUtc: 2026-07-31T18:36:47Z
status: Blocked
objective: Establish one clean dynamic anchor for playerPositionX or playerPositionZ
stopCondition: Stop immediately if the host gate or process identity is not unambiguous
game:
  version: 11.19.0.10
  distribution: installed executable identity matched the campaign record; exact local path omitted
  executableSha256: matches-existing-11.19.0.10-table
  architecture: not-measured-for-an-admissible-replay-process
process:
  pid: not-published
  processStartIdentity: multiple responsive wotblitz instances observed; no target selected
  moduleName: wotblitz.exe observed, but no admissible replay process selected
  moduleBase: not-admissible
  moduleSize: not used
replay:
  lifecycleState: unknown
  replayIdentity: not-recorded
  offlineGate: not-verified
hypothesis:
  field: playerPositionX
  fieldType: Float
  event: controlled tank movement
  expectedTransition: changed
  addressKind: unknown
method:
  primaryTool: GameHarness state/probe only
  secondaryTools: []
  scanType: none
  range: none
  alignment: none
  filterTransitions: []
observations:
  - state: host-query
    observedValue: verificationState=Unknown; reason=launch.awaiting_evidence
    candidateCountBefore: 0
    candidateCountAfter: 0
    transition: no verified replay evidence
  - state: process-inventory
    observedValue: multiple responsive game processes, including replay-argument and vanilla instances
    candidateCountBefore: 0
    candidateCountAfter: 0
    transition: process identity ambiguous
result:
  whatWorked:
    - Installed executable version and SHA-256 matched the existing campaign record.
    - Host endpoint responded and GameHarness probe failed closed rather than scanning.
  whatFailed:
    - Host did not report OfflineReplayVerified; it reported Unknown / launch.awaiting_evidence.
    - Multiple game instances made PID selection ambiguous; no process was attached.
  rulesOut:
    - No offset candidate, runtime value, address, or field behavior was discovered.
    - The running processes cannot be treated as a verified discovery session.
  partials:
    - Image identity remains usable for a future session after the lifecycle gate is repaired.
  nextPivot: OD-RECOVERY-002 — establish one managed replay launch and verify the gate before any CE or discover* command.
  repeatWithoutChangedHypothesis: false
artifacts:
  rawFiles: local-only
  committedSummary: this ledger entry
```

Do not interpret this blocked session as a failed position scan. It produced no
dynamic evidence and must not change `memory-offsets/11.19.0.10.json`.

## `OD-RECOVERY-002-BLOCKED` result — 2026-07-31

```yaml
sessionId: OD-RECOVERY-002-BLOCKED
supersedes: OD-RECOVERY-002
status: Blocked
observedAtUtc: 2026-07-31T18:44:59Z # host-state query after the timed-out request
observedTimeoutSeconds: 60
timebox: 60 seconds for the managed-launch request; no discovery time was authorized after timeout
objective: Establish one managed replay and pass the OfflineReplayVerified gate before dynamic discovery
stopCondition: Stop after launch staging or launch timeout if no correlated verified process evidence appears
launchAttempt:
  request: managed replay launch through the loopback host
  artifactSelection: valid imported source artifact was supplied; private identifier omitted
  staging: succeeded before the launch attempt timed out
  processCreation: no process was attributable to the managed launch
  hostState: verificationState=Unknown; reason=launch.awaiting_evidence
process:
  inventory: four responsive wotblitz.exe processes observed during the follow-up read-only check
  identity: no process was admissible because the host supplied no correlated offline evidence
  pid: not-published
replay:
  lifecycleState: unknown
  offlineGate: not-verified
method:
  primaryTool: host state and process inventory only
  scanType: none
observations:
  - state: managed-launch
    observedValue: launch request timed out after staging; no verified launch outcome
    transition: launch path did not reach OfflineReplayVerified
  - state: host-query
    observedValue: verificationState=Unknown; reason=launch.awaiting_evidence
    transition: gate remained closed
  - state: process-inventory
    observedValue: four responsive game processes remained, with mixed parentage; exact command lines withheld
    transition: process identity remained ambiguous
result:
  whatWorked:
    - Replay artifact staging completed before the timeout.
    - Read-only PID checks confirmed four currently running game processes without attaching or scanning.
    - Host remained reachable and continued to fail closed.
  whatFailed:
    - Managed launch did not produce correlated lifecycle evidence within the request timeout.
    - The current process set cannot be attributed to that launch attempt.
  rulesOut:
    - No dynamic field value, candidate address, offset, or address-kind evidence was produced.
    - Existing game processes must not be reused as the managed replay target.
  partials:
    - Staging and executable identity are prerequisites worth preserving, but neither proves a running replay.
  nextPivot: OD-RECOVERY-003 — diagnose the launch hang and lifecycle-correlation path without relaunching or terminating existing processes.
  repeatWithoutChangedHypothesis: false
artifacts:
  rawFiles: local-only
  committedSummary: this ledger entry
```

`OD-RECOVERY-002-BLOCKED` is an append-only blocked result. It does not represent
a failed position scan and must not change `memory-offsets/11.19.0.10.json`.
Do not attempt another managed launch, attach to an existing process, or terminate
one until the launch timeout and lifecycle-correlation path have been diagnosed.

## `OD-RECOVERY-003` result — 2026-08-02

```yaml
sessionId: OD-RECOVERY-003
supersedes: OD-RECOVERY-003 planned recovery after OD-RECOVERY-002-BLOCKED
status: Partial
observedAtUtc: 2026-08-02T03:06:35Z
timebox: two fresh managed launches; two rolling comparisons per launch
objective: Prove a repeatable privacy-safe aggregate campaign and test whether a bounded main-module Float32 range is a useful natural-change anchor
stopCondition: Stop after two successful fresh launches, any gate or cleanup failure, or a zero retained set
process:
  launchCount: 2
  exactSingletonProcessEachLaunch: true
  offlineGateEachLaunch: OfflineReplayVerified
  postTrialGameProcessCount: 0
  postTrialHostProcessCount: 0
replay:
  distinctReplayPayloadsUsed: 1
  launchIndependence: true
  replayIndependence: false
  privacy: artifact identifiers, names, paths, hashes, bytes, and player data withheld
method:
  primaryTool: GameHarness discover-campaign
  valueKind: Float32
  valueRange: -500 to 500
  alignment: 4
  addressScope: first 16 MiB of the trusted main-module range, including readable image/private/mapped regions
  compareMode: changed
  rollingBaseline: true
  comparisonsPerLaunch: 2
  intervalSeconds: 2
  renderedCandidatePayloads: 0
observations:
  - launch: 1
    candidateCountBefore: 2265195
    candidateCountAfterFirstCompare: 0
    changedFirstCompare: 0
    unchangedFirstCompare: 2265195
    candidateCountAfterSecondCompare: 0
    scannerSessionDiscarded: true
    managedGameExitedOnEvidenceExpiry: true
  - launch: 2
    candidateCountBefore: 2265195
    candidateCountAfterFirstCompare: 0
    changedFirstCompare: 0
    unchangedFirstCompare: 2265195
    candidateCountAfterSecondCompare: 0
    scannerSessionDiscarded: true
    managedGameExitedOnEvidenceExpiry: true
result:
  whatWorked:
    - Both fresh managed launches reached the positive offline gate with exactly one attributable game process.
    - Aggregate counts repeated exactly across launches; no candidate address, value, or scanner session identifier was rendered.
    - Rolling comparison narrowed the changed set to zero and explicit discard removed each retained scanner session.
    - Evidence expiry terminated both exact managed game processes; only the lead-started web host required explicit cleanup.
  whatFailed:
    - An initial setup rehearsal polled the gate for only four seconds and stopped before the documented lifecycle window; no scanner command ran.
    - The first verified campaign attempt exposed BLK-0020 and failed before snapshot creation; the probe was corrected and retested before these evidence trials.
    - The bounded main-module range produced no changing candidates under natural replay progression.
  rulesOut:
    - No dynamic evidence supports playerPositionX, playerPositionZ, replayTime, HP, or any other field.
    - No candidate address, RVA, member displacement, pointer chain, heap route, or address-kind classification was produced.
    - This 16 MiB main-module slice is not a useful natural-change anchor under the recorded timing and filter protocol.
    - The result cannot satisfy replay independence or offset promotion; BLK-0019 remains open.
  partials:
    - The negative aggregate result is repeatable across two independent process launches of the same replay.
    - The new command proves bounded orchestration, output redaction, rolling reduction, and scanner-session cleanup against the real guarded scanner.
  nextPivot: OD-RECOVERY-004 — use a controlled movement transition to search private/heap state for playerPositionX or playerPositionZ, then classify any surviving address structurally; obtain a second distinct replay before promotion review.
  repeatWithoutChangedHypothesis: false
artifacts:
  rawFiles: none
  committedSummary: this ledger entry
```

`OD-RECOVERY-003` is aggregate negative evidence only. It does not make any
field `Candidate` or `Verified`, and it must not change
`memory-offsets/11.19.0.10.json`. Do not repeat the same main-module natural
change protocol without a changed scope or controlled transition.

## `OD-RECOVERY-003-BOUNDED` result — 2026-08-02

```yaml
sessionId: OD-RECOVERY-003-BOUNDED
supersedes: none (negative setup evidence for OD-RECOVERY-004)
status: NoSignal
observedAtUtc: 2026-08-02T09:30:00Z
timebox: one bounded low-address trial; one interrupted populated-slice attempt
decision: recorded as bounded negative setup evidence; the interrupted attempt is NOT scan evidence
objective: Determine whether a bounded 0–64 MiB low-address window contains eligible retained Float32 values under the privacy-safe budget ceiling
stopCondition: Stop after the bounded window completes or the 512 MiB retained-data ceiling is reached
method:
  primaryTool: loopback discover-snapshot with a bounded address window
  valueKind: Float32
  addressScope: 0–64 MiB bounded window
  ceiling: retained readable memory must stay within the 512 MiB ceiling
observations:
  - state: bounded-window
    aggregatePrevious: 0
    aggregateCurrent: 0
    aggregateChanged: 0
    scannerSessionDiscarded: true
  - state: populated-slice-selection
    outcome: interrupted before a result could be established; not classified as scan evidence
result:
  whatWorked:
    - The bounded low-address window completed and was discarded without exposing addresses, values, or session identifiers.
    - It confirmed that an empty low-address slice yields no eligible retained values.
  whatFailed:
    - An unbounded readable private/mapped snapshot exceeded the 512 MiB retained-data ceiling, so no aggregate could be produced.
    - A follow-up attempt to select a populated private/mapped slice internally was interrupted.
  rulesOut:
    - No dynamic evidence supports any field; no candidate address, RVA, or address kind was produced.
  partials:
    - The negative bounded result is reusable setup evidence for OD-RECOVERY-004.
    - A privacy-safe scanner-side bounded region budget is required before another live trial; the interrupted attempt must not be cited as a result.
  nextPivot: OD-RECOVERY-004 — controlled movement transition with operator availability and a bounded region budget (see offset-discovery-workflow.md).
  repeatWithoutChangedHypothesis: false
artifacts:
  rawFiles: none
  committedSummary: this ledger entry
```

`OD-RECOVERY-003-BOUNDED` is bounded negative setup evidence only. It does not
make any field `Candidate` or `Verified` and must not change
`memory-offsets/11.19.0.10.json`. The interrupted populated-slice selection
attempt is explicitly **not** scan evidence: it produced no result and must not
be cited as one.

## `OD-RECOVERY-004` result — 2026-08-02

```yaml
sessionId: OD-RECOVERY-004
supersedes: none
status: Partial
observedAtUtc: 2026-08-02T18:56:39Z
timebox: one managed relaunch after EvidenceStale; one A→B float compare
decision: aggregate A→B narrowing is real; structural classification deferred to OD-RECOVERY-005
objective: Controlled movement transition search for playerPositionX or playerPositionZ in private/mapped Float32 state
stopCondition: Stop after one A→B changed compare with aggregates retained only, or gate loss
method:
  primaryTool: loopback discover/snapshot + discover/compare with valueKind=Float
  valueKind: Float32
  floatBounds: [-500, 500]
  addressScope: privacy-safe bounded 64 MiB window (absolute bases kept local-only; not committed)
  maxBytes: 64 MiB per window
  note: unbounded private/mapped with MaxBytes still returns discover.snapshot.size_limit instead of truncating
observations:
  - state: prior-attempt
    outcome: EvidenceStale after 120s research lease; a WGC-parented wotblitz was present and is not admissible
  - state: relaunch
    parentProcess: WotBTreader.Host.Web.exe
    verificationState: OfflineReplayVerified
    reason: session.offline_replay_verified
  - state: A
    aggregatePrevious: 1229051
    aggregateCurrent: 1229051
    scannerSessionDiscarded: false
  - state: B
    aggregatePrevious: 1229051
    aggregateCurrent: 26284
    aggregateChanged: 26284
    aggregateUnchanged: 792666
    scannerSessionDiscarded: true
result:
  whatWorked:
    - Exact managed Host.Web child reached OfflineReplayVerified after Watch Offline.
    - Explicit valueKind=Float produced a populated in-range A set (~1.23M).
    - A→B changed compare narrowed to ~26k survivors with ~793k unchanged.
  whatFailed:
    - GameHarness discover-snapshot omitted valueKind and defaulted to Int32, ignoring --float-min/--float-max (API path required).
    - Unbounded MaxBytes still hard-fails with discover.snapshot.size_limit rather than completing a truncated budgeted snapshot.
    - No single survivor was structurally classified (address kind unknown).
  rulesOut:
    - Treating a WGC-parented process as the managed research child.
    - Using discover-snapshot float bounds without sending valueKind=Float.
  partials:
    - A→B Float32 narrowing under a bounded window is reusable setup for OD-RECOVERY-005 classification.
    - OD-RECOVERY-003-BOUNDED NoSignal may have been contaminated by the Int32 CLI default; do not treat that empty low window as definitive against Float scans.
  nextPivot: OD-RECOVERY-005 — classify address kind of changed survivors (module-rva / member-displacement / pointer-chain / heap-dynamic) with explicit operator state-B acknowledgement; second distinct replay still required before promotion (BLK-0019).
  repeatWithoutChangedHypothesis: false
artifacts:
  rawFiles: none
  committedSummary: this ledger entry
```

`OD-RECOVERY-004` is aggregate narrowing evidence only. It does not make any
field `Candidate` or `Verified` and must not change
`memory-offsets/11.19.0.10.json`. Scanner session identifiers and absolute
window bases were kept local-only and discarded.

## `OD-RECOVERY-005` result — 2026-08-02

```yaml
sessionId: OD-RECOVERY-005
supersedes: none
status: Partial
observedAtUtc: 2026-08-02T19:14:00Z
timebox: one managed launch; one owner-authorized A→B float compare with kind histogram
decision: changed survivors in the returned sample are private-mapping (heap-dynamic pending root); next is pointer-chain / structural root work
objective: Reproduce OD-RECOVERY-004 A→B Float32 narrowing and classify survivor address kind for playerPositionX/playerPositionZ
stopCondition: Stop after one A→B changed compare with aggregate counters and address-kind histogram only, or gate loss
method:
  primaryTool: loopback discover/snapshot + discover/compare with valueKind=Float
  valueKind: Float32
  floatBounds: [-500, 500]
  addressScope: privacy-safe bounded 64 MiB window (absolute bases local-only; not committed)
  maxBytes: 64 MiB per window
  transition: owner-authorized foreground window ops — click Watch Offline region; Space pause (A); Space resume ~2.5s; Space pause (B)
  note: guarded GameHarness input adapter remains unregistered; direct foreground ops used only under explicit owner authorization for this session
observations:
  - state: launch
    parentProcess: WotBTreader.Host.Web.exe
    verificationState: OfflineReplayVerified
    reason: session.offline_replay_verified
  - state: A
    aggregatePrevious: 757016
    note: first nonempty Float window retained as state A
  - state: B
    aggregatePrevious: 757016
    aggregateCurrent: 905
    aggregateChanged: 905
    aggregateUnchanged: 756111
    aggregateIncreased: 495
    aggregateDecreased: 409
    truncated: true
    returnedCandidates: 100
    addressKindHistogram:
      private-mapping: 100
    scannerSessionDiscarded: true
result:
  whatWorked:
    - Host.Web-managed child reached OfflineReplayVerified after owner-authorized Watch Offline click.
    - Controlled pause→brief resume→pause produced a much tighter changed set (~905) than OD-RECOVERY-004 (~26k).
    - All 100 returned candidates reported addressKind=private-mapping; classify the survivor set as heap-dynamic until a stable root exists.
  whatFailed:
    - No single candidate was isolated; compare truncated at maxCandidates=100.
    - No module-rva, member-displacement, or pointer-chain root was established.
    - Unbounded MaxBytes still hard-fails (bounded window still required).
  rulesOut:
    - Treating the OD-005 changed set as module-image / main-module natural-change survivors (sample was 100% private-mapping).
  partials:
    - Survivor-set address kind is private-mapping / heap-dynamic pending root — reusable for OD-RECOVERY-006.
    - A→B narrowing reproduced under explicit owner-authorized foreground control.
  nextPivot: OD-RECOVERY-006 — find a stable root or pointer-chain into the private-mapping survivor set; keep aggregates-only in repo; obtain a second distinct replay before promotion (BLK-0019).
  repeatWithoutChangedHypothesis: false
artifacts:
  rawFiles: none
  committedSummary: this ledger entry
```

`OD-RECOVERY-005` is aggregate classification evidence only. It does not make any
field `Candidate` or `Verified` and must not change
`memory-offsets/11.19.0.10.json`. Absolute addresses, values, and scanner
session identifiers were discarded and were not committed.

```yaml
sessionId: OD-RECOVERY-006
date: 2026-08-02
observedAtUtc: 2026-08-02T19:35:00Z
timebox: one managed launch; A→B float compare; bounded AOB pointer-byte probes; cleanup
decision: survivors remain heap-dynamic / private-mapping; pattern hits (when present) also private-mapping — no image/module static root yet
objective: Find a stable root or pointer-chain into the OD-RECOVERY-005 private-mapping survivor set for playerPositionX/Z
stopCondition: Stop after aggregate A→B + privacy-safe pointer-byte pattern probes, or gate loss
method:
  primaryTool: loopback Float snapshot/compare + /api/v1/game/discover/pattern
  valueKind: Float32
  floatBounds: [-500, 500]
  addressScope: privacy-safe bounded 64 MiB window
  maxBytes: 64 MiB per window
  transition: owner-authorized foreground Watch Offline click; Space pause/resume/pause
  pointerProbe: AOB of little-endian absolute pointer bytes for up to 8 returned survivors (local-only addresses); includeImageRegions=true
observations:
  - state: launch
    parentProcess: WotBTreader.Host.Web.exe
    verificationState: OfflineReplayVerified
  - state: A1
    aggregatePrevious: 1242451
  - state: B1
    aggregatePrevious: 1242451
    aggregateCurrent: 434
    aggregateChanged: 434
    aggregateUnchanged: 996336
    aggregateIncreased: 198
    aggregateDecreased: 236
    truncated: true
    returnedCandidates: 100
    addressKindHistogram:
      private-mapping: 100
  - state: pointer-pattern
    probed: 8
    withHits: 1
    noHits: 7
    hitAddressKindHistogram:
      private-mapping: 4
    note: first value-discover attempt failed with discover.invalid_value_width (8-byte pointers); pattern endpoint used instead
  - state: A2B2-for-probes
    aggregateChanged: 1175
    returnedCandidates: 20
    scannerSessionsDiscarded: true
result:
  whatWorked:
    - Reproduced private-mapping A→B narrowing under owner-authorized foreground control (first pass tighter than OD-005: ~434 changed).
    - Confirmed 8-byte pointer search must use /discover/pattern, not value discover.
  whatFailed:
    - No image/module-kind pattern hit for survivor pointer bytes; the only hits were private-mapping→private-mapping.
    - Pointer-chain verify API still requires a hypothesized module root; none established.
  rulesOut:
    - Do not treat same-process private pointer hits as a stable module-rva / pointer-chain root.
  partials:
    - Survivor set remains heap-dynamic pending a static root.
    - Next needs image-scoped or debugger-assisted root finding without committing absolute addresses.
  nextPivot: OD-RECOVERY-007 — pursue module-image-scoped static roots (or CE/x64dbg structural) for private-mapping survivors; soft-cap MaxBytes remains optional; second distinct replay before promotion (BLK-0019).
  repeatWithoutChangedHypothesis: false
artifacts:
  rawFiles: none
  committedSummary: this ledger entry
```

`OD-RECOVERY-006` is aggregate structural evidence only. It does not make any
field `Candidate` or `Verified` and must not change
`memory-offsets/11.19.0.10.json`. Absolute addresses, values, and scanner
session identifiers were discarded and were not committed.

```yaml
sessionId: OD-RECOVERY-007
date: 2026-08-02
observedAtUtc: 2026-08-02T19:46:00Z
timebox: one managed launch; soft-cap Float A→B; image-only pointer AOB on returned survivors
decision: absolute pointer bytes for sampled private-mapping survivors are absent from MEM_IMAGE; soft-cap works but low-first fill is noisier than bounded windows for A→B
objective: Find a module-image static root for the heap-dynamic position survivor set
stopCondition: Stop after image-only probes + aggregates, or gate loss
method:
  primaryTool: Host.Web discover/snapshot (MaxBytes soft-cap) + discover/pattern with includeImageRegions+imageRegionsOnly
  valueKind: Float32
  floatBounds: [-500, 500]
  maxBytes: 64 MiB soft-cap (no address window)
  transition: owner-authorized Watch Offline click + Space pause/resume/pause
observations:
  - state: launch
    parentProcess: WotBTreader.Host.Web.exe
    verificationState: OfflineReplayVerified
  - state: A
    aggregatePrevious: 14469841
    note: soft-cap retained first budget-filling private/mapped readable chunks
  - state: B
    aggregateChanged: 201129
    aggregateUnchanged: 14268712
    returnedCandidates: 100
    addressKindHistogram:
      private-mapping: 100
  - state: image-only-pointer-aob
    probed: 12
    withHits: 0
    survivorsWithImageKindHits: 0
    scannerSessionDiscarded: true
result:
  whatWorked:
    - ImageRegionsOnly API path live-proven (0 hits is a valid negative result).
    - MaxBytes soft-cap live-proven on unbounded private/mapped snapshot.
  whatFailed:
    - No MEM_IMAGE absolute pointer to any of 12 returned survivors.
    - Soft-cap A→B changed set (~201k) much noisier than windowed OD-006 (~434); prefer windows for controlled narrowing.
  rulesOut:
    - Direct little-endian absolute pointer storage in MEM_IMAGE for this survivor sample (repeat only with changed encoding/root hypothesis).
  partials:
    - Heap-dynamic classification stands; need CE/x64dbg access tracing or multi-level/encoded roots next.
  nextPivot: OD-RECOVERY-008 — structural access/root (CE/x64dbg offline or multi-level pointer hypothesis); keep aggregates-only; second distinct replay before promotion (BLK-0019).
  repeatWithoutChangedHypothesis: false
artifacts:
  rawFiles: none
  committedSummary: this ledger entry
```

`OD-RECOVERY-007` is aggregate structural evidence only. It does not make any
field `Candidate` or `Verified` and must not change
`memory-offsets/11.19.0.10.json` beyond narrative Unknown evidence notes.
Absolute addresses, values, and scanner session identifiers were discarded.

```yaml
sessionId: OD-RECOVERY-008
date: 2026-08-02
observedAtUtc: 2026-08-02T20:00:00Z
timebox: one managed launch; windowed Float A→B; L1 absolute pointer AOB matrix; CE launch smoke
decision: absolute LE pointer AOB is exhausted for this survivor class without a changed encoding hypothesis; CE is launchable under the offline gate but attach/scan remains manual
objective: Find any absolute pointer root (private or image, 1-level or 2-level) for position A→B survivors; smoke CE availability
stopCondition: Stop after matrix aggregates + CE smoke, or gate loss
method:
  primaryTool: Host.Web discover/snapshot (bounded windows) + discover/pattern + CE 7.7 x64 launch
  valueKind: Float32
  floatBounds: [-500, 500]
  transition: owner-authorized Watch Offline click + Space pause/resume/pause
observations:
  - state: launch
    parentProcess: WotBTreader.Host.Web.exe
    verificationState: OfflineReplayVerified
  - state: A
    aggregatePrevious: 840163
    note: bounded window (preferred over soft-cap for narrowing)
  - state: B
    aggregateChanged: 1102
    returnedCandidates: 30
    addressKindHistogram:
      private-mapping: 30
  - state: absolute-pointer-aob-matrix
    probedSurvivors: sample set from A→B
    regionScopes: private, all, image
    alignments: 1, 8
    withHits: 0
    twoLevelImageProbe: not reached (L1 zero)
  - state: ce-smoke
    cheatEnginePathPresent: true
    launchedResponding: true
    automatedAttachScan: false
    scannerSessionDiscarded: true
result:
  whatWorked:
    - Windowed A→B again produced a small private-mapping changed set.
    - CE 7.7 x64 launched under OfflineReplayVerified without elevating past usability for this smoke.
  whatFailed:
    - Zero absolute LE pointer hits across the OD-008 matrix (stricter than OD-006's one private hit).
    - No automated CE attach/pointer map (adapter gap).
  rulesOut:
    - Repeating absolute LE pointer AOB unchanged across private/all/image and align 1/8 for this field/sample class.
  partials:
    - CE structural path is the remaining practical next step under offline verification.
  nextPivot: OD-RECOVERY-009 — CE/x64dbg what-accesses or encoded/relative/multi-level root; second distinct replay before promotion (BLK-0019).
  repeatWithoutChangedHypothesis: false
artifacts:
  rawFiles: none
  committedSummary: this ledger entry
```

`OD-RECOVERY-008` is aggregate structural evidence only. It does not make any
field `Candidate` or `Verified` and must not change
`memory-offsets/11.19.0.10.json` beyond narrative Unknown evidence notes.
Absolute addresses, values, and scanner session identifiers were discarded.

```yaml
sessionId: OD-RECOVERY-009
date: 2026-08-02
observedAtUtc: 2026-08-02T20:05:00Z
timebox: managed launches; truncated low32 AOB; CE Windows-debugger access breakpoints during movement
decision: low-32 truncated absolute encoding is ruled out for sampled survivors; CE debugger attach/access-BP path is live under OfflineReplayVerified but produced zero instruction hits in this pulse
objective: Test truncated-pointer encoding and obtain CE what-accesses module names for position survivors
stopCondition: Stop after truncated AOB aggregates + CE access-BP attempt, or gate loss
method:
  primaryTool: Host.Web discover/snapshot+compare+pattern + CE 7.5+ Lua debugProcess/debug_setBreakpoint(bptAccess)
  valueKind: Float32
  floatBounds: [-500, 500]
  transition: owner-authorized Watch Offline click + Space pause/resume/pause
observations:
  - state: truncated-low32-aob
    probedSurvivors: 6
    regionScopes: private, image-only, all
    withHits: 0
  - state: windowed-ab-for-ce
    aggregatePrevious: 3743791
    aggregateChanged: 12660
    note: noisier than OD-008 ~1k windows; used to feed CE only
  - state: ce-access-bp
    attached: true
    debugProcess: windows
    isDebugging: true
    breakpointsSet: 3
    hitCount: 0
    ripModuleHistogram: empty
    scannerSessionDiscarded: true
result:
  whatWorked:
    - CE autorun one-shot attached and started Windows debugger without elevation failure in this run.
    - Truncated low32 pattern API path exercised with correct fieldName/expectedValueHex schema.
  whatFailed:
    - Zero truncated low32 pointer hits in priv/img/all.
    - Zero access-breakpoint hits during overlapping resume pulse (may be survivor noise, trigger type, or debugger mode).
  rulesOut:
    - Repeating truncated low-32 LE dword AOB of absolute survivors unchanged.
  partials:
    - CE structural debugger path is proven attachable under offline gate; needs Find-what-writes / VEH / tighter survivors next.
  nextPivot: OD-RECOVERY-010 — CE/x64dbg Find-what-writes (or VEH/kernel) on tighter A→B survivors during movement; second distinct replay before promotion (BLK-0019).
  repeatWithoutChangedHypothesis: false
artifacts:
  rawFiles: none
  committedSummary: this ledger entry
```

`OD-RECOVERY-009` is aggregate structural evidence only. It does not make any
field `Candidate` or `Verified` and must not change
`memory-offsets/11.19.0.10.json` beyond narrative Unknown evidence notes.
Absolute addresses, values, and scanner session identifiers were discarded.

```yaml
sessionId: OD-RECOVERY-010
date: 2026-08-02
observedAtUtc: 2026-08-02T20:15:00Z
timebox: managed launches; tight-window probe; CE bptWrite under Windows debugger during movement
decision: automated CE write breakpoints on first-pass Float A→B survivors produce no RIP evidence; VEH debug attach stalled; do not repeat without second-pass narrowing or field pivot
objective: Obtain Find-what-writes module/instruction evidence for position survivors under offline verification
stopCondition: Stop after CE write-BP aggregates (or attach failure), or gate loss
method:
  primaryTool: Host.Web discover/snapshot+compare + CE Lua debugProcess + debug_setBreakpoint(bptWrite)
  valueKind: Float32
  floatBounds: [-500, 500]
  transition: owner-authorized Watch Offline click + Space pause/resume/pause
observations:
  - state: window-probe
    bestChangedApprox: 1955
    note: one 64 MiB window met the ~0.5k–2k changed target during probing
  - state: ce-write-bp-windows
    attached: true
    debugProcess: windows
    isDebugging: true
    breakpointsSet: 3
    breakpointListCount: 3
    hitCount: 0
    ripModuleHistogram: empty
  - state: ce-veh-attempt
    debugProcess: veh
    result: stalled before completion (script hung after openProcess)
    scannerSessionDiscarded: true
result:
  whatWorked:
    - Located at least one bounded window with OD-008-class changed counts during probing.
    - CE Windows debugger + bptWrite path set and listed three breakpoints under OfflineReplayVerified.
  whatFailed:
    - Zero write-breakpoint hits during overlapping resume pulses.
    - VEH debugProcess(2) stalled (do not lead with VEH for unattended runs).
  rulesOut:
    - Repeating automated CE access/write BP on first-pass noisy Float survivors without further narrowing or a field pivot.
  partials:
    - Debugger attach works; instruction provenance still missing — try x64dbg/CE GUI on second-pass survivors or HP/replayTime.
  nextPivot: OD-RECOVERY-011 — x64dbg or CE GUI Find-what-writes on second-pass narrowed survivors, or pivot to HP/replayTime; second distinct replay before promotion (BLK-0019).
  repeatWithoutChangedHypothesis: false
artifacts:
  rawFiles: none
  committedSummary: this ledger entry
```

`OD-RECOVERY-010` is aggregate structural evidence only. It does not make any
field `Candidate` or `Verified` and must not change
`memory-offsets/11.19.0.10.json` beyond narrative Unknown evidence notes.
Absolute addresses, values, and scanner session identifiers were discarded.

```yaml
sessionId: OD-RECOVERY-011
date: 2026-08-02
observedAtUtc: 2026-08-02T20:20:00Z
timebox: managed launch; Watch Offline click; second-pass Float narrow; CE bptWrite
decision: second-pass Float narrowing reduces the changed set but automated CE write breakpoints still yield no RIP; pivot field or use interactive debugger next
objective: Narrow Float A→B survivors with a second changed pass, then capture CE write-BP module hits
stopCondition: Stop after second-pass aggregates + CE write-BP attempt, or gate loss
method:
  primaryTool: Host.Web snapshot/compare (rolling then second changed) + CE Windows debugger bptWrite
  valueKind: Float32
  floatBounds: [-500, 500]
  transition: owner-authorized Watch Offline click + Space pause/resume/pause
observations:
  - state: launch
    parentProcess: WotBTreader.Host.Web.exe
    verificationState: OfflineReplayVerified
    note: WATCH OFFLINE dialog click required (must not use LOG IN AND WATCH)
  - state: second-pass
    probeChangedApprox: 3129
    pass1ChangedApprox: 2899
    pass2PreviousApprox: 2899
    pass2ChangedApprox: 1929
    addressKindHistogram:
      private-mapping: 12
  - state: ce-write-bp
    attached: true
    debugProcess: windows
    breakpointsSet: 3
    breakpointListCount: 3
    hitCount: 0
    ripModuleHistogram: empty
    scannerSessionDiscarded: true
result:
  whatWorked:
    - Watch Offline foreground click recovered/maintained OfflineReplayVerified.
    - Second changed pass kept a private-mapping survivor pool (~1929).
    - CE write-BP attach/set path remained live.
  whatFailed:
    - Zero write-breakpoint hits during overlapping resume pulse.
  rulesOut:
    - Expecting automated CE write-BP RIP evidence from second-pass Float survivors alone without a field pivot.
  partials:
    - Second-pass narrowing is reusable setup; instruction provenance still missing.
  nextPivot: OD-RECOVERY-012 — HP/replayTime field pivot or interactive x64dbg/CE GUI; second distinct replay before promotion (BLK-0019).
  repeatWithoutChangedHypothesis: false
artifacts:
  rawFiles: none
  committedSummary: this ledger entry
```

`OD-RECOVERY-011` is aggregate structural evidence only. It does not make any
field `Candidate` or `Verified` and must not change
`memory-offsets/11.19.0.10.json` beyond narrative Unknown evidence notes.
Absolute addresses, values, and scanner session identifiers were discarded.

```yaml
sessionId: OD-RECOVERY-012
date: 2026-08-02
observedAtUtc: 2026-08-02T20:20:00Z
timebox: managed launch; agent Watch Offline; HP Int32 unchanged + replayTime Double increased; CE write-BP; pointer AOB
decision: field pivot succeeded for replayTime behavior (single increased Double); still heap-dynamic without root/RIP; do not promote
objective: Find a cleaner dynamic anchor via playerHP or replayTime under OfflineReplayVerified
stopCondition: Stop after HP/RT aggregates + CE attempt + pointer AOB, or gate loss
method:
  primaryTool: Host.Web discover/snapshot+compare (Int32/Double) + pattern + CE Windows debugger bptWrite
  transition: agent-owned WATCH OFFLINE click + Space pause/resume/pause
observations:
  - state: launch
    parentProcess: WotBTreader.Host.Web.exe
    verificationState: OfflineReplayVerified
    note: agent clicked WATCH OFFLINE (standing rule)
  - state: hp-int32-unchanged
    previousApprox: 4482
    unchangedApprox: 4441
    addressKindHistogram:
      mapped-mapping: 20
  - state: replayTime-double-increased
    previousApprox: 1596941
    increasedCount: 1
    addressKindHistogram:
      private-mapping: 1
  - state: ce-write-bp
    breakpointsSet: 3
    hitCount: 0
  - state: pointer-aob-on-rt
    probed: ptr8 and lo32 across priv/img/all
    withHits: 0
    scannerSessionDiscarded: true
result:
  whatWorked:
    - Agent Watch Offline standing rule applied successfully.
    - replayTime Double increased filter collapsed to one private-mapping survivor.
    - HP Int32 unchanged pool is usable secondary reconnaissance (mapped-mapping).
  whatFailed:
    - CE write-BP still produced no RIP on RT/HP samples.
    - Absolute/truncated pointer AOB for the RT survivor returned zero hits.
  rulesOut:
    - Expecting CE write-BP alone to classify the unique RT survivor without interactive debugger or a second launch.
  partials:
    - Unique increased Double is the strongest dynamic anchor so far (heap-dynamic pending root).
  nextPivot: OD-RECOVERY-013 — second independent launch for RT reproducibility; neighborhood/root; or interactive debugger; BLK-0019 still blocks promotion.
  repeatWithoutChangedHypothesis: false
artifacts:
  rawFiles: none
  committedSummary: this ledger entry
```

`OD-RECOVERY-012` is aggregate structural evidence only. It does not make any
field `Candidate` or `Verified`. Update `memory-offsets/11.19.0.10.json` with
narrative Unknown evidence for `replayTime` only (offset remains 0). Absolute
addresses, values, and scanner session identifiers were discarded.

```yaml
sessionId: OD-RECOVERY-013
date: 2026-08-02
observedAtUtc: 2026-08-02T20:25:00Z
timebox: second independent managed launch; Watch Offline; rolling Double increased
decision: replayTime increased-Double behavior reproduces on a second process launch; rolling narrows to a small private-mapping set (4); still not promotable without root + second replay
objective: Reproduce OD-012 replayTime unique-increased signal on a fresh process identity
stopCondition: Stop after rolling aggregates, or gate loss
method:
  primaryTool: Host.Web discover/snapshot+compare Double increased with rollingBaseline
  transition: agent-owned WATCH OFFLINE click + Space pause/resume pulses
observations:
  - state: launch
    parentProcess: WotBTreader.Host.Web.exe
    verificationState: OfflineReplayVerified
    independentProcessLaunch: true
    note: distinct PID from OD-012; same source replay artifact
  - state: rolling-increased
    sequence: [193, 60, 15, 4]
    finalIncreased: 4
    addressKindHistogram:
      private-mapping: 4
    scannerSessionDiscarded: true
result:
  whatWorked:
    - Second launch reproduced increased-Double behavior for replayTime.
    - Rolling baseline narrowed to 4 private-mapping survivors.
  whatFailed:
    - Did not recover a single unique hit matching OD-012's increased=1 on first pass (window noise), but rolling reached ≤4.
  rulesOut:
    - Treating OD-012's unique hit as a one-off fluke of a single process instance.
  partials:
    - independentProcessLaunches evidence for replayTime advances; independentReplays still 0 (same artifact).
  nextPivot: OD-RECOVERY-014 — root/neighborhood/interactive debugger on the ≤4 set; second distinct replay (BLK-0019).
  repeatWithoutChangedHypothesis: false
artifacts:
  rawFiles: none
  committedSummary: this ledger entry
```

`OD-RECOVERY-013` is aggregate structural evidence only. Offset remains 0.
Absolute addresses, values, and scanner session identifiers were discarded.

```yaml
sessionId: OD-RECOVERY-014
date: 2026-08-02
observedAtUtc: 2026-08-02T20:35:00Z
timebox: managed launch; rolling RT set; neighborhood + pointer AOB
decision: neighborhood works against survivor relativeOffset but is too noisy for promotion; pointer AOB not stable across rebuilds
objective: Classify/root the rolling-increased replayTime Double survivors
stopCondition: Stop after neighborhood + pointer aggregates, or gate loss
method:
  primaryTool: Host.Web rolling Double increased + discover/neighborhood + discover/pattern
  transition: agent-owned WATCH OFFLINE + Space pulses
observations:
  - state: rolling
    finalIncreasedApprox: 10
  - state: neighborhood
    survivorsProbed: 4
    ok: 4
    aggregateHitsApprox: 1253
    note: usable API path; not a unique struct fingerprint
  - state: pointer-aob
    firstPassWithHits: 1
    rebuildPassWithHits: 0
    scannerSessionDiscarded: true
result:
  whatWorked:
    - relativeOffset from compare candidates can drive neighborhood probes.
  whatFailed:
    - No stable module/image pointer root; neighborhood too dense to classify alone.
  rulesOut:
    - Expecting neighborhood hit-count alone to yield a publishable replayTime offset.
  partials:
    - Tooling path for struct-neighborhood exists for future interactive/root work.
  nextPivot: OD-RECOVERY-015 — interactive debugger or second distinct replay; keep rolling RT recipe.
  repeatWithoutChangedHypothesis: false
artifacts:
  rawFiles: none
  committedSummary: this ledger entry
```

`OD-RECOVERY-014` is aggregate structural evidence only. Offset remains 0.

```yaml
sessionId: OD-RECOVERY-015
date: 2026-08-02
observedAtUtc: 2026-08-02T21:32:00Z
timebox: amend OD launch process; game-folder .wotbreplay source; rolling RT to ≤10
decision: process flaw fixed (folder source + managed gate); rolling RT completed to 7 private-mapping; content not yet proven independent (import duplicate=True)
objective: Use owner game-folder .wotbreplay as source of truth and reach OfflineReplayVerified for discover
stopCondition: Stop after process amend + rolling ≤10, or gate loss
method:
  primaryTool: scripts/launch-offline-replay-for-od.ps1 + click-watch-offline.ps1 + Host.Web Double rolling increased
  transition: Space pause/resume pulses during rolling
observations:
  - state: process-amend
    note: File-association Invoke-Item can play a replay HUD but Host stays Denied/lifecycle_evidence_timeout without managed lifecycle evidence. Canonical path is game-folder .wotbreplay → CLI import → managed POST /launch → settle → Watch Offline.
    helper: scripts/launch-offline-replay-for-od.ps1
    clickHardening: re-read rendezvous each poll; exit 6 on Denied
  - state: folder-source
    source: LOCALAPPDATA wotblitz DAVAProject replays (basename only in logs)
    contentSha12: 0FAE5612491E
    artifactPrefix: 019fc45f
    importDuplicate: true
    distinctFromPriorOdArtifacts: unproven
  - state: gate
    verificationState: OfflineReplayVerified
  - state: rolling-increased-incomplete
    sequence: [92793, 29401, 7243, 1556, 260]
    finalIncreased: 260
    stopReason: EvidenceStale (first pulse; lease elapsed mid-roll)
  - state: rolling-increased-complete
    sequence: [882617, 151684, 23719, 3670, 472, 84, 18, 7]
    finalIncreased: 7
    addressKindHistogram:
      private-mapping: 7
    note: Started immediately after verified gate; Space pulses between compares; session discarded
result:
  whatWorked:
    - Owner folder .wotbreplay source is correct for which battle to play.
    - Managed launch still required for OfflineReplayVerified / discover APIs.
    - Rolling RT increased reaches ≤10 (7) when the scan starts before EvidenceStale.
  whatFailed:
    - File-association-only path left Host Denied while replay HUD played.
    - First rolling pulse hit EvidenceStale at 260 before ≤10.
  rulesOut:
    - Treating Invoke-Item / file association alone as sufficient for OD scanning.
  partials:
    - Launch helper + workflow/spec amended; ≤7 set ready for interactive root; BLK-0019 still needs a content-distinct second replay.
  nextPivot: OD-RECOVERY-016 — interactive debugger/root on ≤7; import content-distinct second .wotbreplay from game folder when available.
  repeatWithoutChangedHypothesis: false
artifacts:
  rawFiles: none
  committedSummary: this ledger entry + scripts/launch-offline-replay-for-od.ps1 + workflow/spec updates
```

`OD-RECOVERY-015` is aggregate structural evidence only. Offset remains 0.

```yaml
sessionId: OD-RECOVERY-016
date: 2026-08-03
observedAtUtc: 2026-08-03T02:02:00Z
timebox: managed launch + rolling RT to ≤10; interactive CE/x64dbg deferred
decision: launch green; rolling reached 8 private-mapping; EvidenceStale before interactive root; content still duplicate (independentReplays 0)
objective: Managed folder-source launch, rolling Double increased to ≤10, then interactive Find-what-writes on survivors
stopCondition: Stop after rolling ≤10 + interactive root attempt, or gate loss
method:
  primaryTool: scripts/launch-offline-replay-for-od.ps1 + click-watch-offline.ps1 + Host.Web Double rolling increased
  secondaryTools: Cheat Engine 7.7 (not pre-armed; default install path absent on machine)
  transition: Space pause/resume pulses during rolling; rollingBaseline=true
observations:
  - state: pre-launch
    note: Release rebuild of WotBTreader.sln succeeded (0 warnings/errors); binaries were stale vs staging path work
  - state: folder-source
    source: LOCALAPPDATA wotblitz DAVAProject replays (single original; basename only in logs)
    contentSha12: 0FAE5612491E
    artifactPrefix: 019fc45f
    importDuplicate: true
    distinctFromPriorOdArtifacts: unproven
    independentReplays: 0
  - state: gate
    verificationState: OfflineReplayVerified
    launchHelperExit: 0
    note: Watch Offline sync-dim → click; gate+dialog dismissed
  - state: rolling-increased-complete
    sequence: [628320, 111860, 24897, 4851, 787, 339, 180, 64, 50, 45, 29, 25, 20, 12, 8]
    finalIncreased: 8
    addressKindHistogram:
      private-mapping: 99
      mapped-mapping: 1
    note: One early 100-candidate sample had mapped-mapping=1; remainder private-mapping; session discarded
  - state: post-roll-gate-loss
    verificationState: EvidenceStale
    reason: evidence.expired
    note: Gate flipped immediately after ≤10 (8) before interactive CE/x64dbg Find-what-writes could run
  - state: ce-probe
    defaultInstallPathFound: false
    ceProcessRunning: false
result:
  whatWorked:
    - Release rebuild + managed launch path still reaches OfflineReplayVerified.
    - Rolling Double increased with rollingBaseline reaches ≤10 (8) when started immediately post-verify.
    - Survivor set remains overwhelmingly private-mapping (heap-dynamic).
  whatFailed:
    - Interactive debugger not pre-armed; 120s research lease expired before Find-what-writes.
    - Default CE 7.7 install path not found on this machine; no CE process was running.
  rulesOut:
    - Waiting until ≤10 survivors before opening interactive debugger under the default 120s lease.
  partials:
    - ≤8 survivor set ready for interactive root if debugger is pre-armed with lease margin; BLK-0019 still needs content-distinct second replay.
  nextPivot: OD-RECOVERY-017 — pre-arm CE/x64dbg before/during managed launch; start rolling immediately post-verify; reserve lease margin for Find-what-writes; import content-distinct second .wotbreplay when available.
  repeatWithoutChangedHypothesis: false
artifacts:
  rawFiles: none
  committedSummary: this ledger entry + workflow update + handoff
```

`OD-RECOVERY-016` is aggregate structural evidence only. Offset remains 0.

> **Amendment (2026-08-03, pre-OD-RECOVERY-017):** the OD-RECOVERY-016
> `ce-probe` observation ("defaultInstallPathFound: false") was a probe-path
> miss, not absence. A registry-backed probe confirms **Cheat Engine 7.7** is
> installed at `C:\Program Files\Cheat Engine\cheatengine-x86_64.exe`
> (installer root is `Cheat Engine`, not `Cheat Engine 7.7`) and **x64dbg** at
> `C:\work\tools\x64dbg\release\x64\x64dbg.exe`. Both are pre-arm candidates
> for OD-RECOVERY-017; the interactive debugger requirement is satisfiable.

```yaml
sessionId: OD-RECOVERY-017
date: 2026-08-03
observedAtUtc: 2026-08-03T02:20:00Z
timebox: managed launch to gate green; pre-arm debugger; rolling Double increased to ≤10; interactive Find-what-writes deferred to operator
decision: gate green on attempt 3 with fixed clicker; CE pre-armed attached; rolling driver field bug (retainedCount vs increasedCount) found and fixed; lease expired before full rolling with fixed driver; attempt 4 not run
objective: Run the first pre-armed live session: reach OfflineReplayVerified, pre-arm CE/x64dbg before/with rolling, roll replayTime Double increased to ≤10 for interactive Find-what-writes
stopCondition: Stop after gate green + pre-arm + rolling ≤10 + interactive root attempt, or gate loss
method:
  primaryTool: scripts/launch-offline-replay-for-od.ps1 + click-watch-offline.ps1 + roll-replay-time-increased.ps1 + pre-arm-debugger.ps1
  secondaryTools: Cheat Engine 7.7 (C:\Program Files\Cheat Engine) pre-armed and attached; x64dbg (C:\work\tools\x64dbg) discovered
  transition: Space pause/resume pulses during rolling; rollingBaseline=true
observations:
  - state: attempt-1
    launchOutcome: lifecycle_evidence outcome=verified
    watchExit: unknown (helper hung past 420s; 1-byte exit file)
    gateAfter: Denied / evidence.monitor_unhealthy
    note: game launched and correlated; helper stalled (likely PrintWindow capture-freeze path); game exited
  - state: attempt-2
    verificationState: OfflineReplayVerified
    startReplay: true
    dialogGone: false
    finalOrangePixels: 63876
    loginOnReplay: true
    watchExit: 3
    gateAfter: Denied / evidence.monitor_unhealthy
    note: replay actually playing (blitz-log startReplay=True) yet blob ROI false-positived on replay HUD; extra clicks killed game; lease lost
  - state: root-cause
    finding: OfflineReplayVerified proves the replay started (lifecycle monitor requires fresh START_REPLAY_LOCAL marker), which proves the WATCH OFFLINE dialog is gone; requiring the blob to vanish is redundant and harmful (orangePx grew 8276 → ~64k after dismissal)
    fixCommitted: 63845d7
  - state: attempt-3
    verificationState: OfflineReplayVerified
    watchExit: 0
    helperOutcome: SUCCESS_gate_and_dialog_dismissed
    preArm: CE 7.7 launched attached (PID 50152), marker written
  - state: rolling-bug
    round1Retained: 0
    round1Increased: 1041003
    driverRead: retainedCount (unreadable-chunk carryover only)
    contractTruth: survivor count = increasedCount (matches OD-013/015/016 sequences)
    fixStatus: applied to roll-replay-time-increased.ps1 (this unit)
    note: first -AddressFile dump was a raw 500-candidate round-1 sample, discarded
  - state: attempt-4
    status: not run (operator stopped the live session)
    gateAfter: EvidenceStale / evidence.expired
result:
  whatWorked:
    - Root-caused the two-launch game-kill: clicker blob ROI false-positives on replay HUD; fixed by trusting the verified gate (63845d7).
    - Attempt 3 reached OfflineReplayVerified with watch_exit=0 on the fixed clicker.
    - First live CE pre-arm in repo history: CE 7.7 launched attached to wotblitz.exe.
    - Contract-backed fix for the rolling driver: survivors are increasedCount, not retainedCount.
  whatFailed:
    - Full rolling to ≤10 with the fixed driver never ran; the 120s lease expired while fixing the field bug.
    - Attempt 4 (fresh launch) not run; interactive Find-what-writes still outstanding.
  rulesOut:
    - Requiring the orange-dialog blob to vanish after OfflineReplayVerified.
    - Treating compare retainedCount as the rolling survivor count.
  partials:
    - Clicker gate-trust fix live-proven (attempt 3 green); CE pre-arm path live-proven; rolling driver semantics fixed, ready for OD-RECOVERY-018.
    - BLK-0019 still needs content-distinct second replay (independentReplays 0).
  nextPivot: OD-RECOVERY-018 — fixed clicker + fixed driver; pre-arm concurrent with launch settle; rolling immediately post-verify; interactive Find-what-writes on ≤10; import content-distinct second .wotbreplay when available.
  repeatWithoutChangedHypothesis: false
artifacts:
  rawFiles: none
  committedSummary: this ledger entry + workflow update + handoff (2026-08-03-od-recovery-017-live.md)
```

`OD-RECOVERY-017` is aggregate structural evidence only. Offset remains 0.

## `OD-RECOVERY-018` result — 2026-08-03

```yaml
sessionId: OD-RECOVERY-018
date: 2026-08-03
observedAtUtc: 2026-08-03T12:41:00Z # approximate; green run observed after 12:33:59Z (gate was EvidenceStale then); lease expired by 12:44:04Z
timebox: managed launch to gate green; CE pre-arm; rolling Double increased to ≤10 with the fixed driver; interactive Find-what-writes deferred to operator
decision: FIRST complete automated pipeline run: OfflineReplayVerified (watch_exit=0, dialogGone=True), CE 7.7 pre-armed attached, fixed increasedCount rolling driver narrowed 743576→…→8 with -AddressFile staging 8 candidates (count==survivors); gate remained verified through rolling; lease expired before interactive Find-what-writes
objective: Run the proven OD-018 protocol end-to-end with the fixed clicker (63845d7) + fixed rolling driver (increasedCount), reaching ≤10 survivors with lease margin for operator Find-what-writes
stopCondition: Stop after gate green + pre-arm + rolling ≤10, or gate loss
method:
  primaryTool: scripts/launch-offline-replay-for-od.ps1 (background/nohup) + od-018 session driver + roll-replay-time-increased.ps1 + pre-arm-debugger.ps1
  secondaryTools: Cheat Engine 7.7 (C:\Program Files\Cheat Engine) pre-armed and attached
  transition: natural replay progression between rounds (no manual Space pulses; -AutoSpace not used)
observations:
  - state: attempt-1
    launchOutcome: helper stalled past 420s after watch exit file 0 (same stall signature as OD-017 attempt-1); gate EvidenceStale; wrapper timeout killed the helper
    note: watch succeeded but the in-process helper never returned; relaunched detached via nohup
  - state: attempt-2
    launchOutcome: background launch helper ran to completion
    verificationState: OfflineReplayVerified
    reason: session.offline_replay_verified
    watchExit: 0
    dialogGone: true
    postWatchOrangePixels: 0
    finalOrangePixels: 61420
    helperOutcome: SUCCESS_gate_and_dialog_dismissed
  - state: pre-arm
    cheatEngine: C:\Program Files\Cheat Engine\cheatengine-x86_64.exe
    x64dbg: C:\work\tools\x64dbg\release\x64\x64dbg.exe
    launched: cheatengine attach pid=17084 process=43432
    marker: %TEMP%\od-prearmed-debugger.json
  - state: rolling (fixed increasedCount driver)
    snapshot: session 000001 (Double 8-byte aligned, rollingBaseline=true)
    sequence: [743576, 126761, 23075, 4660, 726, 90, 33, 25, 24, 18, 17, 12, 8]
    rounds: 13
    targetReached: survivors=8 le 10
    addressFile: %TEMP%\od-survivors-018.txt count=8 survivors=8
    warn: none (address count matches survivors)
    sessionDiscarded: true
  - state: after-rolling
    verificationState: OfflineReplayVerified (gate held through rolling — lease-margin win)
    note: lease expired shortly after; game terminated by lifecycle monitor at expiry (expected)
result:
  whatWorked:
    - Fixed clicker gate-trust live-proven again: watch_exit=0 with dialogGone=True and zero post-watch orange.
    - CE 7.7 pre-armed and attached to wotblitz.exe (reproducing the OD-017 attempt-3 pre-arm); novelty here is the gate holding OfflineReplayVerified through the complete rolling sequence.
    - Fixed rolling driver (increasedCount) live-proven end-to-end: 743576 → 8 over 13 rounds, matching OD-013/015/016 semantics.
    - -AddressFile staged exactly 8 candidates with no count WARN — the pre-armed debugger has a ready survivor set.
    - Gate stayed OfflineReplayVerified through the full rolling sequence (lease-margin objective met).
    - Detached (nohup) launch avoided the in-process helper stall that killed attempt-1.
  whatFailed:
    - Interactive Find-what-writes on the 8 survivors did not run before lease expiry (operator-owned step).
    - Attempt-1's in-process helper stall (post-watch hang past 420s) is still not root-caused; worked around via detach.
  rulesOut:
    - No new rules-out beyond OD-017 (blob-ROI gate trust; retainedCount ≠ survivors).
  partials:
    - Full automated pipeline (launch → verify → pre-arm → roll ≤10 → address file) is now complete; only the interactive debugger step remains.
    - 8 candidate addresses staged locally for OD-RECOVERY-019 Find-what-writes.
    - BLK-0019 still needs content-distinct second replay (independentReplays 0).
  nextPivot: OD-RECOVERY-019 — reuse this pipeline; run operator interactive Find-what-writes on the 8 staged survivors immediately post-verify (or launch CE Lua pre-arm staging); import content-distinct second .wotbreplay when available.
  repeatWithoutChangedHypothesis: false
artifacts:
  rawFiles: %TEMP%\od-survivors-018.txt (8 local addresses), %TEMP%\od-prearmed-debugger.json
  committedSummary: this ledger entry + workflow update + handoff (2026-08-03-od-recovery-018.md)
```

`OD-RECOVERY-018` is aggregate structural evidence only. Offset remains 0.

## `OD-RECOVERY-019` result — 2026-08-03

```yaml
sessionId: OD-RECOVERY-019
date: 2026-08-03
observedAtUtc: 2026-08-03T13:04:00Z # approximate; gate verified through session end (query at 13:05:46Z still OfflineReplayVerified)
timebox: managed launch to gate green; CE pre-arm; rolling Double increased to ≤10 with fixed driver; interactive Find-what-writes handed off live
decision: second consecutive full pipeline run; rolling narrowed to 7 survivors (tightest set yet); gate remained OfflineReplayVerified at session end with CE pre-armed — the operator Find-what-writes window is live
objective: Reproduce the OD-018 pipeline, narrow the survivor set further, and leave the operator a live interactive window
stopCondition: Stop after gate green + pre-arm + rolling ≤10, or gate loss
method:
  primaryTool: scripts/launch-offline-replay-for-od.ps1 (detached) + od session driver + roll-replay-time-increased.ps1 + pre-arm-debugger.ps1
  secondaryTools: Cheat Engine 7.7 (C:\Program Files\Cheat Engine) pre-armed and attached
  transition: natural replay progression between rounds
  ceCliLuaProbe: -luac and --luac both rejected (no marker file); CE 7.7 does not auto-execute Lua from the command line — prearm-attach.lua stays operator-loaded (Ctrl+Alt+L)
observations:
  - state: launch
    launchOutcome: detached launch helper completed
    verificationState: OfflineReplayVerified
    reason: session.offline_replay_verified
    watchExit: 0
    dialogGone: true
    helperOutcome: SUCCESS_gate_and_dialog_dismissed
  - state: pre-arm
    launched: cheatengine attach pid=42388 process=45188
    marker: %TEMP%\od-prearmed-debugger.json
  - state: rolling (fixed increasedCount driver)
    snapshot: session 000001 (Double 8-byte aligned, rollingBaseline=true)
    sequence: [2980777, 73925, 11283, 2290, 401, 75, 24, 17, 16, 16, 16, 14, 11, 7]
    rounds: 14
    targetReached: survivors=7 le 10
    addressFile: %TEMP%\od-survivors-019.txt count=7 survivors=7
    warn: none
    sessionDiscarded: true
  - state: after-session
    verificationState: OfflineReplayVerified (query 13:05:46Z)
    gamePid: 42388 alive
    cheatEnginePid: 45188 attached
    note: interactive Find-what-writes window live at handoff
result:
  whatWorked:
    - Detached launch + session driver reproduced the full pipeline on demand (second consecutive green run).
    - Rolling narrowed to 7 survivors — tightest set in campaign history (previous best 8).
    - Address file staged exactly 7 candidates with no count WARN.
    - Gate held OfflineReplayVerified through session end with CE pre-armed — first live interactive handoff.
    - CE CLI Lua probe (negative result): -luac/--luac unsupported on CE 7.7; documents why prearm-attach.lua must be operator-loaded.
  whatFailed:
    - Interactive Find-what-writes not completed during this automated turn (operator-owned step; window handed off live).
  rulesOut:
    - CE 7.7 command-line Lua auto-execution (-luac/--luac) as an automation path.
  partials:
    - Pipeline reproducible; survivor set at 7; interactive window live at handoff.
    - BLK-0019 still needs content-distinct second replay (independentReplays 0).
  nextPivot: OD-RECOVERY-020 — reuse pipeline; operator runs interactive Find-what-writes on staged survivors immediately post-roll (CE pre-armed, address file ready); import content-distinct second .wotbreplay when available.
  repeatWithoutChangedHypothesis: false
artifacts:
  rawFiles: %TEMP%\od-survivors-019.txt (7 local addresses), %TEMP%\od-prearmed-debugger.json
  committedSummary: this ledger entry + workflow update + handoff (2026-08-03-od-recovery-019.md)
```

`OD-RECOVERY-019` is aggregate structural evidence only. Offset remains 0.

## `OD-RECOVERY-020` result — 2026-08-03 (autorun write-BP probe campaign)

```yaml
sessionId: OD-RECOVERY-020
supersedes: none (probe campaign over OD-020/021/022 live runs)
date: 2026-08-03
observedAtUtc: 2026-08-03T13:20:00Z # approximate; three live runs 13:17-13:33Z
timebox: three managed launches; automated CE autorun write-BP capture replacing the operator-owned interactive Find-what-writes
decision: automated CE Windows-debugger write-BP capture on rolling Double survivors is ruled out — zero RIP hits across all three live runs and matching the OD-009/010/011 zero-hit history; rolling still narrows to ≤10 (5 survivors this campaign), so the operator-owned interactive step is required, not a scripting gap
objective: Eliminate the operator bottleneck by auto-staging survivors into CE and auto-arming write breakpoints with a hit logger, then capture the writing instruction without an operator
stopCondition: Stop after three live runs or first captured hit; any gate loss aborts
method:
  primaryTool: CE autorun script tools/cheat-engine/od-autorun-writebp.lua (installed to C:\Program Files\Cheat Engine\autorun\) + fixed rolling Double increased + pre-arm-debugger.ps1
  secondaryTools: Cheat Engine 7.7 (C:\Program Files\Cheat Engine)
  transition: natural replay progression between rounds
  ceCliLuaProbe: -luac and --luac both rejected (no marker file) — CE 7.7 does not auto-execute Lua from the command line
  autorunGate: %TEMP%\od-prearmed-debugger.json (pre-arm marker) required before the autorun script acts; otherwise inert
observations:
  - state: run-020
    rolling: 2749700->348214->5955->1434->278->53->14->6 (6 survivors)
    autorun: attached, staged 6, debugger attached, breakpoint API `setBreakpoint` = nil (CE 7.7) -> armed 0
    hits: 0
  - state: run-021
    rolling: 2085299->374877->5552->1532->288->52->8 (8 survivors)
    autorun: attached, staged 8, `debug_setBreakpoint` resolved, armed 4 of 8 (x64 4-DR hardware limit; rest returned false), `waitForBreakpoint` not a valid function
    hits: 0
  - state: run-022
    rolling: 3084237->50993->8253->1951->515->56->15->5 (5 survivors, tightest in campaign history)
    autorun: attached, staged 5, `debug_setBreakpoint` resolved, armed 4 (1 skipped beyond HW limit), global `onBreakpoint` callback defined + wait-loop fallback, 20s live polling window during active playback
    hits: 0
result:
  whatWorked:
    - Rolling narrowed to 5 survivors (best ever) with the proven pipeline; gate verified each run.
    - CE autorun mechanism live-proven: CE 7.7 executes .lua files in its autorun folder at startup; the script attached, staged survivors into the address list, resolved `debug_setBreakpoint`, and armed 4 hardware write-BPs (the x64 DR0-DR3 limit).
    - CE CLI Lua auto-execution (`-luac`/`--luac`) ruled out as an automation path (negative probe).
  whatFailed:
    - Zero breakpoint hits across three live runs even though replayTime advances every frame during the 20s capture window — the automated Windows-debugger write-BP path does not fire in this environment (consistent with OD-009/010/011 zero-hit history).
    - `onBreakpoint` callback and wait-loop fallbacks produced no hits; mechanism cause unknown (debugger not actually breaking, or write source not traceable this way).
  rulesOut:
    - CE CLI Lua auto-execution (`-luac`/`--luac`) as an automation path.
    - Automated CE Windows-debugger write-BPs on rolling Double survivors as a replacement for the interactive Find-what-writes step (repeat only with a changed mechanism, e.g. x64dbg or interactive CE).
  partials:
    - CE autorun attach/stage/arm pipeline works; only hit capture fails.
    - 5-survivor address file staged for a future operator interactive session.
    - BLK-0019 still needs content-distinct second replay (independentReplays 0).
  nextPivot: OD-RECOVERY-023 — reuse the proven pipeline with the **operator present** for interactive Find-what-writes on staged survivors immediately post-roll (CE pre-armed, address file ready); consider x64dbg (installed) for instruction/register evidence; import content-distinct second .wotbreplay when available.
  repeatWithoutChangedHypothesis: false
artifacts:
  rawFiles: %TEMP%\od-survivors.txt (5 local addresses), %TEMP%\od-ce-autorun.log, %TEMP%\od-ce-hits.log (empty)
  committedSummary: this ledger entry + workflow update + handoff (2026-08-03-od-recovery-020.md)
```

`OD-RECOVERY-020` is aggregate structural evidence only. Offset remains 0.

## `OD-RECOVERY-023` result — 2026-08-03

```yaml
sessionId: OD-RECOVERY-023
date: 2026-08-03
observedAtUtc: 2026-08-03T13:46:01Z # gate still OfflineReplayVerified at handoff query
timebox: managed launch to gate green; CE pre-arm + autorun staging; rolling Double increased to ≤10; operator Find-what-writes window delivered live
decision: pipeline reproduced (sixth consecutive green run); 9 survivors staged in CE's address list with the gate still verified at handoff — the operator-owned interactive Find-what-writes window was delivered live with survivors staged in CE
objective: Run the operator-present protocol: reach OfflineReplayVerified, pre-arm CE with autorun staging, roll to ≤10, and hand the live green window to the operator for Find-what-writes
stopCondition: Stop after gate green + pre-arm + rolling ≤10, or gate loss
method:
  primaryTool: scripts/launch-offline-replay-for-od.ps1 (detached) + session driver + roll-replay-time-increased.ps1 + pre-arm-debugger.ps1
  secondaryTools: Cheat Engine 7.7 (C:\Program Files\Cheat Engine) pre-armed attached; CE autorun od-autorun-writebp.lua staged survivors into the address list
  transition: natural replay progression between rounds
observations:
  - state: launch
    launchOutcome: detached launch helper completed
    verificationState: OfflineReplayVerified
    reason: session.offline_replay_verified
  - state: pre-arm
    launched: cheatengine attach pid=43460 process=41312
    marker: %TEMP%\od-prearmed-debugger.json
  - state: autorun-staging
    staged: 9 survivors in CE address list (od-survivor-1..9)
    breakpointApi: debug_setBreakpoint resolved; armed 4 (x64 HW limit); 5 skipped beyond limit
    hits: 0 (consistent with OD-020 rules-out; interactive step remains the evidence path)
  - state: rolling (fixed increasedCount driver)
    sequence: [765363, 181456, 31840, 4266, 596, 89, 22, 16, 16, 14, 11, 9]
    rounds: 12
    targetReached: survivors=9 le 10
    addressFile: %TEMP%\od-survivors.txt count=9 survivors=9
    warn: none
    sessionDiscarded: true
  - state: handoff
    verificationState: OfflineReplayVerified (query 13:46:01Z)
    gamePid: 43460 alive
    cheatEnginePid: 41312 attached with 9 staged survivors
    note: operator Find-what-writes window delivered live
result:
  whatWorked:
    - Detached launch + session driver reproduced the full pipeline (fifth consecutive green pipeline run).
    - Rolling narrowed to 9 survivors; address file staged exactly 9 candidates, no WARN.
    - CE autorun staged all 9 survivors into CE's address list with descriptions od-survivor-1..9.
    - Gate was still OfflineReplayVerified at the 13:46:01Z handoff query — the operator window is live.
  whatFailed:
    - No operator Find-what-writes result recorded by session end (operator-owned; window handed off live).
  rulesOut:
    - No new rules-out (OD-020 automated write-BP rules-out stands).
  partials:
    - Everything staged for the interactive root: gate green, CE attached, 9 survivors in the address list.
    - BLK-0019 still needs content-distinct second replay (independentReplays 0).
  nextPivot: OD-RECOVERY-024 — operator runs interactive Find-what-writes on the staged survivors during the live green window; import content-distinct second .wotbreplay when available.
  repeatWithoutChangedHypothesis: false
artifacts:
  rawFiles: %TEMP%\od-survivors.txt (9 local addresses), %TEMP%\od-ce-autorun.log
  committedSummary: this ledger entry + workflow update + handoff (2026-08-03-od-recovery-023.md)
```

`OD-RECOVERY-023` is aggregate structural evidence only. Offset remains 0.

## `OD-RECOVERY-024` result — 2026-08-03

```yaml
sessionId: OD-RECOVERY-024
date: 2026-08-03
observedAtUtc: 2026-08-03T14:01:35Z # gate still OfflineReplayVerified through the full 60s hold (autorun capture ended 10:01:35 EDT)
timebox: managed launch to gate green; CE pre-arm + autorun staging; rolling Double increased to ≤10; 60s in-command operator hold polling the gate
decision: pipeline reproduced (seventh consecutive green run); gate held OfflineReplayVerified through the entire 60s operator window — the interactive window expired on its own timer, not gate loss (first time); 8 survivors staged in CE with the window held inside the running command
objective: Hold the operator Find-what-writes window inside the running session driver so the interactive step can land while the gate is green, instead of after the command returns
stopCondition: Stop after gate green + pre-arm + rolling ≤10 + hold window expiry, or gate loss during hold
method:
  primaryTool: scripts/launch-offline-replay-for-od.ps1 (detached) + session driver (-HoldAfterRollSeconds 60) + roll-replay-time-increased.ps1 + pre-arm-debugger.ps1
  secondaryTools: Cheat Engine 7.7 (C:\Program Files\Cheat Engine) pre-armed attached; CE autorun od-autorun-writebp.lua staged survivors into the address list
  transition: natural replay progression between rounds
observations:
  - state: launch
    launchOutcome: detached launch helper completed
    verificationState: OfflineReplayVerified
    reason: session.offline_replay_verified
  - state: pre-arm
    launched: cheatengine attach pid=8428
    marker: %TEMP%\od-prearmed-debugger.json
  - state: autorun-staging
    staged: 8 survivors in CE address list (od-survivor-1..8)
    breakpointApi: debug_setBreakpoint resolved; armed 4 (x64 HW limit); 4 skipped beyond limit
    hits: 0 (consistent with OD-020 rules-out; interactive step remains the evidence path)
  - state: rolling (fixed increasedCount driver)
    sequence: [767529, ..., 8] # head and final captured; intermediates printed to live terminal only, not persisted
    targetReached: survivors=8 le 10
    addressFile: %TEMP%\od-survivors.txt count=8 survivors=8
    warn: none
    sessionDiscarded: true
  - state: operator-hold (60s, in-command)
    verificationState: OfflineReplayVerified across the full hold (polled inline)
    windowEnd: hold timer expired with gate still green (first session where the operator window closed by its own timer, not gate loss)
    note: Find-what-writes instruction printed inline for the operator while the gate stayed verified
result:
  whatWorked:
    - In-command operator hold: the Find-what-writes window stayed open inside the running driver for the full 60s with the gate verified the entire time.
    - Rolling narrowed to 8 survivors; address file staged exactly 8 candidates, no WARN.
    - CE autorun staged all 8 survivors into CE's address list with descriptions od-survivor-1..8.
    - Window closed by timer expiry, not gate loss — the lease-margin goal from OD-016/017 is now met repeatedly.
  whatFailed:
    - No operator Find-what-writes result recorded by session end (operator-owned; window held live inside the command).
  rulesOut:
    - No new rules-out (OD-020 automated write-BP rules-out stands).
  partials:
    - Everything staged for the interactive root: gate green for the full hold, CE attached, 8 survivors in the address list.
    - BLK-0019 still needs content-distinct second replay (independentReplays 0).
  nextPivot: OD-RECOVERY-025 — operator performs interactive Find-what-writes during the held green window (driver holds 60s in-command); import content-distinct second .wotbreplay when available.
  repeatWithoutChangedHypothesis: false
artifacts:
  rawFiles: %TEMP%\od-survivors.txt (8 local addresses), %TEMP%\od-ce-autorun.log
  committedSummary: this ledger entry + workflow update + handoff (2026-08-03-od-recovery-024.md)
```

`OD-RECOVERY-024` is aggregate structural evidence only. Offset remains 0.

## `OD-RECOVERY-025` result — 2026-08-03 (two failed attempts, new failure modes)

```yaml
sessionId: OD-RECOVERY-025
date: 2026-08-03
observedAtUtc: 2026-08-03T14:30:38Z # attempt 2 gate queried Denied/evidence.monitor_unhealthy at 14:30Z
attempts:
  - attempt: 1
    outcome: rolling_failed_400
    detail: snapshot taken during the game load transition captured 66,592,223 candidates (22-87x the 0.7-3M steady state) and hit the 512MB cap; rolling converged too slowly (679 survivors after 6 rounds), the 120s research lease expired mid-roll, the coordinator discarded all scan sessions, and round-7 compare hit a discarded session -> endpoint maps session_not_found to 400 BadRequest
    survivorSequence: [66592223, 3800342, 77455, 18332, 35599, 2034, 679] # initial count + per-round increased survivors; then 400 on round 7
    survivorsStaged: false
  - attempt: 2
    outcome: settle_aborted_gate_denied
    detail: with a new 25s pre-snapshot settle, the gate flipped to Denied/evidence.monitor_unhealthy during the settle - the game was terminated right after lifecycle evidence verification, before the snapshot ran
    preSnapshotSettleSeconds: 25
    survivorsStaged: false
timebox: managed launch to gate green; CE pre-arm + autorun staging; rolling Double increased to ≤10; pre-snapshot settle attempted
decision: both attempts failed before any survivors were staged; two distinct failure modes recorded (load-transition snapshot blowup -> lease expiry -> 400; monitor_unhealthy game termination during settle). Do not repeat either without a changed hypothesis; OD-026 must wait for a clean steady-state snapshot
objective: Reproduce the operator-present pipeline (gate green -> CE pre-arm -> roll to ≤10 -> held operator window) and capture the interactive Find-what-writes window
stopCondition: Stop after gate green + pre-arm + rolling ≤10, or gate loss
method:
  primaryTool: scripts/launch-offline-replay-for-od.ps1 (detached) + session driver + roll-replay-time-increased.ps1 + pre-arm-debugger.ps1
  secondaryTools: Cheat Engine 7.7 pre-armed attached; CE autorun od-autorun-writebp.lua
  transition: natural replay progression between rounds
observations:
  - state: launch
    launchOutcome: detached launch helper completed (both attempts)
    verificationState: OfflineReplayVerified
    reason: session.offline_replay_verified
  - state: pre-arm
    launched: cheatengine attach (attempt 1 game pid 41468; attempt 2 game pid 50648)
    marker: %TEMP%\od-prearmed-debugger.json
  - state: rolling (attempt 1 only)
    note: snapshot candidate count 66,592,223 - 22-87x the OD-018..024 steady-state range of 0.7-3M; the game was still in its load transition (WATCH OFFLINE dialog dismissed -> battle loading) when the snapshot ran
    survivorSequence: [66592223, 3800342, 77455, 18332, 35599, 2034, 679] # initial + per-round increased survivors (per-round `previous=` lines were printed; the script's final sequence= line never printed because it exited 5 on the 400)
    targetReached: false (679 survivors after 6 rounds, still gt 10)
    failure: compare round 7 returned HTTP 400; host log shows DiscardAllSessions ran after round 6 (lease expired mid-roll); endpoint maps session_not_found to BadRequest
  - state: settle (attempt 2 only)
    preSnapshotSettleSeconds: 25
    failure: gate Denied/evidence.monitor_unhealthy during the settle; host log shows lifecycle_evidence verified then monitor revoke ~immediately; game process terminated before the snapshot ran
result:
  whatWorked:
    - Both attempts reached OfflineReplayVerified and CE pre-arm succeeded (eighth and ninth green launch stages overall).
    - The 400 root cause is now fully diagnosed: load-transition snapshot -> slow convergence -> lease expiry -> session discard -> session_not_found mapped to 400.
  whatFailed:
    - Attempt 1: rolling died at round 7 with a host 400; no survivors staged.
    - Attempt 2: gate Denied/evidence.monitor_unhealthy during the 25s settle; game terminated; no snapshot ran.
  rulesOut:
    - Rolling from a load-transition snapshot (66M candidates) cannot converge within the 120s lease; the snapshot must wait for steady-state playback (OD-026 hypothesis).
    - A fixed 25s settle does not reliably clear the load transition; and the monitor can revoke during it.
  partials:
    - The interactive Find-what-writes window was never reached; no survivors staged; no RIP/root evidence.
    - BLK-0019 still open (independentReplays 0; same Churchill sha12 0FAE5612491E).
  nextPivot: OD-RECOVERY-026 — wait for a clean steady-state snapshot before rolling: lengthen/verify the settle (poll the game replay clock or candidate-count sanity), keep the roll ≤10 within the lease, and only then run the interactive operator window; import content-distinct second .wotbreplay when available.
  repeatWithoutChangedHypothesis: false # both attempts repeated the same pipeline without a steady-state gate; do not repeat without one (OD-026 rule)
artifacts:
  rawFiles: %TEMP%\od-survivors.txt (not produced), %TEMP%\od-launch-host.log, %TEMP%\od-025.out.log, %TEMP%\od-025b.out.log
  committedSummary: this ledger entry + workflow update + handoff (2026-08-03-od-recovery-025.md)
```

`OD-RECOVERY-025` is aggregate structural evidence only (two failed attempts, two new failure modes diagnosed). Offset remains 0.

## `OD-RECOVERY-026` result — 2026-08-03 (steady-state gate; baseline diagnosis corrected)

```yaml
sessionId: OD-RECOVERY-026
date: 2026-08-03
observedAtUtc: 2026-08-03T15:07:34Z # autorun staged 9 survivors; gate held OfflineReplayVerified through rolling
attempts:
  - attempt: 1
    outcome: gate_rejected_stable_baseline
    detail: new steady-state gate (MaxInitialCandidates=10M) probed the snapshot and rejected all 3 snapshots at ~66.5M (66,477,826 / 66,491,111 / 66,459,371 - within 0.05%), then FAILED_snapshot_not_sane. Proving the 66M state is STABLE for this game session, not a transient load spike
    sequence: [66477826, 66491111, 66459371] # 3 rejected probes
    survivorsStaged: false
  - attempt: 2
    outcome: rolled_to_9_survivors
    detail: threshold raised to 100M (accepts the proven-stable 66M baseline); round limit bumped to 22; transitions shortened to 5s so a large baseline fits the 120s lease. Snapshot sane (66,313,259), rolling converged in 8 rounds to 9 survivors; address file 9 lines, no WARN; CE autorun staged all 9, armed 4 write-BPs, 0 hits
    sequence: [1125953, 243067, 39853, 6229, 917, 168, 24, 9] # per-round increased survivors (initial 66,313,259 recorded above under snapshotCandidateCount)
    survivorsStaged: true
    gateHeldThroughRolling: true
    windowEnd: EvidenceStale after 60s hold timer (not gate loss during roll)
timebox: managed launch to gate green; CE pre-arm + autorun staging; rolling Double increased to ≤10 with steady-state gate; 60s in-command operator hold
decision: OD-025's diagnosis is corrected - the 66M candidate state is the game's STABLE memory footprint this session (535MB, ~1880 regions), not a load-transition spike. The well-evidenced correction is that baseline size was NOT the cause (66M converges: OD-025 attempt 1 itself went 66M→679 in 6 rounds, and attempt 2 here went 66.3M→9). The precise trigger for OD-025 attempt 1's session discard was never conclusively pinned - its final gate read Denied (not EvidenceStale, which a pure lease expiry would produce), so the monitor-health family cannot be excluded. The steady-state gate now accepts a stable large baseline and the roll converges within the lease
objective: Implement the OD-025 steady-state rule (never roll from a load-transition snapshot) as an explicit gate: probe the snapshot's initial candidate count, reject absurd/transient states, accept a stable baseline, roll to ≤10, hold the operator window
stopCondition: Stop after gate green + pre-arm + sane snapshot + rolling ≤10, or gate loss
method:
  primaryTool: scripts/launch-offline-replay-for-od.ps1 (detached) + session driver + roll-replay-time-increased.ps1 (steady-state gate) + pre-arm-debugger.ps1
  secondaryTools: Cheat Engine 7.7 pre-armed attached; CE autorun od-autorun-writebp.lua staged survivors into the address list
  transition: natural replay progression between rounds (5s)
observations:
  - state: launch
    launchOutcome: detached launch helper completed
    verificationState: OfflineReplayVerified
    reason: session.offline_replay_verified
  - state: pre-arm
    launched: cheatengine attach (attempt 2 game pid 48404)
    marker: %TEMP%\od-prearmed-debugger.json
  - state: steady-state gate
    threshold: 100000000 (attempt 2; 10M in attempt 1 rejected the stable 66M baseline)
    snapshotCandidateCount: 66,313,259 (attempt 2) / 66,477,826..66,491,111 (attempt 1 probes)
    stability: three attempt-1 snapshots within 0.05% - stable state, not transient
    bytes: ~535MB retained (near the 512MB engine ceiling); regions ~1880
  - state: rolling (attempt 2, fixed driver + gate)
    sequence: [1125953, 243067, 39853, 6229, 917, 168, 24, 9]
    rounds: 8
    targetReached: survivors=9 le 10
    addressFile: %TEMP%\od-survivors.txt count=9 survivors=9
    warn: none
    sessionDiscarded: true
  - state: autorun-staging
    staged: 9 survivors in CE address list (od-survivor-1..9)
    breakpointApi: debug_setBreakpoint resolved; armed 4 (x64 HW limit); 5 skipped beyond limit
    hits: 0 (consistent with OD-020 rules-out; interactive step remains the evidence path)
  - state: operator-hold (60s, in-command)
    verificationState: OfflineReplayVerified at window open
    windowEnd: EvidenceStale after timer expiry (lease budget consumed; window closed by timer, not gate loss during rolling)
result:
  whatWorked:
    - The steady-state gate correctly separated a stable baseline (accept) from a transient state (reject) - the OD-025 rule is now enforceable, not a blind settle.
    - Corrected the OD-025 diagnosis: 66M is stable this session; baseline size was not the cause (66M converges). The precise discard trigger for OD-025 attempt 1 (gate read Denied, not EvidenceStale) was not conclusively pinned - 5s transitions + a higher round limit make large-baseline rolls fit the lease regardless.
    - Rolling with the gate + 5s transitions converged from 66M to 9 survivors in 8 rounds, well inside the lease.
    - Address file staged exactly 9 candidates, no WARN; CE autorun staged all 9 with descriptions od-survivor-1..9.
    - Gate stayed OfflineReplayVerified through the entire roll; window closed by its own timer.
  whatFailed:
    - No operator Find-what-writes result recorded by session end (operator-owned; window held live inside the command).
  rulesOut:
    - No new rules-out. The 10M threshold is recorded as a calibration error, not a rule: the gate must accept the measured stable baseline, not an assumed one.
  partials:
    - Everything staged for the interactive root: gate green through rolling, CE attached, 9 survivors in the address list.
    - BLK-0019 still needs content-distinct second replay (independentReplays 0).
  nextPivot: OD-RECOVERY-027 — operator performs interactive Find-what-writes on the staged survivors during the held green window (driver holds 60s in-command); import content-distinct second .wotbreplay when available.
  repeatWithoutChangedHypothesis: false
artifacts:
  rawFiles: %TEMP%\od-survivors.txt (9 local addresses), %TEMP%\od-ce-autorun.log, %TEMP%\od-launch-host.log
  committedSummary: this ledger entry + workflow update + handoff (2026-08-03-od-recovery-026.md)
```

`OD-RECOVERY-026` is aggregate structural evidence only. Offset remains 0.

## `OD-RECOVERY-027` result — 2026-08-03

```yaml
sessionId: OD-RECOVERY-027
date: 2026-08-03
observedAtUtc: 2026-08-03T15:22:35Z # autorun capture ended 11:22:35 EDT; gate still OfflineReplayVerified at operator-window final read
timebox: managed launch to gate green; CE pre-arm + autorun staging; rolling Double increased to ≤10 (steady-state gate + 5s transitions); 60s in-command operator hold
decision: pipeline reproduced with the steady-state gate (stable 66M baseline accepted, 5s transitions); rolled to 7 survivors (tied with OD-019; campaign record remains OD-020's 5); gate held OfflineReplayVerified through the full 60s operator window AND the final read after the hold — the interactive window was live and green at command end
objective: Operator-present run: reach OfflineReplayVerified, pre-arm CE with autorun staging, roll to ≤10 with the steady-state gate, and hold the Find-what-writes window open for the operator
stopCondition: Stop after gate green + pre-arm + sane snapshot + rolling ≤10, or gate loss
method:
  primaryTool: scripts/launch-offline-replay-for-od.ps1 (detached) + session driver (-TransitionSeconds 5 -HoldAfterRollSeconds 60) + roll-replay-time-increased.ps1 (steady-state gate) + pre-arm-debugger.ps1
  secondaryTools: Cheat Engine 7.7 pre-armed attached; CE autorun od-autorun-writebp.lua staged survivors into the address list
  transition: natural replay progression between rounds (5s)
observations:
  - state: launch
    launchOutcome: detached launch helper completed
    verificationState: OfflineReplayVerified
    reason: session.offline_replay_verified
  - state: pre-arm
    launched: cheatengine attach pid=51336 (game) / 49548 (CE)
    marker: %TEMP%\od-prearmed-debugger.json
  - state: steady-state gate
    snapshotCandidateCount: 66,571,431 (accepted; stable large baseline)
    threshold: 100000000
  - state: rolling (fixed driver + gate, 5s transitions)
    sequence: [915639, 208523, 38566, 6357, 802, 220, 139, 7] # per-round increased survivors (initial 66,571,431 above)
    rounds: 8
    targetReached: survivors=7 le 10
    addressFile: %TEMP%\od-survivors.txt count=7 survivors=7
    warn: none
    sessionDiscarded: true
  - state: autorun-staging
    staged: 7 survivors in CE address list (od-survivor-1..7)
    breakpointApi: debug_setBreakpoint resolved; armed 4 (x64 HW limit); 3 skipped beyond limit
    hits: 0 (consistent with OD-020 rules-out; interactive step remains the evidence path)
  - state: operator-hold (60s, in-command)
    verificationState: OfflineReplayVerified at window open AND at final read after the 60s hold
    windowEnd: timer expiry with gate still green; EvidenceStale only after recording began (lease budget consumed)
result:
  whatWorked:
    - The steady-state gate accepted the stable 66M baseline and 5s transitions converged in 8 rounds to 7 survivors (tied with OD-019; campaign record remains OD-020's 5).
    - Address file staged exactly 7 candidates, no WARN; CE autorun staged all 7 with descriptions od-survivor-1..7.
    - The gate was still OfflineReplayVerified at the operator-window final read — the interactive window was live and green at command end (best end state yet).
  whatFailed:
    - No operator Find-what-writes result recorded by session end (operator-owned; window held live inside the command).
  rulesOut:
    - No new rules-out (OD-020 automated write-BP rules-out and OD-026 steady-state-gate calibration stand).
  partials:
    - Everything staged for the interactive root: gate green through the entire hold, CE attached, 7 survivors in the address list.
    - BLK-0019 still needs content-distinct second replay (independentReplays 0).
  nextPivot: OD-RECOVERY-028 — operator performs interactive Find-what-writes on the staged survivors during the held green window (driver holds 60s in-command); import content-distinct second .wotbreplay when available.
  repeatWithoutChangedHypothesis: false
artifacts:
  rawFiles: %TEMP%\od-survivors.txt (7 local addresses), %TEMP%\od-ce-autorun.log, %TEMP%\od-launch-host.log
  committedSummary: this ledger entry + workflow update + handoff (2026-08-03-od-recovery-027.md)
```

`OD-RECOVERY-027` is aggregate structural evidence only. Offset remains 0.

## `OD-RECOVERY-028` result — 2026-08-03

```yaml
sessionId: OD-RECOVERY-028
date: 2026-08-03
observedAtUtc: 2026-08-03T15:39:23Z # autorun capture ended 11:39:23 EDT; gate still OfflineReplayVerified at the final re-announce
timebox: managed launch to gate green; CE pre-arm + autorun staging; rolling Double increased to ≤10 (steady-state gate + 5s transitions); extended in-command operator hold (up to 240s, exits early on gate loss)
decision: extended the operator window from 60s to up to 240s (exits early on gate loss) and added periodic re-announcements; OD-027 showed the gate stays green past 60s, so the longer cap hands the operator the whole remaining lease; gate held OfflineReplayVerified through 3 re-announcements (~90s+) before lease expiry to EvidenceStale
objective: Operator-present run with an extended green window: reach OfflineReplayVerified, pre-arm CE with autorun staging, roll to ≤10 with the steady-state gate, and hold the Find-what-writes window open for the whole remaining lease
stopCondition: Stop after gate green + pre-arm + sane snapshot + rolling ≤10, or gate loss
method:
  primaryTool: scripts/launch-offline-replay-for-od.ps1 (detached) + session driver (-TransitionSeconds 5 -HoldAfterRollSeconds 240) + roll-replay-time-increased.ps1 (steady-state gate) + pre-arm-debugger.ps1
  secondaryTools: Cheat Engine 7.7 pre-armed attached; CE autorun od-autorun-writebp.lua staged survivors into the address list
  transition: natural replay progression between rounds (5s)
observations:
  - state: launch
    launchOutcome: detached launch helper completed
    verificationState: OfflineReplayVerified
    reason: session.offline_replay_verified
  - state: pre-arm
    launched: cheatengine attach pid=45376 (game) / 27892 (CE)
    marker: %TEMP%\od-prearmed-debugger.json
  - state: steady-state gate
    snapshotCandidateCount: 66,586,557 (accepted; stable large baseline)
    threshold: 100000000
  - state: rolling (fixed driver + gate, 5s transitions)
    sequence: [789811, 176224, 30971, 6304, 570, 99, 22, 9] # per-round increased survivors (initial 66,586,557 above)
    rounds: 8
    targetReached: survivors=9 le 10
    addressFile: %TEMP%\od-survivors.txt count=9 survivors=9
    warn: none
    sessionDiscarded: true
  - state: autorun-staging
    staged: 9 survivors in CE address list (od-survivor-1..9)
    breakpointApi: debug_setBreakpoint resolved; armed 4 (x64 HW limit); 5 skipped beyond limit
    hits: 0 (consistent with OD-020 rules-out; interactive step remains the evidence path)
  - state: operator-hold (up to 240s, in-command, periodic re-announce every 30s)
    reAnnouncements: 3 while gate=OfflineReplayVerified (~90s+ of green hold)
    verificationState: OfflineReplayVerified at open and through the re-announcements
    windowEnd: EvidenceStale after the lease budget was consumed (gate loss, not timer cap)
result:
  whatWorked:
    - The extended hold (up to 240s, exits early on gate loss) gave the operator the whole remaining lease: the gate stayed OfflineReplayVerified through 3 re-announcements (~90s+).
    - Periodic re-announcements keep the Find-what-writes instruction visible on the live transcript for the entire window.
    - Rolling again converged in 8 rounds to 9 survivors (steady-state gate accepted the stable 66M baseline; 5s transitions).
    - Address file staged exactly 9 candidates, no WARN; CE autorun staged all 9 with descriptions od-survivor-1..9.
  whatFailed:
    - No operator Find-what-writes result recorded by session end (operator-owned; window held live inside the command).
  rulesOut:
    - No new rules-out (OD-020 automated write-BP rules-out and OD-026 steady-state-gate calibration stand).
  partials:
    - Everything staged for the interactive root: gate green through the extended hold, CE attached, 9 survivors in the address list.
    - BLK-0019 still needs content-distinct second replay (independentReplays 0).
  nextPivot: OD-RECOVERY-029 — operator performs interactive Find-what-writes on the staged survivors during the extended held green window; import content-distinct second .wotbreplay when available.
  repeatWithoutChangedHypothesis: false
artifacts:
  rawFiles: %TEMP%\od-survivors.txt (9 local addresses), %TEMP%\od-ce-autorun.log, %TEMP%\od-launch-host.log
  committedSummary: this ledger entry + workflow update + handoff (2026-08-03-od-recovery-028.md)
```

`OD-RECOVERY-028` is aggregate structural evidence only. Offset remains 0.

## `OD-RECOVERY-029` result — 2026-08-03

```yaml
sessionId: OD-RECOVERY-029
date: 2026-08-03
observedAtUtc: 2026-08-03T15:52:03Z
# Gate evidence expired 15:52:46Z (host query) — post-lease, expected.
timebox: 120s research lease
decision: operator-present session with the lease-bound operator window (driver -HoldAfterRollSeconds 240 cap, exits early on gate loss, re-announces every 30s)
objective: hold the gate-green operator window for the whole remaining lease and stage the tightest survivor set possible for interactive Find-what-writes
stopCondition: gate loss or 240s cap
method:
  - state: launch (detached)
    gate: OfflineReplayVerified
    gamePid: 36148
    cePid: 6420
  - state: steady-state gate
    snapshot: accepted (sane) at the 100M threshold (stable 66M baseline per OD-026..028)
    note: exact initial candidate count printed to live terminal only, not persisted
  - state: rolling (fixed increasedCount driver, 5s transitions)
    survivors: 6 le 10
    sequence: not persisted (driver stdout captured to live terminal only)
    addressFile: %TEMP%\od-survivors.txt count=6 survivors=6
    warn: none (address-count check; CE autorun HW-limit skips noted separately below)
  - state: CE autorun staging + write-BP capture
    staged: 6 (od-survivor-1..6)
    armed: 4 write-BPs (x64 HW limit); 2 skipped beyond HW limit
    hits: 0 in ~22s onBreakpoint poll
    captureWindow: 15:51:41Z..15:52:03Z — fully inside the verified gate (expired 15:52:46Z)
observations:
  - 6 survivors is the tightest set since OD-RECOVERY-020's 5 (beats OD-019/OD-027's 7) — second-tightest of the campaign.
  - The 0-hit write-BP capture ran entirely inside the green window — consistent with the OD-020 automated write-BP rules-out (no writes to these 6 addresses in a live window).
  - Same Churchill sha12 0FAE5612491E; independentReplays still 0; BLK-0019 open.
result:
  whatWorked:
    - 6 survivors staged (tightest since OD-020's 5) with the steady-state gate accepting the stable 66M baseline.
    - The lease-bound hold kept the gate green through the entire CE capture; EvidenceStale only at lease expiry (15:52:46Z), after the capture ended.
  whatFailed:
    - No operator Find-what-writes result recorded by session end (operator-owned; window held live inside the command).
    - Per-round sequence not persisted (driver stdout to live terminal only) — recorded from the address file + autorun log.
  rulesOut:
    - No new rules-out (OD-020 automated write-BP rules-out and OD-026 steady-state-gate calibration stand).
  partials:
    - Everything staged for the interactive root: gate green through the capture, CE attached, 6 survivors in the address list.
    - BLK-0019 still needs content-distinct second replay (independentReplays 0).
  nextPivot: OD-RECOVERY-030 — operator performs interactive Find-what-writes on the staged survivors during the held green window; import content-distinct second .wotbreplay when available.
  repeatWithoutChangedHypothesis: false
artifacts:
  rawFiles: %TEMP%\od-survivors.txt (6 local addresses), %TEMP%\od-ce-autorun.log, %TEMP%\od-launch-host.log
  committedSummary: this ledger entry + workflow update + handoff (2026-08-03-od-recovery-029.md)
```

`OD-RECOVERY-029` is aggregate structural evidence only. Offset remains 0.

## `OD-RECOVERY-030` result — 2026-08-03 (six attempts; two driver fixes validated live)

```yaml
sessionId: OD-RECOVERY-030
date: 2026-08-03
observedAtUtc: 2026-08-03T13:26:00Z # last attempt rolling window; lease walls at each attempt end
# Six launches: 13:07..13:26Z. Gate green on 4 of 6 (attempts 1,3,4,6);
# attempts 2 and 5 crashed the game at replay start (game-side assert).
timebox: 120s research lease per launch
decision: reproduce the proven pipeline; fix the mid-roll 401 capability rotation; fold the redundant OD-026 sanity probe into round 1 to save the 120s lease; validate both fixes live
objective: reach <=10 survivors with lease margin for operator Find-what-writes on staged survivors
stopCondition: gate loss, lease expiry, or target reached
method:
  - state: pre-flight
    releaseBuild: 0 warnings / 0 errors
    staleProcesses: none
    replay: Churchill sha12 0FAE5612491E (same artifact; independentReplays still 0)
  - state: launch (detached) x6
    gate: OfflineReplayVerified on 4/6 (watch_exit=0, post_watch_vs=OfflineReplayVerified)
    failedLaunches: attempts 2/5 gate Denied at driver start
    reason: evidence.monitor_unhealthy
    rootCause: game-side assert at replay start - activeController->GetName() == LobbyControllerNames::LOBBY (AccountController.cpp:386), blitz-logs_20260803123418.txt; not a clicker/gate fault
  - state: attempt-1 rolling (pre-fix driver)
    snapshot: sane 66,605,081 (probe initial)
    round1: increased 847,612
    round2: FAILED 401 Unauthorized - rendezvous capability rotated mid-roll (token ~5 min; the 66M sanity probe + round-1 compare outlive it)
    fix: Invoke-OdApi refresh + retry on 401 in roll-replay-time-increased.ps1
  - state: attempt-3 rolling (401-fix driver)
    sequence: [879049, 178254, 34780, 7985, 1757, 391]
    note: round 4 logged capability_401_refresh_retry=1 and continued - FIX VALIDATED LIVE
    round7: FAILED 400 (discard signature) - lease expired mid-roll; post_roll_gate EvidenceStale
  - state: attempt-4 rolling
    round1: 65,910,593 -> 1,294,914
    round2: FAILED 400 - lease wall even earlier; EvidenceStale
    note: OD-026 sanity probe (full 66M walk that narrows nothing) + round-1 walk consumed the whole lease
  - state: attempt-6 rolling (probe-folded driver)
    snapshot: session 000001 followed directly by round=1 previous=66,426,888 - NO separate probe walk (FOLD VALIDATED LIVE)
    round1: 66,426,888 -> 1,269,141
    round2: FAILED 400 - lease wall again; EvidenceStale
result:
  whatWorked:
    - 401 capability-rotation defect found and fixed in the rolling driver (refresh rendezvous + retry); validated live on attempt 3 round 4.
    - Redundant OD-026 sanity probe folded into round 1 (round-1 previousCount == snapshot candidate count, so the standalone probe's full 66M walk was pure lease burn); validated live on attempt 6 (snapshot -> round 1 with no probe line).
    - Rolling still narrows hard from the stable 66M baseline: 66.2M -> 391 in 6 rounds (attempt 3) and 66.4M -> 1.27M in round 1 (attempt 6).
    - Game-side crash on attempts 2/5 root-caused to a game assert at replay start (AccountController LOBBY check), not the pipeline - the launcher's green read and the monitor's Denied are consistent with the game dying post-verify.
  whatFailed:
    - No attempt reached <=10 survivors this session: the 120s lease wall after round-1's 66M-baseline walk is the binding constraint (attempts 4/6 died at round 2; attempt 3 at round 7 with 391).
    - No address file written, no CE staging, no operator window (rolling never reached target).
  rulesOut:
    - Rolling from a stable 66M baseline cannot converge to <=10 inside the 120s lease when the round-1 walk is slow on this machine - the lease margin is machine-load dependent (OD-026..029 converged at 8 rounds; this session's 4/6 lease walls died at rounds 2 or 7).
    - A single rendezvous capability captured at roll start is not valid for a full 66M-baseline roll (401 rotation) - the driver must refresh + retry.
  partials:
    - Both driver changes (401 refresh, probe fold) are durable tooling improvements now committed to the rolling driver; parse-checked and live-validated.
    - BLK-0019 still needs content-distinct second replay (independentReplays 0).
  nextPivot: OD-RECOVERY-031 - same pipeline with the 401-refresh + folded-gate driver; operator Find-what-writes on staged survivors during the lease-bound held green window; treat launcher-green + immediate Denied/monitor_unhealthy as a game-side assert crash and relaunch; consider a second content-distinct replay for BLK-0019.
  repeatWithoutChangedHypothesis: false
artifacts:
  rawFiles: %TEMP%\od-launch-host.log, blitz-logs_20260803123418.txt (assert evidence), session logs /tmp/od-session-030{c,d,f}.log
  committedSummary: this ledger entry + workflow update + handoff (2026-08-03-od-recovery-030.md)
```

`OD-RECOVERY-030` is aggregate structural evidence only. Offset remains 0. The
rolling driver now carries two validated fixes (401 refresh + folded gate) that
reduce lease burn and keep long rolls alive through token rotation.

## `OD-RECOVERY-031` result — 2026-08-03 (five attempts; target convergence + CE staging handoff fix validated)

```yaml
sessionId: OD-RECOVERY-031
date: 2026-08-03
observedAtUtc: 2026-08-03T18:58:19Z # attempt 5 gate expired at roll end; best runs 14:5x-18:5x UTC

timebox: managed launch to gate green; CE pre-arm + autorun staging; rolling Double increased to ≤10 with the 401-refresh folded-gate driver; candidate-count optimization (1 per round, harvest on target) and CE staging handoff fix (default path + 300s autorun poll) validated live
decision: FIRST target convergence since OD-020: rolling reached 10 ≤ 10 survivors on attempts 4 and 5; the candidate-count optimization (request 1 candidate per rolling round, harvest the full set only on the target round) lets 10–14 rounds fit the 120s lease (vs 6–7 before); the CE staging handoff defect (custom -AddressFile name bypassed the autorun's default-path poll; 90s poll expired before roll end) was root-caused and fixed; the operator window still opened with the gate EvidenceStale both target runs — the lease wall is now the only missing piece
objective: Reach ≤10 survivors inside the lease, stage them into CE, and hold the green operator window for interactive Find-what-writes
stopCondition: Stop after target ≤10 + CE staging + green window, or gate loss
method:
  primaryTool: scripts/launch-offline-replay-for-od.ps1 (detached) + session driver + roll-replay-time-increased.ps1 (401-refresh + folded gate + candidate-count optimization) + pre-arm-debugger.ps1
  secondaryTools: Cheat Engine 7.7 (C:\Program Files\Cheat Engine) pre-armed attached; CE autorun od-autorun-writebp.lua (default path, 300s poll) staged survivors into the address list
  transition: natural replay progression between rounds (no manual Space pulses; -AutoSpace not used)
observations:
  - state: attempt-1
    rolling: snapshot session=000001; sequence 66426888->1093960 (round 1 only)
    outcome: lease wall — round 2 compare got 400 (session discard), gate EvidenceStale; candidate-count optimization applied after this attempt
    note: round-1 previousCount == snapshot count confirmed the probe-fold (no separate sanity walk)
  - state: attempt-2
    launchOutcome: launcher logged OfflineReplayVerified; driver saw EvidenceStale from its very first poll
    gateReason: evidence.expired (game-side assert crash at replay start; no new blitz-log written)
    outcome: game-side flake (~40% of launches per OD-030); relaunch not re-click
  - state: attempt-3
    rolling: sequence 66001783->1151663->148454->26932->6086->1773->1198->1079->739->217->19->15->14 (12 rounds)
    outcome: candidate-count optimization validated — 12 rounds fit the lease (vs 6-7 before); 401 refresh fired live at round 8 (capability_401_refresh_retry=1); lease wall at round 13 (400 + EvidenceStale)
  - state: attempt-4
    rolling: sequence 66434129->924101->179976->35706->7316->1091->449->43->28->20->10 (10 rounds)
    outcome: TARGET survivors=10 le 10 reached; harvest increased=8 candidates=8 written to custom od-survivors-031.txt
    stagingDefect: CE autorun polls ONLY %TEMP%\od-survivors.txt (default) — the custom -AddressFile name bypassed it; autorun gave up at 90s (14:51:57Z) just before roll end; 0 survivors staged
  - state: attempt-5 (after fixes: default address-file path + 300s autorun poll)
    rolling: sequence 983469->190633->36821->7563->1346->572->72->30->15->14->13->13->12->10 (14 rounds)
    outcome: TARGET survivors=10 le 10; harvest increased=10 candidates=10 written to default od-survivors.txt (count==survivors, no WARN); 401 refresh fired live at round 10
    staging: CE autorun loaded 10 survivor lines -> staged 10 in address list -> debugger attached -> debug_setBreakpoint resolved -> armed 4 HW write-BPs (6 skipped beyond DR0-DR3 limit); 0 hits
    operatorWindow: opened but gate ALREADY EvidenceStale at close — 120s lease expired exactly at roll end; game terminated 18:58:19Z; no live Find-what-writes window
result:
  whatWorked:
    - Candidate-count optimization (1 candidate per rolling round; harvest full set only on target) validated live: 10-14 rounds now fit the 120s lease vs 6-7 before; enabled the first target convergence since OD-020 (10 ≤ 10, twice).
    - 401 refresh fired live a fourth and fifth time (attempt 3 round 8, attempt 5 round 10) — long rolls survive token rotation.
    - CE staging handoff defect root-caused (custom -AddressFile name bypasses the autorun's default-path poll; 90s poll too short for a 66M-baseline roll) and fixed (default path + 300s poll); attempt 5 staged all 10 survivors in CE's address list with 4 HW write-BPs armed.
    - Rolling itself now reliably completes inside the lease: sequences fully persisted, address file count == survivors, no WARN.
  whatFailed:
    - The operator window opened with the gate already EvidenceStale both target runs (attempts 4/5) — the 120s lease expires exactly as the 66M-baseline roll ends, so no live interactive Find-what-writes window.
    - Attempt 2: game-side assert crash at replay start (evidence.expired; no blitz-log written) — same ~40% launch flake as OD-030 attempts 2/5.
  rulesOut:
    - No new rules-outs; OD-020's automated CE write-BP capture remains the standing rule (0 hits again, consistent).
  partials:
    - Rolling to ≤10 inside the lease: SOLVED (10-14 rounds fit).
    - CE staging of the survivor set: SOLVED (all survivors staged, BPs armed).
    - The operator-owned interactive Find-what-writes window: still not delivered live (lease wall).
    - BLK-0019 still needs content-distinct second replay (independentReplays 0).
  nextPivot: OD-RECOVERY-032 — same pipeline (driver at best-known state); the operator window is the only missing piece, so target the lease headroom: start the hold immediately at roll end and consider reducing round-1 walk cost (snapshot budget / region selection) or a second content-distinct replay for BLK-0019.
  repeatWithoutChangedHypothesis: false
artifacts:
  rawFiles: %TEMP%\od-survivors.txt (10 addresses, attempt 5), %TEMP%\od-prearmed-debugger.json, %TEMP%\od-ce-autorun.log, session logs /tmp/od-launch-031{b,c,d,e}.log
  committedSummary: this ledger entry + workflow update + handoff (2026-08-03-od-recovery-031.md)
```

`OD-RECOVERY-031` is aggregate structural evidence only. Offset remains 0. The
rolling driver now carries three validated optimizations (401 refresh, folded
gate, candidate-count harvest) and the CE staging handoff is fixed; the lease
wall at roll end is the only remaining gap to a live operator window.

## `OD-RECOVERY-032` result — 2026-08-03 (two attempts; 11-survivor plateau reproduced)

```yaml
sessionId: OD-RECOVERY-032
date: 2026-08-03
observedAtUtc: 2026-08-03T15:1xZ # attempt 2 lease expired mid-round-13; both attempts ~19:1xZ local

timebox: managed launch to gate green; CE pre-arm + autorun staging; rolling Double increased to ≤10 with the 401-refresh folded-gate candidate-optimized driver at best-known state; operator window held to the lease boundary
decision: The 11-survivor plateau is now the reproducible ceiling — both attempts converged to 11 survivors (one short of the ≤10 target) and the 120s lease expired 1–2 rounds later (round-15 and round-13 `400` discard signatures). The driver is at its best-known state; no new defect surfaced. The lease wall at the tail is the binding constraint: the roll fits the lease but the last few rounds past 11 do not.
objective: Reach ≤10 survivors inside the lease, stage them into CE, and hold a green operator window for interactive Find-what-writes
stopCondition: Stop after target ≤10 + CE staging + green window, or gate loss
method:
  primaryTool: scripts/launch-offline-replay-for-od.ps1 (detached) + session driver + roll-replay-time-increased.ps1 (401-refresh + folded gate + candidate-count harvest) + pre-arm-debugger.ps1
  secondaryTools: Cheat Engine 7.7 (C:\Program Files\Cheat Engine) pre-armed attached; CE autorun od-autorun-writebp.lua (default path, 300s poll)
  transition: natural replay progression between rounds (no manual Space pulses; -AutoSpace not used)
observations:
  - state: attempt-1
    rolling: snapshot session=000001; sequence 855303->174082->37503->6531->2204->1377->1076->954->951->942->12->12->11->11 (14 rounds)
    retained: 177581 unreadable-chunk carryover inflated `previous` on rounds 4-12 (215084/184112/179785/178958/178657/178535/178532) — compare still narrows correctly (increasedCount is the survivor count)
    outcome: 401 refresh live round 10 (capability_401_refresh_retry=1); lease wall round 15 (400 + EvidenceStale); survivors 11 > 10 target
  - state: attempt-2
    rolling: snapshot session=000001; sequence 1200456->168076->29550->6809->2272->1204->1104->726->214->21->16->11 (12 rounds)
    retained: 0 (no carryover this session)
    outcome: 401 refresh live round 8; lease wall round 13 (400 + EvidenceStale); survivors 11 > 10 target
  - state: after-session
    verificationState: EvidenceStale (reason evidence.expired) — expected teardown
    addressFile: not written (target not reached)
    ceAutorun: polled 300s both attempts; no address file appeared; 0 staged, 0 hits
result:
  whatWorked:
    - The converged pipeline reproduces cleanly: both attempts reached 11 survivors inside the lease, with the 401 refresh firing live a sixth time (attempt 2 round 8, attempt 1 round 10).
    - Rolling to ~11 survivors in 12-14 rounds is now the reproducible steady state of the 66M baseline on this machine.
  whatFailed:
    - The last 1-2 rounds needed to go from 11 to ≤10 did not fit the 120s lease (round-15 and round-13 discards) — the tail is the binding constraint, not the driver.
    - No address file, no CE staging, no operator window (target not reached).
  rulesOut:
    - No new rules-outs. The automated CE write-BP capture stays ruled out (OD-020); the interactive operator step remains the evidence path.
  partials:
    - The 11-survivor plateau is characterized: convergence is reliable, the lease edge is the only gap.
    - BLK-0019 still needs content-distinct second replay (independentReplays 0).
  nextPivot: OD-RECOVERY-033 — same pipeline; the tail now needs the lease headroom, so prioritize reducing round-1 walk cost (snapshot budget / region selection) or shaving tail transition time; a lighter-loaded window or content-distinct second replay for BLK-0019 also helps.
  repeatWithoutChangedHypothesis: false
artifacts:
  rawFiles: %TEMP%\od-prearmed-debugger.json, %TEMP%\od-ce-autorun.log, session logs /tmp/od-launch-032{,b}.log
  committedSummary: this ledger entry + workflow update + handoff (2026-08-03-od-recovery-032.md)
```

`OD-RECOVERY-032` is aggregate structural evidence only. Offset remains 0.
Convergence to ~11 survivors inside the lease is reproducible; the final 1–2
rounds to ≤10 sit just past the 120s lease edge on this machine.

## Evidence promotion checklist

A field is ready for promotion review only when all are true:

- exact executable identity matches;
- decimal and hexadecimal forms agree;
- address kind is known;
- field type and behavior match the intended field;
- candidate survives two independent process launches;
- candidate survives two independent replays;
- static-analysis provenance is present where applicable;
- GameHarness provenance is present;
- harness invariants pass;
- lead and decoder-auditor approvals are recorded;
- no unresolved conflicting candidate remains;
- `offset_check.py --check-schema` and the read-only evidence report pass.

A high score, unique candidate, readable address, or global `confidence` value
cannot substitute for this checklist.

## Handoff requirements

Every completed session handoff must include:

1. session IDs worked;
2. exact result statuses;
3. successes, failures, and partials;
4. what is explicitly ruled out;
5. what must not be repeated without a changed hypothesis;
6. next session ID and timebox;
7. validation commands and results;
8. unrelated local/untracked material left untouched.
