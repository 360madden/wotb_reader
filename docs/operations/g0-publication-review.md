# G0 — Offset-table publication review (pre-staged 2026-08-09)

Run this review the moment G1 + G2 close (G3 is already closed). It decides
whether any numeric value in the position-ring chain may move from
`Candidate` to `Verified` in `memory-offsets/11.19.0.10.json`. Until this
review passes, the table stays frozen and nothing is promoted. The review
itself does not edit the table — it produces the verdict; a separate,
operator-approved change applies the publication.

Source requirements (do not re-derive): `offset-discovery-workflow.md`
Phase 5 (promotion prerequisites), `offset-promotion-checklist.md` (G0 row),
`memory-offsets/11.19.0.10.json` (schema), and the ledger rows
OD-RECOVERY-075/076/077/078.

## Gate prerequisites (ALL must be closed, with evidence attached)

- [ ] **G3 — stable-root live repeatability** (closed 2026-08-09): the
  `-PriorResultPaths` wiring is done; the flag flips on a positive poll that
  supplies the prior positive aggregate(s). Verify the ledger rows attest two
  distinct replays (Dead Rail + Oasis Palms) with fresh processes.
- [ ] **G1 — hardware-atomic read proof** (needs the live session): `clean`
  verdict, or `observed` with 24/24 `allConsistentDoubleRead` byte-identical
  double-reads; interceptor report + `g1-evidence.json` attached to
  OD-RECOVERY-078.
- [ ] **G2 — same-decoded-clock alignment** (needs the live session):
  `sameDecodedClockProven=true` in the poll aggregate; the correlation
  bounds (worst-case tick error across the poll) recorded in OD-RECOVERY-078.

## 1. Executable identity (fail-closed if ANY check fails)

- [ ] The running build's SHA-256 equals the table's recorded
  `executableSha256` (`1cda5c31...`) AND the resolver's exact-build layout
  (`Type10EntityPositionLayout.WotBlitz1119010`) — the coordinator already
  refuses reads on mismatch; the review re-measures, it does not trust.
- [ ] The hash-bound Ghidra decode is the same binary the ring layout was
  derived from (the verifier checksum applies to this exact executable).
- [ ] Record the re-measurement timestamp + the measured hash in the ledger
  row (evidence, not assertion).

## 2. RVA chain re-measurement (every hop, module-relative)

Re-verify each hop against the Ghidra decode of the exact binary. All values
are module-relative RVAs + member displacements — never absolute process
addresses (ASLR).

| Hop | Value to re-verify | Source |
|---|---|---|
| Module root → GameCore | `GameCoreRootRva = 0x04095C88` | resolver layout / Ghidra |
| GameCore → app controller | `GameCoreAppControllerOffset` | resolver layout |
| App → session → account → playback controllers | vtable RVAs | resolver layout |
| Playback → BWServerConnection | `PlaybackControllerConnectionOffset` | resolver layout |
| Connection → BWEntities | map object offsets | resolver layout |
| BWEntities → entity | tree traversal (same three maps as the game) | resolver layout |
| Entity → movement filter | `EntityMovementFilterOffset` | resolver layout |
| Filter → avatar helper (matched subtype) | `AvatarFilterHelperOffset` + `MovementFilterVtableRvas` / `AvatarHelperVtableRvas` | resolver layout |
| Helper → ring | `AvatarHelperRingOffset` (ring at helper `+0x08`) + `AvatarHelperCurrentIndexOffset` | resolver layout |
| Ring record → position | `PositionRecordOffset` = `+0x10` (float32 x3) | resolver layout |
| Ring record → velocity | `+0x28` | resolver layout |

- [ ] Every hop's RVA/displacement matches the resolver layout exactly.
- [ ] The ring layout invariants hold in the live evidence: eight-entry ring
  at helper `+0x08`, position at record `+0x10`, velocity at record `+0x28`
  (OD-075's position-ring correction stands).
- [ ] No absolute address or heap-dynamic address is promoted anywhere (the
  ring record itself is battle-scoped heap memory — only the static chain is
  published; the resolver reads the record through the verified chain).

## 3. Field identity and types

- [ ] `playerPositionX/Y/Z` — float32 triple at record `+0x10`, module-rooted
  and identity-revalidated in every live poll (the fields promotion would
  cover).
- [ ] Velocity record `+0x28` — only if the review scope includes it; the
  current resolver reads the position; do not promote what a poll did not
  read.
- [ ] `playerYaw` stays **Quarantined / Ambiguous** — the decimal/hex/raw/
  address-kind reconciliation is still open (table fieldValidation shows
  `Stale`); the G0 review explicitly does NOT touch it.
- [ ] Field names/types in the table agree with the decoder's output types
  (float32, not the old double `replayTime` model).

## 4. Repeatability across launches and replays (schema fields)

For each field promoted, the table's `fieldValidation` requires:

- [ ] `independentProcessLaunches` = 2 (Dead Rail process + Oasis Palms
  process — fresh processes, ledger-attested).
- [ ] `independentReplays` = 2 (the two content-distinct 11.19.0 replays).
- [ ] `harnessInvariantsPassed` = true (the poll's invariants: 24/24
  resolved, all module-rooted, all identity-revalidated, all
  consistent-double-read in BOTH sessions).
- [ ] `evidence[]` entries carry the provenance kinds: StaticAnalysis
  (hash-bound Ghidra), GameHarness (loopback discover), and the live
  aggregate evidence — matching the schema's expectation.

## 5. Approvals

- [ ] `leadApproved` = true.
- [ ] `decoderAuditorApproved` = true (the replay decoder auditor signs the
  clock/trajectory side of the G2 correlation).

## 6. Publication mechanics (operator-approved change, AFTER the verdict)

1. Update `memory-offsets/11.19.0.10.json`: set the promoted fields' offset
   values (module-relative chain form) + `fieldValidation.<field>.status =
   "Verified"` + the evidence/approval fields above.
2. **Schema decision item:** the current table stores a single integer per
   field — a pointer-CHAIN field (position at the end of the root→…→ring
   chain) is not a single RVA. Decide and record how the chain is expressed
   (e.g., the resolver layout values in the evidence + the root RVA in the
   offset field, or a schema extension) BEFORE editing; do not overload a
   value that would mislead a reader into treating it as an absolute RVA.
3. Run the read-only gates: `tools/report-offset-evidence.ps1
   -GameVersion 11.19.0.10` and `python scripts/python/offset_check.py
   --check-schema`.
4. Regenerate `offline/file-tree.md`; run `scripts/validate.ps1` (the
   coordinator validates build identity against the table — confirm no
   regression).
5. Commit the table + ledger row + handoff as one change; the ledger row
   records `numericOffsetPublication: true`.

## 7. Frozen surfaces (re-affirmed)

- [ ] No resolver read-surface change, no scan-breadth increase, no artifact
  binding change in the same change.
- [ ] The published values are the static module-relative chain — runtime
  addresses, raw bytes, and heap pointers appear nowhere in the table or
  ledger.

## Output

The review produces one line for the ledger: the verdict (promote /
do-not-promote), which fields, the re-measured hash + timestamp, and the
schema-representation decision. No table edit without a separate
operator-approved commit.
