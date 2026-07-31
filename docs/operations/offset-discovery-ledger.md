# Offset-discovery ledger

Last updated: 2026-07-31

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
| Do not repeat | The same yaw neighborhood scan using `0x0317A810` without resolving its provenance |
| Next planned session | `OD-RECOVERY-002` (retry only after the host gate is repaired) |

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

## `OD-RECOVERY-002` protocol (after `OD-RECOVERY-001` blocked)

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
