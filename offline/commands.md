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

# Publish + synthetically validate the x86 instruction-first helper
pwsh -NoProfile -File scripts/publish-instruction-snapshot-helper.ps1
pwsh -NoProfile -File tmpwotb-e2e/test-execute-snapshot-interceptor.ps1
```

## Instruction-first position discovery (offline replay only)

After the publish and synthetic test pass, start a new managed offline replay
host with the helper identity pinned, then run one bounded capture:

```powershell
powershell -File scripts/launch-offline-replay-for-od.ps1 -EnableInstructionSnapshot
dotnet run --project tools/src/WotBTreader.GameHarness -c Release -- `
  discover-instruction-snapshot --seconds 5 --max-hits 64
```

The command emits privacy-safe object keys and XYZ values, not process/object
addresses. A hit proves register/displacement provenance at the pinned
instruction; it does not by itself prove viewpoint identity or a stable root.

## Ghidra offset-discovery evidence

Keep headless disassembly and heuristic reports under the ignored build tree.
The dump scripts use this environment variable instead of a worktree-specific
path:

```powershell
$env:WOTB_READER_GHIDRA_OUTPUT_DIR = `
  (Join-Path $PWD '.build\ghidra-evidence')
```

Run these scripts only against the already analyzed, hash-verified Ghidra
project. `FindType10PositionConsumers.java`, `FindType10RecordDispatch.java`,
and `FindType10DispatchTable.java` are triage tools: their output is not a
consumer, handler, or offset claim without decompiler/data-flow proof.

```powershell
& $analyzeHeadless $projectDirectory $projectName `
  -process wotblitz.exe -noanalysis `
  -postScript FindType10PositionConsumers.java `
  -scriptPath (Join-Path $PWD 'tools\ghidra-scripts')

& $analyzeHeadless $projectDirectory $projectName `
  -process wotblitz.exe -noanalysis `
  -postScript FindType10RecordDispatch.java `
  -scriptPath (Join-Path $PWD 'tools\ghidra-scripts')

& $analyzeHeadless $projectDirectory $projectName `
  -process wotblitz.exe -noanalysis `
  -postScript FindType10DispatchTable.java `
  -scriptPath (Join-Path $PWD 'tools\ghidra-scripts')
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
