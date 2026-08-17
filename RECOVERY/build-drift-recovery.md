# Build-drift recovery playbook

**Scope:** what to do when the installed `wotblitz.exe` no longer matches the
hash recorded in `memory-offsets/<version>.json`. Versioned tables are the
per-build evidence; this playbook turns a drift into an ordered, evidence-first
re-verification instead of a confused wall of `decode_build_mismatch`.

**The one rule:** evidence-first. Never copy an offset, chain, or anchor RVA
from one build to another. Old tables stay frozen as history
(`memory-offsets/11.18.0.7.json` is the precedent). Every field that must
change is re-published under a new `memory-offsets/<new-version>.json`; every
field that re-verifies unchanged gets a dated re-verification record, not a
silent carry-over.

## Step 0 — triage (the only automatic step)

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File RECOVERY\invoke-build-drift-triage.ps1
```

Reads the installed exe hash and every `memory-offsets/*.json`; writes a
report to `.build/reports/`. Exit 0 = same build (nothing to do). Exit 1 =
drift. The report lists, per table: game version, recorded hash, match
status, published fields, and the anchor hops (kind `rootRva` /
`vftableScan`, i.e. the module-relative RVAs the chains start from).

If the exe is not found (exit 2), pass `-GameExePath`.

## Step 1 — freeze and ratchet

Record the drift before any re-verification:

1. Append a dated entry to `docs/operations/blocker-log.md` (new BLK number;
   the union must stay contiguous — `scripts/python/offline_check.py`
   enforces it) or a dated amendment under the campaign record. State the new
   build identity, the recorded identity, and that the live lane is frozen.
2. Mark the affected rows in
   `docs/operations/offset-promotion-checklist.md` as `Stale (build <new>)`
   so nobody treats the old `Verified` rows as current for the new build.
3. Append a handoff + ledger register update ("next planned session" row).
   The coordinator and launcher already fail closed — no code change is
   needed to stop live work.

## Step 2 — replay-format drift check (cheap, offline, do first)

The decoder is bound to the replay container format, not to memory RVAs, so
a memory build change does not automatically break decoding — but verify
it independently:

- Run the golden-vector crosscheck (`docs/operations/replay-crosscheck.md`:
  `scripts/invoke-replay-crosscheck.ps1 -GoldenVector`).
- Re-decode one existing replay and confirm the packet inventory and the
  type scan still hold (`offline/replay-format.md`).

If the format changed, replay work (decoder, `Replays` tests, HUD replay
mode) is blocked too, and the format difference is a separate
re-verification ladder.

## Step 3 — per-field re-verification (dependency order)

The published surface is small and its anchors are known. Re-derive each
consumed field against the NEW executable using the same hash-bound tools
that produced the original evidence; never trust a heuristic carry-over.

| Surface | Anchor kind | Static re-derivation | Live re-validation |
|---|---|---|---|
| `playerPositionX/Y/Z` | `rootRva` chain (GameCore root `0x04095C88` in the 11.19 table) | Re-run the ring-writer / transform-object Ghidra tools that pinned the chain (ledger OD-RECOVERY-083/084 records which); `VerifyTransformRecord`-style layout confirm | Position ring live poll (24/24 contract) + 2-replay repeat |
| `playerYaw` / `playerPitch` / `playerRoll` | `rootRva` chain + `recordOffset` 48/44/40 | Re-run the ring-writer census + rotation-triple static; `yaw-diff --field pitch\|roll` re-verdict needs new dumps | Facing live session on 2 replays (OD-088/089 contract) |
| `playerHP` | `rootRva` chain + `recordOffset` 184 | Re-run `ConfirmHealthFieldStores.java` / health-write census | HP live session on 2 replays (OD-087/091 contract, `hp-diff`) |
| `damageDealt` | `vftableScan` hop (`0x032752a4` entity-Avatar vftable) | Re-derive the avatar vftable RVA via `FindVftableViaCol.java` (RTTI COL chain) | Avatar-stats live increment session (OD-095/096 contract) |
| Camera pose / W2S | avatar/BattleResources/camera vftable set (see `docs/operations/offset-discovery-ledger.md`) | Re-run `FindVftableForType.java` / RTTI hierarchy tools | CAM-013-style pose live check |
| Pen ownership walk + census (`OwnershipCandidate`) | `0x32dacf4` / `0x32eeb40` / `0x324dae8` vftables (in `GameSessionCoordinator.cs` + `pen-ownership-walk-proof-protocol.md`) | Re-run the same `FindVftableViaCol.java` derivations | One exact-build managed launch + `pen-capture` census (43/1/1 repeatability) |
| Gun/Shell descriptor fields (research, un-promoted) | RVAs in `pen-ownership-walk-proof-protocol.md` (2026-08-17 layout) | Re-run `DumpDescriptorVtables.java` / `TraceShellGunProducers.java` | Only ever via the controlled shell-swap experiment |

The general form for every field:

1. Re-measure the executable identity (`tools/compute-exe-hash.ps1`).
2. Re-run the original hash-bound static script(s) against the new build and
   diff the derived constants (do NOT reuse `.build/` evidence from the old
   hash — rerun it).
3. If the static anchors are unchanged and the layout confirmations still
   pass, the offline half is done. If anything changed, the chain hops move;
   treat changed hops as new evidence (new offsets are not publishable until
   the live half repeats).
4. Live half: one owner-approved launch per session, two content-distinct
   replays, the same automated contract (score/flatness/strict), recorded in
   the ledger with the new build hash. This is the slow part — budget it,
   don't parallelize it.

## Step 4 — re-publish

Only after every consumed field re-verified on both replays:

1. Write `memory-offsets/<new-version>.json` (copy `schema.json` structure;
   current table verbatim as the starting point, with re-derived values).
   Do NOT modify the old version file — freeze it.
2. Re-run the publication gates: `scripts/python/offset_check.py`,
   `python scripts/python/offline_check.py --refresh` (regenerate
   `offline/file-tree.md` after new files), then `scripts/validate.ps1`.
3. Update indexes: `docs/operations/next-10-actions.md`, the ledger
   decision register (`Runtime-supported fields` + `Executable identity`
   rows), `knowledge.md`'s offset-evidence bullet, and this playbook's table
   if the anchor inventory gained fields.
4. Append the handoff + ledger record; re-run the triage script as the
   closing evidence (exit 0).

## Stop conditions

- Any field that cannot re-derive statically AND pass the live contract
  after its budget → record as honest unknown for the new build; never
  carry it over. Promotion stays gated on the same criteria as before
  (`offset-promotion-checklist.md`).
- If the new build's replay format differs (Step 2 failed), all replay-mode
  work is blocked first; do not start live re-verification until the format
  ladder is green.
- If the memory layout diverges so far that the anchors themselves are ambiguous
  for any field, stop at that field and escalate with the evidence (the
  same stop discipline as the discovery campaign).

## Out of scope

- Automatic migration, RVA-delta guesses, diff-and-copy of table values.
- Bulk re-discovery of unproven surfaces (pen armor, shell/ray, spotting).
- Anything that touches the game install (read-only always).