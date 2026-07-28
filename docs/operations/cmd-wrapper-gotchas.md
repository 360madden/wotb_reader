# CMD Wrapper Gotchas

Last updated: 2026-07-28 (post-bug-sweep)

This document catalogues every cmd.exe failure mode discovered (or missed) in the
repo-root `.cmd` wrapper scripts. It exists so the same bugs are never
reintroduced and so future reviewers have a checklist to run against any new or
modified wrapper.

## Canonical bug catalogue (2026-07-28)

These are the bugs discovered during the post-hoc bug sweep. They are
catalogued here as canonical examples of each failure mode. Once fixed,
update the Status column and reference the fixing commit.

| # | Script | Severity | Description | Status |
|---|--------|----------|-------------|--------|
| 1 | `everything.cmd` | 🔴 Blocking | Unquoted `%~dp0` inside `cmd /c "cd /d %~dp0 ..."` — fails if repo path contains spaces | Fixed — removed redundant `cd /d` from `start` commands; scripts handle their own `cd /d` |
| 2 | `import.cmd` | 🔴 Blocking | `setlocal enabledelayedexpansion` corrupts filenames containing `!` — `set "FILE_!N!=%%f"` expands `%%f` while delayed expansion is active, destroying `!` characters | Fixed — file scanning moved before `setlocal enabledelayedexpansion`, uses `call` pattern for runtime expansion |
| 3 | `import.cmd` | 🔴 Blocking | `!` in `%~1` for drag-and-drop args — the `if "%~1"=="" goto done` check runs BEFORE `setlocal disabledelayedexpansion`, causing parse errors | Fixed — outer scope now `disabledelayedexpansion`, drag-and-drop path uses `%ERRORLEVEL%` |
| 4 | `import.cmd` | 🔴 Blocking | Whitespace-only input at interactive prompt crashes — `for /f "delims=..."` yields no tokens, `NUM` becomes a space, `if !NUM! LSS 1` becomes `if   LSS 1` → syntax error | Fixed — added `set "CHOICE=!CHOICE: =!"` to strip whitespace before validation |
| 5 | `serve.cmd` | 🟡 Leak | No `setlocal` — `Web__Port` and `Paths__ApplicationDataRoot` leak into caller environment | Fixed — added `setlocal` at top |
| 6 | `overlay.cmd` | 🟡 Leak | No `setlocal` — `OVERLAY` and `WOTBTREADER_DATA_ROOT` leak into caller environment | Fixed — added `setlocal` at top |
| 7 | `everything.cmd` | 🟡 Leak | No `setlocal` — `WEB_PORT` leaks into caller environment | Fixed — added `setlocal` at top |
| 8 | `watch.cmd` | 🟡 Leak | No `setlocal` — `CLI` leaks into caller environment | Fixed — added `setlocal` at top |
| 9 | `validate.cmd` | 🟡 | Hard dependency on `pwsh` (PowerShell Core) — fails on systems with only `powershell.exe` | Fixed — falls back to `powershell` if `pwsh` not found |
| 10 | `serve.cmd` | 🟡 | `dotnet publish --no-restore` fails on fresh clone where restore hasn't run | Fixed — added `project.assets.json` existence guard with clear error message |

## CMD failure mode catalogue

### 1. Delayed expansion + `!` in filenames

```batch
setlocal enabledelayedexpansion
for %%f in (*.wotbreplay) do set "FILE_!N!=%%f"
```

When `enabledelayedexpansion` is active, `!` characters in `%%f` are
interpreted as variable delimiters. A file named `battle!test.wotbreplay`
becomes `battletest.wotbreplay` or causes a parse error.

**Rule:** Always `setlocal disabledelayedexpansion` before any loop that
captures filenames into variables, or pass filenames through `%1`/`%2`/etc.
(the `%` form is safe). Only enable delayed expansion when you genuinely need
`!VAR!` syntax.

### 2. `!` in `%~1` with delayed expansion

```batch
setlocal enabledelayedexpansion
if "%~1"=="" goto done    ← %~1 expands at parse time, but ! in value breaks
```

`%~1` resolves at parse time when `enabledelayedexpansion` is active, so any
`!` in the argument corrupts the comparison. The fix is to disable delayed
expansion before the check, or use `%1`-only syntax.

### 3. Unquoted `%~dp0` in nested `cmd /c`

```batch
start "title" cmd /c "cd /d %~dp0 && call serve.cmd"
```

If `%~dp0` is `C:\work\my project\`, the unquoted space breaks `cd /d`. Fix:

```batch
start "title" cmd /c "cd /d "%~dp0" && call serve.cmd"
```

### 4. Whitespace input → arithmetic crash

```batch
set /p CHOICE="Pick: "
for /f "delims=0123456789" %%v in ("!CHOICE!") do set "NUM="
if "!NUM!"=="" goto invalid   ← a space passes this because space ≠ ""
if !NUM! LSS 1 ...             ← expands to "if   LSS 1" → syntax error
```

**Rule:** Always trim input before validation: `set "CHOICE=!CHOICE: =!"` to
strip spaces, or check `if defined NUM` instead of `if "!NUM!"==""`.

### 5. Environment variable leaking

```batch
REM No setlocal — these persist after script exits
set Web__Port=9182
set Paths__ApplicationDataRoot=C:\...
```

**Rule:** Every `.cmd` that sets environment variables for internal use must
wrap with `setlocal` / `endlocal`, or at minimum document that the variables
intentionally export to the caller.

### 6. `--no-restore` assumption

`serve.cmd` runs `dotnet publish --no-restore`, assuming a prior `build.cmd`.
A fresh clone user who runs `serve.cmd` first gets a confusing restore error.

### 7. `pwsh` vs `powershell`

`pwsh` is PowerShell Core (v6+), installed with modern .NET SDKs.
`powershell` is Windows PowerShell (v5.1), built into Windows 10/11.
`validate.cmd` hardcodes `pwsh` and fails on systems without it.

### 8. `--no-restore` assumption

`serve.cmd` runs `dotnet publish --no-restore`, assuming a prior `build.cmd`
or `dotnet restore`. A fresh clone user who runs `serve.cmd` (or `everything.cmd`,
which calls `serve.cmd`) first gets a confusing restore error instead of a
clear message to run `build.cmd` first.

**Rule:** Either remove `--no-restore` so the publish always restores, or add an
explicit pre-flight check that `obj/project.assets.json` exists with a clear
error message directing the user to run `build.cmd` first.

## Review checklist

When creating or modifying any `.cmd` file, verify:

- [ ] All `%~dp0` expansions inside nested `cmd /c` strings are quoted
- [ ] `setlocal enabledelayedexpansion` is only used where `!VAR!` is needed
- [ ] Filenames from `for` loops or `%1` arguments are handled with delayed expansion OFF
- [ ] User input (especially from `set /p`) is trimmed before arithmetic comparison
- [ ] All `set` for internal use is guarded by `setlocal`
- [ ] Scripts that invoke `dotnet` commands don't assume prior steps without documenting the prerequisite
- [ ] External tool dependencies (`pwsh`, `node`, etc.) are checked with a fallback or clear error
- [ ] `start` commands use the empty-title trick: `start "" "exe path"` to avoid the first quoted arg being consumed as a window title

## Related: rendezvous ACL crash (2026-07-28)

The `UnauthorizedAccessException` that appeared during `import.cmd` smoke-testing
was NOT a wrapper bug. It was a C# bug in `LocalApplicationPaths.EnsureWindowsOwnerOnlyDirectory`:

- **Cause:** `directory.SetAccessControl(security)` throws `UnauthorizedAccessException`
  when the current user lacks `WriteDAC` rights on the rendezvous directory.
  This happens when an elevated admin creates the directory first.
- **Impact:** The entire CLI crashes during startup (`BuildHost` → `EnsureDirectoriesExist`)
  before any command executes. Even `--help` would fail.
- **Fix:** Wrapped `SetAccessControl` in try-catch for `UnauthorizedAccessException`.
  Silently accept existing ACLs; if the user truly can't write to the directory,
  the failure surfaces at the point of use (writing the rendezvous token) rather
  than globally during bootstrapping.
- **Lesson:** When a wrapper appears broken because the CLI crashes, the root cause
  may be in C# startup code, not the wrapper. Trace the full execution path.

## Process lesson (2026-07-28)

These bugs survived multiple prior bug sweeps because they were reviewed through
static reading alone. Three of the blocking bugs (`!` in filenames, whitespace
crash, unquoted paths) are classic cmd.exe edge cases that require **active
adversarial reasoning** to catch — exactly what a thinker agent provides.

**Standing rule:** Any non-trivial review of cmd/batch/PowerShell scripts must
be routed through a thinker agent with the actual file contents. Do not rely on
manual static reading for these file types.

## Basher timeout guide (2026-07-28)

Basher (terminal agent) timeouts are a recurring waste pattern in this project.
The default 30s timeout is never adequate for .NET commands.

| Command | Minimum timeout | Notes |
|---------|----------------|-------|
| `dotnet build WotBTreader.sln -c Release` | 300s | Restore + build across 12 projects |
| `dotnet test WotBTreader.sln -c Release --no-build` | 300s | 253+ tests across 12 projects |
| `dotnet test tests/<project> -c Release` | 120s | Single test project |
| `dotnet publish src/WotBTreader.Host.Web -c Release` | 180s | Publish with static web assets |
| `dotnet restore` | 120s | Locked-mode restore |

**Rules:**
- Never run interactive `.cmd` wrappers through basher (`import.cmd` no-args,
  `everything.cmd`, `serve.cmd` with TTY). These expect terminal input or spawn
  windows — they hang or exit immediately.
- Use direct `dotnet` commands, not `.cmd` wrappers, for headless execution.
- Verify prerequisites before running: check the CLI is built before running
  CLI-dependent commands, check packages are restored before `--no-restore`.
- Set `timeout_seconds` explicitly; never rely on the 30s default.
