# Handoff — 2026-08-10: runtime chain-walking foundation (G0 follow-up)

## Goal

Position is published `Verified` but **not executable**: the legacy observation
path computes `moduleBase + offset` (no chain concept) and the published
16-hop chains have never been dereferenced at runtime. This milestone builds
the offline foundation for making chains readable — without touching the
legacy path or the frozen table.

## What landed

1. **Model** (`src/WotBTreader.Core/OffsetModels.cs`): `OffsetChainHopKind`
   (`RootRva` / `MemberOffset` / `RecordOffset`), `OffsetChainHop`, and an
   additive `Chains` property on `OffsetTable` (defaults empty; existing
   construction sites unaffected).
2. **Parsing** (`src/WotBTreader.Application/Replay/OffsetTableReader.cs`):
   the `chains` section is deserialized and validated — malformed chains
   (unknown kind, bad shape, negative value) are **dropped fail-closed** so a
   defective chain can never be walked; the legacy path is unchanged.
3. **Walker** (`src/WotBTreader.Core/Discovery/OffsetChainWalker.cs`): a pure,
   hash-bound-x86 walker (`uint` addresses, delegate memory reader, mirroring
   `Type10EntityPositionResolver` conventions) that resolves
   root RVA → member-pointer dereferences → record offset, fail-closed:
   `InvalidChain` / `InvalidModuleBase` / `ReadFailed` / `NullPointer` with a
   failure stage.
4. **Tests**: 13 walker tests (happy path, direct root+record, null pointer,
   read failures, zero module base, all invalid shapes) + 3 reader tests
   (chains parsed, malformed chain dropped, absent chains empty).

## Honest scope boundary (important)

`OffsetChainWalker` is a **structural** walker — every member hop dereferences
a pointer. The published 11.19.0.10 position chain's final member hop
(`AvatarHelperCurrentIndexOffset 0x1C8`) is an **integer index read × ring
stride (0x38)**, which no current hop kind expresses. So the walker cannot yet
walk the published position chain end-to-end; `Type10EntityPositionResolver`
remains the authoritative position reader (unchanged). The walker is the
foundation for plain pointer chains (entity-record fields such as HP / the
entity-id) and for a future stride-aware ring hop (schema addition, operator
gated — the table stays frozen).

## Verification

- `scripts/validate.ps1` exit 0: all 12 test projects green (GameIntegration
  263/265, 2 opt-in skips), repo scan 830 files, PSSA baseline 86, offline
  links 112/112, `offset_check.py` → `PASS` (chains validated, 3 fields).
- Legacy contract pinned: `ChainedFields_AreExcludedFromObservationReads`
  still green (nothing in the legacy read path changed).

## Follow-up (same day): stride-aware ring hop + honest scope correction

Added `OffsetChainHopKind.RingIndex` (requires `indexOffset` Int32 field +
`stride`): the walker now tracks the last dereferenced object and selects a
ring entry via `ring = *(object + value)`, `index = *(int32)(object +
indexOffset)`, `address = ring + index*stride` — fail-closed with a new
`InvalidRingIndex` status (negative index). Validator + `schema.json` +
pack doc + format README all accept `ringIndex` (shape rule enforced:
`rootRva -> memberOffset|ringIndex* -> recordOffset`). 5 new walker tests +
1 reader test; validator proven across valid/missing-stride/bad-shape cases;
real table unchanged (still 16-hop memberOffset chains) and still passes.

**Scope correction:** re-deriving the published chains against the resolver
showed the position chains are blocked from mechanical walking by BOTH the
ring step AND the cached fast path + three alternative entity-tree map roots
(`FindEntity` branching — no hop kind expressed them). Docs stated both
blockers; the resolver remained the authoritative position reader.

## Follow-up (same day): clean object-model rework + `entityLookup` hop

Two corrections and one capability landed together (`OffsetChainWalker`):

1. **Semantic correction (resolver-faithful object model).** Re-deriving the
   walk against the resolver's actual traversal exposed that the walker's
   member model was subtly wrong in three places: the root RVA is a POINTER
   SLOT (the walker now dereferences it), the entities step is an INLINE
   member (`Connection + 0x04`, no dereference — new `inlineOffset` hop kind),
   and the ring array is INLINE in the helper (`helper + 0x08 + index·stride`,
   no ring-pointer dereference). The earlier `ringIndex` implementation
   (and its tests/docs) incorrectly dereferenced a ring pointer; the published
   resolver reads the ring record at `helper + AvatarHelperRingOffset +
   index*AvatarHelperRingStride` — the walker now matches exactly.
2. **`entityLookup` hop (the branch capability).** A single hop carrying the
   cached-entity fast path + the three ALTERNATIVE tree-map roots + the
   id-keyed binary-tree node layout (size, nil flag, key/value/child offsets,
   sentinel, node budget), mirroring the resolver's `FindEntity` exactly: try
   the cache, then each root in order, rebasing the walk to the found entity.
   The target entity id is supplied PER WALK (never carried by the chain), so
   the frozen published table is untouched. New statuses
   `EntityNotFound` / `TraversalLimitExceeded`; `valueLength` cap raised to 64
   (the 12-byte position triple). The sentinel is read directly at the tree
   root slot (`*(map + rootOffset)`), matching the resolver — an early
   implementation wrongly dereferenced the root slot first.
3. **Equivalence proofs.** New `OffsetChainWalkerEquivalenceTests` feed the
   SAME synthetic memory to `Type10EntityPositionResolver.ResolveRecordAddress`
   and to the walker with a position chain re-expressed as
   `rootRva + memberOffset* + inlineOffset + entityLookup + memberOffset* +
   ringIndex + recordOffset`, asserting identical outcomes and the exact
   record/field address: cache path, primary tree, alternative tertiary root,
   signed-key traversal (negative root key), and all-trees-empty
   (`EntityNotFound` both sides). The walker is now PROVEN to reproduce the
   resolver's traversal — future hop-semantic drift is caught immediately.

Validator + `schema.json` + pack doc + format README + workflow runbook all
accept `inlineOffset` + `entityLookup` (descriptor fields enforced; shape rule
`rootRva -> memberOffset|inlineOffset|ringIndex|entityLookup* -> recordOffset`;
entityLookup hops must have value 0 and are exempt from the note-hex
cross-check since their notes describe descriptor offsets). Reader drops
malformed entityLookup chains fail-closed (proven by a dedicated test). 32
walker/equivalence tests + 2 new reader tests green.

## Next steps (gated)

- Publish a WALKABLE position-chain form through the operator gate (the
  published 11.19.0.10 chains still spell the inline entities/ring steps and
  the ring-index read as plain `memberOffset` hops, so they remain
  documentation + evidence; the walker now walks the re-expressed form).
- Live chain dereference of the published position chain (needs operator
  approval).
- Entity-record discovery (HP / entity-id member offsets) reusing the spine +
  this walker (the `entityLookup` hop is the missing piece that unlocks every
  entity-record field).
