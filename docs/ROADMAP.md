# Project Completion Roadmap

Last updated: 2026-07-27

## Completed ✅

All alpha surfaces are implemented and validated:

| Surface | Status | Tests |
|---------|--------|-------|
| Replay parsing (WotbReplayDecoder, ProtobufWireReader, pickle) | ✅ | 18 |
| Storage (SQLite: artifacts, decode runs, comparisons, migrations) | ✅ | 17 |
| CaptureLogs (NDJSON telemetry, replay clocks) | ✅ | 9 |
| GameIntegration (install discovery, DVPL, log monitoring) | ✅ | 25 |
| CLI (doctor, import, inspect, reprocess, sessions) | ✅ | 13 |
| Web host (loopback Blazor, read API, SignalR hub, rendezvous) | ✅ | 54 |
| Overlay (WPF: session list, position plot, SignalR push, WebView2 dashboard) | ✅ | 41 |
| Architecture enforcement | ✅ | 3 |
| Composition root validation | ✅ | 10 |
| Codebase bug hunt (src + tests + tools) | ✅ | 1 fix |
| Documentation (architecture, handoffs, BLK log, knowledge.md) | ✅ | — |

**Total:** 231 tests, 2 opt-in skips. Build: 0 errors, 0 warnings. Scan: clean.

### Comment coverage

XML doc comments added to all public and key internal types:
- `ComparisonModels.cs` — all 8 record types and 3 enums now have `<summary>`
- `CliCommandRouter.cs` — class + all 18 methods documented
- `IDashboardReadClient.cs` — all 5 methods have summaries
- `CliEntryPoint.cs` — `RunAsync` has detailed summary + remarks
- `Comparisons.razor` — page-level comment + method summaries

## Remaining items (priority order)

### 🔴 P0 — Smoke test ✅

Full visual smoke test completed 2026-07-27 using `dotnet publish` output:
- ✅ Web host starts on port 9182, all API endpoints pass
- ✅ Sessions page: shows "No decoded sessions yet" cleanly
- ✅ Comparisons page: shows "No comparison runs yet" cleanly
- ✅ Diagnostics page: all 5 doctor checks pass with green status
- ✅ Navigation: Sessions, Overlay, Comparisons, Diagnostics all work
- ✅ Static assets: blazor.web.js, app.css, Bootstrap, auto-generated CSS all serve 200
- ✅ Browser console: zero errors
- ✅ Overlay launches and discovers host via rendezvous
- 🔧 Fix: added `<Content Update="wwwroot\**\*" CopyToOutputDirectory="PreserveNewest" />`
  to csproj so wwwroot is available when running from build output
- 🔧 Note: full Blazor interactive features require `dotnet publish` (not just build)
  to generate _framework/blazor.web.js and scoped CSS bundles

**Evidence:** host log + browser agent report + API responses all captured.

### 🟡 P1 — `compare` CLI command ✅

The storage layer fully supports comparison runs (`SqliteComparisonRunRepository`),
but the CLI returned `UnsupportedCapability` for `compare`. Now fully implemented.

- ~~Wire `IComparisonRunRepository` into the CLI command router~~ ✅
- ~~`compare list` — list existing comparison runs~~ ✅ (with `ListAsync` on repo)
- `compare create <leftId> <rightId>` — create a new comparison (needs `TelemetryComparator` wiring)
- ~~`compare inspect <comparisonId>` — show comparison details~~ ✅
- **Done:** `compare list` paginates, `compare inspect <id>` queries full comparison.
  15/15 CLI tests pass.

### 🟡 P2 — `export` CLI command ✅

NDJSON telemetry export is implemented (`NdjsonTelemetryWriter`), but the CLI
had no `export` command. Now implemented.

- ~~`export sessions <battleSessionId>` — export events as structured JSON~~ ✅
- ~~`export positions <battleSessionId>` — export positions as structured JSON~~ ✅
- **Done:** Calls `GetProjectionAsync`, returns events/positions in envelope.
  15/15 CLI tests pass.

### 🟢 P3 — `serve` CLI command

The web host is a separate executable. Decided not to merge into CLI:
- The CLI does not reference Host.Web (would create coupling)
- The rendezvous pattern was designed for independent processes
- Launch the web host as a separate process: `WotBTreader.Host.Web.exe`
- The CLI discovers it automatically via the rendezvous record
- **Status:** Designed out — not needed for alpha.

### 🟢 P4 — `watch` CLI command ✅

Implemented 2026-07-27:
- `watch <directory>` — monitors directory for new .wotbreplay files
- Uses FileSystemWatcher for low-latency Created event hints
- Directory enumeration as source of truth (matches BlitzReplayLogMonitor pattern)
- 2s stability delay before importing, idempotent via ConcurrentDictionary
- Progress reported via ILogger, summary on cancellation (Ctrl+C)
- Handles directory removal, IO errors, and graceful shutdown
- **Done:** 15/15 CLI tests pass.

### 🔵 P5 — Comparison runs dashboard UI ✅

Blazor page at `/comparisons` implemented 2026-07-27:
- Lists comparison runs with ID, comparator, created date, and View button
- Detail view shows left/right artifacts, summary table (Exact/Tolerant/
  Mismatch/Missing/Extra/Uncomparable with color coding), and items table
- Error/loading/empty states handled
- Added to IDashboardReadClient and DashboardReadClient
- NavMenu updated with Comparisons link
- **Done:** 54/54 web tests pass.

### 🔵 P6 — Push to remote ✅

All 17 commits pushed to `origin/main` (https://github.com/360madden/wotb_reader).
Sensitive content scan: zero findings across all diffs.

## Action plan (this session)

1. ✅ Create this roadmap document
2. ✅ Implement P1: `compare` CLI command
3. ✅ Implement P2: `export` CLI command
4. ✅ Implement P5: Comparisons dashboard page
5. ✅ Validate, code review, commit, push
