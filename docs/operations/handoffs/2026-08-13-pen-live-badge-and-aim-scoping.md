# Pen-chance — live badge + aim scoping (2026-08-13)

**Phase 6** (`docs/operations/pen-chance-design.md`). Offline, no launch.
Continues `2026-08-13-pen-shell-selector-scoring-harness-and-hud-polish.md`.

## What shipped since the last pen handoff

- `887ee83` — **live pen badge**: the badge was replay-only. `GetLiveFrameAsync`
  now resolves `IOverlayPenetrationData` for the decoded roster (inside the
  session-id block) and forwards the context + own entity id + `?shell=` into
  `LiveFrameProjector`, which scores the CAM-013 chase-camera aim (the chase
  camera aims at the turret-level aim point, so no T1 discovery is needed to
  render). Fail-closed to null without the data or an aimed tank.
  `PenetrationContext.SelectShell` centralizes the stock/selected-shell pick
  (deduped from `ReplayFrameSource`). The overlay already applied
  `PenBadge`/`PenShells` in both modes — no HUD change was needed.
- `3e6630a` — **fail-closed contract + prerequisite**: pinned
  `LiveFrame_NoPenetrationData_OmitsBadgeButServesFrame` (absent install data
  still serves names/positions, badge null) and documented that the live badge
  needs a **selected decoded session** (roster join maps entity ids → armor/mesh,
  viewpoint supplies the stock gun's shells) — same prerequisite as the live
  name join.
- `bbd52bd` — **enemy-only filter**: both paths now exclude the own tank AND
  same-team allies, scoping the aim to the viewpoint team's opponents. Own team
  comes from the decoded viewpoint participant (replay) / `participants[own]`
  (live). A tank with an unknown team stays eligible (fail-open — never hide a
  real enemy behind a decode gap).
- `fcb9811` — **alive-only filter**: destroyed wrecks are never a penetration
  target. Replay filters to `tank.Alive`; live drops a definitively-dead tank
  (alive byte 0), unknown/true stays eligible.

## Aim-target scoping (now complete)

`own tank excluded → same-team allies excluded → destroyed wrecks excluded →
enemies (or unknown-team, fail-open) → per-part armor + mesh normal + selected
shell`. This is the final semantic shape of the pen badge's candidates.

## Honest limits (unchanged)

- Thickness stays **nominal** (mesh normal × front/side/rear value); the
  armor-group → mesh-face mapping is still the open accuracy gap.
- Front-only armor for side/rear of turret/gun (0 → Unknown, never guessed).
- Loaded shell not decodable — the selector covers the stock gun's ammo.

## Verified

Full `scripts/validate.ps1` gate green after each change (1079 tests + 5 opt-in
skips). One transient `BeaconsSurviveReopen` SQLite disposed-object race in
Storage.Sqlite flaked during a mid-turn gate run; reproduced in isolation and
cleared on re-run — unrelated to pen (pre-existing).

## Remaining (all live- or owner-gated)

1. PN-4 live validation (CAM-013 aim at shot time → `PenValidation.Score`).
2. Single-launch cluster: completion-marker verify + batch rehearsal + Branch-B
   camera double-reads + `DamageDealt` E2E.
3. `ConsistentDoubleRead` flag-flip approval (owner).
4. L4 replayTime + T1 turret-facing sessions.
