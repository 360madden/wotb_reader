# 2026-08-11 — Exact overlay HP: wired max-health + damage ledger, destroy-marker completeness

## Summary

The previous two handoffs solved the overlay HP math piece by piece: max HP
from the type-5 spawn broadcast (denominator) and the real damage ledger from
type-8 subtype-1 health changes (numerator). This handoff **wires both into
the overlay** and closes two consistency gaps found on the way.

## Product changes

### 1. Exact HP fraction in `ReplayFrameSource`

`BuildFrame` now reads `CanonicalEventKind.MaxHealthObserved` per entity
(`{"maxHealth": n}`) and computes:

```
hpFraction = clamp(1 − damageTaken / maxHealth, 0, 1)
```

with a fail-closed fallback to the old observed-damage arc when max health is
unknown (0). Because the subtype-1 ledger emits a damage event per positive HP
delta (heals update the ledger without emitting, destroy markers credit the
remaining HP), the sum of damage events at any frame time equals
`max − current` — so the fraction is the true current HP fraction, and a
destroyed tank lands exactly at 0.

`OverlayTankState`, `ProjectedTank`, `OverlayTankResponse` (API contract),
and the CLI `overlay-frame` output now also carry **`MaxHealth` /
`CurrentHealth`**. The W2S HUD nameplate renders a `"438 / 700"` readout under
the HP bar when max health is known.

### 2. Destroy markers from the health-change ledger

While verifying the overlay frame on Dead Rail, `tr00per_2015` (2549397)
showed `0/540` HP but `alive=True`: it died at t=183.8s per its HP ledger but
carried **no position destroy marker**. The subtype-1 `0xFFFD` marker is a
first-class death signal with the killer attributed, so `BuildEvents` now
emits `Destroyed` from it too — sharing one dedupe set with the position
markers, first-by-sequence wins.

**Caught 3 tanks the position markers missed**: Dead Rail 2549397 (@183.8s)
and 2549402 (@271.5s), Oasis 3760576 (@245.1s). Alive flags now match the HP
ledger exactly on both replays (0 mismatches).

## Validation (fresh imports of both replays)

| Check | Result |
|---|---|
| HP conservation | **28/28 tanks** on both replays: `current = max − taken` is never negative (destroyed tanks land at exactly 0, survivors keep positive HP — author 700/700, JOY 361/620, Oasis victim 3760578 367/1550) |
| Ledger balance | total dealt == total taken exactly: Dead Rail 6227/6227, Oasis 8964/8964 |
| Alive alignment | every ledger-dead tank has a Destroyed event and vice versa — 0 mismatches (9 destroyed Dead Rail + 9 destroyed Oasis) |
| Overlay frame | CLI `overlay-frame` carries exact `maxHealth`/`currentHealth`/`hpFraction`; `tr00per_2015` now `alive=False` |
| Tests | New: exact-fraction test (survivor 600/700 at end, dead 0/500 with destroy credit) + API contract test (`OverlayFrame_ExactHealthRidesThrough`); strengthened decoder test asserts the 0xFFFD Destroyed event. Full suite: 12 projects, 0 failures |

## Durable verification

`scripts/python/verify-hp-ledger.py` makes the validation re-runnable and
read-only against the treader SQLite store, per session: HP conservation
(current = max − taken never negative), ledger balance (dealt == taken),
alive alignment (ledger-dead iff a Destroyed event exists), and the
battle_results cross-check for players with stats. Verified: exit 0 on
both fresh sessions, exit 1 on the pre-fix Dead Rail session (21 errors:
negative HP, imbalance, battle_results mismatches) — the check is
discriminating. The synthetic form of the same invariants is asserted in
`HealthChangeLedgerComputesDamageFromHpDeltas` (victim taken ≤ max,
attacker totals == victim totals).

## Files touched

`src/WotBTreader.Core/Overlay/OverlayFrameModels.cs`,
`src/WotBTreader.Application/Replay/ReplayFrameSource.cs`,
`src/WotBTreader.Application/Replay/OverlayFrameProjection.cs`,
`src/WotBTreader.ApiContracts/ReadContracts.cs`,
`src/WotBTreader.Host.Web/Endpoints/ReadApiEndpoints.cs`,
`src/WotBTreader.Host.Cli/Cli/CliCommandRouter.cs`,
`src/WotBTreader.Overlay/ViewModels/NameplateItem.cs`,
`src/WotBTreader.Overlay/ViewModels/MainViewModel.cs`,
`src/WotBTreader.Overlay/Views/W2sHudView.xaml.cs`,
`src/WotBTreader.Replays/WotbReplayDecoder.cs`,
`tests/WotBTreader.Application.Tests/ReplayFrameSourceTests.cs`,
`tests/WotBTreader.Replays.Tests/ReplayDecoderTests.cs`,
`tests/WotBTreader.Host.Web.Tests/ReadApiEndpointsTests.cs`,
`offline/replay-format.md`, `docs/operations/product-roadmap.md`.

## Next steps

- The overlay's HP bar, nameplate readout, and scoreboard are now fully
  derived from the replay — no memory reads. A natural next check: run the
  WPF HUD against a real session and eyeball the nameplates.
- The scoreboard's `DamageDealt` uses the same corrected ledger, so the
  on-screen totals are now the true battle numbers (Oasis 8964, not the old
  inflated 22094).
- Offline discovery continues: entity-record layout past the health block
  (max-HP siblings), or the camera/view-projection work for the W2S overlay.
