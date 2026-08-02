# WoT Blitz PC offset-discovery workflow

Last updated: 2026-08-02

This is the operational playbook for discovering memory evidence from the
Windows WoT Blitz client during a **positively verified offline replay**. It is
optimized for timely, reproducible results rather than maximum scan volume.

The workflow is deliberately conservative: a readable address, a unique scan
result, or a plausible value is a hypothesis. It is not a runtime-supported
offset.

## Current decision

The current `11.19.0.10` `playerYaw` evidence is **quarantined as stale and ambiguous**.
The prior table recorded decimal `51808784` (`0x03168A10`), its notes claimed
`0x0317A810` (`51882000`), and the raw Ghidra export's top candidate was decimal
`56085200` (`0x0357CAD0`). The published offset is now cleared to `0` while the
conflict remains in evidence notes and the ledger. None of these values may be
used as a trusted scan anchor until the original analysis is reconciled.

The next session must establish one clean dynamic anchor, preferably position,
replay time, or HP, rather than repeating the unresolved yaw neighborhood scan.

## Why the old workflow was inefficient

WoT Blitz is a native DAVA-era client in the repository's current scope. Do not
assume Unity, `Assembly-CSharp.dll`, Mono, or IL2CPP metadata exists. Those are
conditional branches to test, not default steps. The committed Ghidra result is
an x86 program analysis, but architecture must still be measured for the exact
installed distribution and executable before every new campaign.

Several different things are often called an "offset":

1. **Absolute address** — valid only for one process instance.
2. **Module-relative address/RVA** — stable only if it identifies a real static
   location in the named module.
3. **Object-member displacement** — an instruction such as `[ecx+0x34]`; it is
   not a module RVA.
4. **Pointer-chain root** — a module-relative root followed by object pointers.
5. **Heap-relative scan result** — a dynamic location; it is not publishable as a
   static offset without a stable root or instruction evidence.

Every candidate record must state which kind it is. Never copy a raw Ghidra data
reference or a Cheat Engine address into `memory-offsets/` without classifying
it. This distinction is the main protection against repeating the current
playerYaw error.

## The five-minute rule: establish a session record first

Create a session ID before opening a scanner, for example
`OD-2026-07-31-RECOVERY-001`. Append the session to
[`offset-discovery-ledger.md`](offset-discovery-ledger.md) when it ends, even if
nothing was found.

Record at minimum:

- objective and stop condition;
- game version and exact executable SHA-256;
- distribution/source and measured process architecture;
- process ID, process-start identity, module name, module base, and module size;
- replay identity and lifecycle state, without storing private replay paths;
- tool, scan type, value type, range, alignment, and filter transition;
- raw address/value and normalized representation separately;
- candidate-address kind (`absolute`, `module-rva`, `member-displacement`,
  `pointer-chain`, or `heap-dynamic`);
- candidate count after every meaningful transition;
- result using the ledger vocabulary (`Partial`, `CandidateFound`, `NoSignal`,
  `Ambiguous`, `Blocked`, `Superseded`, or `Verified`);
- failure reason, what it rules out, and the next pivot.

Raw dumps, CE tables, screenshots, pointer maps, and game-derived files remain
local and untracked. Commit only redacted summaries and hashes where needed.

## Phase 0 — Safety and identity gate (10 minutes)

Do not scan until all of these are true:

1. The game is playing a pre-recorded offline replay.
2. The host reports `OfflineReplayVerified`.
3. The observed executable version and SHA-256 match the campaign record.
4. The process identity and window ownership are unambiguous.
5. The module list has been captured and the target module is named explicitly.
6. The client architecture has been measured; do not infer it from the store.
7. The replay is in a known state: loading, playing, paused, or ended.
8. A single field and a timeboxed hypothesis are selected.
9. The target module base resolves at the moment of the operation. A transient
   Windows module-enumeration failure denies that operation only; retry after
   the process settles rather than selecting another PID or bypassing the gate.
10. Cancellation and evidence revocation are honored by the module lookup and
    scanner reads. A read admitted before revocation may finish, while a new
    read is denied; do not treat cancellation as permission to select another
    process or bypass identity checks.

If any item is unknown, record `blocked` and fix that item. Do not compensate
with a longer scan.

## Phase 1 — Static triage (maximum 20 minutes)

Use Ghidra to generate hypotheses, not to declare fields found.

1. Analyze the exact binary identified in Phase 0.
2. Preserve the raw export unchanged.
3. Validate every decimal/hex pair mechanically.
4. Classify each result as a string address, data address, module RVA,
   instruction displacement, or unresolved address.
5. Prefer data-flow evidence and access instructions over string-match counts.
6. Record negative results: noisy HP strings, missing position references, and
   fields with no useful cross-reference are valuable exclusions.

The repository's `FindOffsets.java` output currently counts non-executable data
references. That is useful triage, but it does not by itself prove a field
layout or a module-relative offset. A top-count candidate is only promoted to
the next phase if its address kind and binary context are clear.

**Stop rule:** if static output does not produce one address with a clear kind
and a reproducible access path in 20 minutes, stop static work and move to a
dynamic anchor. Do not rerun the same string search.

## Phase 2 — Choose the cheapest dynamic anchor (maximum 45 minutes)

Choose a field whose state transition is observable in the current replay. Use
this order unless the replay makes a different field clearly measurable:

| Priority | Field | Useful transition | Main caveat |
|---:|---|---|---|
| 1 | `playerPositionX` or `playerPositionZ` | tank movement | spectator/camera movement can be mistaken for tank movement |
| 2 | `replayTime` | monotonic progression | may be a global timeline object, not the player structure |
| 3 | `playerHP` | damage or heal event | unchanged for long portions of a replay |
| 4 | `playerYaw` | hull/turret rotation | camera yaw, turret yaw, and hull yaw may be different fields |
| 5 | `cameraPitch` | deliberate camera tilt | may be UI/camera state rather than battle state |
| 6 | `aliveTankCount` | confirmed death | rare and often stored in battle-global state |

For each scan, record a transition pair, not just a scan count:

```text
state A: paused/observed value
operator event or replay transition
state B: paused/observed value
expected relation: changed / increased / decreased / unchanged
actual relation: ...
remaining candidates: before → after
```

Use Cheat Engine's interactive scan or the guarded loopback snapshot/compare
path for controlled transitions. The timer-based `autoDiscover()` and
`discover-campaign` paths are useful for a first pass, but their natural replay
transitions do not prove that the observed field is the requested field. A
controlled scan requires an operator-created replay event.

### Bounded interactive research lease

Normal hosts keep correlated replay-start evidence valid for 15 seconds. A
controlled transition may opt the loopback web host into a longer local
research lease, with a hard maximum of two minutes:

```powershell
$env:Research__OfflineReplayEvidenceLifetimeSeconds = '120'
$env:Research__LifecycleEvidenceTimeoutSeconds = '120'
dotnet run --project src/WotBTreader.Host.Web -c Release --no-build
```

The replay-evidence lifetime accepts 5–120 seconds. The lifecycle-evidence
timeout accepts 5–300 seconds and extends only the bounded startup wait for a
fresh native replay-start marker; it does not authorize scanning before that
marker exists. The active lifecycle monitor still revokes authorization and
terminates the exact managed child immediately on replay stop, unhealthy or
gapped evidence, cancellation, or expiry. Reported process exit and identity
changes also revoke immediately; each scanner read independently revalidates
the exact process identity and fails closed if the process disappears or is
replaced. Remove both environment variables when the research host exits.

For one controlled transition:

1. Start from zero game and web-host processes, then launch exactly one replay
   with `scripts/launch-offline-replay-for-od.ps1` (game-folder `.wotbreplay` →
   import → managed launch → Watch Offline). Do not use file-association alone
   when the discover gate is required.
2. Require `OfflineReplayVerified`, the expected executable identity, and one
   exact game PID before any scanner attaches or opens a guarded reader.
3. Prefer the loopback snapshot endpoint for aggregate reconnaissance. Use
   `valueKind=Float`, `valueSize=4`, `alignment=4`, private/mapped regions only,
   a finite expected range, and the engine's 512 MiB snapshot ceiling. Request
   at most one comparison candidate and suppress its address and value from
   rendered output; retain only the aggregate counters.
4. If Cheat Engine is used for local structural follow-up, keep it read-only:
   do not edit or freeze values, inject code, enable speedhack, or create dumps,
   pointer maps, tables, or screenshots for the repository.
5. Capture state A while the replay is paused. Have the operator resume replay
   movement briefly and pause it again, then compare state B with `changed` and
   a rolling baseline. An `unchanged` pass while paused may remove volatile
   values before a second controlled movement.
6. Abort immediately if the host gate changes. Discard the scanner session,
   detach any diagnostic, and confirm the exact game exits at stop or lease
   expiry. Commit only redacted aggregate counts and structural conclusions;
   raw addresses, values, session identifiers, and replay details remain
   local-only.

The guarded input adapter remains unavailable until a concrete Windows
implementation is registered. Do not compensate by sending game input through
another automation path; the operator owns the transition.

**Information-gain rule:** continue only when a transition materially reduces
the candidate set or increases confidence in the value behavior. After two
transitions with no useful reduction, change the field, range, or method.

**Stop rules:**

- three filters produce no meaningful narrowing;
- the result goes to zero and the replay transition was not confirmed;
- the same candidate does not reproduce the expected behavior twice;
- the scan is returning only heap-dynamic addresses with no stable route;
- the 45-minute budget is exhausted.

Record the failure and pivot. Never silently restart the same experiment.

## Phase 3 — Convert a dynamic hit into a structural hypothesis (maximum 30 minutes)

A dynamic value scan finds a location, not necessarily a usable offset. For the
best one to three candidates:

1. Use CE's **Find out what writes/accesses this address**.
2. Record the instruction bytes/address and the register or pointer expression.
3. Determine whether the instruction expresses a member displacement such as
   `[reg+0x34]`.
4. Inspect nearby fields from the same object base.
5. Compare the neighboring values against the expected position/HP/yaw/pitch
   behavior.
6. Use GameHarness `discover-nearby` only after the reference address kind is
   established and the offline gate is open.
7. Use x64dbg for instruction/register confirmation when CE's trace is
   insufficient; do not automate x64dbg before the manual path is repeatable.

The desired result is a structural record such as:

```text
field: playerPositionX
address kind: member-displacement
access: movss [register+0x28], xmm0
object identity: same register base observed in two transitions
neighbors: +0x2C and +0x30 behave as Y/Z
module/pointer root: separately unresolved
status: Candidate
```

Do not call this a static module offset until the root or pointer chain is
identified.

## Phase 4 — Repeatability (two launches, two replays)

Only after one candidate has a clear address kind and expected behavior:

1. close the game and start a fresh process;
2. verify the executable hash and module identity again;
3. repeat the same transition in a second replay;
4. compare the member displacement or pointer chain, not absolute addresses;
5. run the relevant GameHarness scan/snapshot/compare path;
6. preserve evidence counts and invariant results in the ledger.

A candidate that survives one replay but not a second is `superseded` or
`stale`, not a near-success.

## Phase 5 — Publication and promotion

Use the repository tools only after the evidence record is complete:

```powershell
.\tools\discover-offsets.ps1 -SelfTest
.\tools\report-offset-evidence.ps1 -GameVersion 11.19.0.10
python scripts/python/offset_check.py --check-schema
```

`discover-offsets.ps1` may normalize a unique CE candidate, but that operation
only records `Candidate` evidence. Before publication, review that:

- decimal and hexadecimal forms agree;
- the module name and module base are recorded;
- the value is not merely a heap-dynamic address; module-range membership is
  only a publication prerequisite, not proof of field identity or correctness;
- the executable hash belongs to the exact analyzed binary;
- the field name and field type are correct;
- the source session ID is present in the ledger;
- conflicting candidates were retained as report-only evidence.

Promotion to `Verified` remains separate and requires the schema's complete
requirements: exact executable identity, two independent process launches, two
independent replays, passing harness invariants, static-analysis and
GameHarness provenance, lead approval, and decoder-auditor approval.

## Timeboxed decision tree

```text
10 min  identity/safety gate
  ├─ blocked → fix identity/lifecycle; no scan
  └─ ready
20 min  static triage
  ├─ clear typed access → dynamic verification
  └─ unclear/noisy → choose dynamic anchor
45 min  controlled dynamic anchor
  ├─ narrowing + expected behavior → structural tracing
  ├─ no signal → change field or scan method
  └─ ambiguous → record candidates; do not publish
30 min  instruction/neighbor tracing
  ├─ member displacement → repeatability campaign
  └─ heap-only → find pointer/root or abandon field
2 launches × 2 replays
  ├─ stable → candidate publication review
  └─ unstable → stale/superseded; pivot
```

A session that ends at any branch still produces a useful result if its ledger
entry says what was ruled out.

## Current next-session protocol

Session ID: `OD-RECOVERY-016`. `OD-RECOVERY-015` completed as **Partial**:
canonical OD launch amended (game-folder `.wotbreplay` → managed gate via
`scripts/launch-offline-replay-for-od.ps1`); Churchill sha12 `0FAE5612491E`
imported (`duplicate=True` → `independentReplays` still 0); rolling RT increased
**882617→…→7** (`private-mapping`).

### Canonical OD launch (amended 2026-08-02)

**Replay source:** `%LOCALAPPDATA%\wotblitz\DAVAProject\replays\*.wotbreplay`
(example basename pattern: `YYYYMMDD_HHMM__… .wotbreplay`). That folder is the
owner-chosen source of which battle to play.

**Do not** treat file-association alone (`Invoke-Item` / double-click) as the OD
launch path. It can start playback and show a replay HUD, but Host.Web never
receives managed lifecycle evidence, so the gate stays `Denied` /
`launch.lifecycle_evidence_timeout` (or `Unknown`) and discover APIs refuse.
Watch Offline clicks against a `Denied` host are wasted.

**Correct sequence** — use the helper:

```text
powershell -File scripts/launch-offline-replay-for-od.ps1
```

What it does:

1. Stops stale `wotblitz` / Host.Web / CE (clears a sticky `Denied` host).
2. Starts Host.Web with research lease (`OfflineReplayEvidenceLifetimeSeconds=120`,
   `LifecycleEvidenceTimeoutSeconds=120`).
3. Picks the newest `.wotbreplay` from the game replays folder (or `-ReplayPath`).
4. Imports via CLI → content-addressed artifact (basename + sha12 only in logs).
5. `POST /api/v1/game/launch` (managed) with a **freshly read** rendezvous
   capability (tokens rotate ~5 minutes; mid-wait 401 means re-read, not “auth broken”).
6. Waits for a real window, then **settles** (default 40s) so **WATCH OFFLINE**
   can appear — launching/clicking too early drops into the hangar.
7. Runs `scripts/click-watch-offline.ps1` (agent-owned). Exit 0 only when **both**
   `OfflineReplayVerified` **and** the orange dialog is gone. Screenshot:
   `%TEMP%\wotb-watch-offline-verify.png`. Exit 6 = host already `Denied` → re-run
   the launch helper, do not keep clicking.

File-association remains useful only as a **playback smoke** (“does this
`.wotbreplay` open?”). It is not a substitute for steps 1–7 before scanning.

### After gate green

1. Confirm the screenshot is a **replay HUD**, not the garage / **BATTLE!** menu.
2. Reuse rolling Double increased recipe to ≤10 survivors, then interactive
   x64dbg/CE GUI Find-what-writes, **or** confirm a **second distinct replay**
   (BLK-0019).
3. Do not promote from neighborhood hit counts alone.
4. Append ledger + handoff before stopping.

The success criterion remains **one correctly classified, reproducible
candidate**. Do not promote from aggregate counts alone.
