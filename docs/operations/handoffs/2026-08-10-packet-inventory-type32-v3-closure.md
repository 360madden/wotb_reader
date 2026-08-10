# Handoff: full packet-type inventory + type-32 damage-mirror discovery + V3 evidence closure (2026-08-10)

## Scope

- Walked the **complete 11.19.0 packet-type inventory** on both replays (73 993
  packets on Oasis Palms) — the first 100% coverage of the stream, including
  every previously-undocumented type.
- Identified **type 32 as a damage/impact event mirror** of the type-8 direct
  damage stream.
- Closed **V3 (visibility model)** with evidence: **no spotting/reveal packet
  exists** in the replay stream.
- Documented a **Destroyed-events gap**: the decoder emits no
  `CanonicalEventKind.Destroyed`, so the HUD's `Alive`/death-pip path only
  runs on synthetic fixtures today.

## Evidence (all from `offline/replay-format.md`, section "Packet-type inventory")

| Type | Count (Oasis) | Semantics |
|---|---|---|
| 32 | 258 | **damage/impact event mirror** — same-instant as type-8 direct-damage (81/85 Oasis, 107/120 Dead Rail; every miss is amt=0), embeds the SAME 6-byte shell signature (`a6 a5 e0 a2 a8 b1` @ t=69.13 vs type-8 at the same time) |
| 33 | 52 | per-entity stream-open marker at spawn |
| 5 | 52 | per-entity full-state broadcast at spawn; x/y/z match type-10 |
| 4 | 4 | sparse entity marker; does NOT align with the destroy timeline |
| 23 | 59 | battle-state toggles (1/0 flips through combat) |
| 26 | 16 | sparse combat-window marker, all-zero payload |
| 29/36/17/38 | 4/1/1/1 | sparse spawn/end markers |
| 0/1/2/11 | 1/1/1/2 | entity-create / BasePlayerCreate at spawn |
| 13 | 1 | battle-end blob (4 381 B) after BattleEnded |

Counts are Oasis-only; the earlier "unexplored" table (31/35/39) is now part
of the full inventory.

## V3 closure

The full type inventory covers 100% of the stream and **no packet carries
reveal/spotting data** — spotted-reproduction is not data-possible from
replays. Replay mode renders god-view (already the HUD default). The live
spotting model remains an X5 policy-gated deliverable. Roadmap V3 row updated
to ✅ with the evidence note.

## Destroyed-events gap (new, open)

- The decoder emits `ParticipantObserved`, `Position`, `Damage`, `BattleEnded`
  only — never `Destroyed`.
- Type 4's 4-byte entity markers fire mid-battle for entities that keep
  streaming positions (e.g. entity 3760567 at t=105.54, last position t=222.2)
  — not destroy signals.
- amt=0 direct-damage events end a victim's chain but are the last *damage*
  event, not a destroy marker.
- Type-8 subtype 38 fires on the effect entity (4122629), not the victim.
- **Open target**: locate the destroy signal (likely a type-8 entity-method
  subtype or the type-7 status stream) so `Alive`/death pips work on real
  replays. `offline/replay-format.md` documents this.

## Verification

- Type-32/damage alignment: 81/85 Oasis, 107/120 Dead Rail (misses are all
  amt=0/no-damage).
- Shell signature byte match confirmed at multiple same-instant pairs
  (t=69.13, t=69.62).
- HUD death check on live web host: 0/13–0/14 tanks ever `alive=false` on
  Oasis (t=180/230/274) — confirms the gap on real data.
- Web host end-to-end re-verified (V1+V2): 2 damage pips at t=178 on entity
  3760567, 12/13 tanks with `screenHeadingDegrees` at t=150.

## Files

- `offline/replay-format.md` — full inventory table, V3 finding,
  Destroyed-gap note
- `docs/operations/product-roadmap.md` — V3 row closed with evidence

No production code changed; scratch scripts in `.data/tmp-type*.py` are
throwaway analysis (untracked).
