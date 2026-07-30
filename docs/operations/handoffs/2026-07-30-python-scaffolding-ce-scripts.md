# Session handoff — 2026-07-30: Python scaffolding, Cheat Engine scripts, doc updates

**Author:** Codex Agent
**Branch:** `main`
**Commits:**
- `e06a520` — docs: update test counts, mark minimap textures and multi-scan engine complete
- `53c3c86` — feat(tools): add Cheat Engine Lua scripts for automated offset discovery
- `4352499` — feat(scripts): add Python scaffolding for E2E smoke tests and offset validation
**Total:** 3 files changed (+619), 3 files created (+687)
**Tests:** 375 passed, 0 failed, 2 skipped across all 12 projects
**Build:** 0 errors, 0 warnings (Release)
**Scan:** 468 files clean

---

## What was accomplished

### Python scaffolding — new subsystem (`scripts/python/`)

Created a Python tooling layer using the system Python (3.14.6) with a local venv
at `.venv/` (already gitignored). Zero pip installs required — all scripts use
stdlib only. Follows conventions from `scripts/ghidra-scan.py` (timestamped logs
to `.build/`, `REPO_ROOT` from `__file__`, `main()` with `if __name__ == "__main__":`).

#### New files (3)

| File | Summary |
|------|---------|
| `scripts/python/e2e_smoke.py` | Full E2E smoke test: finds free port → `dotnet publish` → starts host via subprocess with `Web__Port` and `Paths__ApplicationDataRoot` env vars → hits all 7 API endpoints → validates JSON schemas → cleans up. **24/24 assertions pass.** |
| `scripts/python/offset_check.py` | Validates all `memory-offsets/*.json` files for schema compliance. Checks `executableSha256` format, filename vs `gameVersion`, all 8 fields (known/unknown), offset plausibility, confidence, `discoveredAtUtc`. |
| `scripts/python/README.md` | Setup and usage documentation for both scripts. |

#### Key design decisions

- `Web__Port` env var replaces `--urls` flag — the host's `Program.cs` uses
  `ConfigureKestrel()` which binds to config key `Web:Port`, overriding any `--urls`
  CLI argument. This was discovered during testing when the host silently failed
  to bind to the requested port.
- `http_get()` accepts `parse_json=False` for HTML dashboard pages — the default
  `parse_json=True` is for API endpoints that return JSON.
- Host subprocess drains stdout via a daemon thread to prevent buffer deadlock.

### Cheat Engine Lua scripts — `tools/cheat-engine/`

Two scripts for automated offset discovery using Cheat Engine 7.5+'s Lua API.
Cheat Engine is installed at `C:\Program Files\Cheat Engine`. The game is currently
running with a "WoT Blitz" window (PID 54412), making offset discovery immediately
possible.

#### New files (3)

| File | Summary |
|------|---------|
| `tools/cheat-engine/discover-offsets.lua` | Neighborhood scanner: auto-attaches to `wotblitz.exe`, reads memory ±1024 bytes around known `playerYaw` (0x0317A810), reports float/int32/double values filtered by plausibility, saves top 30 closest candidates to JSON. Uses CE built-in `readFloat`/`readInteger`/`readDouble` directly (no wrapper functions). |
| `tools/cheat-engine/multiscan.lua` | Interactive multi-scan engine: `scanInteractive()` for first unknown-value scan, `nextScan(changed|unchanged|increased|decreased)` for iterative filtering, `nextScanValue()` for exact value matching, `showCandidates()` and `saveDiscovered()` for output. Uses correct CE 7.5 `firstScan` (18-param) and `nextScan` (8-param) signatures. |
| `tools/cheat-engine/README.md` | Prerequisites, usage docs, integration guide for both scripts. |

#### Code review fixes applied

| Finding | Fix |
|---------|-----|
| Infinite recursion via local `readFloat`/`readInteger`/`readDouble` wrappers | Removed all three local function declarations; use CE built-ins directly |
| `dwordToByteTable()` doesn't exist in CE Lua API | Removed `valueHex` field from output |
| `scan.OnlyOneResult` property doesn't exist | Removed assignment |
| `scan.firstScan()` / `scan.nextScan()` wrong parameter counts | Matched CE 7.5 API signatures (18 params / 8 params) |
| `pairs()` used for array serialization | Added proper gap-checking array detection + `ipairs()` iteration |

### Documentation updates

| File | Change |
|------|--------|
| `docs/ROADMAP.md` | Test counts → 395 passed, 0 failed, 2 skipped; minimap textures moved from deferred to done; multi-scan engine and neighborhood scanner documented; scan file count → 468 |
| `README.md` | Test counts → 395/0/2; feature progress bar → only live HUD smoke test remains; `GameIntegration.Tests` count corrected to 141 |
| `knowledge.md` | Test counts → 397 tests, 395 passed |

---

## Bugs found and fixed this session

| Bug | Root cause | Fix |
|-----|-----------|-----|
| **GameHarness tests (2) failing** — returned exit code 5 (ConflictOrBusy) instead of 3 (UnsupportedCapability) | Stale rendezvous file from a previous host run (PID 35808) was still present. `ReadRendezvousUrl()` found the host URL, attempted gate check, returned ConflictOrBusy. | Cleaned rendezvous file, killed orphaned host process. Tests now pass. |
| **E2E smoke test host didn't start** — `--urls http://127.0.0.1:<port>` silently ignored | `Program.cs` uses `ConfigureKestrel()` which binds to `Web:Port` config key (default 9182), ignoring `--urls`. Host always tried port 9182. | Switched to `Web__Port` env var (ASP.NET Core config convention). |
| **Dashboard page tests returned HTTP -1** — `http_get` always called `json.loads()` on HTML responses | Dashboard pages return HTML, not JSON. `json.loads` raised `JSONDecodeError`, caught by generic `except Exception`. | Added `parse_json=False` parameter for HTML endpoints. |
| **Dead import `signal`** in `e2e_smoke.py` | Initially imported for process management, then unused after refactoring to `subprocess.Popen`. | Removed. |

---

## Unresolved

1. **7 of 8 offsets remain unknown** — `memory-offsets/11.19.0.10.json` has only
   `playerYaw=0x0317A810` (51808784). `playerHP`, `playerPositionX/Y/Z`,
   `cameraPitch`, `aliveTankCount`, and `replayTime` are all zeros. The game is
   running on the desktop (PID 54412, "WoT Blitz" window) but a replay needs to be
   playing and the gate needs to be satisfied for the C# scanner to work.
   Cheat Engine scripts in `tools/cheat-engine/` provide a separate path.

2. **Host-based launch pipeline conflicts with already-running game** — the
   coordinator's `LaunchAsync` creates a new suspended `wotblitz.exe` process.
   With 3+ wotblitz processes already running, launch fails. A "latch onto
   existing process" mode would bypass this.

3. **Legacy offset files missing SHA256** — `offset_check.py` found 4 issues:
   `11.8.0.7.json` and `11.18.0.7.json` both lack `executableSha256` and
   `discoveredAtUtc`. These are placeholders from the initial Ghidra analysis
   and should be either fixed or removed.

4. **CE Lua `getCurrentMemScan()` casing uncertain** — Cheat Engine's Lua API
   has inconsistent method naming. If the function is `getCurrentMemscan()`
   (lowercase 's') instead of `getCurrentMemScan()`, the interactive functions
   in `multiscan.lua` will silently no-op. Verify by running
   `print(getCurrentMemScan)` in CE's Lua Engine.

5. **Host E2E smoke tested on ephemeral port but not on production port 9182** —
   the script finds a free port and tests against it. A smoke test against the
   standard port 9182 (with `serve.cmd`) has not been run since the docs update.

---

## Recommended resume steps

1. **Discover offsets via Cheat Engine** — launch CE, load `discover-offsets.lua`
   to scan around playerYaw, then use `multiscan.lua` for interactive
   changed/unchanged filtering while a replay plays.

2. **Fix legacy offset files** — add `executableSha256` and `discoveredAtUtc`
   to `11.8.0.7.json` and `11.18.0.7.json`, or delete them if they're
   permanently placeholder.

3. **Build a discover-runner Python script** — reads the rendezvous file, checks
   gate state, runs `POST /api/v1/game/discover/snapshot`, waits for user input,
   runs `POST /api/v1/game/discover/compare`, and reports candidates. This
   automates the multi-scan workflow without requiring Cheat Engine.

4. **Run E2E smoke test against serve.cmd** — verify the full stack works on
   the standard port 9182 with the published output, including the overlay
   rendezvous.
