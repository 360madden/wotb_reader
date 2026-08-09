# External tool policy

The alpha starts with built-in .NET, Win32/WinRT, installed Windows SDK tools,
and Windows Performance Recorder. Add an executable tool only after documenting
a concrete capability failure.

Before downloading a tool, register its exact version, canonical source URL,
SHA-256, SPDX license, purpose, and supported platform in `tools.lock.json`.
Binaries go under ignored `tools/external/installed/`; never download or update
them at application runtime.

## Registered tools

The authoritative registry is `tools.lock.json` (10 tools: x64dbg, System
Informer, ReClass.NET, ILSpy, Ghidra, OpenCode, PSScriptAnalyzer,
wotbreplay-inspector, wotbreplay-parser, Grok Build).

Two of the registered tools participate in repo quality gates and are
documented below: PSScriptAnalyzer (PowerShell static analysis) and the
wotbreplay pair (independent replay-decode cross-validation).

PowerShell scripts in this repo must pass the PSScriptAnalyzer gate before
landing. `scripts/install-psscriptanalyzer.ps1` downloads the pinned, hash-
verified module into `tools/external/installed/`; `scripts/invoke-scriptanalyzer.ps1`
runs the gate (settings + repo custom rules). Both are wired into
`scripts/validate.ps1` and CI.

## Replay-decode cross-validation (wotbreplay-inspector + wotbreplay-parser)

The C# `Replays` adapter decodes `.wotbreplay` files (zip archive,
`data.wotreplay` pickle stream, `battle_results.dat` protobuf). To prove that
decoding is correct rather than merely self-consistent, the repo cross-checks
it against a **completely independent implementation**: the Rust
`wotbreplay-inspector` CLI built on eigenein's `wotbreplay-parser` crate
(v0.4.2). The parser source doubles as a schema reference and ships real
replay fixtures with published expected values.

- `scripts/invoke-replay-crosscheck.ps1` runs both decoders on the same
  `.wotbreplay` and compares the cross-check surface: battle timestamp,
  participant set (account/name/team), and packet clock sequence. Exit 0 =
  agree, 1 = disagree, 2 = oracle/inspector missing, 3 = decode failed.
- `-GoldenVector` validates the Rust oracle against the parser's published
  expected values (fixture `20221203_player_results.wotbreplay`).
- **Known, documented divergences** (noted, not failed): bot-account
  sentinels (Rust truncates the wire varint to uint32; C# deliberately
  rejects sign-extended IDs as unverifiable identity) and battle-time source
  (Rust reads server-recorded protobuf tag 2; C# prefers client-recorded
  `meta.json` `battleStartTime`; a small delta is a clock artifact).
- **No Rust at runtime**: the exe is a hash-pinned dev-time artifact only,
  exactly like Python/Ghidra/x64dbg. The repo's no-Rust-dependency rule is
  unchanged.

The parser's `read_quirky_length` framing (`FF <len:u16-LE> 00` for payloads
>254 bytes) was verified against the C# `ReadQuirkyLength` in
`EventPacketDecoders.cs` — both implementations agree byte-for-byte, and the
C# side independently discovered the same framing.
