# 2026-08-11 — Max HP is in the replay: type-5 spawn full-state broadcast

## Summary

The overlay's HP fraction has a missing piece: it needs each tank's **max HP**
to turn the decoded damage arc into an absolute fraction, but "exact max HP
is not in the decoded data" was the standing assumption. **That assumption is
now refuted.** The type-5 spawn full-state broadcast carries it.

`TryReadSpawnHealth` (new in `EventPacketDecoders.cs`) decodes type-5
packets: `u32 entityId @ +0x00`, `u16 currentHealth @ +0x33` (LE). The
decoder emits one `CanonicalEventKind.MaxHealthObserved` per **roster**
entity — the *first* broadcast only — with `{"maxHealth": n}`, plus a typed
`spawnHealth` raw record for every type-5 packet. Non-roster entities
(duplicate "self" stream, debris) are filtered like the Destroyed dedupe.

## Why the first broadcast = max HP (validated, not assumed)

| Check | Result |
|---|---|
| Author cross-check | 3760577 / 2549401 (tank_id 2897, Churchill I, same account) = **700 on both replays**, exactly `battle_results.hitpoints_left: 700` |
| Monotonicity | u16@+0x33 is monotonic non-increasing per tank across broadcasts (Dead Rail 2549397: 540 → 501 after damage) → it's current HP, and the first value is the max |
| First-broadcast-before-damage | **28/28 tanks** across both replays: the first type-5 packet for each entity precedes its first damage packet |
| Aggregate bound | total `damage_dealt` ≤ Σ first-broadcast values: Oasis 8964 ≤ 12140, Dead Rail 6227 ≤ 8500 |
| Cross-replay identity | same tank_id → same value (700 twice) |
| Real-replay decode | **14/14 Oasis + 14/14 Dead Rail** `MaxHealthObserved` events, values byte-exact vs the raw scan (Oasis: 680…1550, Dead Rail: 520…780) |

## Product changes

- `CanonicalEventKind.MaxHealthObserved` (value 7) added to
  `src/WotBTreader.Core/TelemetryModels.cs` with the validation summary in
  its doc comment.
- `EventPacketDecoders.TryReadSpawnHealth` + `SpawnHealthObservation`
  (type 5, payload ≥ 0x35).
- `WotbReplayDecoder`: type-5 packets decode into spawn-health observations
  + typed raw records; `BuildEvents` emits the first-broadcast-per-roster-
  entity `MaxHealthObserved` draft.
- `SyntheticReplayFactory`: `includeSpawnHealth` fixture (first broadcast
  per roster entity, a lower re-broadcast, a non-roster entity).
- New test `SpawnHealthFirstBroadcastPerEntityEmitsMaxHealthObserved`
  (ReplayDecoderTests). Full gate green (12 projects, 0 failures).

## Separate finding: decoded damage amounts do NOT match HP loss

While validating, the damage-amount field surfaced as suspect — this is
**tracked separately** and is the remaining blocker for an absolute
`1 − taken/maxHp` overlay fraction:

- The author's decoded pen hits sum to ~2184 vs `battle_results`
  `damage_dealt: 752` (~2.9×), and per-victim sums can be 2.5–7× the tank's
  max HP (2549397 "received" 4046 with max HP 540).
- `TryReadDirectDamage` only accepts `damageSubtype == 3`, but the packet
  that actually dropped 2549397 540 → 501 (clk 92.68, dsub=1) is dropped,
  so the decoded damage arc misses real HP loss while over-counting others.

Until the type-5 `MaxHealthObserved` value can be paired with trustworthy
per-hit damage, the overlay keeps its self-normalizing damage-arc fraction.
Next steps: pin the real damage amount field in type-8/subtype-8 (the
dsub=1 packet layout), then wire `ReplayFrameSource` to compute
`hpFraction = 1 − taken/maxHp` from the new canonical event.

## Evidence

- Manual scan + validation scripts (inline Python over `data.wotreplay`
  framing: u32 len / u32 type / f32 clock headers).
- Real-replay decodes written to the local host DB: Oasis decode run
  `019fef3e-dabc-7146-bd18-71cba0c80d2e`, Dead Rail
  `019fef3f-c72d-716b-880d-d126d691f355` (14 `MaxHealthObserved` each).
