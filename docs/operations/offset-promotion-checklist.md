# Offset promotion checklist

Last verified: 2026-08-09 (after BLK-0026 resolution and the cross-replay proof,
ledger `OD-RECOVERY-076`).

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
| `sameDecodedClockProven` | `false` (hardcoded) | `GameSessionCoordinator.cs` lines 1789/1844 return `false` | Positive verdict **requires it false** |
| `stableRootLiveRepeatabilityProven` | `false` (hardcoded) | `scripts/od-073-entity-position-poll.ps1` line 468 — not wired to any input | Not part of the verdict |
| `offsetTablePromotionReady` | `false` (hardcoded) | `scripts/od-073-entity-position-poll.ps1` line 469 | Not part of the verdict |
| Verdict `stable-resolver-positive` | — | Requires resolved == requested, moving, trajectory consistent, all module-rooted, all identity-revalidated, all double-read consistent, **and** atomic + same-clock both false (`od-073` ~lines 426–433) | — |

Key consequence: the framework deliberately refuses to claim atomicity or
same-clock alignment until the coordinator/resolver return those flags from
real evidence. Hardcoding either flag to `true` would be a false claim and
would not change the verdict logic's intent.

## Gates and acceptance criteria

### G0 — Numeric-offset publication and promotion (final gate)

Uses the existing Phase 5 process in
[`offset-discovery-workflow.md`](offset-discovery-workflow.md): first
`report-offset-evidence.ps1` + `offset_check.py --check-schema` (publication
records only `Candidate`), then promotion to `Verified` requires exact
executable identity, two independent process launches, two independent replays,
passing harness invariants, static-analysis and GameHarness provenance, lead
approval, and decoder-auditor approval.

Prerequisite for any numeric value in the position-ring chain: G1, G2, and G3
below are closed, and the exact RVA chain is re-measured against the live build
hash immediately before the session. The offset table
(`memory-offsets/11.19.0.10.json`) stays frozen until then.

### G1 — Hardware-atomic read proof

Current: the resolver's read is an **optimistic double-read, not an atomic
read** (its own doc comment, line 134). Two byte-identical 56-byte ring-record
snapshots with a stable ring index prove the writer did not update the record
*between* the two reads, but nothing observes the writer's behavior *during*
either read. A 12-byte float triple cannot be read as one x86 atomic unit, so
"atomic" must be established with respect to the writer.

Acceptance — `HardwareAtomicReadProven` becomes `true` only from evidence, not
hardcoding:

- Mechanism A (write-observation): reuse the guard-page interceptor family
  (OD-RECOVERY-046/047/048 machinery, `tools/WriteInterceptor`) in the same
  battle to prove no write to the ring record's position bytes occurred across
  a poll read window (or that every observed write produced a byte-identical
  record). **Offline mechanism test — done 2026-08-09 (OD-RECOVERY-077):**
  `scripts/test-offline-write-observation.ps1` passed — the interceptor
  captured 185/185 consecutive CRT-memcpy writes with an exact 0.5 value
  progression (no gaps = no missed writes), zero hits across a suspended 2 s
  no-write window with liveness on both sides, and 100% of hits attributed to
  `msvcrt.dll+0x8DD34` with the i386 esi/edi copy ABI. Remaining: one live
  poll with the interceptor armed on the ring-record position page (new
  approved session).
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
a busy span → observed). The live sequence in one new approved session:

1. Gate: `invoke-g1-live-poll.ps1 -ReplayPath ...` launches and blocks until
   `OfflineReplayVerified` (same session the poll runs in; addresses are
   battle-scoped heap allocations — cross-battle arming is ruled out by live
   evidence). Use `-WindowWaitSeconds 240` for cold boots.
2. Resolve: position-page → `recordAddress` (the interceptor arms the page
   holding it). Privacy stance: internal diagnostic surface, localhost only,
   gated, address not bytes — same evidence class as the od-048 family
   reports; never serialized into poll results or persisted aggregates (the
   poll aggregate's `processAddressesPersisted` stays false).
3. Arm + poll: the interceptor captures every page write from before the
   stage delay to after the poll; the unchanged bounded od-073 double-reads
   run inside that window.
4. Verdict (two branches, computed by the script; grilling review
   2026-08-09):
   - **clean**: zero interceptor hits inside the read window
     `[pollStart + stageDelay, pollEnd]` with liveness (hits) on both sides
     and interceptor exit 0 — a real no-write across the read window. This
     is a STRONGER global claim than the acceptance needs.
   - **observed** (the EXPECTED live outcome for a moving entity whose ring
     slots are rewritten every few frames): writes landed on the page during
     the window. The interceptor arms the whole page (PAGE_GUARD granularity)
     and cannot attribute a hit to the exact position bytes, so the primary
     per-read atomicity proof comes from the POLL: the resolver only returns
     `Resolved` with `ConsistentDoubleRead=true` when the two 56-byte
     ring-record snapshots are identical with a stable ring index (a
     mid-read write would tear them → `UnstableSnapshot` retry). An observed
     verdict is therefore NOT a failure — every Resolved poll read already
     attests the byte-identical branch; the interceptor's role is the
     complete page write history around the reads.
5. Evidence: `g1-evidence.json` (verdict, read window, hit counts, armed
   addresses, report + aggregate paths, poll exit/succeeded) is attached to
   the ledger row before the flag may be claimed. The marker owner-only ACL
   invariant is enforced by the poll (fails before any read if tampered).
   The same session also exercises G2 live: the poll POSTs the
   `CaptureLog` clock anchor at the gate moment, so a single
   `invoke-g1-live-poll.ps1` run produces the G1 evidence AND the
   `SameDecodedClockProven` outcome in the poll aggregate.

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
   and monotonicity conflicts are ignored. Remaining: exercise it live in a
   new approved session (parse/PSSA green; no live run yet).
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

Remaining: the flag flips to `true` on the next positive poll that supplies
the prior positive result file(s).

## Sequencing recommendation

| Step | Type | Dependency |
|---|---|---|
| ~~G3 flag wiring~~ | ~~Offline (runner script)~~ | **done 2026-08-09** (`-PriorResultPaths` in od-073) |
| ~~G2 coordinator wiring + tests~~ | ~~Offline~~ | **done 2026-08-09** (`BattleSessionId` in request; flag from clock source) |
| ~~G2 anchor endpoint (append capability)~~ | ~~Offline~~ | **done 2026-08-09** (`/discover/clock-segment`, 6 tests) |
| ~~G2 anchor caller (gate moment → POST segment)~~ | ~~Offline~~ | **done 2026-08-09** (built into od-073; live exercise pending) |
| G2 live run: anchor + flag end-to-end | Live (new approved session) | caller + endpoint done |
| ~~G1 mechanism test (write-observation)~~ | ~~Offline~~ | **done 2026-08-09** (`scripts/test-offline-write-observation.ps1`, OD-RECOVERY-077) |
| ~~G1 position-page capability (resolver entry + coordinator + endpoint + tests)~~ | ~~Offline~~ | **done 2026-08-09** (`POST /discover/position-page`, diagnostic-only) |
| ~~G1 live orchestration (wrapper + verdict)~~ | ~~Offline~~ | **done 2026-08-09** (`scripts/invoke-g1-live-poll.ps1`; verdict validated on OD-077 reports) |
| G1 live poll + G2 live correlation | Live (new approved session) | G1/G2 offline steps |
| G0 publication review | Offline | G1 + G2 + G3 closed |

## Frozen surfaces (unchanged)

- Resolver read surface stays server-owned and unchanged.
- No broadening of scans, reads, or the artifact binding.
- `memory-offsets/11.19.0.10.json` is not edited and no numeric offset is
  promoted before G0's Phase 5 review.
- Privacy rules: aggregate-only results; no entity ID, coordinates, process
  address, raw byte, capability, replay path, or player/account data in
  tracked docs.
