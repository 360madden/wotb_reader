# Item 7 — hardware-atomicity proof plan

**Date:** 2026-08-12
**Status: PLAN (pre-staged).** Execution is ordered LAST by design (item 7 of
`docs/operations/resolver-path-consolidation.md`) — it runs after the
operator-approved publication applies (HP then yaw). No code changed, no
memory touched; this document pre-stages the methodology so the proof can run
immediately when ordered.

## The claim (precise, honest)

The values the readers consume are never observed torn — established by ONE of
two branches, whichever the evidence supports:

- **Branch A — per-field hardware atomicity (static):** every consumed field
  is written by the game with a single aligned atomic-sized store, so an
  aligned read of that field cannot observe a partial write (x86: aligned
  ≤ 8-byte accesses are atomic; 16-byte SSE only when aligned).
- **Branch B — read discipline (dynamic):** the snapshot/ring + double-read
  discipline detects any cross-field tear and retries, making tearing
  unobservable in the values the readers consume.

The claim is NOT "the whole ring record is atomic" — the game updates fields
independently. It is per-field atomicity (A) plus consistency (B), and the
project's existing framework already refuses to claim more than evidence
supports (`hardwareAtomicReadProven` is hardcoded false — the resolver never
claims it; the checklist's "Positive verdict requires it false" clause).

## Current evidence (verified against the code, 2026-08-12)

| Surface | Discipline today | Flag/evidence |
|---|---|---|
| Position ring (resolver) | **Double-read**: ring slot read twice + ring index read before/middle/after + `SequenceEqual` + full root-chain re-validation (`Type10EntityPositionResolver.cs` ~545–585) | `allConsistentDoubleRead = true` (computed); live poll aggregates byte-identical (OD-075/076, 24/24) |
| Batch region spans + entity-base spans | **Read once** — "the address path does not double-collect position bytes" (`GameSessionCoordinator.cs` ~2097) | `ConsistentDoubleRead: false` explicitly NOT claimable from a region dump |
| Read-pass window | Measured per batch/frame: `EntityRegionsReadMeasurement(BatchStartedAtUtc, BatchEndedAtUtc, ClockSnapshotAtUtc)` / `LiveFrameReadMeasurement(...)` | The item-7 timing budget |
| Atomicity flag | `HardwareAtomicReadProven: false` (hardcoded) | Never claimed |
| Static thread | Type-10 XYZ write at RVA `0x022FA78D`, bytes `F30F7E00` (MOVQ, 8-byte) — contiguous XYZ written at `EAX` (OD-RECOVERY-069/070/071) | Entry point for the write-size analysis |

## Branch A — static write-size proof

Hash-bound Ghidra verification that every consumed field is written by a
single aligned atomic-sized instruction:

- Position floats at ring-record `+0x10` (x/y/z) — MOVQ pairs or three
  MOVSS/MOVSD? each 4-byte store aligned → per-field atomic.
- Rotation triple at `+0x28` (roll) / `+0x2C` (pitch) / `+0x30` (yaw) —
  same question.
- Entity-base current/max HP int16 `+0xB8` / `+0x11C` and alive byte `+0xBA` —
  single MOV/movzx stores.
- The ring-slot handoff: confirm the game writes a slot THEN advances the ring
  index (the snapshot convention the resolver relies on), so the slot at the
  current index is complete.

### Branch A — HP sub-proof (2026-08-11, DONE — hash-bound, listing-confirmed)

Tooling (tracked, reproducible): `FindHealthFieldStores.java` (16-bit
encodings), `ScanHealthFieldStoreWidths.java` (width census),
`ConfirmHealthFieldStores.java` (listing boundary confirmation). Evidence in
`.build/ghidra-evidence-hp-writesite/` (local, gitignored — the hp1-15 model):
`find-health-field-stores.txt`, `scan-health-field-store-widths.txt`,
`confirm-health-field-stores.txt` — all `executable_sha256=1cda5c31…` (the
published identity). Runner: `.build/ghidra-run-findhealth.bat` (local).

Result:

- **Methodology lesson first:** an unaligned raw-byte scan is NOT evidence on
  its own — of 443 candidates, 15 are not at real instruction boundaries, and
  a first confirmation attempt with wrong address math (recorded opcode
  position + missing image-base subtraction) produced a zoo of misparses.
  The confirmation pass (`getInstructionAt` at the true sequence-start RVA)
  is mandatory.
- **428 listing-confirmed stores:** 360 dword + 13 word + 53 byte to `+0xB8`,
  2 word + 1 byte to `+0x11E`; **zero 64-bit and zero 128-bit stores** to
  either field anywhere in the binary (XMM census empty).
- **The health-set functions write BOTH fields as single 16-bit stores:**
  `FUN_0166b9f0` (0x126ba40 → `MOV word ptr [ESI+0xb8],AX`;
  0x126bafe → `MOV word ptr [ESI+0x11e],AX`) and `FUN_01675f60`
  (0x1276111 / 0x12767b3, same pair) — aligned 16-bit stores are atomic
  within a cache line on x86, so a concurrent aligned 16-bit reader (the
  resolver's `[entity+0xB8]` int16 read) cannot tear. All 13 word sites of
  the pre-existing `FindHealthFieldStores.java` are re-confirmed real.
- **The 360 dword / 53 byte stores at +0xB8 belong to other object families**
  (immediates like `0x3f800000`=1.0f, `0x40000000`=2.0f, `0xbf5cf96e`,
  `0x1388`=5000, `0x1000000` are not int16-health semantics).
- **HONEST BOUND:** the claim "vehicle HP is written only by 16-bit stores"
  is setter-derived + live-bounded, not a per-object proof of all 360 dword
  sites: residual risk that some dword/byte site targets a VEHICLE entity is
  bounded by the live byte-exact reads (OD-087/091 — every drop equals its
  damage sum exactly, 132 dumps, zero torn values). Closing that residual
  (per-function object-type attribution) remains an option, not a gate.

Verdict: Branch A's HP/`+0xB8` word-store claim is DONE (static half). Branch
A remains open for position `+0x10` (MOVQ/MOVSS family — the OD-053 ring
write-site disasm is the starting artifact) and rotation `+0x28..0x30`.

### Branch A — position + rotation sub-proof (2026-08-11, DONE — hash-bound, chain-anchored)

The ring-record writer is statically located and verified on the 40-check
semantic chain from the type-10 packet (`.build/ghidra-evidence/
type10-movement-position-trace.txt`, `verdict=semantic-chain-proven`):
type-10 handler `0xFE31C0` → engine `0x26F9710` → entity resolver `0x26FC850`
→ entity apply `0x26FA780` (XYZ anchor `0x26FA78D`, `F30F7E00` MOVQ load) →
movement-filter member `[ESI+0x38]` → apply slot `[EAX+0x8]` → avatar-filter
apply (`0x230E8F0`, table `0x3442520` slot 2) → helper member `[ECX+0x8]` →
helper apply slot `[EAX+0x8]` → **avatar-helper store slot `0x230DF40`
(`FUN_0270df40`, vtable `0x34424A4` slot 2)** — the ring writer.

Full-function disasm: `.build/ghidra-evidence-ring-writer/functions-disasm.txt`
(+ window-disasm.txt), `executable_sha256=1cda5c31…` (published identity).

The writer's store sequence (record = `helper + 0x08 + slot*0x38`, stride
`0x38` == resolver `RingRecordSize`; index at `helper+0x1C8`, masked `& 7`;
slot = `(index−1) & 7`):

| Field | RVA | Instruction | Width | Atomicity |
|---|---|---|---|---|
| x,y (record+0x10/+0x14) | 0x270DFD7 | `MOVQ [ECX],XMM0` (ECX = record+0x10) | 8-byte aligned | atomic within cache line → the float32 x/y reads cannot tear |
| z (record+0x18) | 0x270DFDE | `MOV [ECX+8],EAX` | 4-byte aligned | atomic |
| roll,pitch (record+0x28/+0x2C) | 0x270DFFC | `MOVQ [ESI+EDX*0x8+0x30],XMM0` | 8-byte aligned | atomic → roll/pitch reads cannot tear |
| yaw (record+0x30) | 0x270E005 | `MOV [ESI+EDX*0x8+0x38],EAX` | 4-byte aligned | atomic |
| time (record+0x00, double) | 0x270DFCD | `MOVSD [ESI+EDX*0x8+0x8],XMM1` | 8-byte aligned | atomic (not consumed by the resolver) |

This EXACTLY matches the resolver's consumed offsets (`RingRecordRegion.cs`:
`PositionOffset 0x10`, `YawOffset 0x30`, `PitchOffset 0x2C`) and the stride
validation (`AvatarHelperRingStride == RingRecordSize`) — an independent
static confirmation of the live-verified layout. The slot-4 readback
function (`0x230DBE1`: `MOVQ XMM0,[ESI+EDX*0x8+0x18]` = record+0x10) and slot
3 (the interpolator at `0x230E030`) are readers, consistent with the same
layout.

**Ring handoff — honest description (inverted from the plan's assumption):**
`FUN_0270df40` advances the index FIRST (`0x270DFAA` `MOV [ESI+0x1c8],EAX`)
and fills the record after, behind a monotonic-time stale guard (`0x270DF95`
MOVSD + `COMISD` + `JNC` skips when the incoming time is not newer than the
current slot's). A reader sampling the CURRENT index during the ~10-instruction
write window can observe a half-filled slot; the window is sub-microsecond and
the resolver's double-read discipline (before/middle/after index stability +
`SequenceEqual`) retries on any inconsistency — zero tears observed live
(24/24+ byte-identical across OD-075/076/082/087-091). Branch B extends the
same discipline to the batch surface.

**Honest bounds:** (1) the write-site proof is chain-anchored, not
census-based — a binary-wide displacement census at `+0x10`/`+0x28`/`+0x30`
is infeasible (universal struct offsets); (2) the HP census's 360 dword/53
byte `+0xB8` sites and this function's un-consumed `+0x08`/`+0x0C` dwords and
`+0x1C`/`+0x24` MOVQ+dword pair are other-object/other-field writes; (3) the
claim "consumed fields are written only by these instructions" is anchored to
the proven chain + the reader-side functions, bounded live by the byte-exact
reads.

**Branch A is now COMPLETE for all consumed fields** (HP word stores;
position MOVQ+MOV; rotation MOVQ+MOV; ring handoff characterized). Branch B
(read-discipline extension) and the contract flag flip remain.

Evidence format mirrors the earlier hash-bound work: instruction listing with
RVA, operand size, and alignment analysis; the exact analyzed binary hash
recorded. This is OFFLINE (the existing `tools/ghidra-scripts/` lane) and can
run before any live session.

## Branch B — read-discipline extension (dynamic)

Extend the resolver's proven discipline to the batch surface:

1. In `ReadEntityRegionsCoreAsync`, read each requested region span TWICE and
   re-read the anchor (ring index / chain head / entity address) before/middle/
   after; `UnstableSnapshot`-style retry on any mismatch (the resolver template).
   **DONE 2026-08-11 (offline implementation + tests):** the region span and
   the entity-base span are each read TWICE per attempt with a bounded retry
   (`layout.MaxAttempts`) and a fail-closed exhaustion stage
   (`region-unstable-snapshot` / `entity-base-unstable-snapshot`); the batch
   stays resolved when only an item tears. **Anchor-witness note (honest
   simplification):** the per-span `SequenceEqual` IS the stability witness —
   for ring records the record's leading time field sits inside the span, so
   any ring advance or mid-write changes the bytes and retries (the ring-index
   re-read is subsumed); entity identity is already established by the Phase-1
   resolver discipline, and any object swap changes the span bytes. Tests:
   `RegionTearRetriesAndSucceeds` (torn first read → stable re-read wins),
   `RegionAlwaysTornFailsRegionOnly` (never settles → item fails closed, batch
   resolved), plus the read-count assertions in the existing batch tests now
   document the double-read (2 reads per span).
2. Only then set `ConsistentDoubleRead: true` on the region items (today it is
   hardcoded false and "not claimable" — the flag becomes claimable only when
   the discipline lands). **NOT APPLIED** — the flag flip is the shared-contract
   proposal (below); the discipline (step 1) runs with the flag still false
   (fail-closed).
3. Bounded live sessions (approved launches, Oasis + Dead Rail, Phase-4
   standard): N read passes over the batch surface; record the read-pass
   window per pass (`EntityRegionsReadMeasurement`); acceptance = 100%
   byte-identical double-reads, zero torn reads, zero index/chain instability
   across both replays.
4. Camera pose + entity-base reads get the same double-read treatment — the
   entity-base span landed with step 1; the camera pose read remains for the
   live half.

## Contract change (shared-contract proposal — NOT applied here)

When the evidence lands, `HardwareAtomicReadProven` flips from hardcoded-false
to computed-true and `ConsistentDoubleRead` becomes claimable on region items.
This is a shared-contract change: proposed to the owner for review before any
edit (the resolver-path-consolidation deferred-decision pattern), and the
checklist's "requires it false" clause is updated in the same proposal.

## Honest-negative discipline

- If ANY torn read is observed, the claim narrows to per-field atomicity only
  (Branch A) and the discipline must handle the tear (retry) — never hide it.
- Bounded sessions with fixed pass budgets; the read surface is never widened
  on a hunch.
- The framework keeps refusing to claim atomicity until the flag comes from
  real evidence.

## Definition of done

`hardwareAtomicReadProven: true` (computed from evidence, not hardcoded) with:
Branch A write-size proof recorded (hash-bound) AND/OR Branch B 100%
byte-identical double-reads over both content-distinct replays with the
read-pass window measured; owner-approved contract change; full gate green.

## Sequencing

1. Operator-approved publication applies (HP then yaw) — pre-requisite order
   (unchanged; the publication gates are operator approval + gate run only).
2. Branch A static write-size analysis (offline) — **COMPLETE 2026-08-11**
   (analysis-only, no live surface: it ran before the publications without
   changing any gate): HP sub-proof (16-bit setters), position + rotation
   sub-proofs (MOVQ+MOV ring writer, chain-anchored), ring handoff
   characterized.
3. Branch B discipline extension + bounded live sessions — **step 1 DONE
   2026-08-11** (region + entity-base double-read, offline); steps 3-4 need
   the approved live sessions.
4. Shared-contract proposal (flag flip) + owner approval.
5. Full gate + records.
