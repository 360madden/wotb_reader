# Repo map

Annotated layout of the repository. Root files and folders only; per-module
detail lives in the module's own files. Full tree: `src/`, `tests/`, `tools/`,
`docs/` below.

## Root

| Path | Purpose |
|------|---------|
| `WotBTreader.sln` | Solution; 28 projects |
| `knowledge.md` | Agent knowledge: quickstart, architecture, conventions, gotchas |
| `AGENTS.md` | Agent entry: rules, task routing, delegation |
| `README.md` | Human quickstart |
| `*.cmd` | Convenience wrappers (`build`, `test`, `validate`, `serve`, `overlay`, `import`, …) — see [`commands.md`](commands.md) |
| `Directory.Packages.props` | Central package versions (lock files committed) |
| `global.json` | SDK pinned to 10.0.302 |
| `.gitignore` | Runtime-data patterns (`*.sqlite`, `dist/`, …) — case-insensitive on Windows |
| `memory-offsets/` | Versioned offset evidence (`11.18.0.7.json`, …) + `schema.json` |
| `research/` | Deep-dive research notes on the game's replay loading, IPC, memory layout, community tools (`research/README.md` is the index) |
| `docs/` | Canonical documentation (architecture, decisions, formats, operations, testing) |
| `scripts/` | `validate.ps1` (full gate), `scan-repository.ps1` (secrets/ignore scan), `ghidra-scan.py`, `invoke-cursor-agent.ps1`, `python/` smoke tooling |
| `tmpwotb-e2e/` | Local E2E logs (runtime data) |

## src/ — layered modules

```
Core (no project refs)
 └── Application → Core only
      ├── Replays        — .wotbreplay parsing (pickle data-only, protobuf, event stream)
      ├── CaptureLogs    — NDJSON telemetry capture logs, replay clocks, comparison
      ├── GameIntegration — installed-game discovery, DVPL, log monitoring, offline
      │                    session gate, guarded Win32, process launch
      ├── UltimateScanner — standalone Cheat Engine-like memory scan module
      │                    (multi-scan snapshot/compare, pattern scans, neighborhood
      │                    scans, guarded VM reader); referenced only by GameIntegration
      ├── Storage.Sqlite — SQLite storage (artifacts, decode runs, comparisons)
      └── Bootstrap      — composition root; all DI registration (hosts start via this)
           ├── Host.Cli  — console CLI (import, sessions, compare, export, watch, …)
           └── Host.Web  — Blazor web app, loopback-only, port 9182, SignalR, read+game APIs
ApiContracts (serialization-only; NO refs, NO packages)
 Overlay (net10.0-windows WPF HUD; loopback client; references ONLY ApiContracts)
```

### Modules (key files)

| Module | Key files |
|--------|-----------|
| `Core` | `Identifiers.cs`, `TelemetryModels.cs`, `OffsetModels.cs`, `ComparisonModels.cs`, `AffiliationResolver.cs` |
| `Application` | `Replay/ReplayIngestionService.cs`, `Results/OperationResult.cs`, `Storage/StorageContracts.cs`, `Game/GameSessionContracts.cs`, `Capture/CaptureContracts.cs` |
| `Replays` | `WotbReplayDecoder.cs`, `RestrictedPickleReader.cs`, `ProtobufWireReader.cs`, `EventStreamReader.cs`, `ReplayBinary.cs` |
| `CaptureLogs` | `Ndjson/NdjsonTelemetrySource.cs`, `Clock/SegmentedReplayClockSource.cs`, `Comparison/TelemetryComparator.cs` |
| `GameIntegration` | `Discovery/GameInstallationDiscovery.cs`, `Dvpl/DvplReader.cs`, `Logs/BlitzReplayLogMonitor.cs`, `Session/` (coordinator, process launch, identity) — delegates scanning to `UltimateScanner` |
| `UltimateScanner` | `MemoryScanEngine.cs` (multi-scan snapshot/compare), `MemoryScanDiscoverer.cs` (pattern + neighborhood scans), `GuardedMemoryReader.cs` (bounded VM reads), `NativeMethods.cs` (process-memory interop) |
| `Storage.Sqlite` | (SQLite repos: artifacts, decode runs, comparisons, replay clock segments) |
| `Bootstrap` | `DependencyInjection/FoundationServiceCollectionExtensions.cs`, `Startup/StorageInitializationHostedService.cs`, `Logging/TreaderLogging.cs` |
| `Host.Cli` | `Cli/CliCommandRouter.cs`, `Cli/CliEntryPoint.cs` |
| `Host.Web` | `Endpoints/ReadApiEndpoints.cs`, `Endpoints/GameApiEndpoints.cs`, `Hubs/TelemetryHub.cs`, `Services/DashboardReadClient.cs`, `Services/MinimapTextureService.cs` |
| `Overlay` | `Views/PositionPlot.xaml`, `ViewModels/MainViewModel.cs`, `Discovery/RendezvousLocator.cs`, `Services/TreaderApiClient.cs`, `Services/TelemetryStreamService.cs` |

## tests/

12 MSTest projects, including the module suites, architecture/bootstrap suites,
and the Windows-only `tools/tests/WotBTreader.GameHarness.Tests`. The shared
`TestSupport` project supplies synthetic fixtures but is not itself a test suite.
The current snapshot is 412 tests: 410 passed, 0 failed, and 2 local opt-in skips.
`Architecture.Tests` enforces the reference graph, TFM allowlist, and native-access
boundary.

## tools/

| Path | Purpose |
|------|---------|
| `src/WotBTreader.GameHarness/` | Developer harness CLI (scan, discover, start-game; `net10.0-windows`) |
| `src/WotBTreader.ReplayInspector/` | Replay inspection tool |
| `src/WotBTreader.ReplaySanitizer/` | Replay sanitization tool |
| `tests/WotBTreader.GameHarness.Tests/` | Harness containment tests |
| `cheat-engine/` | Approved local CE 7.7 Lua scripts (offline replay sessions only) |
| `ghidra-scripts/` | Ghidra offset discovery (`FindOffsets.py`/`.java`) |
| `external/` | External tool pins (`tools.lock.json`) |
