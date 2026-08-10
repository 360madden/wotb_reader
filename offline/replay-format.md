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

## Event packets → canonical events (`EventPacketDecoders`)

**Position packet (type 10, `0x0A`) — 49-byte fixed layout (cross-validated
byte-identical against the decoded DB ground truth on the 11.19.0 Dead Rail
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
| 36 | 4 | **yaw (float32 LE, radians)** | the entity's facing. Validated 2026-08-10 on both 11.19.0 replays: matches the position-derived heading 1:1 in radians while moving forward (Oasis Palms 144/157 moving windows within 15°, Dead Rail 109/122), is EXACTLY constant during stationary stretches (spawn `+0.1270` unchanged for 10 s — a velocity would ease to 0), and stays unchanged during off-axis reversals (motion heading flips 180° while the tank faces the same way) — the facing, not the velocity. Persisted as `position_samples.yaw` (migration 5). |
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
| Arena participants (`updateArena2` wrapper) | `CanonicalEventKind.ParticipantObserved` roster entries |
| Position | `CanonicalEventKind.Position` → `PositionSample` (raw + normalized coords) |
| Direct damage | `CanonicalEventKind.Damage` |
| Entity method / lifecycle | `CanonicalEventKind.Destroyed`, `BattleStarted`, `BattleEnded` |

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
