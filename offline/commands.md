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

## Type-10 entity-position capture (offline replay only)

After the publish and synthetic test pass, start a new managed offline replay
host with the helper identity pinned, then run one bounded capture:

```powershell
powershell -File scripts/launch-offline-replay-for-od.ps1 -EnableInstructionSnapshot
dotnet run --project tools/src/WotBTreader.GameHarness -c Release -- `
  discover-instruction-snapshot --seconds 5 --max-hits 64
```

The command emits privacy-safe opaque object keys, replay-local entity IDs,
UTC, and XYZ values, not process/entity/vector addresses. A hit proves
same-debug-event entity/vector register provenance at the pinned instruction;
the ID and vector are still two reads, so it does not by itself prove hardware
atomicity, same-decoded-clock identity, local-player identity, or a stable root.
Compare each ID only to the same decoded type-10 entity trajectory and stop
after the bounded result.

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
`FindType10DispatchTable.java`, and `FindVehiclePositionFamily.java` are triage
tools: their output is not a consumer, handler, object type, or offset claim
without decompiler/data-flow proof.

Do not trust the native headless exit code by itself. Ghidra can return zero
after a post-script compile or runtime error. Every evidence run must also:

1. reject `SCRIPT ERROR` or `error:` in the script log;
2. require the expected report to be newer than the invocation start time; and
3. for a verifier, require its explicit success verdict.

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

& $analyzeHeadless $projectDirectory $projectName `
  -process wotblitz.exe -noanalysis `
  -postScript FindVehiclePositionFamily.java `
  -scriptPath (Join-Path $PWD 'tools\ghidra-scripts')

& $analyzeHeadless $projectDirectory $projectName `
  -process wotblitz.exe -noanalysis `
  -postScript FindReplayEntityBridges.java `
  -scriptPath (Join-Path $PWD 'tools\ghidra-scripts')

& $analyzeHeadless $projectDirectory $projectName `
  -process wotblitz.exe -noanalysis `
  -postScript TraceType10MovementPosition.java `
  -scriptPath (Join-Path $PWD 'tools\ghidra-scripts')

python tools/find-static-roots.py `
  --chain 0x03E91978 `
  --vtable-root VehicleGameLogic
```

The vehicle-family scan deliberately separates exact `[reg+0x04]` handoffs
from same-base displacement fallbacks and reports matrix-shaped matches. The
root/vtable command independently rechecks the stale community root and names
the current-build `VehicleGameLogic` vtable. Neither command authorizes a live
read. `FindReplayEntityBridges.java` is a broad relationship mapper and remains
heuristic. `TraceType10MovementPosition.java` is the hash-bound verifier: its
fresh report must contain `verdict=semantic-chain-proven` and zero failed
checks. That verdict proves the static entity/XYZ event anchor only; it does
not authorize live capture or publish an offset.

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
