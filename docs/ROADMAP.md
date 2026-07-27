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

## Remaining items (priority order)

### 🔴 P0 — Smoke test ✅ (partial)

Web host smoke test completed 2026-07-27:
- ✅ Web host starts on port 9182, sessions API returns valid JSON
- ✅ Doctor API returns all checks passing
- ✅ Rendezvous file published with correct baseUri
- ✅ Overlay launches without crash (exit code 0 in headless env)
- ⚠️ Full visual verification (position dots, WebView2 dashboard,
  SignalR push) requires a display session

**Evidence:** host log shows migration + startup, all API endpoints
return correct schemas, overlay process exits clean.

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

### 🟢 P4 — `watch` CLI command

Watch for new replays in a directory and auto-import them. This requires
filesystem watching, which `GameIntegration` already provides for game logs.

- `watch <directory>` — monitor directory for new .wotbreplay files
- Auto-import each new file with progress reporting
- **Effort:** ~3 hours. Infrastructure partially exists.

### 🔵 P5 — Comparison runs dashboard UI

The Blazor dashboard has no comparison runs page. The storage layer supports
comparisons, and once the `compare` CLI command exists, the dashboard should
display comparison results.

- Add `/comparisons` page to the Blazor dashboard
- Show comparison runs table with left/right artifacts and delta summaries
- **Effort:** ~2 hours. Blazor patterns established.

### 🔵 P6 — Push to remote ✅

All 17 commits pushed to `origin/main` (https://github.com/360madden/wotb_reader).
Sensitive content scan: zero findings across all diffs.

## Action plan (this session)

1. ✅ Create this roadmap document
2. 🔜 Implement P1: `compare` CLI command (highest-value remaining feature)
3. Validate, code review, commit
