# Facing/yaw ground truth discovered in the replay packet + persisted (2026-08-10)

Status: OFFLINE COMPLETE (no live session). The facing/yaw track now has
authoritative replay ground truth in the DB; the memory-side offset remains
a gated live read (the same `EntityRecordRegionReadRequest` seam the
HP/damage-dealt plans need).

## The discovery

The 49-byte type-10 position packet carries **yaw/pitch/roll float32 at
payload +36/+40/+44 (radians)** — the earlier replay-format claim that the
triple was a physics velocity with "no orientation field in any packet" was
WRONG. Decisive evidence (both 11.19.0 replays, viewpoint entity):

- **Yaw = facing, 1:1 in radians**: 144/157 (savanna) and 109/122
  (medvedkovo) moving windows within 15° of the position-derived heading;
  every "miss" is a reversal (motion heading exactly 180° from facing — the
  tank backing up), where a velocity would flip but yaw stays.
- **Constant when stationary**: `+0.1270` unchanged across the full 10 s
  spawn (a physics velocity eases to ~0 over ~1 s).
- **Velocity-vector reading fails**: 14% of moving windows vs 79% for yaw.
- The +24–35 bytes are per-entity-constant (entity-specific handles; NOT
  padding as previously documented); +48 is a flags byte (1 observed).

## What changed

1. **Decoder** (`EventPacketDecoders.cs`): `TryReadPosition` now reads
   yaw/pitch/roll from the tail (fail-closed on non-finite, like x/y/z);
   `PositionObservation` carries them.
2. **Model** (`TelemetryModels.cs`): `PositionSample` gains nullable
   `Yaw/Pitch/Roll` (defaults null — existing constructions compile).
3. **Migration 5** (`SqliteMigrations.cs`): `position_packet_rotation` —
   idempotent table rebuild adding `yaw/pitch/roll REAL` columns, recreating
   `ix_positions_session_time`; existing rows copy with NULL (pre-migration
   decodes). `SqliteDecodeRunRepository` inserts the columns.
4. **Synthetic factory + tests**: the type-10 fixture writes yaw 0.75f and
   `ReplayDecoderTests` asserts it decodes; migration tests bumped 4 → 5.
5. **Docs**: `offline/replay-format.md` corrected (rotation, not velocity;
   +24–35 unclassified); `docs/operations/record-diffing-groundwork.md`
   gains the facing-track section (playbook: record-diffing on the ring
   record with wrap-aware float32 deltas, stationary control windows).

## Validation

- Both artifacts re-imported with the rebuilt CLI: migration 5 applied,
  yaw/pitch/roll persisted — savanna 2812/2812 viewpoint samples,
  medvedkovo 2784/2784.
- DB-level re-validation of the persisted yaw reproduces the probe signature
  (144/157 and 109/122 within 15°, reversals exactly 180°).
- Suites green: Replays 20+1 skipped (the env-gated packet-tail probe),
  Storage.Sqlite 21, Host.Web 124.

## Artifacts

- `tests/WotBTreader.Replays.Tests/PositionPacketTailProbeTests.cs` — the
  env-gated re-scan tool (`WOTB_PROBE_ARTIFACT`/`WOTB_PROBE_OUTPUT`) that
  dumps plaintext type-10 tails from stored artifacts (the persisted
  evidence is ciphertext; the tail is only visible at decode time). Skips
  (Inconclusive) without the env var.
- `.data/position-packet-tails.json` / `-medvedkovo.json` (gitignored) — the
  probe outputs used for this analysis.

## Next (approval-gated)

The gated live read on the ring record (0x38-byte stride: position
+0x10/+0x14/+0x18, velocity +0x28 — the unaccounted +0x2C..+0x37 tail is
the first place to look for the in-memory rotation), correlated against
`position_samples.yaw` via the record-diffing flow. The offline half is
complete and rehearsable now that the ground truth is in the DB.
