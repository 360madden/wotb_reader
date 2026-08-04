# Operator-run replay cross-check validation

The C# `Replays` decoder is the product's core capability, so its correctness
must be proven against an **independent implementation**, not just its own
tests. `scripts/invoke-replay-crosscheck.ps1` runs the C# decoder and the Rust
`wotbreplay-inspector` oracle (built on eigenein's `wotbreplay-parser` 0.4.2)
on the same `.wotbreplay` and compares the cross-check surface: battle
timestamp, participant set (account/name/team), and packet clock sequence.

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
| 0 | Both decoders agree on battle time, participants, clocks | Proceed |
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
  large real battle replays instead.

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

## Registry

The oracle and parser are registered in `tools/external/tools.lock.json`
(11 tools total) under the repo's external-tool policy; the exe is a
hash-pinned dev-time artifact under the gitignored
`tools/external/installed/` path. No Rust at runtime — the no-Rust-dependency
rule is unchanged.
