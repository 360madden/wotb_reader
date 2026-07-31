# Offset discovery walkthrough

End-to-end flow for discovering game memory offsets. Canonical detail:
[`docs/operations/offset-discovery-guide.md`](../docs/operations/offset-discovery-guide.md)
(4-phase Ghidra/x64dbg/ILSpy/CE pipeline) and
[`memory-offsets/README.md`](../memory-offsets/README.md) (three-phase workflow).

## What offsets are

Versioned, module-relative offsets into the game process that let the reader
observe live replay state (replay time, player HP/position/yaw/pitch, alive
tank count). Evidence lives in `memory-offsets/<gameVersion>.json`, validated
against `memory-offsets/schema.json`. 8 fields, each with an expected type:

| Field | Type |
|-------|------|
| `replayTime` | double (seconds) |
| `playerHP` | int32 |
| `playerPositionX/Y/Z` | float (world units / height) |
| `playerYaw` | float (radians) |
| `cameraPitch` | float (radians) |
| `aliveTankCount` | int32 |

`0` = unknown. `confidence`: none/low/medium/high. The offset file is read by
`Application/Replay/OffsetTableReader.cs` and consumed in
`GameSessionCoordinator` (which refuses memory reads without a known,
version-matched offset table — `HasKnownOffsets`).

## Software flow (what the app does)

```
serve (web host on 127.0.0.1:9182, loopback)
  ├─ POST /api/v1/game/start        → GameProcessLauncher (plain wotblitz.exe launch, no replay)
  ├─ POST /api/v1/game/launch       → GameSessionCoordinator M2 suspended-process pipeline
  │    (prepare → executable lease → artifact staging → suspended process →
  │     correlation → resume) and lifecycle evidence → OfflineReplayVerified gate
  ├─ GET  /api/v1/game/state        → gate check (OfflineReplayVerified required for scans)
  ├─ POST /api/v1/game/discover                    → single known-value scan
  ├─ POST /api/v1/game/discover/snapshot           → snapshot all committed memory
  ├─ POST /api/v1/game/discover/compare/{sessionId} → compare vs snapshot (changed/unchanged/increased/decreased)
  ├─ POST /api/v1/game/discover/neighborhood       → scan a window around a known offset
  └─ DELETE /api/v1/game/discover/session/{sessionId} → discard a snapshot session
```

- Scanner: `GameIntegration/Session/MemoryScanEngine.cs` (snapshot/compare) +
  `MemoryScanDiscoverer.cs` (pattern scans, neighborhood scans), surfaced
  through `IGameMemoryScanner` on `GameSessionCoordinator`.
- **Safety gate:** every discover command requires an `OfflineReplayVerified`
  session (`GET /api/v1/game/state`); never scan an online match.

## GameHarness CLI (tools/src/WotBTreader.GameHarness)

All commands discover the host via the rendezvous file and check the gate:

| Command | What it does |
|---------|--------------|
| `start` / `start-game` | POST `/api/v1/game/start` — plain game launch (no replay) |
| `state` | Show saved scanner state (read-only, local `ScannerStateStore`) |
| `scan` | Gate check + offset field status (X/Y fields known) |
| `probe` | Gate check + field status + raw offset table |
| `discover <field> <Float\|Int32\|Double> <value> [tolerance]` | Known-value scan; tolerance → mantissa wildcard mask (0.01→1, 0.1→2, 1.0→3 bytes) |
| `discover-snapshot [valueSize] [--float-min/--float-max/--int-min/--int-max]` | Snapshot of committed memory; prints session id |
| `discover-compare <sessionId> [changed\|unchanged\|increased\|decreased]` | Compare current memory vs snapshot |
| `discover-nearby <refOffset> [--window <bytes>]` | Neighborhood scan around a known offset |
| `discover-discard <sessionId>` | Discard a snapshot session |

Run it like: `dotnet run --project tools/src/WotBTreader.GameHarness -c Release -- discover playerYaw Float 1.57 0.1`
(from a directory with a `memory-offsets/` folder for the offset-status commands).

## Evidence publication

1. Discover candidate offsets (Ghidra `FindOffsets.py`/`.java`,
   `tools/cheat-engine/*.lua`, or the scanner flow above).
2. Update `memory-offsets/<gameVersion>.json` — all 8 fields, set
   `confidence`, `executableSha256` (SHA-256 of wotblitz.exe for exact
   matching), `discoveredAtUtc`, `notes`.
3. Normalize and publish conservatively with `tools/discover-offsets.ps1`.
   It accepts both `autoDiscover()` (`fieldResults`) and legacy
   `saveDiscovered()` (`fieldName` + `candidates`) output. Only exactly one
   valid candidate for a known field is written, always as `Candidate`; ambiguous
   results remain report-only. Use `tools/report-offset-evidence.ps1` for a
   read-only status summary.
4. Validate: `scripts/python/offset_check.py` checks schema compliance
   (format, sha256, filename↔gameVersion match, plausibility, confidence).
   `memory-offsets/scanner-state.json` is generated runtime state — never commit.
5. Verify end-to-end: serve → overlay → `GET /api/v1/game/memory` returns
   non-null values only after the exact executable hash and complete promotion
   evidence are present.

Current state (2026-07-31): game 11.19.0.10, only the static-analysis
`playerYaw` candidate is recorded; 7 fields remain unknown. Full tool status in
[`docs/operations/offset-discovery-guide.md`](../docs/operations/offset-discovery-guide.md).

## Hard rules

- **Offline only.** Gate must be `OfflineReplayVerified`; never during an
  online match.
- Cheat Engine 7.7 is an approved local diagnostic tool for offline replay
  sessions only.
- Never commit scan files, memory dumps, pointer maps, or game-derived data.
- `memory-offsets/` evidence files are committed; `scanner-state.json` is not.
