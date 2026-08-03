# WoT Blitz PC offset-discovery workflow

Last updated: 2026-08-03 (OD-042-STATIC: **`0x037F3054` precisely identified as the shared RTTI `type_info` vftable** (every TypeDescriptor's pVFTable points at it — confirmed td@0x03DB5120 `pVFTable == 0x037F3054`; constructor at 0x020CB956 writes it to `[this]+0`); **`tools/find-static-roots.py` gained `--vtables` which names 17,133 of 18,721 vtables via the COL chain** — the fix: MSVC x86 stores the mangled name **inline at td+8** (char[]), not as a VA pointer; chain-class vtables located incl. `GameScene` 0x0319D3C4 (26 slots, **0 .data roots** = honest negative for the vtable-singleton path), `BaseContext`/`RootContext`, Vehicle component family; **Vehicle-family TypeDescriptor xref = 0 .text refs / 0 slots** — RTTI name→root path exhausted; prior milestone OD-041-STATIC: the two 'root candidates' reclassified as members of a repeating 0x50-byte record family, NOT gameplay roots; rolling driver gained `-CompareMode delta`/`-DeltaTarget`/`-DeltaTolerance` pass-through for the Track C2 pilot)

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

Session ID: `OD-RECOVERY-043`. Static milestones `OD-039..042-STATIC`
landed and then **re-classified the two "root candidates"** as members of a
repeating 0x50-byte `.data` record family (base `0x03FA0C20`) — NOT
standalone gameplay roots; `0x037F3054` is precisely the shared RTTI
`type_info` vftable (every TypeDescriptor's pVFTable points at it). The new
`--vtables` mode names **17,133 of 18,721 vtables** (fix: MSVC x86 stores the
mangled name inline at td+8) including `GameScene` 0x0319D3C4 (26 slots, **0
.data roots** = honest negative for the vtable-singleton path) and the
Vehicle component family — but the Vehicle-family TypeDescriptor xref is
negative (0 refs / 0 slots), exhausting the RTTI name→root path. The rolling
driver exposes `-CompareMode delta -DeltaTarget X -DeltaTolerance T`.
`OD-RECOVERY-043` is the **Track C2 pilot**: reuse the proven invocation
(`-SnapshotMaxBytes 402653184 -MaxRounds 40 -HoldAfterRollSeconds 240`) but
add `-CompareMode delta -DeltaTarget <replay position delta> -DeltaTolerance
<tol>` and measure survivor collapse vs "increased" (11 → predicted ≤2–4),
then run operator Find-what-writes on the staged set — the replayTime set
remains the live anchor; do NOT waste lease probing the handler records or
chasing the exhausted static paths as singletons. This builds on the
validated driver stack:
The session also produced the **first-ever 401-refresh failure in 13
validations** (OD-038 attempt 3, round 9: the refreshed context re-read the
rendezvous file but retried immediately into a mid-rotation file, exhausting
the 2-retry budget) — **fixed by adding a 750ms settle after refresh +
raising Retries to 4** and validated live on its first use (attempt 4 round
8: 401 absorbed, roll continued). Attempt 4 rolled **39M → 11 in 20 rounds**
(plateau 11 rounds 16–20 — the value-bound tail again, 1 above target) then
the lease wall (round 21 `400`). The roll pipeline is proven (OD-036 staged
9 + armed 4), so **OD-039 runs the proven invocation**
(`-SnapshotMaxBytes 402653184 -MaxRounds 40 -HoldAfterRollSeconds 240`)
**with the operator present during the held green window** — the 11-survivor
set is usable for interactive Find-what-writes even without ≤10. This builds
on the validated driver
stack:

1. **401 capability-rotation refresh** — the rendezvous token rotates ~5 min,
   and a 66M-baseline roll outlives it; the driver refreshes the rendezvous
   and retries on 401 (`Invoke-OdApi`, logged `capability_401_refresh_retry`).
   Validated live an eleventh time (OD-036 round 6).   Validated live an eleventh time (OD-036 round 6).
   Validated live a tenth time (OD-035 rounds 15/10).
2. **Probe-fold** — the OD-026 standalone sanity probe ran a full 66M-candidate
   compare whose `previousCount` was identical to round-1's; the steady-state
   gate now lives in round 1 (an absurd round-1 `previousCount` discards +
   re-snapshots with a gate-aware wait).
3. **Candidate-count harvest** — rolling rounds request 1 candidate and pay no
   serialization cost on the big early compares (66M→1M); the full set is
   harvested only on the target round. This doubled rounds-in-lease (6–7 →
   10–14) and enabled OD-031's target convergence.

Plus the CE staging handoff rule: always use the default survivor
address-file path so the autorun can stage survivors into CE, and keep the
autorun poll window (300s) covering the whole lease + roll tail.

Key rules: (1) **never roll from a load-transition snapshot** — but the
steady-state gate must accept the *measured stable baseline* (66M this
session), not an assumed one (a 10M threshold wrongly rejected the stable
state); (2) a large stable baseline needs shorter transitions (5s) and a
higher round limit (22) to converge inside the 120s lease; (3) the rolling
`400` on a discarded session is a discard signature (gate flip or session
loss), not a host fault - the precise gate-transition trigger (lease expiry
vs monitor health) may be either and must be read from the gate state, not
assumed; (4) capture evidence should be read from persisted artifacts
(address file, autorun log, gate state) when the driver sequence is
live-terminal-only, so the record never invents unpersisted intermediates;
(5) **launcher-green + immediate `Denied`/`EvidenceStale`** on roughly
1-in-4 to 1-in-2 launches is a game-side assert crash at replay start
(OD-RECOVERY-030 attempts 2/5, OD-RECOVERY-031 attempt 2) — relaunch rather
than re-clicking; (6) a single rendezvous capability captured at roll start
is not valid for a full 66M-baseline roll (the 401 rotation) — the driver
refreshes + retries on 401; (7) rolling rounds request 1 candidate and
harvest only on the target round (OD-RECOVERY-031); (8) use the default CE
autorun survivor address-file path — a custom `-AddressFile` silently skips
CE staging (OD-RECOVERY-031 attempt 4); (9) **convergence lands at 11–17
survivors under current load, always just past the lease edge** — plan for
lease-headroom work, not more identical runs (OD-RECOVERY-032/033);
(10) **the tail is value-bound, not round-bound** — the last survivors tick
every frame and survive even 1s pulses, so pulse shaving alone cannot reach
≤10; the interactive Find-what-writes step is required regardless of lease
headroom (OD-RECOVERY-034); (11) **lease headroom alone cannot reach ≤10** —
proven across lease-constrained (OD-032/033/034) and lease-viable (OD-035:
round-1 823K/50M, full round budget with rolling_exit=0) rolls alike;
(12) **the `Invalid password status=68` login failure is a red herring, not
an offline-path blocker** — it appears in every blitz log since 14:48
game-time including the OD-036 SUCCESS run, and every launch reaches `Start
replay event` + `LoadGameScene`; the real replay-start death is `become
hidden` + `GameCore::OnBackground` ~1–2s after `LoadGameScene` ends with no
crash dump (OD-RECOVERY-038 corrected the OD-037 record); (13) **the 401
refresh can fail against a mid-rotation rendezvous file** — after refreshing
on a 401, settle ~750ms before retrying and keep ≥4 retries; validated live
when a round-8 401 was absorbed and the roll continued (OD-RECOVERY-038).

Operational notes: (1) detached launch + session driver remains the proven
pattern; (2) the CE autorun staging (`od-autorun-writebp.lua`) works and now
has a 300s poll — it stages survivors into CE's address list as
`od-survivor-N`, so the operator's Find-what-writes is a single right-click;
(3) the automated CE write-BP hit path stays ruled out (OD-020), so the
interactive step is the evidence path; (4) stale CE / host are stopped
automatically by the next launch helper; (5) the operator window is held
inside the running driver and re-announced every 30s — use
`-HoldAfterRollSeconds 240` (or higher) so the window is lease-bound, not
timer-bound; (6) the steady-state gate now lives in round 1 of the rolling
driver (`roll-replay-time-increased.ps1`): round-1 `previousCount` reports
the snapshot candidate count; reject only absurd/transient counts and
discard + re-snapshot with a gate-aware wait — no separate probe compare;
(7) the two-phase tail shave is now in the driver (`TailThreshold=200`,
`TailTransitionSeconds=1` passthrough from the session driver) and buys
lease margin (OD-034: 18 rounds fit the lease) — but it does not break the
value-bound tail; OD-035 should prioritize the interactive step, a snapshot
budget passthrough for a live operator window, or a content-distinct second
replay — not more identical runs.

### Canonical OD launch (amended 2026-08-02)

**Replay source:** `%LOCALAPPDATA%\wotblitz\DAVAProject\replays\*.wotbreplay`
(example basename pattern: `YYYYMMDD_HHMM__… .wotbreplay`). That folder is the
owner-chosen source of which battle to play. Managed launch stages under
`…\replays\wotbtreader-staging\` so GUID temp copies are not mixed with
originals; flat GUID clones the game may drop beside originals are scavenged
when the stage file is disposed.

**Playback-only (hangar UI)** — preferred when you only need an offline replay
in-game and want to avoid argv/`WATCH OFFLINE` Error 126:

```text
powershell -File scripts/play-replay-from-hangar.ps1
```

Attach or start `wotblitz.exe` (no replay argv) → orange **Battle** → profile →
**REPLAYS** → white play triangle on the first LATEST card. Spec:
`docs/superpowers/specs/2026-08-02-hangar-replays-play.md`. Confirms via
`Start replay event` / `START_REPLAY_LOCAL` in blitz-log. This path does **not**
set Host `OfflineReplayVerified` (discover APIs stay fail-closed).

**Do not** treat file-association alone (`Invoke-Item` / double-click) as the OD
launch path. It can start playback and show a replay HUD, but Host.Web never
receives managed lifecycle evidence, so the gate stays `Denied` /
`launch.lifecycle_evidence_timeout` (or `Unknown`) and discover APIs refuse.
Watch Offline clicks against a `Denied` host are wasted.

**Correct OD sequence** (discover gate) — use the helper:

```text
powershell -File scripts/launch-offline-replay-for-od.ps1
```

What it does:

1. Stops stale `wotblitz` / Host.Web / CE (clears a sticky `Denied` host).
2. Starts Host.Web with research lease (`OfflineReplayEvidenceLifetimeSeconds=120`,
   `LifecycleEvidenceTimeoutSeconds=120`).
3. Picks the newest **original** `.wotbreplay` from the game replays folder
   (top-level only; skips GUID leftovers and `wotbtreader-staging\`), or
   `-ReplayPath`.
4. Imports via CLI → content-addressed artifact (basename + sha12 only in logs).
5. `POST /api/v1/game/launch` (managed). Staging writes under
   `…\DAVAProject\replays\wotbtreader-staging\` (not mixed with originals). A
   freshly read rendezvous capability is required (tokens rotate ~5 minutes).
6. Waits for a real window, then runs Watch Offline with **visual ready
   feedback**: poll until the orange blob is stable (3 samples), hold ~2s, then
   click. No long blind settle (avoids dialog timeout) and no instant click
   (avoids racing the dialog).
7. Runs `scripts/click-watch-offline.ps1` (agent-owned). Exit 0 when
   `OfflineReplayVerified` is reached — the verified gate is the dialog-gone
   proof (the lifecycle monitor requires a fresh `START_REPLAY_LOCAL` marker;
   the orange-blob ROI false-positives on the replay HUD, amended 2026-08-03
   OD-RECOVERY-017). Screenshot: `%TEMP%\wotb-watch-offline-verify.png`.
   Exit 6 = host already `Denied` → re-run the launch helper, do not keep
   clicking. For hangar-chained dismiss without a Host correlation, use
   `-VisualDismissOnly` (playback-only success).

File-association remains useful only as a **playback smoke** (“does this
`.wotbreplay` open?”). It is not a substitute for steps 1–7 before scanning.

### After gate green

1. Confirm the screenshot is a **replay HUD**, not the garage / **BATTLE!** menu.
2. **Pre-arm CE 7.7 or x64dbg before or concurrent with rolling** — attach or
   open the interactive debugger as soon as the gate is green (or during managed
   launch settle). Do **not** wait until ≤10 survivors to start the debugger;
   the default 120s research lease can flip to `EvidenceStale` before
   Find-what-writes runs (OD-RECOVERY-016). Use the helper:

   ```text
   powershell -File scripts/pre-arm-debugger.ps1 -AutoAttach
   ```

   It locates CE 7.7 / x64dbg via registry + known paths, launches the chosen
   debugger attached to the running `wotblitz.exe`, and writes
   `%TEMP%\od-prearmed-debugger.json`. `-PreferX64Dbg` switches the default
   (CE first). CE's `-p <pid>` open-process flag is version-dependent; the
   robust alternative is loading `tools/cheat-engine/prearm-attach.lua` in CE
   (Ctrl+Alt+L → Execute) — it attaches to `wotblitz.exe` and stages the
   rolling driver's survivor addresses into CE's address list for one-click
   Find-what-writes.
3. Start rolling Double increased (`rollingBaseline=true`) immediately
   post-verify with Space pause/resume pulses; aim to reach ≤10 with lease
   margin reserved for interactive Find-what-writes on survivors. Use the
   driver:

   ```text
   powershell -File scripts/roll-replay-time-increased.ps1 -TargetSurvivors 10 `
     -AddressFile "$env:TEMP\od-survivors.txt"
   ```

   It snapshots Double (8-byte aligned) and compares `increased` with a
   rolling baseline each round, prints aggregate counts only, stops at the
   target or on gate loss, and discards the scanner session. The survivor set
   each round is the compare's `increasedCount` — `retainedCount` is only
   unreadable-chunk carryover, not survivors (OD-RECOVERY-017). With
   `-AddressFile`, the final compare's candidate addresses are written to that
   local (untracked) file for the pre-armed debugger; addresses never reach
   stdout or the repo. The compare candidate list is not contractually
   guaranteed to equal the retained survivor set — the driver logs a `WARN` on
   count mismatch, and the Lua pre-arm prints the same caution. The operator
   owns the Space transition; `-AutoSpace` is an explicit opt-in pulse loop.
   The steady-state gate is folded into round 1: round-1 `previousCount`
   reports the snapshot's candidate count, so an absurd count (game still
   loading) discards and re-snapshots with a gate-aware wait — no separate
   probe walk (OD-RECOVERY-030). The driver also refreshes the rendezvous
   capability and retries on a mid-roll `401` (the token rotates ~5 min and a
   66M-baseline roll outlives it — OD-RECOVERY-030).
4. Confirm a **second distinct replay** in the game folder when available
   (BLK-0019).
5. Do not promote from neighborhood hit counts alone.
6. Append ledger + handoff before stopping.

The success criterion remains **one correctly classified, reproducible
candidate**. Do not promote from aggregate counts alone.
