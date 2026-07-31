# Project Completion Roadmap

Last updated: 2026-07-31

Project context: the owner identifies as a junior developer at Wargaming.net.
This is a personal, independently maintained project; see
[Project context](project-context.md).

> This file is a historical feature-delivery ledger. A check mark means code
> was implemented; it does not mean the feature satisfies the current
> architecture, trust-boundary, or offline-safety gates. The active
> architecture hardening plan is
> [`architecture/roadmap.md`](architecture/roadmap.md).

## Implemented feature inventory

The following surfaces were implemented and validated at the time recorded;
their current architecture-hardening status is tracked separately. The inventory below is historical; the current test snapshot is recorded in the
matrix below and in the root README.

| Surface | Status | Tests |
|---------|--------|-------|
| Replay parsing (WotbReplayDecoder, ProtobufWireReader, pickle) | ✅ | 18 |
| Storage (SQLite: artifacts, decode runs, comparisons, migrations) | ✅ | 17 |
| CaptureLogs (NDJSON telemetry, replay clocks) | ✅ | 9 |
| GameIntegration (install discovery, DVPL, log monitoring, offline session gate) | ✅ | 118 |
| CLI (doctor, import, inspect, reprocess, sessions, compare, export, watch) | ✅ | 15 |
| Web host (loopback Blazor, read API with events, battle stats on session detail, SignalR hub, rendezvous) | ✅ | 61 |
| Overlay (WPF: session list, position plot, velocity trails, event feed, battle stats, time slider, playback controls, keyboard shortcuts, minimap grid, collapsible sidebar, SignalR push; WebView2 removed in M5) | ✅ | 91 |
| Overlay HTTP API (embedded Kestrel on port 9190, 8 automation endpoints) | ❌ Removed — listener and former handler/state files removed in M3 | — |
| Shared wire contracts (`ApiContracts`, zero project/package refs) | ✅ | — |
| Architecture enforcement (reference graph, TFM allowlist, native-access boundary) | ✅ | 15 |
| Composition root validation | ✅ | 13 |
| Developer harness tooling (GameHarness; gate-checked `scan`/`probe`/`discover*`) | ✅ | 28 |
| Codebase bug hunt (src + tests + tools) | ✅ | 1 fix |
| Performance optimization (DrawingVisual renderer, zero-GC) | ✅ | — |
| Session search/filter (overlay sidebar) | ✅ | — |
| Documentation (architecture, handoffs, BLK log, knowledge.md) | ✅ | — |

**Current snapshot (2026-07-31):** 411 tests — 409 passed, 0 failed, 2 skipped
(local opt-in) across 12 test projects. Build: 0 errors, 0 warnings.
Vulnerability audit: 0 vulnerable packages across all 27 projects. Repository scan
counts are intentionally not repeated here because the tracked file set changes as
offline evidence and documentation are added.

All eight original roadmap items are complete. Seven additional features were
implemented across autonomous sessions (2026-07-27 through 2026-07-28):

### Session 1 — HUD finalization

| Feature | Status |
|---------|--------|
| Transparent HUD window (borderless, topmost, drag-to-move) | ✅ |
| Game window tracking (P/Invoke FindWindowW/GetWindowRect/SetWindowPos) | ✅ |
| Game launching from HUD (find replay + launch wotblitz.exe) | ✅ |
| Game path auto-discovery (env var → default roots → fallback) | ✅ |
| Minimap projection with map boundary computation | ✅ |
| `compare create` CLI (wire TelemetryComparator) | ✅ |

### Session 2 — Autonomous (overlay analysis tools + web dashboard parity)

| Feature | Status |
|---------|--------|
| Velocity trails (fading polylines per participant on position plot) | ✅ |
| Session detail panel (participants, event count in overlay sidebar) | ✅ |
| Event feed (chronological event list with human-readable summaries) | ✅ |
| EventResponse DTO (API + overlay contracts, damage parsing, kind filtering) | ✅ |
| Time-slider scrubber (cumulative position playback, play/pause) | ✅ |
| Playback controls (speed cycle 0.5–8×, ⏮⏭ jump, loop mode) | ✅ |
| Battle stats in overlay (damage taken + kills per team) | ✅ |
| Battle stats + events table on web dashboard session detail page | ✅ |
| Minimap background grid (dashed reference lines + map name label) | ✅ |
| Sidebar opacity toggle (cycles 0.85→0.50→0.20) | ✅ |
| Keyboard shortcuts (Space/←→/1-5/Esc) | ✅ |
| Collapsible sidebar (shrink to controls-only strip) | ✅ |
| DrawingVisual renderer (zero-GC position plot, frozen brushes/pens) | ✅ |
| Session search/filter (case-insensitive map name filter in sidebar) | ✅ |

### Session 3 — Overlay HTTP automation API (2026-07-28) — ❌ superseded

> [!WARNING]
> This entire surface was **removed** during Milestone 0. The overlay no longer starts
> a listener and nothing binds port 9190; a second local mutation server duplicated
> control-plane policy. `Host.Web` is the single control plane. The table below is
> retained only as delivery history. `Endpoints/OverlayApiEndpoints.cs` and
> The former `OverlayApiEndpoints.cs` and `OverlayApiState.cs` files were deleted
> after the listener was removed. `Host.Web` is the only supported control plane.

| Feature | Status |
|---------|--------|
| Embedded Kestrel HTTP API on port 9190 (FrameworkReference, zero new packages) | ✅ |
| OverlayApiState thread-safe singleton (SynchronizationContext marshaling) | ✅ |
| GET /api/v1/status (connected, sessions, playback, game window) | ✅ |
| POST /api/v1/sessions/refresh | ✅ |
| POST /api/v1/launch (replay path → QuickLaunchWithPathAsync) | ✅ |
| POST /api/v1/playback/{play,pause,seek,speed} | ✅ |
| POST /api/v1/sessions/select | ✅ |
| Loopback-only security (IPAddress.IsLoopback on all write endpoints) | ✅ |
| OverlayApiEndpointsTests (16 tests) + OverlayApiStateTests (12 tests) | ✅ |

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

### 🔴 P0-b — End-to-end replay import → dashboard → overlay ✅

Full end-to-end test completed 2026-07-27:
- ✅ Synthetic replay created via `SyntheticReplayFactory` (990 bytes, 2 participants)
- ✅ CLI import: artifact `019fa367`, decode run `Succeeded`, 2 participants, 2 positions, 5 events
- ✅ CLI sessions: 1 session listed with correct counts
- ✅ Web host API: `GET /api/v1/sessions` returns 1 item with correct metadata
- ✅ Dashboard: session appears on home page ("synthetic-map", 2 participants, 2 positions)
- ✅ Session detail page: metadata, participants, positions all render
- ✅ Comparisons page: empty state still correct
- ✅ Browser console: zero errors across all pages
- ✅ Overlay: launches cleanly, discovers host via rendezvous
- 🔧 Bash backslash caveat: use forward slashes (`C:/tmp/...`) when passing Windows
  paths from bash shells — backslashes get eaten as escape characters

### 🟡 P1 — `compare` CLI command ✅

The storage layer fully supports comparison runs (`SqliteComparisonRunRepository`),
but the CLI returned `UnsupportedCapability` for `compare`. Now fully implemented.

- ~~Wire `IComparisonRunRepository` into the CLI command router~~ ✅
- ~~`compare list` — list existing comparison runs~~ ✅ (with `ListAsync` on repo)
- ~~`compare create <leftId> <rightId>` — create a new comparison~~ ✅ (`CompareCreateAsync`)
- ~~`compare inspect <comparisonId>` — show comparison details~~ ✅
- **Done:** all three subcommands (`list`, `create`, `inspect`) are implemented.
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

## Architecture hardening (M0–M7) — COMPLETE

All seven architecture hardening milestones were completed between 2026-07-28 and
2026-07-30. The full plan is in [`docs/architecture/roadmap.md`](architecture/roadmap.md).

| Milestone | Commit | Summary |
|-----------|--------|---------|
| M0 — Baseline | (early) | Disabled unsafe auto-attach, restored ACL, binder guard |
| M1 — Boundaries | (early) | Portable TFMs, enforced reference graph, ApiContracts |
| M2 — Game access | `b2ed7b7..dc84f39` | Suspended process, correlation, thread resume, VM-read |
| M3 — Control plane | `bf71cee` | Deleted dead endpoints, hardened mutations, capability wiring |
| M4 — Offset evidence | `8f48432` | Offset models, hash enforcement, publication separation, orphan reconciler |
| M5 — Focused HUD | `6b71cc8` | Removed WebView2, host startup, game launch, import (−685 lines) |
| M6 — Operability | `e67e0db` | Port conflict detection, orphaned host PID check |
| M7 — Release gate | `e67e0db` | 0 vulnerabilities, roadmap reconciled, all gaps closed; v0.1.0-alpha tagged |

Full pipeline smoke test passed on 2026-07-30: synthetic replay generated → imported
via CLI → published → served → all API endpoints verified → overlay launched without crash.

## Deferred / future work

- **Live HUD smoke test**: Verify transparent window, session list, position
  dots, velocity trails, time slider, keyboard shortcuts, and Launch button
  against a real WoT Blitz installation.
- ~~Real minimap textures~~ ✅: `MinimapTextureService` serves minimap PNG textures
  from the installed game's DVPL-encapsulated WebP files via `GET /api/v1/maps/{mapId}/minimap`.
  `TreaderApiClient.GetMinimapPngAsync` fetches them on the overlay, `MainViewModel.LoadMinimapAsync`
  caches and renders them via `PositionPlot.MinimapImage`. SkiaSharp converts WebP→PNG with
  90-quality encoding. Cache invalidated on game version change. Fully implemented.
- **Game path via DI**: The overlay's game path discovery is a lightweight
  replica of `GameInstallationDiscovery`. A future refactor could extract
  discovery into a shared portable utility.
- **Dynamic offset discovery**: Cheat Engine-like multi-scan engine (`MemoryScanEngine`)
  with snapshot/compare/filter (changed/unchanged/increased/decreased). Neighborhood scanner
  reads memory windows around known offsets (`MemoryScanDiscoverer.ScanNeighborhood`).
  `POST /api/v1/game/discover` plus snapshot/compare/neighborhood/session endpoints.
  `GameHarness` CLI: `discover`, `discover-snapshot`, `discover-compare`,
  `discover-nearby`, `discover-discard`. The 11.19.0.10 table is hash-bound to the
  installed executable; only `playerYaw` is a static-analysis `Candidate`, and seven
  fields remain unknown. Candidate evidence is not runtime-supported.

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

### 🔵 P7 — Convenience .cmd wrappers ✅

14 `.cmd` wrappers in repo root, all runnable from any directory:
- Build: `build`, `validate`, `test`
- Runtime: `serve`, `overlay`, `everything` (one-shot launch)
- CLI: `import`, `watch`, `sessions`, `doctor`, `compare`, `export`, `treader`
- Bug fixes: serve.cmd restore check, treader.cmd --data-root order
- **Done:** scan clean, all 371 files pass.

### 🔵 P8 — Startup sequence documentation ✅

- `knowledge.md`: new Startup Sequence section with 1-2-3 flow diagram
- `everything.cmd`: launches serve + overlay in separate windows
- Wrapper headers updated with sequence hints
- **Done:** committed `7641676`.

### 🔵 P9 — Everything.cmd smoke test ✅

Full visual smoke test completed 2026-07-27:
- ✅ Synthetic replay imported into `.data/` (990 bytes, 2 participants, 2 positions)
- ✅ Web host started on port 9182 (storage migration v3, rendezvous published)
- ✅ API returns 1 session: "synthetic-map", 2 participants, 2 positions, 5 events
- ✅ Session detail: participants (pilot-a/TAG/team-1, unit-b/team-2), positions (2 samples with coords)
- ✅ All pages return 200: home, /comparisons, /diagnostics
- ✅ Overlay launches and is responding (PID 35084, Responding=True)
- 🔧 Note: `%~dp0` does not expand in bash — use absolute Windows paths with forward slashes
- 🔧 Note: database is `treader.db` (not `.sqlite`) — the earlier glob missed it
- 🔧 Note: `/api/v1/comparisons` returns 404 (expected — comparisons are Blazor SSR via IDashboardReadClient, not exposed on the read API)

**Evidence:** API responses, HTTP status codes, process table all captured.

## Action plan (this session)

1. ✅ Create this roadmap document
2. ✅ Implement P1: `compare` CLI command
3. ✅ Implement P2: `export` CLI command
4. ✅ Implement P5: Comparisons dashboard page
5. ✅ Validate, code review, commit, push
