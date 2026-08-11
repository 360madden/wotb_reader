# WoT Blitz PC offset-discovery workflow

Last updated: 2026-08-11 (OD-RECOVERY-087 closed L1 HP: the entity-base
current-health int16 is confirmed live at `+0xB8` — HIT, score 1.0,
flatness 1.0, Strict 8/8)

This is the operational playbook for discovering memory evidence from the
Windows WoT Blitz client during a **positively verified offline replay**. It is
optimized for timely, reproducible results rather than maximum scan volume.

The workflow is deliberately conservative: a readable address, a unique scan
result, or a plausible value is a hypothesis. It is not a runtime-supported
offset.

## Current decision

Session ID: `OD-RECOVERY-088`.

**OD-RECOVERY-087 is DONE (2026-08-11) — L1 HP HIT live.** The entity-base
current-health signed int16 is **confirmed at `+0xB8`** on Oasis Palms
(victim 3760578, 9 events / 1,183 damage, 74 dense-span dumps): every one
of the 8 health drops equals its damage sum exactly (149, 173, 174, 164,
168, 142, 198 = 41+157, 15), max `+0x11C` 1550 constant, alive `+0xBA` 1,
healing `+0x11E` 0; the automated contract fires **HIT — score 1.0,
flatness 1.0, Strict 8/8** via the new subset-sum lag attribution
(`hp-diff --int16 true --lag-tolerance 4`). Key finding: the game applies
decoded damage events to the health field with a **variable ~1–3.4 s
memory-apply lag** (measured with the dense span) — the driver now dumps a
dense span around each hit and the correlator matches drops against event
subsets (`--lag-tolerance`, default 0 = exact). The X4 live frame's
`hp: null` can become real (additive change). Evidence:
`docs/operations/od-recovery-087-evidence-template.md` (filled) + ledger
`OD-RECOVERY-087` result section.

**Next planned session (2026-08-11, OD-RECOVERY-088): L2 facing** — the
ring-record `+0x2C` yaw confirm. Drive `scripts/invoke-facing-session.ps1`
(region ≥ 0x40, `+0x2C..+0x37` probe first, wrap-aware matcher, flatness
1.0 over stationary segments) live on Oasis Palms with the
**launch-matched host-store session + `-DataRoot "$env:LOCALAPPDATA\
WotBTreader"`** (the launcher logs `battleSession=` at the gate; both 086
and 087 proved the repo-local `.data/treader.db` 404s in the host store).
The Phase-4 rule requires the offset to agree on Dead Rail too. Evidence
template: `docs/operations/od-recovery-088-evidence-template.md`.

**The position anchor is ESTABLISHED and PUBLISHED (2026-08-10):**
`playerPositionX/Y/Z` are `Verified` via the module-rooted position-ring chain
(`OD-RECOVERY-083`) and the chains are mechanically walkable by
`OffsetChainWalker` since `OD-RECOVERY-084` (see `docs/operations/g0-offset-
table-draft.md` §7; the walker reads the published table directly, pinned
resolver-equal by `Walk_PublishedTableChains_*`). The position family met the
roadmap definition of done (2 launches × 2 replays, pointer-chain
classification, published). No further live session is needed for position.

The `11.19.0.10` `playerYaw` evidence remains **quarantined as stale and
ambiguous** — the prior table recorded decimal `51808784` (`0x03168A10`), its
notes claimed `0x0317A810` (`51882000`), and the raw Ghidra export's top
candidate was decimal `56085200` (`0x0357CAD0`). The published offset stays `0`
while the conflict remains in evidence notes and the ledger; none of these
values may be used as a trusted scan anchor until the original analysis is
reconciled.

**Reconciled 2026-08-11 (resolved-by-supersession, see the ledger):** the
address-kind question is answered by the L2 facing track — yaw is a **runtime
chain field on the movement ring record** (`+0x2C`, ring stride 0x38),
reachable only through the module-rooted entity chain (same reason position
moved to `chains` in G0). Static module-offset candidates are the wrong kind
for this field by construction, and the three legacy values were mutually
inconsistent on their own terms (the table's own decimal does not convert to
its own notes hex). The static candidates are retired; the ring-record `+0x2C`
field is the yaw anchor, rehearsed 27/27 + 35/35 by the facing correlator
against packet yaw ground truth, pending the live L2 facing session. The
published table keeps yaw at `0`/Stale; a future publication would be a
`chains` entry.

Next anchors after position: the pre-staged live gates in order — **L2
facing** (OD-RECOVERY-088, ring-record `+0x2C`;
`invoke-facing-session.ps1`), then CAM-001 v7. **L1 HP is DONE
(OD-RECOVERY-087, HIT at `+0xB8`)** — the live-frame HP bar can become real
(additive contract change), and the Phase-4 two-replay rule for HP (Dead
Rail victim 2549399) gates any HP publication.
`replayTime` retains
its rolling increased-Double evidence (OD-012..038) and `playerHP` has the
query-side ground truth ready (`IHpGroundTruthProvider`); both ride the same
`entity-region` seam. Do not repeat the unresolved yaw neighborhood scan
(quarantine resolved-by-supersession 2026-08-11).

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
reference or an x64dbg-resolved address into `memory-offsets/` without classifying
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

**Freshness gate (added 2026-08-05):** the host must be freshly built before
every live session. A stale publish or stale build silently 404s on newer
endpoints — e.g. the Jul-31-class publish predates the strategy-v4
trajectory/correlate endpoints entirely, so an M1 campaign against it would
burn a CAP-2 session before producing any evidence. The OD launch path runs
`dotnet run --no-build` (stale `bin/Release` builds are the same class of
failure); `launch-offline-replay-for-od.ps1` now fail-closes on an
out-of-date host assembly. The dashboard/serve path uses
`.build\publish\WotBTreader.Host.Web.exe` — run `serve.cmd` (it republishes)
instead of invoking the publish exe directly. See
[`offset-discovery-m1-m2-choreography.md`](offset-discovery-m1-m2-choreography.md)
Phase 0 for the exact commands.

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

Use x64dbg's interactive tracing or the guarded loopback snapshot/compare
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
4. If x64dbg is used for local structural follow-up, keep it read-only: do
   not edit or freeze values, inject code, or create dumps, pointer maps, or
   screenshots for the repository.
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
.\tools\report-offset-evidence.ps1 -GameVersion 11.19.0.10
python scripts/python/offset_check.py --check-schema
```

Publication only records `Candidate` evidence (never `Verified`). Before
publication, review that:

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

Pointer-chain fields (e.g. the position family, published 2026-08-10 via
OD-RECOVERY-083) are recorded in the table's additive `chains` section
(field → array of `{kind: rootRva|memberOffset|inlineOffset|recordOffset|
ringIndex|entityLookup, value, note}` hops; `ringIndex` also requires
`indexOffset` and `stride`, `entityLookup` requires its descriptor (cached
fast path + alternative tree roots, node layout, target id supplied per
walk), and the shape must be
`rootRva → memberOffset|inlineOffset|ringIndex|entityLookup* → recordOffset`)
with their `offsets` value kept 0 — the runtime observation path computes
`moduleBase + offset` and cannot represent a chain, so a non-zero value
would corrupt reads; the resolver reads chained fields via its own
hash-bound layout. `OffsetChainWalker` walks chains fail-closed and
expresses the position walk (inline entities + entityLookup + INLINE
ringIndex) — proven equivalent to the resolver. The published 11.19.0.10
position chains ARE mechanically walkable since OD-RECOVERY-084
(2026-08-10): the walkable re-expression (`inlineOffset` +
`entityLookup` + INLINE `ringIndex`) was applied through the operator gate
(`docs/operations/g0-offset-table-draft.md` §7) — the same walk the
OD-RECOVERY-083 evidence verified, now read directly by `OffsetChainWalker`
and pinned resolver-equal by `Walk_PublishedTableChains_*` (published table
→ `OffsetTableReader` → walker → X/Y/Z floats, plus the Core equivalence
suite). The pre-publication memberOffset-spelled form remains in git
history (commit `0e6bdba`) and the ledger. The resolver remains the
authoritative position reader; the walker is a proven-equivalent consumer
of the published table. The full mechanism, gates, and post-publication
contract are in
`docs/operations/g0-offset-table-draft.md`,
`docs/operations/g0-operator-checklist.md`, and
`docs/operations/g0-post-publication-regression.md`. The replay-event
inventory for record-diffing discovery (damage/destroyed canonical events,
persistence, and the ground-truth gap) is in
`docs/operations/record-diffing-groundwork.md`.

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

> **Amended 2026-08-11 (composed X2+X3 rehearsal is next; OD-RECOVERY-086).**
> The next approved session runs `scripts/invoke-batch-rehearsal.ps1
> -EnumerateLive -LiveAcquire -Times 90,150,220 -FailOnMiss` on Oasis Palms —
> (1) the X3 live-roster enumeration through `/discover/entity-roster`
> (design `docs/operations/live-roster-read-design.md`) verdict against the
> decoded participants roster (matched/missing/extra + movement-filter
> precision), then (2) the X2 full-roster batch read through
> `/discover/entity-regions` (design
> `docs/operations/batch-entity-read-design.md`, consolidation item 6) with
> the ENUMERATED ids, cross-checked against decoded positions and measuring
> the read-pass window. Pre-staged: coordinator methods + endpoints + 21
> tests shipped, driver `-EnumerateLive` + cross-check tool proven (42/42
> position pairs; enumeration self-tested exact/missing/extra/
> traversal-limited), evidence template
> `docs/operations/od-recovery-086-evidence-template.md`. It closes the X2 +
> X3 rehearsals and feeds the item-7 measurement; the L1/L2/CAM-001 live
> gates follow in their pre-staged order. The scan/roll/debugger material
> below is retained as historical evidence only.
>
> **Amended 2026-08-10 (position closed; HP-discovery live plan pre-staged).**
> The scan/roll/debugger material below is retained as historical evidence,
> but it is superseded: the position family is PUBLISHED and walkable
> (OD-RECOVERY-083/084, `docs/operations/g0-offset-table-draft.md` §7), and
> HP discovery's offline side is complete with the approved-session live plan
> pre-staged (`docs/operations/record-diffing-groundwork.md` — one bounded
> gated region-read addition, session flow, verdict contract). The next
> approved session runs the HP-diffing live plan (or, per the roadmap
> preference order, a `replayTime` live attempt); the material below remains
> valid only as evidence of what was ruled out.
>
> **Amended 2026-08-08 (type-10 application-path policy).** The scan/roll/debugger
> material below is retained as historical evidence, but it is superseded for
> player-position work. FRESH44 satisfied repeatability only for transient
> viewpoint correlation; FRESH45 rejected the candidate-derived contiguous
> layout at the sampled instant. Do not run either path unchanged.

OD-RECOVERY-066 completed the one permitted world-matrix-translation capture.
The exact instruction fingerprint, bounded seven-hit read, and cleanup were
valid, but the opaque-object trajectory did not match any decoded participant.
The best coherent absolute fit across every participant, 48 axis/sign mappings,
0.5x-8x playback, and scene-marker uncertainty missed by mean 10.850 / max
12.556 units, with 0/7 samples within 1 unit. Static analysis still proves that
EBX+`0x90/+0x94/+0x98` is a composed matrix translation; the negative concerns
the sampled object's semantic identity or coordinate space.

OD-RECOVERY-069 completed the offline/static trace. `ReplayPlayer` installs
type-10 handler RVA `0x00FE31C0`, which reads the exact 49-byte sequence and
dispatches into `BWEntities::handleEntityMoveWithError`. The resolver proves
`[entity+0x1C]` is the packet entity ID. The entity-movement function at RVA
`0x022FA780` reaches instruction RVA `0x022FA78D`, bytes `F30F7E00`, with the
resolved entity in `ESI` and the packet-derived XYZ pointer in `EAX`.
`TraceType10MovementPosition.java` verifies 40/40 fixed relationships against
the exact executable hash.

OD-RECOVERY-070 completed implementation and synthetic validation. The helper
now reads one int32 at `[ESI+0x1C]` and one contiguous 12-byte vector at `EAX`
while the matching debug event is held. Target, registers, displacements, and
schema remain server/helper policy for the exact version/hash. The synthetic
x86 target executed the exact instruction and produced entity ID `4242` plus
four changing finite XYZ samples. Fingerprint, hit bound, non-Host parent
rejection, cleanup, and detach passed; the Host response suppresses all raw
addresses. The entity-ID and vector reads share a suspended debug event but are
not hardware-atomic. Same-decoded-clock and local-player identity also remain
unproven.

OD-RECOVERY-071 completed that bounded live proof. The five-second/64-hit
request ran in a positively verified offline replay and completed with 49 hits;
all 49 had successful replay-entity-ID and finite XYZ reads. Seven decoded
vehicle entities matched exactly after Float32 normalization, and one was the
replay viewpoint entity. A separate zero-vector object had no decoded
trajectory and was excluded. Fingerprint and cleanup were proven, and all live
processes were stopped after comparison.

This establishes player-position identity at the fixed type-10 instruction in
one session, not motion freshness or a polling offset: the retained values did
not change during the capture, the comparison was time-agnostic, and the two
reads are not hardware-atomic. `OD-RECOVERY-072` may repeat the exact frozen
target during a verified movement window on the other content-distinct replay.
Require a changing viewpoint series plus same-entity decoded matches; do not
scan or change the target/register/displacement in that session.

OD-RECOVERY-072 completed that motion/repeatability gate. The unchanged target
reached its 64-hit bound with 64/64 valid reads. Twelve decoded entity IDs
matched; 13 hits exactly survived the downsampled trajectory, 41 were within
one unit, and 57 were within three units. Most importantly, the replay
viewpoint yielded six distinct triples and two exact downsampled matches. The
same instruction/register contract therefore reads moving player position on
both content-distinct replays and fresh processes.

Do not spend more live budget repeating this event. The workflow now pivots
offline/static to continuous-read architecture: trace the resolved viewpoint
entity back to a stable container/root and validate the already identified
`[entity+0x38]` movement-filter helper/ring (`0x38` stride, current index at
helper `+0x1C8`, position at record `+0x18`). Any later live request must test a
specific stable resolver plan, not reconfirm the type-10 event.

OD-RECOVERY-073/074/075 completed and corrected that implementation gate. The
current-build resolver is now pinned to executable version `11.19.0.10`, SHA-256
`1cda5c31919c9784a41bee7f3270ec1b4536b124c51e8b36f2221b381760307d`, and
module root RVA `0x04095C88`. It follows `GameCore +0x0C -> AppController
+0x124 -> SessionController +0x118 -> AccountController +0x128 -> active
PlaybackController +0x120 -> replay BWServerConnection`, treats connection
`+0x04` as embedded `BWEntities`, then
uses the engine's cache plus three bounded entity-ID trees. The chosen entity,
AvatarFilter, helper, ring index, full record, and root chain are revalidated.
`TraceEntityRegistryPosition.java` passes 82/82 fixed static checks. The ring
begins at helper `+0x08`; position is record `+0x10/+0x14/+0x18`; velocity is
record `+0x28/+0x2C/+0x30`; and current index remains helper `+0x1C8`.

The production contract is deliberately narrower than the old discovery APIs:
the caller supplies only the decoded replay entity ID. The coordinator owns the
verified managed process, module base, exact build identity, and every address
and displacement. Unsupported builds return an honest status before a memory
reader is created; gate revocation cancels the identity-bound read and discards
its result. The public result contains status, bounded diagnostics, and XYZ but
no process address or raw bytes. The OD-073 runner persists aggregates only.

Static follow-up proved the observed `WGVehicleFilterHelper` shares the exact
common store/readback layout. It also found that the earlier resolver
double-counted helper-relative position `+0x18` and landed on velocity at
record `+0x28`. The corrected, exact-artifact-bound live poll resolved 24/24
positions with 24 distinct triples, 5 exact retained-trajectory matches, and
21/24 within three units in one fresh verified process. This is the first
strong continuous-poll positive, but not cross-replay proof.

The unchanged content-distinct repeat exited before `OfflineReplayVerified`;
no memory request ran. Treat BLK-0026 as a launch/evidence blocker, diagnose it
separately, and then run one unchanged bounded repeat. Do not merely broaden
the vtable allowlist, expose caller-supplied addresses, or resume broad scans.

Session ID: `OD-RECOVERY-075`.

This is the durable policy pivot for community-derived clues: use historical
offsets only to propose relationship families, re-derive all current-build
addresses and displacements from hash-bound static evidence, implement an
exact-build server-owned resolver, prove it synthetically, and spend live
budget only on the remaining semantic question. No historical address is
carried forward merely because it was recently posted.

The first static triage did not find a direct consumer anchor. Across 526,935
executable functions, displacement-layout matches were dominated by matrix,
copy, serializer, and destructor code; the highest-ranked candidate was
decompiled and refuted. No same-base direct comparison of record length `49`
and type `10` exists in the scanned code, and all eight apparent initialized
dispatch rows were MSVC exception metadata. The surviving route is to find the
generic replay event reader/framer from replay/file entry points and follow its
payload data flow. Do not rerun the literal/layout heuristics as if a higher
candidate count supplied semantic evidence.

OD-RECOVERY-068 also tested the repository's historical community Vehicle
layout as a candidate family. The old root remains refuted. The real
`VehicleGameLogic` slot-`+0x04` getter still returns `[this+0x04]`, but none of
17 getter-using virtual methods accesses the claimed entity position triple at
`+0x68/+0x6C/+0x70`. The sole exact generic chained match and the strongest
float fallback both decompile as matrix/pose structures. Do not read that stale
triple live. Its useful `+0x1C` clue is now resolved: the type-10 application
path directly compares that entity member with the packet ID. The stale
position triple remains refuted.

Do not spend a live replay on a broader matrix read, a different displacement at
the transform-fill instruction, a latency-only retry, or another heap scan. The
instruction-snapshot mechanism is now pinned to the two-source target and has
passed synthetic and first-live equality review. OD-RECOVERY-071 proves
entity-location identity and independently matches one exact entity to the
replay viewpoint. OD-RECOVERY-072 proves motion freshness and cross-replay
repeatability for the unchanged event. Stable resolution and offset publication
remain separate work. Existing safety contract:
[`../superpowers/specs/2026-08-08-instruction-first-position-snapshot.md`](../superpowers/specs/2026-08-08-instruction-first-position-snapshot.md).

> **Amended 2026-08-04 (v3 strategy).** The pilot order below is superseded by
> [`offset-discovery-strategy-v3.md`](offset-discovery-strategy-v3.md) /
> [`offset-discovery-roadmap.md`](offset-discovery-roadmap.md): the
> **exact-value pause scan is now the primary pivot** (`-CompareMode exact`,
> replay paused at a known decoded value, absolute target), with the Double
> replayTime delta pilot as the fallback filter. The pipeline facts in this
> section remain valid.

Historical session ID: `OD-RECOVERY-045`. `OD-RECOVERY-044` proved the live pipeline
**mechanically end-to-end for the first time**: gate green → pre-arm → rolling
→ harvest → address file → x32dbg direct attach → arm → `scriptload`+`scriptrun`
injection → run. Rolling collapsed **861399→…→1 survivor in 16 rounds** (the
campaign record; previous best OD-020's 5) with three driver fixes landed live:
**harvest retry** (the fresh increased-compare can return 0 when the tail froze
between the target round and the harvest — retry 5×2s, survivors tick every
frame), **plateau-stop** (`increased=0` is a value-bound plateau, NOT a
0-survivor target — keep the last non-zero round's serialized candidates), and
**small-set serialization** (bump the candidate request once the set is small so
the last non-zero round carries the addresses). The single survivor was
**`0x7FFE0010` = `KUSER_SHARED_DATA.SystemTime`** — the Windows shared kernel
clock (FILETIME-style, ticks every 100ns): the always-ticking clock is the last
"increased" Double in a process whose game field stopped ticking (the game died
mid-roll from the replay-start flake; the 47→44→1 drop is the death signature).
Kernel writes to that page never fire user-mode hardware breakpoints, so the 0
write-trace hits were **by construction** — the mechanism was NOT the failure.
The driver now drops the `0x7FFE0xxx` page from the address file + WARN. Also
landed: the **x96dbg launcher was dropped from pre-arm** — it stayed alive
without spawning x32dbg (ShellExecute/state-machine brittleness); pre-arm and
the write-trace resolver now launch `release22dbg.exe` directly (the
game bitness is known x86 — WOW64-observed, `ImageFileMachineI386`), which
attached 2/2.

`OD-RECOVERY-045` runs the **Double replayTime delta pilot FIRST** (the
OD-045-STATIC simulation ranks it deterministic: pass-rate 1.0 at every
tolerance, survival 1.0 over 15 rounds): reuse the proven invocation
(`-SnapshotMaxBytes 402653184 -MaxRounds 40 -HoldAfterRollSeconds 240`) with
`-CompareMode delta -DeltaTarget 4.0 -DeltaTolerance 0.4 -ValueKind Double`
(unit variants if needed: 4000ms / 4,000,000ticks). The delta band's
value-bound rejection also sheds the kernel clock (its 100ns increments fall
outside a 4.0±0.4s delta), removing the dying-game false positive outright.
Measure survivor collapse vs "increased" (11 → predicted ≤2–4), then the Float
position pilot **on a movement-only span** (`--movement`: only 32.3% of the
Dead Rail replay is moving — 891/2,756 windows), then **automated x64dbg
write-trace** on the staged set via `scripts/x64dbg-write-trace.ps1`
(`-AutoWriteTrace`; bphw write breakpoints by command-bar injection; writing
RIP captured via `{rip}`-named `savedata` evidence files — the operator step
is now optional). **Run with the operator present** during the held green
window: the write-trace mechanism is proven, so a surviving window is the only
missing ingredient for the first RIP. The replay-start flake (~50% of 8
launches this session died within ~2s of gate green — `onLeaveWorld` → `become
hidden` → `OnBackground`, no crash dump) is the lease-budget killer; root-causing
it is a parallel workstream. `--hp-delta --victim-entity <id>` remains a
supporting marker (conditional on a damaged victim). Do NOT waste lease probing
the handler records, the AnyFn table, or chasing the exhausted static paths as
singletons.
static paths as singletons. This builds on the validated driver stack:
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

Plus the staging handoff rule: always use the default survivor
address-file path (`%TEMP%\od-survivors.txt`) so the pre-armed debugger and
the automated write-trace (`scripts/x64dbg-write-trace.ps1`) find the staged
set, and keep the write-trace poll window covering the whole lease + roll tail.

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
harvest only on the target round (OD-RECOVERY-031); (8) use the default survivor
address-file path — a custom `-AddressFile` silently skips the downstream
staging step (first observed as a CE-autorun staging miss in OD-RECOVERY-031
attempt 4; the same rule now covers the x64dbg pre-arm/write-trace staging); (9) **convergence lands at 11–17
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
pattern; (2) the rolling driver stages survivors to
`-AddressFile` (default `%TEMP%\od-survivors.txt`) for the pre-armed x64dbg,
and **`scripts/x64dbg-write-trace.ps1` automates the write-trace**: it reads
the staged addresses, generates an x64dbg script that arms `bph <addr>,w,8`
write breakpoints (≤4 — the DR0-DR3 limit; extras are reported unarmed),
sets `bphwlog` + a `SetHardwareBreakpointCommand` that runs `savedata` so
each hit writes `<hits>\odwt-0x<addr>-<rip>.bin` (64 bytes at RIP — the
writing instruction bytes; `savedata` string-formats its filename arg, so the
RIP is in the filename: the automatable evidence channel), and fast-resume so
the replay keeps playing; it injects `scriptload`+`scriptrun` into the
running x64dbg's command bar (x64dbg's CLI has no script-execution flag —
only `-p` attach — so command-bar injection is the no-new-install path) and
polls the hits dir + the gate; (3) the automated CE write-BP hit path stays
ruled out (OD-020) — x64dbg's hardware write BPs are a *changed mechanism*
the OD-020 rules-out explicitly sanctioned, and unlike CE's automated path
this one writes evidence files without GUI scraping; Cheat Engine is no
longer part of the pipeline (removed 2026-08-03); (4) stale game / host
processes are stopped automatically by the next launch helper; (5) the
operator window is held
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

   It locates x64dbg via known install roots, launches it attached to the
   running `wotblitz.exe` with `-p <pid>`, and writes
   `%TEMP%\od-prearmed-debugger.json`. Then run the automated write-trace:

   ```text
   powershell -File scripts/x64dbg-write-trace.ps1 -TraceSeconds 120
   ```

   (or pass `-AutoWriteTrace -WriteTraceSeconds 120` to the session driver
   so it runs inside the held green window). It arms a hardware write
   breakpoint (`bph <addr>,w,8`) on the staged survivors, captures the
   writing RIP to `%TEMP%\od-wt-hits.txt` + evidence bytes in
   `%TEMP%\od-wt-hits\`, and exits 0 with the gate held. `-DryRun` generates
   the x64dbg script + prints the plan without touching the debugger.
   Manual fallback (operator present): load `%TEMP%\od-survivors.txt` and
   arm a hardware write breakpoint (bphw) on each for Find-what-writes.
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

### G1/G2 live run — write-observation + clock anchor (2026-08-09)

The current gates (hardware-atomic read proof G1, same-decoded-clock G2) are
exercised by **one command** — the unchanged bounded od-073 poll, orchestrated
by `scripts/invoke-g1-live-poll.ps1`. **CORRECTED (2026-08-09,
OD-RECOVERY-080):** the guard-page interceptor arm is SKIPPED — arming
PAGE_GUARD on the ring-record page fails the poll's own reads at the
avatar-helper vtable hop (ERROR_PARTIAL_COPY 299; the OD-078/079 19/24 and
22/24 failures were harness artifacts, not a pointer race). The G1 per-read
byte-identical branch is the poll's own `allConsistentDoubleRead` (proven
24/24 un-armed in OD-075/076); the interceptor's clean branch is impossible
while the ring is actively rewritten. Run:

```text
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/invoke-g1-live-poll.ps1 `
  -ReplayPath <replay> -WindowWaitSeconds 240 -SkipInterceptorArm `
  -PriorResultPaths .data/od-073-entity-position-poll-20260809-021445.json,`
    .data/od-073-entity-position-poll-20260809-165144.json
```

The wrapper normalizes comma-joined `-PriorResultPaths` (2026-08-09,
OD-RECOVERY-082 — `-File` binds the comma form as a single path, which
failed the poll's `Test-Path`); both comma-joined and space-separated forms
work.

**Pre-flight.** Exact-build binaries present (a `scripts/validate.ps1` green
run); no stale `wotblitz.exe` / Host.Web / interceptor processes; the
content-distinct replay (Oasis Palms, 1,045,525 B) in place; disk space for
the evidence dir (`.data/diagnostics/g1-live-<stamp>/`). (The x86
interceptor publish is not needed in the corrected mode; it is only for the
legacy armed evidence mode.)

**During.** The launcher blocks until `OK OfflineReplayVerified` — a
`FAILED_no_window` on a cold boot is covered by `-WindowWaitSeconds 240`;
marker/ACL failures are the BLK-0026 class (resolved). The wrapper then
prints: `game_pid`, `battle_session`, `entity_id`, `record_address`, and the
final `verdict` line (in corrected mode: `write-observation-skipped`). Do
not touch the game window (the launcher already shrank it and the dialog
clicker is done). The poll's `clock_anchor appended` line tells whether the
G2 anchor POST landed.

**After — evidence review (a flag is claimable only when ALL of these hold):**

- `g1-evidence.json` exists with `pollSucceeded=true`.
- Poll aggregate: `verdict=stable-resolver-positive`, `resolvedReads=24`,
  `allModuleRooted`, `allEntityIdentityRevalidated`,
  `allConsistentDoubleRead` all true — the per-read byte-identical branch
  (a mid-read write would tear the double-read into an `UnstableSnapshot`
  retry, so every `Resolved` read is byte-identical with a stable ring
  index). This is the G1 claim in the corrected procedure.
- **G1 claim:** poll 24/24 `stable-resolver-positive` with
  `allConsistentDoubleRead=true` (un-armed runs already achieved this in
  OD-075/076; the armed runs could not because the guard page failed the
  reads). In corrected mode the write-observation verdict is
  `write-observation-skipped` by design.
- **G2 claim:** poll aggregate `sameDecodedClockProven=true` (the anchor
  POST succeeded and its 1 s uncertainty is within the 2 s coordinator
  bound).
- **Composability (CORRECTED 2026-08-09, OD-RECOVERY-081, schema v4):** a
  working G2 (`sameDecodedClockProven=true`) does **not** disqualify the
  G1 verdict. The old `-not $anySameDecodedClock` clause (written when the
  flag was hardcoded false, pre-G2) made a positive G1 verdict unreachable
  whenever the clock anchor landed; the same-clock proof is separate
  composable evidence. If a stored aggregate shows
  `honest-negative-or-inconclusive` despite 24/24 clean reads, check the
  poll schema — v3 predates the fix, v4 is current.
- Do **not** promote the offset table on this evidence; G0 publication
  review follows.

**Shutdown.** Stop the managed processes after reviewing the evidence:
`wotblitz.exe` and Host.Web (no interceptor in corrected mode); verify zero
orphans remain (the ledger's shutdown row counts them).

**Failure branching.** Launcher exit != 0 → diagnose by exit code, no poll
ran. Poll exit != 0 → the wrapper exits non-zero with the evidence written;
per the exactly-one-unchanged-poll rule, do **not** auto-retry — diagnose
(the research lease / battle-end classes are known) and re-run only after a
fresh decision. Legacy armed mode: interceptor exit != 0 → verdict fails
closed and the evidence records it; the poll result alone cannot claim the
write-observation branch.
