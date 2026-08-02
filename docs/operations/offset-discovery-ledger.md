# Offset-discovery ledger

Last updated: 2026-08-02 (OD-RECOVERY-005)

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
| Do not repeat | The same yaw neighborhood scan using `0x0317A810` without resolving its provenance; absolute image-only AOB of survivor pointer bytes without a changed encoding/root hypothesis (ruled out by OD-RECOVERY-007); absolute LE pointer AOB across private/all/image + align 1/8 without a changed encoding hypothesis (ruled out by OD-RECOVERY-008); truncated low-32 LE dword AOB of survivor absolutes without a changed encoding hypothesis (ruled out by OD-RECOVERY-009); automated CE `bptAccess`/`bptWrite` on first- or second-pass Float A→B survivors without a field pivot or interactive debugger (0 RIP hits through OD-RECOVERY-011) |
| Next planned session | `OD-RECOVERY-012` (field pivot to HP/replayTime, or x64dbg/CE GUI on a further-narrowed unique survivor; second-pass Float + automated CE write-BP still 0 RIP hits; second distinct replay still required before promotion) |

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
