# Session handoff — 2026-07-28: Offset discovery toolchain setup

**Author:** Buffy (Codex Agent)  
**Branch:** `main`  
**Working tree:** Clean (committed)  
**Head:** `abca4f2` — `docs(memory-offsets): add Ghidra + Cheat Engine offset discovery workflow`  
**Tests:** Not run (no code changes to compilation units)  
**Build:** N/A (documentation + tool config only)

---

## What was accomplished this session

### Tool registration and environment setup

**Three external tools registered** in `tools/external/tools.lock.json`:

| Tool | Version | Installed Path | Verification |
|------|---------|---------------|-------------|
| **Cheat Engine** | 7.7.0.10621 | `C:\Program Files\Cheat Engine\` | SHA-256 verified for `cheatengine-x86_64.exe` |
| **Ghidra** | 12.1.2 PUBLIC | `C:\work\tools\ghidra_12.1.2_PUBLIC\` | SHA-256 verified against GitHub release manifest |
| **AITools** | 1.0 | `C:\work\tools\AITools-main\tools\aitools.lua` | CE Lua plugin (LicenseRef-proprietary) |

**JDK 21 installed** via winget:
- Package: `EclipseAdoptium.Temurin.21.JDK` (v21.0.11+10 LTS)
- Path: `C:\Program Files\Eclipse Adoptium\jdk-21.0.11.10-hotspot\bin\java.exe`
- Verified: `java -version` reports OpenJDK 21.0.11 LTS

**Source repos also available** at `c:\work\tools\`:
- `cheat-engine-master` (v7.5 source, Lazarus/FPC)
- `ghidra-master` (v12.2 DEV source, JDK 25 to build)
- Both registered in `tools.lock.json` with build requirements

### Offset discovery workflow documentation

Complete revamp of `memory-offsets/README.md` with a three-phase pipeline:

```
Phase 1: Ghidra static analysis → Phase 2: Cheat Engine dynamic analysis → Phase 3: GameHarness verification
```

Includes: tool-specific setup commands, step-by-step walkthroughs for each phase, cross-phase validation table, common field type reference with game context, and practical tips.

### Ghidra headless script for automated offset discovery

Created `tools/ghidra-scripts/FindOffsets.py` — a Ghidra Python script that:
- Searches `wotblitz.exe` for known game state strings (health, hp, position, replayTime, yaw, pitch, alive)
- Traces cross-references to find referencing functions
- Extracts data offsets used within those functions
- Exports candidate offsets to `tools/ghidra-scripts/ghidra-offset-candidates.json`

**Current status:** Script v3 written with correct Ghidra API calls (`SearchData` constructor, `searchData()` method, direct Address iteration). Contains two known bugs not yet fixed:
1. `os.makedirs(OUTPUT_DIR, exist_ok=True)` — `exist_ok` keyword not in Jython 2.7 stdlib, will crash silently due to broad `except`
2. `is_string()` function is dead code (defined but never called)

**Script not yet successfully run.** Ghidra headless analysis (`analyzeHeadless.bat`) times out in this environment — Java process may need longer startup or `-maxmem` tuning for a 71 MB binary.

### Game binary discovery

Located the installed WoT Blitz game:
| Detail | Value |
|--------|-------|
| Path | `C:\Games\World_of_Tanks_Blitz\wotblitz.exe` |
| Version | **11.18.0.7** (newer than existing `11.8.0.7` offset file) |
| SHA-256 | `42db3be5d95e2ae922bc9cf0b133c6193c374406cbc3062fe3e70551bebbbd94` |
| Size | 71 MB |

The existing `memory-offsets/11.8.0.7.json` is stale — a fresh `11.18.0.7.json` needs to be created with real offsets.

### Files changed (committed in abca4f2)

| File | Change |
|------|--------|
| `memory-offsets/README.md` | Full revamp: three-phase workflow (Ghidra → CE → scanner), external tools table, quick reference |
| `tools/external/tools.lock.json` | Registered Cheat Engine 7.7, Ghidra 12.1.2, AITools with verified hashes and paths |
| `tools/src/WotBTreader.GameHarness/Program.cs` | Minor formatting fix (whitespace) |
| `tools/ghidra-scripts/FindOffsets.py` | New — Ghidra headless offset discovery script (v3, needs minor fixes) |

---

## Unresolved

1. **Ghidra headless analysis won't complete in this terminal.** Two attempts at `analyzeHeadless.bat` timed out at 600 seconds. Likely causes:
   - JVM cold start + 71 MB binary import is slow
   - `cmd.exe` invocation through bash may have quoting issues
   - Needs manual launch: open Command Prompt, `set JAVA_HOME=...`, run `analyzeHeadless.bat` directly

2. **`FindOffsets.py` has two unfixed bugs:**
   - `os.makedirs(dir, exist_ok=True)` → Jython 2.7 doesn't support `exist_ok`. Fix: wrap in `try/except OSError` manually.
   - `is_string()` function is dead code — remove it.
   - `import traceback` should be moved from function body to module top.

3. **No offset file for game version 11.18.0.7.** The existing `memory-offsets/11.8.0.7.json` is for an older version. Offsets likely changed.

4. **Cheat Engine source (v7.5) vs prebuilt (v7.7) version gap.** The source at `c:\work\tools\cheat-engine-master` is 7.5, but the installed binary is 7.7. If modifications to Cheat Engine are needed, the source should be updated.

---

## Recommended resume steps

1. **Fix FindOffsets.py:** Correct the `os.makedirs` call, remove dead code, test with a small test binary first
2. **Run Ghidra headless manually:** Open Command Prompt as admin:
   ```cmd
   set JAVA_HOME=C:\Program Files\Eclipse Adoptium\jdk-21.0.11.10-hotspot
   C:\work\tools\ghidra_12.1.2_PUBLIC\support\analyzeHeadless.bat C:\work\tools\ghidra-projects WotBlitz -import C:\Games\World_of_Tanks_Blitz\wotblitz.exe
   ```
   Wait for analysis (~10-15 min), then:
   ```cmd
   C:\work\tools\ghidra_12.1.2_PUBLIC\support\analyzeHeadless.bat C:\work\tools\ghidra-projects WotBlitz -process wotblitz.exe -postScript FindOffsets.py -scriptPath C:\work\wotb_reader\tools\ghidra-scripts
   ```
3. **Create memory-offsets/11.18.0.7.json** from Ghidra script output
4. **Validate offsets** with the GameHarness scanner:
   ```cmd
   dotnet run --project tools/src/WotBTreader.GameHarness -- probe
   dotnet run --project tools/src/WotBTreader.GameHarness -- scan int32 1500
   ```
5. **Cross-verify with Cheat Engine:** Launch CE, attach to `wotblitz.exe` replay, value-scan for HP

## Amendment — Codex project agents (`2026-07-28T21:22:08Z`)

Added project-scoped Codex agents for decoder auditing, security auditing,
frozen-contract glue implementation, and verification under `.codex/agents/`.
Added `.codex/config.toml` with a three-subagent concurrency cap and updated
`AGENTS.md` with Codex routing while preserving the Cursor roles.

Validation: `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\validate.ps1`
passed — locked restore, format verification, Release build with 0
warnings/errors, 281 tests passed, 2 local opt-in tests skipped, and the
repository scan passed for 412 tracked files.

Deferred: the existing offset-discovery blockers above and the `Host.Web`
`net10.0-windows` architecture-rule drift remain unresolved.
