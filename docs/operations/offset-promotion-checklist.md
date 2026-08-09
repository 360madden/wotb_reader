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
  record). Offline mechanism test first, then one live poll.
- Mechanism B (interlocked discipline): a documented read discipline where the
  12 bytes are validated against a hardware-atomic guard (e.g., the ring
  index + a checksum read atomically) — requires a demonstrated invariant, not
  an assertion.

Evidence artifact must be attached to the ledger row before the flag may be
claimed.

### G2 — Same-decoded-clock alignment

Current: **the `replay_clock_segments` table is empty** (verified 2026-08-09) —
the schema exists (`source_anchor_utc`, `replay_anchor_ticks`, `speed`,
`source`, `uncertainty_ticks`) but nothing populates it. The coordinator
hardcodes `SameDecodedClockProven: false`, so no poll claim can be made today.

Acceptance — `SameDecodedClockProven` becomes `true` from evidence:

1. The decoder records clock segments per battle session (offline work, no live
   testing): anchor UTC ↔ replay ticks with speed and uncertainty, as the
   schema already models.
2. The coordinator correlates each poll read's wall-clock to replay time
   through those segments and reports a bounded uncertainty.
3. A ledger row records the correlation bounds (e.g., worst-case tick error
   across the poll).

### G3 — Stable-root live repeatability (framework wiring)

Current: repeatability is **proven at the ledger level** (OD-075 + OD-076) but
the aggregate flag is hardcoded `false`. The framework has no input that
reflects prior positive result files.

Acceptance — `stableRootLiveRepeatabilityProven` reflects reality:

- Wire the flag to a real input — e.g., a campaign registry keyed by
  campaign + artifact that the runner reads, or a `-PriorResults` parameter
  pointing at the earlier positive aggregate files — and set it only when two
  distinct-artifact positive results exist.
- Runner-only change (scripts), no product code, no read-surface change.

## Sequencing recommendation

| Step | Type | Dependency |
|---|---|---|
| G3 flag wiring | Offline (runner script) | none |
| G2 decoder clock recording + validation against `replay_clock_segments` | Offline | none |
| G1 mechanism test (write-observation) | Offline | tools/WriteInterceptor |
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
