# Item 7 — Hardware-atomicity proof (design + staged checklist)

> **STATUS: DESIGNED — NOT STARTED (LAST by design).** Every earlier gate
> (G0 publication, G1 read consistency, G2 same-decoded-clock, G3
> repeatability, L2 facing, camera/W2S) is deliberately closed BEFORE this
> one. The batch read surface (`/discover/entity-regions`) and its
> `Measurement` contract (pre-staged 2026-08-11) are the proof's
> home — this doc is the operator-facing spec + checklist so the proof can
> be executed the moment the earlier gates close. Nothing here has been
> run; no evidence exists yet.

## 1. What item 7 must prove

The position resolver reads a float32 triple (`+0x10/+0x14/+0x18` on the
ring record) that the game updates continuously. The question is NOT
"do two back-to-back reads agree" (that is G1, CLOSED — the stored v4
aggregate is 24/24 with `allConsistentDoubleRead=true`). Item 7 asks the
stronger question:

> Can the read surface claim the position bytes were captured as ONE
> coherent snapshot — either (a) hardware-atomic (a single read observes
> all three floats from the same write), or (b) atomicity-free by design
> (the consumer's tolerance makes partial reads indistinguishable from
> coherent ones)?

The answer is expected to be **(b)**: the float triple is written by
normal game code (not a lock-free atomic), so strict hardware atomicity
of a 12-byte triple is unlikely to be provable. What IS provable is the
**read discipline** that makes atomicity unnecessary:

- A bounded read window (measured, not assumed) per batch and per double-read.
- A consistency check that a torn read cannot silently pass.
- A consumer contract that tolerates the residual window (the frame is a
  best-effort snapshot, not a transactional query).

## 2. What already exists (all pre-staged / closed)

| Piece | Status | Where |
|---|---|---|
| Per-read double-read + `ConsistentDoubleRead` flag | CLOSED (G1) | position poll v4 aggregate 24/24; flag travels in every read result |
| Batch read surface (≤ 16 entities, one clock attestation per batch) | SHIPPED + rehearsal-proven 42/42 | `/discover/entity-regions`, `ReadEntityRegionsAsync` |
| Batch `Measurement` contract (`BatchStartedAtUtc`, `BatchEndedAtUtc`, `ClockSnapshotAtUtc`) | SHIPPED (pre-staged for the rehearsal run) | `EntityRegionsReadMeasurementResponse` |
| Per-entity double-read spans | NOT YET MEASURED | region dumps do not double-collect position bytes; separate item-7 question |
| One-coherent-pass semantics | DESIGNED, not executed | batch design doc §Atomicity groundwork |

## 3. The proof (staged, executed only after the earlier gates)

### Step A — measure the read windows (one live session)

Run the batch rehearsal driver (`scripts/invoke-batch-rehearsal.ps1
-EnumerateLive -LiveAcquire`, Oasis Palms) and record, per batch:

- `BatchStartedAtUtc` → `BatchEndedAtUtc` (the whole-roster read window).
- The G2 clock snapshot span (`ClockSnapshotAtUtc` vs the read window).
- Per-entity region dump round-trip time (derived from the per-entity
  `CompletedAtUtc` fields).

Output: a measured window table (e.g. "14 entities read in ~12 ms; the
position payload double-read spans ~0.2 ms"). This converts the atomicity
argument from assumption to measurement.

### Step B — torn-read analysis (offline, no session)

The 12-byte position triple can tear only in specific ways the game's
writer produces (a position update writes X then Y then Z, or the game
writes a 12-byte blob — the write ORDER is an evidence question, answered
offline from the packet/record decode, NOT from memory reads). The
analysis must enumerate:

1. What a torn read looks like at the consumer (a tank position that is a
   mixture of two consecutive updates).
2. The physical bound on the mixture (game update rate × write span vs the
   measured read window).
3. Whether the double-read discipline (byte-identical second read) already
   filters the torn case — it does IF the game holds each update steady for
   longer than the double-read span; that hold-time is the thing the
   rehearsal measurement must bound.

### Step C — the atomicity-free contract (design conclusion)

Declare the consumer contract that makes strict atomicity unnecessary:

- The overlay frame is a best-effort snapshot: positions may be at most
  one-update-old (never fabricated, never out of the measured window).
- A position whose double-reads disagree is dropped for that frame (the
  existing fail-closed path) — torn reads cannot render.
- The measured window (Step A) is asserted in the evidence ledger so a
  future consumer can rely on the bound.

## 4. Staged checklist (executed by the operator when the earlier gates close)

- [ ] Earlier gates closed: G0 publication, G1, G2, G3, L2 facing, HP
      Phase-4, yaw publication (all pre-item-7 by design).
- [ ] Step A: one approved batch-rehearsal live session (Oasis Palms),
      `Measurement` recorded + appended to the ledger as an item-7 section.
- [ ] Step B: offline torn-read analysis written to this doc (write-order
      evidence + bound).
- [ ] Step C: the atomicity-free contract declared in this doc + the batch
      design doc.
- [ ] Evidence appended to `offset-discovery-ledger.md` (new item-7 section,
      immutable).
- [ ] `scripts/validate.ps1` green; handoff written.

## 5. Relationship to the other gates

| Gate | Status | Item-7 relation |
|---|---|---|
| G1 read consistency | CLOSED | prerequisite (double-read discipline exists) |
| G2 same-decoded-clock | CLOSED | prerequisite (the batch attestation is the window bound) |
| G3 repeatability | CLOSED | prerequisite (the walk is stable across launches) |
| L2 facing / HP / yaw | CLOSED or approval-gated | must close BEFORE item 7 starts |
| **Item 7** | **DESIGNED — NOT STARTED** | **LAST by design** |
