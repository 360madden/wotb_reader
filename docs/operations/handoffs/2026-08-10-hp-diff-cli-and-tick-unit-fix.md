# Handoff — 2026-08-10: `hp-diff` CLI verdict seam + replay-tick unit bug (10×)

## What changed

1. **Replay-tick unit bug found and fixed (`replay-delta-extractor.py`).**
   The decoded DB stores replay ticks as .NET ticks (10⁷/s): `position_samples`
   max tick ≈ `battle_sessions.duration_ticks` (Oasis Palms 2,798,934,300
   ticks = **279.9 s** battle, not 2798 s), and the decoder writes
   `TimeSpan.FromSeconds(...).Ticks`. The extractor's `TICKS_PER_SECOND`
   was 10⁶, so every "seconds" output and the hit-window bucketing were
   **10× too large**. Fixed to 10⁷, and the movement-proxy participant
   ranking (a consecutive-only scan, dead at ~100 samples/s) now scans
   ~1s-apart pairs. Schedules re-derived and verified window-by-window
   against raw event ticks.

   **Corrected schedules (real replay seconds):**
   - Oasis Palms (≈280 s): victim **3760578**, 9 hits / 4,028 dmg at
     90.4–167.4 s → six 10 s windows **90–100, 100–110, 130–140,
     140–150, 150–160, 160–170 s** (sums 256/1278/664/386/933/511).
   - Dead Rail (≈271 s): victim **2549399**, 18 hits / 4,647 dmg at
     114.4–152.4 s → five 10 s windows **110–120 … 150–160 s**.
   The earlier draft's 900–1680 s / ~2798 s numbers in the groundwork doc
   and the qualification handoff were corrected (with a unit note).

2. **`hp-diff` CLI command — the executable verdict seam.** The session
   flow's bucket → correlate → verdict steps had no CLI surface; now
   `wotbtreader-cli hp-diff <snapshots.json> --session <id> --victim
   <entity> [--mode strict|lenient]`:
   - Loads the dump-file contract (`HpDiffSnapshotsFile`, schema
     `wotbtreader.od.hp-diff.snapshots.v1`, region ≤ 4096 B, strictly
     increasing clocks, byte-length fail-closed — 9 parse tests).
   - Queries the REAL `IHpGroundTruthProvider` (damage/destroyed events
     from the decoded DB).
   - Buckets, correlates (Lenient first — overkill), confirms under
     Strict, and emits the hardened verdict (score 1.0 + flatness 1.0 +
     ≥ 2 exact-sum Strict matches) with the matched-window list
     (candidates now carry `MatchedWindows`).
   - 3 end-to-end tests against a seeded real DB: HIT at +0x48, honest
     no-hit, malformed input → `InvalidInput`.
   Also fixed `CliInvocation` so `--session`/`--victim`/`--mode` consume
   their values (they were parsed as positionals).

## Why it matters

- The dump schedules the live session would have used were 10× wrong —
  caught offline, before any live run, by cross-checking the max position
  tick against the session duration.
- The live session is now fully executable on the offline side: the only
  remaining input is the trusted region dumps (the gated
  `EntityRecordRegionReadRequest`), which feed the `hp-diff` contract
  directly.

## Gates

- `scripts/validate.ps1` exit 0 — all 12 projects green (Host.Cli.Tests
  15 → 27, Core 123, Application 32); offset validator PASS.
- Extractor `--top-victims`, `--hp-delta`, `--simulate`, `--movement` all
  run clean under the corrected tick constant; hit windows verified
  against direct SQL.

## Next

The gated live session (region-read addition + one Oasis Palms run on
victim 3760578, dumps on the six windows above + Dead Rail 2549399 for
repeatability), or the `replayTime` live attempt (OD-044).
