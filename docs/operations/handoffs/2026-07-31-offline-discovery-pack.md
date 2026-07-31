# Session handoff — 2026-07-31: Offline discovery pack

**Author:** Codex Agent  \n**Branch:** `main`  \n**Head:** `4fc2da3` — `feat(scripts): add setup.cmd, improve everything.cmd with replay import`  \n**Working tree:** mixed — this session's changes uncommitted, plus other pre-existing in-flight work (see "Integration risks")

---

## What was accomplished this session

Created a **focused offline discovery pack** at the repo root (`offline/`): a
self-contained, link-validated index that lets an agent or human orient without
scanning the full tree or using the network. It is wired into every harness and
the validation gate.

### New files

| File | Purpose |
|------|---------|
| `offline/README.md` | Pack index: purpose, contents table, reading order, validation command, maintenance rules |
| `offline/repo-map.md` | Annotated project layout (root, src modules, tests, tools) |
| `offline/entry-points.md` | First-files-to-read, routed by task |
| `offline/api-surface.md` | Read + game API routes, caps, SignalR hub, rendezvous, contracts |
| `offline/glossary.md` | Domain terms + frequently-cited BLK refs |
| `offline/commands.md` | Build/test/run commands, basher timeouts, startup sequence |
| `offline/replay-format.md` | `.wotbreplay` structure, constants, pickle/protobuf boundary, event packets |
| `offline/offset-discovery.md` | Game launch → multi-scan → memory-offsets evidence publication |
| `offline/data-flow.md` | Telemetry from decode → SQLite → read API/SignalR → overlay & comparison |
| `offline/memory-offsets.md` | Offset evidence schema, `OffsetTableReader` validation, runtime gating |
| `offline/file-tree.md` | Physical snapshot of all committed files (`git ls-files`) for path resolution |
| `scripts/python/offline_check.py` | Stdlib-only link checker: validates every internal link in `offline/*.md` (fences/titles/fragments handled); exit 0/1 |

### Modified files (8)

| File | Change |
|------|--------|
| `AGENTS.md` | Offline-discovery paragraph (pack contents + link-check command); 3 new route-by-task rows (replay format, data flow, offset/memory evidence) |
| `scripts/validate.ps1` | Added `Invoke-CheckedNative python …/offline_check.py 'Offline pack link check'` after the scan phase |
| `.github/workflows/ci.yml` | Added `actions/setup-python@v5` (3.x) after `setup-dotnet`; added `Check offline discovery pack links` step after the scan step |
| `.cursor/reference/canonical-paths.md` | Added "Offline discovery pack" row |
| `.opencode/agents/deepseek-glue.md` | Added sentence: load `offline/README.md` for fast orientation |
| `.grok/agents/grok-glue.md` | Same orientation sentence |
| `scripts/python/README.md` | Documented `offline_check.py` |
| `tests/WotBTreader.Architecture.Tests/ProjectReferenceTests.cs` | New `OverlayProject_ReferencesOnlyApiContracts` test pinning the Overlay's actual csproj references to exactly `ApiContracts` (`FirstOrDefault` + `Assert.IsNotNull`, MSTest 4 `[NotNull]` flow analysis) |

**Public contracts changed: none** — docs-only plus one new test and one new
validation script. No wire shapes, DI ports, or domain types were altered by
this session's changes.

### Validation results

| Check | Command | Result |
|-------|---------|--------|
| Pack links | `python scripts/python/offline_check.py` | **11 files, 53 links, 0 broken, exit 0** |
| Architecture suite | `dotnet test tests/WotBTreader.Architecture.Tests -c Release` | **15/15 passed** (incl. new overlay pin) |
| Repository scan | `scripts/scan-repository.ps1` | **477 tracked files clean, exit 0** |
| validate.ps1 syntax | PowerShell AST parse | **OK** |
| Full gate | `./scripts/validate.ps1` | **RED — 2 test failures, both from OTHER in-flight work (see below)** |

### Review loop

Three `code-reviewer` passes across the feature. Findings raised and fixed:

| Finding | Resolution |
|---------|-----------|
| `offline/api-surface.md` heading called the whole game API "mutation-gated" (GETs aren't) | Reworded: "write endpoints require the mutation capability" |
| `data-flow.md` claimed the overlay tracks sequences via `StreamSequenceTracker` (impossible — Overlay references only ApiContracts) | Reworded: host-side tracking only; overlay refreshes on event/snapshot kinds + polling fallback |
| `offline_check.py` scanned inside fenced code blocks | Fence-state tracking added |
| Dead title-handling guard in link regex | Real title-strip loop added |
| Test comment cited fabricated `BLK-0010` | Removed (not in blocker log) |
| `.Single(...)` → opaque exception if overlay csproj missing | `FirstOrDefault` + `Assert.IsNotNull` with message |
| `offline/file-tree.md` stale-by-construction (pack untracked pre-commit) | Header note added: uncommitted files absent until next refresh |
| CI used unpinned runner Python | `actions/setup-python@v5` added |

---

## Unresolved / integration risks

1. **Full gate is red — two test failures NOT caused by this change set.** This
   session's diff touches zero GameHarness/Host.Web launch-path files, but the
   shared checkout contains other pre-existing uncommitted work
   (`GameSessionCoordinator.cs`, `SuspendedGameProcessLaunch.cs`,
   `WindowsGameProcessQueryPlatform.cs`, `GameHarness/Program.cs`,
   `GameApiEndpoints.cs`, `GameSessionContracts.cs`, `CompositionRootTests.cs`,
   `memory-offsets/11.19.0.10.json`, new `Session/GameProcessLauncher.cs`,
   untracked `research/`). Two tests fail there:
   - `ScanIsDeniedBeforeItCanAttachToTheRequestedProcess` (GameHarness.Tests) —
     expects exit code 3 (`UnsupportedCapability`), actual 5.
   - `LaunchFailureExposesOnlyTheStableErrorCode` (Host.Web.Tests) — expects
     message `launch.game_unavailable`, actual
     `launch.game_unavailable | C:\secret\ga...` (synthetic test-fixture path).
     **This leaks a full path in an API error message — a privacy-rule
     violation** in the in-flight work.
2. **`offline/file-tree.md` excludes uncommitted files by design** — after this
   pack is committed, re-run the snippet in its header so the snapshot includes
   the pack itself.
3. **`validate.ps1` now requires `python` on PATH locally.** The repo already
   ships python tooling (`scripts/python/`, `.venv`), so this matches
   conventions; CI pins Python via `setup-python@v5`.
4. **Two stray committed paths** `%~dp0.data/...` exist from a past cmd-wrapper
   quoting bug — noted in the pack; do not create new paths like that.

## Assumptions

- Python 3.x is available (`python` on PATH; 3.14.6 verified locally).
- GitHub `windows-latest` runners accept `actions/setup-python@v5` and the new
  CI step runs `offline_check.py` from the repo root (script resolves its own
  root via `__file__`, so cwd is irrelevant).
- The Overlay's reference isolation is a hard architectural invariant
  (enforced pre-existing via `OverlayAllowedReferences`; now also pinned to the
  real csproj).

## Recommended next steps

1. **Commit the pack as one cohesive unit**: all `offline/` files +
   `offline_check.py` + validate.ps1 + ci.yml + AGENTS.md + harness configs +
   the new architecture test. Conventional message: `feat(docs): add offline
   discovery pack with link-validated CI/gate wiring`.
2. **Re-run `git ls-files | sort > offline/file-tree.md` splice after commit.**
3. **Investigate the two failing tests in the in-flight work** (owner:
   whoever is working on the plain game-process launch feature): the
   `launch.game_unavailable | C:\...` message must be scrubbed to the stable
   code (privacy), and GameHarness `scan` exit-code semantics need reconciling.
4. Optionally wire `offline_check.py` into a pre-commit hook or keep it in the
   gate only — the gate + CI coverage is already in place.

---

## Amendment (2026-07-31, same session) — gate GREEN

Both previously-failing tests were fixed; `./scripts/validate.ps1` now passes
end-to-end (restore → format → build → 12 test projects → scan → offline link
check, exit 0).

### Fix 1 — Host.Web error-message path leak (`GameApiEndpoints.cs`)

`ErrorCode()` had been changed by in-flight work to return
`$"{code} | {message}"`, leaking `ApplicationError` messages (which contain
absolute paths) onto the wire — a privacy-rule violation that also broke
`LaunchFailureExposesOnlyTheStableErrorCode`. Restored to stable-code-only:
`string.IsNullOrWhiteSpace(error?.Code) ? "launch.failed" : error.Code`.

### Fix 2 — GameHarness containment test hermeticity

`ScanIsDeniedBeforeItCanAttachToTheRequestedProcess` returned exit 5
(`ConflictOrBusy`) instead of 3 (`UnsupportedCapability`) because a **live web
host (PID 44520) was listening on 127.0.0.1:9182** with a valid rendezvous
record — the black-box harness found the host and attempted a gate check. The
blocker-log precedent (2026-07-30) was to kill the orphan host; here the host
was alive and possibly in use, so the tests were made deterministic instead:

- `GameHarness/Program.cs` `ReadRendezvousUrl()` honors a new
  `WOTB_TREADER_RENDEZVOUS_PATH` env override (falls back to LocalApplicationData),
  matching the existing `WOTB_TREADER_GAME_ROOT` override convention.
- `GameHarnessCommandContainmentTests` sets it to a GUID-named temp path that
  cannot exist, so `scan`/`probe` deterministically take the no-host path.

### Result

- `WotBTreader.Host.Web.Tests`: 61/61 passed
- `WotBTreader.GameHarness.Tests`: 28/28 passed
- Full gate: 0 failed, 2 opt-in skips, scan 477 clean, link check 0 broken, exit 0
- Reviewer pass: all three changes approved with no follow-ups

**Note for the in-flight launch work:** `ErrorCode()` returning the message was
likely an intentional debugging aid — if richer launch errors are wanted,
deliver detail via a separate field (e.g. `detail`), never by concatenating
into `Message`. Also `memory-offsets/11.19.0.10.json` and the `research/`
folder remain part of that other uncommitted work; untouched here.

---

## Amendment 2 (2026-07-31, same session) — file-tree refresh self-gating

### What changed

The pack was committed at HEAD `ef7ad42` (with the managed-launch fix commit),
making the pre-commit `file-tree.md` snapshot stale by construction (27
committed files missing: the pack itself, `offline_check.py`, `research/`,
`GameProcessLauncher.cs`, the new handoffs). Two rounds addressed this:

**Round 1 — snapshot + research routing (reviewer-approved):**

- `offline/file-tree.md` regenerated from `git ls-files | sort` (504 files,
  byte-identical to the live tree, verified programmatically). Header note
  updated: uncommitted work absent by design; regenerate in the same change
  that adds/renames/removes files.
- `offline/repo-map.md`, `offline/entry-points.md`, `offline/README.md` now
  route the committed `research/` folder (12 deep-research docs) via
  `research/README.md`.
- `research/README.md` count drift fixed: header claimed 9 files but the table
  had 10 rows and 12 actual docs; now "11 files" with the missing
  `memory-offsets-unknowncheats.md` row added.
- `offline_check.py` extended to also link-check `research/README.md` (the
  pack's canonical research index); per-file log lines now use repo-relative
  paths so the two READMEs aren't ambiguous in the log.
- `AGENTS.md` route-by-task gains a "Game internals research" row.

**Round 2 — staleness now fails the gate (reviewer-approved):**

- `offline_check.py` gains `--refresh` (regenerate `file-tree.md` from
  `git ls-files`, then link check; idempotent) and `--check-fresh` (exit 1
  with an actionable message if the committed snapshot is stale, then link
  check). The `FILE_TREE_HEADER` constant lives in the script; the file's
  header was regenerated to match.
- `scripts/validate.ps1` and `.github/workflows/ci.yml` now run
  `offline_check.py --check-fresh`, so a stale snapshot fails the build — the
  stale-by-design failure mode is eliminated, not just documented.
- Docs updated: `offline/README.md`, `scripts/python/README.md`, `AGENTS.md`.

### Validation

- `--check-fresh` on the current tree: exit 0 ("up to date")
- `--refresh` twice in a row: second run reports "already up to date" (idempotent)
- Negative test — injected a `FAKE/STALE/ENTRY` line into `file-tree.md` body:
  `--check-fresh` exited 1 with missing/extra counts + fix instruction;
  restored file passes again
- Link check: 12 files, 67 links, 0 broken, exit 0
- `validate.ps1` PowerShell AST: syntax OK
- Reviewer: three passes (checker modes, gate wiring + docs, cosmetic log
  path fix) — all approved, zero outstanding items

### Unresolved / integration notes

- Working tree still carries other in-flight modifications (Replays decoder
  files, `GameIntegrationOptions.cs`, Host.Web tests) not part of this change
  set — left untouched.
- `research/README.md` links are now gated (checked by `offline_check.py`),
  but its non-index docs are not scanned for internal links — acceptable:
  the index is the curated surface.

### Recommended next steps

1. **Commit this refresh as one unit**: file-tree regeneration + research
   routing + checker `--refresh`/`--check-fresh` modes + validate.ps1/ci.yml
   flag + the four doc updates.
2. Optionally add a pre-commit hook that runs `--check-fresh` (gate + CI
   coverage already exists).
