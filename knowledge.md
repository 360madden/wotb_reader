# Project knowledge

WotB Treader is a **Windows-first offline replay telemetry reader** for World of Tanks Blitz. It parses replay evidence, stores versioned telemetry projections, and presents a local Blazor dashboard + WPF/WebView2 overlay with SignalR push-based updates.

- **Stack:** .NET 10 (C#), WPF, ASP.NET Core Blazor Web App, SQLite, SignalR
- **No:** Python, Node.js, Rust, Electron, containers, cloud services, runtime AI, dynamic decoder DLLs

## Quickstart

**Requirements:** Windows 10/11, .NET SDK 10.0.302, Edge WebView2 Runtime (for overlay dashboard tab)

```powershell
dotnet restore WotBTreader.sln --locked-mode
dotnet build WotBTreader.sln -c Release --no-restore
dotnet test WotBTreader.sln -c Release --no-build
```

**Full gate (run before milestone commits):**
```powershell
./scripts/validate.ps1                     # locked restore → format → build → test → scan
./scripts/validate.ps1 -AuditPackages      # above + transitive vulnerability audit
```

**Single test project:**
```powershell
dotnet test tests/WotBTreader.Core.Tests -c Release
dotnet test tests/WotBTreader.Core.Tests -c Release --filter "FullyQualifiedName~SomeTest"
```

- Tests are MSTest 4 on Microsoft.Testing.Platform. Some installed-game tests skip by default (local opt-in).
- 12 test projects, 231 tests, 2 opt-in skips (as of 2026-07-27).

## Architecture

```
Core (no project refs)
 └── Application → Core only
      ├── Replays → Application + Core      (replay parsing: .wotbreplay, pickle, protobuf)
      ├── CaptureLogs → Application + Core  (telemetry capture log reading)
      ├── GameIntegration → Application + Core (installed-game discovery, DVPL reading)
      ├── Storage.Sqlite → Application + Core (SQLite storage)
      └── Bootstrap (composition root; all DI registration)
           ├── Host.Cli (net10.0 console)
           ├── Host.Web (net10.0 Blazor Web App, loopback-only)
           └── Overlay → (net10.0-windows WPF, loopback web client; NO parser/storage refs)
                ├── Discovery/RendezvousLocator  (finds host via rendezvous file)
                ├── Services/TreaderApiClient     (read API HTTP client)
                ├── Services/TelemetryStreamService (SignalR push client, auto-reconnect)
                ├── ViewModels/MainViewModel      (session list, position detail, BaseUri)
                ├── Views/PositionPlot             (canvas scatter plot, team-colored)
                └── MainWindow                     (TabControl: Position Plot + WebView2 Dashboard)
```

**Key rules:**
- Adapters (Replays, CaptureLogs, GameIntegration, Storage.Sqlite) never reference each other
- Only `Overlay` and `tools/GameHarness` target `net10.0-windows`; everything else is portable `net10.0`
- New DI ports must be added to `CompositionRootTests` published-port list or no host starts
- Warnings are errors (`TreatWarningsAsErrors`), NuGet audit mode is `all` — fix with central pins, never suppress
- Package versions are centrally managed in `Directory.Packages.props` with committed lock files

## Conventions

- **Testing:** MSTest 4, synthetic fixtures only in CI. Private replays/captures/DBs stay in gitignored paths.
- **Evidence-first:** unknown stays unknown. Reprocess = new immutable decode run. Pickle = data only; never execute opcodes.
- **Privacy:** never log raw replay bytes, tokens, full paths, player names, account IDs, chat, or screenshots.
- **Bot status:** never infer from name; use `unknown` without evidence.
- **Game automation:** developer-only, offline-replay-only, denied by default, fully audited.
- **Commits:** author as `Codex Agent <codex@local.invalid>` unless user says otherwise. Never force-push. Push only when asked.
- **Blockers:** append `docs/operations/blocker-log.md` (immutable UTC).
- **Handoffs:** append under `docs/operations/handoffs/` per format in the handoff README. Correct with amendments, never rewrite.

## Gotchas

- `.gitignore` patterns match **case-insensitively on Windows**. Runtime-data patterns (`*.sqlite`, `diagnostics/`, `dist/`) can hide real source folders. Add explicit `!` unignore rules when creating paths that collide with runtime-data patterns.
- In `validate.ps1`, route every native command through `Invoke-CheckedNative`; `$ErrorActionPreference='Stop'` does NOT catch non-zero exit codes.
- `NuGetAuditMode=all` fails restore on vulnerable transitive packages. Fix with a central version pin — never suppress.
- `scan-repository.ps1` checks for secrets (API keys, private keys, connection strings, absolute replay paths) and ignored files in source trees.
- SignalR callbacks run on non-UI threads without a `SynchronizationContext`. Any ObservableCollection mutations from SignalR callbacks must be marshalled via `SynchronizationContext.Post`.
- WebView2 requires the Evergreen runtime. The overlay falls back gracefully with a user-visible message if it's missing.
