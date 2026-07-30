# Session handoff — 2026-07-30: Dynamic memory offset scanner

**Author:** Codex Agent  
**Branch:** `main`  
**Commits:** `38b6acc` (12 files, +760/-7)  
**Tests:** 375 passed, 0 failed, 2 skipped across all 12 projects  
**Build:** 0 errors, 0 warnings (Release)  
**Scan:** 464 files clean

---

## What was accomplished this session

### Dynamic memory offset scanner — full pipeline

Built a complete memory region scanner for discovering unknown game offsets
at runtime. The scanner enumerates committed memory regions via `VirtualQueryEx`,
reads them in 64KB chunks with `ReadProcessMemory` (GCHandle pattern), and
searches for specific float/int32/double byte patterns with tolerance masks.

The pipeline flows through the existing security boundary:

```
GameHarness discover CLI
  → rendezvous → POST /api/v1/game/discover
    → GameApiEndpoints (hex validation, field-type routing)
      → IGameMemoryScanner.ScanAsync() (gate check)
        → MemoryScanDiscoverer.Scan() (VirtualQueryEx + ReadProcessMemory)
          → returns candidate addresses with module-relative offsets
```

### New files (2)

| File | Purpose |
|------|---------|
| `Session/MemoryScanDiscoverer.cs` | Core scanner: enumerates committed non-image regions, scans for byte patterns with tolerance masks, returns candidates |
| `ApiContracts/OffsetDiscoveryContracts.cs` | `OffsetDiscoveryRequest`, `OffsetDiscoveryResponse`, `OffsetDiscoveryCandidate` DTOs |

### Modified files (10)

| File | Change |
|------|--------|
| `Application/Game/GameSessionContracts.cs` | Added `IGameMemoryScanner`, `MemoryScanRequest`, `MemoryScanResult`, `MemoryScanCandidate` — all Application-layer types |
| `Session/GameSessionCoordinator.cs` | Implements `IGameMemoryScanner`; injects `MemoryScanDiscoverer`; `ScanAsync()` checks gate → delegates to scanner via `Task.Run` |
| `Session/WindowsGameProcessQueryPlatform.cs` | Added `VirtualQueryEx` P/Invoke, `MemoryBasicInformation` struct, removed duplicate `ReadProcessMemory` |
| `DependencyInjection/GameIntegrationServiceCollectionExtensions.cs` | Registers `MemoryScanDiscoverer` and `IGameMemoryScanner` |
| `Host.Web/Endpoints/GameApiEndpoints.cs` | `POST /api/v1/game/discover` — hex validation, tolerance mask, field-type routing, result serialization |
| `WotBTreader.GameHarness/Program.cs` | `discover <field> <type> <value> [tolerance]` CLI command with float tolerance mask heuristic |
| `Architecture.Tests/NativeAccessBoundaryTests.cs` | `MemoryScanDiscoverer.cs` added to VM-read allowlist |
| `Bootstrap.Tests/CompositionRootTests.cs` | `IGameMemoryScanner` added to published ports + `ReferenceEquals` check |
| `GameIntegration.Tests/GameIntegrationRegistrationTests.cs` | `IGameMemoryScanner` registration verified |
| `GameIntegration.Tests/GameSessionCoordinatorTests.cs` | Factory method updated with `MemoryScanDiscoverer` parameter |

### API endpoint

```
POST /api/v1/game/discover
{
  "fieldName": "playerPositionX",
  "fieldType": "Float",
  "expectedValueHex": "00002A42",    // 42.5f LE hex
  "toleranceMaskHex": "0000FFFF",     // wildcard bytes 0-1 (optional)
  "maxCandidates": 200,
  "minRegionSize": 4096
}

→ {
  "completedAtUtc": "...",
  "baseAddress": "0x7FF600000000",
  "regionsScanned": 847,
  "bytesScanned": 524288000,
  "totalMatchesBeforeTruncation": 0,
  "candidates": [
    {
      "absoluteAddress": "0x7FF60345A210",
      "relativeOffset": "0x0345A210",
      "relativeOffsetDecimal": 54870544,
      "observedValueHex": "00002A42",
      "valueSummary": "42.500"
    }
  ]
}
```

### GameHarness discover command

```
dotnet run -- discover playerPositionX Float 42.5 1.0
dotnet run -- discover playerHP Int32 1200
dotnet run -- discover playerYaw Float 1.57 0.1
```

Float tolerance masks: ±0.01→1 wildcard byte, ±0.1→2, ±1.0+→3.

### Code review findings — all addressed

| Finding | Resolution |
|---------|-----------|
| `Marshal.UnsafeAddrOfPinnedArrayElement` returns `ref T`, not pointer | Rewrote to `GCHandle.Alloc` + `AddrOfPinnedObject()` pattern (matches `GuardedMemoryReader`) |
| `DateTimeOffset.UtcNow` bypasses `_timeProvider` | Changed to `_timeProvider.GetUtcNow()` |
| `_scanDiscoverer` created with `new` instead of DI injection | Registered in DI, injected via constructor |
| `CompositionRootTests` missing `IGameMemoryScanner` | Added to published ports array + `ReferenceEquals` check |
| `NativeAccessBoundaryTests` allowlist missing scanner | Added `MemoryScanDiscoverer.cs` to VM-read allowlist |
| `GameIntegrationRegistrationTests` missing `IGameMemoryScanner` | Added registration assertion |
| Float tolerance ±0.01 with 0 wildcards → exact match (impossible for IEEE 754) | Minimum 1 wildcard byte for all tolerance levels |
| `Math.Min` ambiguous between `decimal` and `float` | Replaced with ternary: `remaining >= ReadChunkSize ? ReadChunkSize : (int)remaining` |
| `CA1305` culture-specific formatting | Added `CultureInfo.InvariantCulture` to all `ToString()` calls |

### Game binary status

WoT Blitz is still **11.19.0.10** with SHA256 `1cda5c31919c9784a41bee7f3270ec1b4536b124c51e8b36f2221b381760307d`.
The existing offset file (`playerYaw=0x0317A810`) is valid. No migration needed.

---

## Unresolved

1. **`Scan()` doesn't accept `CancellationToken`** — the coordinator wraps it in
   `Task.Run(() => ..., cancellationToken)` but the scan loop itself never checks
   the token. A long scan (hundreds of MB) would run to completion after
   cancellation. Pass the token through or check periodically in the region loop.

2. **Float tolerance mask heuristic is coarse** — the byte-count heuristic
   (±0.01→1 wildcard, ±0.1→2, ±1.0+→3) doesn't account for exponent-dependent
   precision differences. A more accurate approach would compute the mask from
   the actual float bit patterns by adding/subtracting the tolerance.

3. **No multi-scan filtering** — the scanner finds ALL matching addresses in a
   single pass. Multi-scan filtering (scan at T1, wait for movement, scan at T2,
   intersect candidates that changed correctly) would dramatically reduce false
   positives. This is the most important follow-up for practical offset discovery.

4. **E2E not smoke-tested with live game** — the full pipeline (publish → serve
   → launch replay → gate → discover) has not been run against a real game
   process. The gate check and API return correct errors when the gate isn't
   satisfied, but the actual `ReadProcessMemory` against wotblitz.exe with real
   replay position data has not been exercised.

---

## Recommended resume steps

1. **Smoke-test the discover pipeline** — publish, serve, import replay, launch
   game, verify gate reaches `OfflineReplayVerified`, then run `discover
   playerPositionX Float <known X> 1.0` against the running game.

2. **Add multi-scan filtering** — the most impactful feature for practical
   offset discovery. The coordinator already polls memory (via
   `MemoryObservationPublisher`). Extend `discover` to accept two expected values
   (T1 and T2) and return only candidates that changed from the first to the second.

3. **Pass CancellationToken into Scan()** — add token checks in the region
   enumeration loop and chunk-scanning loop.

4. **Update knowledge.md** with the new test count (375 passed), the discover
   pipeline architecture, and the `MemoryScanDiscoverer` allowlist entry.
