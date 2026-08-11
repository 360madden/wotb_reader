# Handoff — 2026-08-10: battle scoreboard on the W2S overlay

**Status:** done, committed, pushed. Gate green.

## What and why

The overlay showed HP bars and the kill feed, but no running battle totals.
A compact scoreboard — every tank's cumulative damage dealt and kills at the
current frame time — completes the data-rich replay HUD and is a natural
consumer of the damage events the decode already produces.

## The change

- Core: `OverlayTankState` gained `DamageDealt` and `Kills` (defaulted
  trailing params, so all existing construction sites stay valid).
- Frame source: one extra pass over events accumulates damage by **attacker**
  (time-filtered, same `TryParseAttacker` used by the kill feed); kills-per-
  tank are counted from the same `BuildKills` log (moved before the tank
  loop so the frame and the scoreboard share one attribution).
- Seam: `ProjectedTank` → `OverlayTankResponse` (`damageDealt`, `kills`) →
  CLI; view model builds `ScoreboardItems` sorted by damage dealt (then
  kills, then entity id for a stable order); `W2sHudView.BuildScoreboard`
  draws the top-right panel (team-colored rows, greyed when destroyed,
  capped at 14 rows).

## Verification

- Frame-source test: damage dealt is cumulative at the frame time (0 → 60 →
  200 across t=1/3/6), kills land with the destroy, kill-feed attribution
  matches. Projector passthrough test, endpoint field assertions, and a
  view-model test proving damage-desc sort + name resolution + dead-listed
  rows. Application 60/60, Overlay 93/93, Web 138/138; full suite 12
  projects green, 0 warnings.
- Real data (Oasis Palms t=250): 14 rows, moldeytoezzz leads with 5201 dmg,
  **kills sum (8) == kill-feed size**, dead tanks listed with final totals.
- The consistency checker now guards the invariants on every frame of both
  replays: damage dealt non-negative int, kills non-negative, and
  `sum(kills) == kills-feed size` — both replays PASS.

## Damage taken + nameplate totals (2026-08-10, appended)

- `OverlayTankState` gained `DamageTaken` (the value `BuildFrame` already
  computed for `hpFraction` — now extracted once and reused) and it threads
  the whole seam to a new scoreboard column. The nameplates additionally
  render a compact totals line under the HP bar — "1,200 dmg · 2 kills"
  (`NameplateTotalsLabel` pure helper) — so the carry follows each tank
  in-world.
- Verified on Oasis Palms t=250: **total damage dealt (22094) == total
  damage taken (22094)** — every damage event balanced, a strong
  end-to-end consistency signal. The consistency checker now also requires
  `damageTaken` non-negative on both replays; both PASS.

## Notes for next

- Damage-dealt excludes unattributed (environmental) hits — the same
  fail-closed rule as kill attribution. Exact max HP is not in the decoded
  data, so the scoreboard shows totals, not remaining HP per row.
- The one-pass damage-dealt accumulation keeps the frame build at the same
  complexity as the existing HP arc pass (measured warm frames unchanged at
  ~10 ms).
