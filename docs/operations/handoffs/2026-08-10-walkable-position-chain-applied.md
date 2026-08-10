# Handoff — Walkable position-chain form APPLIED to the published table (2026-08-10)

## Summary

The operator gate (draft §7.5) ran and the published `chains` for
`playerPositionX/Y/Z` in `memory-offsets/11.19.0.10.json` were REPLACED with
the 2nd-generation walkable form (ledger `OD-RECOVERY-084`). The chain walker
now reads the published table directly, proven resolver-equivalent. The
position chain is fully closed: evidence (`OD-RECOVERY-083`) → canonical
draft → published table → walker, with no silent-drift path between any two.

## What changed (ONE commit)

1. **`memory-offsets/11.19.0.10.json`** — `chains.playerPositionX/Y/Z`
   replaced with the canonical walkable form (12 hops vs the prior 16):
   `rootRva → memberOffset×5 → inlineOffset (entities map, no deref) →
   entityLookup (cachedEntityOffset 0x48, treeRootOffsets [0x1C, 0x40, 0x34],
   node 0x18 layout; target entity id supplied per walk) → memberOffset×2 →
   INLINE ringIndex (0x08 + index 0x1C8 × stride 0x38) → recordOffset
   0x10/0x14/0x18`. `offsets` stay all 0; executable identity, fieldValidation,
   confidence (high), and schemaVersion (1) untouched. Notes appended.
2. **`tests/.../WalkablePositionChainTests.cs`** — 5 new
   `Walk_PublishedTableChains_*` tests load the REAL published table through
   `OffsetTableReader` (true hash) and walk its chains, asserting resolver
   equivalence on cache, primary tree, alternative tertiary root,
   not-found, plus Y/Z landing on the exact +0x14/+0x18 field addresses.
3. **`scripts/python/offset_check.py`** — fidelity check upgraded to TWO
   branches: post-publication the published chains must be IDENTICAL to the
   canonical draft (semantic signature per hop — proven to fire on mutation);
   the old 16-hop re-expression branch is preserved (verified against git
   commit `0e6bdba`).
4. **Docs** — `g0-offset-table-draft.md` §7 moved to APPLIED (§7.4 JSON block
   byte-unchanged — the gate still compares it to the canonical file);
   `docs/operations/g0-walkable-position-chains.draft.json` notes → APPLIED;
   `offline/memory-offsets.md` paragraph updated (published chains ARE walkable
   since OD-RECOVERY-084); ledger `OD-RECOVERY-084` (summary row, detail
   section, last-updated, next-planned).

## Gates (all green, exit 0)

- `offset_check.py --check-schema` — PASS, "chains validated (3 field(s))",
  "fidelity: walkable draft matches the published position chains (3 field(s))".
- `report-offset-evidence.ps1 -GameVersion 11.19.0.10` — clean (position
  fields Verified/0 via fieldValidation).
- `offline_check.py --refresh` — 112/112 links, file-tree current, blocker
  numbering BLK-0001..0026 contiguous, ledger 65 sections / 79 rows consistent.
- `ChainedFields_AreExcludedFromObservationReads` — Passed (chained fields
  still never read as `moduleBase + offset`; position stays null on the legacy
  path).
- `validate.ps1` — exit 0; Application.Tests 24 → 29 (5 new published-table
  proofs), all 12 test projects green.

## State

- **Resolver** remains the authoritative position reader; read surface,
  interceptor, coordinator untouched. No live session this record.
- **Legacy observation path** still emits position nulls for chained fields
  (pinned by the exclusion test).
- **Prior form preserved** as evidence: git commit `0e6bdba` + ledger
  `OD-RECOVERY-083`.

## Next (candidate directions, none started)

- Heading-from-world-matrix promotion reuses this exact chain shape
  (layout already known: 4×4 world matrix at `[entity+0x3C]+0x60`).
- HP discovery: the memory-side diffing harness matched to
  `IHpGroundTruthProvider` damage/destroyed events (query side done, commit
  `728c7a6`).
- A walker consumer milestone: wire `OffsetChainWalker` into a read path
  behind the resolver-equivalence guarantee (currently the walker is a
  proven library capability; the runtime still uses the resolver).
