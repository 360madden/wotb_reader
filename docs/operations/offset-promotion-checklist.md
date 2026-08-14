# Offset promotion checklist

Last verified: 2026-08-12 (after G0 publication OD-RECOVERY-083, the
Phase-4 two-replay closures OD-RECOVERY-089/091, CAM-013, the 08-12
live-frame refinements, and the **G1 publication applies OD-RECOVERY-092 —
`playerHP` + `playerYaw` are now PUBLISHED `Verified` via module-rooted
`chains`** (2026-08-12; both `offsets` stay 0; post-edit gates green).

Purpose: the single place that maps every promotion gate to its **current,
source-verified status** and the exact evidence that flips it. The poll verdict
(`stable-resolver-positive`) and the two hardcoded aggregate flags are the
framework's own conservative claims; this checklist tracks what remains before
a numeric offset may be published and promoted. Nothing here broadens the
resolver, the read surface, or `memory-offsets/11.19.0.10.json`.

## Verified current state

Two distinct 11.19.0 replays/fresh processes have produced 24/24
`stable-resolver-positive` polls with the unchanged module-rooted resolver:

- `OD-RECOVERY-075` — Dead Rail replay (battle 2026-07-29), 24/24, 24 distinct,
  5 exact retained matches, 21/24 within three units.
- `OD-RECOVERY-076` — Oasis Palms replay (battle 2026-08-02), 24/24, 24 distinct,
  12 within one unit, 21/24 within three.

Replay distinctness was verified against the decoded-sessions store (the two
11.19.0 `battle_sessions` rows: Dead Rail vs Oasis Palms). **Cross-replay
continuous-polling repeatability is therefore proven at the ledger level.**

## Framework flag map (source-verified 2026-08-09)

| Aggregate flag | Value today | Where it comes from | Verdict effect |
|---|---|---|---|
| `allConsistentDoubleRead` | `true` (computed) | Resolver double-collects the 56-byte ring record twice with the ring index stable before/middle/after, then re-validates the full root chain (`src/WotBTreader.Core/Discovery/Type10EntityPositionResolver.cs` ~482–541) | Required for the positive verdict |
| `hardwareAtomicReadProven` | `false` (hardcoded) | Resolver success path returns `HardwareAtomicReadProven: false` (`Type10EntityPositionResolver.cs` line 210); endpoint passes through | Positive verdict **requires it false** |
| `sameDecodedClockProven` | `true` when the clock anchor landed (computed from replay-clock segments since the G2 wiring, 2026-08-09) | `GameSessionCoordinator.IsSameDecodedClockAsync` (segment uncertainty within the coordinator bound) | **Not part of the G1 verdict** — reported separately as the G2 claim (CORRECTED OD-RECOVERY-081: the old "requires it false" clause made a positive G1 verdict unreachable whenever G2 worked) |
| `stableRootLiveRepeatabilityProven` | `false` (hardcoded) | `scripts/od-073-entity-position-poll.ps1` line 468 — not wired to any input | Not part of the verdict |
| `offsetTablePromotionReady` | `false` (hardcoded) | `scripts/od-073-entity-position-poll.ps1` line 469 | Not part of the verdict |
| Verdict `stable-resolver-positive` | — | Requires resolved == requested, moving, trajectory consistent, all module-rooted, all identity-revalidated, all double-read consistent, and `hardwareAtomicReadProven` false (the resolver never claims it) — `sameDecodedClockProven` is NOT a disqualifier since OD-RECOVERY-081 (schema v4) | — |

Key consequence (amended 2026-08-09, OD-RECOVERY-081): the framework
refuses to claim atomicity until the resolver returns that flag from real
evidence (it never does — the G1 claim rests on the poll's own
`allConsistentDoubleRead` byte-identical double-read branch). The
same-decoded-clock proof is computed from real segments (G2 wiring) and is
a separate, composable claim — a working G2 no longer disqualifies the G1
stability verdict. Prior to this correction, the poll's `-not
$anySameDecodedClock` clause (written when the flag was hardcoded false)
made a positive G1 verdict UNREACHABLE in any session where the G2 clock
anchor landed.

## Gates and acceptance criteria

### G0 — Numeric-offset publication and promotion (final gate)

Uses the existing Phase 5 process in
[`offset-discovery-workflow.md`](offset-discovery-workflow.md): first
`report-offset-evidence.ps1` + `offset_check.py --check-schema` (publication
records only `Candidate`), then promotion to `Verified` requires exact
executable identity, two independent process launches, two independent replays,
passing harness invariants, static-analysis and GameHarness provenance, lead
approval, and decoder-auditor approval.

**G0 review executed 2026-08-09 (OD-RECOVERY-082) — verdict PROMOTE-READY
(conditional):** executable identity exact (`tools/compute-exe-hash.ps1`
re-measured `1cda5c31...`), RVA chain re-verified hop-by-hop against
`Type10EntityPositionLayout.WotBlitz1119010`, field identity
(playerPositionX/Y/Z float32 triple at record `+0x10`; velocity NOT
promoted; playerYaw untouched), repeatability attested (2 launches, 2
content-distinct replays, harness invariants), read-only gates PASS
(`report-offset-evidence.ps1` + `offset_check.py --check-schema`). The
remaining step is the operator-approved table edit
(`memory-offsets/11.19.0.10.json` → playerPositionX/Y/Z `Verified`,
chain-form values, evidence, approvals) + post-edit gates; the exact
change is drafted in `docs/operations/g0-offset-table-draft.md` (grill
resolved the schema decision: offsets stay 0 — the runtime computes
`moduleBase + offset` — and the chain is recorded in a new additive
`chains` section); the post-publication regression plan (what the legacy
observation path must/must not emit, with the exclusion test now
implemented and passing) is in `docs/operations/g0-post-publication-regression.md`;
the one-page operator gate (exact commands, values, commit contents) is in
`docs/operations/g0-operator-checklist.md`.

**G0 publication APPLIED 2026-08-10 (OD-RECOVERY-083, operator-approved):**
the table is no longer frozen — `playerPositionX/Y/Z` are `Verified` via
the module-rooted position-ring chain (additive `chains` section in
`schema.json` + `11.19.0.10.json`; `offsets` stay 0 by design — the
runtime computes `moduleBase + offset` and the ring record is battle-scoped
heap), evidence appended (launches 4 / replays 2), approvals set,
`numericOffsetPublication: true`. All post-edit gates green:
`offset_check.py` PASS + `chains validated (3 field(s))`, evidence report
clean, offline links 112/112, exclusion test passed, `validate.ps1` exit 0.
What was NOT promoted at G0: velocity, playerYaw, replayTime, playerHP,
cameraPitch, aliveTankCount (playerHP + playerYaw published under G1
2026-08-12, OD-RECOVERY-092); no absolute/heap addresses published. The
resolver + read surface are untouched; the legacy observation path still
emits position nulls (chained fields excluded — pinned by
`ChainedFields_AreExcludedFromObservationReads`).

**Independent review (2026-08-10): zero defects found.** A fresh-eyes pass
cross-checked the whole package against source of truth: all 16 hops of
each published chain equal the `Type10EntityPositionLayout.WotBlitz1119010`
constants (root `0x04095C88` = 67722376; member offsets 12/292/280/296/
288/4/72/28/64/52/56/8/8/456; record offsets 16/20/24), the
primary/tertiary/secondary entity-map labels match `FindEntity`'s `sources`
array, the draft §2b chain equals the published table exactly, the wrong
decimal `67518856` appears nowhere (only as the documented caught error),
and no doc still claims the position is unpublished. Chain shape is also
enforced by `offset_check.py` (chained ⇒ `offsets` 0, note-hex cross-check)
on every `validate.ps1`/CI run.

**Gate-closure evidence review (2026-08-10): sound.** Re-derived the stored
verdict of every positive-run aggregate with the poll's exact schema-v4
condition (evidence untouched): OD-075 (v2), OD-076 (v3), and OD-082 (v4)
stored `stable-resolver-positive` re-derive positive; OD-081 (v3) stored
`honest-negative-or-inconclusive` re-derives positive — the **documented**
verdict-contract case (its label is historical under the old v3 logic; the
gates were deliberately NOT closed on it). G2: `sameDecodedClockProven`
true in the four live runs (OD-078/079/081/082 stored aggregates), false
pre-G2 (OD-075/076) — the 4x confirmation claim holds. G3: OD-075/076 pass
the poll's exact fail-closed prior check (schema `wotbtreader.od073*` +
positive verdict); the stored `stableRootLiveRepeatabilityProven` is false
in 081 (never exercised — verdict negative) and 082 (mechanical
comma-binding, documented), both consistent with the ledger.

## Publication packages READY (2026-08-11) — Phase-4 two-replay rules

The G0 Phase-5 review defined promotion gates per field; the Phase-4 rule
requires **two-replay live agreement** (`twoReplayRepeatability = true`).
Status as of 2026-08-12:

| Field | Live agreement | Evidence | Publication state |
|---|---|---|---|
| `playerPositionX/Y/Z` | two replays, 24/24 polls (Dead Rail OD-075 + Oasis OD-076) | OD-RECOVERY-083 | **PUBLISHED 2026-08-10** (`Verified`, chains, offsets 0) |
| Facing/yaw (ring record `+0x30`) | **HIT both replays** — Oasis 088 (48/48), Dead Rail 089 Phase-4 (56/56, score 1.0, flatness 1.0) | OD-RECOVERY-088/089 | **PUBLISHED 2026-08-12 (OD-RECOVERY-092)** — `Verified`, chains (position walk + `recordOffset 48`), offsets 0 |
| HP (entity base `+0xB8`, signed int16) | **HIT both replays** — Oasis 087 (8/8 strict), Dead Rail 091 Phase-4 (4/4 strict, 58 dumps) | OD-RECOVERY-087/091 | **PUBLISHED 2026-08-12 (OD-RECOVERY-092)** — `Verified`, chains (entity-lookup prefix + `recordOffset 184`), offsets 0. Item-7 static support: hash-bound listing-confirmed 16-bit health setters (`FUN_0166b9f0`/`FUN_01675f60` write `+0xB8`/`+0x11E` as `MOV word`), zero 64/128-bit stores to the fields — `item7-hardware-atomicity-proof-plan.md` Branch A |
| Velocity, replayTime, playerHP-as-offset, aliveTankCount | not promoted | G0 record | untouched |
| Pitch/roll (ring `+0x2C`/`+0x28`) | **HIT both replays** — rotation-triple reconciliation: `yaw-diff --field pitch\|roll` re-verdicts the SAME OD-088/089 dumps (Oasis 48/48 + Dead Rail 56/56 each, score 1.0, flatness 1.0, record-span 0x38-trimmed) | OD-RECOVERY-088/089 | **PUBLISHED 2026-08-12 (OD-RECOVERY-098)** — `Verified`, chains (position walk + `recordOffset 44`/`40`), offsets 0; rotation triple fully published |
| Damage-dealt (Avatar stats quad `+0x0`, uint32) | **HIT both replays** — Oasis 095 (5/5 exact sums, lag-corrected), Dead Rail 096 Phase-4 (9/9 exact sums, score 1.0, flatness 1.0, Strict ≥ 2), re-proven live in-session OD-099 (5/5 exact sums, default lag); offsets agree at `0x0` → `twoReplayRepeatability = true` | OD-RECOVERY-095/096/099 | **PUBLISHED 2026-08-12 (OD-RECOVERY-097)** — `Verified`, chains (`vftableScan` 0x032752a4 → `recordOffset 280`), offsets 0; new hop kind; CONSUMPTION committed 2026-08-12 (live frame's own-row `DamageDealt` via the avatar-stats anchor; fail-closed null) |
| Camera pose (posA yz-swap + basis) | CAM-013 verified the W2S seam live (091/091b/092 chase, `w2sProjectionVerified: true`) | CAM-013 | overlay consumption only — no offset promotion (the camera track is a separate workstream, read surface untouched) |

Applied: `g1-yaw-publication-draft.md` + `g1-hp-publication-draft.md`
(OD-RECOVERY-092, 2026-08-12), `g2-damage-dealt-publication-draft.md`
(OD-RECOVERY-097, 2026-08-12 — new `vftableScan` hop kind, apply
rehearsal-proven before landing), and
`g1-pitch-roll-publication-draft.md` (OD-RECOVERY-098, 2026-08-12 —
table-only, rehearsal-proven first run). Each applied the chain from the
pre-staged draft to the published table (`chains` additive section,
`offsets` stay 0), appended evidence + approvals, then re-ran
`offset_check.py --check-schema` and the full gate. **Eight fields are now
`Verified` via chains.** The fidelity branch (`offset_check.py`) validates
the walkable draft against the published fields (identity enforced
post-publication). Remaining lanes: damage-dealt consumption (live frame's own-row read,
implemented 2026-08-12 — the own row now carries real `DamageDealt`;
enemy/teammate rows stay honest-0) and item 7 (hardware atomicity, LAST). Item 7 (hardware atomicity) stays LAST by design —
execution plan pre-staged `docs/operations/item7-hardware-atomicity-proof-plan.md`.

### G1 — Hardware-atomic read proof

**CLOSED 2026-08-09 (OD-RECOVERY-082):** the stored v4 aggregate is 24/24
`stable-resolver-positive` with `allConsistentDoubleRead=true` — the per-read
byte-identical branch (see the acceptance below). Proven across four positive
runs (OD-075, OD-076, OD-081 re-derivation, OD-082 stored), including two
live un-armed sessions (081/082).

Current: the resolver's read is an **optimistic double-read, not an atomic
read** (its own doc comment, line 134). Two byte-identical 56-byte ring-record
snapshots with a stable ring index prove the writer did not update the record
*between* the two reads, but nothing observes the writer's behavior *during*
either read. A 12-byte float triple cannot be read as one x86 atomic unit, so
"atomic" must be established with respect to the writer.

Acceptance — `HardwareAtomicReadProven` becomes `true` only from evidence, not
hardcoding:

The batch surface's `ConsistentDoubleRead` flag became computed-true for a
delivered byte-identical pair on 2026-08-14, with retry/tear telemetry exposed
per entity. This does **not** change the resolver poll's existing requirement
that `hardwareAtomicReadProven` remain false, nor does it itself prove hardware
atomicity; the post-change two-replay tear telemetry in the item-7 plan still
has to land first.

- Mechanism A (write-observation): reuse the guard-page interceptor family
  (OD-RECOVERY-046/047/048 machinery, `tools/WriteInterceptor`) in the same
  battle to prove no write to the ring record's position bytes occurred across
  a poll read window (or that every observed write produced a byte-identical
  record). **Offline mechanism test — done 2026-08-09 (OD-RECOVERY-077):**
  `scripts/test-offline-write-observation.ps1` passed — the interceptor
  captured 185/185 consecutive CRT-memcpy writes with an exact 0.5 value
  progression (no gaps = no missed writes), zero hits across a suspended 2 s
  no-write window with liveness on both sides, and 100% of hits attributed to
  `msvcrt.dll+0x8DD34` with the i386 esi/edi copy ABI. **Superseded
  (OD-RECOVERY-080):** arming the ring-record page makes the poll's own
  reads fail (ERROR_PARTIAL_COPY 299 at the avatar-helper vtable hop), so
  the operative G1 acceptance is the per-read byte-identical branch below
  (the interceptor's clean branch is impossible while the ring is actively
  rewritten).
- Mechanism B (interlocked discipline): a documented read discipline where the
  12 bytes are validated against a hardware-atomic guard (e.g., the ring
  index + a checksum read atomically) — requires a demonstrated invariant, not
  an assertion.

#### G1 live-poll procedure (built 2026-08-09, exercises live)

The position-page capability is **built and tested offline (2026-08-09):**
`POST /api/v1/game/discover/position-page` (gate-verified, fail-closed on
unsupported build) returns the ring-record address and its page-aligned
address via a diagnostic-only resolver entry
(`Type10EntityPositionResolver.ResolveRecordAddress`, same traversal and
reads as the poll path — the poll path itself is untouched and never carries
addresses). Tests: 5 resolver (Core), 3 coordinator (GameIntegration), 2
endpoint (Host.Web). The one-command orchestration is **built offline
(2026-08-09):** `scripts/invoke-g1-live-poll.ps1` runs the launcher →
rendezvous/state → session/trajectory → position-page → interceptor arm →
**unchanged** od-073 poll (`-SessionId` passed explicitly) → verdict; its
`Test-WriteObservationVerdict` was validated against the real OD-077
mechanism-test reports (a window inside the suspended span → clean, exit 0;
a busy span → observed). The operator runbook (pre-flight, watch items,
evidence review, shutdown, failure branching) is in
`docs/operations/offset-discovery-workflow.md` → “G1/G2 live run”, and the
pre-staged ledger section + handoff skeleton are in
`docs/operations/g1-live-evidence-template.md` (static values pre-filled,
placeholders for the evidence). **CORRECTED (2026-08-09, OD-RECOVERY-080):**
the interceptor arm is SKIPPED in the corrected run — arming PAGE_GUARD on
the ring-record page fails the poll's own reads at the avatar-helper vtable
hop (ERROR_PARTIAL_COPY 299; the OD-078/079 19/24 and 22/24 failures were
harness artifacts, not a pointer race). The G1 per-read byte-identical
branch is delivered by the unchanged poll itself (`allConsistentDoubleRead`,
proven 24/24 un-armed in OD-075/076). The live sequence in one new approved
session:

1. Gate: `invoke-g1-live-poll.ps1 -ReplayPath ... -WindowWaitSeconds 240
   -SkipInterceptorArm -PriorResultPaths <OD-075 + OD-076 aggregates>`
   launches and blocks until `OfflineReplayVerified` (same session the poll
   runs in). The prior positives arm G3's `stableRootLiveRepeatabilityProven`
   (see the template for the exact two paths).
2. Resolve: position-page → `recordAddress` (diagnostic evidence only; NOT
   armed in the corrected mode). Privacy stance: internal diagnostic
   surface, localhost only, gated, address not bytes — same evidence class
   as the od-048 family reports; never serialized into poll results or
   persisted aggregates (the poll aggregate's `processAddressesPersisted`
   stays false).
3. Poll: the unchanged bounded od-073 double-reads run; with
   `-SkipInterceptorArm` the wrapper records `write-observation-skipped`
   (the interceptor's clean branch is impossible while the ring is actively
   rewritten, and arming corrupts the poll — OD-RECOVERY-080).
4. Verdict: the G1 per-read byte-identical branch is the poll's own
   `allConsistentDoubleRead` — the resolver only returns `Resolved` with
   `ConsistentDoubleRead=true` when the two 56-byte ring-record snapshots
   are identical with a stable ring index (a mid-read write would tear them
   → `UnstableSnapshot` retry). Acceptance: **24/24 `stable-resolver-positive`
   with `allConsistentDoubleRead=true`** (already achieved un-armed in
   OD-075/076; the armed runs could not reach it because the guard page
   failed the reads).
5. Evidence: `g1-evidence.json` (verdict, read window, poll exit/succeeded,
   aggregate path; report path empty in corrected mode) is attached to the
   ledger row before the flag may be claimed. The marker owner-only ACL
   invariant is enforced by the poll (fails before any read if tampered).
   The same session also exercises G2 live: the poll POSTs the
   `CaptureLog` clock anchor at the gate moment, so a single
   `invoke-g1-live-poll.ps1` run produces the G1 evidence AND the
   `SameDecodedClockProven` outcome in the poll aggregate.

**First live run — done 2026-08-09 (OD-RECOVERY-078):** the whole chain
worked end-to-end (position-page 30 ms, interceptor armed, 128 module-mapped
page writes — `wotblitz.exe+0x1AD2D9D` dominant, verdict `observed` with 18
in-window / 53 before / 56 after), but the poll resolved 19/24: 5 reads
failed at the `avatar-helper` hop after exhausting 3 attempts (entity stayed
in the primary map — originally read as a pointer-race/reallocation pattern;
**SEE OD-RECOVERY-080: this was the interceptor's PAGE_GUARD corrupting the
poll's own reads**, not the game).

**Second live run — done 2026-08-09 (OD-RECOVERY-079):** the identical
command repeated cleanly and the poll improved to **22/24** (2 reads failed
at the same `avatar-helper` hop; write-observation `observed` again — 129
module-mapped writes, same two dominant copy-loop sites
`wotblitz.exe+0x1AD2D9D` 85 / `+0x230E856` 39, 18 in-window / 53 before /
57 after; G2 anchor re-confirmed `sameDecodedClockProven=true`).

**ROOT CAUSE — done 2026-08-09 (OD-RECOVERY-080, offline):** the avatar-
helper failures were NOT a game-side pointer race. The guard-page interceptor
arms PAGE_GUARD on the ring-record page, and the resolver's avatar-helper
stage reads the helper vtable at `helper+0x00` — the same page as the ring
(ring at `helper+0x08`, verified from the armed addresses). The interceptor's
own code documents that ReadProcessMemory on a PAGE_GUARD page fails with
ERROR_PARTIAL_COPY (299). Natural experiment: the SAME unchanged poll
UN-ARMED delivered 24/24 twice (OD-075/076, 48/48 reads,
`allConsistentDoubleRead=true` — the verdict requires it); armed, it failed
7/48 reads, 7/7 at `avatar-helper`. The interceptor's clean (zero-write)
branch is impossible while the ring is actively rewritten, and arming
actively breaks the poll. **Corrected procedure: `invoke-g1-live-poll.ps1
-SkipInterceptorArm`** — the per-read byte-identical branch is the poll's
own `allConsistentDoubleRead` (proven 24/24 un-armed); the wrapper's new
`-SkipInterceptorArm` mode records `write-observation-skipped` and rests the
G1 claim on the poll aggregate. One corrected session targets 24/24
`stable-resolver-positive` to close G1 + G3.

Evidence artifact must be attached to the ledger row before the flag may be
claimed.

### G2 — Same-decoded-clock alignment

Current (verified 2026-08-09): the clock machinery **already exists and is
unit-tested** — `ReplayClockSegment` (`src/WotBTreader.Core/ComparisonModels.cs`),
`IReplayClockSegmentRepository.AppendAsync/ListAsync` +
`SqliteReplayClockSegmentRepository` (tested in
`ReplayClockSegmentRepositoryTests`), `SegmentedReplayClockSource` which
extrapolates `replayEstimate = ReplayAnchor + (observedAtUtc -
SourceAnchorUtc) * Speed` with `Estimated`/`Stale` quality (tested in
`SegmentedReplayClockSourceTests`), and `TrajectoryCorrelation` which already
anchors wall time to the replay clock (`ReplayClockTicksPerSecond` =
10,000,000). What is missing: **no production caller ever appends a segment**
(`AddSegmentAsync` has no caller outside the clock source itself, so
`replay_clock_segments` stays empty), and `SameDecodedClockProven` is
hardcoded `false` at both coordinator result sites
(`GameSessionCoordinator.cs`, passed through by `GameApiEndpoints`).

Acceptance — `SameDecodedClockProven` becomes `true` only from evidence:

1. **Live anchor (new approved session):** record one segment at the
   verified-gate moment — `SourceAnchorUtc` = wall clock when the gate verifies
   (the blitz-log `Start replay event` marker is the natural anchor, from the
   replay-start-flake fix), `ReplayAnchor` ≈ 0 + measured watch offset,
   `Speed` = 1.0 (managed offline playback), `Source` = `CaptureLog`,
   `Uncertainty` = measured marker/gate latency. **The append capability is
   built and tested (2026-08-09):** `POST /api/v1/game/discover/clock-segment`
   (`AppendClockSegmentRequest` → `IReplayClockSource.AddSegmentAsync`, server
   assigns id/creation time, enforces monotonicity; 6 endpoint tests cover
   valid append, bad/missing session id, invalid values, and source failure).
   **The caller is built (2026-08-09):** `od-073` captures the gate
   wall-clock on verification and POSTs the anchor (sequence 0,
   replay-anchor 0, speed 1.0, `CaptureLog`, 1 s uncertainty — the gate-loop
   cadence) before its stage delay; a failure is non-fatal (flag stays false)
   and monotonicity conflicts are ignored. **Live exercise — done 2026-08-09
   (OD-RECOVERY-078):** the poll POSTed the anchor at the gate
   (`clock_anchor appended sequence=0 uncertainty_s=1`) and the aggregate
   returned `sameDecodedClockProven=true` — the flag computed from real
   segments within the 2 s bound. Remaining: the ledger row records the
   correlation bounds (anchor 1 s + gate cadence 1 s).
2. **Wiring — implemented 2026-08-09.** The request now carries an optional
   `BattleSessionId` (endpoint parses the GUID; `od-073` sends the session it
   already selected), and `GameSessionCoordinator` computes
   `SameDecodedClockProven` from `IReplayClockSource` (injected, registered via
   `AddCaptureLogs`): segments must exist, the snapshot must not be `Stale`,
   and uncertainty must be ≤ 2 s (`SameDecodedClockUncertaintyLimit`).
   Five focused unit tests cover null-session / missing-segments / stale /
   beyond-bound / within-bound; full gate green (659 tests, architecture +
   composition intact). The flag still stays `false` until a live anchor
   populates segments.
3. **Record:** a ledger row with the correlation bounds (worst-case tick
   error across the poll).

### G3 — Stable-root live repeatability (framework wiring)

Current: repeatability is **proven at the ledger level** (OD-075 + OD-076) and
the framework wiring is **implemented (2026-08-09)**. The runner now takes
`-PriorResultPaths` and computes `stableRootLiveRepeatabilityProven` from this
positive run plus at least one operator-supplied prior positive aggregate;
fail-closed (any missing/unparseable/non-positive prior keeps the flag false).
Distinct artifacts are attested by the operator/ledger because result files
carry no artifact id by privacy design. Flag logic verified against real
result files (positive, negative, missing, no-prior cases). No product code
or read-surface change.

**CLOSED 2026-08-09 (OD-RECOVERY-082):** the v4 close-out run was positive
(`stable-resolver-positive`) and the OD-075/076 priors were validated
directly against the poll's exact fail-closed check (schema
`wotbtreader.od073*` + positive verdict — both pass). The stored
`stableRootLiveRepeatabilityProven` field is false only because the
comma-joined `-PriorResultPaths 'a,b'` invocation was bound by `-File` as a
single path (mechanical bug, fixed in the wrapper — comma elements are now
split/trimmed); it is not an evidence deficiency. The G0 review's G3
definition — ledger attestation of two distinct replays with fresh
processes (Dead Rail OD-075 + Oasis Palms OD-076) — is satisfied.

## Sequencing recommendation

| Step | Type | Dependency |
|---|---|---|
| ~~G3 flag wiring~~ | ~~Offline (runner script)~~ | **done 2026-08-09** (`-PriorResultPaths` in od-073) |
| ~~G2 coordinator wiring + tests~~ | ~~Offline~~ | **done 2026-08-09** (`BattleSessionId` in request; flag from clock source) |
| ~~G2 anchor endpoint (append capability)~~ | ~~Offline~~ | **done 2026-08-09** (`/discover/clock-segment`, 6 tests) |
| ~~G2 anchor caller (gate moment → POST segment)~~ | ~~Offline~~ | **done 2026-08-09** (built into od-073; live exercise pending) |
| ~~G2 live run: anchor + flag end-to-end~~ | ~~Live (approved session)~~ | **done 2026-08-09 (OD-RECOVERY-078): `sameDecodedClockProven=true` in the poll aggregate** — CaptureLog anchor at the gate, 1 s uncertainty within the 2 s bound; correlation bounds (anchor 1 s + gate cadence 1 s) recorded |
| ~~G1 mechanism test (write-observation)~~ | ~~Offline~~ | **done 2026-08-09** (`scripts/test-offline-write-observation.ps1`, OD-RECOVERY-077) |
| ~~G1 position-page capability (resolver entry + coordinator + endpoint + tests)~~ | ~~Offline~~ | **done 2026-08-09** (`POST /discover/position-page`, diagnostic-only) |
| ~~G1 live orchestration (wrapper + verdict)~~ | ~~Offline~~ | **done 2026-08-09** (`scripts/invoke-g1-live-poll.ps1`; verdict validated on OD-077 reports) |
| ~~G1 live poll (G1 closed)~~ | ~~Live (done 2026-08-09)~~ | **done — OD-RECOVERY-082:** stored v4 aggregate 24/24 `stable-resolver-positive` with `allConsistentDoubleRead=true` (per-read byte-identical branch). Armed runs 19/24 + 22/24 were harness artifacts (OD-RECOVERY-080); run 081 hit 24/24 clean read evidence but the verdict label was blocked by a verdict-contract conflict (fixed, schema v4); run 082 delivered the stored positive aggregate |
| G0 publication review | Offline (verdict delivered) | **done 2026-08-09 (OD-RECOVERY-082): PROMOTE-READY (conditional)** — exe identity exact, RVA chain verified, field identity set, repeatability attested, read-only gates PASS; the table edit is a separate operator-approved change |
| G0 offset-table publication | Offline (operator-approved gate) | **done 2026-08-10 (OD-RECOVERY-083):** `playerPositionX/Y/Z` → `Verified` via the module-rooted position-ring chain (additive `chains` section; offsets stay 0), evidence appended (4 launches / 2 replays), approvals set, `numericOffsetPublication: true`; post-edit gates all green (`offset_check.py` chains-validated 3 fields, `validate.ps1` exit 0). Resolver + read surface untouched; NOT promoted: velocity, playerYaw, replayTime, playerHP, cameraPitch, aliveTankCount |
| Phase-4 facing/yaw repeat | Live | **done 2026-08-11 (OD-RECOVERY-089): HIT** — ring `+0x30` agrees on Dead Rail (56/56, score 1.0, flatness 1.0, per-dump bounded bidirectional lag). `twoReplayRepeatability = true`; yaw publication **READY** (`g1-yaw-publication-draft.md`) |
| Phase-4 HP repeat | Live | **done 2026-08-11 (OD-RECOVERY-091): HIT** — entity `+0xB8` agrees on Dead Rail (score 1.0, flatness 1.0, Strict 4/4 exact sums via `--lag-lead-seconds`). `twoReplayRepeatability = true`; HP publication **READY** (`g1-hp-publication-draft.md`) |
| Publication applies (HP then yaw) | Operator-gated | **DONE 2026-08-12 (OD-RECOVERY-092)** — both packages applied, all post-edit gates green (`offset_check.py` chains-validated 5 fields + fidelity 5/5, `offline_check.py --refresh`, `ChainedFields_AreExcludedFromObservationReads`, `validate.ps1` exit 0) |
| Rotation-triple Phase-4 reconciliation (pitch +0x2C / roll +0x28) | Offline re-verdict | **done 2026-08-12** — `yaw-diff --field pitch\|roll` re-verdicts the SAME immutable OD-088/089 dumps: pitch `+0x2C` and roll `+0x28` AGREE on both replays (Oasis 48/48 + Dead Rail 56/56 each, score 1.0, flatness 1.0; record-span 0x38-trimmed verdicts — ring-sibling decoy `0x60` removed). `twoReplayRepeatability = true` for the full rotation triple; pitch/roll publication package **PRE-STAGED** (`g1-pitch-roll-publication-draft.md` — schema slots + draft chains + checker already staged, operator approval only) |

## Frozen surfaces (unchanged)

- Resolver read surface stays server-owned and unchanged.
- No broadening of scans, reads, or the artifact binding.
- ~~`memory-offsets/11.19.0.10.json` is not edited and no numeric offset is
  promoted before G0's Phase 5 review~~ — **superseded 2026-08-10
  (OD-RECOVERY-083): the operator-approved G0 publication edited the table
  (playerPositionX/Y/Z → `Verified` via the `chains` section; `offsets`
  stay 0).** The resolver and read surface remain frozen and untouched.
- Privacy rules: aggregate-only results; no entity ID, coordinates, process
  address, raw byte, capability, replay path, or player/account data in
  tracked docs.
