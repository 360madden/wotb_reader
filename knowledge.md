# Project knowledge

WotB Treader is a **Windows-first offline replay telemetry reader** for World of Tanks Blitz. It parses replay evidence, stores versioned telemetry projections, and presents a local Blazor dashboard + WPF/WebView2 overlay with SignalR push-based updates.

The overlay is a **transparent heads-up display (HUD)** designed to sit on top
of the WoT Blitz game while it plays back a pre-recorded replay. It shows
decoded position plots and telemetry that the game's built-in viewer does not
expose. See `docs/architecture/overview.md#overlay--hud-design-intent` for the
full design specification.

- **Stack:** .NET 10 (C#), WPF, ASP.NET Core Blazor Web App, SQLite, SignalR
- **No:** Python, Node.js, Rust, Electron, containers, cloud services, runtime AI, dynamic decoder DLLs

## Quickstart

**Requirements:** Windows 10/11, .NET SDK 10.0.302, Edge WebView2 Runtime (for overlay dashboard tab)

**Convenience wrappers (repo root .cmd files, run from any directory):**

| Wrapper | What it does |
|---------|-------------|
| `build` | Build the solution (Release) |
| `validate` | Full gate: restore → format → build → test → audit → scan |
| `test` | Run all tests (skip build) |
| `serve` | Publish + start web host at http://127.0.0.1:9182 |
| `everything` | One-shot: launch serve then overlay (the full HUD experience) |
| `overlay` | Launch the WPF overlay (needs web host running) |
| `import <file>` | Import a .wotbreplay file |
| `watch <dir>` | Watch directory and auto-import new replays |
| `sessions` | List decoded battle sessions (JSON) |
| `doctor` | Run environment health checks (JSON) |
| `compare list` | List comparison runs |
| `compare inspect <id>` | Inspect one comparison run |
| `export sessions <id>` | Export session events as JSON |
| `export positions <id>` | Export position samples as JSON |
| `treader <cmd> [args]` | General CLI passthrough for any command |

All CLI wrappers store data under `.data\` in the repo root (gitignored).
Publish output goes to `.build\publish\` (also gitignored).

### Startup Sequence

The overlay (HUD) is a loopback web client — it has no data of its own.
It discovers the web host via a rendezvous file. The correct order is:

```
┌─────────────────────────────────────────────────────┐
│  1. import  a .wotbreplay  (one-time per replay)   │
│     or watch a folder to auto-import new replays    │
│                         ↓                           │
│  2. serve   start the web host (keep it running)   │
│                         ↓                           │
│  3. overlay  launch the HUD                        │
│     (or open http://127.0.0.1:9182 in a browser)   │
└─────────────────────────────────────────────────────┘
```

- **Step 1** decodes replays into a SQLite database under `.data\`.
- **Step 2** serves that database via REST + Blazor + SignalR at `127.0.0.1:9182`
  and writes a rendezvous file so the overlay can auto-discover it.
- **Step 3** launches the WPF overlay which finds the host, loads the session
  list, and plots position data.

You can re-run step 1 later (import more replays) while the host is running —
the overlay refreshes to show new sessions.

**One-command launch:** `everything.cmd` starts both `serve` and `overlay`
in separate windows with a short wait for the host to be ready.

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
- 12 test projects, 233 tests, 2 opt-in skips (as of 2026-07-27).

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
           ├── Overlay → (net10.0-windows WPF, transparent HUD; NO parser/storage refs)
                ├── Discovery/RendezvousLocator  (finds host via rendezvous file)
                ├── Services/TreaderApiClient     (read API HTTP client)
                ├── Services/TelemetryStreamService (SignalR push client, auto-reconnect)
                ├── ViewModels/MainViewModel      (session list, position detail, BaseUri)
                ├── Views/PositionPlot             (canvas scatter plot, team-colored)
                └── MainWindow                     (INTENDED: transparent borderless topmost HUD over game)
                                                  (CURRENT: standard WPF window with TabControl)
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
