# Project knowledge

WotB Treader is a **Windows-first offline replay telemetry reader** for World of Tanks Blitz. It parses replay evidence, stores versioned telemetry projections, and presents a local Blazor dashboard + WPF/WebView2 overlay with SignalR push-based updates.

The project owner identifies as a junior developer at Wargaming.net. This is
user-provided background for a personal, independently maintained project;
see [Project context](docs/project-context.md).

The overlay is a **transparent heads-up display (HUD)** designed to sit on top
of the WoT Blitz game while it plays back a pre-recorded replay. It shows
decoded position plots and telemetry that the game's built-in viewer does not
expose. See `docs/architecture/overview.md#overlay--hud-design-intent` for the
full design specification.

- **Stack:** .NET 10 (C#), WPF, ASP.NET Core Blazor Web App, SQLite, SignalR
- The overlay is a loopback client only. It hosts no HTTP control plane; the legacy
  embedded Kestrel listener on port 9190 was removed and `OverlayControlPlaneContainmentTests`
  keeps it removed.
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
| `compare create <leftId> <rightId>` | Create a comparison run from two decode runs |
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
- 12 test projects, 397 tests: 395 passed, 2 opt-in skips (as of 2026-07-29).

### Keyboard shortcuts

| Key | Action |
|-----|--------|
| Space | Play / Pause |
| ← | Scrub back 5 seconds |
| → | Scrub forward 5 seconds |
| 1 | Speed 0.5× |
| 2 | Speed 1× |
| 3 | Speed 2× |
| 4 | Speed 4× |
| 5 | Speed 8× |
| Esc | Close overlay |

## Architecture

```
Core (no project refs)
 └── Application → Core only
      ├── Replays → Application + Core      (replay parsing: .wotbreplay, pickle, protobuf)
      ├── CaptureLogs → Application + Core  (telemetry capture log reading)
      ├── GameIntegration → Application + Core (installed-game discovery, DVPL reading,
      │                                         offline session gate, guarded Win32)
      ├── Storage.Sqlite → Application + Core (SQLite storage)
      └── Bootstrap (composition root; all DI registration)
           ├── Host.Cli (net10.0 console)
           ├── Host.Web (net10.0 Blazor Web App, loopback-only, port 9182)
           └── (tools) GameHarness / ReplayInspector / ReplaySanitizer
                       resolve product ports through Bootstrap only

ApiContracts (net10.0; NO project refs, NO package refs — empty lock file)
 ├── ReadContracts.cs   (session/participant/position/event/comparison read shapes)
 ├── GameContracts.cs   (game status, launch, memory-observation shapes)
 └── HudContracts.cs    (HUD command/status shapes)
      ├── referenced by Host.Web  (serializes them on the wire)
      └── referenced by Overlay   (its ONLY project reference)

Overlay (net10.0-windows WPF, transparent HUD; loopback client only)
 ├── Discovery/RendezvousLocator     (finds host via owner-only rendezvous file)
 ├── Services/TreaderApiClient       (read API HTTP client)
 ├── Services/TelemetryStreamService (SignalR push client, auto-reconnect)
 ├── ViewModels/MainViewModel        (session list, positions, events, stats, playback)
 ├── Views/PositionPlot              (canvas scatter plot, velocity trails, minimap grid)
 └── MainWindow                      (transparent borderless topmost HUD, P/Invoke window tracking)
```

**Dormant code in `Overlay`:** `Endpoints/OverlayApiEndpoints.cs` and
`Services/OverlayApiState.cs` still compile but are unreachable — nothing binds port
9190 and no listener starts. Milestone 3 deletes them. Do not extend either file.

**Key rules:**
- Adapters (Replays, CaptureLogs, GameIntegration, Storage.Sqlite) never reference each other
- `Overlay` references only `ApiContracts` — never a host, adapter, `Application`, or `Core`
- `ApiContracts` is serialization-only: no domain behavior, no project refs, no package refs
- Hosts and tools compose exclusively through `Bootstrap`; tools do not build their own adapters
- Only `Overlay` and `tools/GameHarness` target `net10.0-windows`; everything else is portable `net10.0`
- New DI ports must be added to `CompositionRootTests` published-port list or no host starts
- Warnings are errors (`TreatWarningsAsErrors`), NuGet audit mode is `all` — fix with central pins, never suppress
- Package versions are centrally managed in `Directory.Packages.props` with committed lock files

`WotBTreader.Architecture.Tests` (14 tests) enforces the reference graph, the TFM
allowlist, and the native-access boundary. Breaking any rule above fails the build.

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
- **cmd.exe wrapper scripts** have failure modes that survive casual review — delayed expansion corrupts `!` in filenames, unquoted `%~dp0` breaks on paths with spaces, whitespace input crashes arithmetic checks, and missing `setlocal` leaks env vars. See `docs/operations/cmd-wrapper-gotchas.md` for the full catalogue and review checklist. Always route cmd/batch reviews through a thinker agent.
- **Basher (terminal agent) timeouts are a recurring waste pattern.** Default 30s timeout is never enough for .NET commands. Use these timeouts: `dotnet build` → 300s, `dotnet test` (full suite) → 300s, `dotnet test` (single project) → 120s, `dotnet publish` → 180s. Never run interactive `.cmd` wrappers through basher — use direct `dotnet` commands. Verify prerequisites (CLI built, packages restored) before running dependent commands.
