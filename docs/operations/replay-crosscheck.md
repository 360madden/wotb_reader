# Operator-run replay cross-check validation

The C# `Replays` decoder is the product's core capability, so its correctness
must be proven against an **independent implementation**, not just its own
tests. `scripts/invoke-replay-crosscheck.ps1` runs the C# decoder and the Rust
`wotbreplay-inspector` oracle (built on eigenein's `wotbreplay-parser`, see
"Oracle provenance" below) on the same `.wotbreplay` and compares the
cross-check surface: battle timestamp, participant set (account/name/team),
packet clock sequence, the **typed packet surface** (`BasePlayerCreate`
header + `UpdateArena` players roster), and the **per-player battle-stats
surface** (damage/XP/credits/mm_rating/tank_id and the full
`PlayerResultsInfo` stat set).

This is an **operator-run step, not a CI step**: it needs real (non-synthetic)
11.18/11.19 replays, which CI does not have. It is not part of
`validate.ps1` or the CI gate.

## When to run it

Run `crosscheck.cmd` (or the script directly) after **every** change that
touches replay decoding:

- the game updates its replay format and the decoder's version gate or
  packet decoders change (`11.18`/`11.19` compatibility changes);
- `BattleResultsReader`, `EventStreamReader`, `EventPacketDecoders`, or the
  pickle/protobuf readers are modified;
- battle-stats tag mappings are added or adjusted;
- before a milestone commit whose diff touches any `src/WotBTreader.Replays`
  file.

It is cheap insurance: a few minutes on the real `.data/launch` replays, and
it catches decoder regressions that unit tests cannot (tests are written
against the same decoder's assumptions).

## How to run it

From the repo root (any directory):

```powershell
.\crosscheck.cmd                          # newest .data\launch replay
.\crosscheck.cmd -Replay <path>           # a specific .wotbreplay
.\crosscheck.cmd -GoldenVector            # validate the oracle itself
```

Direct invocation is equivalent:

```powershell
pwsh ./scripts/invoke-replay-crosscheck.ps1 -Replay <path>
```

For the full sweep, run it once per real replay under `.data\launch`
(the two ~1 KB files there are synthetic fixtures; the large ones are real
battles — those are the meaningful checks). Reports land at
`.data\replay-crosscheck-report.json`. Note the encoding: Windows PowerShell
5.1's `Set-Content` writes a UTF-8 **BOM** while pwsh 7 does not — read it
with `Get-Content -Raw -Encoding UTF8` (handles both), not a bare
`ConvertFrom-Json` on the raw file.

## Exit codes and interpretation

| Code | Meaning | Action |
|---|---|---|
| 0 | Both decoders agree on battle time, participants, clocks, the typed packet surface, and per-player battle stats | Proceed |
| 1 | Real divergence found | Investigate before committing decoder changes |
| 2 | Oracle or C# inspector binary missing | Re-stage `tools/external/installed/wotbreplay-inspector/` or rebuild the inspector tool |
| 3 | One decoder failed to decode the replay | Check the replay is 11.18/11.19 and not corrupt |
| 4 | Other error (reserved; not currently emitted) | Report the script output |

## Known, expected differences (notes, not failures)

- **Bot-account sentinels.** WoTB bot accounts are negative int32 (-1…-10).
  Rust's prost schema declares the field `uint32` and truncates the
  sign-extended wire varint to the `4294967286..4294967295` sentinel range.
  C# deliberately rejects the 64-bit sign-extended value as an unverifiable
  identity (evidence-first) and records the player from updateArena with no
  account. Recorded as a note; matched by (nickname, team).
- **Battle-time source.** Rust reads `battle_results.dat` protobuf tag 2
  (server-recorded); C# prefers `meta.json` `battleStartTime`
  (client-recorded). A small delta (a few seconds) is a client-vs-server
  clock artifact — noted, not failed. A delta ≥ 60s **is** a hard failure.
- **Arena-only participants on synthetic fixtures.** Tiny (~1 KB) fixtures
  with names like `pilot-a`/`unit-b` can show an accountless updateArena
  participant on the C# side that Rust's battle-results read does not see
  (`player only in cs: unit-b|2`). This is a pre-existing harness nuance of
  the synthetic fixtures, unrelated to decoder changes — validate against the
  large real battle replays instead. Note that the **typed UpdateArena
  roster surface still agrees** on these fixtures: both decoders decode the
  same field-1 roster, so the divergence is confined to the battle-results
  comparison.
- **UpdateArena `account_id 0` vs C# `null`.** Rust emits `account_id: 0`
  for roster entries with no account binding; C# emits `null`. Both mean
  "no account evidence" and the harness treats them as equal.
- **Bot sentinel stats.** Rust carries full battle stats for bot sentinels
  (accounts -1…-10, read as truncated uint32); C# deliberately rejects
  sentinel identities (evidence-first), so bot participants have
  `battleStats: null` and their stats are **not compared** — counted as
  `sentinel_stats_skipped`, never a failure.
- **`rust=0` vs `cs=null` on absent stats.** Rust's prost serializes a
  uint32 stat field that is absent on the wire as the default `0` (it
  cannot distinguish "absent" from "genuinely zero"); C# keeps absent
  fields `null` (evidence-first). A `rust=0 / cs=null` pair is counted as
  an `absent_zero_note`, not a disagreement. A `rust≠0 / cs=null` pair
  **is** a hard disagreement (the Rust oracle read a value the C# decoder
  missed).

## Battle-stats surface

`crosscheck` now compares the per-player `PlayerResultsInfo` stat set from
`battle_results.dat` (root.301.2) for every non-sentinel account present in
both decoders:

- `credits_earned` / `base_xp` / `damage_dealt` / `damage_assisted_1|2` /
  `damage_blocked` and the rest of the uint32 stat fields (exact match);
- `mm_rating` (float, compared with 1e-3 tolerance; Rust serializes it as
  `Option<f32>`);
- `tank_id` — Rust tag 103 vs C# `vehicleCompactDescriptor` (the same
  field; C# maps tag 103 to the participant's compact descriptor).

The C# inspector emits these as `participant.battleStats` (camelCase) plus
`vehicleCompactDescriptor`; the Rust oracle emits them as
`player_results[].info`. Verified exact-match on the Dead Rail replay:
4 real accounts compared, 10 bot sentinels skipped, 0 disagreements
(e.g. author: damage 1598, base_xp 759, credits 13220, tank_id 2897 on
both sides).

## Typed packet surface

Beyond battle time and rosters, the cross-check compares the typed packets
both decoders can parse independently:

- **`BasePlayerCreate` (type 0) header** — `author_nickname`,
  `arena_unique_id`, `arena_type_id`. The fixed layout (10 skipped bytes,
  1-byte-length UTF-8 nickname, little-endian u64/u32 ids) is shared by both
  decoders, and `arena_unique_id` is the replay's **third arena-identity
  source** after `meta.json` and the battle-results tuple. On real replays
  all three agree; the C# decoder emits a warning if the packet header
  disagrees with the battle-results tuple.
- **`UpdateArena` (type 8 / subtype 48) players roster** — the field-1
  players list, compared as a (nickname, team) presence map plus account
  bindings (with `0 ≡ null` normalization above).

## Golden vectors

`crosscheck.cmd -GoldenVector` validates the **Rust oracle itself** against
the parser's published expected values — the fixture
`20221203_player_results.wotbreplay` (timestamp `1670083956`, 14 players,
`yuranhik_hustriy26`) lives in the **external parser snapshot**, not this
repo: `C:\work\tools\wotbreplay-parser-main\replays\`. Run this when the
oracle binary is re-staged or rebuilt, so the oracle is trusted before it is
used as the comparison baseline. The parser fixtures are 9.4–10.1 era —
older than the C# decoder's 11.18/11.19 gate — so golden vectors never
validate the C# decoder; real 11.18/11.19 replays do.

## Oracle provenance

The oracle binary is rebuilt from `C:\work\wotbreplay-inspector-main` with a
**local path dependency** on the patched parser at
`C:\work\tools\wotbreplay-parser-main` (`Cargo.toml` `[patch]`/path dep),
staged to `tools/external/installed/wotbreplay-inspector/` and hash-pinned in
`tools/external/tools.lock.json`. The local parser patch adds UpdateArena
support for **subtype 48** (the 11.18/11.19 variant) alongside the upstream
subtype 47 — without it, the oracle reports all arena packets as `Unknown`
and the typed roster surface is empty. When re-staging a rebuilt binary,
re-run `crosscheck.cmd -GoldenVector` before trusting it.

## Registry

The oracle and parser are registered in `tools/external/tools.lock.json`
(10 tools total) under the repo's external-tool policy; the exe is a
hash-pinned dev-time artifact under the gitignored
`tools/external/installed/` path. No Rust at runtime — the no-Rust-dependency
rule is unchanged.
