# Handoff — Replay-decode cross-validation oracle (2026-08-04)

## Sessions worked

- Integrated **both** community assets: the Rust `wotbreplay-inspector` CLI
  (built on eigenein's `wotbreplay-parser` 0.4.2) as an independent decode
  oracle, and the parser source snapshot as a schema reference + golden-vector
  corpus.
- Registered both in `tools.lock.json` (11 tools total), staged the inspector
  exe as a hash-pinned dev-time artifact (gitignored `installed/` path),
  wrote `scripts/invoke-replay-crosscheck.ps1`, and ran it across all four
  `.data/launch` replays plus the golden-vector mode.
- Verified the parser's quirky-length pickle framing against the C# decoder's
  `ReadQuirkyLength` — identical, no fix needed.

## What shipped

| Artifact | Purpose | Status |
|---|---|---|
| `tools/external/tools.lock.json` | `wotbreplay-inspector` (sha256-pinned exe) + `wotbreplay-parser` 0.4.2 (source-reference) entries | New entries, JSON-valid |
| `tools/external/installed/wotbreplay-inspector/wotbreplay-inspector.exe` | The oracle binary (gitignored, hash-pinned) | Staged, smoke-verified |
| `scripts/invoke-replay-crosscheck.ps1` | Runs both decoders on one `.wotbreplay`, compares battle timestamp + participants + packet clocks; `-GoldenVector` validates the oracle against parser fixtures; exit 0 agree / 1 disagree / 2 missing / 3 decode-failed | Passing on 4 real replays + golden vectors |
| `tools/external/README.md` | Cross-validation section: surface, known divergences, no-Rust-at-runtime stance | Updated |
| `knowledge.md` | Durable note on the cross-check + divergences | Updated |

## Findings the oracle produced

1. **Bot-account sentinels**: Rust reads raw uint32 sentinels
   (`4294967290`–`4294967295` = 0xFFFFFFF6–0xFFFFFFFF) for bot accounts; C#
   deliberately rejects the sign-extended 64-bit wire varint as an
   unverifiable identity (evidence-first). Harness normalizes this known
   difference and still hard-fails on real divergences.
2. **Battle-time source**: `meta.json` `battleStartTime` (client) vs
   `battle_results.dat` protobuf tag 2 (server) differed by 9s on one real
   battle. Neither is wrong — a client/server clock artifact. Recorded as a
   note, not a failure; >60s delta still hard-fails.
3. **Quirky-length framing**: parser's `read_quirky_length`
   (`FF <len:u16-LE> 00`) matches C# `ReadQuirkyLength` byte-for-byte. The
   parser's doc comment says `00 <XX XX> FF` but its code reads `FF <len> 00`
   — same as C#. No decoder change needed.

## Tests and validation run

- Cross-check all 4 `.data/launch` replays: **all AGREE** (same battle time,
  participants, packet clocks). One replay required a long timeout (28k-event
  C# inspector run ~ minutes).
- Golden-vector: `20221203_player_results.wotbreplay` reproduces the parser's
  published expected values (timestamp `1670083956`, 14 players,
  `yuranhik_hustriy26`) — **GOLDEN PASS**.
- PSScriptAnalyzer gate not yet re-run for the new script (pending final
  validation step).

## Assumptions and unknowns

- The parser fixtures are 9.4–10.1 era — older than the C# decoder's
  11.18/11.19 version gate — so golden vectors validate the **Rust oracle**
  itself, not the C# decoder. Cross-checks must use 11.18/11.19 replays
  (the four `.data/launch` ones).
- The exe is a debug build of an un-versioned `0.0.0` crate; provenance is
  documented as "locally built from snapshot @ 7659570b" so a rebuild is
  reproducible (rebuild instructions are in the registry entry).
- The C# ReplayInspector surfaces counts, not clocks; packet-clock comparison
  is currently Rust-side only (`report.cs.packets` is informational).

## Integration risks

- **No CI change**: cross-checks need real (non-synthetic) replays, so the
  oracle is an operator-run validation step, matching the offline-validation
  tool policy. The repo's no-Rust-dependency rule is unchanged.
- The cross-check script hard-fails (exit 2) if the oracle exe is missing —
  intended: the decoder must not be validated against itself only.

## Recommended next steps

- Wire the cross-check into a future full-gate operator checklist (not CI).
- Optionally extend C# `BattleResultsReader` with the parser's mapped
  author/player-stat tags (damage, XP, credits, mm_rating) — the schema
  reference now makes that a port, not a hunt.
- Re-run the cross-check after any future decoder or format-version change.
