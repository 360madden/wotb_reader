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

## Next steps (gated)

- Live chain dereference of the published position chain (needs operator
  approval + a stride-aware hop or the resolver's own walker).
- Entity-record discovery (HP / entity-id member offsets) reusing the spine +
  this walker.
