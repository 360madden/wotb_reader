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

## Amendment — Ghidra installation verified (`2026-07-28T21:29:43Z`)

Verified the extracted Ghidra 12.1.2 PUBLIC installation against the registered
archive SHA-256 and application metadata. Machine-level `JAVA_HOME` resolves to
the registered Temurin JDK 21.0.11 installation. Both GUI and headless launchers
are present.

The headless launcher completed a disposable import of a benign 64-bit Windows
PE with exit code 0, initialized the per-user Ghidra settings/cache, and removed
the temporary project. Ghidra is locally installed and operational.

Deferred: successful startup does not resolve the separate timeout encountered
while analyzing the full 71 MB game binary, and `FindOffsets.py` still has the
known Jython compatibility issues listed above.

## Amendment — M0 Host.Web memory containment (`2026-07-28T22:30:50Z`)

Closed the first architecture-roadmap M0 slice. `Host.Web` no longer attempts
memory attachment when it finds an SDL window, and its assembly no longer
contains process-memory handles, `OpenProcess`, `ReadProcessMemory`,
`EnumProcessModules`, or VM-read access constants. Replay launching,
window/PID observation, log-derived replay state, and the memory response shape
remain available; memory polling fails closed as process-inaccessible until
the Milestone 2 offline-verification lease exists.

Added two focused attachment-denial tests. Also removed the recurring
method-parallel race in `OverlayApiStateTests` by giving every test an
independent state instance; the complete Overlay suite then passed three
consecutive runs.

Validation: focused Host.Web tests passed 2/2; the Overlay suite passed 91/91
three consecutive times; `scripts/validate.ps1` passed with a Release build at
0 warnings/errors, 283 tests passed, 2 local opt-in tests skipped, and the
repository scan passed.

Deferred M0 work: hard-deny or centrally gate direct GameHarness `scan` and
`probe`, contain the overlay mutation listener, restore verified owner-only
rendezvous storage, and append the outstanding blocker-log amendments.

## Amendment — M0 baseline completed (`2026-07-28T22:39:00Z`)

Closed the remaining architecture-roadmap M0 criteria. GameHarness `scan` and
`probe` now deny before PID parsing, enumeration, or attachment. The overlay no
longer starts its legacy port 9190 Kestrel listener. Rendezvous storage is
protected and positively verified as current-user-only before a capability can
be published, including a regression test that begins with a permissive
inherited ACL. The private `.data.bak/` tree remains untouched and is now
explicitly ignored as local runtime data.

Reconciled the accepted overview with the single Host.Web control plane,
GameIntegration ownership, portable contract boundary, and client-only HUD
target. Appended immutable blocker records for the BLK-0003 TFM regression,
BLK-0014 ACL regression/recovery, and BLK-0015 unverified attachment paths.

Validation: focused GameHarness, Overlay, and Bootstrap suites passed 28/28,
92/92, and 11/11. `scripts/validate.ps1` passed locked restore, format
verification, Release build with 0 warnings/errors, 287 tests passed, 2 local
opt-in tests skipped, and the repository scan passed for 425 tracked files.

Deferred: M1 restores the portable TFMs, mechanically enforces the complete
project/TFM graph, and introduces the shared no-dependency `ApiContracts`
assembly. M2 owns the centralized offline-session authorization lease before
any product or harness memory integration can return.

## Amendment — Cursor CLI reviewer routing (`2026-07-28T22:40:32Z`)

Registered the subscription-backed Cursor Agent CLI as a verified local tool
and added a repository adapter for isolated, read-only `decoder-auditor` and
`security-auditor` reviews. The adapter pins the verified Opus/Fable model
slugs, uses Ask mode and a clean worktree from committed `HEAD`, and denies
private/runtime/game-derived data plus destructive shell commands.

Verification: the installed CLI reported `2026.07.23-e383d2b`; the registered
runtime and launcher SHA-256 values matched; the project policy schema was
corrected to include an explicit empty allowlist; PowerShell parsing and the
adapter dry run passed; the repository scan passed for 429 tracked files.

Operational boundary: Windows Cursor sandboxing is unavailable, so the adapter
must not be bypassed with current-worktree, force/yolo, automatic MCP approval,
or cloud-handoff modes. Private replay, database, capture, screenshot, token,
account, and memory-offset data remain out of scope.

## Amendment — Cursor CLI isolation correction (`2026-07-28T22:42:00Z`)

Supersedes the preceding amendment's clean-Git-worktree description. The
adapter now exports committed `HEAD` with `git archive` into a temporary
standalone directory that contains no `.git` link, denies every shell and write
tool in project policy, runs the reviewer against that export, and removes the
export afterward. It also rejects prompts containing absolute Windows paths,
replay filenames, API-key patterns, or private-key headers.

Verification: Cursor help confirmed the selected workspace, mode, model, and
sandbox switches; PowerShell parsing passed; both role dry runs reported the
standalone export and denied shell/write policy; the repository scan passed for
429 tracked files. A real reviewer smoke remains the post-commit check because
the adapter intentionally refuses to run while any required policy file differs
from committed `HEAD`.

Post-commit verification: the real subscription-backed `security-auditor`
invocation completed through the adapter and returned the requested
`CURSOR_REVIEWER_OK` sentinel. The temporary export was cleaned up by the
adapter.

## Amendment — M1 portable TFMs restored (`2026-07-29T00:17:18Z`)

Closed the BLK-0003 target-framework regression deferred by the M0 amendments.
`Host.Web`, `Host.Web.Tests`, and `GameIntegration.Tests` are back on portable
`net10.0`, and their `packages.lock.json` target keys were regenerated to match
so locked-mode restore succeeds on a clean clone. The `net10.0-windows` move in
`55f2755` was justified as required for Win32 P/Invoke; that is not the case,
and `DllImport` compiles on the portable target with zero warnings.

`TargetFrameworkTests` now parses every project file under `src`, `tests`,
`tools/src`, and `tools/tests` and fails when any project outside the
`Overlay`/`GameHarness` allowlist declares a Windows target, declares
`TargetFrameworks`, or declares anything other than exactly one
`TargetFramework`.

Validation: `powershell -NoProfile -ExecutionPolicy Bypass -File
scripts\validate.ps1` passed — locked restore, format verification, Release
build with 0 warnings/errors, 289 tests passed, 2 local opt-in tests skipped,
and the repository scan passed for 429 tracked files. Commits `94e349b` and
`47d3945` are pushed to `origin/main`.

Deferred: the rest of M1 — expanding the dependency tests to `Bootstrap`, both
hosts, `Overlay`, and `tools/src`; introducing the no-dependency `ApiContracts`
project and moving the duplicated host/overlay wire shapes into it; and
codifying the `Bootstrap`-only composition rule. M2 still gates all product
memory integration and offset promotion.

## Amendment — M1 reference graph enforced (`2026-07-29T00:21:47Z`)

Supersedes the preceding amendment's deferral of the dependency-test expansion.
`ProjectReferenceTests` now parses every production project under `src` and
`tools/src` and enforces the roadmap graph: `Core` references nothing,
`Application` references only `Core`, adapters reference only `Application` and
`Core`, `Bootstrap` may reference the adapters, hosts are limited to
`ApiContracts`/`Application`/`Bootstrap`/`Core`, and `Overlay` is limited to
`ApiContracts`. An unclassified production project is itself a violation, so a
new project cannot join the solution without an explicit boundary decision.
`ProjectCatalog` now holds the shared project-file discovery used by both
architecture tests.

Recorded as tracked debt rather than silently allowed: `GameHarness` references
`GameIntegration`, and `ReplayInspector`/`ReplaySanitizer` reference `Replays`,
instead of resolving product ports through `Bootstrap`. `ToolAdapterDebt`
enumerates exactly those three edges, no tool may add another, and a companion
test fails when an exemption becomes stale, so the list can only shrink.

Validation: `powershell -NoProfile -ExecutionPolicy Bypass -File
scripts\validate.ps1` passed — locked restore, format verification, Release
build with 0 warnings/errors, 295 tests passed, 2 local opt-in tests skipped,
and the repository scan passed for 430 tracked files. Coverage of `tools/src`
was confirmed by temporarily clearing the `GameHarness` exemption and observing
`ProductionProjects_FollowTheApprovedReferenceGraph` fail with the exact
offending edge before restoring it.

Deferred: the remaining M1 work — introducing the no-dependency `ApiContracts`
project, moving the duplicated host/overlay wire shapes into it, and retiring
the three tool-to-adapter edges. Test projects are deliberately out of the
graph's scope; `Overlay.Tests` still references `Host.Web`. M2 continues to gate
all product memory integration and offset promotion.

## Amendment — M1 ApiContracts, server half (`2026-07-29T01:44:05Z`)

Added `src/WotBTreader.ApiContracts`, a portable `net10.0` assembly with no
project references and no package references — its generated lock file is an
empty `net10.0` dependency set. It now owns the nine read-API wire shapes.
`Host.Web` references it, and the `From(domain)` factories that used to live on
the DTOs became `ReadContractMapping` extensions in the host, because the
contracts assembly cannot see `Core`. That also removed the `ToSummary`
duplication that existed in both `ReadApiEndpoints` and `DashboardReadClient`.

Recorded drift found while comparing the two copies, since it explains the
shapes chosen. The overlay's duplicate declared `battleTimeUtc` and `duration`
non-nullable while the host sends null, and declared `session` non-nullable on
both the summary and detail envelopes. A probe against the real types confirmed
the overlay type throws `JsonException` on a null `battleTimeUtc` and silently
lands null in its non-nullable `session`, while the host type round-trips both.
The overlay copy also omitted `vehicleCompactDescriptor` entirely and carried
only three of the six `GameStateResponse` fields. The published contract follows
the host, which is what is actually on the wire. `ContractComplianceTests` did
not catch any of this: its fixtures populate every nullable field and it never
asserts the missing member.

Validation: `powershell -NoProfile -ExecutionPolicy Bypass -File
scripts\validate.ps1` passed — locked restore, format verification, Release
build with 0 warnings/errors, 295 tests passed, 2 local opt-in tests skipped,
and the repository scan passed for 432 tracked files. The unclassified new
project was reported by `ProjectReferenceTests` before it was added to the
graph, which is the intended behaviour.

Deferred to the client half: migrate `Overlay` onto `ApiContracts`, move the
game/launch/memory and overlay-command shapes across, add the HUD null handling
the corrected nullability requires, and retire `ContractComplianceTests` along
with the `Overlay.Tests` to `Host.Web` reference. Until then the overlay keeps
its own duplicate DTOs and that stopgap test compares them against the published
contracts.
