# Commands

Exact commands for building, testing, running, and importing. Never run
interactive `.cmd` wrappers through an agent shell (they spawn windows / expect
a TTY) — use direct `dotnet` commands instead.

## Environment

- SDK pinned by `global.json` to **10.0.302**. Restore is always `--locked-mode`.
- Warnings are errors; NuGet audit mode is `all` (fix with central pins, never suppress).

## Full gate (before milestone commits)

```powershell
./scripts/validate.ps1                # locked restore → format → build → test → scan
./scripts/validate.ps1 -AuditPackages # + transitive vulnerability audit
```

## Direct dotnet commands

```bash
# Build (Release)
dotnet build WotBTreader.sln -c Release

# Single test project
dotnet test tests/WotBTreader.Core.Tests -c Release

# Focused test
dotnet test tests/WotBTreader.Core.Tests -c Release --filter "FullyQualifiedName~SomeTest"

# Run CLI directly (data root defaults to .data\)
dotnet run --project src/WotBTreader.Host.Cli -c Release -- doctor

# Publish web host
dotnet publish src/WotBTreader.Host.Web -c Release -o .build/publish
```

## Agent-shell (basher) timeouts — never use the default 30s

| Command | Timeout |
|---------|---------|
| `dotnet build` | 300s |
| `dotnet test` (full suite) | 300s |
| `dotnet test` (single project) | 120s |
| `dotnet publish` | 180s |

## Convenience wrappers (repo root; human use)

| Wrapper | What it does |
|---------|-------------|
| `build` | Build the solution (Release) |
| `validate` | Full gate (see above) |
| `test` | Run all tests (skip build) |
| `serve` | Publish + start web host at http://127.0.0.1:9182 |
| `everything` | One-shot: serve then overlay |
| `overlay` | Launch the WPF overlay (needs web host running) |
| `import <file>` | Import a .wotbreplay |
| `watch <dir>` | Watch a directory and auto-import new replays |
| `sessions` | List decoded battle sessions (JSON) |
| `doctor` | Environment health checks (JSON) |
| `compare list` / `inspect <id>` / `create <l> <r>` | Comparison runs |
| `export sessions <id>` / `export positions <id>` | Export events / position samples |
| `treader <cmd> [args]` | CLI passthrough for any command |

## Startup sequence (1-2-3)

1. `import` a `.wotbreplay` (or `watch` a folder)
2. `serve` — start the web host (keep it running)
3. `overlay` — launch the HUD (or open http://127.0.0.1:9182)
