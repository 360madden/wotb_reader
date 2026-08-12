# Handoff — G1 publication applies: playerHP + playerYaw Verified via chains (2026-08-12)

**Ledger:** OD-RECOVERY-092 · **Docs:** `docs/operations/g1-hp-publication-draft.md`,
`docs/operations/g1-yaw-publication-draft.md` (both now APPLIED), checklist row
`Publication applies (HP then yaw)` → DONE.

## What happened

The operator-gated publication applies (HP then yaw) — the last remaining step
for both G1 packages — were executed 2026-08-12. The offset table is no longer
frozen at 3 chained fields: **5 fields are now `Verified` via module-rooted
`chains`**, with `offsets` staying 0 by design (both records are battle-scoped
heap; the runtime computes `moduleBase + offset` and chained fields are read by
the resolver, not the observation path).

| Field | Chain | Final hop | Live evidence |
|---|---|---|---|
| `playerHP` | entity-base: position hops 1..8 (through `entityLookup`) + `recordOffset 184` — the health field lives on the ENTITY BASE record, NOT the ring path (9 hops) | `[entity+0xB8]` signed int16 | OD-RECOVERY-087 (Oasis, 74 dumps, Strict 8/8) + OD-RECOVERY-091 (Dead Rail, 58 dumps, Strict 4/4 lead-side) — `twoReplayRepeatability = true`; siblings max `+0x11C` / alive `+0xBA` / healing `+0x11E` |
| `playerYaw` | ring-record: IDENTICAL position walk (position `+0x10` and yaw `+0x30` proven on the SAME record) + `recordOffset 48` (12 hops) | `+0x30` float32 hull yaw | OD-RECOVERY-088 (Oasis, 48/48) + OD-RECOVERY-089 (Dead Rail, 56/56 per-dump bidirectional lag) — `twoReplayRepeatability = true`; rotation triple roll `+0x28` / pitch `+0x2C` / yaw `+0x30` |

Both canonical chains were published **verbatim from
`docs/operations/g0-walkable-position-chains.draft.json`** (pre-staged
2026-08-11) — the fidelity check now enforces draft ↔ published identity for
all 5 fields (it goes active the moment the published table gains a chain).

## What changed

- `memory-offsets/11.19.0.10.json` — `fieldValidation.playerHP`/`playerYaw` →
  `Verified` (evidence appended: OD-RECOVERY-087/091 HP, 8 launches / 2
  replays; OD-RECOVERY-088/089 yaw, 3 launches / 2 replays; approvals set,
  `harnessInvariantsPassed: true`), new `chains.playerHP` + `chains.playerYaw`
  (draft-identical), `offsets` stay 0, notes gain the G1 summary (the G0-era
  "Not promoted" list is amended to "at G0" + a "Still NOT promoted" tail:
  velocity, replayTime, cameraPitch, aliveTankCount).
- `offline/memory-offsets.md` — new "HP and hull-yaw chains (G1, 2026-08-12)"
  section mirroring the position-chains documentation.
- `docs/operations/g1-hp-publication-draft.md` / `g1-yaw-publication-draft.md`
  — STATUS READY → APPLIED (what-was-done preserved in §3).
- `docs/operations/offset-discovery-ledger.md` — `OD-RECOVERY-092` row,
  current-status block (2026-08-12), runtime-fields row (5 fields), next
  planned session row.
- `docs/operations/offset-promotion-checklist.md` — package rows → PUBLISHED,
  applies row → DONE, last-verified line.
- `AGENTS.md` — G0 NOT-promoted list amended; "both packages READY" superseded
  by the 2026-08-12 G1 applies; item 7 still LAST.

## Verified

- `offset_check.py --check-schema` — **PASS**: "chains validated (5 field(s))"
  on both the published table and the walkable draft, "fidelity: walkable
  draft matches the published chains (5 field(s))" (the drafts' predicted
  spot-check).
- `offline_check.py --refresh` — 59 files / 118 links, 0 broken, blocker +
  ledger consistency OK.
- `ChainedFields_AreExcludedFromObservationReads` — passes (chained HP/yaw
  never read as `moduleBase + 0`).
- Full `scripts/validate.ps1` — **VALIDATE_EXIT=0, 1045 passed / 3 local
  opt-in skips / 0 failed** (docs/JSON-only change; count unchanged).

## What remains

- **L3 damage-dealt** — needs a NEW object family (avatar/player-stats); plan
  pre-staged `docs/operations/l3-damage-dealt-avatar-family-plan.md`.
- **Item 7 (hardware atomicity)** — stays LAST; Branch A static write-size
  proofs COMPLETE for every consumed field; Branch B step 1 (double-read
  discipline) shipped; steps 3–4 (live) need approved launches; the
  `ConsistentDoubleRead` flag flip + per-entity span measurement fields are
  the owner-gated shared-contract proposal.
- Velocity, replayTime, cameraPitch, aliveTankCount — not promoted.
