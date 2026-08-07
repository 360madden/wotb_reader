# REPLAY CROSS-CHECK — DECODER RE-VERIFICATION + PER-PLAYER BATTLE-STATS SURFACE (2026-08-07)

**Session outcome:** re-ran the C#↔Rust decoder cross-check after the
bug-hunt campaign (decoder still agrees with the oracle), then extended the
cross-check surface with per-player battle stats (damage / base XP /
credits / assisted damage / damage blocked / victory points / mm_rating /
tank_id) — verified exact-match on the Dead Rail replay, committed and
pushed as `a38c32e`.

## Repository state

- Branch `main`, head `a38c32e` (pushed to origin), working tree clean.
- Prior head when the session started: `9d701d4` (FRESH36 first write-site
  hit milestone).

## What was checked first (the user's question)

Both community assets (`wotbreplay-inspector` Rust oracle +
`wotbreplay-parser` schema/fixtures) were **already integrated** — commits
`841531b` (oracle), `f3fa1ad` (stat-tag port), `85db67a` (typed packet
surface). Verified on disk: oracle exe staged + hash-pinned in
`tools/external/tools.lock.json`, parser snapshot at
`C:\work\tools\wotbreplay-parser-main`, `crosscheck.cmd` wrapper, docs in
`docs/operations/replay-crosscheck.md`. The last report (Aug 4 22:02Z) had
zero disagreements; the tool had simply not been re-run since the bug-hunt
campaign (it is operator-run, not CI — correctly dormant).

## Changed files and public contracts

| File | Change |
|---|---|
| `tools/src/WotBTreader.ReplayInspector/Program.cs` | Each participant now emits `battleStats` (all 17 `BattleStats` fields, camelCase) plus the pre-existing `vehicleCompactDescriptor` (= Rust `tank_id`, tag 103). Public CLI surface extended; existing fields unchanged. |
| `scripts/invoke-replay-crosscheck.ps1` | New Surface 6: per-player stats comparison (`stats_surface` report block: `players_compared` / `sentinel_stats_skipped` / `absent_zero_notes` / `disagreements` / `field_failures`). Exit-code contract unchanged (0 agree / 1 disagree / 2 missing / 3 decode-failed / 4 other). |
| `docs/operations/replay-crosscheck.md` | Documents the battle-stats surface, bot-sentinel-stats rule, `rust=0 vs cs=null` rule; updated exit-code table wording. |

## Semantic rules implemented (evidence-first)

- **Bot sentinels (accounts -1…-10):** Rust carries their full stats (reads
  the sentinel as truncated uint32); C# deliberately rejects sentinel
  identities (evidence-first), so bot participants have `battleStats: null`
  and their stats are **not compared** — counted as `sentinel_stats_skipped`,
  never a failure. On Dead Rail: 10 sentinels skipped.
- **`rust=0` vs `cs=null`:** prost serializes a wire-absent uint32 stat as
  default `0` and cannot distinguish "absent" from "genuinely zero"; C#
  keeps absent = null. Counted as an `absent_zero_note`, not a failure.
  A **`rust≠0` / `cs=null` pair is a hard disagreement** (the oracle read a
  value the C# decoder missed). `mm_rating` is `Option<f32>` in the schema
  (absent → null on both sides) and is excluded from the note path.
- **`mm_rating`** compared with 1e-3 float tolerance; all other stats exact.
- **`tank_id`** = Rust tag 103 ↔ C# `vehicleCompactDescriptor`; one-sided
  presence is a hard failure (mirrors the field loop).

## Tests and validation run (all pass)

- `invoke-replay-crosscheck.ps1 -Replay <Dead Rail>` → exit 0,
  `disagreements: []`, `stats_surface: players_compared=4,
  sentinel_stats_skipped=10, absent_zero_notes=16, disagreements=0`,
  `field_failures={}`.
- Spot-check (author, player name `mrkool1138`): damage **1598/1598**,
  base_xp **759/759**, credits **13220/13220**, damage_blocked **140/140**,
  tank_id **2897/2897** — identical on both decoders.
- `-GoldenVector` → PASS (oracle reproduces the parser's published values).
- `dotnet test tests/WotBTreader.Replays.Tests -c Release` → 20/20.
- `dotnet build tools/src/WotBTreader.ReplayInspector -c Debug` → 0 errors
  (inspector rebuilt from current decoder source; old Debug exe predated the
  last decoder touch).
- Dual-engine parse (`tmpwotb-e2e/check-parse.ps1`) → PARSE_OK; ASCII clean.
- Reviewer pass found + fixed before commit: `mm_rating` was missing from the
  field map (float branch dead code; docs overclaimed); tank_id one-sided
  presence silently passed. Both fixed and re-verified.

## Assumptions and unknowns

- **mm_rating not exercised live:** every player on Dead Rail has
  `mm_rating: null` (Rating Battles only). The float branch is unit-proven
  by inspection but not by a real non-null rating value; a Rating Battles
  replay would close this gap.
- **Single-battle proof:** the stats surface is verified on one real battle
  (Dead Rail). The earlier surfaces were proven on 4 real replays at
  integration; the stats surface should be swept across the remaining real
  `.data/launch` replays.
- **No decoder divergence found** — the campaign's Replays commits
  (`f3fa1ad`, `85db67a`) predate the cross-check extension and are already
  cross-validated.

## Integration risks

- The C# inspector is a Debug build at a fixed path; if the decoder source
  changes, the cross-check silently validates the **stale** binary. Rebuild
  before every operator run (or add a freshness guard — see next steps).
- The `rust=0 vs cs=null` note rule depends on prost's default-zero
  behavior; if the parser crate ever switches to `Option<u32>`, the rule
  should be revisited (absent would then serialize as null on both sides).
- Handoff omits account identifiers per the working agreement; stats
  evidence is keyed by player name in any future write-up.

## Recommended next steps

1. Sweep the extended cross-check across all real (non-synthetic)
   `.data/launch` replays; tabulate per-replay verdicts.
2. Exercise `mm_rating` on a Rating Battles replay if one is available
   locally.
3. Consider a decoder-freshness guard (auto-run cross-check when
   `src/WotBTreader.Replays` changed since the last report) before the
   operator-run step can be skipped by accident.
