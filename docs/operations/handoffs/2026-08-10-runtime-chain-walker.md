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
ring step (now expressible as `ringIndex`) AND the cached fast path + three
alternative entity-tree map roots (`FindEntity` branching — no hop kind
expresses it). Docs now state both blockers; the resolver remains the
authoritative position reader.

## Next steps (gated)

- A branch/alternative hop kind (or a resolver-driven table walk) so the
  published position chain becomes mechanically walkable — schema addition,
  operator gated.
- Live chain dereference of the published position chain (needs operator
  approval).
- Entity-record discovery (HP / entity-id member offsets) reusing the spine +
  this walker.
