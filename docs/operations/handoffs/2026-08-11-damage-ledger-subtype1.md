# 2026-08-11 — The real damage ledger: type-8 subtype-1 health changes

## Summary

The type-5 max-HP discovery (previous handoff) exposed a second, harder
problem: the decoder's `Damage` events were **wrong**. Decoded damage was
~2.5–7× battle_results, and the one packet that carried a real 39-HP drop
(dsub=1) was being dropped by `TryReadDirectDamage`. That blocked
`hpFraction = 1 − taken/maxHp` in the overlay even though the denominator
was solved.

**Both are now fixed.** The real HP ledger is type-8 **subtype-1** (19-byte)
packets: `victim u32 @ +0x00`, `subtype 1 @ +0x04`, `declared length 7 @
+0x08`, **post-hit HP u16 @ +0x0C**, `attacker i32 @ +0x0E`, flag byte @
+0x12. The packet carries the victim's CURRENT HP — never the amount. The
amount is the delta from the victim's previous known health, seeded by the
type-5 max-HP broadcast (first broadcast = max HP, verified last handoff).

Post-hit HP **0xFFFD is the destroy marker** and carries the killer in the
attacker field; the victim's remaining HP is credited to the killer, which
matches battle_results damage accounting exactly.

## Ground-truth chain (how it was found)

1. Type-5 broadcasts give per-tank HP at multiple clocks, so damage packets
   between two broadcasts must sum to the HP difference. One perfect window:
   medvedkovo 2549397 dropped exactly 39 HP with one damage packet.
2. Brute-forcing the subtype-8 layout found candidates (u16 BE@29 / LE@30)
   that did NOT match the 39-HP ground truth — the packet wasn't a hit at
   all.
3. The window's only damage event was the subtype-1 packet plus its type-32
   mirror. Decoding subtype-1 exhaustively showed the post-hit-HP pattern:
   for 2549399 the field dropped 432→407→380→…→13→65533 (destroy) with the
   killer in the attacker field.
4. Per-attacker sums matched battle_results almost exactly; the last
   mismatches (e.g. JOY 879 vs 905) resolved exactly when the destroy
   marker's remaining HP was credited to the killer (+26 = 13+7+6, the
   remaining HP of its 3 destroyed tanks).

## Validation (both replays re-imported, fresh decode runs)

| Check | Result |
|---|---|
| Per-attacker sums vs battle_results | **Exact for every player WITH battle results — 9/9**: savanna 1719 / 1158 / 56 / 959 / **752** (author), medvedkovo 1598 / 326 / 489 / **905** (JOY, with destroy credit) |
| Players without battle results | 10/14 medvedkovo + 8/14 savanna have NULL `battle_stats_json` (they left the battle — WoT only records results for players present at the end). Their decoded damage is still true and satisfies HP conservation |
| HP-pool conservation | Decoded totals equal the type-5-verified damage totals exactly: savanna 8964 ≤ 12140, medvedkovo 6227 ≤ 8500 (same numbers the previous handoff verified from raw packets) |
| Destroy credit | 0xFFFD marker credits remaining HP to the killer; without it JOY = 879 ≠ 905 |
| Regression tests | Synthetic subtype-1 fixture: 3 damage events (100, 50, 600-destroy-credit), per-attacker sum 700, typed `healthChange` raw records, no malformed warnings — all pass |

## Product changes

- `EventPacketDecoders`: `TryReadDirectDamage` **replaced** by
  `TryReadHealthChange` (subtype-1). `DamageObservation` removed;
  `HealthChangeObservation` carries victim/post-hit HP/attacker/isDestroy.
- `WotbReplayDecoder`: the damage ledger is built in `BuildEvents` — seed
  `healthByEntity` from the type-5 broadcasts (first per entity), then walk
  health changes in sequence order: non-destroy emits `Damage` with
  `damage = previous − postHitHealth` (heals/discrepancies update the ledger
  without emitting); destroy markers emit `Damage` with the remaining HP
  credited to the killer and reset the ledger to 0. Non-roster entities are
  skipped (no max-HP seed). Typed `healthChange` raw records preserved.
- Tests: `HealthChangeLedgerComputesDamageFromHpDeltas` +
  `CreateHealthChangePayload` synthetic fixture (clocks written after the
  position packets so stream clocks ascend — a backwards clock makes the
  stream reader resync and warn).

## State / next steps

- **Blocker cleared**: `hpFraction = 1 − taken/maxHp` is now wireable — the
  overlay's HP fraction can be exact with zero memory reads.
- Next offline milestone in preference order: wire `ReplayFrameSource` to
  the `MaxHealthObserved` + corrected `Damage` events (exact HP fraction,
  then a full balance check vs battle_results on the overlay totals), or
  continue the entity-record layout past the health block.

## Files touched

`src/WotBTreader.Replays/EventPacketDecoders.cs`,
`src/WotBTreader.Replays/WotbReplayDecoder.cs`,
`tests/WotBTreader.TestSupport/SyntheticReplayFactory.cs`,
`tests/WotBTreader.Replays.Tests/ReplayDecoderTests.cs`,
`offline/replay-format.md`, `docs/operations/product-roadmap.md`,
`docs/operations/record-diffing-groundwork.md`.
