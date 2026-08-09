# Offset-discovery ledger

**Current status (2026-08-09, OD-RECOVERY-075):** the exact-build module-rooted
resolver produced the first positive continuous player-position poll. Static
evidence proved that the eight-entry ring begins at helper `+0x08`, position is
record `+0x10`, and velocity is record `+0x28`. OD-073 had double-counted the
helper-relative position displacement and therefore read velocity. With that
corrected, one fresh `OfflineReplayVerified` process returned 24/24 resolved
positions, 24 distinct triples, 5 exact retained-trajectory matches, and 21/24
within three world units. Ground truth was bound to the exact canonical launch
artifact. Cross-replay continuous-polling repeatability remains blocked by a
pre-gate content-distinct replay launch failure (BLK-0026), so offset
publication remains unproved and no further live retry is recommended yet.

Last updated: 2026-08-08 (OD-RECOVERY-052/053: **FIRST DURABLE GAME-CODE FILL-SITE HIT (FRESH43)** — the dynamic source-arm caught `wotblitz.exe+0x7C39AB` writing the memcpy source buffer per-frame, with the CRT propagation copy `VCRUNTIME140.dll+0xE8AE` (`rep movsb`) landing on the armed member `0x22AB0F90` whose `esi` = `0x28FFCF10` exactly, and the SSE 4-float `movdqu` stage `VCRUNTIME140.dll+0xED49` refilling that source — the write chain is now: game fill → CRT vectorized stage → memcpy into tracked field; **Ghidra decode (hash-verified binary)**: the fill is `FUN_00bc3940`, a per-frame tank transform update gating on a **position triple at `[entity+0x3C]+0x1C/0x20/0x24`** and refilling a **4×4 world matrix at `[entity+0x3C]+0x60`** via 4× `MOVUPS` (candidate stable layout, no promotion); OD-RECOVERY-046/047/048/049/050/051: first durable module-mapped write-site hits (M2, FRESH37/38) — the C# guard-page interceptor captured real writes inside live battles and the write sites resolve to **VCRUNTIME140.dll+0xED69 / +0xE8AE**, proving the armed coordinate is a synchronized multi-copy field written by **CRT struct copies**, not a direct `movss`; the real game write is one level up (the memcpy source buffers, held in `esi`, which are **battle-scoped heap allocations** — cross-battle arming ruled out by live evidence; a same-window dynamic source-arm is the changed hypothesis, now implemented in the interceptor and offline-tested); two earlier runs were honest timing negatives (2.4× starved M1, 0.857<0.9 solo floor) that tuned the invocation to `-PlaybackSpeedEstimate 2.4 -StageMinBattleSeconds 30`; earlier milestone OD-RECOVERY-044: **live pipeline proven end-to-end + kernel-clock false positive identified** — rolling collapsed **861399→…→1 survivor in 16 rounds** (campaign record, was OD-020's 5) with the fixed harvest retry + plateau-stop logic; the single survivor was **`0x7FFE0010` = `KUSER_SHARED_DATA.SystemTime`**, the Windows shared kernel clock (FILETIME-style, +100ns ticks) — NOT the game field: the game died mid-roll from the documented replay-start flake, and the always-ticking kernel clock is the last 'increased' Double in a dying process; kernel writes never fire user-mode HW breakpoints so write-BP hits there are 0 by construction (explains the 0 hits — the mechanism was NOT the failure); driver hardened to drop the `0x7FFE0xxx` page from the address file + WARN; **x96dbg launcher bug found & fixed** — it stayed alive without spawning x32dbg (ShellExecute/state-machine brittleness), replaced by direct `x322dbg.exe` launch (game bitness known x86); x32dbg attach, `scriptload`+`scriptrun` injection, and arming all proven live (pid 45256); prior milestones OD-045/046-STATIC: offline delta-filter simulation ranked the **Double replayTime delta marker deterministic (pass-rate 1.0, survival 1.0/15 rounds)** — pilot order flip: delta pilot FIRST); **replay-start flake root-caused & fixed (2026-08-04)** — the ~50% OD-044 launch deaths were two defects: watch_offline's round-2 double-click + SW_RESTORE churn into the live replay HUD (become hidden → OnBackground, 2s/16s/42s deaths), and mid-battle `OfflineReplayEvidenceLifetime` expiry terminating the managed game (~60–105s exits, incl. the dying-process kernel-clock artifact); the click script now stops on the blitz-log `Start replay event` marker and the coordinator keeps verified authorization fresh via a liveness heartbeat while the process identity stays healthy (see `docs/operations/handoffs/2026-08-04-replay-start-flake-fix.md`)

**Current amendment (2026-08-08, OD-RECOVERY-063 through 068):** the live
instruction-first mechanism is proven. The first game had 164 threads, so the
fail-closed arming cap moved from 128 to 256. EBX+`0x1C/0x20/0x24` is
live-proven scale `(1,1,1)`, not position. EBX+`0x10/0x14/0x18` is a changing
local translation but did not exactly match any decoded participant. The one
permitted read of the statically confirmed composed translation at
EBX+`0x90/0x94/0x98` also returned an honest no-match: seven valid samples,
best coherent clock-aligned mean 10.850 / max 12.556 units, and 0/7 within 1
unit. The transform-fill branch is closed. Current policy is offline/static
discovery of the verified type-10 position packet's application path before
another live request. Historical entries below retain inferences later
corrected; they are not current policy.

**OD-RECOVERY-067 amendment (2026-08-08):** first-pass hash-bound static triage
found no direct type-10 consumer anchor. Across 526,935 executable functions,
displacement-layout matches were noisy matrix/copy/serializer shapes and the
top candidate was manually refuted. No same-base direct length-49/type-10
comparison exists, and eight apparent initialized dispatch-table rows were
MSVC exception metadata. Continue with a data-flow trace from the generic
replay event reader/framer; no live run is authorized.

**OD-RECOVERY-068 amendment (2026-08-08):** the historical community
`VehicleGameLogic +0x04 -> entity +0x68/+0x6C/+0x70` family was re-tested as a
hash-bound structural clue. The old module root remains refuted. The real
current-build `VehicleGameLogic` entity getter survives, but none of 17
getter-using virtual methods accesses the claimed position triple. The only
complete generic `+0x04`-handoff/triple match is matrix/pose copy code. Use the
getter as a data-flow anchor only; no live read is authorized.

**OD-RECOVERY-069 amendment (2026-08-08):** the data-flow-first pivot found the
exact replay-to-entity movement chain. Replay dispatch index 10 installs RVA
`0x00FE31C0`, whose reads match the verified 49-byte layout. Engine resolution
compares the packet ID to `[entity+0x1C]`, proving that member is the entity ID.
At RVA `0x022FA78D` (`F30F7E00`), `ESI` is the resolved entity and `EAX` points
to the packet-derived XYZ vector. A 40-check hash-bound verifier passes. This
is an entity-bound instruction-event candidate, not a stable offset. No live
run is authorized until a fixed two-source helper contract is synthetically
proven.

**OD-RECOVERY-070 amendment (2026-08-08):** the fixed two-source contract is
implemented and synthetically proven. Production now pins RVA `0x022FA78D`,
bytes `F30F7E00`, entity register `ESI`/ID `+0x1C`, and XYZ pointer register
`EAX`. The synthetic x86 target returned replay entity ID `4242` and four
changing finite XYZ samples; max-hit stop, non-Host parent rejection, cleanup,
and detach passed. Host output contains no raw addresses. One bounded live
equality test is admissible only after the full gate and fresh pinned publish.

**OD-RECOVERY-071 amendment (2026-08-08):** the first bounded live equality
test passed under `OfflineReplayVerified`. The fixed target returned 49 hits;
all had readable replay entity IDs and finite XYZ. Seven decoded vehicle
entities matched exactly at Float32 precision, including the replay viewpoint
entity. One zero-vector object had no decoded trajectory and was excluded.
This proves event-based player-position identity in one static window. Motion
freshness, same-decoded-clock identity, cross-replay repeatability, hardware
atomicity, a stable polling root, and offset promotion remain unproven.

**OD-RECOVERY-072 amendment (2026-08-08):** the unchanged event target repeated
on the other content-distinct replay and fresh managed process during movement.
All 64 bounded hits had readable IDs and finite XYZ. The replay viewpoint had
six distinct triples, including two exact matches retained by the downsampled
decoded trajectory. Twelve decoded entity IDs matched and 12 captured entities
changed. Motion freshness and cross-replay repeatability are proven for
event-based player-position reading. Same-clock/hardware-atomic proof, a stable
polling resolver, and offset promotion remain open. Stop unchanged live event
captures and pivot offline/static to the viewpoint entity/movement-filter ring.

**OD-RECOVERY-073 amendment (2026-08-08):** the stable current-build polling
family is statically pinned and implemented. A 47-check Ghidra verifier proves
the module root, `AppContext -> BWApp -> connection -> BWEntities` chain,
bounded cache/three-tree entity-ID lookup, AvatarFilter/helper identities, and
8-entry movement ring for the exact executable hash. The pure resolver caps
traversal/retries, double-collects the full current record, and revalidates the
root/entity/filter/helper chain. Production exposes only a decoded replay
entity ID; process, module, root, pointers, and layout remain server-owned and
offline-gated. Static/synthetic proof is complete. One bounded live OD-073 poll
is admissible after the full gate and fresh Host publish; no offset is promoted.

**OD-RECOVERY-074 amendment (2026-08-09):** live evidence corrected OD-073's
ownership path. The main `BWApp` connection returned `EntityNotFound` for
24/24 requests, and the inferred `AppContext+0x118` owner failed its vtable
gate for 24/24 requests. Static analysis then proved the module-rooted
`GameCore -> AppController -> SessionController -> AccountController ->
PlaybackController -> replay connection` chain. The corrected verifier passes
67/67 checks. Two corrected-root runs found the requested entity in the
primary replay map for 24/24 requests, then stopped at the movement-filter and
helper subtype gates. The exact filter family is proved; the observed vehicle
helper subtype is not layout-proved. Static follow-up names it as
`WGVehicleFilterHelper::vftable` at RVA `0x0325658C`, with the constructor
vtable store at RVA `0x010139F1` and a factory assignment to `filter+0x08`.
Do not broaden its allowlist or spend another live run until the position-store
slot and ring layout are proved offline.

**OD-RECOVERY-075 amendment (2026-08-09):** hash-bound analysis proves the
WGVehicle helper uses the same common store and readback family. The ring base
is helper `+0x08`; position is record `+0x10/+0x14/+0x18`; velocity is record
`+0x28/+0x2C/+0x30`; and the current index remains helper `+0x1C8`. The
verifier passes 82/82 checks, and focused tests distinguish position from
velocity and reject mismatched filter/helper subtype pairs. A first diagnostic
live run's 24/24 changing values were velocity, explaining their approximately
116-unit trajectory error. The corrected run resolved 24/24, moved on every
sample, matched retained decoded positions exactly 5 times, and placed 21/24
within three units. This is a strong one-replay/fresh-process continuous-poll
positive, not cross-replay publication proof. The unchanged content-distinct
repeat never reached the memory gate and is recorded as BLK-0026.

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
| Trusted next anchor | Exact-build replay owner: module RVA `0x04095C88` through `GameCore`, the controller chain, replay `BWServerConnection`, `BWEntities`, matched movement-filter/helper subtype, eight-entry ring at helper `+0x08`, and position at record `+0x10`; one live process agrees with decoded trajectory |
| Do not repeat | The same yaw neighborhood scan using `0x0317A810` without resolving its provenance; absolute image-only AOB of survivor pointer bytes without a changed encoding/root hypothesis (ruled out by OD-RECOVERY-007); absolute LE pointer AOB across private/all/image + align 1/8 without a changed encoding hypothesis (ruled out by OD-RECOVERY-008); truncated low-32 LE dword AOB of survivor absolutes without a changed encoding hypothesis (ruled out by OD-RECOVERY-009); automated CE `bptAccess`/`bptWrite` on Float position survivors without a field pivot or interactive debugger (0 RIP hits through OD-RECOVERY-011); CE write-BP alone on the single increased `replayTime` Double without interactive debugger or a second independent launch (0 RIP in OD-RECOVERY-012); treating file-association / `Invoke-Item` alone as the OD gate path (playback can succeed while Host stays `Denied` / `lifecycle_evidence_timeout` — amended 2026-08-02); reaching ≤10 RT survivors then starting interactive debugger after the fact under a 120s research lease loses the window to EvidenceStale (OD-RECOVERY-016) — pre-arm debugger / reserve lease margin; requiring the Watch Offline orange-dialog blob to vanish after `OfflineReplayVerified` (the replay HUD renders orange in that ROI, so `dialogGone` never sets, extra clicks hit in-game UI and kill the game — OD-RECOVERY-017) — trust the verified gate; reading compare `retainedCount` as the rolling survivor count (it is unreadable-chunk carryover only; survivors are `increasedCount` — OD-RECOVERY-017); automated CE Windows-debugger write-BPs (`debugProcess(1)` + `debug_setBreakpoint(addr, bptWrite, 1)`) on rolling Double survivors — zero RIP hits across OD-009/010/011 and OD-020/021/022 probes, so the operator-owned interactive Find-what-writes step is required, not a scripting gap to keep probing; rolling from a snapshot taken during the game load transition — the candidate set can be 66M+ (22–87× steady state), convergence cannot fit the 120s lease, and the resulting session discard surfaces as a confusing compare `400` (OD-RECOVERY-025 attempt 1) — wait for a clean steady-state snapshot before rolling; capturing the rendezvous capability once at roll start — the token rotates ~5 min and a 66M-baseline roll outlives it, so a mid-roll compare dies with a confusing 401 (OD-RECOVERY-030 attempt 1; fixed by refresh + retry in the rolling driver); running the separate full-walk sanity probe when round-1 `previousCount` reports the identical snapshot count — the probe's 66M-candidate walk wasted lease inside the 120s budget (OD-RECOVERY-030; gate folded into round 1); requesting `maxCandidates=500` (or any large harvest) on every rolling round when only the final target round's addresses are written — the big early compares (66M→1M) pay candidate serialization for nothing and cost lease; request 1 candidate per round and harvest the full set only on the target round (OD-RECOVERY-031 attempt 1 → fixed in driver, validated attempts 3–5: 10–14 rounds fit the lease vs 6–7 before); overriding the CE autorun's default survivor address-file path (`%TEMP%\od-survivors.txt`) with a custom `-AddressFile` — the autorun polls the default path only, so staged survivors silently never reach CE (OD-RECOVERY-031 attempt 4; use the default path so the staging handoff works); keeping the CE autorun poll window at 90s when a 66M-baseline roll outlives it — the file appears right at the 120s lease edge, so the poll must span the whole lease + margin (OD-RECOVERY-031 attempts 3/4; extended to 300s)  trusting a rolled-down survivor set landing on `0x7FFE0xxx` as a game-field hit — `KUSER_SHARED_DATA.SystemTime` (0x7FFE0010) is a FILETIME-style value that ticks every 100ns, so it survives every 'increased' compare after the game field stops ticking (replay tail / dying game); kernel writes to that page never fire user-mode hardware breakpoints, so a write-BP there returns 0 hits by construction (OD-RECOVERY-044 — drop the page from the address file + WARN, now in the driver); treating the x96dbg launcher as unusable for pre-arm — **re-verified 2026-08-04: in a healthy gated session `release\x96dbg.exe -p <pid>` headlessly dispatched cleanly to `x32\x32dbg.exe -p <pid>` (x86 build attached to wotblitz pid 50724, launcher exited, window title confirmed `wotblitz.exe - PID: 50724`) — the OD-RECOVERY-044 linger was environmental (game already dying that session), not a launcher defect; direct `x32\x32dbg.exe` launch remains the pipeline choice for determinism (removes the ShellExecute/elevation surface entirely), not because the launcher is broken (OD-044 launcher re-verification) |
| Next planned session | Execute the BLK-0026 diagnosis plan (`docs/operations/blk-0026-diagnosis-plan.md`, 2026-08-09 — hypothesis (b) refuted offline; no live testing yet) without memory access, then permit exactly one unchanged bounded `OfflineReplayVerified` poll on the content-distinct replay within 20 minutes of a fresh import; do not change the resolver, broaden reads, or promote the offset table. |

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
| `OD-RECOVERY-033` | 2026-08-03 | Ride convergence variance for a ≤10 target with the best-known driver | background launch helper + session driver (2 attempts) + pre-arm-debugger.ps1 + 401-refresh folded-gate candidate-optimized rolling Double increased + default-path autorun staging (300s poll) | `Partial` | Attempt 1: rolling **66.3M→…→12 in 14 rounds** (`863861→…→12`; 401 refresh live round 10) then lease wall round 15 (`400` + `EvidenceStale`); attempt 2: rolling **66.6M→…→17 in 13 rounds** (`938727→…→17`; 401 refresh live round 9) then lease wall round 14 (`400` + `EvidenceStale`) | Convergence range **11–17 survivors across OD-032/033 (4 attempts), always past the 120s lease edge**; no ≤10 target, no address file, no operator window; `independentReplays` still 0; no RIP/root |
| `OD-RECOVERY-034` | 2026-08-03 | Changed hypothesis: two-phase tail transition shave (1s pulse below 200 survivors) to fit more tail rounds in the lease | background launch helper + session driver (1 attempt) + pre-arm-debugger.ps1 + 401-refresh folded-gate candidate-optimized rolling Double increased + **two-phase tail shave** + default-path autorun staging (300s poll) | `Partial` | Rolling **66.6M→…→12 in 18 rounds** (`906685→…→12`; rounds 13–18 at `pulse_window=1s` — tail shave live; 401 refresh round 10; **18 rounds fit the lease vs 14–15 before**); count **plateaued at 12 through rounds 15–18** — the last survivors tick every frame and survive even 1s pulses → **the tail is value-bound, not round-bound**; round 19 `400` + `EvidenceStale` | **Rolling alone cannot disambiguate high-frequency tickers — interactive Find-what-writes is required regardless of lease headroom**; no ≤10 target, no address file, no operator window; `independentReplays` still 0; no RIP/root |
| `OD-RECOVERY-035` | 2026-08-03 | Changed hypothesis: snapshot byte-budget passthrough (MaxBytes) to shrink the round-1 66M walk | background launch helper + session driver (2 attempts) + pre-arm-debugger.ps1 + 401-refresh folded-gate candidate-optimized rolling Double increased + two-phase tail shave + **MaxBytes snapshot budget** + `MaxRounds` passthrough + default-path autorun staging (300s poll) | `Partial` | Attempt 1 (`-SnapshotMaxBytes 402653184`): round-1 **previous=823,484 vs 66M unbounded — 80× walk reduction**; rolled **823484→…→12 in 22 rounds** with **`rolling_exit=0` — full round budget completed, first non-lease-wall exit since OD-031** (tail shave rounds 8–22 at 1s; 401 refresh live round 15; plateaued 12 from round 15; round-22 address file wrote 1 candidate — WARN mismatch, target not reached); attempt 2 (`-MaxRounds 40`): round-1 **previous=50,061,014** (budget bound ~50M), rolled **750293→…→17 in 16 rounds**, plateaued 17 rounds 16–20, lease wall round 21 (`400` + `EvidenceStale`; 401 refresh live round 10) | **Budget hypothesis validated — rounds are cheap and can complete inside the lease, but the value-bound plateau (12/17) persists**; no ≤10 target staged; `independentReplays` still 0; no RIP/root |
| `OD-RECOVERY-036` | 2026-08-03 | Operator-window run: TARGET ≤10 + CE staging with the proven budget invocation | background launch helper + session driver (4 attempts) + pre-arm-debugger.ps1 + 401-refresh folded-gate candidate-optimized rolling Double increased + two-phase tail shave + **MaxBytes budget** + `MaxRounds 40` + default-path autorun staging (300s poll) | `Partial` | Attempts 1–3: game-side flake — attempt 1 died during soft-focus settle (`game_window_lost_during_soft_focus_settle`); attempts 2/3 launcher-green but gate flipped `Denied`/`evidence.monitor_unhealthy` before the driver's first poll (no new blitz-log; 3-of-4 flake rate this session); attempt 4: **TARGET 10 ≤ 10 in 17 rounds** (`772551→…→10`; round-1 previous=39,126,523 budget-bound; 401 refresh live round 6; `rolling_exit=0`) with harvest **9 candidates → CE autorun loaded 9, staged 9 in address list, armed 4 HW write-BPs, 20s capture 0 hits** | **Full staging handoff proven end-to-end (9 staged + 4 armed)**; operator window opened but gate was `EvidenceStale` at close (lease expired during the 240s hold); no interactive Find-what-writes result; `independentReplays` still 0; no RIP/root |
| `OD-RECOVERY-037` | 2026-08-03 | Launch-reliability diagnosis: 4 attempts with the proven invocation, all game-side flakes | background launch helper + session driver (4 attempts) + pre-arm-debugger.ps1 + proven invocation `-SnapshotMaxBytes 402653184 -MaxRounds 40 -HoldAfterRollSeconds 240` | `Partial` | Attempt 1: launcher-green but gate flipped `Denied`/`evidence.monitor_unhealthy` before the driver's first poll (no new blitz-log); attempt 2: gate verified then `window_lost_final`/`watch_exit=1` — blitz log shows the game was in the **login/lobby phase** (`LoginHandler::fail status=68 Invalid password` + `ConnectionManager::onLogOnFailure`) then `Window::HandleVisibilityChanged: become hidden` + `GameCore::OnBackground`; attempt 3: reached `BattleController::LoadGameScene ends` then window lost; attempt 4: launcher-green, driver first poll `Denied` (blitz log again shows lobby login-failure signature) | **NEW crash signature — game dies in the login/lobby phase, never reaching the replay (7 of last 8 launches across OD-036/037 flaked)**; no roll ran; `independentReplays` still 0; no RIP/root |
| `OD-RECOVERY-038` | 2026-08-03 | Diagnose the lobby-login failure; run the proven invocation with the corrected diagnosis + hardened 401-refresh | background launch helper + session driver (4 attempts) + pre-arm-debugger.ps1 + proven invocation `-SnapshotMaxBytes 402653184 -MaxRounds 40 -HoldAfterRollSeconds 240` + **401-refresh hardening (settle + 4 retries)** | `Partial` | **Diagnosis corrected: `Invalid password status=68` is a red herring** — it appears in *every* blitz log since 14:48 game-time, *including the OD-036 SUCCESS run* (15:03 log: login failure + full replay to `onLeaveWorld`), and every log reaches `Start replay event` + `LoadGameScene`; real death = `become hidden` + `GameCore::OnBackground` ~2s after scene load (known replay-start flake, elevated). Attempt 1: launcher-green then gate flipped `Denied` (blitz 15:47:51: LoadGameScene ends 20:48:18, become hidden 20:48:19); attempt 2: `no_game_window_while_waiting`; attempt 3: gate green, roll **39.2M→…→65 in 9 rounds** then **first-ever 401-refresh failure** (round 9, `capability_401_refresh_retry=1` then `FAILED_unexpected 401`) — mechanism never failed in 13 prior validations → driver hardened (750ms settle + 4 retries); attempt 4: gate green, round-8 401 **absorbed by the hardened refresh** (validated live), roll **39M→…→11 in 20 rounds** (plateau 11 rounds 16–20 — value-bound, 1 above target) then lease wall round 21 (`400` + `EvidenceStale`) | **Lobby-login hypothesis ruled out (red herring); 401-refresh hardening validated live; value-bound 11-survivor plateau again 1 above target**; no ≤10 target, no address file, no operator-window staging; `independentReplays` still 0; no RIP/root |
| `OD-039-STATIC` | 2026-08-03 | Batch static root analysis (Track A): RTTI-walk all 9 chain classes; verify store-slot xref candidates as static roots | `tools/find-static-roots.py` batch (`--rtti` class list + `--chain` candidate list) against the hash-bound 11.19.0.10 binary | `Partial` | **Two runtime-written static root candidates confirmed**: `0x03FA0C74` (**9 .text refs**) and `0x03FA012C` (**6 refs**) — both `.data` zero-on-disk non-reloc = code-initialized global signature; RTTI TypeDescriptors located for every major chain class (VehicleGameLogicComponent family 11 mangled, AppContextImpl 3, ScreensFlow 1, GameScene 5, GameCamera* 6, VehicleDescr 20, Vehicle*Component 809 mangled hits, Context 244); `EntityList` has **0 RTTI hits → plain struct, xref-discovery only**; `0x03E7DF28` (AvatarContextBattle td) has 0 .text refs → not a root | No static chain root proven; `Vehicle`/`Context` COL slots all `plausible=False` (signature mismatch) → no statically-reachable vtable root for those classes; live confirmation still required; no RIP/root |
| `OD-040-STATIC` | 2026-08-03 | Deepen the two confirmed static root candidates: reference-site instruction decode (offline Find-what-writes) + typed member-offset dump | `tools/find-static-roots.py` new `--refs`/`--fields` modes against the hash-bound 11.19.0.10 binary | `Partial` | **`0x03FA0C74` = 9 refs (5 load + 4 store)** — `mov eax,[abs]`/`mov [abs],eax` (A1/A3) + `mov r32,[m+disp32]`/`mov [m+disp32],r32` (8B/89, ecx) across 3 code clusters (`0x0005D5xx`, `0x006E52xx`, `0x006F18xx`); **`0x03FA012C` = 6 refs (2 load + 4 store)** — all A1/A3 across 2 clusters (`0x005F7Bxx`, `0x006017xx`); **mixed load+store mix = read-write code-initialized globals, not dead data** — both are written by runtime code (offline equivalent of Find-what-writes); field dump shows `0x03FA0C74` neighbors hold .text ptr `0x00404064` and .rdata ptr `0x037F3054` | Still no root→field mapping; no RIP/root; live probe of the two candidates still required; `independentReplays` still 0 |
| `OD-041-STATIC` | 2026-08-03 | Identify the repeating .rdata ptr `0x037F3054`; classify the two 'root candidates' as record-family members, not singletons | `tools/find-static-roots.py` investigation + new `--record-map BASE,STRIDE,COUNT` mode against the hash-bound 11.19.0.10 binary | `Partial` | **`0x037F3054` is an MSVC EH handler/funclet table** — 0xFFFFFFFF sentinel + interleaved {state, code-ptr} pairs into `.text` rva `0x02CF0D70`+ (`lea r,[ebp+..]; jmp` thunks), **113 FuncInfo magics (0x19930522) within ±0x2000**, referenced from **48,609 4-aligned `.data` slots (0 unaligned)** = massively-shared infrastructure pointer; **`0x03FA0C74` = +0x04 member of record[1] in a repeating 0x50-byte `.data` record family** `{0x00404064 (.text member-fn), <runtime slot>, 0x037F3054 (.rdata), 0, …}` based at `0x03FA0C20`; `--record-map 0x03FA0C20,0x50,10` → 10 records, 9 runtime slots (+0x04, +0x08, +0x0C, +0x10, +0x18, +0x28, +0x30, +0x44, +0x4C; +0x04 zero in 4/10) | **Both 'root candidates' reclassified as EH/handler record members — not gameplay state**; live-probing them as singleton roots ruled out without a changed hypothesis; no RIP/root; `independentReplays` still 0 |
| `OD-042-STATIC` | 2026-08-03 | Vtable discovery + type_info vftable identification; Vehicle-family TypeDescriptor xref (negative) | `tools/find-static-roots.py` new `--vtables` mode (COL-chain name resolution fix: MSVC x86 mangled name is **inline at td+8**, not a pointer) against the hash-bound 11.19.0.10 binary | `Partial` | **`0x037F3054` re-identified as the shared RTTI `type_info` vftable** — every TypeDescriptor's pVFTable points at it (confirmed td@0x03DB5120 `pVFTable == 0x037F3054`), explaining the 48,609 `.data` refs; **`--vtables` names 17,133 of 18,721 vtables** (was 0 before the inline-name fix) including `GameScene` 0x0319D3C4 (26 slots, **0 .data roots** = honest negative for vtable-singleton path), `BaseContext` 0x03197044 / `RootContext` 0x03197068, Vehicle component family (VehicleMovementFilterComponent 0x03199C68, VehicleFashionComponent 0x03199FF8); **Vehicle-family TypeDescriptor xref = 0 .text refs / 0 slots** — RTTI name→root path exhausted | vtable-singleton path gives no chain-class root (GameScene vtable has no .data holder); `independentReplays` still 0; no RIP/root |
| `OD-043-STATIC` | 2026-08-03 | Class→vtable→root query + decode of the 0x03B7E198 vtable-pointer array | `tools/find-static-roots.py` new `--vtable-root` + `--table-map` modes against the hash-bound 11.19.0.10 binary | `Partial` | **`0x03B7E198` = DAVA `AnyFn` invoker vtable table**: 34 entries, modal stride 0x2C (44-byte vtables, 24 at that pitch + 9 irregular), 24 named `StaticAnyFnInvoker<lambda>` vtables (bind `TankComponent`/`AimingPointComponent`/`Scene`/`Entity` = component event subscriptions), all 24 sharing dispatcher fn `0x002C4550` (24/24), each with **exactly 1 .data root = its array entry** (internally-closed set, 0 external roots); 3 .text refs incl. **runtime patch `mov [0x03B7E198],imm32` at 0x03104FAB repointing entry[0]**; `--vtable-root GameScene` → 0x0319D3C4/COL 0x034A89F0/0 roots/12 slots; `--vtable-root TankComponent` → 19 matches all AnyFn invokers roots=1 | **invoker table is dispatch infrastructure, NOT a gameplay root** (component event subscriptions route through AnyFn); GameScene/TankComponent vtables have no singleton .data holder; `independentReplays` still 0; no RIP/root |
| `OD-044-STATIC` | 2026-08-03 | Delta-compare pilot prep: replay-derived target/tolerance extractor + Float scan support | `scripts/python/replay-delta-extractor.py` (new) + `roll-replay-time-increased.ps1` `-ValueKind Double\|Float` (new) against the decoded 11.19.0 Dead Rail session | `Partial` | Delta-compare pipeline verified wired end-to-end (ApiContracts `DeltaTarget`/`DeltaTolerance` → Host.Web → Coordinator → `MemoryScanEngine.PassesDelta`, Float/Double/Int, tested); extractor emits dense per-window displacement stats via sliding-window interpolation: **2,779 measurements for the most-moving participant, median 2D displacement 0.6935 m/4s, p90 3.1927, max 6.1432** → recommended `-CompareMode delta -DeltaTarget 0.6935 -DeltaTolerance 2.4992 -ValueKind Float` (position) and `-DeltaTarget 4.0` (Double replayTime); driver `-ValueKind` keeps default `Double` (valueSize/alignment follow kind) — PS parse verified | Track C2 pilot is now command-ready with a statically-derived marker; still needs the operator-present live run to measure survivor collapse; `independentReplays` still 0; no RIP/root |
| `OD-045-STATIC` | 2026-08-03 | Offline delta-filter simulation: predict survivor collapse per marker before spending lease | `replay-delta-extractor.py --simulate` (new) on the 11.19.0 Dead Rail session (4s window, 2,779 measurements) | `Partial` | **Simulation of `PassesDelta` as a rolling filter**: replayTime delta marker pass-rate **1.0 at every tolerance (0.2/0.4/1.0/2.0/4.0s), survival 1.0 even over 15 rounds** — deterministic (replayTime advances exactly window×speed per window), the true field never sheds → **ideal filter**; position-delta marker is **bursty → HOLLOW collapse**: pass-rate 0.8996 at recommended tol 2.4992 → survival 0.347/10 rounds, 0.205/15 (the tank stands still much of the replay, so the true position field sheds like a decoy); speed marker passes 1.0 only at tol ≥8×target (not selective); unit variants for the unknown in-memory replayTime Double: 4.0s / 4000ms / 4,000,000ticks | **Pilot order flip: run the Double replayTime delta pilot FIRST** (`-ValueKind Double -CompareMode delta -DeltaTarget 4.0 -DeltaTolerance 0.4`); Float position pilot deferred or re-targeted to a movement-only window; `independentReplays` still 0; no RIP/root |
| `OD-046-STATIC` | 2026-08-03 | Movement-only windows + HP damage-delta markers for the live pilot | `replay-delta-extractor.py --movement` + `--hp-delta --victim-entity <id>` (new) on the 11.19.0 Dead Rail session | `Partial` | **Movement segmentation**: 32.3% of the replay is moving (891/2,756 1s windows @ 0.5 m/s); moving-window 1s displacement median 0.712 m, p90 0.992, max 1.489 — the Float position pilot should scan a movement-only span (e.g. the 32% moving window) where the position marker is selective; **HP damage-delta**: kind-3 events carry `{attackerEntityId, victimEntityId, damage}`; the player (entity 2549401) took **0 damage** this replay (marker needs a victim that gets hit — conditional); victim 2549395: 260 windows, 5 hit windows, 2,618 total damage (512/819/462/314/511), pass-rate 0.9808 @ tol 0, survival 0.907/0.824/0.747 over 5/10/15 rounds — sparse-but-exact, a supporting marker only | **replayTime delta remains the primary live filter**; Float position re-targeted to movement-only span; HP-delta is conditional on a damaged victim; fixed truncated module-docstring closer (line 47 `""`→`"""`) that broke both new modes; `independentReplays` still 0; no RIP/root |
| `OD-RECOVERY-044` | 2026-08-04 | Live pipeline end-to-end: gate green → pre-arm x32dbg -> rolling ≤10 -> automated write-trace for {rip} evidence | launch helper + session driver + pre-arm-debugger.ps1 (x32dbg direct) + fixed rolling Double increased (harvest retry, plateau-stop) + x64dbg-write-trace.ps1 -AutoWriteTrace | `Partial` | **Full pipeline mechanically proven end-to-end**: rolling collapsed **861399→…→1 survivor in 16 rounds** (campaign record; was OD-020's 5); harvest retry + plateau-stop fixes live-proven; **x96dbg launcher bug found** (stayed alive, never spawned x32dbg) -> replaced by direct `x322dbg.exe` launch, which attached (pid 45256) and the write-trace injected `scriptload`+`scriptrun` + armed 1 HW BP; the single survivor was **`0x7FFE0010` = `KUSER_SHARED_DATA.SystemTime`** (kernel clock false positive — game died mid-roll from the replay-start flake; always-ticking clock outlives the game field) → driver now drops the `0x7FFE0xxx` page + WARN | No {rip} evidence captured (lease expired before hits; hits would have been 0 by construction - kernel writes never fire user-mode HW BPs); no RIP/root; `independentReplays` still 0 |
| `OD-RECOVERY-046` | 2026-08-07 | FRESH37 live run: first durable module-mapped write-site hit from the C# guard-page interceptor (M2) | od-049-autoloop.ps1 (`-PlaybackSpeedEstimate 2.4 -StageMinBattleSeconds 30`, timing tuned from two honest negatives) → C# WriteInterceptor auto-trace, durable `.capture.json` + `.family.json` | `Partial` | **First durable module-mapped write-site evidence**: `family-hit`, **4 real writes inside the live battle**, `windowValuesChanged=true`, 135-module attach-time snapshot; write sites decode to **VCRUNTIME140.dll+0xED69 (4-dword copy loop `mov [edi],edx; add edi,4; sub ecx,1; jnz`) and +0xE8AE (`rep movsb`)** — the armed x-coordinate is a synchronized multi-copy field written by **CRT struct copies**, not a direct `movss`; captured values (-0.0003 / 245.35 / -124.00) are coherent world coordinates | **The real game write is one level up — the memcpy SOURCE buffers** (captured `esi` 0x2C2A9E18 / 0x2C2AB2E0 / 0x2C2AD880); no offset promoted, kind stays `heap-dynamic`, M3 repeatability not yet met; `independentReplays` still 0 |
| `OD-RECOVERY-047` | 2026-08-07 | FRESH38 live round: reproduce the FRESH37 hit; test arming the memcpy source addresses next battle (same-process two-pass) | od-049-autoloop.ps1 proven invocation + `-KeepGame`; phase A reproduced the hit, phase B (arm `esi` sources in battle 2) | `Partial` | **Hit reproduced a second time** (`family-hit`, 2 real writes, VCRUNTIME+0xED69, `valuesChanged=true`, 135-module durable capture) — M3 repeatability step 1; **phase B ruled out by live evidence**: the `esi` copy sources are **battle-scoped heap allocations** (FRESH37: 0x2C2A9E18… vs FRESH38: **0x2DDBB418 / 0x3EBEB878** — different address space, and ~0x110000 apart within one window), not process-stable buffers — arming captured sources in a later battle is invalid for the same reason cross-process arming was | **Changed hypothesis required to catch the real write site**: arm the source page IN THE SAME WINDOW it is discovered (on first hit, read `esi` and dynamically arm that page) — implemented in `tools/WriteInterceptor` (FRESH38+ source-arm, `-ArmSourceOnFirstHit`, offline mechanism test passing); no offset promoted; `independentReplays` still 0 |
| `OD-RECOVERY-048` | 2026-08-07 | FRESH40 live round: first live attempt at the FRESH39 dynamic source-arm (proven invocation + `-ArmSourceOnFirstHit`) | od-049-autoloop.ps1 `-AttachSmokeOnFirstRound -StageViewpointOnly -PlaybackSpeedEstimate 2.4 -StageMinBattleSeconds 30 -AutoTraceSeconds 25 -ArmSourceOnFirstHit`; offline diagnosis vs od-048 source + FRESH37/38 control runs | `NoSignal` | **Honest negative — 4th consecutive sub-0.9**: verdict `evidence-strong`, 526 addresses scored (130 viewpoint-only), 7364 samples, 20 strong survivors, top score **0.857 (6/7) < 0.9 solo floor** → `family_mapping_failed` → auto-trace SKIPPED → **source-arm never armed** (requires a family hit first); M2 stop rule held; game cleaned up (launch_exit=0) | **Root cause diagnosed offline, not a driver defect**: `measuredPlaybackSpeed=None` is the expected estimate fallback (FRESH38's 0.933 hit ran the identical estimate-only path with the same fire-by-deadline stop) and staging tolerance stayed 0.001 exact-match; the 0.857 cap is **correlation score quantization from a thinner sample grid** (526 addresses / 7364 samples vs the hit run's 598 / 8970) — changed hypothesis for FRESH41: sharpen the grid (`-ReadIntervalSeconds 1.0 -MaxReadRounds 120`); no offset promoted; `independentReplays` still 0 |
| `OD-RECOVERY-049` | 2026-08-07 | FRESH41 live round: test the sample-grid changed hypothesis (`-ReadIntervalSeconds 1.0 -MaxReadRounds 120`) + `-ArmSourceOnFirstHit` | od-049-autoloop.ps1 sample-grid fix + proven invocation + `-ArmSourceOnFirstHit`; FRESH37/38/39/40 controls | `NoSignal` | **Changed hypothesis tested and REFUTED**: the finer grid worked mechanically (27 rounds vs 15–16, 589 addresses, **15,314 samples ≈ 2× FRESH40's 7,364**) but the top score held at **0.846 (11/13)**, not the predicted 14/16 → no family ≥ 0.9 → auto-trace SKIPPED → **source-arm still never armed**; top z survivors carry ~45s ambiguity bands (6→51.5/52s) vs FRESH37's hit at 0.933 with a **6.5s band** — the ~0.85 cap is the axis's inherent run-to-run correlation variance, not a sampling artifact | **Ledger rule applied — no further live rounds on this replay with the current scoring setup**: next moves are offline (aggregate the 5-round score distribution to test whether the 0.9 floor sits inside the natural range ~0.77–0.93; band-weighted emission selector — prefer tight-band over score-max, FRESH37's 6.5s band vs today's 45s; `independentReplays` still 0 / BLK-0019 unchanged) |
| `OD-RECOVERY-051` | 2026-08-07 | FRESH42 live round: first test of the band-weighted emission floor + `-ArmSourceOnFirstHit` | od-049-autoloop.ps1 proven invocation + `-ArmSourceOnFirstHit` (band-weighted floor = new od-048 defaults) | `NoSignal` | **Floor behaved CORRECTLY; emission not yet observed live**: top survivors were x@0.800 with 0s bands — all below the 0.85 tight-band floor, refused exactly as designed (no floor defect); FRESH42 drew the low tail of the round-top distribution (0.80–0.933 observed), so no candidate ≥ 0.85 existed to emit → auto-trace SKIPPED → source-arm unexercised | **One more roll (FRESH43) is within the changed-hypothesis warrant**: the floor has not yet had a live emission to observe; offline replay proves it converts ~4/8 rounds including FRESH40's 0.857 x/3s class — completing the first live test of the new approach, not repeating an exhausted one |
| `OD-RECOVERY-052` | 2026-08-07 | FRESH43 live round: second roll of the band-weighted floor (within warrant) + `-ArmSourceOnFirstHit` — **FIRST DURABLE GAME-CODE FILL-SITE HIT** | od-049-autoloop.ps1 proven invocation + band-weighted floor (od-048 defaults) + `-ArmSourceOnFirstHit`; source-arm = dynamic esi-page arming on first hit (FRESH38 design) | `Hit` | **The source-arm CAUGHT the game's fill site**: family emitted at **0.933** (`0x23A4C490` z + `0x22AB0F90` x, band 50.5s, span 177.9/46.8), auto-trace invoked, `source_arm ON`, **6 hits → verdict `family-hit`** (`hit_members=1`, `values_changed=true`): (1) `VCRUNTIME140.dll+0xE8AE` `rep movsb` wrote **into armed member `0x22AB0F90`** with `esi`=**`0x28FFCF10` exactly** (the copy source); (2) `VCRUNTIME140.dll+0xED49` `movdqu` SSE-stored **4 floats into that source buffer** (`0x28FFCF10..1C`: 0.000457/2.574/−0.112/−0.112 = x,y,z,+1); (3) **`wotblitz.exe+0x7C39AB` (game code, base 0x00C30000, RVA math re-verified) wrote a float into the second armed source page `0x2C5C8A90`** — the per-frame fill site; write path = game fills staging buffer → CRT vectorized copy stages 4-float chunks → `memcpy` propagates into the tracked position field | **Write-chain identified**: the member address is a **copy destination**; the *fill* happens at `wotblitz.exe+0x7C39AB` + `VCRUNTIME140.dll+0xED49`; source buffer holds x,y,z consecutively (MOVDQU writes 4 floats) — next: Ghidra-disassemble `wotblitz.exe+0x7C39AB` to identify the function and trace the staging-buffer pointer chain, then evaluate reading x/y/z from the source (or promoting the destination if M3 repeatability met); `independentReplays` still 0 |
| `OD-RECOVERY-053` | 2026-08-07 | Offline: Ghidra decode of the FRESH43 write-site chain — **the fill is a per-frame tank transform update** | `tools/ghidra-scripts/DumpWriteSite.java` + `DumpChain.java` (headless `-noanalysis`, hash-verified binary 1cda5c31…) | `Complete` (offline) | **Write site = `FUN_00bc3940` (RVA 0x7C3940)**: per-frame entity transform update (called from entity-list `FUN_00bb9b30` when `[entity+0x20] & 0x800`); object = `[entity+0x3C]` via getter `FUN_00d29ea0`; gates on **position triple `[obj+0x1C/0x20/0x24]`**; refills **4×4 world matrix `[obj+0x60..0x9C]`** via 4× `MOVUPS` (the exact FRESH43 SSE pattern) composed by `FUN_00729570` (matrix multiply) from quaternion→matrix `FUN_00d1a0f0` + basis normalizer `FUN_00d155c0`; `MOVDQU` values were a rotation/scale row, not world coords | **Candidate stable layout: `x/y/z = [entity+0x3C] + 0x1C/0x20/0x24`, world matrix `+0x60`** — no promotion (M3 needs live-read matching + cross-battle + `independentReplays`, BLK-0019); next: root the entity container to a global, interceptor-arm the position triple and match to decoded ground truth, import a second replay |
| `OD-RECOVERY-055` | 2026-08-08 | Offline correction: singleton root **REFUTED** — `DAT_043f516C` is the DAVA logger, not the BattleController | Dump of singleton builders `FUN_008dfaa0` (TagLoggerExternalImpl, 4B) + `FUN_008dfb10` (0x70B alloc → `FUN_008e13e0`) + constructor `FUN_008e13e0` (TagLoggerInstanceImpl vftable + SkipAssert/BreakAssert/ContinueAssert handlers) | `Refuted` | **`FUN_008ee9f0`/`DAT_043f516C` is the DAVA thread-safe logger singleton getter** (vftable `DAVA::TagLoggerInstanceImpl`, assert-handler members); the "BattleResources::LoadGameScene" / "…/Battle/BattleResources.cpp" strings are **log messages** written through it, not object identity proof; the `[X+0x4]` AvatarController hop (thunk `FUN_016673a0`, vtable-dispatched, no static caller) cannot be rooted → **no stable global root for the entity container** | **OD-RECOVERY-053 candidate layout survives** (`[entity+0x3C]+0x1C/20/24` position, `+0x60` matrix — from `chain-disasm.txt` disasm); call-site member offsets `[X+0x154]`/`[X+0x8]`/`[X+0x88]`/`[X+0x30]` remain valid raw facts (object-type labels inferred from log strings, unproven); promotion needs the M3 live-read path (interceptor-arm position triple, match to decoded ground truth) — no static root required; no offset promoted; `independentReplays` still 0 |
| `OD-RECOVERY-056` | 2026-08-08 | Offline retroactive M3 check: FRESH43 captured floats vs decoded ground truth — **fill site writes matrix rows, CONFIRMED** | FRESH43 capture `.data/od-048-autotrace-20260807-123621.json.capture.json` vs decoded DB `treader.db` session 019fb86c-… (sha 59c3b92e…, Dead Rail, player mrkool1138, 2784 samples) | `Complete` (offline) | **Captured floats are NOT world coordinates**: SSE quad 0.000457/2.574/−0.112/−0.112 and game fill 0.008281542 are ~3 orders of magnitude below player ground-truth ranges (x [−75.4,64.4], y [24.0,34.8], z [−169.3,237.2]) → **matrix rows, not positions** — independently confirms OD-RECOVERY-053's inference | **M3 sharpened**: arming the fill site/SSE staging captures matrix rows, useless for position; M3 must arm `[obj+0x1C..0x24]` (position triple) and read the member address at write time; expected M3 sanity band x/z ∈ [−170,+240], y ∈ [24,35]; no offset promoted; `independentReplays` still 0 |
| `OD-RECOVERY-058` | 2026-08-08 | Offline: **BLK-0019 unblocked — second independent 11.19.0 replay found and decoded** (user replay folder) | `AppData/Local/wotblitz/DAVAProject/replays` inventory: 18 saved 11.19.0 battles probed via `WotBTreader.ReplayInspector` (gameVersion/map/battleTimeUtc) + sha256 distinctness check + CLI import | `Complete` (offline) | **A second independently recorded 11.19.0 replay exists**: `20260802_1615__mrkool1138_GB08_Churchill_I_8565111466734423.wotbreplay` (sha `0fae5612…`, savanna/Oasis Palms, battle 2026-08-02T21:15:07, 1 045 525 B) is distinct from FRESH43's Dead Rail replay (sha `59c3b92e…`); the other 16 folder files are re-recordings of the medvedkovo 2026-07-29 battle (same payload) | **BLK-0019 resolution path confirmed**: same player (mrkool1138) + same tank (GB08_Churchill_I) in both battles — ideal for cross-battle M3 validation; decoded as session `019fdff7-8dcf-7426-8547-9fb8cc3eb07b` (14 participants, 26 822 positions, world-coordinate envelope x[−254,198] y[33,42] z[−248,186] in the expected M3 sanity band); **next live round (FRESH44+) can run the correlate + interceptor on this second replay for cross-battle repeatability + `independentReplays`** |
| `OD-RECOVERY-057` | 2026-08-08 | Offline: FRESH43 arm-snapshot anomaly — member addresses are **transient multi-copy buffers**, not stable position fields | Interceptor arm snapshots (x=274.0174 @ 0x22AB0F90, z=296.2679 @ 0x23A4C490, capture `od-048-autotrace-20260807-123621`) vs whole-session decoded coordinate envelope (max \|coordinate\| = 251) + correlate 0.933 (14/15, tol 0.001) ground-truth provider check (`SqliteTrajectoryGroundTruthProvider.cs` reads same `raw_x/raw_y/raw_z`) | `Complete` (offline) | **Anomaly resolved**: correlate-time reads matched decoded world coordinates within 0.001 (real M3-machinery evidence, same ground-truth columns), but arm-time snapshots ~1s later read 274/296 — **outside the entire battle envelope** (no participant ever reaches them; tank max speed 14.8 m/s ⇒ cannot move ~300 units in 1s) → the member addresses are **transient multi-copy buffers** (FRESH37 class): they hold position data only during the staging window, then get reused for unrelated matrix/pool contents | **M3 stable-read NOT satisfied** — trace-time arm snapshot does not reliably return the player position; the correlate match (14/15 exact) is stronger evidence than any trace-time read; next-hypothesis: arm the position triple IMMEDIATELY at correlate completion (<100ms gap) or use the correlate reads themselves as the M3 read; promotion framing shifts to correlate match + cross-battle repeatability + `independentReplays` (BLK-0019); no offset promoted |
| `OD-RECOVERY-050` | 2026-08-07 | Offline score-distribution analysis (76 survivors across FRESH37/38/39/40/41) + band-weighted emission implemented | `.data/score-distribution-analysis.py` aggregate + od-048 emission harness (4 cases) + end-to-end replay vs real FRESH38/40/41 reports | `Complete` (offline) | **The 0.9 floor is band-blind, not wrong**: both hits were tight-band x (0.5s/6.5s at 0.933); the refused class splits into tight-band x@0.857/3s (FRESH40, same class as hits — should emit) vs wide-band z@0.846–0.857/45–65s (should refuse); band width is the discriminator (score quantizes coarsely: 6/7 = 0.857, 14/15 = 0.933) | **Band-weighted floor implemented in BOTH gates** (od-048 solo emission + family-usable + write-trace Test-FamilyScored, threaded via `TightBandMinScore`/`TightBandMaxSeconds`): tight-band (≤10s) clears at 0.85, wide-band needs strict 0.9; selection order now span → band asc → score; validated offline: FRESH40's 0.857 x/3s would now emit, FRESH38 still emits (0.933 + 2 tight x-siblings), FRESH41's wide z still refused — **2/8 → 4/8 rounds emit, all tight-band x class**; FRESH42 live round is the changed hypothesis |
| `OD-RECOVERY-054` | 2026-08-08 | Offline: entity container walked toward a stable global — **root claim later REFUTED (see OD-RECOVERY-055)** | `DumpCallers.java` + `DumpWindow.java` (headless `-noanalysis`, hash-verified binary 1cda5c31…); caller BFS from `FUN_0165247c` (RVA 0x125247C) + window dumps at 0x165DB56 / 0x165192F / 0x12673B0 + singleton builders `FUN_008dfaa0`/`FUN_008dfb10`/`FUN_008e13e0` | `Refuted` (root) / `Partial` (offsets) | **Proposed root `DAT_043f516C` (RVA 0x3FF516C) = BattleController singleton — WRONG**: dumping the singleton builders proves it is the **DAVA logger** (`TagLoggerInstanceImpl` + SkipAssert/BreakAssert/ContinueAssert handlers); `FUN_008ee9f0` is the logger singleton getter and the "BattleResources::LoadGameScene" strings are log messages; the AvatarController hop (`[X+0x4]` thunk `FUN_016673a0`) is vtable-dispatched with no static caller → **no stable global root exists for the entity container** (battle-scoped heap). Call-site member offsets `[X+0x154]`/`[X+0x8]`/`[X+0x88]`/`[X+0x30]` remain valid disasm facts; **OD-RECOVERY-053 candidate layout stands** (`[entity+0x3C]+0x1C/20/24` position, `+0x60` matrix) | **Honest negative**: promotion does NOT need a static root — M3 live-read path arms the position triple via the interceptor on a family hit and matches captured values to decoded ground truth; no offset promoted; `independentReplays` still 0 (BLK-0019) |
| `OD-RECOVERY-059` | 2026-08-08 | FRESH44 live cross-battle M3 correlation on the second independent replay | `invoke-fresh44-crossbattle.ps1` → managed offline launch → OD-048 correlate → C# guard-page interceptor | `Partial` (M3 correlation repeatability) | **BLK-0019 resolved**: a fresh `OfflineReplayVerified` launch on the second content-distinct replay repeated the viewpoint-position phenomenon; selected x family scored **0.9375 (15/16)**, 21 sampled series were preserved, and several survivors matched 16/16 | No stable module RVA, pointer chain, or same-clock live position-triple read; 25-second trace stayed live with 3 pages armed but captured 0 hits/write sites; no offset promoted |
| `OD-RECOVERY-060` | 2026-08-08 | Formal read-only promotion review after FRESH44 | Promotion checklist + workflow + schema + current offset table + FRESH43/FRESH44 aggregate evidence | `Blocked` (publication only) | M3 cross-battle repeatability is satisfied for the transient viewpoint-position correlation phenomenon; the second independent replay/fresh process and negative heap-copy classification are established | No single module-relative candidate, stable resolver, same-clock `[obj+0x1C/0x20/0x24]` read, all-axis field identity, candidate-bound invariants/provenance, conflict resolution, or approvals; `playerPosition*` correctly remain `0` / `Unknown` |
| `OD-RECOVERY-061` | 2026-08-08 | FRESH45 live immediate position-triple read | Managed `OfflineReplayVerified` launch → viewpoint correlate → one immediate Float32 batch read for four `candidate-0x1C` layout hypotheses; delayed trace disabled | `NoSignal` (layout hypothesis) / `Partial` (instrumentation) | All 12 requested floats were readable, but none of the four candidates produced a complete XYZ match; the immediate-read choreography and fail-closed reporting worked | Honest negative for those four candidate-derived layouts at that sampled instant only; 102.2 ms completion gap, no proven object base/atomicity/same clock/stable resolver, and no offset promoted |
| `OD-RECOVERY-062` | 2026-08-08 | Implement the instruction-first player-position pivot | Coordinator-authorized x86 execute-breakpoint helper + parent-bound pipe capability + server-pinned target + privacy-safe Host/GameHarness surface + synthetic owned target | `Complete` (implementation) / `Partial` (discovery) | Separate no-legacy helper; Host EXE+DLL and helper identity manifest pinned; post-attach event reverified; synthetic changing XYZ plus max-hit/timeout cleanup and non-pinned-parent rejection pass; scan-first repeats stopped | No live game hit, viewpoint identity, decoded-clock match, stable resolver, publication candidate, promotion count, or offset change |
| `OD-RECOVERY-063` | 2026-08-08 | First live instruction-snapshot attempt and fail-closed diagnosis | Fresh helper publish + synthetic pass + managed `OfflineReplayVerified` launch + 5-second capture | `Partial` (instrumentation) | Initial error collapsed to `helper_failed`; privacy-safe diagnostic projection was added and the fresh diagnostic attempt returned `thread_bound_or_target_invalid`; aggregate process inspection measured 164 game threads | No target values accepted; cleanup/detach succeeded; all live processes stopped; 128-thread cap was insufficient and no evidence claim was made |
| `OD-RECOVERY-064` | 2026-08-08 | Coverage-corrected live read of the originally claimed EBX+`0x1C/0x20/0x24` triple | Same pinned instruction, cap raised to 256, one managed offline 5-second capture | `Complete` (classification) / `NoSignal` (position) | Fingerprint and cleanup proven; 7 finite hits from one opaque object, all exactly `(1,1,1)` | Live evidence plus `FUN_00d1a0f0` proves this is scale, not position; the older static position label is refuted; no offset promoted |
| `OD-RECOVERY-065` | 2026-08-08 | Static-corrected live read of EBX+`0x10/0x14/0x18` local translation | Hash-verified `FUN_00d1a0f0` layout + one managed offline 5-second capture + offline decoded-position comparison | `Partial` / `No exact participant match` | Fingerprint and cleanup proven; 7 changing finite hits from one opaque object; all 48 axis/sign conventions tested against 26,822 decoded positions; best time-agnostic viewpoint fit mean 7.374 / max 10.272 units | Register/local-translation provenance proven, but viewpoint and decoded-clock identity remain false; next target is composed world-matrix translation EBX+`0x90/0x94/0x98`; no offset promoted |
| `OD-RECOVERY-066` | 2026-08-08 | One live read of the composed world-matrix translation at EBX+`0x90/0x94/0x98` | Fresh helper publish + synthetic pass + one managed offline 5-second capture + UTC-aligned comparison against every decoded participant | `NoSignal` (player identity) / `Complete` (bounded hypothesis) | Fingerprint and cleanup proven; 7 finite hits from one opaque object; all 48 axis/sign mappings and 0.5x-8x playback tested; best coherent absolute fit mean 10.850 / max 12.556 units with 0/7 within 1 unit | Matrix-row arithmetic remains statically proven, but sampled object/coordinate identity is not the decoded player; close transform-fill branch and pivot offline/static to the verified type-10 position-application path; no offset promoted |
| `OD-RECOVERY-067` | 2026-08-08 | Find a static type-10 replay position consumer/dispatch anchor | Three hash-bound Ghidra heuristics: local/framed displacement use, direct same-base length/type checks, and initialized dispatch-table relationships | `Partial` / `No direct consumer anchor` | 526,935 functions scanned; 3,457 local-layout and 190 framed-layout heuristic candidates classified as noisy; top match refuted as matrix/grid code; zero direct same-base length-49/type-10 pairs; eight table candidates refuted as MSVC EH metadata | Generic/table-driven framing or a recorder-side classification remains possible; next is a data-flow trace from the replay reader/framer into entity/physics state; no live run or offset promotion |
| `OD-RECOVERY-068` | 2026-08-08 | Re-test the UnknownCheats-derived Vehicle position layout as a current-build candidate family | Hash-bound Ghidra full-function structural scan, current `VehicleGameLogic` RTTI/vtable query, vtable-method dump, and manual decompilation | `NoSignal` (position layout) / `Partial` (entity anchor) | Root `0x03E91978` remains refuted; current VGL vtable RVA `0x0327DA50`; slot `+0x04` getter RVA `0x0031B560` returns `[this+0x04]`; 17/79 getter-using methods touch 23 entity offsets but not `0x68/0x6C/0x70`; sole complete generic chained match is matrix/pose copy code | Does not prove entity member semantics, replay type-10 dispatch, XYZ destination, stable root, or live identity; next is static convergence of reader/framer flow with the proven entity getter; no live run or offset promotion |
| `OD-RECOVERY-069` | 2026-08-08 | Trace the verified replay type-10 XYZ into a resolved engine entity | Hash-bound Ghidra replay/entity mapper, fixed 40-check semantic verifier, direct-call/vtable/instruction review | `CandidateFound` (entity-bound instruction event) / `Partial` (reliable player read) | Type-10 handler RVA `0x00FE31C0`; engine resolver proves `[entity+0x1C]` is packet entity ID; at RVA `0x022FA78D` bytes `F30F7E00`, `ESI` is resolved entity and `EAX` points to contiguous XYZ; downstream AvatarFilterHelper ring corroborates position semantics | Not a stable polling offset and no player identity/live equality yet; next is fixed two-source synthetic capture of `[ESI+0x1C]` plus 12 bytes at `EAX`, with no live run until review |
| `OD-RECOVERY-070` | 2026-08-08 | Implement and synthetically prove the fixed type-10 entity/XYZ capture | Re-pinned coordinator/helper policy, v2 private report, replay-entity-ID public projection, x86 owned target, focused tests | `Complete` (synthetic capture) / `CandidateFound` (live plan) | Exact `F30F7E00` hit; entity ID `4242`; 4 changing finite XYZ hits; max-hit stop; non-Host pipe caller rejected; cleanup/detach proven; no raw addresses on Host surface | No live equality or stable polling root yet; after full gate/fresh publish, one 5-second/64-hit positively verified offline capture may compare same-ID XYZ to decoded type-10 |
| `OD-RECOVERY-071` | 2026-08-08 | First live equality proof for the fixed type-10 entity/XYZ event | Fresh pinned publish + managed `OfflineReplayVerified` launch + one 5-second/64-hit capture + same-entity decoded trajectory comparison | `Hit` / `Partial` (reliable player read) | 49/49 ID+XYZ reads valid; 7 decoded vehicle entities matched exactly at Float32 precision; 1 exact match was the replay viewpoint; fingerprint/cleanup passed; 1 zero-vector non-trajectory object excluded | Player-position identity proven for one static window; no motion freshness/same clock/cross-replay repeatability/stable root/offset promotion; next repeat exact target during movement on other replay |
| `OD-RECOVERY-072` | 2026-08-08 | Prove motion freshness and cross-replay repeatability for the fixed player-position event | Other content-distinct replay + fresh managed process + unchanged 5-second/64-hit target + bounded decoded trajectory comparison | `Hit` / `Complete` (event-based player read) | 64/64 reads valid; 12 decoded IDs matched; 12 captured entities changed; viewpoint 6/6 distinct with 2 exact retained matches; fingerprint/cleanup passed | Reliable moving player-position event proven across both replays; no same clock/hardware atomicity/stable polling root/offset promotion; pivot offline to viewpoint resolver + movement-filter ring |
| `OD-RECOVERY-073` | 2026-08-08 | Freeze and implement a stable module-rooted entity-position polling family | 47-check hash-bound Ghidra verifier + bounded pure Core resolver + guarded exact-build coordinator/API + focused synthetic tests + aggregate-only runner | `Complete` (static/synthetic) / `CandidateFound` (live polling) | Exact module root/ownership/maps/filter/ring layout proven; caller supplies only decoded entity ID; revocation/build mismatch fail closed; no addresses/raw bytes in public result | No live polling result, cross-process stable-root repeatability, hardware atomicity, same decoded clock, or offset promotion yet |
| `OD-RECOVERY-074` | 2026-08-09 | Correct the replay-owned root and narrow the live polling blocker | 67-check hash-bound verifier + four bounded aggregate-only live checks + corrected pure resolver | `Partial` (continuous polling) / `CandidateFound` (replay entity root) | Corrected root found the requested entity in the primary replay map for 24/24 requests; filter family narrowed exactly; both earlier roots refuted safely | Observed vehicle-helper subtype/ring remains unproved; no position read, movement, decoded agreement, cross-process repeatability, or offset promotion |
| `OD-RECOVERY-075` | 2026-08-09 | Correct the position-ring layout and prove bounded continuous polling | 82-check hash-bound verifier + strict matched-subtype Core resolver + artifact-bound aggregate runner + one fresh verified replay | `Hit` / `Partial` (continuous polling) | Position/velocity fields separated; corrected poll resolved 24/24 with 24 distinct triples, 5 exact retained matches, and 21/24 within three units; exact launch-artifact binding proved | Cross-replay polling repeat blocked before the memory gate by BLK-0026; no hardware atomicity, same-clock proof, numeric-offset publication, or promotion |

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

## `OD-RECOVERY-033` result — 2026-08-03 (two attempts; convergence range widened)

```yaml
sessionId: OD-RECOVERY-033
date: 2026-08-03
observedAtUtc: 2026-08-03T15:2xZ # attempt 2 lease expired mid-round-14; both attempts ~19:2xZ local

timebox: managed launch to gate green; CE pre-arm + autorun staging; rolling Double increased with the best-known driver (401-refresh + folded gate + candidate-count harvest + default-path staging); two attempts to ride convergence variance for a ≤10 target
decision: No changed hypothesis was warranted for the driver — OD-031 reached 10 ≤ 10 twice on this same machine, so OD-032's 11-survivor runs are load variance, not a hard ceiling. Two more attempts under identical best-known parameters landed at 12 and 17 survivors, both past the 120s lease edge. The convergence range across OD-032/033 (4 attempts) is now 11–17 survivors — always just past the lease wall, never ≤10 with staging.
objective: Reach ≤10 survivors inside the lease, stage them into CE, and hold a green operator window for interactive Find-what-writes
stopCondition: Stop after target ≤10 + CE staging + green window, or gate loss
method:
  primaryTool: scripts/launch-offline-replay-for-od.ps1 (detached) + session driver + roll-replay-time-increased.ps1 (best-known state) + pre-arm-debugger.ps1
  secondaryTools: Cheat Engine 7.7 (C:\Program Files\Cheat Engine) pre-armed attached; CE autorun od-autorun-writebp.lua (default path, 300s poll)
  transition: natural replay progression between rounds (no manual Space pulses; -AutoSpace not used)
observations:
  - state: attempt-1
    rolling: snapshot session=000001; sequence 863861->178820->39254->7755->2962->1744->1226->946->943->642->242->32->12->12 (14 rounds)
    outcome: 401 refresh live round 10 (capability_401_refresh_retry=1); lease wall round 15 (400 + EvidenceStale); survivors 12 > 10 target
  - state: attempt-2
    rolling: snapshot session=000001; sequence 938727->213638->33463->6794->2458->1208->1024->978->669->21->21->18->17 (13 rounds)
    outcome: 401 refresh live round 9; lease wall round 14 (400 + EvidenceStale); survivors 17 > 10 target
  - state: after-session
    verificationState: EvidenceStale (reason evidence.expired) — expected teardown
    addressFile: not written (target not reached)
    ceAutorun: polled 300s both attempts; no address file appeared; 0 staged, 0 hits
result:
  whatWorked:
    - Pipeline reproduces cleanly with the best-known driver: 12 and 17 survivors in 13-14 rounds, 401 refresh live a seventh time (rounds 9/10).
    - No game-side crash, no gate anomalies this session — both launches ran the full roll to the lease edge.
  whatFailed:
    - Neither attempt reached ≤10; the lease expired during the round after convergence plateaued (rounds 15 and 14).
    - No address file, no CE staging, no operator window.
  rulesOut:
    - No new rules-outs. Automated CE write-BP capture stays ruled out (OD-020).
  partials:
    - Convergence range characterized: 11-17 survivors across OD-032/033 (4 attempts), always just past the 120s lease edge on current load.
    - BLK-0019 still needs content-distinct second replay (independentReplays 0).
  nextPivot: OD-RECOVERY-034 — the tail/lease-headroom work is now the confirmed priority, not more identical runs: reduce round-1 walk cost via a snapshot byte budget / region selection passthrough (MemoryScanEngine MaxBytes), or shave the tail transition; a lighter-loaded window or content-distinct second replay for BLK-0019 also helps.
  repeatWithoutChangedHypothesis: false
artifacts:
  rawFiles: %TEMP%\od-prearmed-debugger.json, %TEMP%\od-ce-autorun.log, session logs /tmp/od-launch-033{,b}.log
  committedSummary: this ledger entry + workflow update + handoff (2026-08-03-od-recovery-033.md)
```

`OD-RECOVERY-033` is aggregate structural evidence only. Offset remains 0.
Convergence lands at 11–17 survivors under current load; the 120s lease edge
is the standing blocker. Next session changes the hypothesis (lease headroom),
not the driver parameters.

## `OD-RECOVERY-034` result — 2026-08-03 (one attempt; tail-transition shave validated; tail proven value-bound)

```yaml
sessionId: OD-RECOVERY-034
date: 2026-08-03
observedAtUtc: 2026-08-03T15:3xZ # lease expired mid-round-19; ~19:3xZ local

timebox: managed launch to gate green; CE pre-arm + autorun staging; rolling Double increased with the 401-refresh folded-gate candidate-optimized driver PLUS the new two-phase tail transition (1s pulse below 200 survivors)
decision: The changed hypothesis (OD-034's mandate) was the two-phase tail shave: keep the full 3s pulse for the expensive early rounds, then drop to 1s once survivors fall to ≤200 so more tail rounds fit the fixed 120s lease. It worked as designed — 18 rounds fit the lease (vs 14-15 before) — but the survivor count plateaued at 12 through rounds 15-18: the last survivors tick every frame and survive even 1s pulses. This proves the tail is VALUE-bound, not round-bound: rolling alone cannot disambiguate the high-frequency tickers, so interactive Find-what-writes is required regardless of how much lease headroom we buy.
objective: Reach ≤10 survivors inside the lease via lease headroom, stage them into CE, and hold a green operator window
stopCondition: Stop after target ≤10 + CE staging + green window, or gate loss
method:
  primaryTool: scripts/launch-offline-replay-for-od.ps1 (detached) + session driver + roll-replay-time-increased.ps1 (best-known + two-phase tail shave) + pre-arm-debugger.ps1
  secondaryTools: Cheat Engine 7.7 (C:\Program Files\Cheat Engine) pre-armed attached; CE autorun od-autorun-writebp.lua (default path, 300s poll)
  transition: natural replay progression between rounds; tail rounds at 1s (TailThreshold=200, TailTransitionSeconds=1)
observations:
  - state: attempt-1
    rolling: snapshot session=000001; sequence 906685->180281->39341->8665->2023->1389->988->945->943->867->410->48->16->13->12->12->12->12 (18 rounds)
    tailShave: rounds 1-12 at pulse_window=3s; rounds 13-18 at pulse_window=1s (survivors 48 -> 16 -> 13 -> 12 -> 12 -> 12 -> 12)
    outcome: 18 rounds fit the lease (vs 14-15 before) — tail shave validated; 401 refresh live round 10; count plateaued at 12 (rounds 15-18) then round 19 400 + EvidenceStale
  - state: after-session
    verificationState: EvidenceStale (reason evidence.expired) — expected teardown
    addressFile: not written (target not reached)
    ceAutorun: polled 300s; no address file appeared; 0 staged, 0 hits
result:
  whatWorked:
    - Two-phase tail shave implemented (params TailThreshold=200, TailTransitionSeconds=1 in the rolling driver, passthrough in the session driver) and validated live: 18 rounds fit the 120s lease, 4 more than the 14-15 before.
    - 401 refresh fired live an eighth time (round 10).
    - The plateau diagnosis is now conclusive: the count stopped at 12 across four consecutive 1s-pulse rounds — the remaining survivors increase every frame.
  whatFailed:
    - The plateau at 12 proves the tail is value-bound: no amount of short-pulse rounds sheds the every-frame tickers. The interactive Find-what-writes step is the only disambiguator.
    - No ≤10 target, no address file, no CE staging, no operator window.
  rulesOut:
    - More lease-headroom by pulse shaving alone will not reach ≤10 (OD-034) — the tail survivors tick every frame.
  partials:
    - The driver now carries a fourth validated optimization (two-phase tail shave) that buys lease margin for future operator-present runs.
    - BLK-0019 still needs content-distinct second replay (independentReplays 0).
  nextPivot: OD-RECOVERY-035 — the interactive step is now required regardless of lease headroom: either operator presence at a green window (staged 12-survivor set is acceptable for Find-what-writes), a snapshot byte budget/region-selection passthrough to open the operator window with lease to spare, or a content-distinct second replay for BLK-0019.
  repeatWithoutChangedHypothesis: false
artifacts:
  rawFiles: %TEMP%\od-prearmed-debugger.json, %TEMP%\od-ce-autorun.log, session log /tmp/od-launch-034.log
  committedSummary: this ledger entry + workflow update + handoff (2026-08-03-od-recovery-034.md)
```

`OD-RECOVERY-034` is aggregate structural evidence only. Offset remains 0.
The tail-transition shave is a durable driver improvement that buys lease
margin, and the value-bound tail diagnosis redirects the campaign: rolling
alone cannot finish the job; the interactive step (or a content-distinct
second replay) is required.

## `OD-RECOVERY-035` result — 2026-08-03 (two attempts; snapshot byte-budget passthrough validated)

```yaml
sessionId: OD-RECOVERY-035
date: 2026-08-03
observedAtUtc: 2026-08-03T15:4xZ # attempt 2 lease expired mid-round-21; both attempts ~19:4xZ local

timebox: managed launch to gate green; CE pre-arm + autorun staging; rolling Double increased with the best-known driver PLUS the new snapshot byte-budget passthrough (OffsetSnapshotRequest MaxBytes)
decision: The changed hypothesis (OD-035's mandate) was the snapshot byte-budget passthrough: cap the RETAINED snapshot bytes so the round-1 66M walk shrinks and the roll finishes inside the lease, opening the operator window with lease to spare. Implemented `-SnapshotMaxBytes` (rolling driver) + `-MaxRounds` passthrough (session driver), 0 = engine ceiling (unchanged). Validated live on both attempts: round-1 previous dropped to 823K (attempt 1) / 50M (attempt 2) vs 66M unbounded, and attempt 1 completed the full 22-round budget with rolling_exit=0 — the first non-lease-wall exit since OD-031. The value-bound plateau persisted (12 / 17), so no ≤10 staging: rolling is now lease-viable, but the interactive Find-what-writes step remains the only disambiguator of the every-frame tickers.
objective: Shrink the round-1 walk so the roll finishes with lease to spare, then reach ≤10, stage into CE, and hold a green operator window
stopCondition: Stop after target ≤10 + CE staging + green window, or gate loss
method:
  primaryTool: scripts/launch-offline-replay-for-od.ps1 (detached) + session driver + roll-replay-time-increased.ps1 (401-refresh + folded gate + candidate-count harvest + two-phase tail shave + MaxBytes budget) + pre-arm-debugger.ps1
  secondaryTools: Cheat Engine 7.7 (C:\Program Files\Cheat Engine) pre-armed attached; CE autorun od-autorun-writebp.lua (default path, 300s poll)
  transition: natural replay progression; tail rounds at 1s; snapshot retained bytes capped at 384 MiB (402653184)
observations:
  - state: attempt-1 (-SnapshotMaxBytes 402653184)
    rolling: snapshot session=000001; sequence 823484->152041->30625->6400->637->246->71->51->46->45->45->45->45->45->12->12->12->12->12->12->12->12 (22 rounds)
    budget: round-1 previous=823,484 vs ~66M unbounded — 80x walk reduction; tail shave active rounds 8-22 (1s pulses below 200)
    outcome: rolling_exit=0 — full 22-round budget completed inside the lease (first non-lease-wall exit since OD-031); 401 refresh live round 15; plateaued at 12 from round 15; target not reached so no harvest — round-22 address file wrote 1 candidate (WARN_address_count_mismatch candidates=1 survivors=12, expected: lastCmp was the 1-candidate rolling compare)
  - state: attempt-2 (-SnapshotMaxBytes 402653184 -MaxRounds 40)
    rolling: snapshot session=000001; sequence 750293->132591->28888->6053->1743->1262->957->950->943->698->19->19->19->19->17->17->17->17->17 (20 rounds)
    budget: round-1 previous=50,061,014 — budget bound at ~50M (this session's snapshot retained less); tail shave rounds 12-20 at 1s
    outcome: 401 refresh live round 10; plateaued at 17 rounds 16-20; lease wall round 21 (400 + EvidenceStale)
  - state: after-session
    verificationState: EvidenceStale (reason evidence.expired) — expected teardown
    addressFile: not written (target not reached)
    ceAutorun: polled 300s both attempts; no address file appeared; 0 staged, 0 hits
result:
  whatWorked:
    - Snapshot byte-budget passthrough implemented and validated live: round-1 walked 823K/50M instead of 66M, and attempt 1 completed its full 22-round budget with rolling_exit=0 — the first non-lease-wall exit since OD-031. Rounds are now cheap.
    - 401 refresh fired live a ninth and tenth time (rounds 15 and 10).
    - The two-phase tail shave combined with the budget: 22 rounds at 1s pulses fit comfortably.
  whatFailed:
    - The value-bound plateau persisted at 12 (attempt 1) and 17 (attempt 2) — the every-frame tickers survive short pulses regardless of lease headroom, so ≤10 staging still eludes the roll.
    - No ≤10 target, no CE staging, no green operator window.
  rulesOut:
    - Lease headroom alone (budget + tail shave) cannot reach ≤10 — the value-bound plateau is now proven across lease-constrained (OD-032/033/034) and lease-viable (OD-035) rolls alike.
  partials:
    - The driver now carries five validated optimizations (401 refresh, folded gate, candidate-count harvest, two-phase tail shave, MaxBytes budget) + MaxRounds passthrough; a full round budget completes inside the lease.
    - BLK-0019 still needs content-distinct second replay (independentReplays 0).
  nextPivot: OD-RECOVERY-036 — the operator window can finally open green (roll completes inside the lease), so the priority is operator presence at the held green window for interactive Find-what-writes on a staged ~12-survivor set (-SnapshotMaxBytes 402653184 -MaxRounds 40 -HoldAfterRollSeconds 240); alternatively a content-distinct second replay for BLK-0019.
  repeatWithoutChangedHypothesis: false
artifacts:
  rawFiles: %TEMP%\od-prearmed-debugger.json, %TEMP%\od-ce-autorun.log, session logs /tmp/od-launch-035{,b}.log
  committedSummary: this ledger entry + workflow update + handoff (2026-08-03-od-recovery-035.md)
```

`OD-RECOVERY-035` is aggregate structural evidence only. Offset remains 0.
The MaxBytes budget passthrough is a durable driver improvement: a full
round budget now completes inside the 120s lease, so the operator window can
finally open green — the interactive step (or a content-distinct second
replay) is the remaining path.

## `OD-RECOVERY-036` result — 2026-08-03 (four attempts; TARGET ≤10 + full CE staging handoff)

```yaml
sessionId: OD-RECOVERY-036
date: 2026-08-03
observedAtUtc: 2026-08-03T20:06:26Z # attempt-4 CE capture completed 16:06:26 local; gate EvidenceStale at hold close

timebox: managed launch to gate green; CE pre-arm + autorun staging; rolling Double increased with the proven budget invocation; TARGET ≤10 + CE staging + held operator window
decision: THE STAGING HYPOTHESIS LANDED: attempt 4 reached TARGET 10 ≤ 10 in 17 rounds with the budget driver, harvested 9 candidates, and the CE autorun staged all 9 into CE's address list with 4 hardware write-BPs armed (the full staging handoff proven end-to-end). The operator window opened but the gate was already EvidenceStale at close (the 120s lease expired during the 240s hold) — so the interactive Find-what-writes result is still operator-owned. 3 of 4 launches hit the game-side flake this session (attempt 1 died in soft-focus settle; attempts 2/3 flipped to Denied/monitor_unhealthy before the driver's first poll), an unusually high rate.
objective: Reach ≤10 inside the lease, stage survivors into CE, and hold a green operator window for interactive Find-what-writes
stopCondition: Stop after target ≤10 + CE staging + green window, or gate loss
method:
  primaryTool: scripts/launch-offline-replay-for-od.ps1 (detached) + session driver + roll-replay-time-increased.ps1 (401-refresh + folded gate + candidate-count harvest + two-phase tail shave + MaxBytes budget) + pre-arm-debugger.ps1
  secondaryTools: Cheat Engine 7.7 (C:\Program Files\Cheat Engine) pre-armed attached; CE autorun od-autorun-writebp.lua (default path, 300s poll)
  transition: natural replay progression; tail rounds at 1s; snapshot retained bytes capped at 384 MiB (-SnapshotMaxBytes 402653184 -MaxRounds 40)
observations:
  - state: attempt-1
    outcome: game died during soft-focus settle (game_window_lost_during_soft_focus_settle) before the Watch Offline click; gate Denied/evidence.monitor_unhealthy — game-side flake
  - state: attempt-2
    launchOutcome: launcher logged OfflineReplayVerified (clicker needed round 2); driver saw Denied from first poll
    gateReason: evidence.monitor_unhealthy; no new blitz-log written — game-side flake
  - state: attempt-3
    launchOutcome: same signature as attempt 2 (round-2 click, launcher green, driver first poll Denied/monitor_unhealthy) — game-side flake, 3-of-4
  - state: attempt-4
    rolling: snapshot session=000001; sequence 772551->161108->34755->6524->2363->710->173->84->46->16->15->13->13->11->11->11->10 (17 rounds)
    budget: round-1 previous=39,126,523 (budget bound ~39M); tail shave rounds 8-17 at 1s
    outcome: TARGET survivors=10 le 10; harvest increased=9 candidates=9; rolling_exit=0; 401 refresh live round 6 (11th live validation)
    staging: CE autorun loaded 9 survivor lines -> staged 9 in address list (od-survivor-1..9) -> debugger attached -> debug_setBreakpoint resolved -> armed 4 HW write-BPs (5 skipped beyond DR0-DR3 limit) -> 20s capture (16:06:05-26) hits=0
    operatorWindow: opened for 240s but gate EvidenceStale at close (lease expired during the hold); no live Find-what-writes result
  - state: after-session
    verificationState: EvidenceStale (reason evidence.expired) — expected teardown
    addressFile: %TEMP%\od-survivors.txt count=9 survivors=9 (no WARN)
result:
  whatWorked:
    - FULL STAGING HANDOFF PROVEN END-TO-END: TARGET ≤10 with the budget driver, 9 candidates harvested to the default path, CE autorun staged all 9 in the address list and armed 4 HW write-BPs (0 hits in 20s, consistent with the OD-020 standing rule).
    - 401 refresh fired live an eleventh time (round 6) — long rolls survive token rotation.
    - The budget + tail shave combination converged to ≤10 in 17 rounds with rolling_exit=0 (round-1 walked 39M).
  whatFailed:
    - The operator window opened with the gate already EvidenceStale at close — the 120s lease expired during the 240s hold, so no live interactive Find-what-writes result (the staged set is ready for the operator, but the green window did not hold).
    - 3-of-4 launches hit the game-side flake (highest rate this campaign) — relaunch budget is required.
  rulesOut:
    - No new rules-outs. Automated CE write-BP capture stays ruled out (OD-020; 0 hits again).
  partials:
    - The full pipeline is now end-to-end: launch -> verify -> pre-arm -> roll ≤10 -> harvest -> CE staging with BPs armed.
    - BLK-0019 still needs content-distinct second replay (independentReplays 0).
  nextPivot: OD-RECOVERY-037 — the staged set + armed BPs are the deliverable; the operator-window interactive Find-what-writes step is the remaining evidence path. Run the proven invocation (-SnapshotMaxBytes 402653184 -MaxRounds 40 -HoldAfterRollSeconds 240) with the operator present during the held green window; a content-distinct second replay still closes BLK-0019.
  repeatWithoutChangedHypothesis: false
artifacts:
  rawFiles: %TEMP%\od-survivors.txt (9 addresses, attempt 4), %TEMP%\od-prearmed-debugger.json, %TEMP%\od-ce-autorun.log, session logs /tmp/od-launch-036{,b,c,d}.log
  committedSummary: this ledger entry + workflow update + handoff (2026-08-03-od-recovery-036.md)
```

`OD-RECOVERY-036` is aggregate structural evidence only. Offset remains 0.
The full staging handoff is proven end-to-end (TARGET ≤10, 9 survivors
staged, 4 HW write-BPs armed); the operator-window interactive
Find-what-writes step is the remaining evidence path.

## `OD-RECOVERY-037` result — 2026-08-03 (four attempts; launch-reliability diagnosis; NEW lobby-login crash signature)

```yaml
sessionId: OD-RECOVERY-037
date: 2026-08-03
observedAtUtc: 2026-08-03T20:29:24Z # attempt-4 blitz log ended (become hidden); all four attempts ~20:17-20:29Z

timebox: managed launch to gate green with the proven invocation; all four attempts flaked before a roll could start
decision: Launch reliability is now the binding constraint, not the roll. All 4 attempts flaked game-side (7 of the last 8 launches across OD-036/037), and the blitz logs show a NEW crash signature: the game never reaches the replay — it dies in the login/lobby phase (`LoginHandler::fail status=68 Invalid password` + `ConnectionManager::onLogOnFailure`, then `Window::HandleVisibilityChanged: become hidden` + `GameCore::OnBackground`). This differs from the OD-030/031 replay-start AccountController assert and may indicate an online-login requirement blocking the offline replay path. The pipeline/driver are proven (OD-036 staged 9 + armed 4), so no driver change is warranted; the next session must first diagnose the lobby-login failure.
objective: Reach ≤10 inside the lease, stage survivors into CE, and hold a green operator window (blocked pre-roll by launch flake)
stopCondition: Stop after target ≤10 + CE staging + green window, or gate loss
method:
  primaryTool: scripts/launch-offline-replay-for-od.ps1 (detached) + session driver + roll-replay-time-increased.ps1 (best-known + budget) + pre-arm-debugger.ps1
  secondaryTools: Cheat Engine 7.7 (C:\Program Files\Cheat Engine); CE autorun od-autorun-writebp.lua (default path, 300s poll)
  transition: not reached — no roll ran this session
observations:
  - state: attempt-1
    launchOutcome: launcher logged OfflineReplayVerified (clicker needed round 2; unusual bright blob 59K px / meanL 153 vs usual 8K/60); driver saw Denied from first poll
    gateReason: evidence.monitor_unhealthy; no new blitz-log — game-side flake
  - state: attempt-2
    launchOutcome: gate verified then window_lost_final / watch_exit=1
    blitzLog: game in login/lobby phase — LoginHandler::fail status=68 Invalid password, ConnectionManager::onLogOnFailure, then become hidden + OnBackground
  - state: attempt-3
    launchOutcome: gate verified then window_lost_final / watch_exit=1
    blitzLog: reached BattleController::LoadGameScene ends then become hidden + OnBackground
  - state: attempt-4
    launchOutcome: launcher logged OfflineReplayVerified; driver saw Denied from first poll
    blitzLog: again lobby login-failure signature (LoginHandler::fail status=68) then become hidden
  - state: after-session
    verificationState: Denied (reason evidence.monitor_unhealthy) — game dead
    blitzLogs: blitz-logs_20260803152454/152658/152855.txt (16:25/16:27/16:29 local)
result:
  whatWorked:
    - Diagnosis: 7 of the last 8 launches across OD-036/037 flaked game-side, with a NEW lobby-login crash signature (Invalid password status=68) distinct from the replay-start AccountController assert.
    - The launcher/clicker/pipeline behaved as designed on all four attempts; the gate correctly revoked to monitor_unhealthy when the game died.
  whatFailed:
    - No roll ran: all four launches died before the session driver's gate poll returned green.
  rulesOut:
    - No new rules-outs. The staging handoff (OD-036) and rolling-to-≤10 (OD-036) remain proven; the blocker is now launch reliability.
  partials:
    - The NEW lobby-login crash signature is characterized and recorded for OD-038 diagnosis.
    - BLK-0019 still needs content-distinct second replay (independentReplays 0).
  nextPivot: OD-RECOVERY-038 — first diagnose the lobby-login failure (offline flag / login config / network), then run the proven invocation (-SnapshotMaxBytes 402653184 -MaxRounds 40 -HoldAfterRollSeconds 240) with the operator present during the held green window; a content-distinct second replay still closes BLK-0019.
  repeatWithoutChangedHypothesis: false
artifacts:
  rawFiles: %TEMP%\od-prearmed-debugger.json, blitz-logs_20260803152454/152658/152855.txt, session logs /tmp/od-launch-037{,b,c,d}.log
  committedSummary: this ledger entry + workflow update + handoff (2026-08-03-od-recovery-037.md)
```

`OD-RECOVERY-037` is aggregate structural evidence only. Offset remains 0.
Launch reliability is the binding constraint: a NEW lobby-login crash
signature (Invalid password status=68, window hidden before the replay)
flaked 4-of-4 launches this session. The roll pipeline is proven; the next
session diagnoses the login path first.

## `OD-RECOVERY-038` result — 2026-08-03 (lobby-login diagnosis corrected; 401-refresh hardening validated)

```yaml
sessionId: OD-RECOVERY-038
supersedes: the OD-037 lobby-login crash-signature hypothesis (now ruled out)
date: 2026-08-03
observedAtUtc: 2026-08-03T20:50:00Z # approximate; four attempts 20:44-21:03Z

timebox: diagnose the OD-037 lobby-login failure first (offline flag / login config / network), then run the proven invocation with the corrected diagnosis; harden the 401-refresh when it failed live
decision: the lobby-login hypothesis is RULED OUT — the `Invalid password status=68` login failure is baseline noise (present in every blitz log since 14:48 game-time INCLUDING the OD-036 SUCCESS run, and every log reaches Start replay event + LoadGameScene); the real death signature is become hidden + GameCore::OnBackground ~2s after LoadGameScene ends (the known replay-start flake family at an elevated rate). The first-ever 401-refresh failure in 13 validations hit at round 9 — fixed with a 750ms settle + 4 retries and validated live (round-8 401 absorbed). Attempt 4 rolled 39M->11 in 20 rounds (plateau 11 rounds 16-20, value-bound, 1 above target) then the lease wall.
objective: per OD-038 protocol, diagnose the login failure before running the proven invocation; capture the best roll the elevated flake rate allows
stopCondition: stop after diagnosis + proven-invocation attempts, or gate loss
method:
  primaryTool: scripts/launch-offline-replay-for-od.ps1 (detached) + od-018 session driver + roll-replay-time-increased.ps1 + pre-arm-debugger.ps1
  secondaryTools: Cheat Engine 7.7 (C:\Program Files\Cheat Engine) pre-armed and attached; CE autorun od-autorun-writebp.lua
  transition: natural replay progression between rounds (no manual Space pulses; -AutoSpace not used)
observations:
  - state: diagnosis
    loginFailure: 'LoginHandler::fail status=68 Invalid password' present in EVERY blitz log since blitz-logs_20260803144823 (14:48 game-time) - INCLUDING blitz-logs_20260803150349 (15:03) which is the OD-036 SUCCESS run (login failure + Start replay event + onLeaveWorld full replay)
    correctedSignature: every log reaches Start replay event + BattleController::LoadGameScene begins/ends; the dying logs then show Window::HandleVisibilityChanged: become hidden + GameCore::OnBackground ~1-2s after LoadGameScene ends, and the log stops (no crash dump, no assert)
    redHerring: the login failure does not stop the replay path; it is a stored-token rejection baseline, not the blocker
    elevatedFlakeRate: 7 of last 8 launches before this session; 2 of 4 this session game-side
  - state: attempt-1
    verificationState: launcher logged OK OfflineReplayVerified (watch_exit=0) then driver first poll Denied
    blitzLog: blitz-logs_20260803154751 (15:47:51) - LoadGameScene ends 20:48:18, become hidden + OnBackground 20:48:19
    note: exact OD-037 signature; game died ~1s after scene load
  - state: attempt-2
    outcome: no_game_window_while_waiting (watch_exit=1) - game window vanished during the watch-wait phase
    note: same replay-start flake family
  - state: attempt-3
    verificationState: OfflineReplayVerified (watch_exit=0, round-2 click)
    rolling: snapshot session 000001 (budget bound); sequence 39,190,222->617,377->146,022->28,098->6,427->1,985->1,024->74->65 (9 rounds)
    failure: round 9 compare returned 401 - capability_401_refresh_retry=1 then FAILED_unexpected='The remote server returned an error: (401) Unauthorized.'
    firstEver401Failure: the refresh+retry mechanism had survived 13 prior live validations (OD-030..036); this is its first failure
    rootCause: the refreshed context re-read the rendezvous file but the retry fired immediately - a mid-rotation file (old token still present or host re-rotating) 401'd again and the 2-retry budget was exhausted
    gateAfter: post_roll_gate=OfflineReplayVerified (gate held through the whole roll)
  - state: driver-hardening
    change: Invoke-OdApi Retries default 2->4 + Start-Sleep -Milliseconds 750 after each refresh before the retry (roll-replay-time-increased.ps1)
    parseCheck: parse_ok
  - state: attempt-4 (hardened refresh)
    verificationState: OfflineReplayVerified (watch_exit=0, round-2 click)
    rolling: sequence to live terminal only; previous 39,126,523 (round 1, budget bound) -> ... -> 11 in 20 rounds; plateau 11 rounds 16-20 (1s pulses - value-bound, the last 11 tick every frame)
    hardRefreshValidated: round 8 returned 401 -> capability_401_refresh_retry=1 -> retry succeeded and the roll continued (validated live on first use)
    end: lease wall round 21 (FAILED_unexpected 400 Bad Request - session_not_found mapped to 400) + EvidenceStale; rolling_exit=5
    addressFile: none (target 10 not reached; 11 > 10)
    ceAutorun: polled for od-survivors.txt (max 300s) - no address file to stage
result:
  whatWorked:
    - Lobby-login hypothesis RULED OUT with hard evidence: the login failure appears in the OD-036 SUCCESS log too and every launch reaches LoadGameScene - the OD-037 'new lobby-login crash signature' record is corrected.
    - Real death signature isolated: become hidden + GameCore::OnBackground ~2s after LoadGameScene ends; no crash dump, no assert - a quiet game-side exit at replay start (known flake family, elevated rate).
    - First-ever 401-refresh failure diagnosed and fixed: 750ms settle + 4 retries.
    - The hardened refresh was validated live on its first use (attempt 4 round 8: 401 absorbed, roll continued).
    - Attempt 4 rolled 39M->11 in 20 rounds - the value-bound plateau (11, 1 above target) reproduced consistently.
    - Gate held OfflineReplayVerified through both green rolls.
  whatFailed:
    - Target 10 not reached either attempt (plateau at 11, 1 above - the same value-bound tail from OD-034/035).
    - Attempts 1-2 game-side flakes (2 of 4 this session; 9 of last 12 launches across OD-036/037/038 have flaked).
    - No interactive Find-what-writes (no staging - target not reached).
  rulesOut:
    - The OD-037 lobby-login crash-signature hypothesis: the `Invalid password status=68` login failure is baseline noise, not a blocker (present in the OD-036 success log; every launch reaches LoadGameScene).
  partials:
    - 401-refresh hardening (settle + retries) validated live - the roll pipeline can now survive token rotation at an 8-round cadence.
    - 11-survivor set again the reproducible convergence floor; 1 above target.
    - BLK-0019 still needs content-distinct second replay (independentReplays 0).
  nextPivot: OD-RECOVERY-039 - run the proven invocation (-SnapshotMaxBytes 402653184 -MaxRounds 40 -HoldAfterRollSeconds 240) with the operator present during the held green window; the 11-survivor set is usable for interactive Find-what-writes even without <=10; alternatively investigate the replay-start flake root cause (quiet exit ~2s after LoadGameScene, no dump) or import a content-distinct second replay for BLK-0019.
  repeatWithoutChangedHypothesis: false
artifacts:
  rawFiles: blitz-logs_20260803154751/155628/155812.txt, session logs /tmp/od-launch-038{,b,c,d}.log, %TEMP%\od-ce-autorun.log
  committedSummary: this ledger entry + workflow update + handoff (2026-08-03-od-recovery-038.md) + roll-replay-time-increased.ps1 401-refresh hardening
```

`OD-RECOVERY-038` is aggregate structural evidence only. Offset remains 0.
The lobby-login hypothesis from OD-037 is ruled out (red herring - present in
success runs too). The real signature is the replay-start flake (quiet exit
~2s after LoadGameScene). The 401-refresh hardening is the session's durable
driver result.

## `OD-039-STATIC` result — 2026-08-03 (Track A batch static root analysis)

```yaml
sessionId: OD-039-STATIC
supersedes: none (Track A milestone; no live session or lease used)
date: 2026-08-03
timebox: offline batch analysis of the hash-bound 11.19.0.10 binary only; no game process
decision: batch RTTI walk over all 9 community chain classes + chain-root verification of xref store-slot candidates; two runtime-written static root candidates confirmed (0x03FA0C74 with 9 .text refs, 0x03FA012C with 6), EntityList proven a plain struct (0 RTTI hits), and no statically-reachable vtable root found for any chain class; rolling driver gained delta pass-through for the Track C2 pilot
objective: Convert the Track A static backlog into concrete evidence: locate RTTI TypeDescriptors for every class in the community chain, verify which store-slot xref candidates are genuine static roots, and arm the Track C2 pilot with the new delta-compare driver parameters
stopCondition: batch complete (all requested RTTI + chain verifications recorded)
method:
  primaryTool: tools/find-static-roots.py (stdlib-only PE parser; new --rtti/--chain batch args)
  target: hash-bound 11.19.0.10 binary (identity re-verified before scan)
  rttiClasses: EntityList, VehicleGameLogic, Vehicle, AppContextImpl, ScreensFlow, GameScene, GameCamera, Context, VehicleDescr
  chainCandidates: 0x03FA0C74, 0x03FA012C, 0x03E7DF28 (AvatarContextBattle TypeDescriptor)
observations:
  - state: chain-verify-0x03FA0C74
    root: 0x03FA0C74
    section: .data
    onDiskShape: zero (runtime-initialized candidate)
    relocTarget: false (not a reloc target - runtime-written or not a pointer)
    textReferences: 9 (.text operand references; samples 0x0005D531, 0x0005D53C, 0x006E52D1, 0x006E52DE, 0x006E52F6, 0x006E64D1, 0x006F18C1, 0x006F18F1)
    verdict: PLAUSIBLE root candidate - code-initialized global signature
  - state: chain-verify-0x03FA012C
    root: 0x03FA012C
    section: .data
    onDiskShape: zero (runtime-initialized candidate)
    relocTarget: false
    textReferences: 6 (samples 0x005F7BBB, 0x005F7BCE, 0x005F7BDB, 0x00601764, 0x00601785, 0x00601792)
    verdict: PLAUSIBLE root candidate - code-initialized global signature
  - state: chain-verify-0x03E7DF28
    root: 0x03E7DF28 (AvatarContextBattle TypeDescriptor from OD-036-era scan)
    section: .data
    onDiskShape: zero
    relocTarget: false
    textReferences: 0 (no .text instruction ever references this address)
    verdict: NOT a root - dead data, no code reference
  - state: rtti-EntityList
    substring: EntityList
    nameHits: 0 (mangled filter)
    typedescriptorSteps: 0
    verdict: plain struct, no RTTI - xref-discovery only
  - state: rtti-chain-classes
    VehicleGameLogic: 43 raw hits -> 11 mangled; td steps 11; colSlots 0; vtableSlots 0
    Vehicle: 1516 raw -> 809 mangled; td steps 809; colSlots 8 (all plausible=False, signature mismatch); vtableSlots 0
    AppContextImpl: 4 -> 3 mangled; colSlots 0
    ScreensFlow: 6 -> 1 mangled; colSlots 0
    GameScene: 26 -> 5 mangled; colSlots 0
    GameCamera: 15 -> 6 mangled; colSlots 0
    Context: 405 -> 244 mangled; colSlots 7 (all plausible=False); vtableSlots 0
    VehicleDescr: 35 -> 20 mangled; colSlots 0
result:
  whatWorked:
    - Batch mode validated: 9 RTTI class scans + 3 chain verifications in one pass with per-class TypeDescriptor/COL/vtable/data-root accounting.
    - Two runtime-written static root candidates confirmed: 0x03FA0C74 (9 .text refs) and 0x03FA012C (6 refs) - both fail the reloc test which is exactly the code-initialized-global signature (a reloc target would be a compiler-generated pointer; a non-reloc slot with .text operands is written by code at init). These are the highest-confidence static roots found to date.
    - EntityList is a plain struct: 0 RTTI hits means no vtable/COL to anchor - xref discovery is the only path, which the 2398-store-slot scan already covers.
    - TypeDescriptors exist for the entire chain family - Ghidra anchors available for later deeper analysis.
    - Rolling driver now exposes -CompareMode delta/-DeltaTarget/-DeltaTolerance pass-through (parse-checked) so the Track C2 pilot can run without further code changes.
  whatFailed:
    - No statically-reachable vtable root: Vehicle/Context COL slots all fail the signature check (plausible=False) - the classic missing-COL-hop evidence pattern (consistent with the AvatarContextBattle no-root result in the strategy-v2 pilot).
    - 0x03E7DF28 (AvatarContextBattle td) is dead data - no code reference, so it cannot anchor a chain.
  rulesOut:
    - Statically-reachable vtable roots for the chain classes as a Track A mechanism (COL signature mismatch across all 15 slots examined) - repeat only with a changed hypothesis (e.g. COL signature check against the actual RTTI layout or a complete-object-locator fixup table).
  partials:
    - Two static root candidates (0x03FA0C74, 0x03FA012C) ready for live verification against a replayTime-anchored session (Track C2 step 4 in strategy-v2).
    - Delta-compare driver pass-through ready; first live validation is the Track C2 pilot.
    - BLK-0019 still needs content-distinct second replay (independentReplays 0).
  nextPivot: OD-RECOVERY-040 - run the proven invocation with the operator present during the held green window; optionally pilot delta-compare with a replay-derived position delta to break the 11-survivor plateau; verify 0x03FA0C74/0x03FA012C live if the window allows; content-distinct second replay when available.
  repeatWithoutChangedHypothesis: false
artifacts:
  rawFiles: %TEMP%\fsr-batch.json, %TEMP%\find-static-roots-*.log (timestamped per-run)
  committedSummary: this ledger entry + workflow update + strategy-v2 updates + tools/find-static-roots.py batch mode + roll-replay-time-increased.ps1 delta pass-through
```

`OD-039-STATIC` is aggregate structural evidence only. Offset remains 0.
The static campaign's durable outputs are the two runtime-written root
candidates (0x03FA0C74, 0x03FA012C) and the proof that the chain classes
are RTTI-reachable except EntityList. Live confirmation is still required.

## `OD-040-STATIC` result — 2026-08-03 (reference-site decode + member-offset dump)

```yaml
sessionId: OD-040-STATIC
supersedes: none (Track A milestone; no live session or lease used)
date: 2026-08-03
timebox: offline batch analysis of the hash-bound 11.19.0.10 binary only; no game process
decision: the two OD-039-STATIC root candidates are confirmed as read-write code-initialized globals via reference-site instruction decode (the offline equivalent of Find-what-writes): 0x03FA0C74 has 9 .text refs (5 load + 4 store), 0x03FA012C has 6 (2 load + 4 store); a typed member-offset dump of the 0x03FA0C74 window exposes neighboring .text/.rdata pointers; candidates remain un-mapped to a gameplay field until live probing
objective: Move the two static root candidates from "zeroed slot with code refs" to "read-write global written by runtime code" classification, and pre-compute plausible member displacements for a live session
stopCondition: batch complete (refs decode + fields dump recorded)
method:
  primaryTool: tools/find-static-roots.py new --refs/--fields modes (stdlib-only)
  target: hash-bound 11.19.0.10 binary
  refsRoots: 0x03FA0C74, 0x03FA012C
  fieldsRoots: 0x03FA0C74, 0x03FA012C (window 0x80)
observations:
  - state: refs-0x03FA0C74
    summary: {load: 5, store: 4}
    opcodes: A1/A3 (mov eax,[abs] / mov [abs],eax) + 8B/89 (mov r32,[m+disp32] / mov [m+disp32],r32, reg=ecx)
    clusters:
      - 0x0005D531 load eax / 0x0005D53C store eax (pair)
      - 0x006E52D1 load ecx / 0x006E52DE store ecx / 0x006E52F6 store eax (triple)
      - 0x006E64D1 load eax
      - 0x006F18C1 load eax / 0x006F18F1 load eax / 0x006F18FC store ecx
    verdict: read-write global - written by runtime code across 3 disjoint code clusters
  - state: refs-0x03FA012C
    summary: {load: 2, store: 4}
    opcodes: A1/A3 only
    clusters:
      - 0x005F7BBB load eax / 0x005F7BCE store eax / 0x005F7BDB store eax (triple)
      - 0x00601764 load eax / 0x00601785 store eax / 0x00601792 store eax (triple)
    verdict: write-biased read-write global - runtime stores dominate
  - state: fields-0x03FA0C74 (window +/-0x80)
    pointerCandidates: +0xFFFFFFAC->0x00404064(.text), +0xFFFFFFB4->0x037F3054(.rdata), +0xFFFFFFFC->0x00404064(.text), +0x00000004->0x037F3054(.rdata), +0x0000004C->0x00404064(.text), +0x00000054->0x037F3054(.rdata)
    float32Candidates: 8 (e.g. +0xFFFFFFA4=2.972, +0xFFFFFFF4=2.9747, +0x00000044=2.9722)
    doubleCandidates: 17 (e.g. +0xFFFFFFA0=30.2085, +0xFFFFFFF0=30.3836, +0x00000040=30.2234)
  - state: fields-0x03FA012C (window +/-0x80)
    pointerCandidates: +0xFFFFFFFC->0x037F3054(.rdata)
    float32Candidates: 17; doubleCandidates: 15 (e.g. +0xFFFFFF88=32.5032, +0xFFFFFF98=52.7442, +0xFFFFFFE8=163.6391)
result:
  whatWorked:
    - Reference-site decode now classifies every .text ref by instruction (load/store/lea/imm + register + width) - the offline equivalent of Find-what-writes.
    - Both candidates are confirmed read-write globals: the load/store mix (9 refs: 5/4; 6 refs: 2/4) proves runtime code writes them (A3/89 stores), which a pure compile-time constant would never show.
    - Disjoint code clusters (3 for 0x03FA0C74, 2 for 0x03FA012C) indicate the globals are touched from multiple subsystems - consistent with singleton/state roots.
    - Field dump pre-computes candidate member displacements (relative offsets) around the roots for a live session - the .rdata pointer 0x037F3054 repeats at +0xFFFFFFB4/+0x00000004/+0x00000054 around 0x03FA0C74 (likely a shared vtable/type descriptor pointer).
  whatFailed:
    - No root-to-field mapping: the candidates are still unclassified relative to the 8 gameplay fields; the field dump values are on-disk defaults, not live state.
  rulesOut:
    - Dead-data / string-constant interpretation of 0x03FA0C74 and 0x03FA012C (they are written by runtime code - ruling this out is the milestone's win).
  partials:
    - Two read-write static root candidates ready for a live probe (Track C2 step 4 in strategy-v2): compare/pattern them under OfflineReplayVerified to classify what they hold.
    - Field-dump relative offsets give the live session a prepared displacement list.
    - BLK-0019 still needs content-distinct second replay (independentReplays 0).
  nextPivot: OD-RECOVERY-041 - run the proven invocation with the operator present during the held green window; optionally pilot delta-compare and probe 0x03FA0C74/0x03FA012C live; content-distinct second replay when available.
  repeatWithoutChangedHypothesis: false
artifacts:
  rawFiles: %TEMP%\fsr-refs-v2.json, %TEMP%\fsr-refs-fields.json, %TEMP%\find-static-roots-*.log
  committedSummary: this ledger entry + memory-offsets 11.19.0.10 notes/README + tools/find-static-roots.py --refs/--fields + workflow/strategy-v2 pointer updates + handoff (2026-08-03-od-static-040.md)
```

`OD-040-STATIC` is aggregate structural evidence only. Offset remains 0.
The milestone upgrades the two OD-039-STATIC candidates from "zeroed slot
with code refs" to "read-write globals written by runtime code" - the
offline Find-what-writes equivalent. Live probing is still required.

## `OD-041-STATIC` result — 2026-08-03 (repeating-record classification; EH table identification)

```yaml
sessionId: OD-041-STATIC
supersedes: none (Track A milestone; no live session or lease used)
date: 2026-08-03
timebox: offline batch analysis of the hash-bound 11.19.0.10 binary only; no game process
decision: the repeating .rdata pointer 0x037F3054 found around 0x03FA0C74 is an MSVC EH handler/funclet table, and 0x03FA0C74 is NOT a standalone gameplay root - it is the +0x04 member of record[1] in a repeating 0x50-byte .data record family; both OD-039/040 'root candidates' are reclassified as EH/handler record members; live-probing them as singleton roots is ruled out without a changed hypothesis
objective: Explain the repeating 0x037F3054 neighbor pointer and classify the two strongest 'root candidates' correctly before they are live-probed
stopCondition: batch complete (handler-table identification + record-family map recorded)
method:
  primaryTool: tools/find-static-roots.py investigation + new --record-map BASE,STRIDE,COUNT mode
  target: hash-bound 11.19.0.10 binary
observations:
  - state: handler-table-0x037F3054
    section: .rdata
    onDiskDword0: 0xFFFFFFFF (sentinel)
    layout: interleaved {state/int, .text code-ptr} pairs
    codeTargets: .text rva 0x02CF0D70+ (lea r,[ebp+disp]; jmp cleanup/throw thunks)
    funcInfoMagics: 113 x 0x19930522 within +/-0x2000
    dataReferences: 48,609 4-aligned .data slots, 0 unaligned - massively-shared
    verdict: MSVC C++ exception-handling handler/funclet table (not a vtable, not a TypeDescriptor, not gameplay state)
  - state: record-family-0x03FA0C20
    base: 0x03FA0C20 (stride 0x50, .data)
    template: {0x00404064 (.text member-fn), <runtime slot>, 0x037F3054 (.rdata EH table), 0, ...}
    record1: 0x03FA0C70; its +0x04 member == 0x03FA0C74 (the OD-039 'root candidate')
    siblingRecords: records at 0x03FA0B80..0x03FA0D60+ all identical on-disk template
    signatureHits: {0x00404064, *, 0x037F3054} matches 3,565 .data slots
    recordMap: --record-map 0x03FA0C20,0x50,10 -> 10 records mapped, 9 runtime slots
    runtimeSlots: +0x04 (zero 4/10), +0x08 (1/10), +0x0C (4/10), +0x10 (1/10), +0x18 (1/10), +0x28 (1/10), +0x30 (1/10), +0x44 (4/10), +0x4C (1/10)
  - state: member-fn-0x00404064
    bytes: 8b 5d 08 57 8d b9 44 01 00 00 3b fb ... (member-function prologue: [ebp+8], this+0x144 compare, then list ops on [edi+4]/[edi+8])
    verdict: small member function (likely a container/list node helper) shared by every record in the family
result:
  whatWorked:
    - 0x037F3054 identified as MSVC EH handler/funclet data (0xFFFFFFFF sentinel + {state, code-ptr} pairs + 113 FuncInfo magics) - the repeating neighbor pointer is infrastructure, not a vtable pointer.
    - The 48,609 aligned .data references (0 unaligned) prove 0x037F3054 is a massively-shared handler pointer across many records.
    - Record-family geometry: 0x03FA0C74 is exactly record[1].+0x04 (0x03FA0C70 + 4) - the runtime slot of the record, not a standalone global.
    - --record-map mode built and validated: 10 records mapped, 9 runtime-initialized slots enumerated with zero-counts per member.
    - Both OD-039/040 candidates now carry an honest classification: EH/handler record members, NOT gameplay state - this prevents a wasted live probe.
  whatFailed:
    - No gameplay-field mapping: the record family is handler infrastructure; none of its members correspond to the 8 gameplay fields.
  rulesOut:
    - 0x03FA0C74 / 0x03FA012C as standalone singleton gameplay roots (they are members of a repeating 0x50-byte EH/handler record family; repeat only with a changed hypothesis, e.g. probing the whole record's runtime slots +0x04/+0x0C/+0x44 for a chain).
    - 0x037F3054 as a vtable/TypeDescriptor (it is an EH handler/funclet table).
  partials:
    - The record family's 9 runtime slots (+0x04, +0x08, +0x0C, +0x10, +0x18, +0x28, +0x30, +0x44, +0x4C) are enumerated for any future per-record state probe.
    - 3,565 sibling records share the {0x00404064, *, 0x037F3054} template - a large handler registry, not a singleton.
    - BLK-0019 still needs content-distinct second replay (independentReplays 0).
  nextPivot: OD-RECOVERY-042 - run the proven invocation with the operator present during the held green window; the 11-survivor replayTime set remains the live anchor; the static handler records are NOT gameplay roots, so the live session should not probe them as singletons; optionally pilot delta-compare; content-distinct second replay when available.
  repeatWithoutChangedHypothesis: false
artifacts:
  rawFiles: %TEMP%\fsr-recordmap2.json, %TEMP%\fsr-final.json, %TEMP%\find-static-roots-*.log
  committedSummary: this ledger entry + memory-offsets 11.19.0.10 notes/README + tools/find-static-roots.py --record-map + workflow/strategy-v2 updates + handoff (2026-08-03-od-static-041.md)
```

`OD-041-STATIC` is aggregate structural evidence only. Offset remains 0.
The milestone's durable win is a classification correction: the two
OD-039/040 'strongest root candidates' are members of a repeating 0x50-byte
EH/handler record family, not standalone gameplay state. The offline
Find-what-writes pipeline is extended with a record-map mode, and the live
session is steered back to the replayTime anchor instead of a dead singleton
probe.

## `OD-042-STATIC` result — 2026-08-03 (vtable discovery; type_info vftable; Vehicle-family TD xref)

```yaml
sessionId: OD-042-STATIC
supersedes: none (Track A milestone; no live session or lease used)
date: 2026-08-03
timebox: offline batch analysis of the hash-bound 11.19.0.10 binary only; no game process
decision: 0x037F3054 is the shared RTTI type_info vftable (every TypeDescriptor's pVFTable points at it), the --vtables mode now names 17,133 of 18,721 vtables via the COL chain (fix: MSVC x86 stores the mangled name inline at td+8, not as a pointer), and the Vehicle-family TypeDescriptor xref is negative (0 .text refs / 0 slots) - the RTTI name-to-root path is exhausted for the chain classes, and the vtable-singleton path yields no chain-class root (GameScene vtable 0x0319D3C4 has 0 .data roots)
objective: (1) Name the EH infrastructure behind 0x037F3054 precisely; (2) build a vtable-discovery primitive that resolves RTTI class names; (3) run the strategy's ranked remaining Track A step - Vehicle-family xref discovery
stopCondition: batch complete (EH naming + vtable scan + TD xref recorded)
method:
  primaryTool: tools/find-static-roots.py new --vtables mode + targeted investigation scripts
  target: hash-bound 11.19.0.10 binary
observations:
  - state: eh-model-naming
    refContext: 0x020CB956 = `c7 06 54 30 7f 03` = mov dword ptr [esi], 0x037F3054 - canonical MSVC x86 vftable write in a constructor ([this]+0)
    typeInfoVftable: td@0x03DB5120 (for .?AU?$StaticAnyFnInvoker@V<lambda_0872e1a5...>@@PAVDebugEntitySkeletonComponent...) has pVFTable == 0x037F3054 -> 0x037F3054 is the shared type_info vftable, explaining the 48,609 .data references
    funcletThunks: table targets are lea r,[ebp+..]; jmp cleanup/throw thunks at .text 0x02CF0D70+ (EH funclets reachable through the vftable slots)
    verdict: 0x037F3054 = RTTI type_info vftable (refined from OD-041's 'EH handler/funclet table' label)
  - state: vtables-discovery
    mode: --vtables --min-slots 5
    vtableCount: 18,721
    namedCount: 17,133 (0 before the inline-name fix)
    dataRootCount: 2,615
    inlineNameFix: MSVC x86 TypeDescriptor stores mangled name inline at td+8 (char[]), not as a VA pointer
    chainClassVtables:
      GameScene: 0x0319D3C4 (26 slots) - .?AVGameScene@@
      BaseContext: 0x03197044 (8 slots) / RootContext: 0x03197068 (8 slots)
      VehicleMovementFilterComponent: 0x03199C68 / VehicleTerrainMaterialKindsComponent: 0x03199C90 / VehicleFashionComponent: 0x03199FF8
      VehicleGameLogic family: lambda invokers / Func_impl_no_alloc vtables (no direct VehicleGameLogicComponent vtable with static .data root)
    gameSceneDataRoots: 0 (.data holds no pointer to the GameScene vtable -> honest negative for the vtable-singleton path)
    vtableArray: .data 0x03B7E198.. holds consecutive vtable pointers -> 44-byte vtables at .rdata 0x031988B4+ (StaticAnyFnInvoker lambda vtables for TankComponent/AimingPoint handlers - a component event table)
  - state: vehicle-family-td-xref
    tds: VehicleGameLogicComponent td=0x03C24F44, GameCameraComponent td=0x03C19E90, GameSceneController td=0x03F10644, AppContextImpl td=0x03E356EC, VehicleDescr family
    textRefs: 0 direct absolute operands referencing any of these tds
    dataSlots: 0 .data/.rdata slots hold these td VAs
    verdict: dynamic_cast/typeid in this build does not emit direct absolute td operands (or uses COL-relative addressing) - the RTTI name-to-root path is exhausted for the chain classes
result:
  whatWorked:
    - The --vtables mode is a new discovery primitive: 17,133/18,721 vtables now resolve to RTTI class names, giving the campaign a named vtable inventory for the whole binary.
    - The inline-name fix (td+8 is char[], not a pointer) is the kind of correctness win that compounds - it turned a 0%-named scan into a 91.5%-named scan.
    - 0x037F3054 is now precisely the shared RTTI type_info vftable - closes the OD-041 EH question with a confirmed identity.
    - GameScene's vtable is located (0x0319D3C4, 26 slots) with an honest negative: 0 .data roots means no static vtable-singleton path to the scene graph from .data.
  whatFailed:
    - No chain-class gameplay root from either path: vtable-singleton (GameScene vtable has no .data holder) or RTTI name-to-root (0 td refs).
  rulesOut:
    - Direct .text absolute references to Vehicle-family TypeDescriptors as a discovery mechanism (0 refs in this build).
    - .data-held chain-class vtable pointers as a static root mechanism (GameScene vtable has 0 .data roots).
  partials:
    - Named vtable inventory (17,133 vtables) is now queryable - future class→vtable→root lookups are one tool call.
    - The component event table at .data 0x03B7E198 (TankComponent/AimingPoint handler vtables) is a candidate per-component dispatch structure for later analysis.
    - BLK-0019 still needs content-distinct second replay (independentReplays 0).
  nextPivot: OD-RECOVERY-043 - run the proven invocation with the operator present during the held green window; the 11-survivor replayTime set remains the live anchor; delta-compare pilot optional; content-distinct second replay when available.
  repeatWithoutChangedHypothesis: false
artifacts:
  rawFiles: %TEMP%\fsr-vtables2.json (18,721 vtables / 2,615 roots), %TEMP%\fsr-vtables.json (pre-fix), %TEMP%\find-static-roots-*.log
  committedSummary: this ledger entry + memory-offsets 11.19.0.10 notes/README + tools/find-static-roots.py --vtables + workflow/strategy-v2 updates + handoff (2026-08-03-od-static-042.md)
```

`OD-042-STATIC` is aggregate structural evidence only. Offset remains 0.
The milestone delivers a named vtable inventory (17,133/18,721) and
precisely identifies the shared `type_info` vftable behind 0x037F3054. The
RTTI name→root and vtable-singleton paths are both exhausted for the chain
classes; the live session remains anchored on the replayTime set.

## `OD-043-STATIC` result — 2026-08-03 (class→vtable query; AnyFn invoker table decode)

```yaml
sessionId: OD-043-STATIC
supersedes: none (Track A continuation; next live session is OD-RECOVERY-044)
date: 2026-08-03
observedAtUtc: 2026-08-03T19:40Z
method: tools/find-static-roots.py new --vtable-root + --table-map modes against the hash-bound 11.19.0.10 binary
objective: (1) Turn the OD-042 named vtable inventory (17,133/18,721) into a direct class→root query mode; (2) decode the .data vtable-pointer array at 0x03B7E198 that OD-042 flagged as a candidate per-component dispatch structure
findings:
  - class: game-scene-query
    tool: --vtable-root GameScene
    result: 6 matches; primary .?AVGameScene@@ vtable=0x0319D3C4 col=0x034A89F0 slots=12 roots=0
    interpretation: GameScene's vtable has ZERO .data holders — no static singleton holds a pointer to it; consistent with OD-042's vtable-singleton negative
  - class: tank-component-query
    tool: --vtable-root TankComponent
    result: 19 matches, ALL .?AU?$StaticAnyFnInvoker@V<lambda_...>@@PAVTankComponent@@...@AnyFnDetails@DAVA@@
    interpretation: every TankComponent-named vtable is an AnyFn delegate invoker (event subscription), each with exactly 1 .data root = its entry in the 0x03B7E198 array
  - structure: 0x03B7E198 vtable-pointer array
    tool: --table-map 0x03B7E198,256
    result: 34 entries; modal target stride 0x2C (44-byte vtables, 24 entries at that pitch + 9 irregular); 24 of 34 named; all 24 named entries are DAVA AnyFn StaticAnyFnInvoker<lambda> vtables
    slots: all named entries share the same 4 .text dispatcher functions; first-slot fn 0x002C4550 shared by 24/24 entries
    refs: 3 .text reference sites — 0x002DF2CB mov [esi+0x0C],<table> (constructor stores the table at object+0x0C), 0x02B817E1 mov ecx,<table> (thunk), 0x03104FAB mov [0x03B7E198],imm32 (RUNTIME WRITE repointing entry[0] to 0x03594958)
    dataRefs: 0 .data slots point at the array base (only the 24 array entries reference the vtables)
    interpretation: the array is the per-lambda AnyFn invoker vtable table for component event subscriptions — dispatch infrastructure, NOT a gameplay root; entry[0] is runtime-patchable (static-initializer-style fixup), so the table is mutable but holds no gameplay state
result:
  whatWorked:
    - --vtable-root turns the 17,133-name inventory into a one-call class→vtable→data-root query (GameScene / Vehicle / TankComponent all resolve with COL anchors + root counts).
    - --table-map decodes any .data pointer array: per-entry RTTI name + function slots, modal stride + irregularity, shared dispatcher analysis, and decoded .text refs — including catching the runtime store (the offline 'who writes this' for a table).
    - 0x03B7E198 fully classified as DAVA AnyFn delegate dispatch infrastructure with an internally-closed reference set (each vtable has exactly 1 root = its array entry).
  whatFailed:
    - No new gameplay-root candidate emerged; the AnyFn table's lambdas name the component event subscriptions but the components themselves are heap objects reached via [esi+0x0C], not static singletons.
  rulesOut:
    - 0x03B7E198 as a gameplay root (AnyFn dispatch infrastructure; entry[0] runtime-patchable but holds no field state).
    - Class→vtable→data-root as a root-discovery path for the chain classes: GameScene vtable has 0 .data holders; TankComponent's named vtables are all delegate invokers (roots=1 each = internal array entries).
  partials:
    - Both query modes are committed tool capabilities for future sessions.
    - BLK-0019 still needs content-distinct second replay (independentReplays 0).
  nextPivot: OD-RECOVERY-044 - live session with the proven invocation + operator present during the held green window; 11-survivor replayTime set remains the live anchor; delta-compare pilot optional; the AnyFn table must NOT be probed as a root.
  repeatWithoutChangedHypothesis: false
artifacts:
  rawFiles: %TEMP%\od043-evidence.json (vtable-root TankComponent + table-map 0x03B7E198), %TEMP%\find-static-roots-*.log
  committedSummary: this ledger entry + memory-offsets 11.19.0.10 notes/README + tools/find-static-roots.py --vtable-root/--table-map + workflow/strategy-v2 updates + handoff (2026-08-03-od-static-043.md)
```

`OD-043-STATIC` is aggregate structural evidence only. Offset remains 0.
The milestone converts the named vtable inventory into reusable query modes
and reclassifies 0x03B7E198 as DAVA AnyFn delegate dispatch infrastructure
with an internally-closed reference set — one more ruled-out dead end on the
static root path. The live session remains anchored on the replayTime set.

## `OD-044-STATIC` result — 2026-08-03 (delta-compare pilot prep: replay-derived target extractor + Float scan)

```yaml
sessionId: OD-044-STATIC
supersedes: none (Track C2 pilot prep; next live session is OD-RECOVERY-044)
date: 2026-08-03
observedAtUtc: 2026-08-03T21:30Z
method: scripts/python/replay-delta-extractor.py (new) + roll-replay-time-increased.ps1 -ValueKind (new) against the decoded 11.19.0 Dead Rail session
objective: Close the last autonomous gap before the operator-present Track C2 live run — (1) derive the exact replay-position delta the driver's CompareMode='delta' needs from a decoded session, and (2) let the rolling driver scan Float (position X/Z) in addition to the proven Double (replayTime)
findings:
  - plumbing: delta-compare verified wired end-to-end
    path: ApiContracts OffsetCompareRequest.DeltaTarget/DeltaTolerance → Host.Web GameApiEndpoints (validation: finite, non-negative tolerance, delta-only-with-delta) → GameSessionCoordinator → MemoryScanEngine.PassesDelta
    kinds: FloatValue/DoubleValue/Int32Value/UInt32Value/Int64Value/UInt64Value all supported; Bytes rejected in delta mode
    tests: GameApiEndpointsTests delta cases (2.5 target / 0.1 and 0.25 tolerances) + UltimateScannerUnitTests PassesDelta cover the path
    snapshotKind: snapshot API accepts valueKind Float (MemoryValueKind.FloatValue, valueSize 4, alignment 4) — GameApiEndpoints resolves all six kinds
  - extractor: replay-delta-extractor.py
    session: 019fb86c-c8e7-7004-9df6-a574f5a7835b (11.19.0, Dead Rail, 33,281 position samples)
    tickRate: 1,000,000 ticks/sec (confirmed: sample median dt 992,279 ticks ~= 1s; session span 2,784.6s == duration_ticks 2,713,761,600 / 1e6)
    participant: most-moving (019fb86c-c8f2-78ef-bc09-fa56f50031eb; viewpoint is stationary — median 1s disp 0.0, so auto-selection prefers moving participants)
    window: 4.0s at speed 1.0 → 4,000,000 ticks
    measurements: 2,779 window-straddling pairs via sliding-window linear interpolation (binary search + lerp between ~1s-spaced samples)
    displacement2d: median 0.6935 m, mean 1.2675 m, p90 3.1927 m, max 6.1432 m
    deltaX: median 0.0, mean -0.131, p90 1.048, max 3.1563 | deltaZ: median -0.0, mean -0.5902, p90 0.3719, max 2.1327
    recommended: position -DeltaTarget 0.6935 -DeltaTolerance 2.4992 (Float); replayTime -DeltaTarget 4.0 -DeltaTolerance 2.4992 (Double window*speed)
  - driver: -ValueKind Double|Float
    default: Double (preserves the proven campaign; valueSize 8 / alignment 8)
    Float: valueSize 4 / alignment 4; explicit -Alignment still overrides
    parse: PowerShell AST parse verified (0 errors)
result:
  whatWorked:
    - Delta-compare is fully wired and tested across the whole stack — the pilot needs no new product code.
    - The extractor produces dense, usable marker statistics (2,779 measurements, not the 2 from the first naive straddling-pair scan) via sliding-window interpolation; the recommended target/tolerance are concrete command-line values.
    - The rolling driver now scans Float without changing the default campaign behavior.
  whatFailed:
    - First extractor iteration returned only 2 measurements (pair filter required >=80% of a 4s window between consecutive ~1s samples) — fixed by interpolation-based sliding windows.
  rulesOut:
    - None new (pilot prep; no root hypothesis tested live).
  partials:
    - Track C2 pilot command is ready: `-CompareMode delta -DeltaTarget 0.6935 -DeltaTolerance 2.4992 -ValueKind Float -TransitionSeconds 4 -MaxRounds 22 -HoldAfterRollSeconds 240 -SnapshotMaxBytes 402653184` (or Double replayTime variant).
    - BLK-0019 still needs content-distinct second replay (independentReplays 0).
  nextPivot: OD-RECOVERY-044 - operator-present live run with the proven invocation + the delta pilot above; measure survivor collapse vs 'increased' (11 → predicted <=2-4); then operator Find-what-writes on the staged set.
  repeatWithoutChangedHypothesis: false
artifacts:
  rawFiles: .data/treader.db (decoded sessions; gitignored), scripts/python/replay-delta-extractor.py (committed)
  committedSummary: this ledger entry + workflow next-session + strategy-v2 + driver -ValueKind + extractor tool + handoff (2026-08-03-od-static-044.md)
```

`OD-044-STATIC` is aggregate structural evidence only. Offset remains 0.
The delta-compare pilot is now command-ready with a statically-derived marker:
the 11-survivor replayTime plateau can be filtered by the replay's own
position/time deltas on the next operator-present live run.

## `OD-045-STATIC` result — 2026-08-03 (offline delta-filter simulation)

```yaml
sessionId: OD-045-STATIC
supersedes: none (Track C2 simulation; next live session is OD-RECOVERY-044)
date: 2026-08-03
observedAtUtc: 2026-08-03T21:55Z
method: replay-delta-extractor.py --simulate (new mode) on the 11.19.0 Dead Rail session 019fb86c-… (4s window, 2,779 window measurements, most-moving participant)
objective: Predict which delta marker actually collapses the survivor set across MULTIPLE rolling rounds before the live run spends lease — PassesDelta keeps a candidate when |Δvalue − target| ≤ tolerance, and rolling sheds any candidate that fails ONE round, so per-round pass-rate compounds as pass_rate^N
findings:
  - replayTime-delta marker: DETERMINISTIC, ideal filter
    series: replay-time advance per window == window×speed seconds exactly
    pass: 1.0 at every tolerance swept (0.2 / 0.4 / 1.0 / 2.0 / 4.0s)
    survival: 1.0 at 5/10/15 rounds for all tolerances
    interpretation: the true replayTime field never sheds; decoys whose per-window advance differs shed immediately — this is the filter that breaks the 11-survivor plateau
  - position-delta marker (Float X/Z): BURSTY → HOLLOW collapse
    median 2D displacement 0.6935 m/4s; recommended tolerance 2.4992
    pass-rate at recommended tol: 0.8996 (2500/2779) → survival 0.589/0.347/0.205 at 5/10/15 rounds
    tighter: tol 0.6935 → pass 0.5642 → survival 0.057/0.003/0.0002
    interpretation: the tank stands still for much of the replay, so the TRUE position field produces ~0 displacement windows that shed like decoys — the pilot would collapse survivors but also shed the field (hollow win)
  - speed marker (|pos|/Δt): NOT selective at usable tolerances
    target 0.1734 m/s; passes 1.0 only at tol ≥ 1.387 (8× target)
    interpretation: tolerance that admits the true field also admits everything
  - unit variants for the unknown in-memory replayTime Double
    seconds: 4.0 | milliseconds: 4000.0 | ticks_1e6: 4000000
    (the live operator picks the scale that matches the observed field value)
result:
  whatWorked:
    - The simulator quantifies what intuition could not: the replayTime marker's determinism (pass 1.0 at ALL tolerances) vs the position marker's burstiness (hollow collapse at the recommended tolerance).
    - Survival projection over 5/10/15 rounds gives the operator an explicit risk table before spending lease.
    - Unit variants (s/ms/ticks) handle the unknown replayTime Double scale in one output.
  whatFailed:
    - None (simulation only; no live lease spent).
  rulesOut:
    - Float position-delta pilot at the recommended median-target tolerance as the FIRST delta pilot (hollow collapse risk); re-target to a movement-only window or run it after the replayTime delta pilot.
  partials:
    - Predicted pilot order: 1) Double replayTime delta (`-DeltaTarget 4.0 -DeltaTolerance 0.4`), 2) Float position delta on a movement-only window, 3) operator Find-what-writes.
    - BLK-0019 still needs content-distinct second replay (independentReplays 0).
  nextPivot: OD-RECOVERY-044 - live run the Double replayTime delta pilot FIRST; measure survivor collapse vs 'increased' (11 → predicted <=2-4); then the Float position pilot on a movement-only window; then operator Find-what-writes on the staged set.
  repeatWithoutChangedHypothesis: false
artifacts:
  rawFiles: .data/treader.db (decoded sessions; gitignored)
  committedSummary: this ledger entry + workflow next-session + strategy-v2 + extractor --simulate mode + handoff (2026-08-03-od-static-045.md)
```

`OD-045-STATIC` is aggregate structural evidence only. Offset remains 0.
The simulation reorders the pilot plan: the deterministic replayTime delta
marker is the first filter to run live; the bursty position marker is
downgraded to a movement-only-window second pass.

the 11-survivor replayTime plateau can be filtered by the replay's own
position/time deltas on the next operator-present live run.

## `OD-046-STATIC` result — 2026-08-03 (movement-only windows + HP damage-delta markers)

```yaml
sessionId: OD-046-STATIC
supersedes: none (extends the OD-045-STATIC simulation with two new marker modes)
date: 2026-08-03
observedAtUtc: 2026-08-03T15:20:00Z # approximate; both modes run against the decoded 11.19.0 Dead Rail session 019fb86c
objective: Give the live pilot two more statically-derived options: (1) a movement-only span where the Float position marker is actually selective (OD-045 showed the position marker is hollow over the full replay), and (2) an HP damage-delta series from kind-3 damage events as a sparse-but-exact supporting marker
method:
  primaryTool: scripts/python/replay-delta-extractor.py --movement and --hp-delta --victim-entity <id> (new modes)
  inputSession: 019fb86c-c8e7-7004-9df6-a574f5a7835b (11.19.0 Dead Rail, 2,784.6s, 1e6 ticks/sec)
observations:
  - state: movement-segmentation
    windows: 2756 (1s)
    movingFraction: 0.3233
    movingWindows: 891
    stationaryWindows: 1865
    thresholdMps: 0.5
    movingDisp1s: n=891 median=0.712 mean=0.740 p90=0.992 max=1.489
    note: the Float position pilot should scan a movement-only span (the ~32% moving windows) where the position marker is selective rather than the full replay
  - state: hp-damage-series (kind-3 events: {attackerEntityId, victimEntityId, damage})
    playerEntity: 2549401 (mrkool1138) took 0 damage this replay - marker needs a victim that gets hit (conditional)
    victim2549395: windows=260 hitWindows=5 totalDamage=2618.0 series=[...,512,0,0,0,0,0,0,0,0,0,0,819,0,0,462,0,0,0,0,0,0,0,0,0,0,314,0,511]
    simulation: passRate 0.9808 @ tol 0 (255/260), survival 0.907/0.824/0.747 over 5/10/15 rounds
    note: sparse-but-exact; the true HP field drops by exact damage amounts on hit windows - a supporting marker, not the primary filter
  - state: fix
    issue: truncated module-docstring closer (line 47 was "" instead of """") broke both new modes (SyntaxError invalid decimal literal at line 144 + TokenError EOF-in-string at 374)
    fix: restored the third quote; both modes then compile and run clean
result:
  whatWorked:
    - Movement segmentation quantifies the hollow-collapse fix: only 32.3% of the replay is moving, so the position pilot must scan a movement-only span.
    - HP damage-delta mode decodes kind-3 events into a per-window damage series and runs the survival simulation end-to-end (proven on victim 2549395: 5 exact hits, 2,618 dmg).
    - Root-caused and fixed the file corruption that had left the extractor un-runnable (truncated docstring closer from the in-flight OD-046 edit).
  whatFailed:
    - The player's own entity took no damage in this replay, so the HP marker cannot anchor on the player for this session (conditional on a damaged victim).
  rulesOut:
    - HP-delta as a PRIMARY live filter (sparse; requires a victim that gets hit during the scan window).
  partials:
    - Ranked live pilot markers: 1) Double replayTime delta (deterministic, OD-045), 2) Float position delta on a movement-only span (movement-fraction now quantified), 3) HP damage-delta as supporting evidence if a damaged victim is available.
    - BLK-0019 still needs content-distinct second replay (independentReplays 0).
  nextPivot: OD-RECOVERY-044 - live run the Double replayTime delta pilot FIRST; Float position pilot re-targeted to a movement-only span; operator Find-what-writes on the staged set.
  repeatWithoutChangedHypothesis: false
artifacts:
  rawFiles: .data/treader.db (decoded sessions; gitignored)
  committedSummary: this ledger entry + workflow next-session + strategy-v2 + extractor --movement/--hp-delta modes + handoff (2026-08-03-od-static-046.md)
```

`OD-046-STATIC` is aggregate structural evidence only. Offset remains 0.
The movement-fraction measurement (32.3%) gives the Float position pilot a
target span where it is selective; the HP damage-delta marker is proven
sparse-but-exact but conditional on a damaged victim.

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

## `OD-RECOVERY-044` result — 2026-08-04

```yaml
sessionId: OD-RECOVERY-044
date: 2026-08-04
observedAtUtc: 2026-08-04T05:21:00Z # final run 05:20-05:22Z; 8 launch attempts total, gate green on 5
timebox: 8 managed launches; gate green -> pre-arm -> rolling <=10 -> automated write-trace; replay-start flake dominated the session
decision: FULL PIPELINE MECHANICALLY PROVEN END-TO-END (rolling -> harvest -> address file -> x32dbg direct attach -> scriptload/scriptrun injection -> run); the converged survivor was a KERNEL CLOCK false positive (KUSER_SHARED_DATA.SystemTime), NOT the game field; three driver bugs found and fixed live; x96dbg launcher replaced with direct x32dbg launch
objective: Run the full pipeline (launch -> gate green -> pre-arm -> rolling <=10 -> automated x64dbg-write-trace -AutoWriteTrace) and capture the first {rip}-named savedata evidence
stopCondition: Stop after write-trace completes or gate loss
method:
  primaryTool: scripts/launch-offline-replay-for-od.ps1 (detached) + od-018 session driver + roll-replay-time-increased.ps1 (fixed) + pre-arm-debugger.ps1 (x32dbg direct) + x64dbg-write-trace.ps1 -AutoWriteTrace
  secondaryTools: x64dbg x32 build (C:\work\tools\x64dbg\release\x32\x32dbg.exe) direct attach
  transition: natural replay progression between rounds (1-3s pulses)
  invocation: -SnapshotMaxBytes 402653184 -MaxRounds 40
observations:
  - state: attempt-1 (first full pipeline)
    launchOutcome: gate OfflineReplayVerified
    preArm: x96dbg launcher (release\x96dbg.exe -p) stayed ALIVE with no window and never spawned x32dbg
    rolling: 38900000->...->17 survivors in 22 rounds (round-cap plateau, not <=10)
    writeTrace: FAILED_x64dbg_not_running (no x32dbg process to inject into)
    finding: x96dbg launcher state-machine/ShellExecute brittleness (first-run path) - it must be bypassed
  - state: fix-1
    fix: pre-arm-debugger.ps1 + write-trace resolver now launch release\x32\x32dbg.exe DIRECTLY (game bitness known x86: WOW64-observed, GuardedMemoryReader ImageFileMachineI386)
  - state: attempt-3 (first with x32dbg direct)
    preArm: x32dbg attached pid=49028 (fix WORKED)
    rolling: died at round 3 - game died quietly (replay-start flake)
  - state: attempt (2-round roll)
    rolling: collapsed to 5 survivors <= 10 in just 2 rounds (tightest yet at the time)
    harvest: returned 0 candidates - the tail stopped ticking between the target round and the fresh harvest compare -> EMPTY address file -> write-trace FAILED_survivor_file_empty
    fix-2: harvest retry loop (5x 2s) - survivors tick every frame during active playback
  - state: attempt-4
    rolling: round 9 increased=0 - tail frozen, 0 treated as "target 0" (0 <= 10) -> harvested nothing
    finding: increased=0 is a value-bound PLATEAU signal, NOT a 0-survivor target
    fix-3: plateau-stop (keep last non-zero round's candidates) + small-set candidate serialization bump (TailThreshold) so the last non-zero round's addresses survive a plateau
  - state: attempts-5..7
    result: gate green then Denied within ~90s x3 - the documented replay-start flake dominated (game dies ~2s after LoadGameScene; onLeaveWorld -> become hidden -> OnBackground)
  - state: attempt-8 (final, fully-fixed driver)
    launchOutcome: gate OfflineReplayVerified
    preArm: x32dbg attached pid=45256 via direct launch; marker OK
    rolling: sequence=861399->141156->26922->5011->1093->370->213->139->84->51->47->47->47->47->44->1 (16 rounds)
    targetReached: TARGET survivors=1 le 10 (CAMPAIGN RECORD; previous best OD-020's 5)
    harvest: attempt=1 increased=1 candidates=1 (retry fix live-proven)
    addressFile: %TEMP%\od-survivors.txt count=1 survivors=1 no WARN
    writeTrace: armed=1 (dr_limit=4), script_written (7 lines), x64dbg_pid=45256, injected scriptload+scriptrun, run
    writeTraceStop: STOP_gate=EvidenceStale (lease expired during the trace window - the game had already been dying from the flake)
    hits: 0
  - state: post-run artifact analysis (CRITICAL)
    survivorAddress: 0x7FFE0010
    identity: KUSER_SHARED_DATA.SystemTime - the Windows shared kernel clock page (FILETIME-style 8-byte value, ticks every 100ns)
    implication1: the always-ticking kernel clock is the LAST "increased" Double in a process whose game field stopped ticking (game died mid-roll; the 47->44->1 drop is the death signature)
    implication2: kernel writes to KUSER_SHARED_DATA never fire user-mode hardware breakpoints -> a write-BP there returns 0 hits BY CONSTRUCTION (fully explains the 0 hits; the mechanism was NOT the failure)
    fix-4: roll driver now drops the 0x7FFE0xxx page from the address file + WARN_kuser_clock_dropped so the operator knows the game field stopped
result:
  whatWorked:
    - Full pipeline mechanically proven end-to-end for the first time: gate green -> x32dbg direct attach -> rolling -> TARGET <=10 -> harvest -> address file -> arm -> scriptload/scriptrun injection -> run.
    - Rolling converged to 1 survivor (campaign record; was OD-020's 5) in 16 rounds with the fixed driver.
    - Harvest retry fix live-proven (caught the candidate on attempt 1 after the tail had frozen).
    - Plateau-stop + small-set serialization fixes live-proven (OD-044 live findings).
    - x96dbg launcher replaced with direct x32dbg launch - attaches every time (2/2 direct-attach runs).
    - KUSER_SHARED_DATA false positive identified, explained, and hardened against in the driver.
  whatFailed:
    - No {rip}-named savedata evidence captured: the lease expired during the trace window and the game was already dying from the replay-start flake (0 hits; by-construction for the kernel clock survivor).
    - The replay-start flake dominated: ~50% of 8 launches died within ~2s of gate green (onLeaveWorld -> become hidden -> OnBackground).
  rulesOut:
    - x96dbg launcher as the pre-arm path (ShellExecute/state-machine brittleness; direct x32 build launch is the fix).
    - KUSER_SHARED_DATA survivors as game-field evidence (kernel clock; 0 user-mode HW BP hits by construction).
  partials:
    - The write-trace mechanism is proven; only the live window needs to survive long enough for a hit.
    - A rolled-down survivor at 0x7FFE0xxx is a dying-game signature, not a target - the driver now surfaces it as a WARN.
    - BLK-0019 still needs content-distinct second replay (independentReplays 0).
  nextPivot: OD-RECOVERY-045 - run the Double replayTime DELTA pilot FIRST (-CompareMode delta -DeltaTarget 4.0 -DeltaTolerance 0.4): the offline simulation (OD-045-STATIC) ranks it deterministic (pass-rate 1.0, survival 1.0/15 rounds), its value-bound rejection also sheds the kernel clock (100ns increments fall outside the replay-time delta band), and the operator present during the held green window runs Find-what-writes on the staged <=10 set; alternatively root-cause the replay-start flake (it costs ~half the lease budget) or import a content-distinct second replay.
  repeatWithoutChangedHypothesis: false
artifacts:
  rawFiles: %TEMP%\od-survivors.txt (1 addr: 0x7FFE0010), %TEMP%\od-wt-x64dbg.script (7 lines), %TEMP%\od-prearmed-debugger.json, .data/logs/od-session-20260804-052034.log
  committedSummary: this ledger entry + roll driver hardening (KUSER drop + harvest retry + plateau-stop) + pre-arm x32dbg direct-launch fix + write-trace resolver + handoff (2026-08-04-od-recovery-044.md)
```

`OD-RECOVERY-044` is aggregate structural evidence only. Offset remains 0. The
campaign's live pipeline is now mechanically complete; the binding constraint
moving forward is the replay-start flake eating the lease window, and the next
pilot is the deterministic delta marker.

### Amendment — 2026-08-04 (x96dbg launcher re-verification)

`rulesOut` above states the x96dbg launcher is broken ("ShellExecute/state-machine
brittleness"). **Re-verified headlessly against a live gated session: the launcher
works correctly.** `release\x96dbg.exe -p 50724` (wotblitz.exe, x86/WOW64)
dispatched cleanly to `release\x32\x32dbg.exe -p 50724` — the x86 build,
window title `wotblitz.exe - PID: 50724`, launcher exited (no linger). The
OD-RECOVERY-044 attempt-1 linger was environmental (the game was already dying
that session, so the attach target was unhealthy). Decision stands unchanged:
pre-arm uses direct `x32\x32dbg.exe` for determinism (no ShellExecute/elevation
surface), not because the launcher is defective. Evidence: `%TEMP%\od-x96dbg-verify.txt`
(scratch harness `.data/x96dbg-launcher-verify.ps1`).


## `OD-047` prep — 2026-08-04 (M1 exact-scan session driver + T1/T2 anchors)

```yaml
sessionId: OD-047
supersedes: none (strategy-v3 M1; first live exact-scan campaign)
status: Prepared (driver built + validated; live session not yet run)
```

- **New driver** `scripts/od-047-exact-scan-session.ps1` — the exact-mode
  session wrapper the M0 handoff flagged as missing: waits for
  `OfflineReplayVerified`, then runs the roll driver (`-CompareMode exact`)
  for the three unit variants at T1 (seconds / ms / µs), stages survivors
  per-variant, and emits `.data\od-047-<timestamp>.json` with per-variant
  final survivors. `-RunT2` runs the M2 two-pause fingerprint (re-pause at
  T2, per-variant T1∩T2 intersection in the report).
- **T1/T2 anchors** from the decoded 11.19.0 Dead Rail session
  `019fb86c-c8e7-7004-9df6-a574f5a7835b`: `replay_time_ticks` 599,839,248
  (sequence ≈ 8543) ≈ 60s and 1,199,907,379 (sequence ≈ 26186) ≈ 120s
  (100ns ticks; operator pauses when the HUD clock shows 1:00 / 2:00).
- **Validation:** PS 5.1 + pwsh 7 parse clean; PSSA gate 20 tracked scripts
  0 warnings; preflight fails closed (exit 1) with no host.
- **Not run:** the live session burns one of the 2-session M1 cap and needs
  the operator at the keyboard to pause at T1 — pending operator go-ahead.

---

## OD-048 — strategy-v4 trajectory-correlation capability (2026-08-04)

**Verdict:** capability built, tested, and pushed; live campaign pending
operator go-ahead (burns one of the 2-session M1 cap; no operator input
needed during the run).

**Why (v3 M1 was a design defect):** the exact-pause scan required the
operator to pause at a decoded clock value within ~50ms — machine precision,
not human precision. The pipeline cannot read the very value it hunts, so no
automation could take the pause. The original strategy was always
replay-guided correlation: **stage candidate addresses, monitor them while
the replay plays, score each series against the known replay trajectory**.

**Built and verified:**

- `Core/Discovery/TrajectoryCorrelation.cs` — pure scorer: per-axis (x/y/z)
  with sign flips, piecewise-linear tick lookup, whole-second time-shift
  sweep (±8s default) absorbing Start-marker anchor error; the sweep finds
  ONE consistent shift per (entity, axis, sign) — per-sample independent
  shifts were rejected at review as weak, noisy evidence; stationary
  ground-truth axes and constant observed series excluded. 12 unit tests.
- Replay clock pinned at **10,000,000 ticks/s**: synthetic 120s fixture is
  exactly 1,200,000,000 ticks; real decode 599,839,248 ticks ≈ 59.98s.
- `SqliteTrajectoryGroundTruthProvider` — per-entity downsampled series from
  `position_samples` (≤ 256/entity) + `duration_ticks` +
  `viewpoint_participant_id` (local player).
- `IGameMemoryScanner.ReadAddressesAsync` + `POST /api/v1/game/discover/read`
  — the missing "re-read a fixed staged set" primitive (≤ 2000/call, guarded
  reader, gate-checked; the batch read opens ONE process lease for the whole
  call instead of a handle per address).
- `GET /api/v1/game/discover/trajectory/{sessionId}` +
  `POST /api/v1/game/discover/correlate` endpoints.
- `scripts/od-048-monitor-correlate-session.ps1` — gate wait → stage
  (viewpoint + top movers, 3 axis scans each, `FloatTolerance` 8) → monitor
  loop (re-read every 2s) → correlate → JSON report with verdict +
  `strongSurvivors` (score ≥ 0.7). Staging keeps canonical hex addresses
  end-to-end; warns when the gate is already verified on the first poll (the
  wall anchor would be wrong for a battle already underway) and accepts a
  `-ReplayStartWallTimeUtc` override. PSSA gate 0 warnings (22 tracked
  scripts); preflight fails closed (exit 1) with no host.
- 6 new Host.Web endpoint tests (read validation, trajectory mapping/404,
  correlate scoring/sign-flip/validation) + 12 scorer unit tests incl. the
  per-sample-jitter regression (inconsistent shifts must not score).

**Not run:** the live OD-048 session (launch → let the battle play → read the
report) — pending operator go-ahead.

## 2026-08-04 — OD-048 bug-fix pass (deep analysis, 3 rounds)

**Result: 4 real defects found and fixed in the strategy-v4 build (777f92c).**

- `Downsample` overflow: `tick - long.MinValue` wraps negative for every
  non-negative tick → EMPTY ground truth for every battle > 256 samples
  (real battles are thousands). Regression test with a 300-sample commit.
- Whole-second shift sweep rejected fast movers (0.5s residual × 17 m/s =
  8.5 units > 6 tolerance → true field scored ~0). Sweep now 0.5s-step;
  winning `shiftSeconds` reported for audit.
- Unvalidated `ReplayStartWallTimeUtc` (epoch anchor → silent garbage
  evidence). Scorer throws; endpoint returns `discover.invalid_options`.
- Staging targeted the tick-0 band (unstageable on a moving/loading battle).
  Now: load-settle delay, nearest-tick staging, speed-scaled tolerance,
  retry loop; sweep default ±30s for load latency.

Plus: per-address failure isolation in `ReadBatchAsync`, hex validation for
correlate series, scorer `MaximumTimeShiftSeconds` const used by the endpoint,
coordinator value-kind/width guard.

**Tests:** 15 scorer tests (incl. fast-mover sub-second regression, shift
reporting, anchor rejection), provider downsample regression, endpoint
hardening (incl. null-address 400), full suite 587 passed / 0 failed, PSSA
gate 0 warnings. Committed and pushed; see the strategy-v4 doc and handoff
amendment for details.

## 2026-08-05 — FRESH9 chunked investigation: write-trace root causes fixed (08c6a4f)

**Context.** FRESH9 (live M1→M2) completed every mechanical step — gate
verified, anchor pinned, M1 monitored 220,560 samples / 3,176 addresses,
`evidence-strong` verdict, auto-write-trace fired in-process, scriptload+
scriptrun injected, exit 0 — but produced **zero hits** for the armed x/z
pair (0x2CACA258/0x2CACA260, both score 1.0).

**Result: 4 root causes proven, each verified against x64dbg development-branch source.**

1. **Decimal-pid attach (the zero-hit killer).** x64dbg parses every
   integer literal as hex, so `attach 42284` targets a nonexistent pid.
   The old pre-arm's `x32dbg.exe -p <decimal>` was provably broken.
   Fix: `attach 0x<hex>` via the command bar; pre-arm now launches the
   debugger window only.
2. **SendKeys broken by IME** (mangled `strref 0, 0, 1` landed in the
   command bar). Fix: UIA ValuePattern + focus + PostMessage ENTER.
3. **`bph` arms DRs only on the active thread** (engine has a literal
   `//TODO: hwbp in multiple threads TEST`); after attach that is the
   break-in thread. Fix: memory breakpoints `bpm <addr>, 1, w`
   (guard page, fires on any thread).
4. **Capture idiom:** `bpmwlog` is not a script command; fast-resume +
   condition-0 skips the command; `{rip}`-token savedata froze the
   debuggee. Proven shape: `bpm` + `SetMemoryBreakpointLog` + static
   per-address `savedata`, no condition (first hit pauses, then detach).

**Also caught:** leaked duplicate counter processes from earlier probes
contaminated every zero-hit run (BPs armed at a stale process's address);
stackalloc counter address confounded memory-BP behavior. Rebuilt the rig
as a static-field counter (`tmpwotb-e2e/wt-counter-target.cs`).

**First write evidence ever produced:** clean-rig run of the product's
exact step-5 flow captured `odwt-0x<addr>.bin` (4 bytes) via
`tmpwotb-e2e/probe-integration.ps1`.

**Status: chunks 1–5 done** (memory-BP flow, UIA channel, product-script
fixes, offline integration, static gate — PSSA passed, parse OK, ASCII
clean). **Chunk 6 (FRESH10 live) pending** — needs a hands-off window;
checklist in `docs/operations/handoffs/2026-08-05-fresh9-chunked-write-trace-fix.md`.

**Known limitation:** one capture per armed address per trace (condition-0
and `; run` self-resume are dead ends in this x32dbg build). `ODWT_HIT`
log harvest returned 0 (UIA log-tab read limitation); proof files are the
primary evidence; follow-up is `setlogfile`/active-tab log capture.

## 2026-08-06 — Attach-smoke gate (M2 pre-flight) added to od-048

**Why.** FRESH10 is one scarce CAP-2 launch; the two live-only write-trace
mechanics (x64dbg attach to the real game, memory-BP install) were only
proven offline against the counter rig. Rather than chunking the live run
into multiple launches (each a full session), chunk INSIDE the launch:
fail-closed pre-flight mid-battle.

**What.** `od-048 -AttachSmokeOnFirstRound`: after the first monitor round
proves the game readable, invoke `x64dbg-write-trace.ps1 -AttachSmoke`
against the LIVE game — hex-pid attach → pause → verify the stall (CPU-time
delta) → optional `bpm` arm/clear on the first staged address → detach →
verify resume. Report to `od-048-attach-smoke-*.json`. **Red smoke aborts
with exit 6 before the correlate + trace window is spent** — a live defect
is diagnosed as attach-vs-address, never a mystery no-hit run.

**Validated offline:** parse OK on PS 5.1 + pwsh 7 (both scripts), PSSA
gate passed with 0 findings on the edited scripts, ASCII clean; smoke
DryRun → `smoke: ok` report, no-game fail-closed → `smoke: fail` +
diagnostic. Full attach round-trip remains live-only (FRESH10).

**Status:** FRESH10 is one launch with two internal gates — the attach
smoke (round ~2) and the auto-write-trace (family verdict, battle tail).
Checklist updated in `2026-08-05-fresh9-chunked-write-trace-fix.md` and
`2026-08-05-od-049-session-prep.md`.

## 2026-08-06 — FRESH10 live: attach-smoke gate GREEN, auto-trace mechanics clean, hits=0 (family quality)

**Run.** One launch (Dead Rail content 59c3b92eb221 via 36f5ab..., replay
auto-loops), anchor = LoadGameScene-ends 01:48:53Z, M1 70 rounds / 218,502
samples / 3,178 series, verdict `evidence-strong`, auto-trace fired
in-process.

**MILESTONE: attach-smoke gate proven live.** Round-2 smoke against the
REAL game went green: `attach_smoke ok pid=0xAA6C pause=True bpm=yes
resume=True` (report `od-048-attach-smoke-20260805-214906.json`). The two
live-only write-trace mechanics are now source-confirmed AND
live-confirmed: x64dbg hex-pid attach to wotblitz works, and a memory
breakpoint (bpm) installs on a live game page (log-tab read returned the
breakpoint marker - first time that channel produced data live).

**Auto-trace mechanics all clean:** family selected (2-member x/y),
liveness re-read OK, `attached pid=0xAA6C`, `injected scriptload+scriptrun`,
`released_detach`, exit 0, report + .family.json written.

**But hits=0** (`family-no-hit`). The armed family was WEAK:
`0x22FC05D4 x score=0.20` (noise) + `0x22FC05D8 y score=1.00`. The single
strong survivor (y@1.0, shift -7.5s) rides the sweep EDGE (band
[-10,-7.5]) - the bad-anchor signature the edge audit exists to catch. The
trace window (01:52:47-01:53:12Z) correctly overlapped the battle tail
(battle 2 of the loop ends 01:53:24Z), so timing was NOT the cause this
time. Remaining gap is EVIDENCE QUALITY: the family builder armed a weak
2-member family instead of a clean complete triple; and the y@1.0 survivor
needs its anchor re-audited before it can be trusted as a write site.

**Two run-blocking bugs found live and fixed:**

1. **Anchor UTC-date bug (staged=0 killer).** `Convert-LogTimeToUtc`
   built the anchor from the LOCAL date + log first-column (UTC) time;
   after 20:00 local the UTC date has rolled to the next day, so the
   anchor landed ~20h in the past and every round saw
   `staging_budget_exhausted staged=0` (elapsed_s=86403.9). Fixed: base
   the anchor date on `[datetime]::UtcNow.Date` with a rollover guard.
   **Propagated to all 5 anchor producers** (fresh-launch-m1,
   gate-watch-m1, hangar-m1, od-049-autoloop, reclick-m1).
2. **Launch flow must run under PowerShell 5.1, not pwsh 7.** The
   click-watch-offline.ps1 Add-Type needs System.Drawing (in .NET
   Framework, not the .NET Core shared framework): pwsh 7 fails with
   CS1069 (Bitmap type forwarded to System.Drawing.Common). FRESH10's
   first attempt died at watch_exit=4; relaunching the wrapper under
   powershell.exe (5.1) got the full pipeline through. od-048 itself
   still runs under pwsh 7 (PS7-only syntax) - the wrapper already splits
   this correctly.

**Status: chunk 6 (FRESH10) COMPLETE - mechanics proven, evidence pending.**
The debugger layer is no longer the unknown. Next: (a) hold the auto-trace
for a complete family (3 axes) or a 2-member family with BOTH members
score >= ~0.9, (b) re-audit the y@1.0 survivor's anchor (edge-riding shift
= suspect), (c) consider arming earlier in the battle tail so position
writes are still flowing.

## 2026-08-06 — FRESH11: family-gate score floor (no more noise-member arming)

**Why.** FRESH10 armed x@0.20 (noise) + y@1.00 and the trace burned the
green window on the noise member (family-no-hit). Root cause: the
usable-family gates in BOTH scripts checked member COUNT + edge-alignment
but never member SCORE, and od-048 blind-picked `completeFamilies[0]`
(complete only proves 3 axes + no edge, not that members scored).

**What.**
- `x64dbg-write-trace.ps1`: new `-MinMemberScore` (default 0.9);
  `Test-FamilyScored` (every member >= floor; missing score = 0);
  `Test-UsableFamily` gates on score first; `Select-BestFamily` filters
  EVERY tier (complete/usable/any) through the floor and returns $null
  when nothing clears it; `FAILED_family_selection` names the floor.
- `od-048`: new `-AutoTraceMinMemberScore` (default 0.9); gate requires
  weakest member score >= floor before the edge check; blind
  completeFamilies[0] shortcut REMOVED; skip log carries the reason;
  floor passed through to the write-trace (`MinMemberScore` splat) so
  both gates can never disagree. Review fixes: skip reason reports the
  BEST near-miss (not the last-scanned family), and a missing-score wire
  regression is called out distinctly (`members_missing_score`) instead
  of masquerading as weakest_score=0.00.

**Validated:** parse OK PS 5.1 + pwsh 7, PSSA 0 findings on edited
scripts, ASCII clean. Functional: the REAL FRESH10 family file
(x@0.20+y@1.00) is now REFUSED (`no_family_clears_min_score=0.9`, exit 2);
a synthetic x@0.98/y@1.00 family still ARMS (exit 0, full bpm script).
Behavior note: ad-hoc score-less family JSONs now need `-MinMemberScore 0`
(the write-trace's own .family.json and od-048 reports always carry scores).

**Next (FRESH12):** re-audit the edge-riding y@1.0 survivor (shift -7.5
sitting on band edge [-10,-7.5]) - decide if the anchor is off or the
address is real, then a clean-family live round.

## 2026-08-06 — FRESH12 offline audit: the "edge-riding y@1.0 survivor" is REAL evidence, not a bad anchor (CORRECTED verdict)

**Question:** FRESH10's only strong y survivor (0x1FC57238, y@1.00, shift -7.5s)
was labelled "edge-riding" and demoted. Is the anchor off, or is the address
real? Decided OFFLINE from the FRESH10 report + the live host DB — no new
session spent.

**Verdict: the address is real evidence; the FRESH10 writeup misread the
ambiguity band as the sweep edge.**

- 0x1FC57238: y@1.000 (69/69), entity 2549406, obsSpan=2.8, shift=-7.5s,
  band=[-10,-8] (2.5s WIDE, INTERIOR), edgeAligned=**False**. The ambiguity
  band is the set of shifts achieving max match count (width = tolerance /
  |local slope|). A 2.5s-wide interior band 18s inside the ±30 sweep is the
  OPPOSITE of degenerate: the observed series reproduces entity 2549406's y
  shape at exactly one alignment. FRESH10 called shift -7.5 "band edge
  [-10,-7.5]" — that band IS the ambiguity band, and its edge (-10) is nowhere
  near the sweep edge (±28 threshold). The address survived every discriminator
  this pipeline has.
- The x-axis ground truth for entity 2549406 moves 70.6 units over 278.5s;
  y moves only 10.9 units (avg slope ~0.04 u/s). Band width = 6.0/0.04 = 150s
  → clamped to the whole sweep. **A near-flat ground axis makes y@~1.0 cheap**:
  42 of 50 results were y-axis and 30/50 had ambiguity bands >20s (up to the
  full 60s) — those "1.0" scores match at EVERY shift and carry zero
  information. 0x1FC57238 is the ONLY result in the run with a tight band
  (<=6s) AND not edge-aligned.
- **The armed family was the worst possible pick**: fam0 = x@0.20 (noise) +
  y@1.00, and the y member (0x22FC05D8) has a degenerate band [-10,+30]
  (matches at any shift, obsSpan only 4.0). The report's family members carry
  NO band fields (all [0,0] on the wire) — the discrimination information is
  LOST in family serialization, so the auto-trace gate could never see it.
- 0x1FC57238 was not even IN a family — its ±16-byte neighbors scored below
  the seed floor, so the family builder never grouped it and the auto-trace
  could only arm family members. The best evidence in the whole run was
  structurally excluded.

**Consequence:** FRESH10's hits=0 is explained: the trace armed degenerate
family members (a noise x + an any-shift y), not the genuine survivor. The
score floor (FRESH11) prevents noise members now, but a degenerate member
still scores 1.0 and passes.

**Next (FRESH13):** (1) enforce a BAND-WIDTH floor in the family gate — a
member whose ambiguity band covers more than ~1/3 of the sweep is degenerate
regardless of score; (2) serialize shiftMin/shiftMax per family member so the
gate can see it; (3) consider a "solo survivor" arming path so a tight-band
non-edge high-score address (like 0x1FC57238) can be traced even without a
byte-window family.

## 2026-08-06 — FRESH13: band-width floor in the family gate (implemented + validated offline)

The FRESH12 finding implemented: a family member whose ambiguity band covers
too much of the sweep matches at ANY shift, so its score is cheap (FRESH10's
armed y@1.00 had a [-10,+30] = 40s band on a 60s sweep -> family-no-hit even
with a perfect score). The score floor (FRESH11) cannot catch this; the BAND
WIDTH can.

**What.**
- `x64dbg-write-trace.ps1`: new `-MaxMemberBandSeconds` (default 20.0 = 1/3 of
  the od-048 default ±30s/60s sweep); `Get-MemberBandWidth` (accepts BOTH wire
  pairs — correlate response emits shiftMin/MaxSeconds, the M1 report re-emits
  shiftBandMin/MaxSeconds); `Test-FamilyBanded` (every member band known and
  ≤ floor; unknown band = refuse, fail-closed; 0 disables entirely);
  `Test-UsableFamily` gates on it; `Select-BestFamily` applies it to EVERY tier
  via a PARENTHESIZED filter — a bare `Test-FamilyScored -Family $_ -and
  (Test-FamilyBanded ...)` parsed `-and` into the first command's argument
  binding (harness-proven: the un-parenthesized form selected a bandless
  family; the parenthesized form refused it); `.family.json` output now carries
  shiftBandMin/MaxSeconds per member (normalized from either wire pair).
- `od-048`: new `-AutoTraceMaxMemberBandSeconds` (default 20.0); the gate loop
  computes each family's widest member band and refuses degenerate families
  (`degenerate_member_band widest_band=Ns floor=Ns`) or unknown-band wire
  (`member_band_unknown`); best-near-miss band tracked for the skip log;
  floor 0 disables entirely (parity with the write-trace); splat passes
  `MaxMemberBandSeconds` through so both gates agree.

**Validated (offline, no session spent):** parse OK PS 5.1 + pwsh 7, PSSA 0
findings on both scripts, ASCII clean. Gate matrix (exit codes): the real
FRESH10 report -> refused (exit 2); band-only fixture (scores 0.98/1.0 pass
the score floor, y band 40s) -> refused — proves the band floor catches what
the score floor cannot; bandless wire -> refused (fail-closed); floor disabled
(0) -> bandless arms (parity); the tight-band synthetic family (0x1FC57238
metrics: 2s interior band, score 1.0, non-edge) -> arms (exit 0).

**Honest caveat:** 0x1FC57238 is a RESULT, not a family member (its ±16-byte
neighbors scored below the seed floor), and both gates require ≥2 members — so
the real FRESH10 report still cannot arm it. Its METRICS pass the new floor;
arming it standalone needs the solo-survivor path (FRESH13 follow-up, not
implemented here). A future live round must not misread a ≥2-member refusal
as the band floor failing.

**Next:** solo-survivor arming path (trace a tight-band non-edge high-score
address without a byte-window family), then a clean-family live round.

## `OD-RECOVERY-046` result — 2026-08-07 (FRESH37: first durable module-mapped write-site hit)

```yaml
sessionId: OD-RECOVERY-046
date: 2026-08-07
observedAtUtc: 2026-08-07T06:58:00Z
timebox: 3 live launches (2 honest timing negatives then the hit); interceptor publish + Host Release build fresh
decision: FIRST DURABLE MODULE-MAPPED WRITE-SITE HIT (family-hit, 4 real writes inside the live battle); write sites resolve to VCRUNTIME140.dll+0xED69 / +0xE8AE = CRT struct-copy sites; the armed x-coordinate is a synchronized multi-copy field, NOT a direct movss store; no offset promoted
objective: Capture a write-trace whose RIPs map to modules (closing the FRESH36 durability gap) using the proven live choreography
stopCondition: family-hit with durable .capture.json + .family.json, or gate loss
method:
  primaryTool: od-049-autoloop.ps1 (-AttachSmokeOnFirstRound -StageViewpointOnly -PlaybackSpeedEstimate 2.4 -StageMinBattleSeconds 30) -> C# WriteInterceptor auto-trace (invoke-csharp-write-trace.ps1)
  secondaryTools: offline WriteSiteAnalysis (pure Core), 135-module attach-time snapshot
  transition: natural replay progression
  invocation: -PlaybackSpeedEstimate 2.4 -StageMinBattleSeconds 30
observations:
  - state: attempt-1 (2.4x alone)
    outcome: 3 monitor rounds, verdict=no-evidence - the 55s staging gate is wall-clock and consumed the whole fire-by budget at 2.4x
    finding: timing negative, not field negative
  - state: attempt-2 (default 2.0)
    outcome: 15 rounds, evidence-strong, 50 x-strong survivors, top score 0.857 < 0.9 solo floor -> no family, no trace (fail-closed held)
    finding: score variance below the AutoTraceMinMemberScore floor
  - state: attempt-3 (2.4x + StageMinBattleSeconds 30)
    outcome: 16 rounds, score-0.933 solo family, family-hit: 4 real writes, windowValuesChanged=true, 135 modules
    writeSites: VCRUNTIME140.dll+0xED69 (4-dword copy loop: mov [edi],edx; add edi,4; sub ecx,1; jnz) hits=3; +0xE8AE (rep movsb) hits=1
    capturedValues: -0.0003 / 245.35 / -124.00 (coherent world coordinates)
    registers: edi=0x22BA1020/0x22BA1060 (dest base), esi=0x2C269518/0x2C26A670/0x2C26E900 (copy sources), ebx=0x22BA10A0
    finding: the armed coordinate sits at edi+0xB0 inside the memcpy destination struct - the write is a CRT struct copy, not a direct movss
candidates:
  - rawAddress: local-only
    absoluteAddress: local-only
    moduleRelativeOffsetHex: VCRUNTIME140.dll+0xED69 / +0xE8AE
    addressKind: heap-dynamic (write site); the field itself remains unknown
```

Offline `WriteSiteAnalysis` (real Core code, both family-hit captures) confirms the
struct-copy class: resolver `unknown/ambiguous` with **object-base candidates
`ebx+0x70` / `edi+0xB0` (support 2 each)** on the 4-hit capture. The next write
site is one level up — the memcpy **source** buffers held in `esi`.

## `OD-RECOVERY-047` result — 2026-08-07 (FRESH38: hit reproduced; cross-battle source-arm ruled out)

```yaml
sessionId: OD-RECOVERY-047
date: 2026-08-07
observedAtUtc: 2026-08-07T07:22:00Z
timebox: same-process two-pass (phase A reproduce with -KeepGame, phase B arm esi sources in battle 2); phase A hit, phase B invalid by live evidence
decision: HIT REPRODUCED (M3 repeatability step 1); CROSS-BATTLE SOURCE-ARM RULED OUT - the esi copy sources are battle-scoped heap allocations, not process-stable buffers; the changed hypothesis is a same-window dynamic source-arm, now implemented + offline-tested
objective: Reproduce the FRESH37 hit, then arm the memcpy source addresses captured at hit time to catch the game's real per-frame write site
stopCondition: family-hit + fresh esi capture in the same process, then phase B within the same launch
method:
  primaryTool: od-049-autoloop.ps1 proven invocation + -KeepGame (phase A); interceptor direct arm of freshly captured esi sources (phase B)
  secondaryTools: blitz-log battle-boundary watch, offline WriteSiteAnalysis
  transition: game auto-loop into battle 2 (same process)
  invocation: -AttachSmokeOnFirstRound -StageViewpointOnly -PlaybackSpeedEstimate 2.4 -StageMinBattleSeconds 30 -KeepGame
observations:
  - state: phase-A (phaseA1)
    outcome: families=0 - top survivor scores 0.8 (tight 3s bands), under the 0.9 solo floor; no auto-trace, no fresh esi capture
  - state: phase-A2
    outcome: family-hit (2 real writes, 1 write site VCRUNTIME140.dll+0xED69, valuesChanged=true, 135 modules) - second reproducible hit; game stayed alive into battle 2
    esiSources: 0x2DDBB418 / 0x3EBEB878 (vs FRESH37's 0x2C2A9E18 / 0x2C2AB2E0 / 0x2C2AD880)
  - state: phase-B
    outcome: invalid by evidence - esi values differ per hit within one window (~0x110000 apart) and per launch; battle 2 reallocates them; the game process also exited after battle 2
    finding: memcpy sources are per-battle heap allocations; arming captured sources in a later battle is invalid for the same reason cross-process arming was
candidates:
  - rawAddress: local-only
    absoluteAddress: local-only
    moduleRelativeOffsetHex: VCRUNTIME140.dll+0xED69
    addressKind: heap-dynamic (write site); the field itself remains unknown
```

**Changed hypothesis (implemented, offline-tested):** arm the page containing
`esi` **in the same trace window it is discovered** — `Interceptor.cs` now
dynamically arms the esi copy-source page on the first captured hit
(`-ArmSourceOnFirstHit`, `sourcePagesArmed` cap 8, source-kind hits tagged in the
report). Offline mechanism test passes end-to-end: the synthetic counter faults
inside a real CRT memcpy (esi=source/edi=dest like the game), the source page is
armed at hit time, and the fill-site write is captured as a **source-kind hit at
a distinct RIP**. Ledger rule: do not repeat cross-battle/cross-process source
arming without a changed hypothesis (this IS the changed hypothesis).

## `OD-RECOVERY-048` result — 2026-08-07 (FRESH40: 4th consecutive sub-0.9; sample-depth diagnosis)

```yaml
sessionId: OD-RECOVERY-048
date: 2026-08-07
observedAtUtc: 2026-08-07T11:30:00Z
timebox: live round on the primary 11.19.0 replay, proven invocation + -ArmSourceOnFirstHit (the FRESH39 changed hypothesis, first live attempt)
decision: NEGATIVE (honest) - no family emitted, auto-trace skipped, source-arm unexercised; root cause diagnosed offline as sample-depth score quantization, not a driver defect
objective: exercise the FRESH39 dynamic source-arm live by reproducing a >=0.9 solo family and catching the game's real fill-site write
stopCondition: family >= 0.9 emitted, auto-trace fires, source-arm arms the esi page and traps a source-kind hit
method:
  primaryTool: od-049-autoloop.ps1 -AttachSmokeOnFirstRound -StageViewpointOnly -PlaybackSpeedEstimate 2.4 -StageMinBattleSeconds 30 -AutoTraceSeconds 25 -ArmSourceOnFirstHit
  secondaryTools: offline diagnosis against od-048 source + FRESH37/38 control runs
  invocation: proven invocation + -ArmSourceOnFirstHit (first live use)
observations:
  - state: M1 correlate
    outcome: verdict=evidence-strong, addresses_scored=526 (130 viewpoint-only), total_samples=7364, strong_survivors=20, families=0
    topScores: 0.857 (x2), then 0.786 (x6) - all under the 0.9 solo-emission floor; family_mapping_failed no_families_from_survivors; M2 stop rule held
  - state: auto-trace
    outcome: SKIPPED no_usable_family - source-arm never armed (requires a family hit first)
  - state: diagnostics
    measuredPlaybackSpeed: None - expected fallback, NOT the discriminator (FRESH38's 0.933 hit ran the identical estimate-only path: no measured speed, fire-by-deadline stop)
    stagingTolerance: 0.001 exact-match (unchanged; the old max-speed x load-latency auto-scale is gone from the code)
    scoreQuantization: 0.857 = 6/7 and 0.786 = 11/14 match ratios; FRESH40 scored fewer addresses (526 vs 598) and fewer samples (7364 vs 8970) than the FRESH38 hit run, so ratios quantize coarser and bands widen
candidates:
  - rawAddress: 0x23D33B50 (top z survivor, score 0.857)
  - rawAddress: 0x23CCC2D0 (top x survivor, score 0.857)
  - moduleRelativeOffsetHex: none - no trace ran
  - addressKind: unknown (staged heap candidates; no write-site evidence this run)
```

**Diagnosis (offline, no game):** `measuredPlaybackSpeed: None` and the 50s
attendance latency were both ruled out as discriminators — FRESH38's 0.933 hit
ran the identical estimate-only path with the same fire-by-deadline stop. The
cap at 0.857 is correlation score quantization from a thinner sample grid
(526 addresses / 7364 samples vs the hit run's 598 / 8970). **Changed
hypothesis before the next live round:** sharpen the sample grid
(`-ReadIntervalSeconds 1.0 -MaxReadRounds 120`) so the score ratio quantizes
finer (6/7 -> 14/16) and ambiguity bands tighten. Ledger rule: do not repeat
the proven invocation unchanged a fifth time.

## `OD-RECOVERY-049` result — 2026-08-07 (FRESH41: sample-grid hypothesis tested live and refuted)

```yaml
sessionId: OD-RECOVERY-049
date: 2026-08-07
observedAtUtc: 2026-08-07T11:47:00Z
timebox: live round with the OD-RECOVERY-048 changed hypothesis (-ReadIntervalSeconds 1.0 -MaxReadRounds 120) + proven invocation + -ArmSourceOnFirstHit
decision: NEGATIVE (honest) - the sample-grid changed hypothesis was tested and REFUTED: 2x samples held the top score at ~0.85, not the predicted 14/16; no family, trace skipped, source-arm unexercised; ledger rule stops further identical rounds
objective: test whether a finer sample grid pushes the correlation score over the 0.9 solo floor (score quantization hypothesis from OD-048)
stopCondition: family >= 0.9 emitted, auto-trace fires, source-arm arms and traps a fill-site hit
method:
  primaryTool: od-049-autoloop.ps1 -AttachSmokeOnFirstRound -StageViewpointOnly -PlaybackSpeedEstimate 2.4 -StageMinBattleSeconds 30 -AutoTraceSeconds 25 -ReadIntervalSeconds 1.0 -MaxReadRounds 120 -ArmSourceOnFirstHit
  secondaryTools: FRESH37/38/39/40 control runs for the score-distribution comparison
  invocation: sample-grid fix + proven invocation + -ArmSourceOnFirstHit
observations:
  - state: M1 correlate
    outcome: verdict=evidence-strong, addresses_scored=589, total_samples=15314 (2x FRESH40's 7364), 27 rounds (vs 15-16), strong_survivors=15, families=0
    topScores: 0.846 (11/13, x3 z-survivors), then 0.769 - the finer grid did NOT quantize finer; ~0.85 is the axis's inherent run-to-run correlation
    bandWidths: top z survivors carry ~45s ambiguity bands (6 -> 51.5/52s) - low discrimination vs FRESH37's hit at 0.933 with a 6.5s band
  - state: auto-trace
    outcome: SKIPPED no_usable_family - source-arm never armed (requires a family hit first)
  - state: hypothesis test
    scoreQuantization: REFUTED - 7364->15314 samples and 15->27 rounds held top score at 0.846 (11/13 ~= 6/7), not the predicted 14/16
    inherentVariance: the ~0.85 cap is run-to-run correlation variance of the viewpoint axis class, matching FRESH37's documented 'pure variance' (0.8-1.0 observed on identical configs)
candidates:
  - rawAddress: 0x23A56AD0 (top z survivor, score 0.846, band 6-51.5s)
  - rawAddress: 0x23B163D0 (top z survivor, score 0.846, band 6-52s)
  - rawAddress: 0x236E5510 (top x survivor, score 0.769)
  - moduleRelativeOffsetHex: none - no trace ran
  - addressKind: unknown (staged heap candidates; no write-site evidence this run)
```

**Ledger rule applied:** two changed hypotheses tested, both honest negatives
(proven invocation x4 at 0.8-0.867; sample-grid x1 at 0.846). **No further live
rounds on this replay with the current scoring setup.** Next moves are offline:
(1) aggregate the five rounds' strong-survivor distributions to test whether the
0.9 solo floor sits inside the natural score range (~0.77-0.93) - if so, retune
the floor or emission selection with evidence; (2) band-width vs score study -
FRESH37's hit paired 0.933 with a 6.5s band vs today's 45s bands, so a
band-weighted emission selector (prefer tight-band over score-max) is
offline-testable; (3) `independentReplays` still 0 (BLK-0019 unchanged).

## `OD-RECOVERY-050` result — 2026-08-07 (offline score-distribution analysis + band-weighted emission implemented)

```yaml
sessionId: OD-RECOVERY-050
date: 2026-08-07
observedAtUtc: 2026-08-07T16:10:00Z
timebox: pure offline - aggregate the five FRESH rounds' strong-survivor distributions (FRESH37/38/39/40/41, 76 survivors) and derive + implement the band-weighted emission hypothesis
decision: HYPOTHESIS DERIVED + IMPLEMENTED (offline-tested) - the flat 0.9 score floor mis-ranks: both hits were tight-band x (0.5s/6.5s at 0.933) while the refused class splits into tight-band x at 0.857 (FRESH40, should emit) vs wide-band z at 0.846-0.857 (45-65s, should refuse); band-weighted floor + band-asc selection implemented in od-048 AND the write-trace so both gates agree
objective: test whether the 0.9 solo floor sits inside the natural correlation range and derive an evidence-based retune
stopCondition: n/a (offline) - hypothesis validated against all five real reports before any live round
method:
  primaryTool: .data/score-distribution-analysis.py (aggregate 76 strong survivors) + od-048 emission-block harness (tmpwotb-e2e/test-solo-emission.ps1) + end-to-end replay of the new floor against FRESH38/40/41 real reports
  secondaryTools: PSSA gate, offline pack gate
  invocation: none (offline)
observations:
  - state: score distribution (76 strong survivors, 5 rounds)
    outcome: round-top range 0.800-0.933; the 0.9 floor sits at the 90th percentile of round-tops (2/8 rounds clear) - reachable but a ~30% gate, not wrong per se
    finding: the 0.9 floor is NOT the problem; the problem is it is band-blind - a tight-band x@0.857 (FRESH40, 3.0s band, same class as both hits) is refused identically to a wide-band z@0.846 (FRESH41, 45.5s band, low discrimination)
  - state: band vs score relationship
    outcome: both hits = tight-band (0.5s/6.5s) at 0.933; refused misses split into tight-band x@0.857/3s (FRESH40, would emit) and wide-band z@0.846-0.857/45-65s (FRESH41/40, correctly refuse)
    finding: band width is the discriminator, not raw score - the score is a match ratio that quantizes coarsely on short series (6/7=0.857, 14/15=0.933)
  - state: implementation (od-048 + invoke-csharp-write-trace.ps1)
    outcome: band-weighted floor (AutoTraceTightBandMinScore 0.85 for band <= AutoTraceTightBandMaxSeconds 10s; strict 0.9 for wide bands) applied identically in the solo emission block, the family-usable gate, AND the write-trace's Test-FamilyScored (threaded via TightBandMinScore/TightBandMaxSeconds wtArgs) so od-048 approve can never be refused there; selection order changed to span desc -> BAND ASC -> score desc (was score before band)
    finding: FRESH40's 0.857 x/3.0s would now emit (the exact candidate the flat floor refused); FRESH38 still emits its 0.933 hit plus 2 tight x-siblings; FRESH41's wide z still refused - 2/8 -> 4/8 rounds emit, all tight-band x class
candidates:
  - moduleRelativeOffsetHex: none - no live trace ran this round (offline only)
  - addressKind: n/a
```

**Validated offline against all five real reports before any live round.** The
emission harness now covers four cases: historical fixture emits, static
degenerate refused (span floor), **tight-band x@0.857 emits (new)**, wide-band
z@0.846 refused (new). PSSA gate + offline pack gate green. **Changed
hypothesis for FRESH42:** run the proven invocation + `-ArmSourceOnFirstHit`
with the band-weighted floor in place - the next live round should emit the
tight-band x class at 0.85+ and finally exercise the source-arm.

## `OD-RECOVERY-051` result — 2026-08-07 (FRESH42: band-weighted floor live, first test — roll produced no 0.85+ candidate)

```yaml
sessionId: OD-RECOVERY-051
date: 2026-08-07
observedAtUtc: 2026-08-07T16:30:00Z
timebox: live round with the OD-RECOVERY-050 band-weighted emission (tight-band <=10s clears at 0.85, wide-band needs 0.9) + proven invocation + -ArmSourceOnFirstHit
decision: NEGATIVE (honest) but informative - the band-weighted floor behaved CORRECTLY (top survivors 0.800 x/0s-band were refused, exactly as designed); this round's roll simply produced no tight-band candidate >= 0.85, so the new floor has NOT yet been observed emitting live - one more roll is within the changed-hypothesis warrant
objective: first live test of the band-weighted floor; emit the tight-band x class at 0.85+ and exercise the source-arm
stopCondition: tight-band survivor >= 0.85 emitted, auto-trace fires, source-arm arms and traps a fill-site hit
method:
  primaryTool: od-049-autoloop.ps1 -AttachSmokeOnFirstRound -StageViewpointOnly -PlaybackSpeedEstimate 2.4 -StageMinBattleSeconds 30 -AutoTraceSeconds 25 -ArmSourceOnFirstHit (band-weighted floor = new od-048 defaults)
  secondaryTools: score-distribution analysis (OD-RECOVERY-050)
  invocation: proven invocation + -ArmSourceOnFirstHit (band-weighted floor active)
observations:
  - state: M1 correlate
    outcome: verdict=evidence-strong, addresses_scored=528, total_samples=7920, 16 rounds fire-by-deadline, strong_survivors=20, families=0
    topScores: 0.800 (x, 0s band, x2), then 0.733 (x) - ALL below the 0.85 tight-band floor; no candidate >= 0.85 existed to emit
    floorBehavior: CORRECT - the 0.800 x survivors were refused by the band-weighted floor (0.800 < 0.85), exactly as designed; this is not a floor defect, it is a low roll (the round's top score is at the bottom of the observed 0.80-0.933 range)
  - state: auto-trace
    outcome: SKIPPED no_usable_family - no candidate crossed the floor; source-arm never armed
  - state: hypothesis status
    bandWeightedFloor: validated as CORRECT (refuses sub-0.85), but not yet observed EMITTING live - FRESH42 rolled 0.800 tops; the floor converts 0.857+ tight-band rounds (FRESH40 class) into emissions and needs a roll that produces one
    emissionRate: observed round-top distribution 0.800/0.867/0.800/0.857/0.846/0.800/0.800/0.933/0.933 - the floor emits ~4/8 rounds by the offline replay; FRESH42 drew the 0.800 tail
candidates:
  - rawAddress: 0x23BEF4D0 (top survivor, x@0.800, 0s band - refused, below 0.85 floor)
  - moduleRelativeOffsetHex: none - no trace ran
  - addressKind: unknown
```

**Floor verified correct; emission not yet observed live.** The band-weighted
floor refused the 0.800 tops exactly as designed (0.800 < 0.85 tight floor) —
no floor defect, no candidate existed. FRESH42 drew the low tail of the
round-top distribution. **One more roll (FRESH43) is within the changed-
hypothesis warrant**: the floor has not yet had a live emission to observe,
and the offline replay proves it converts ~4/8 rounds (including the FRESH40
0.857 x/3s class). A second identical roll to give the floor a fair live test
is not "repeating an exhausted approach" - it is completing the first test of
the new approach.

## `OD-RECOVERY-052` result — 2026-08-07 (FRESH43: FIRST DURABLE GAME-CODE FILL-SITE HIT)

```yaml
sessionId: OD-RECOVERY-052
date: 2026-08-07
observedAtUtc: 2026-08-07T16:37:00Z
timebox: live round - second roll of the band-weighted floor (within the FRESH42 warrant), proven invocation, -ArmSourceOnFirstHit
decision: HIT - the dynamic source-arm caught the game's per-frame fill site: wotblitz.exe+0x7C39AB writes the memcpy source buffer, VCRUNTIME140.dll+0xED49 SSE-stages 4-float chunks into it, and VCRUNTIME140.dll+0xE8AE (rep movsb) propagates into the armed member 0x22AB0F90
objective: give the band-weighted floor a fair live emission test AND exercise the source-arm against a real family (FRESH39/40/41 never armed)
stopCondition: family-hit with values_changed=true and a module-mapped write site, or clean negative
hypothesis: a same-window dynamic source-arm (arm the esi page at first hit) will catch the game code that fills the memcpy source buffer
```

### Evidence (all six hits, one game process, thread 45272)

| # | Address | Kind | RIP | Module RVA | Instruction | Value |
|---|---|---|---|---|---|---|
| 1 | `0x22AB0F90` | member | `0x5B24E8AE` | `VCRUNTIME140.dll+0xE8AE` | `F3A4` rep movsb | 0 |
| 2 | `0x28FFCF10` | source | `0x5B24ED49` | `VCRUNTIME140.dll+0xED49` | `F30F7F07` movdqu | 0.000457 |
| 3 | `0x28FFCF14` | source | `0x5B24ED49` | same | movdqu | 2.5736 |
| 4 | `0x28FFCF18` | source | `0x5B24ED49` | same | movdqu | -0.1119 |
| 5 | `0x28FFCF1C` | source | `0x5B24ED49` | same | movdqu | -0.1119 |
| 6 | `0x2C5C8A90` | source | `0x013F39AB` | **`wotblitz.exe+0x7C39AB`** | `8B83A0000000`… | 0.008282 |

Module bases verified against the capture's own module table:
`wotblitz.exe` base `0x00C30000` size 0x4482000; `VCRUNTIME140.dll` base
`0x5B240000` size 0x15000. RVA re-derived: `0x013F39AB − 0x00C30000 =
0x7C39AB`; `0x5B24E8AE − 0x5B240000 = 0xE8AE`; `0x5B24ED49 − 0x5B240000 =
0xED49`.

### Analysis

- **Hit 1 is the propagation copy**: `rep movsb` wrote into the armed member
  `0x22AB0F90` (the x-position candidate, score 0.933, span 46.8s). The
  captured `esi` = 687,853,328 = **`0x28FFCF10` exactly** — the copy source
  pointer.
- **Hits 2–5 are the source refill**: `movdqu` at `VCRUNTIME140.dll+0xED49`
  stored 4 consecutive floats into that exact source buffer (`0x28FFCF10`..
  `1C`). Values (0.000457, 2.574, −0.112, −0.112) are plausibly x, y, z + one
  more — the source buffer holds the position triple consecutively.
- **Hit 6 is the game fill site**: **`wotblitz.exe+0x7C39AB`** — game code,
  not CRT — wrote a float into the second armed source page at `0x2C5C8A90`
  (`sourcePagesArmed=2`).

**Write chain (answers FRESH37's question):** game code fills a staging
buffer (`wotblitz.exe+0x7C39AB`) → CRT vectorized copy stages 4-float chunks
(`VCRUNTIME140.dll+0xED49`) → `memcpy` propagates into the tracked position
field (`VCRUNTIME140.dll+0xE8AE` → member `0x22AB0F90`). The member address is
a copy destination; the *fill* is at `wotblitz.exe+0x7C39AB`.

### M3 status

- `complete=False` on the family: z member `0x23A4C490` was armed but not hit
  in the 25s window (only the x destination took the copy). Multi-copy family
  confirmed — one source, several destinations.
- `independentReplays` still 0 (BLK-0019 unchanged). The write-site evidence is
  durable (capture/family/trace JSONs on disk, module table, full registers).

### Next

1. Ghidra-disassemble `wotblitz.exe+0x7C39AB` (FindOffsets.py workflow) to
   identify the enclosing function and the source-page pointer chain.
2. Test reading x/y/z directly from the source buffer layout hinted by the
   `movdqu` 4-float store (0x28FFCF10..1C consecutive).
3. M3 repeatability: one more live family-hit on a fresh battle to confirm
   `0x22AB0F90`-class destinations + `0x7C39AB` fill persist; then evaluate
   promotion.
4. `independentReplays`: import a second replay and re-run the emission to
   satisfy BLK-0019.

## `OD-RECOVERY-053` result — 2026-08-07 (offline: Ghidra decode of the FRESH43 write site — transform-object hypothesis)

```yaml
sessionId: OD-RECOVERY-053
date: 2026-08-07
observedAtUtc: 2026-08-07T14:22:00Z
timebox: pure offline - Ghidra headless disassembly + decompile of the FRESH43 write-site chain (wotblitz.exe hash-verified 1cda5c31... = the exact 11.19.0.10 binary)
decision: WRITE-SITE DECODED - the game-code fill at wotblitz.exe+0x7C39AB is a per-frame tank transform update (FUN_00bc3940) that refills a 64-byte 4x4 world matrix at [entity+0x3C]+0x60 via 4x MOVUPS (the exact SSE pattern captured in FRESH43 hits 2-5) and gates on a position triple at [entity+0x3C]+0x1C/0x20/0x24
objective: identify what game code actually writes the memcpy source buffer the FRESH43 source-arm caught
stopCondition: n/a (offline) - decode complete when the enclosing function, its getter, and the matrix/position layout were identified
hypothesis: the armed member address (0x22AB0F90) is a copy DESTINATION of a 64-byte transform block whose producer is game transform code, not a CRT blob; the stable layout is [entity+0x3C]+0x1C..0x24 (position triple) and +0x60 (4x4 world matrix)
```

### Evidence chain (all module-mapped, hash-bound binary)

| RVA | Function | Role |
|---|---|---|
| `0x7C39AB` | inside `FUN_00bc3940` (RVA 0x7C3940, 0x2E6B) | the FRESH43 RIP; `MOV EAX,[EBX+0xA0]` validity gate |
| `0x7C3940` | `FUN_00bc3940` | per-frame tank/entity transform update; refills `[obj+0x60..0x9C]` 4x4 matrix (4x MOVUPS), writes `[obj+0x38..0x5C]`, gates on `[obj+0x1C/0x20/0x24]` floats != 0 |
| `0x7B9B75` (caller) | `FUN_00bb9b30` | entity-list iteration; calls the update per entity when `[entity+0x20] & 0x800` |
| `0x929EA0` | `FUN_00d29ea0` | getter: `return *(int*)(param_1+0x3C)` — single indirection to the transform object |
| `0x329570` | `FUN_00729570` | 4x4 matrix multiply (16 floats); composes the world matrix |
| `0x91A0F0` | `FUN_00d1a0f0` | quaternion->matrix build from `[obj+0x10]` |
| `0x916380` | `FUN_00d16380` | identity+diagonal (0x3f800000) then quaternion product terms |
| `0x9155C0` | `FUN_00d155c0` | orthonormal-basis normalizer (sqrt of squared sums) |

### Interpretation

- The FRESH43 `REP MOVSB`/`MOVDQU` hits were the CRT copy of a **64-byte
  transform block** whose producer is game transform code. The armed member
  `0x22AB0F90` is one destination copy.
- Candidate stable layout: `x/y/z = [entity+0x3C] + 0x1C/0x20/0x24`;
  `world matrix = [entity+0x3C] + 0x60`. Captured 4-float MOVDQU values
  (0.000457/2.574/−0.112/−0.112) are a rotation/scale row, not world
  coordinates — consistent with FRESH37's matrix-row hit values.
- **No promotion.** M3 still requires live-read matching against decoded
  ground truth + cross-battle repeatability + `independentReplays` (BLK-0019).

### Next

1. Walk `FUN_00bb9b30`'s caller to root the entity-array container to a
   stable global → candidate static pointer chain.
2. Offline-validate an interceptor experiment: arm `[obj+0x1C..0x24]` (the
   position triple) on the next family-hit and match captured values to
   decoded replay positions at the same clock.
3. `independentReplays`: import a second replay (BLK-0019).

## `OD-RECOVERY-054` result — 2026-08-08 (offline: entity container walked to a stable global — candidate static pointer chain)

sessionId: OD-RECOVERY-054
status: Complete (offline)
type: caller-chain decode (Ghidra headless -noanalysis, hash-verified 11.19.0.10 binary 1cda5c31…)
tools: `DumpCallers.java` (caller BFS), `DumpWindow.java` (call-site windows)
commands: analyzeHeadless -noanalysis -postScript DumpCallers.java 0x125247C 2 5; DumpWindow.java 0x125192F / 0x125DB56 / 0x12673B0; DumpCallers.java 0x4E8E90 1 5

### Chain (stable global -> candidate target)

    DAT_043f516C  (BattleController singleton, RVA 0x3FF516C)
       -> [BC + 0x4]            = AvatarController      (UNVERIFIED: thunk
           FUN_016673a0 does MOV ECX,[ECX+4]; JMP ReloadScreenForRewind;
           thunk `this` is vtable-dispatched, no static caller)
       -> [AVC + 0x154]         = BattleResources       (0x165DB56: MOV ECX,[EDI+0x154];
           CALL FUN_01651780 = BattleResources::Load)
       -> [BR + 0x8]            = GameScene             (0x16528D4: MOV ECX,[EDI+0x8];
           CALL FUN_00765670 = GameScene::OnLoadingFinished)
       -> [GS + 0x88]           = TransformSystem       (0x765723: MOV ECX,[EBX+0x88];
           CALL FUN_00bbffb0 = TransformSystem update)
       -> [TS + 0x30]           = entity vector         (FUN_00bbffb0 feeds the list
           FUN_00bb9b30 walks; populated from world container [*(TS+0x28)+0xFC])
       -> entity[i] : gate [entity+0x20] & 0x800        (FUN_00bb9b30 scene-graph DFS,
           recurses children via [e+0x8] array when [e+0x20] & 0x1000)
       -> [entity + 0x3C]       = transform object      (getter FUN_00d29ea0
           = return [ECX+0x3C])
       -> [obj + 0x1C/0x20/0x24] = position x/y/z        <<< CANDIDATE TARGET
       -> [obj + 0x60..0x9C]     = 4x4 world matrix      (4x MOVUPS fills)

### Call-site evidence (each hop is a disassembled call site, not decompiler guesswork)

- `FUN_00bc3940` (write site) <- `FUN_00bb9b30` @0x7B9B75: `TEST [entity+0x20],0x800; CALL FUN_00bc3940`
- `FUN_00bb9b30` <- `FUN_00bbffb0` (TransformSystem) with `this` = [GS+0x88]
- `FUN_00bbffb0` <- `FUN_00765670` (GameScene) with `this` = [BR+0x8]
- `FUN_00765670` <- `FUN_0165247c` (BattleResources::LoadGameScene) with `this` = [BR]
- `FUN_0165247c` <- `FUN_01651bd0` (LoadGameScene wrapper) <- `FUN_01662f00`
  (TryLoadResources) <- `FUN_01651780` (BattleResources::Load) <- `FUN_0165d9b0`
  (AvatarControllerBase::ReloadScreenForRewind, `this` = AvatarController) <-
  thunk `FUN_016673a0` (vtable-dispatched)
- Singleton: `FUN_008ee9f0` (getter) = thread-safe lazy singleton returning
  `DAT_043f516C`; built by `FUN_008e8e90` (calls FUN_008dfaa0 + FUN_008dfb10).

### Conclusion

- **Candidate static pointer chain exists** — first time the FRESH43 write site
  is rooted to a stable global (`DAT_043f516C`). All hops from BattleResources
  down are call-site-verified member offsets.
- **One unverified hop**: BattleController -> AvatarController (vtable dispatch).
  Do NOT rely on `[BC+0x4]` until confirmed (dump `FUN_008dfaa0` — the object
  built into the singleton — and hunt `MOV [BC+0x4], X` writes).
- **No promotion** — evidence-first discipline. M3 still requires a live read of
  the position triple matched to decoded ground truth at the same replay clock,
  cross-battle repeatability, and `independentReplays >= 1` (BLK-0019).
- **Next**: interceptor-arm `[obj+0x1C..0x24]` on the next family-hit and compare
  captured values to decoded ground truth; verify the AvatarController hop;
  import a second replay for `independentReplays`.

## `OD-RECOVERY-055` result — 2026-08-08 (offline correction: OD-RECOVERY-054 root REFUTED — DAT_043f516C is the DAVA logger)

sessionId: OD-RECOVERY-055
status: Refuted (root claim)
type: singleton-constructor decode (Ghidra headless -noanalysis, hash-verified 11.19.0.10 binary 1cda5c31…)
tools: `DumpCallers.java`
commands: analyzeHeadless -noanalysis -postScript DumpCallers.java 0x4DFAA0 1 5; 0x4DFB10 1 5; 0x4E13E0 1 5

### Evidence

- `FUN_008dfaa0` — allocates 4 bytes, `*ptr = DAVA::TagLoggerExternalImpl::vftable`.
- `FUN_008dfb10` — allocates 0x70 bytes, delegates to `FUN_008e13e0`.
- `FUN_008e13e0` (constructor) — `*param_1 = DAVA::TagLoggerInstanceImpl::vftable`;
  builds `std::_Ref_count_obj2<DAVA::SkipAssertHandler>` (member 0x38),
  `DAVA::BreakAssertHandler` (0x54 via param), `DAVA::ContinueAssertHandler`;
  calls `FUN_008e4680` (handler registration).
- `FUN_008ee9f0` — thread-safe lazy singleton getter returning `DAT_043f516C`,
  first-call guard on `DAT_043f5170`, built by `FUN_008e8e90`.

### Why the earlier claim was wrong

OD-RECOVERY-054 saw `FUN_008ee9f0()` calls throughout the caller chain and
assumed the returned object was a battle object. The strings
`"BattleResources::LoadGameScene"` + source path `"…/Battle/BattleResources.cpp"`
are **log payloads** written through that logger (the `FUN_008e1740`-style
log-write helpers with level/channel/format args). The getter returns the DAVA
logger, period. Object-type labels in the OD-RECOVERY-054 chain
(AvatarController/BattleResources/GameScene/TransformSystem) were inferred from
those log strings — plausible function-naming hints, NOT object identity proof.

### What survives

- **OD-RECOVERY-053 intact** — the write-site function `FUN_00bc3940`,
  entity DFS `FUN_00bb9b30` (`[e+0x20]&0x800` gate), transform getter
  `[entity+0x3C]` (`FUN_00d29ea0`), **position triple `[obj+0x1C/0x20/0x24]`,
  world matrix `[obj+0x60..0x9C]`** — all from `chain-disasm.txt` disassembly.
- **Call-site member offsets** `[X+0x154]`, `[X+0x8]`, `[X+0x88]`, `[X+0x30]`
  are raw `MOV ECX,[reg+off]; CALL` disasm facts (window dumps) — valid as
  offsets, but the labels are unproven and the root object is unknown.

### Conclusion

- **No stable global root for the entity container.** Top of the chain is
  battle-scoped heap via vtable dispatch. `DAT_043f516C` = DAVA logger.
- **No offset promoted.** Promotion path is M3 live-read: interceptor-arm
  `[obj+0x1C..0x24]` on a family hit, capture floats, match to decoded ground
  truth at the same replay clock. This does NOT require a static root.
- Ledger rule honored: the failed root hypothesis is recorded as an honest
  negative with the reasoning that killed it.

## `OD-RECOVERY-056` result — 2026-08-08 (offline retroactive M3 check: FRESH43 captured floats vs decoded ground truth)

sessionId: OD-RECOVERY-056
status: Complete (offline)
type: capture-vs-ground-truth cross-check (retroactive M3 step 1)
tools: python sqlite3 against `.data/treader.db`
data: FRESH43 capture `od-048-autotrace-20260807-123621.json.capture.json` (6 hits);
      decoded session `019fb86c-c8e7-7004-9df6-a574f5a7835b` (sha 59c3b92e…, Dead Rail)

### Evidence

Captured floats:
- SSE source quad `0x28FFCF10..1C` (VCRUNTIME140+0xED49): 0.00045715118 / 2.5736246 / -0.11194487 / -0.11190009
- Game-code fill `0x2C5C8A90` (wotblitz+0x7C39AB): 0.008281542
- Member write `0x22AB0F90` (VCRUNTIME140+0xE8AE rep movsb): value 0 (byte-level copy start)

Ground truth (player mrkool1138 / GB08_Churchill_I, 2784 samples, tick range 5129510..2789718627):
- x: [-75.4, 64.4]  y: [24.0, 34.8]  z: [-169.3, 237.2]

### Conclusion

- Captured magnitudes (0.008, 0.0005, 2.57, -0.11) are ~3 orders below world
  coordinates → they are matrix rows (rotation/scale), NOT positions.
- **OD-RECOVERY-053 inference independently CONFIRMED with decoded ground truth.**
- M3 must arm `[obj+0x1C..0x24]` (position triple) and read the member address
  at write time; expected sanity band for this replay: x/z in [-170, +240],
  y in [24, 35].
- No offset promoted; `independentReplays` still 0 (BLK-0019).

## `OD-RECOVERY-057` result — 2026-08-08 (offline: FRESH43 arm-snapshot anomaly — transient multi-copy buffers)

sessionId: OD-RECOVERY-057
status: Complete (offline)
type: arm-snapshot vs session-envelope anomaly resolution
tools: python sqlite3 against `.data/treader.db` + FRESH43 capture `od-048-autotrace-20260807-123621.json.capture.json`
data: decoded session `019fb86c-c8e7-7004-9df6-a574f5a7835b` (sha 59c3b92e…, Dead Rail, 435k samples)

### The anomaly

The correlate scored both family members at **0.933 (14/15 samples, tolerance
0.001)** against the viewpoint trajectory. The ground-truth provider
(`SqliteTrajectoryGroundTruthProvider.cs:163`) reads the SAME
`raw_x/raw_y/raw_z` columns I queried, so the staging reads genuinely matched
decoded world coordinates within 0.001.

But the interceptor's arm-time snapshots (~1s after the last staging read,
capture start 16:36:21.6 vs correlate complete 16:36:21.19) read:

| Member | Axis | Arm snapshot | Session envelope |
|---|---|---|---|
| 0x22AB0F90 | x | 274.0174 | x [-130.0, 120.0] |
| 0x23A4C490 | z | 296.2679 | z [-235.5, 251.0] |

Both are **outside the entire session's coordinate envelope** — no participant
ever reaches x=274 or z=296 in this battle (max |coordinate| = 251). The tank
cannot move ~300 units in 1s (max speed 14.8 m/s).

### Interpretation

- The member addresses are **transient multi-copy buffers** (FRESH37 class),
  not stable dedicated position fields. During the staging window they held
  decoded-matching position values (the 0.933 exact match); by trace-arm time
  they had been **reused for unrelated data** (274/296 could be matrix rows,
  other tanks, or pooled buffer contents).
- The FRESH43 `value: 0` member write and the matrix-row source quads are the
  same story: the write chain moves matrix/struct rows into these buffers.
- **M3 stable-read NOT satisfied**: a read of the member address at arm time
  does not reliably return the player position. The correlate-time match is
  real evidence the address holds positions *during the staging window*, but
  that lifetime is short.

### Conclusion / next-hypothesis impact

- **Arm the position triple IMMEDIATELY at correlate completion** (shrink the
  staging→arm gap from ~1s+ to <100ms) so the interceptor's snapshot catches
  the buffer while it still holds position data — OR read the member addresses
  DURING staging (the reads already match 0.933) instead of relying on a later
  trace-time read.
- The correlate itself is the M3 read: 14/15 exact matches against decoded
  ground truth is stronger than any trace-time snapshot. Promotion should be
  framed around the correlate match + cross-battle repeatability +
  `independentReplays` (BLK-0019), not the write-trace value capture.
- No offset promoted; `independentReplays` still 0 (BLK-0019).

## `OD-RECOVERY-058` result — 2026-08-08 (offline: BLK-0019 unblocked — second independent 11.19.0 replay found and decoded)

sessionId: OD-RECOVERY-058
status: Complete (offline)
type: replay inventory → distinctness proof → second independent decode
tools: `WotBTreader.ReplayInspector` probe (gameVersion/map/battleTimeUtc) over
      `AppData/Local/wotblitz/DAVAProject/replays/*.wotbreplay`; sha256
      distinctness; CLI `import` into `.data/treader.db`
data: 18 saved 11.19.0 battles in the user replay folder

### Inventory

Probing all 18 files in the user replay folder (`AppData/Local/wotblitz/
DAVAProject/replays/`):

- 16 files: medvedkovo, battle `2026-07-29T17:35:16`, sha `59c3b92e…` — the
  SAME payload as FRESH43's Dead Rail replay (re-recorded by the offline
  viewer; identical 1 100 265-byte size).
- **2 files**: savanna (Oasis Palms), battle `2026-08-02T21:15:07`, sha
  `0fae5612…` — a DISTINCT, independently recorded 11.19.0 battle, including
  the named save `20260802_1615__mrkool1138_GB08_Churchill_I_…` (1 045 525 B).
  The two savanna copies hash-identical to each other.

### Decode

The savanna replay imported cleanly as session
`019fdff7-8dcf-7426-8547-9fb8cc3eb07b`:
- gameVersion 11.19.0, map Oasis Palms (mapId 11), battle 2026-08-02T21:15:07,
  duration 00:04:39, arena `8565111466734423`
- 14 participants, 26 822 position samples, 26 894 events, 47 258 raw records
- **Same player (mrkool1138) + same tank (GB08_Churchill_I, team 1) as FRESH43**
- World-coordinate envelope x[−254,198] y[33,42] z[−248,186] — genuine
  positions in the expected M3 sanity band (x/z hundreds, y tens)

### Conclusion

- **BLK-0019's resolution path is confirmed**: the second independently
  recorded 11.19.0 replay exists on this machine, obtained through normal
  gameplay (game-named save of the 2026-08-02 Churchill I battle).
- Cross-battle M3 validation is now possible: run the od-048 correlate +
  interceptor on THIS replay in a fresh managed launch and compare against the
  decoded session ground truth; the FRESH43 members' 0.933 correlate match
  must repeat on the second battle, then `independentReplays` = 1 is
  satisfiable and promotion (BLK-0019) can proceed.
- No offset promoted; this unblocks the promotion pipeline rather than
  promoting itself.

## `OD-RECOVERY-059` result — 2026-08-08 (FRESH44 live: cross-battle correlation repeated; trace no-hit)

sessionId: OD-RECOVERY-059
startedUtc: 2026-08-08T16:30:27Z
endedUtc: 2026-08-08T16:32:13Z
status: Partial
objective: Repeat the FRESH43 viewpoint-position correlation on the second independent replay and capture durable M3 evidence.
stopCondition: One bounded correlate + 25-second write-trace window, or any offline-gate/artifact failure.
game:
  version: 11.19.0.10
  processStartIdentity: recorded-locally
replay:
  replayIdentity: local-redacted-independent-replay-2
  offlineGate: OfflineReplayVerified
method:
  staging: viewpoint-only
  playbackSpeedEstimate: 2.4
  traceEngine: csharp-guard-page
  sourceArmOnFirstHit: true
result:
  verdict: evidence-strong
  addressesScored: 812
  totalSamples: 12992
  strongSurvivors: 21
  durableSeries: 21
  samplesPerSeries: 16
  selectedFamily: x-only-solo
  selectedScore: 0.9375
  selectedMatches: 15/16
  selectedSpan: 249.2
  selectedBandSeconds: 4.0
  traceVerdict: family-no-hit
  traceWindowSeconds: 25
  traceLiveness: running
  pagesArmed: 3
  hits: 0
evidence:
  independentReplays: 1
  crossBattleCorrelationRepeatability: satisfied
  stableAddressKind: not-satisfied
  sameClockPositionTripleRead: not-satisfied
artifacts:
  rawEvidence: private-local-ignored
  durableSeries: private-local-ignored

### Interpretation

- The transient viewpoint-position correlation repeated cross-battle:
  FRESH43 scored 0.933 (14/15); FRESH44 selected 0.9375 (15/16), with several
  additional 16/16 survivors. The actual value/time samples survived in the
  bounded `seriesEvidence` payload.
- BLK-0019 is resolved: the second content-distinct replay was exercised in a
  fresh positively verified offline launch. Resolution establishes replay
  independence, not offset correctness.
- The trace is an honest negative: the game stayed live, three pages were
  armed, and no writes or source pages were observed in the 25-second window.
  This does not erase FRESH43's game-code matrix-fill evidence, and it does not
  prove writes are absent outside this window.
- No offset is promoted. FRESH44 is `evidence-strong`, not `family-complete`;
  the selected family is x-only, the addresses are transient heap copies, the
  proposed static root remains refuted, and the position triple has not been
  read live at the same decoded clock across replays.

### Operational fixes made before the accepted run

- The first launch attempt was stopped and its incomplete log discarded after
  a preflight audit found a stale published interceptor. No evidence from that
  attempt is accepted.
- FRESH44 now fails closed when the interceptor publish is older than its
  source, redacts private paths and replay identity from its durable log/result,
  and stops the research host after the bounded run.
- The report now exposes `strongSurvivorCount` and
  `strongSurvivorsTruncated`; the prior array was intentionally capped at 20,
  which made the live count of 21 appear inconsistent with the JSON summary.

### Next

1. Open a read-only promotion review that records M3 cross-battle correlation
   repeatability as satisfied but publication blocked.
2. Do not repeat the delayed 25-second trace unchanged.
3. If another live round is approved, first validate a changed hypothesis that
   reads `[obj+0x1C..0x24]` immediately (<100 ms) at correlate completion,
   aligns it to decoded ground truth, preserves object/displacement provenance,
   and repeats the same addressable candidate on the other replay.

## `OD-RECOVERY-060` result — 2026-08-08 (read-only promotion review)

```yaml
sessionId: OD-RECOVERY-060
status: Blocked (publication only)
type: formal promotion-checklist review
inputs: workflow + schema + current offset table + FRESH43/FRESH44 aggregate evidence
result:
  m3CrossBattleCorrelationRepeatability: satisfied
  publishableCandidate: not-satisfied
  verifiedPromotion: not-satisfied
  currentTableAction: keep-playerPosition-fields-zero-and-Unknown
```

### Criterion review

| Criterion | Status | Review finding |
|---|---|---|
| Offline authorization | Satisfied | FRESH44 ran under `OfflineReplayVerified`. |
| Independent replay/process repeatability | Satisfied for the phenomenon | A fresh launch on the second content-distinct replay repeated the viewpoint-x correlation. |
| Address kind | Classified, disqualifying | The correlated addresses are transient heap copies, not publishable module-relative fields. |
| Decimal/hex candidate agreement | Not satisfied | No numeric offset candidate exists to reconcile. |
| Intended field type and behavior | Partial | Float viewpoint-x behavior repeated; a live object-position x/y/z triple did not. |
| Same candidate across launches/replays | Not satisfied | The phenomenon repeated, but no stable displacement, pointer chain, or resolver did. |
| Static-analysis provenance | Partial | Static disassembly supports `[entity+0x3C]+0x1C/0x20/0x24`; it is not connected to a live addressable candidate. |
| GameHarness provenance and invariants | Not satisfied | Historical gated scanner evidence exists, but no candidate-specific invariant pass exists. |
| Conflicts | Not satisfied | The proposed static root is refuted and no replacement resolver is proven. |
| Lead / decoder approvals | Not satisfied | No promotion approval is recorded. |
| Schema and evidence report | Not satisfied for promotion | Validation proves the zero-offset placeholder is valid; it does not create missing evidence. |

### Decision

- M3 cross-battle repeatability is accepted only for the transient correlation
  phenomenon. It does not increment candidate-specific promotion counts.
- `memory-offsets/11.19.0.10.json` remains unchanged: the position fields stay
  `0` / `Unknown`, harness invariants and approvals stay false.
- Publication is blocked until one addressable layout is tied to the actual
  transform object, read at the same decoded clock, and repeated with the same
  relative displacements in another fresh process/replay. Even then, the
  current offset schema still needs a stable module-relative resolver before
  the field can be published.
- The next admissible live hypothesis is a synthetically validated immediate
  batch read: derive a **candidate object-base hypothesis** from a strong
  viewpoint-x address (`candidate - 0x1C`), read `+0x1C/+0x20/+0x24` in one
  gated call immediately after correlation, measure the response gap, compare
  all three floats to decoded viewpoint ground truth, and keep the base marked
  `hypothesized` unless independent write/register evidence proves it.

## `OD-RECOVERY-061` result — 2026-08-08 (FRESH45 immediate triple: no layout match)

```yaml
sessionId: OD-RECOVERY-061
startedUtc: 2026-08-08T17:25:33Z
endedUtc: 2026-08-08T17:27:22Z
status: NoSignal (candidate-derived layout) / Partial (instrumentation)
objective: Test OD-RECOVERY-060's immediate candidate-minus-displacement hypothesis without claiming a proven object base.
stopCondition: One bounded correlate plus one immediate batch read, with the delayed write trace disabled.
replay:
  replayIdentity: local-redacted-independent-replay-2
  offlineGate: OfflineReplayVerified
method:
  staging: viewpoint-only
  playbackSpeedEstimate: 2.4
  positionCandidateCap: 4
  requestedFloatReads: 12
  targetGapMilliseconds: 100
  tolerance: 6.0
  delayedWriteTrace: disabled
result:
  correlateVerdict: evidence-strong
  addressesScored: 866
  totalSamples: 13856
  strongSurvivors: 22
  strongByAxis: { x: 19, y: 0, z: 3 }
  immediateReadStatus: complete
  readableFloats: 12/12
  matchingXyzCandidates: 0/4
  immediateReadVerdict: no-hypothesis-match
  dispatchGapMilliseconds: 81.838
  requestRoundTripMilliseconds: 2.28
  completionGapMilliseconds: 102.2
  withinTargetGap: false
evidence:
  provenanceKind: candidate-derived-layout-hypothesis
  objectBaseProven: false
  atomicReadProven: false
  sameClockProven: false
  stableResolverProven: false
artifacts:
  rawEvidence: private-local-ignored
```

### Interpretation

- The immediate-read instrumentation worked end-to-end under the positive
  offline gate: it selected four strong viewpoint-x candidates, derived the
  proposed bases, issued one bounded batch, preserved timing/provenance, and
  rejected instrumentation failures separately from scientific negatives.
- None of the four proposed contiguous layouts matched the complete decoded XYZ
  triple. One candidate matched X within tolerance while Y and Z missed by
  28.6 and 10.5 units; two high-scoring candidates had zero-valued neighbors.
  This strengthens the existing classification that the correlated x values
  are transient copies rather than proven transform-object bases.
- The 2.2 ms target overrun does not explain the substantive Y/Z mismatches.
  An unchanged latency-only rerun is not warranted.
- This is not evidence against the static position-member layout globally.
  Candidate-derived bases are still hypotheses, and no true object pointer,
  atomic snapshot, same-clock identity, stable resolver, or module-relative
  offset was established.
- FRESH44's cross-battle correlation repeatability and BLK-0019 resolution stay
  accepted. `memory-offsets/11.19.0.10.json` remains unchanged.

### Next

1. Do not repeat FRESH45 unchanged and do not optimize merely to cross the
   100 ms target.
2. Offline/static-only, design and synthetically validate a bounded capture
   anchored at the already evidenced game-code transform-fill instruction and
   register path. Preserve module/instruction identity and the live
   register-derived object pointer.
3. Only after that provenance path passes synthetic validation, use one fresh
   positively verified offline session to read the actual object base's
   `+0x1C/+0x20/+0x24` members and compare them with decoded ground truth.

## `OD-RECOVERY-062` result — 2026-08-08 (instruction-first pivot implemented)

```yaml
sessionId: OD-RECOVERY-062
status: Complete (implementation) / Partial (discovery)
objective: Replace the exhausted candidate-scan loop with a provenance-changing capture of the actual transform-object register.
liveAccess: none
targetPolicy:
  gameVersion: 11.19.0.10
  executableIdentity: exact-hash-pinned
  module: wotblitz.exe
  rva: 0x7C39AB
  instructionHex: 8B83A0000000
  objectRegister: ebx
  positionRead: one-12-byte-read-at-ebx-plus-0x1C
bounds:
  durationSecondsMax: 5
  acceptedHitsMax: 64
  threadsMax: 128
  resultBytesMax: 65536
authorization:
  gate: OfflineReplayVerified
  process: exact-managed-child
  generationCancellation: required
  rawPidProductionCli: denied
  helperBinary: separate-no-legacy-mode
  helperIdentity: owner-only-publish-manifest
  coordinatorIdentity: build-pinned-exe-and-managed-dll
  postAttachIdentity: create-process-event-revalidated-before-arm
result:
  fullRepositoryValidation: pass
  releaseBuild: pass
  syntheticExactInstructionHits: 4
  syntheticChangingFiniteXyz: pass
  maxHitCleanupDetach: pass
  timeoutCleanupDetach: pass
  directPipeFromNonPinnedParent: rejected-before-target-access
  publicObjectIdentity: per-capture-opaque-key
  offsetTableChanged: false
```

### Decision

- The active player-position workflow is instruction-first. Broad scans,
  transient-candidate `address-0x1C` guesses, and delayed PAGE_GUARD tracing are
  historical evidence paths, not fallbacks for the next session.
- Production callers control only duration and accepted-hit bounds. The
  coordinator fixes PID/creation identity/path/version/hash/module/RVA/bytes/
  register/displacement and transmits them through inherited anonymous pipes.
- The helper accepts only an owned first-chance single-step at the exact target,
  reads the XYZ triple while the event is held, preserves unrelated debug
  registers, and must prove restore/detach. Cleanup failure denies the session
  and terminates the exact managed child.
- The helper does not trust self-asserted pipe metadata: its controlled publish
  embeds the exact Host.Web EXE+DLL hashes, the launcher checks an owner-only
  helper/Host identity manifest and fresh nonce response, and the helper
  independently verifies its actual parent plus the post-attach process-event
  handle before any thread context write.
- Host/GameHarness output replaces heap addresses with `object-NN` keys. This
  permits trajectory grouping while keeping process-local addresses private.
- The synthetic result proves the mechanism, not the game semantics. Hardware
  atomicity, exact decoded clock, viewpoint identity, stable root, candidate
  publication, and Verified promotion remain not satisfied.

### Next

`OD-RECOVERY-063` is one bounded live capture after a fresh helper publish,
synthetic pass, and new `OfflineReplayVerified` managed launch. Group XYZ
samples by object key and compare them with decoded ground truth. Stop after
the result. Only a matching object-key trajectory permits repeating this exact
instruction/member relationship on the other replay/fresh process.

## `OD-RECOVERY-063` result — 2026-08-08 (live thread-bound diagnosis)

```yaml
sessionId: OD-RECOVERY-063
status: Partial (instrumentation)
gate: OfflineReplayVerified
target: wotblitz.exe+0x7C39AB / 8B83A0000000 / EBX
captureSeconds: 5
acceptedHitsMax: 64
result:
  firstAttempt: cleanup-proven-helper-failure
  diagnosticAttempt: thread_bound_or_target_invalid
  observedGameThreadCount: 164
  configuredThreadBound: 128
  acceptedHits: 0
  cleanupDetach: proven
  postRunProcesses: game=0 host=0 helper=0 debugger=0
```

The helper already carried stable, privacy-safe diagnostic codes, but the
managed runner did not deserialize or project them. The runner now allows only
a fixed diagnostic-code set and keeps arbitrary helper strings private. The
diagnostic attempt then proved the exact blocker: the real game exceeded the
128-thread complete-coverage bound. No target value or no-hit claim was
accepted. The cap is now 256; the one-breakpoint, five-second, 64-hit,
12-byte-read, and 64 KiB bounds are unchanged.

## `OD-RECOVERY-064` result — 2026-08-08 (live scale classification)

```yaml
sessionId: OD-RECOVERY-064
status: Complete (classification) / NoSignal (position)
gate: OfflineReplayVerified
objectDisplacement: 0x1C
threadBound: 256
fingerprintMatched: true
cleanupDetach: proven
acceptedHits: 7
opaqueObjects: 1
vectors:
  distinct: 1
  value: [1, 1, 1]
proofFlags:
  objectRegisterCaptured: true
  viewpointIdentity: false
  sameDecodedClock: false
  stableRoot: false
offsetPromoted: false
```

This refutes the historical `+0x1C/+0x20/+0x24 = position` interpretation.
The hash-verified disassembly explains the result: `FUN_00d1a0f0` consumes
those three fields as the local scale while copying the preceding
`+0x10/+0x14/+0x18` triple into matrix translation. Historical OD-053/055/056
entries retain the earlier inference, but this live result supersedes it.

## `OD-RECOVERY-065` result — 2026-08-08 (local translation captured)

```yaml
sessionId: OD-RECOVERY-065
status: Partial / No exact participant match
gate: OfflineReplayVerified
objectDisplacement: 0x10
threadBound: 256
fingerprintMatched: true
cleanupDetach: proven
acceptedHits: 7
opaqueObjects: 1
capturedRanges:
  first: [-230.118, -223.063]
  second: [-164.172, -163.130]
  third: [42.642, 43.585]
decodedComparison:
  totalPositions: 26822
  viewpointPositions: 2812
  axisSignMappingsTested: 48
  exactParticipantMatches: 0
  bestViewpointMeanNearestUnits: 7.374
  bestViewpointMaxNearestUnits: 10.272
proofFlags:
  objectRegisterCaptured: true
  localTranslationLayout: true
  viewpointIdentity: false
  sameDecodedClock: false
  stableRoot: false
offsetPromoted: false
```

The live vector changes and has register/member provenance, but a
time-agnostic comparison across every decoded participant and all axis/sign
conventions found no exact match. It is therefore a local transform, not yet a
decoded world-position field. Static matrix flow supplies the next bounded
hypothesis without guessing: `FUN_00d1a0f0` places local translation at matrix
row `+0x30`, `FUN_00729570` composes the parent matrix, and
`FUN_00bc3940` copies the 4x4 result to EBX+`0x60`. The composed translation is
therefore EBX+`0x90/+0x94/+0x98`.

### Next

`OD-RECOVERY-066` may run one five-second capture at the unchanged pinned
instruction with the server/helper-fixed displacement `0x90`. GameHarness now
prints capture UTC for every hit. Align the opaque-object series to decoded
ground truth before judging identity. Stop after that result; do not scan,
change the instruction/register, or promote an offset.

## `OD-RECOVERY-066` result — 2026-08-08 (world translation no-match)

```yaml
sessionId: OD-RECOVERY-066
status: NoSignal (player identity) / Complete (bounded hypothesis)
gate: OfflineReplayVerified
objectDisplacement: 0x90
threadBound: 256
fingerprintMatched: true
cleanupDetach: proven
acceptedHits: 7
opaqueObjects: 1
decodedComparison:
  totalPositions: 26822
  axisSignMappingsTested: 48
  playbackSpeedRange: [0.5, 8.0]
  exactTrajectoryMatches: 0
  bestCoherentMeanUnits: 10.850
  bestCoherentMaxUnits: 12.556
  bestCoherentWithinOneUnit: 0/7
  fixed2_4xMeanUnits: 28.227
  fixed2_4xMaxUnits: 38.752
constantOffsetDiagnostic:
  bestMeanUnits: 1.260
  requiredOriginShiftUnits: 250.832
  requiredPlaybackSpeed: 6.26
proofFlags:
  objectRegisterCaptured: true
  composedMatrixTranslationLayout: true
  decodedParticipantIdentity: false
  viewpointIdentity: false
  sameDecodedClock: false
  stableRoot: false
offsetPromoted: false
```

The exact fingerprint, one bounded contiguous read per hit, finite samples, and
cleanup prove that the instruction-snapshot mechanism worked. Hash-verified
disassembly independently proves EBX+`0x90/+0x94/+0x98` is the translation row
of the composed 4x4 matrix written by `FUN_00bc3940`. The clock-aligned
comparison nevertheless found no decoded participant identity. The apparent
free motion-shape resemblance depends on a large invented origin shift and an
implausibly free playback rate, so it is not promotion evidence.

### Decision and next

Do not repeat, widen, or retime the transform-fill read. OD-RECOVERY-067 is
offline/static-only: locate where the verified type-10 replay position packet
is consumed or applied, then freeze a hash/module/RVA/bytes target with
entity/register and member provenance. A new live request is inadmissible until
that target passes bounded synthetic authorization, cancellation, cleanup, and
privacy validation. No offset-table change is warranted.

## `OD-RECOVERY-067` result — 2026-08-08 (static consumer triage)

```yaml
sessionId: OD-RECOVERY-067
status: Partial / No direct consumer anchor
mode: offline static only
executableFunctionsScanned: 526935
heuristics:
  localLayoutCandidates: 3457
  framedLayoutCandidates: 190
  directSameBaseLength49Type10Pairs: 0
  initializedType10Length49TableCandidates: 8
manualClassification:
  topLayoutCandidate: matrix/grid false positive
  tableCandidates: MSVC exception metadata
liveAccess: false
offsetPromoted: false
```

`FindType10PositionConsumers.java` intentionally over-approximated the verified
payload/framed displacement layouts. The volume and manual top-hit review prove
that this signature is dominated by matrices, copies, serializers, and
destructors. `FindType10RecordDispatch.java` found 13 loose `0x31` byte
comparisons but no dword length/type pair on one record base.
`FindType10DispatchTable.java` found eight nearby `{10,49,code-pointer}` rows;
their `0x19930522` neighborhoods identify MSVC FuncInfo/EH state tables.

### Decision and next

Do not rerun the same literal, displacement, or table-neighborhood searches.
The verified replay type-10 classification may be produced by generic framing,
table-driven code not carrying length beside type, or recorder-side semantics.
Continue offline by locating the generic replay event reader/framer from
replay/file entry points and tracing its payload data flow into an entity or
physics setter. No live capture is authorized until that trace yields a frozen
hash/module/RVA/bytes target with entity/register and destination-member
provenance and the bounded synthetic plan passes review.

## `OD-RECOVERY-068` result — 2026-08-08 (community Vehicle family triage)

```yaml
sessionId: OD-RECOVERY-068
status: NoSignal (position layout) / Partial (entity anchor)
mode: offline static only
executableSha256: 1cda5c31919c9784a41bee7f3270ec1b4536b124c51e8b36f2221b381760307d
historicalFamily:
  rootRva: 0x03E91978
  entityGetterDisplacement: 0x04
  claimedPositionDisplacements: [0x68, 0x6C, 0x70]
rootVerdict: refuted
fullScan:
  executableFunctions: 526935
  genericPlus04Loads: 111693
  directCandidates: 68
  directExactTriples: 1
  directExactNonMatrixTriples: 0
  sameBaseFallbackTriples: 662
vehicleGameLogic:
  vtableRva: 0x0327DA50
  virtualMethods: 79
  entityGetterSlot: 0x04
  entityGetterRva: 0x0031B560
  entityGetterBytes: 8B4104C3
  getterUsingMethods: 17
  distinctReturnedEntityOffsets: 23
  claimedPositionOffsetsObserved: 0
liveAccess: false
offsetPromoted: false
```

The exact community root is still non-pointer/string data with no relocation
or code reference. `FindVehiclePositionFamily.java` then treated the remaining
member layout as a family rather than as exact offsets. Its sole complete
direct `[reg+0x04] -> +0x68/+0x6C/+0x70` result was
`FUN_00c1ad60` (RVA `0x0081AD60`). Decompilation shows a larger pose/matrix
record copied and interpolated across `+0x60..+0x80`; it is matrix-shaped,
unanchored to `VehicleGameLogic`, and not a position candidate. The strongest
unanchored float fallback (`FUN_01ebf860`, RVA `0x01ABF860`) was independently
refuted by its caller, which fills `+0x6C..+0xA8` as a 4x4 matrix.

The useful survivor is structural: the current executable contains the exact
`VehicleGameLogic` vtable at RVA `0x0327DA50`, and virtual slot `+0x04` resolves
to RVA `0x0031B560`, `MOV EAX,[ECX+0x04]; RET`. A dump of all 79 virtual
methods found 17 that call this getter. Their decompiled returned-entity uses
cover 23 member offsets, including frequent `+0x1C` and health-like
`+0xB8/+0xBA`, but never `+0x68/+0x6C/+0x70`. Same-base triple hits inside
`VehicleGameLogic::onEnterWorld` and `showDamageFromShot` are fields on the
logic object itself and decompile as pointers/state, not floats.

### Decision and next

The historical position layout is closed for this binary. Do not spend a live
replay reading the stale triple and do not infer currentness from a recent post
date. `OD-RECOVERY-069` remains offline/static-only: converge the generic replay
reader/framer trace with the proven `VehicleGameLogic` entity getter, use
returned-entity `+0x1C` only as an identifier hypothesis and `+0xB8` only as
class corroboration, and trace into the exact entity-bound XYZ application or
write. A live request remains inadmissible until that path yields frozen
hash/module/RVA/bytes, register/entity provenance, one fixed contiguous member
read/write, and a reviewed bounded synthetic plan.

## `OD-RECOVERY-069` result - 2026-08-08 (type-10 entity movement anchor)

```yaml
sessionId: OD-RECOVERY-069
status: CandidateFound (entity-bound instruction event) / Partial (reliable player read)
mode: offline static only
executableSha256: 1cda5c31919c9784a41bee7f3270ec1b4536b124c51e8b36f2221b381760307d
replayDispatch:
  typeIndex: 10
  handlerRva: 0x00FE31C0
  readLengths: [4, 4, 4, 12, 12, 4, 4, 4, 1]
  totalPayloadBytes: 49
enginePath:
  blitzMoveRva: 0x00F7A610
  engineForwardRva: 0x022F9710
  entityResolverRva: 0x022FC850
  entityApplyRva: 0x022FA780
entityIdentity:
  memberDisplacement: 0x1C
  semantic: type-10 entity id
captureAnchor:
  rva: 0x022FA78D
  bytes: F30F7E00
  instruction: MOVQ XMM0,[EAX]
  entityRegister: ESI
  entityIdRead: ESI+0x1C
  xyzPointerRegister: EAX
  xyzRead: EAX+0x0/+0x4/+0x8
downstreamCorroboration:
  type: BW::AvatarFilterHelper ring
  entries: 8
  stride: 0x38
  positionDisplacement: 0x18
verifier:
  script: tools/ghidra-scripts/TraceType10MovementPosition.java
  checksPassed: 40
  checksFailed: 0
liveAccess: false
hardwareAtomicReadProven: false
sameDecodedClockProven: false
stablePollingOffsetProven: false
playerIdentityProven: false
offsetPromoted: false
```

The broad replay/entity mapper first located `ReplayPlayer`,
`BlitzServerMessageHandler`, their vtables, construction references, and named
entity lifecycle handlers. That exposed the normalized replay event table:
the constructor writes handler RVA `0x00FE31C0` at index 10. The handler reads
the exact decoded payload shape and calls vtable slot `+0x34`, which resolves
to the engine movement forwarding path.

The engine resolves the packet entity across its entity maps and directly
compares `[entity+0x1C]` with the packet entity ID. It then calls entity
movement application RVA `0x022FA780`. At instruction RVA `0x022FA78D`, the
prologue has copied the resolved entity from `ECX` into `ESI` and loaded the
packet-derived position pointer into `EAX`. The four-byte instruction reads the
first eight XYZ bytes; the following instruction reads the final four bytes.
This freezes a two-source capture plan with semantic entity provenance rather
than a guessed position displacement.

The downstream `BW::AvatarFilter` path forwards the same vector to
`BW::AvatarFilterHelper`. Its store method maintains an 8-entry circular
buffer; each `0x38`-byte record contains timestamp/IDs, position at `+0x18`, a
zero vector at `+0x24`, and velocity at `+0x30`. This independently supports
the position interpretation, but the ring and helper pointer are dynamic, so
they are not suitable for offset publication.

### Decision and next

The community family was fruitful as a navigation clue, not as a current
offset. Do not run the old root/member triple, another broad scan, or the
existing EBX-only helper unchanged. `OD-RECOVERY-070` must first implement and
synthetically validate a server/helper-fixed capture at RVA `0x022FA78D` that
reads one int32 at `[ESI+0x1C]` and one contiguous 12-byte vector at `EAX`
inside the same held debug event. Preserve the existing exact target identity,
authorization, bounds, cancellation, restoration, cleanup, and privacy
controls. Only after that review passes may one positively verified offline
replay be requested to compare the captured entity ID and XYZ against decoded
type-10 ground truth. A successful match would prove reliable event-based
player-location reading; stable polling resolution and offset publication
would remain separate work.

## `OD-RECOVERY-070` result - 2026-08-08 (synthetic two-source capture)

```yaml
sessionId: OD-RECOVERY-070
status: Complete (synthetic capture) / CandidateFound (live plan)
mode: implementation and synthetic only
target:
  module: wotblitz.exe
  rva: 0x022FA78D
  bytes: F30F7E00
  captureKind: type10-entity-position
  entityRegister: ESI
  entityIdDisplacement: 0x1C
  vectorRegister: EAX
  vectorBytes: 12
synthetic:
  replayEntityId: 4242
  acceptedHits: 4
  distinctX: 4
  finiteXyz: true
  maxHitStop: proven
  nonHostParentRejected: true
  cleanupProven: true
  detached: true
projection:
  privateSchema: wotbtreader.execute-object-snapshot.v2
  replayEntityIdExposed: true
  rawAddressesExposed: false
  instructionBytesExposed: false
liveAccess: false
stablePollingOffsetProven: false
playerIdentityProven: false
offsetPromoted: false
```

The production target policy and helper independently pin the exact version,
game hash, module, RVA, instruction bytes, capture kind, registers, and entity
ID displacement. Callers still supply only duration and hit bounds. The helper
reads the replay entity ID and XYZ as two bounded reads while the same matching
debug event holds the process. This is not a hardware-atomic read and does not
yet establish same-decoded-clock or local-player identity. It retains private
addresses only in its ignored owner-local report; GameIntegration projects an opaque object key,
`replayEntityId`, UTC, values, and proof flags.

The owned x86 target sets `ESI` to a synthetic entity record, sets `EAX` to a
changing XYZ vector, and executes the exact four bytes. Four accepted hits all
returned entity ID `4242` and finite, changing values. The max-hit path restored
debug-register state and detached. The separate non-Host caller-created pipe
test was rejected before target access. Existing identity, cancellation,
bounded-output, crash-containment, and cleanup paths remain unchanged.

### Decision and next

After full repository validation and a fresh helper publish pinned to that
validated Host build, `OD-RECOVERY-071` may run one five-second/64-hit capture
in a positively verified offline replay. Compare only successful finite hits,
and match each `replayEntityId` only to that entity's decoded type-10 XYZ at the
aligned clock. Stop after the result. Exact equality would prove reliable
event-based entity-location reading, not local-player identity, a stable polling
offset, or a publication-ready resolver. Player-location wording requires
independent evidence that the matched replay entity ID belongs to the local
player.

## `OD-RECOVERY-071` result - 2026-08-08 (first live type-10 equality)

```yaml
sessionId: OD-RECOVERY-071
status: Hit / Partial (reliable player read)
mode: managed positively verified offline replay
gate: OfflineReplayVerified
target:
  module: wotblitz.exe
  rva: 0x022FA78D
  bytes: F30F7E00
  captureKind: type10-entity-position
capture:
  durationSeconds: 5
  maxHits: 64
  acceptedHits: 49
  successfulEntityAndFiniteVectorHits: 49
  opaqueObjects: 8
  fingerprintMatched: true
  cleanupProven: true
comparison:
  decodedTrajectoryEntities: 14
  decodedEntityMatches: 7
  float32ExactTripleMatches: 7
  viewpointFloat32ExactMatches: 1
  unmatchedZeroVectorObjects: 1
  capturedMovementObserved: false
proof:
  entityPositionIdentityProven: true
  viewpointPlayerPositionIdentityProven: true
  motionFreshnessProven: false
  sameDecodedClockProven: false
  hardwareAtomicReadProven: false
  crossReplayRepeatabilityProven: false
  stablePollingRootProven: false
  offsetPromoted: false
privacy:
  trackedIdsCoordinatesPathsOrNames: false
  aggregateOnly: true
shutdown:
  gameHostHelperDebuggerRemaining: 0
```

The capture was admitted only after the managed replay reached
`OfflineReplayVerified`. Every accepted hit supplied a readable replay-local
entity ID and finite XYZ from the fixed `ESI`/`EAX` event. Comparison was
strictly same-entity: seven vehicle entities each had an exact Float32 triple
in decoded type-10 ground truth, and one of those entities was independently
marked as the replay viewpoint. The eighth opaque object was a zero vector with
no decoded trajectory and was not counted as a match.

This is the first direct live proof that the current instruction event yields
the replay player's position value. It is deliberately not a stable offset or
a timing claim. Values were unchanged during the window, no decoded-clock value
was captured, and entity ID plus XYZ use two reads during the same suspended
event. No address, entity ID, coordinate, player/account datum, replay path, or
raw capture is persisted in tracked source.

### Decision and next

Do not return to heap scans, stale community offsets, or the transform-fill
branch. `OD-RECOVERY-072` may run the unchanged five-second/64-hit target during
a verified movement window on the other content-distinct replay. It must show a
changing viewpoint series and same-entity decoded matches. That would establish
motion freshness and cross-replay repeatability for event-based player-location
reading; a stable continuously pollable resolver remains separate work.

## `OD-RECOVERY-072` result - 2026-08-08 (moving cross-replay proof)

```yaml
sessionId: OD-RECOVERY-072
status: Hit / Complete (event-based player read)
mode: managed positively verified offline replay
gate: OfflineReplayVerified
replayRelationship: content-distinct from OD-RECOVERY-071
processRelationship: fresh managed process
target:
  module: wotblitz.exe
  rva: 0x022FA78D
  bytes: F30F7E00
  captureKind: type10-entity-position
capture:
  durationSeconds: 5
  maxHits: 64
  acceptedHits: 64
  successfulEntityAndFiniteVectorHits: 64
  opaqueObjects: 13
  fingerprintMatched: true
  cleanupProven: true
  truncatedAtHitLimit: true
comparison:
  decodedTrajectoryEntities: 14
  decodedEntityMatches: 12
  matchedHits: 58
  exactDownsampledHits: 13
  withinOneUnitHits: 41
  withinThreeUnitHits: 57
  entitiesWithChangingValues: 12
  viewpointHits: 6
  viewpointDistinctTriples: 6
  viewpointExactDownsampledHits: 2
  viewpointWithinOneUnitHits: 3
proof:
  entityPositionIdentityProven: true
  viewpointPlayerPositionIdentityProven: true
  motionFreshnessProven: true
  crossReplayRepeatabilityProven: true
  sameDecodedClockProven: false
  hardwareAtomicReadProven: false
  stablePollingRootProven: false
  offsetPromoted: false
privacy:
  trackedIdsCoordinatesPathsOrNames: false
  aggregateOnly: true
shutdown:
  gameHostHelperDebuggerRemaining: 0
```

The capture used the exact OD-071 target, registers, displacement, duration,
and hit bound. It ran only after a fresh managed launch of the other
content-distinct replay and a movement settle. All 64 accepted events had
readable replay-local IDs and finite XYZ. The request reached its configured
hit cap, so the result makes no no-hit/coverage claim beyond that bound.

Comparison was same-entity and used the bounded trajectory API. Twelve decoded
entity IDs matched. The replay viewpoint supplied six hits and six distinct
triples; two were exact samples retained by the 256-sample-per-entity ground
truth. Across all matched data, 13 hits were exact retained samples, 41 were
within one unit, and 57 were within three units. Downsampling explains why
exact equality is not expected for every moving event; no decoded-clock claim
is inferred from the distance buckets.

Combined with OD-071, the same fixed instruction event now reads the player's
position on two content-distinct replays and fresh processes, including active
movement. This satisfies motion freshness and cross-replay repeatability for
the event-based capability. It does not provide a stable address or continuous
polling contract, and `memory-offsets/11.19.0.10.json` remains unchanged.

### Decision and next

Stop unchanged type-10 live captures. Return offline/static to the proven
entity resolver at RVA `0x022FC850` and trace its container/owner to a stable
viewpoint-entity resolver. Validate the downstream movement-filter ring family:
`[entity+0x38]`, helper current index `+0x1C8`, 8 entries of stride `0x38`, and
position at record `+0x18`. Freeze and synthetically review a bounded polling
plan before any further live session. Do not publish those offsets yet.

## `OD-RECOVERY-073` result - 2026-08-08 (module-rooted polling implementation)

```yaml
sessionId: OD-RECOVERY-073
status: Complete (static/synthetic) / CandidateFound (live polling)
mode: offline static analysis and synthetic implementation
targetBuild:
  version: 11.19.0.10
  executableSha256: 1cda5c31919c9784a41bee7f3270ec1b4536b124c51e8b36f2221b381760307d
staticEvidence:
  verifier: TraceEntityRegistryPosition.java
  verdict: resolver-layout-proven
  checksPassed: 47
  checksFailed: 0
resolver:
  kind: module-rooted-entity-id-map
  appContextRootRva: 0x04054780
  maxTreeNodes: 1024
  maxAttempts: 3
  ringEntries: 8
  ringStride: 0x38
  fullRecordDoubleCollected: true
  rootEntityFilterHelperRevalidated: true
productionBoundary:
  callerControlsOnlyDecodedEntityId: true
  exactBuildAndModuleBaseServerOwned: true
  identityBoundReadOnlyLease: true
  liveAuthorizationCancellation: true
  unsupportedBuildOpensMemoryReader: false
  revokedResultDiscarded: true
validation:
  fullRepositoryGate: passed
  releaseBuildWarningsOrErrors: 0
  testsPassed: 646
  expectedOptInSkips: 2
proof:
  staticLayoutProven: true
  syntheticResolverBehaviorProven: true
  liveReadProven: false
  stableRootCrossProcessRepeatabilityProven: false
  hardwareAtomicReadProven: false
  sameDecodedClockProven: false
  offsetPromoted: false
privacy:
  publicProcessAddressesOrRawBytes: false
  aggregateRunnerPersistsIdsOrCoordinates: false
```

The current executable has a direct store to a non-executable module-rooted
`AppContextImpl` pointer. Constructor/data-flow evidence then fixes the
`AppContext -> BWApp -> BWServerConnection -> BWEntities` ownership chain.
The game resolver's cache and three map trees identify an entity by the same
`+0x1C` replay ID proven in OD-069 through OD-072. Vtable checks constrain the
read to AvatarFilter/AvatarFilterHelper before the newest ring record is read.

The Core resolver is IO-free and bounded. The UltimateScanner adapter supplies
one exact identity-bound read-only process lease; every native read rechecks
authorization and process identity. The coordinator rejects unsupported build
identity before reader creation and discards any result after gate revocation.
The HTTP request exposes only the decoded replay entity ID. The response and
OD-073 runner exclude process addresses and raw bytes; the runner persists no
entity ID or coordinate values.

### Decision and next

Static/synthetic review changes the live recommendation to **GO after the full
repository gate and fresh Host publish** for one bounded OD-073 poll. Require
all requested reads to resolve, at least two distinct positions, consistency
with the retained decoded viewpoint trajectory, module-root/entity identity
proof flags, and consistent double-collects. Hardware atomicity and
same-decoded-clock proof must remain false. A positive first result may be
repeated once on the content-distinct replay/fresh process; a negative returns
offline without broadening the read surface. No offset-table change is
authorized by this milestone.

## `OD-RECOVERY-074` result - 2026-08-09 (replay-root live narrowing)

```yaml
sessionId: OD-RECOVERY-074
status: Partial (continuous polling) / CandidateFound (replay entity root)
mode: offline static correction plus bounded aggregate-only live checks
targetBuild:
  version: 11.19.0.10
  executableSha256: 1cda5c31919c9784a41bee7f3270ec1b4536b124c51e8b36f2221b381760307d
staticEvidence:
  verifier: TraceEntityRegistryPosition.java
  verdict: replay-resolver-layout-proven
  checksPassed: 67
  checksFailed: 0
resolver:
  kind: module-rooted-replay-entity-id-map
  gameCoreRootRva: 0x04095C88
  ownerChain: GameCore/AppController/SessionController/AccountController/PlaybackController
  maxTreeNodes: 1024
  maxAttempts: 3
liveAggregate:
  originalMainConnection:
    requests: 24
    dominantStatus: EntityNotFound
  inferredAppContextOwner:
    requests: 24
    dominantStatus: UnsupportedAccountController
  correctedRootBeforeFilterFamily:
    requests: 24
    entitySource: primary-map
    dominantFailureStage: movement-filter-vtable
  correctedRootAfterFilterFamily:
    requests: 24
    entitySource: primary-map
    dominantFailureStage: avatar-helper-vtable
proof:
  replayOwnershipChainProven: true
  requestedEntityFoundInReplayMap: true
  positionRingReadProven: false
  movementProven: false
  decodedTrajectoryAgreementProven: false
  stableRootCrossProcessRepeatabilityProven: false
  hardwareAtomicReadProven: false
  sameDecodedClockProven: false
  offsetPromoted: false
privacy:
  publicProcessAddressesOrRawBytes: false
  aggregatePersistsIdsOrCoordinates: false
shutdown:
  gameHostHelperDebuggerProcessesRemaining: 0
```

The original module-rooted chain was structurally valid but followed the main
connection, not the replay connection. A second inferred owner failed its
vtable gate before traversal. Hash-bound constructor and runtime-reference
evidence then proved that the long-lived `GameCore` object is published through
module RVA `0x04095C88` and owns the controller chain leading to the active
replay `BWServerConnection`. The corrected resolver reached the requested
entity through the primary replay map in every bounded request.

Static analysis also narrowed the movement-filter family to KineticsFilter,
WGVehicleFilter2, and AvatarFilter because each has the exact verified
position-apply slot. The live object then exposed a helper subtype that is not
covered by the two already proved helper layouts. Treating that vtable as
sufficient evidence would turn an exact type gate into an assumption, so the
resolver stopped before any ring or position read.

A fresh offline reference report names the candidate at RVA `0x0325658C` as
`WGVehicleFilterHelper::vftable`. It records the constructor vtable store at
RVA `0x010139F1`, and separately inspected factory code assigns the constructed
helper to `filter+0x08`. That closes the subtype/ownership naming question but
does not establish its position-store slot or ring layout.

### Decision and next

The replay-owned stable root and entity lookup are now live-supported in one
managed process. Reliable continuous player-location polling is not yet
proved. Work offline from the now named vehicle-helper constructor and factory:
prove the position-store slot and the ring index/stride/position layout. Extend
the strict verifier and focused synthetic fixture before requesting one further
bounded live check.
Do not rerun the unchanged poll, broaden arbitrary vtables, or modify the
offset table.

## `OD-RECOVERY-075` result - 2026-08-09 (position-ring correction and live proof)

```yaml
sessionId: OD-RECOVERY-075
status: Hit / Partial (continuous polling)
mode: exact-build static correction plus artifact-bound bounded live polling
targetBuild:
  version: 11.19.0.10
  executableSha256: 1cda5c31919c9784a41bee7f3270ec1b4536b124c51e8b36f2221b381760307d
staticEvidence:
  replayResolverChecks: 82/82
  type10MovementChecks: 40/40
  matchedFilterHelperSubtypes: 3
  ringBaseFromHelper: 0x08
  ringStride: 0x38
  currentIndexFromHelper: 0x1C8
  positionFromRecord: 0x10/0x14/0x18
  velocityFromRecord: 0x28/0x2C/0x30
diagnosticVelocityRun:
  resolved: 24/24
  distinct: 21
  minimumRetainedTrajectoryDistance: 115.686
  classification: implementation diagnostic; not a position negative
correctedPositionRun:
  authorization: OfflineReplayVerified
  groundTruthBoundToExactLaunchArtifact: true
  requested: 24
  resolved: 24
  distinct: 24
  exactRetainedTrajectoryMatches: 5
  withinOneWorldUnit: 8
  withinThreeWorldUnits: 21
  minimumRetainedTrajectoryDistance: 0
  maximumRetainedTrajectoryDistance: 3.57889998332587
crossReplayRepeat:
  status: Blocked before evidence gate (BLK-0026)
  memoryRequestsIssued: 0
  evidenceResultCreated: false
proof:
  moduleRootedResolver: true
  matchedSubtypeIdentity: true
  positionRingLayoutStatic: true
  positionInsteadOfVelocitySynthetic: true
  oneReplayFreshProcessLivePosition: true
  movementObserved: true
  retainedTrajectoryAgreement: true
  exactLaunchArtifactBinding: true
  crossReplayContinuousPolling: false
  hardwareAtomicRead: false
  sameDecodedClock: false
  numericOffsetPublication: false
  offsetPromoted: false
privacy:
  publicProcessAddressesOrRawBytes: false
  aggregatePersistsIdsOrCoordinates: false
  trackedPrivateArtifactValues: false
shutdown:
  gameHostHelperDebuggerProcessesRemaining: 0
```

The shared helper store at RVA `0x0230DF40` writes position at
helper-relative `+0x18`, but the ring itself begins at helper `+0x08`.
Consequently the position is record-relative `+0x10`. OD-073 incorrectly used
`+0x18` for both coordinate systems and landed at record `+0x28`, which static
evidence identifies as velocity. The approximately 116-unit live mismatch was
therefore diagnostic confirmation of the addressing defect rather than a
refutation of the stable family.

After correcting the layout, the unchanged server-owned chain returned a
moving sequence that agreed closely with the retained decoded viewpoint
trajectory. The runner additionally bound that trajectory to the exact
artifact UUID emitted by the canonical managed launcher, eliminating the
possibility of silently comparing against the newest unrelated decode.

### Decision and next

This is the first strong positive for continuous module-rooted player-position
polling, but it covers only one replay/fresh process. The content-distinct
repeat failed before `OfflineReplayVerified`; no memory read occurred and the
failure is recorded separately as BLK-0026. Diagnose that launch/evidence
failure without memory access. Only then run one unchanged bounded poll on the
other replay. Do not spend discovery budget shaving timing, broaden the
resolver, or modify `memory-offsets/11.19.0.10.json`.
