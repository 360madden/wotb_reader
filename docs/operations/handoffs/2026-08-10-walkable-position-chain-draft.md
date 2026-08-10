# Handoff — Walkable position-chain form: draft + end-to-end proof (2026-08-10)

## Summary

The published 11.19.0.10 position chains (`OD-RECOVERY-083`) are a faithful
transcription of the resolver's traversal but are NOT mechanically walkable:
they spell the CONDITIONAL cached-entity fast path and the three ALTERNATIVE
tree roots as sequential `memberOffset` hops (no rebase semantics), the inline
entities map and ring as deref hops, and the ring-index read as a plain member
offset. The chain walker now has the hop kinds to express the same walk with
correct semantics (`inlineOffset`, `entityLookup` with a full descriptor,
INLINE `ringIndex` — commits `f401e69` → `7ef94d6`). This pass turned that
capability into a concrete, operator-ready artifact:

1. **The draft** — `docs/operations/g0-offset-table-draft.md` §7 carries the
   exact walkable JSON for `playerPositionX/Y/Z` (12 hops vs the published 16;
   Y/Z differ only in the final `recordOffset` 0x14/0x18). The operator gate
   decides whether to replace the published `chains` with it; the prior form
   stays in git history (`0e6bdba`) and the ledger as evidence.
2. **The end-to-end proof** — `tests/WotBTreader.Application.Tests/
   WalkablePositionChainTests.cs` (6 tests) deserializes the EXACT draft JSON
   through the real parse path (`OffsetTableReader`), walks the parsed chains
   on full-spine synthetic memory, and requires the record address + X/Y/Z
   floats to equal the resolver's own traversal over the SAME memory:
   - cache fast path, primary tree, alternative tertiary root, all-trees-empty
     (both readers `EntityNotFound`);
   - parse-to-constants equality (the draft JSON is pinned to the resolver's
     layout constants hop-by-hop, descriptor field-by-field);
   - malformed `entityLookup` descriptor → chain dropped fail-closed while the
     other fields' chains survive.
3. **Validator confirmed** — the walkable chain passes `offset_check.py`'s
   shape rule (0 problems); missing `stride` and a descriptor-less
   `entityLookup` are both caught (1 / 14 problems, fail-closed).
4. **Docs** — the runbook's chain paragraph now points at the draft §7 and the
   new proof.

## Follow-up (same day): closed the two-copy drift gap

The walkable JSON originally existed in two places (the draft doc §7.4 block
and the test's embedded constant) with nothing enforcing they matched — the
repo's #1 failure mode. Now there is exactly ONE canonical copy:

- **`docs/operations/g0-walkable-position-chains.draft.json`** — full table
  shape (`gameVersion: g0-walkable-position-chains.draft`, real executable
  hash, all 8 offsets 0, the three walkable chains).
- **The C# test loads the FILE** through `OffsetTableReader` (via a
  repo-root finder, mirroring `ProjectCatalog`), so the canonical file is
  pinned to the resolver's constants + walk equivalence; the embedded JSON
  constant is gone.
- **`offset_check.py` now validates the draft file** with the same chain
  rules as the published tables (shape, descriptor requirements, note-hex
  cross-check) and — in `--check-schema` mode — **compares the doc §7.4
  block to the file's `playerPositionX`**; the file is authoritative, any
  drift fails the gate. Proven both ways: a mutated file fires the note-hex
  check and the drift check.
- **Re-expression fidelity check** (same pass): the validator now also
  proves the walkable draft is the SAME walk as the published evidence
  chains (`memory-offsets/11.19.0.10.json`), offset for offset — root RVA,
  controller spine, entities map, cache fast path, ALTERNATIVE tree roots
  (order-sensitive), filter/helper, ring base/index/stride, record offset.
  Kinds may differ by design (published spells inline/lookup/ring steps as
  memberOffset hops); offsets must not. Proven: swapping the tree-root order
  fires `fidelity[playerPositionX]: tree roots differ`.

## Files

- `tests/WotBTreader.Application.Tests/WalkablePositionChainTests.cs` (new)
- `docs/operations/g0-offset-table-draft.md` (§7 added)
- `docs/operations/g0-walkable-position-chains.draft.json` (new — canonical)
- `scripts/python/offset_check.py` (draft-file validation + doc-block drift
  check)
- `docs/operations/offset-discovery-workflow.md` (chain paragraph updated)

## Gates

`scripts/validate.ps1` exit 0 — all 12 test projects green (Application.Tests
18 → 24), chains validated, pack-doc ↔ schema ↔ validator cross-check
consistent, file-tree/links/ledger clean, PSSA baseline 86. `offset_check.py
--check-schema` now also reports "Validating walkable draft" and passes.

## Not done (operator decision pending)

- The published table `memory-offsets/11.19.0.10.json` is UNTOUCHED — the
  frozen table stays frozen until the operator approves §7.
- No resolver/read-surface change; `offsets` stay 0; the target entity id
  stays a per-walk input (never in the chain).

## Next

- Operator approves §7 → apply the `chains` replacement in ONE commit (table +
  draft §7 marked applied + ledger row + handoff), re-run the §5 gates.
- After approval, the walker can read the published table's chains directly —
  the resolver's job and the walker's job become the same mechanism.
