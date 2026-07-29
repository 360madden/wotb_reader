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

## Amendment — M1 ApiContracts, client half (`2026-07-29T14:09:48Z`)

Completed the bounded client-half migration. All game and dormant HUD
command/status wire shapes now live in dependency-free `ApiContracts`, and
`Overlay` directly references that assembly. Removed the duplicate Overlay
read/HUD DTOs, the Host.Web game DTOs, `Overlay.Tests` → `Host.Web`, and
`ContractComplianceTests`. The overlay now skips missing or invalid session
rows and preserves a nullable battle timestamp.

Validation: focused Overlay, Host.Web, and architecture suites passed 88/88,
56/56, and 11/11. `powershell -NoProfile -ExecutionPolicy Bypass -File
scripts\validate.ps1` passed — locked restore, format verification, Release
build with 0 warnings/errors, 291 tests passed, 2 local opt-in tests skipped,
and the repository scan passed for 435 tracked files.

Deferred: retire the three tool-to-adapter edges and implement M2 offline-session
authorization.

## Amendment — M1 selected-session reconciliation correction (`2026-07-29T14:16:05Z`)

An independent security audit found that a refresh could leave replay-derived
HUD state visible after its selected session disappeared through a null or
invalid row. `ReconcileSelectedSession` now retains selection only while its
ID remains in the refreshed rows; otherwise it cancels the detail load, clears
the selection, and clears positions, participants, map, and duration. A
two-refresh regression test covers the removal path.

Validation: `powershell -NoProfile -ExecutionPolicy Bypass -File
scripts\validate.ps1` passed — locked restore, format verification, Release
build with 0 warnings/errors, Overlay 89/89, 292 tests passed, 2 local opt-in
tests skipped, and the repository scan passed for 435 tracked files.

## Amendment — M1 retained-selection refresh correction (`2026-07-29T14:19:47Z`)

Refresh now snapshots the selected ID. After reconciliation deasserts its
refresh state, it performs exactly one deferred detail load when a different,
retained ID was selected while the request was pending. The blocked A-to-B
regression asserts B data is shown and exactly one request is made. Security
re-audit found no remaining concrete issue.

Validation: `powershell -NoProfile -ExecutionPolicy Bypass -File
scripts\validate.ps1` passed — locked restore, format verification, Release
build with 0 warnings/errors, Overlay 90/90, 293 tests passed, 2 local opt-in
tests skipped, and the repository scan passed for 435 tracked files.

## Amendment — M1 tool-boundary completion (`2026-07-29T14:54:17Z`)

Completed M1. Removed ReplaySanitizer's stale `Replays` edge. The narrow
`Bootstrap.AddWotBTreaderReplayTooling` registers only the replay decoder
registry, probe, and decoders, without foundation filesystem, storage, or game
side effects; ReplayInspector now resolves `IReplayProbe` and the
registry-selected `IReplayDecoder` through Bootstrap. Deleted dormant,
unreachable GameHarness `Win32Platform` and its `GameIntegration` and SkiaSharp
dependencies; `UnavailableGameHarnessPlatform` and hard-denied `scan`/`probe`
remain. Removed every `ToolAdapterDebt` exemption and its mechanism: tools are
now limited to `ApiContracts`/`Application`/`Bootstrap`/`Core`.

Independent decoder and security audits found no issues; verifier locked
restore and focused checks passed. `powershell -NoProfile -ExecutionPolicy
Bypass -File scripts\validate.ps1` passed — locked restore, format
verification, Release build with 0 warnings/errors, 293 tests passed, 2 local
opt-in tests skipped, and the repository scan passed for 433 tracked files.

M1 is complete. Next: M2 centralized positive offline-session authorization
before any product or harness memory authority.

## Amendment — M2 fail-closed session boundary (`2026-07-29T16:04:04Z`)

Completed the first M2 slice without enabling process-memory access.
`Application` now publishes capability-neutral ports for game-session state,
managed replay launch, and ephemeral memory observation. Their contracts expose
no PID, HWND, executable path, native handle, offset, attachment operation, or
authorization token. `Bootstrap` publishes all three ports and proves they
resolve through one GameIntegration coordinator.

GameIntegration now owns an internal evidence state machine and private
authorized-observation generation. Positive synthetic verification requires a
coordinator-owned managed-launch context, independently trusted installed-game
identity, exact observed path/version/SHA-256, PID plus process-start identity,
owned window, healthy monitor, confirmed replay UI, exact
`BlitzNativeLog` provenance, a marker bound to the same process and launch
correlation, and a source cursor strictly after the launch baseline. Stop or
online evidence, monitor failure, process exit, PID reuse, identity mismatch,
cursor regression, and expiry revoke or deny. Memory observation remains
`Unknown`, and managed launch remains unavailable, so this slice opens no
process handle.

Removed Host.Web's native declarations, process probing, direct memory reader,
path-based replay staging, shell association launch, and hosted game-state
service. Its endpoints now consume only the Application ports, return
capability-neutral DTOs, and accept managed source-artifact IDs. The Overlay
passes the selected session's source-artifact ID; quick-import launch fails
closed until import returns an ID. Removed GameHarness's dormant raw-PID memory
reader, scanner, and native declarations while retaining hard-denied
`scan`/`probe` and a bounded read-only scanner-state viewer.

Architecture tests now reject native interop/direct memory readers in Host.Web
and GameHarness and reject authority-bearing public session contracts. A
security audit found and prompted fixes for initially unbound lifecycle/process
evidence and self-vouching expected identity. Focused GameIntegration,
Host.Web, Overlay, GameHarness, architecture, and composition tests passed.
`scripts/validate.ps1` passed locked restore, format verification, Release
build with 0 warnings/errors, 323 tests passed, 2 local opt-in tests skipped,
and the repository scan passed for 432 tracked files.

Deferred M2 work: implement the real query-only process/window identity source
and lifecycle reconciliation inside GameIntegration; generate managed-launch
correlation from a verified artifact/executable path; add a guarded,
exact-version-and-hash VM-read factory with immediate handle disposal and
between-chunk revalidation; and migrate GameHarness commands onto those same
ports before re-enabling them. Memory access remains disabled until those
invariants and disposal tests are complete.

## Amendment — M2 query-only process observer (`2026-07-29T16:21:12Z`)

Added a disconnected, internal Windows process-identity observer in
GameIntegration. It enumerates bounded eligible top-level `SDL_app` windows,
fails closed on ambiguity or incomplete enumeration, and opens a process only
with exact `PROCESS_QUERY_LIMITED_INFORMATION` (`0x1000`). It binds PID to the
creation `FILETIME`, queries the image path from that handle, pins the
executable without write/delete sharing, resolves its final path and stable
file identity, and computes SHA-256 from that same file handle. It rechecks
process liveness and the complete window class/top-level/visibility/ownership/
client-area predicate before returning an internal observation, then disposes
all process and file handles.

The observer is registered internally but remains deliberately disconnected
from `GameSessionCoordinator`, replay logs, launch correlation, and every
Application port. It cannot create authorization or open a memory-capable
handle. Architecture coverage now rejects VM-read/write/operation APIs and
symbolic access rights anywhere in GameIntegration. Synthetic tests prove exact
`0x1000` access, unsupported/absent/ambiguous/incomplete states, query failure,
PID/owner races, process exit, cancellation before and after open, and session
disposal.

A security audit found two fail-closed gaps in the first implementation:
candidate-cap overflow could appear unique, and final validation rechecked only
PID ownership. Enumeration now carries an explicit completeness result, and
the same full eligibility predicate runs both before and after identity
collection. Re-audit found no remaining issue.

`scripts/validate.ps1` passed locked restore, format verification, Release
build with 0 warnings/errors, 335 tests passed, 2 local opt-in tests skipped,
and the repository scan passed for 431 tracked files.

Deferred: canonicalize the trusted installed-game identity through the same
file-handle mechanism; replace the transient replay-log monitor with a
long-lived singleton feed that exposes atomic per-source generation/cursor
baselines plus explicit health/gap events; only then correlate the observer,
managed launch, replay UI, and lifecycle evidence in the coordinator. VM-read
remains disabled.

## Amendment — M2 atomic lifecycle evidence feed (`2026-07-29T17:06:29Z`)

Added a disconnected, internal, process-lifetime lifecycle feed in
GameIntegration. Initial reconciliation completes before readiness, existing
and recovery bytes are historical, and new/reset file incarnations retain that
provenance through partial lines. The singleton publishes only allowlisted
marker metadata; it exposes no raw native-log text, file name, full path,
player/account data, or runtime authorization capability.

The feed now owns an atomic bounded journal with a global sequence, explicit
health epochs, per-source generations and cursors, lifetime tombstones, closed
reason codes, and explicit gap/fault/reset events. Reconciliation reads cloned
tail state and stages markers, resets, and EOF cursors. A batch commits only if
every source succeeds and the journal sequence still matches the pass-start
revision; a concurrent watcher failure rejects the entire batch. Retention
eviction returns an explicit history gap rather than partial evidence.

Each Windows log incarnation is bound to volume/file identity from the same
held read handle. Continuity is hashed across the bounded configured evidence
window (expanded to cover a longer pending line) and revalidated before and
after reading. Truncation, deletion/reappearance, replacement, and same-file
rewrite advance the generation; enumeration overflow, watcher failure,
identity/read failure, and unexpected producer failure degrade the feed.

Security and decoder audits found and prompted fixes for initially healthy
uninitialized state, duplicate/generation-jumping markers, forgotten
tombstones, live pre-existing incarnation bytes, non-atomic multi-source
publication, truncate/regrow continuity, watcher-gap races, stale recovery
boundaries, partial-line provenance, and throwing-clock partial commits. Final
re-audit found no remaining High or Medium issue. Synthetic coverage includes
historical/live provenance, pending lines, atomic producer failure, revision
races, retention gaps, health epochs, cursor/tombstone monotonicity,
truncation, deletion/reappearance, incomplete enumeration, and prefix rewrites
that preserve the former 256-byte boundary.

The feed is registered internally but remains deliberately disconnected from
`GameSessionCoordinator`, managed launch correlation, replay UI evidence,
process identity, every Application port, and all memory authority. VM-read
remains disabled.

Validation: `scripts/validate.ps1` passed locked restore, format verification,
Release build with 0 warnings/errors, 360 tests passed, 2 local opt-in tests
skipped, and the repository scan passed.

Deferred: bind the canonical installed-game identity to the query observer,
then correlate a managed-launch baseline, exact process identity, replay UI,
and post-baseline lifecycle marker in the coordinator. Do not enable VM-read
until that correlation and its revocation/disposal tests are complete.

## Amendment — M2 trusted executable identity (`2026-07-29T17:18:42Z`)

Added a disconnected internal trusted-game identity provider and one shared
Windows executable fingerprint reader. The reader opens the selected file once
with read-only access and `FileShare.Read`, denying new write/delete sharing
while it is inspected. It derives the final canonical path and volume/file
index, reads the pinned version resource, hashes SHA-256 through that same
handle, then revalidates final path and file identity before returning.

Both installed-game trust preparation and the query-only process platform now
use the same fingerprint implementation. The provider starts from deterministic
installation discovery but replaces discovery-supplied path, version, and hash
with the fresh pinned fingerprint, preserves resource/DLC roots, and retains
the stable file identity only inside GameIntegration. It remains disconnected
from the coordinator, launch, lifecycle feed, Application ports, logging, and
memory authority.

A real copied-PE regression caught and fixed an initially synchronous handle
being wrapped as an asynchronous stream; the handle now opens with asynchronous
and sequential-scan options. Tests cover real Windows final-path/file-ID/hash
collection, missing-version failure, cancellation and handle release, provider
projection/failure/cancellation, singleton registration, and process-platform
reuse. Security audit found no High or Medium issue, and independent verification
confirmed no coordinator/lifecycle/memory references.

Validation: `scripts/validate.ps1` passed locked restore, format verification,
Release build with 0 warnings/errors, 367 tests passed, 2 local opt-in tests
skipped, and the repository scan passed.

Deferred: managed-launch preparation must capture a healthy lifecycle
sequence/health-epoch baseline before any process start, without preselecting a
log source. Later correlation must compare the retained file identity plus
canonical path/version/SHA-256 against a fresh process observation. VM-read
remains disabled.

## Amendment — M2 managed-launch preparation (`2026-07-29T17:51:59Z`)

Added a disconnected internal managed-launch preparer in GameIntegration. Each
call freshly re-fingerprints the trusted executable, creates an adapter-owned
32-byte cryptographic correlation encoded as 43-character unpadded base64url,
and captures a healthy lifecycle baseline without selecting a log source. The
result retains the complete trusted executable and file identity plus the
global lifecycle sequence, health epoch, and all source cursors. Its safe
`ToString()` exposes neither the correlation nor executable paths.

Preparation now ends with a producer-side reconciliation barrier instead of a
journal snapshot. Barrier generations ensure a request is completed only by a
pass that began after that request; markers already written are committed
before the returned sequence. A successful barrier also marks carried partial
lines historical, so a pre-baseline line completed afterward cannot appear as
fresh live evidence. Enumeration, read, watcher, revision, producer, and
shutdown failures remain fail-closed. Disposal marks the producer as stopping
under the same lock used to complete barriers, preventing a healthy result from
escaping after shutdown begins.

The preparer and correlation generator are internal singletons. They remain
deliberately disconnected from `GameSessionCoordinator`, process start,
Application ports, Bootstrap, overlay/UI, harness routing, and all memory
authority. This preparation is immutable input only; it grants no session
authorization and performs no launch.

Security review found and prompted fixes for the stale queued-marker snapshot
race, cross-baseline partial lines, correlation shape and synthesized-string
exposure, and disposal overlapping an in-flight barrier. Final re-audit found
no Critical, High, or Medium issue. Independent verification confirmed 101
GameIntegration tests and 14 architecture tests passed and found no
coordinator, public-port, overlay, Bootstrap, or memory wiring.

Validation: `scripts/validate.ps1` passed locked restore, format verification,
Release build with 0 warnings/errors, 380 tests passed, 2 local opt-in tests
skipped, and the repository scan passed for 443 tracked files.

Deferred: the next unit must hand this preparation directly into an immutable
artifact/process-start boundary, then correlate only fresh contiguous
post-baseline lifecycle evidence with the exact observed process identity and
trusted replay UI. Any history gap, gap/fault/reset discontinuity, unhealthy
current feed, identity mismatch, or cancellation must revoke the attempt.
VM-read remains disabled.
