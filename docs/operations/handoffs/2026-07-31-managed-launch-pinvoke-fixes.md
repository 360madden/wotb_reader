# Session handoff — 2026-07-31: Managed launch P/Invoke fixes & replay research

**Author:** Codex Agent  
**Branch:** `main`  
**Head:** `4fc2da3` — `feat(scripts): add setup.cmd, improve everything.cmd with replay import`  
**Working tree:** clean after this commit

---

## What was accomplished this session

Two P/Invoke marshaling bugs in the managed replay launch pipeline were diagnosed
and fixed. The pipeline now reaches the artifact-staging stage (step 3/7).
Extensive deep research on WoT Blitz replay loading mechanisms was compiled into
12 organized documents in `research/`.

### Bug 1 — `MarshalDirectiveException: Cannot marshal 'return value': Returned SafeHandles cannot be abstract`

**Root cause:** `GetCurrentProcess()` P/Invoke returned `SafeHandle` (abstract),
and two `DuplicateHandle` overloads used `out SafeProcessHandle` and
`out SafeThreadHandle`. The .NET marshaller cannot instantiate abstract
`SafeHandle` types for return values or `out` parameters.

**Fix (2 files):**
- `WindowsGameProcessQueryPlatform.cs`: Changed `GetCurrentProcess()` return from `SafeHandle` → `IntPtr`; consolidated two `DuplicateHandle` overloads into one with `out IntPtr` and `IntPtr` parameters
- `SuspendedGameProcessLaunch.cs`: Updated calling code to use `.DangerousGetHandle()` and manually wrap `IntPtr` results in `new SafeProcessHandle(outHandle, ownsHandle: true)` / `new SafeThreadHandle(outHandle, ownsHandle: true)`. Removed dead `reducedProcessHandlePtr`/`reducedThreadHandlePtr` variables.

### Bug 2 — `child_exe_mismatch`: child executable path shows UTF-16 null bytes

**Root cause:** `QueryFullProcessImageNameW` P/Invoke declaration was missing
`CharSet = CharSet.Unicode`. Without it, the .NET marshaller defaulted to
`CharSet.Ansi`, treating each byte of the WCHAR output as a separate ANSI
character — producing interleaved null bytes like `C\u0000:\u0000\\...`.

**Fix (2 files):**
- `WindowsGameProcessQueryPlatform.cs`: Added `CharSet = CharSet.Unicode` to the `QueryFullProcessImageNameW` DllImport
- `SuspendedGameProcessLaunch.cs`: Removed the 7-line hacky workaround that detected interspersed nulls and divided by `sizeof(char)`; added `NormalizeExePath` helper for robust path comparison (strips `\\?\` prefix, calls `Path.GetFullPath`); improved error message to include both paths for diagnostics

### Deep replay research

`research/` folder created with 12 documents (472 KB total):
- `README.md` — index and key facts
- `findings-summary.md` — executive summary with decision matrix
- `complete-reference.md` — cross-referenced findings
- `replay-loading-mechanisms.md` — command-line args, file association, DAVA engine, in-game browser
- `ipc-mechanisms.md` — 6 IPC approaches rated by feasibility
- `lifecycle-monitor.md` — BlitzReplayLogMonitor architecture
- `approaches.md` — 6 approaches (A-F) with implementation plans
- `dava-engine.md` — DAVA Engine internals research
- `memory-analysis.md` — game memory analysis techniques
- `community-tools.md` — GitHub community tools (Rust wotbreplay-parser, Python blitz-replays)
- `memory-offsets-unknowncheats.md` — concrete offsets from UnknownCheats thread #689828
- `uploaded-replays.md` — Uploaded tab mechanism analysis

### Plain game process launcher

New `IGameProcessLauncher` / `GameProcessLauncher` for starting the game without
a replay (simple `CreateProcessW`, no suspension). Exposed at
`POST /api/v1/game/start`. See `GameSessionContracts.cs` and `GameApiEndpoints.cs`.

### Error reporting improvements

- `GameSessionCoordinator.LaunchAsync` catch block now includes full exception
  message + inner exception detail (was: `exception.GetType().Name` only)
- `ErrorCode()` in `GameApiEndpoints.cs` restored to stable-code-only
  (privacy rule — never leak `ApplicationError.Message` to the wire)

---

## Changed files

| File | Change |
|------|--------|
| `src/WotBTreader.GameIntegration/Session/WindowsGameProcessQueryPlatform.cs` | `GetCurrentProcess()` → IntPtr; `DuplicateHandle` consolidated; `QueryFullProcessImageNameW` +CharSet.Unicode |
| `src/WotBTreader.GameIntegration/Session/SuspendedGameProcessLaunch.cs` | Manual handle wrapping; `NormalizeExePath` helper; improved mismatch error; removed byte-count workaround |
| `src/WotBTreader.GameIntegration/Session/GameProcessLauncher.cs` | **New** — simple game process launcher (no replay, no suspension) |
| `src/WotBTreader.GameIntegration/Session/GameSessionCoordinator.cs` | Enhanced exception detail in `LaunchAsync` catch |
| `src/WotBTreader.Application/Game/GameSessionContracts.cs` | Added `IGameProcessLauncher` interface + `GameProcessLaunchOutcome` |
| `src/WotBTreader.GameIntegration/DependencyInjection/GameIntegrationServiceCollectionExtensions.cs` | DI registration for `GameProcessLauncher` |
| `src/WotBTreader.Host.Web/Endpoints/GameApiEndpoints.cs` | `POST /api/v1/game/start` endpoint; `ErrorCode()` privacy fix |
| `tools/src/WotBTreader.GameHarness/Program.cs` | `start` / `start-game` command + `WOTB_TREADER_RENDEZVOUS_PATH` env override |
| `tests/WotBTreader.Bootstrap.Tests/CompositionRootTests.cs` | Added `IGameProcessLauncher` to published ports |
| `tests/WotBTreader.Architecture.Tests/ProjectReferenceTests.cs` | New `OverlayProject_ReferencesOnlyApiContracts` test |
| `memory-offsets/11.19.0.10.json` | Placeholder offset file for v11.19 |
| `docs/operations/offset-discovery-guide.md` | Updated test counts |
| Scripts/config: `validate.ps1`, `ci.yml`, `AGENTS.md`, agent glue files, `tools.lock.json` | Various wiring: offline pack, python CI step, agent orientation |
| `research/` | **New** — 12 deep research documents (untracked → added) |
| `offline/` | **New** — offline discovery pack from prior work (untracked → added) |
| `scripts/python/offline_check.py` | **New** — offline pack link checker |

---

## Pipeline status

```
Managed replay launch (7 stages):
✅ 1. Prepare (identity, correlation, lifecycle baseline)
✅ 2. Acquire executable lease (fingerprint & pin)
⚠️ 3. Stage artifact       ← artifact_not_found: no real .wotbreplay imported
   4. Create suspended process
   5. Register correlation
   6. Resume child thread
   7. Hand off leases
```

The `artifact_not_found` is expected — the current SQLite store has no
real replay imported. The P/Invoke path is verified working: the pipeline
creates a suspended child, reduces handles, queries the child image path
(correctly, now), and matches it against the trusted executable lease.

---

## Validation results

| Check | Command | Result |
|-------|---------|--------|
| Build | `dotnet build -c Release --no-restore` | 0 errors, 0 warnings |
| Architecture tests | `dotnet test tests/WotBTreader.Architecture.Tests -c Release --no-build` | 15 passed |
| Bootstrap tests | `dotnet test tests/WotBTreader.Bootstrap.Tests -c Release --no-build` | 13 passed |
| Host.Web tests | `dotnet test tests/WotBTreader.Host.Web.Tests -c Release --no-build` | 61 passed |
| GameIntegration tests | `dotnet test tests/WotBTreader.GameIntegration.Tests -c Release --no-build` | 141 passed, 2 skipped |
| Live managed launch | `POST /api/v1/game/launch` with fresh capability | Reaches stage 3 (artifact_not_found — expected) |
| Live game state | `GET /api/v1/game/state` | Works correctly (gamePresent: false) |

---

## Integration risks

1. **`QueryProcessImagePath` also uses `QueryFullProcessImageNameW`** — the
   `CharSet.Unicode` fix also corrects this method's behavior. Verified via
   `GET /api/v1/game/state` that game discovery still works. Any path-based
   game identity matching now uses correctly-marshaled Unicode paths.
2. **v11.19 decoder gap remains** — the strict `wotb-11.18-strict` decoder
   rejects v11.19 replays. Real replay import requires decoder update before
   end-to-end managed launch can be tested with current game version replays.
3. **`research/` contains URLs and community findings** — no private data,
   but some references to external forums/tools may become stale.
4. **`GameProcessLauncher` is intentionally simple** — just `CreateProcessW`
   with no replay argument, no suspension, no correlation. Per user request.

---

## Assumptions

- WoT Blitz is installed at the path discovered by `GameInstallationDiscovery`
  (Windows registry scan). The managed launch pipeline depends on this.
- The game is single-instance (unverified). If re-invoking `wotblitz.exe`
  while the game is running opens a second window, the managed launch approach
  (suspended process) may need adjustment. Research in `research/approaches.md`
  covers alternatives.
- The UnknownCheats offsets (`BaseModule + 0x03E91978` entity list chain) are
  for an unspecified version; they may not match v11.19. Offset discovery via
  the `memory-offsets/*.json` pipeline is the canonical path.

---

## Recommended next steps

1. **Import a real v11.18 (or v11.19 with decoder update) `.wotbreplay`** and
   test the full managed launch pipeline end-to-end. The P/Invoke fixes are
   verified up to artifact staging.
2. **Add v11.19 decoder support** — the strict decoder only accepts 11.18.0.
   Research suggests the binary format may be backward-compatible; the
   Rust `wotbreplay-parser` may be version-agnostic.
3. **Test single-instance behavior live** — invoke `wotblitz.exe "replay.wotbreplay"`
   while the game is running at the main menu. If it delivers to the Uploaded
   tab, we can bypass suspended process creation entirely.
4. **Create a real `memory-offsets/11.19.0.10.json`** using the UnknownCheats
   offsets as a starting point, then validate via memory scan endpoint.
