# G0 — Offset-table publication review (pre-staged 2026-08-09)

> **COMPLETED 2026-08-09 (OD-RECOVERY-082) with verdict PROMOTE-READY
> (conditional); the publication was applied 2026-08-10 (OD-RECOVERY-083,
> commit `0e6bdba`)** — `playerPositionX/Y/Z` are now `Verified` via the
> `chains` section. The procedure below remains the canonical review for
> future fields.

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
  distinct replays (medvedkovo + savanna) with fresh processes.
- [ ] **G1 — hardware-atomic read proof** (CLOSED 2026-08-09,
  OD-RECOVERY-082): corrected acceptance — poll aggregate
  `stable-resolver-positive` 24/24 with `allConsistentDoubleRead=true` (the
  per-read byte-identical branch; the guard-page interceptor arm was
  abandoned in OD-RECOVERY-080 because it fails the poll's own reads).
  `g1-evidence.json` + `od073-poll.json` attached to OD-RECOVERY-082.
- [ ] **G2 — same-decoded-clock alignment** (CLOSED live, 4 confirmations
  OD-078/079/081/082): `sameDecodedClockProven=true` in the poll aggregate;
  the correlation bounds (anchor 1 s + gate cadence 1 s) recorded in
  OD-RECOVERY-078.

## Execution record (2026-08-09, OD-RECOVERY-082 — verdict PROMOTE-READY, conditional)

All gate prerequisites closed; every verification step below PASSED:

- Executable identity: `tools/compute-exe-hash.ps1` re-measured the
  installed `C:\Games\World_of_Tanks_Blitz\wotblitz.exe` (v11.19.0.10) at
  2026-08-09 ~22:17 local = `1cda5c31919c9784a41bee7f3270ec1b4536b124c51e8b36f2221b381760307d` —
  exact match with the table and `Type10EntityPositionLayout.WotBlitz1119010`.
- RVA chain: every hop re-verified against the resolver layout (root
  `0x04095c88` → app `0x0c` → session `0x124`/`0x118` → account `0x128` →
  playback `0x120` → connection `0x04`/`0x48` → entity tree
  `[0x1c,0x40,0x34]` → filter `0x38` → helper `0x08` (vtable-matched) → ring
  `0x08`/`0x1c8`/stride `0x38` → position `+0x10`). Ring invariants hold in
  the live evidence (eight-entry ring, 56-byte double-read).
- Field identity: playerPositionX/Y/Z (float32 triple at record `+0x10`)
  are the promotion candidates; velocity `+0x28` NOT promoted (the poll
  reads position only); playerYaw stays Stale/Quarantined (untouched).
- Repeatability: 2 launches (medvedkovo + savanna), 2 content-distinct
  replays, harness invariants (24/24, all module-rooted, all
  identity-revalidated, all consistent-double-read), provenance kinds
  StaticAnalysis (hash-bound Ghidra) + GameHarness (loopback discover) +
  live od073 aggregate.
- Read-only gates PASS: `tools/report-offset-evidence.ps1 -GameVersion
  11.19.0.10` and `python scripts/python/offset_check.py --check-schema`.
- Pending: `leadApproved` + `decoderAuditorApproved` (human sign-offs at
  promotion time). The schema-representation decision is RESOLVED (additive
  `chains` section; offsets stay 0) — drafted in
  `docs/operations/g0-offset-table-draft.md`, applied only on operator
  approval. A comma-binding audit (2026-08-09) confirmed the wrapper's
  `-PriorResultPaths` normalization covers the only `-File`-bound array
  parameter; the poll's param help now documents direct-invocation usage.

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

- [ ] `independentProcessLaunches` = 2 (medvedkovo process + savanna
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
2. **Schema decision item — RESOLVED 2026-08-09 (grill + draft):** the
   position is a pointer chain, not a single RVA, and the runtime
   observation path computes `moduleBase + field.Offset`, so a non-zero
   `offsets` value would corrupt reads (and the ring record is battle-scoped
   heap — never publishable). Decision: `offsets.playerPositionX/Y/Z` stay 0;
   `fieldValidation` → `Verified` with evidence; the chain is expressed in a
   new additive `chains` section (schema.json extended; `schemaVersion`
   stays 1). The exact operator-ready change is drafted in
   `docs/operations/g0-offset-table-draft.md`; apply it only on operator
   approval.
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
