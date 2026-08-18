# Replay format cheat sheet

Focused reference for `.wotbreplay` parsing. Canonical detail:
[`docs/architecture/overview.md`](../docs/architecture/overview.md) (evidence
lifecycle) and the decoder source itself. All code below lives in
`src/WotBTreader.Replays/`.

## Pipeline

```
.wotbreplay (zip-like archive)
  → ValidatedReplayArchive.ReadAsync      (strict size/count/entry validation)
  → three entries extracted:
      meta.json            → WotbReplayMetadata
      battle_results.dat   → RestrictedPickleReader → ProtobufWireReader → BattleResultsReader
      data.wotreplay       → EventStreamReader (packets) → EventPacketDecoders → CanonicalEvents
  → ReplayDecodeProjection (Core models: BattleSession, Participants, Positions, Events, RawRecords)
```

## Archive entries & limits (`ReplayFormatConstants`)

| Entry | Name | Limit |
|-------|------|-------|
| Metadata | `meta.json` | 1 MiB |
| Battle results | `battle_results.dat` | 8 MiB |
| Event stream | `data.wotreplay` | 20 MiB |
| Whole archive | — | 20 MiB strict / 24 MiB expanded |

All three entries are **required** (`RequiredEntries`). Other hard caps:
`MaximumStrictPacketBytes` = 200 KiB, `MaximumStrictPacketCount` = 200 000,
`MaximumRosterEntries` = 64. Enforced by `ValidatedReplayArchive` and the
decoder; errors are `ReplayFormatException` with `replay.*` codes.

## Event stream (`data.wotreplay`)

- Magic: `0x12345678` (`EventStreamMagic`), verified by `EventStreamReader.ReadHeader`.
- Header: magic → length-prefixed `clientHash` (≤128 B) → length-prefixed
  `clientVersion` (≤128 B) → 1-byte marker. Compatible versions normalize to
  `11.18.0` (`IsCompatibleStreamVersion`).
- **Packet frame** (12-byte header + payload):
  - `uint declaredLength` (LE)
  - `uint type` (0–255; must be ≤ `byte.MaxValue`)
  - `float clockSeconds` (finite, non-decreasing, ≤ `maxClock` derived from
    expected duration, clamped 600–3600 s)
  - payload of `declaredLength` bytes
- An EOF-aligned `0xffffffff` type with clock 0 is a **legitimate end
  sentinel** written by 11.18 — not corruption.
- Malformed regions produce `EventStreamGap` records and a bounded
  resynchronization scan (`FindResynchronization`) instead of a hard abort;
  gaps become decode warnings. See `EventStreamReader.Scan`.

## Pickle boundary (`battle_results.dat`)

- The entry is a **Python pickle protocol 2** envelope: `(arenaInt, protobufBytes)`.
- `RestrictedPickleReader` is DATA-ONLY: it executes only passive scalar/tuple
  opcodes (STOP, MARK, EMPTY_TUPLE, TUPLE1–3, BININT*, INT, LONG*, BINSTRING/
  BINBYTES variants, memo BINPUT/BINGET, POP, DUP). Any code-loading or
  callable opcode throws `replay.unsafe_pickle_opcode`. **Opcodes are never
  executed.**
- Caps: 100 000 opcodes, stack depth 4 096, text 1 MiB, binary 128 B per int.
- The envelope must be exactly `(arena, protobuf bytes)`; trailing bytes or
  missing STOP → `replay.invalid_pickle`.

## Protobuf boundary

- `ProtobufWireReader` is a **generic wire-grammar parser** (varint, fixed32,
  fixed64, length-delimited, groups) — no generated schemas, no reflection.
  Unknown fields are preserved as evidence, never dropped.
- `ProtobufBudget` caps field count and nesting depth (`replay.protobuf_*`
  codes). `BattleResultsReader` walks the known field numbers:
  - root: field 2 = arena id varint, field 201 = roster, field 301 = match info
  - roster entries: field 1 = varint, field 2 = name, etc.
  - Unknown fields → `UnknownProtobufEvidence` (recorded, never fatal by itself).

**Full top-level walk (2026-08-10, both 11.19.0 replays, pickle unwrapped):**

| Field | Wire | Observed | Notes |
|---|---|---|---|
| 1 | varint | 65547 (savanna) / 7 (medvedkovo) | arena id (matches root.2 in the packet header) |
| 2 | varint | 1785705303 / 1785346505 | arena unique id |
| 3, 4 | varint | 1 / 1 | constant across both replays |
| 5 | varint | 244 / 209 | battle-length-ish counter (seconds?) |
| 8 | len | 92 / 91 B | arena descriptor: fields 3 = 3235 / 2277 (**client map-config id** — NOT the DB `map_id` 11/7), 101 = player mmr-ish, 105 = uint64 sentinel |
| 11 | len | 63 B | coordinate pairs with a constant 3:2 aspect (e.g. 3235×4853 savanna, 2277×3416 medvedkovo) — map-space descriptors |
| 137 | varint | 160 / 111 | matches roster count × map scale (160 = 10 × 16?) |
| 150 | len | 4.3 KB | per-team arena data (fields 20–23 are large blobs, 27 = `(tank_id, count)` pairs, 114 = per-entity records) |
| 181, 182, 183 | varint | 647/32/8404 vs 759/37/13220 | battle-specific counters |
| 184 | len ×10 | 19–39 B | per-player compact record (field 1 = identity, field 2 = team mini-stats) |
| 185 | len | 34 B | team result record (field 2.27 = `(tank_id, count)` pairs) |
| 201 | len ×14 | 29–242 B | roster entries (field 1 = account id or sentinel) |
| 301 | len ×14 | 58–199 B | per-player stats (field 1 = entity id, field 2 = stats) |
| 302 | len | 53 B | team records: `.1` repeats per team (2 on savanna, 4 on medvedkovo), field 1 = top player entity id, field 2 = team mini-stats |
| 303 | len | 8 B | team-level varints (32759/29118 savanna, 29736/26593 medvedkovo) — semantics unproven; NOT scores/victory points |
| 999 | varint | 3 | rare |

**Negative finding (O4):** the replay contains **no capture-zone / base
geometry**. `battle_results.dat` holds per-player stats, roster, and
map/arena descriptors only; the packet stream has no static zone
coordinates (see type-31/35/39 below). Capture zones in WoTB are
**map-static game data**, not replay data — objective markers must come from
map-static coordinates or manual beacon placement (the O3 beacon layer), not
from any replay file. 302/303 are team records but their semantics are
unproven; they are recorded as unknown-field evidence, never guessed.

## Event packets → canonical events (`EventPacketDecoders`)

**Position packet (type 10, `0x0A`) — 49-byte fixed layout (cross-validated
byte-identical against the decoded DB ground truth on the 11.19.0 medvedkovo
replay, 33 281 packets, all 49 bytes):**

| Offset | Size | Field | Notes |
|--------|------|-------|-------|
| 0 | 4 | entity id (int32 LE) | e.g. `2549401` = the player (`mrkool1138`, Churchill_I) |
| 4 | 4 | space id (int32 LE) | constant per battle (e.g. 261) |
| 8 | 4 | vehicle id (int32 LE) | 0 in the observed sample |
| 12 | 4 | x (float32 LE) | world coordinate, byte-identical to `position_samples.raw_x` |
| 16 | 4 | y (float32 LE) | same for `raw_y` |
| 20 | 4 | z (float32 LE) | same for `raw_z` |
| 24 | 12 | per-entity constant (NOT zero) | entity-specific bytes, constant per entity across the stream (e.g. `3679FB37 00000036 3679FB37`); one observed entity all-zero. Unclassified — not padding. |
| 36 | 4 | **yaw (float32 LE, radians)** | the entity's facing. Validated 2026-08-10 on both 11.19.0 replays: matches the position-derived heading 1:1 in radians while moving forward (savanna 144/157 moving windows within 15°, medvedkovo 109/122), is EXACTLY constant during stationary stretches (spawn `+0.1270` unchanged for 10 s — a velocity would ease to 0), and stays unchanged during off-axis reversals (motion heading flips 180° while the tank faces the same way) — the facing, not the velocity. Persisted as `position_samples.yaw` (migration 5). |
| 40 | 4 | pitch (float32 LE, radians) | small residual (observed ±0.24); persisted as `position_samples.pitch`. |
| 44 | 4 | roll (float32 LE, radians) | small residual (observed ±0.40); persisted as `position_samples.roll`. |
| 48 | 1 | flags byte | 1 for the observed stream |

**Orientation — CORRECTION (2026-08-10):** the +36–47 triple is the
entity's **rotation (yaw/pitch/roll)**, NOT a physics velocity as an earlier
draft concluded. The decisive evidence: (a) the +36 value tracks the
position-derived heading 1:1 in radians across both replays (the earlier
"~0.3 direction match" was measured without the movement gate — stationary
stretches and reversals make a velocity's direction meaningless noise); (b)
it is exactly constant through long stationary stretches (a physics velocity
eases to 0 over ~1 s); (c) it stays unchanged when the tank reverses
(motion heading +180° — a velocity vector would flip direction): the
velocity-vector reading matches only 14% of moving windows vs 79% for the
yaw reading. The decoder now persists all three (migration 5). The
28-byte type-39 packet (16 313 on this replay) is a fixed-size scene point
(two float32 pairs + a float triple) that does NOT align to any type-10
entity — a camera/marker coordinate, not per-tank orientation. Type-7
(19 378) is an entity-status packet (varint entity + packed int32s; the
value `0x02000000` repeats as a state flag — NOT yaw).

**Replay clock:** each frame header carries `float clockSeconds` (non-
decreasing). Position packets are emitted at ~10 Hz per entity and are NOT
per-entity sequential in the stream (they interleave across the 14–19
entities, which is why the decoder stores `sequence` + `replay_time_ticks`).
The full participant set (all tanks, all teammates) is present from
`LoadGameScene` to `onLeaveWorld` — the complete per-entity position
history for every entity is in `position_samples` (19 entities on this
replay: 14 named participants + 5 effect/shell entities).

Known packet types (decoded by `WotbReplayDecoder`):

| Packet type | Result |
|-------------|--------|
| Arena participants (`updateArena2` wrapper, type 8) | `CanonicalEventKind.ParticipantObserved` roster entries |
| Position (type 10) | `CanonicalEventKind.Position` → `PositionSample` (raw + normalized coords) |
| Health change (type 8/subtype 1, 19 B) | `CanonicalEventKind.Damage` — victim u32 at +0x00, subtype 1 at +0x04, declared length 7 at +0x08, post-hit HP u16 at +0x0C, attacker i32 at +0x0E, flag byte at +0x12. The packet holds the victim's CURRENT HP, not the amount; damage = HP delta from the ledger seeded by the type-5 max-HP broadcast. 0xFFFD post-hit HP is the destroy marker carrying the killer (remaining HP credited to the killer, matching battle_results accounting). The 0xFFFD marker ALSO emits `CanonicalEventKind.Destroyed` (deduped with the position destroy marker, first by sequence wins) — it is the more complete death signal: on both replays it caught 3 tanks the position markers missed (medvedkovo 2549397 @183.8s + 2549402 @271.5s, savanna 3760576 @245.1s), leaving alive flags exactly aligned with the HP ledger. Validated 2026-08-11: per-attacker sums equal battle_results damage_dealt exactly on both replays for every player WITH battle results (left players have none but their decoded damage is still true); the old subtype-8 amount field was never the HP damage |
| Spawn full-state (type 5) | `CanonicalEventKind.MaxHealthObserved` (first broadcast per roster entity; u32 eid at +0x00, u16 current HP at +0x33 — the first broadcast precedes any damage, so it equals max HP) |
| Shot impact (type 32, `01 11`/`01 12`) | `CanonicalEventKind.ShotImpact` — victim u32 at +0x00, flag u16 at +0x04, hit-result byte at +0x13 (`01 12`) / +0x12 (`01 11`): `0x03` = penetrating, `0x00/0x01/0x02/0x04` = non-penetrating (pinned on three replays, ~98% agreement with the type-8 ledger). Values json: `{"victimEntityId", "hitResult", "penetrated"}`. The short companions (`01 02`/`01 03`) and shell/effect entities (`01 05`/`01 06`) stay raw |
| Lifecycle (type 14) | `CanonicalEventKind.BattleEnded` |

**Raw (undecoded) shot-attribution packet:** the type-8 subtype-8 packet
(33 B) is the per-shot attribution mirror — victim u32 at +0x00, subtype 8
u32 at +0x04, declared length 21 u32 at +0x08, **attacker u32 at +0x0C**,
victim u32 again at +0x10, a 3-byte flags region, the 6-byte shell signature
(at +0x17, matching the type-32 mirror's), and a 4-byte trailing field. It
fires for BOTH penetrating and bouncing shots (verified on bounces whose
shell signatures match the type-32 mirror) but is NOT complete — in the
sample run 28 packets vs 69 type-32 shots, none before t≈79.9 s. It is
captured raw (`packetType=8`, `entityMethodSubtype=8`), not yet decoded.

**Packet-type inventory — structure evidence (2026-08-10, savanna
11.19.0, 73 993 packets; counts are savanna-only):**

| Type | Count | Structure | Semantics |
|---|---|---|---|
| 31 | 6 777 | 4-byte float, **combat-only** (first at t≈71 s = battle start, last at t≈275 s) | unknown; NOT distance-to-nearest-enemy (tested, no correlation); value toggles between ~27.009 (repeated default) and 6.7–8.8 minima; ~30 Hz during combat |
| 35 | 2 814 | 1 byte, exactly one per 0.1 s tick, values 0x5f→0xbc→0x19→… | **mod-256 tick counter** (wraps every 25.6 s; 10 Hz) |
| 39 | 16 984 | 28 bytes = 7 float32, **per-frame (~60 Hz)** | **scene point, semantics UNRESOLVED**: smooth drift, matches NO entity position, team centroid, or bbox anchor; NOT a third-person camera (offset 30→507 m, ~38 m below the tank); settles on fixed anchors (spawn corner t≈1.7–68 s, victory point t≈245–281 s on savanna). Static pass `FindScenePointWriter` (2026-08-10): its bit-exact constant -0.0011081547 (f32 0xBA913F80) has **0 hits** — computed at runtime, writer not locatable by that anchor; Rust oracle also reports type 39 unknown. Not zone geometry; camera/VP-track candidate remains open (see `record-diffing-groundwork.md` triage). |
| 10 | 60 103 | 49 B = entity-id + space + vehicle + x/y/z + per-entity constant + yaw/pitch/roll + flags byte | **position stream** (decoder `TryReadPosition`; 0.1 s cadence per entity; rotation tail verified 2026-08-10). **Destroy marker**: the same 49 B with the per-entity constant (payload +24..+35) zeroed AND flags byte (+48) cleared — fires at the death instant (position freezes), first marker per roster entity = `Destroyed` (2026-08-10) |
| 32 | 258 | entity-id + event flag + payload, 3 layouts (11 B, 25–27 B, 14 B) | **damage/impact event mirror (2026-08-10)**: fires at the same instants as the type-8 direct-damage events for the same victim (81/85 alignment on savanna, 107/120 on medvedkovo — every miss is an amt=0/no-damage event) and embeds the SAME 6-byte shell signature as its matching type-8 packet (e.g. `a6 a5 e0 a2 a8 b1` at t=69.13, `ff e0 b9 d7 d8 98` at t=69.62). Flag prefixes distinguish the event (`01 11`/`01 12` = damage-with-payload, `01 02`/`01 03` = short companion, `00 10` = state snapshot at spawn and at the end of the victim's event chain); shell/effect entities (0x30xxxx range) carry `01 05`/`01 06`. NOT spotting: no reveal/visibility data in any payload. **Layout pinned + decoded (2026-08-13):** the prefix does NOT separate pen from bounce (the same `01 12` fires for both); the payload's HIT-RESULT byte (offset 19 for `01 12`, 18 for `01 11`) does — `0x03` = pen, `0x00/0x01/0x02/0x04` = non-pen, ~98% agreement with the type-8 ledger on three replays. Shipped as `CanonicalEventKind.ShotImpact` (victim + hitResult + penetrated); the non-0x03 bounce-vs-absorb mapping is not yet pinned. |
| 33 | 52 | 8 B = entity-id + 4 zero bytes | per-entity stream-open marker (1× per entity, at spawn t≈0.11) |
| 5 | 52 | 48–173 B = entity-id (u32 +0x00) + current HP (u16 +0x33) + unclassified tail | per-entity full-state broadcast at spawn (1–3 per tank). CAVEAT (2026-08-11 scan): the older "48–173 B = … + x/y/z + tail" row did NOT reproduce — the 48/51-byte variants carry no 4-byte float triple matching decoded position at the same clock (not even lag-tolerant), their tails are packed ints/zeros with no rotation-like floats; the long variants are serialized name/id blobs. The tail beyond eid/HP is UNCLASSIFIED; no turret/aim rotation exists in type-5 |
| 4 | 4 | 4 B = entity-id | sparse entity marker; does NOT match the destroy timeline (fires mid-battle for entities that keep streaming positions) — semantics unresolved |
| 23 | 59 | 4 B int32 toggling 0/1 | battle-state toggles (1 at LoadGameScene, 0 at battle start t≈71, then dense 1/0 flips through combat, ~30 pairs) |
| 26 | 16 | 4 B (all-zero observed) | sparse combat-window marker (t≈119–274) — semantics unresolved |
| 29 | 4 | 1 B = 0x01 | sparse marker at spawn (t≈0–2.1) |
| 0 | 1 | 903 B | BasePlayerCreate (decoder `TryReadBasePlayerCreate`) |
| 1 | 1 | 102 B | entity create with x/y/z floats at spawn |
| 2 | 1 | 5 B | entity-id + 0x00 |
| 11 | 2 | 20/97 B | entity create (x/y/z floats at spawn) |
| 13 | 1 | 4 381 B | battle-end blob at t≈279.3 (post-BattleEnded) |
| 36 | 1 | 4 B | spawn marker |
| 17 | 1 | 0 B | empty packet at t≈1.61 |
| 38 | 1 | 1 B | single byte |
| 7 | 19 040 | entity-id + packed int32s (13–16 B) | entity-status stream. **Surveyed 2026-08-11 (savanna 11.19.0, payload bytes vs canonicalized hull yaw):** layout = entity-id u32 + 2 entity-specific state int32s (e.g. `2,2` for most tanks, `9,4` for one effect entity) + a fast-rotating 16-bit tail that sweeps the full circle at 2 000–3 600°/s — a tick counter/bitfield, NOT an angle (a real turret traverse is ~20–60°/s). One effect entity carries a 16-byte layout with a third rotating float X (moves while hull yaw is static, but X ≠ yaw, X ≠ pitch — an effect parameter, not a tank field). **No turret angle and no lock/target field exist in type-7** |

**NO spotting/reveal packet exists** — the full type inventory above covers
100% of the stream and none carries reveal/visibility data. This is the V3
finding (2026-08-10): spotted-reproduction is not data-possible from replays;
replay mode renders god-view. Type 8 also carries large protobuf blobs
(avatar URLs, player skins) — the `updateArena2` roster source.

**NO turret-facing or lock/target packet exists either** — the type-7
entity-status survey (2026-08-11, above) plus the full inventory closes both:
the replay can provide per-enemy position, HP, hull yaw/pitch/roll, alive
state, and team/tank — but NOT the turret angle and NOT any auto-aim /
lock-on / "has-me-targeted" state (client-side UI state, absent from the
server-authoritative stream). The replay HUD's "aims at me" signal is
therefore limited to the HULL direction (geometry), and true turret/lock
state is a live-memory discovery target, not a replay decode.

**Destroy signal FOUND (2026-08-10):** the destroy marker is a **type-10
position packet with the per-entity constant (payload +24..+35) zeroed AND
the status flags byte (+48) cleared** — the same 49-byte layout as a normal
sample, with the constant field zeroed at the death instant. Verified on
both 11.19 replays: 15/15 destroyed tanks have exactly one first-marker and
0/13 survivors have any; the position stream freezes at the marker (the
wreck then re-broadcasts the frozen position, and can re-carry the marker
byte pattern — only the FIRST marker per entity is a death). The decoder
now emits `CanonicalEventKind.Destroyed` for the first marker per roster
entity (non-roster viewpoint/debris entities are ignored even though they
can carry the byte pattern). The HUD's `Alive` flag and death pips now run
on real replays. Ruled out en route: type 4's entity markers (fire
mid-battle for entities that keep streaming), amt=0 direct-damage events
(last *damage* events, not a kill), and the type-7 status stream (states
toggle constantly, no transition at death times). Unknown packets remain
`RawRecord`s with the `UnknownRecordsPreserved` capability — unknown stays
unknown.

Decoding is **evidence-first**: every decoded fact carries an
`EvidenceReference` (artifact, entry, offset, length, SHA-256) and a
`EvidenceConfidence` (Exact/Derived/Estimated). Unknown packets are preserved
as `RawRecord`s with the `UnknownRecordsPreserved` capability — unknown stays
unknown.

## Core models (`WotBTreader.Core`)

- `CanonicalEventKind`: Unknown, ParticipantObserved, Position, Damage,
  Destroyed, BattleStarted, BattleEnded
- `ReplayCapability` flags: Metadata, BattleResults, Participants, Teams,
  EntityMapping, Positions, Damage, Lifecycle, InstalledGameMetadata,
  UnknownRecordsPreserved
- Projection shape: `ReplayDecodeProjection` (see `TelemetryModels.cs`)

## Tests & fixtures

- `tests/WotBTreader.Replays.Tests/` — `ReplayDecoderTests`, `BinaryReaderSecurityTests`
  (pickle opcode rejection, protobuf limits), `ReplayProbeSecurityTests`
- `tests/WotBTreader.TestSupport/SyntheticReplayFactory.cs` — builds a synthetic
  replay in-memory (incl. `CreatePickle`) for CI; private replays never enter tests
- Replay probe (`WotbReplayProbe`) validates structure without full decode
