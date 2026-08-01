# Session handoff — 2026-08-01: Managed-launch evidence fixes (#1–#3)

**Author:** Codex Agent
**Branch:** `freebuff/anything-safe-for-deepseek-v4-flash-f0d41ddc-27d9-4317-8e3c-2dfc7ddabb91`
**Head:** `ef53903` — `docs(operations): amend diagnosis status after fixes #1-#3 landed`
**Working tree:** clean after this commit

---

## What was accomplished this session

Root-caused the managed-launch timeout that stalled `OD-RECOVERY-002`, then
implemented and committed the first three of five ranked fixes. The launch
dead-zone is now bounded, evidence is real (not fabricated), and a failed
launch is attributable from the wire. All changes are analysis + code + tests
only — no live game process was started, attached, or scanned.

### Diagnosis (fixes ranked)

`docs/operations/managed-launch-timeout-diagnosis.md` (new): `POST
/api/v1/game/launch` returns `launch.accepted` immediately; evidence arrives
asynchronously from `StartMonitoringLifecycle`, which transitioned to
`OfflineReplayVerified` only on a qualifying `OfflineReplayStarted` marker.
The monitor **fabricated** process evidence (`IsAlive: true`,
`WindowHandle: 1`, `ReplayUiConfirmed: true` hardcoded) and never observed a
marker in the failing run, so the session sat at `Unknown /
launch.awaiting_evidence` forever. Five fixes were ranked; #1–#3 landed here.

### Fix #2 — server-side evidence deadline

- `GameIntegrationOptions.EvidenceDeadline` (default 60 s, validated 5 s–10 min).
- The deadline starts at `RecordManagedLaunch`; if `OfflineReplayVerified` is
  not reached before it elapses, the session transitions to terminal
  `launch.no_evidence` (`ApplyEvidenceDeadline`) and the monitor stops polling.
  The transition is also applied lazily on `GetSnapshotAsync`, so the terminal
  state is observable even if the monitor task dies.
- Verification disarms the deadline — later expiry surfaces as
  `evidence.stale`/`evidence.expired`, never `launch.no_evidence`.
- Late evidence after the terminal state is rejected (`_evidenceDeadlineApplied`).

### Fix #1 — real query-only process observer

- The coordinator now takes `IGameProcessIdentityObserver` (already
  DI-registered; query-only `PROCESS_QUERY_LIMITED_INFORMATION`, no write
  access). The fabricated `GameProcessEvidence` block is gone.
- `BuildObservedProcessEvidence` (static) maps observations: `Available` + PID
  matching the launched child → evidence from the **observed** identity (real
  window handle, path, version, hash, start identity); other-instance PID /
  `Ambiguous` / `QueryFailed` / `Unsupported` → incomplete (keep polling);
  `Absent` → candidate `Exited`.
- Because the child launches hidden (`SwHide`) and window eligibility requires
  a visible `SDL_app` window, `Absent` is disambiguated by `IsChildProcessAlive`
  (query-only `Process.GetProcessById` + `HasExited`): confirmed-dead child →
  terminal `Deny("process.exited_after_launch")` via
  `ReportProcessExitedAfterLaunch`; alive-but-hidden child → keep polling.
- The monitor observes on **every** poll iteration and holds an
  `OfflineReplayStarted` marker as `PendingOfflineReplayMarker`, so a marker
  whose window appears a moment later still verifies (`_lastCursor` is not
  advanced by incomplete evidence, so the pending marker stays cursor-valid).

### Fix #3 — bounded launch correlation on the wire

- `GameSessionSnapshot` (Application) and `GameStateResponse` (ApiContracts)
  each carry a nullable `LaunchCorrelation` — the adapter-generated GUID,
  never a PID, path, or machine-identifying value.
- `GameSessionCoordinator` tracks `_lastLaunchCorrelation`, set on every
  `RecordManagedLaunch` and deliberately not cleared by `RevokeSession`, so
  the owning launch of a failed/denied session remains attributable from
  `GET /api/v1/game/state`. A new launch replaces it; state + reason code
  always disambiguate.
- `GameApiEndpoints.GetGameStateAsync` maps the correlation onto the wire.

### Roadmap + hygiene

- `docs/ROADMAP.md`: "Game path via DI" marked resolved-by-removal — M5
  (`6b71cc8`) deleted the overlay's game-path discovery replica; the overlay
  delegates launch to the host and `GameInstallationDiscovery` is the single
  implementation. No shared utility to extract.
- `research/approaches.md` + `research/memory-analysis.md`: redacted committed
  absolute replay paths (containing a username) to `%LOCALAPPDATA%`
  placeholders — the `scan-repository.ps1` finding that failed the gate.

---

## Changed files

| File | Change |
|------|--------|
| `docs/operations/managed-launch-timeout-diagnosis.md` | **New** — root-cause diagnosis + amendments for fixes #1–#3 |
| `docs/ROADMAP.md` | Marked "Game path via DI" resolved by M5 removal; fixed stale launch-button wording |
| `research/approaches.md`, `research/memory-analysis.md` | Privacy redactions (absolute replay paths → `%LOCALAPPDATA%`) |
| `src/WotBTreader.GameIntegration/GameIntegrationOptions.cs` | `EvidenceDeadline` option + `Validate()` bounds |
| `src/WotBTreader.GameIntegration/Session/GameSessionCoordinator.cs` | Deadline enforcement, real observer wiring, correlation persistence, pending-marker monitor |
| `src/WotBTreader.Application/Game/GameSessionContracts.cs` | `GameSessionSnapshot.LaunchCorrelation` (optional) |
| `src/WotBTreader.ApiContracts/GameContracts.cs` | `GameStateResponse.LaunchCorrelation` |
| `src/WotBTreader.Host.Web/Endpoints/GameApiEndpoints.cs` | Maps `snapshot.LaunchCorrelation` |
| `tests/WotBTreader.GameIntegration.Tests/GameSessionCoordinatorTests.cs` | 17 new tests this session (12 observer/liveness + 5 correlation persistence); 5 deadline tests from the prior session landed in the same commit |
| `tests/WotBTreader.GameIntegration.Tests/GameIntegrationRegistrationTests.cs` | `EvidenceDeadline` bounds test |
| `tests/WotBTreader.Host.Web.Tests/GameApiEndpointsTests.cs` | Correlation mapping tests |

## Public contracts changed

- `GameSessionSnapshot` — new optional `string? LaunchCorrelation = null`.
- `GameStateResponse` — new `string? LaunchCorrelation` (init, null default).
- `GameIntegrationOptions` — new `TimeSpan EvidenceDeadline` (default 60 s,
  validated 5 s–10 min).
- No existing member removed; the overlay's only contract reference
  (`ApiContracts`) remains backward-compatible.

---

## Validation results

| Check | Command | Result |
|-------|---------|--------|
| Full gate | `./scripts/validate.ps1` | Passed — restore, format, build, all 12 test projects, NuGet audit, scan (514 files), 69/69 links |
| Repository scan | `./scripts/scan-repository.ps1` | Passed after redactions (514 tracked files, exit 0) |
| GameIntegration tests | `dotnet test tests/WotBTreader.GameIntegration.Tests -c Release` | 165 passed, 0 failed, 2 opt-in skipped |
| Host.Web tests | `dotnet test tests/WotBTreader.Host.Web.Tests -c Release` | 62 passed |
| Overlay tests | `dotnet test tests/WotBTreader.Overlay.Tests -c Release` | 70 passed (ApiContracts addition backward-compatible) |
| Bootstrap tests | `dotnet test tests/WotBTreader.Bootstrap.Tests -c Release` | 13 passed (DI resolves new ctor param) |
| Whitespace | `git diff --check` | Clean |

## Integration risks

1. **Monitor-loop orchestration not exercised end-to-end.** The pending-marker →
   window-appears → verify path and the confirmed-exit terminal path are each
   unit-tested at the component level (mapping, liveness, `ApplyEvidence`,
   terminal reports), but the `StartMonitoringLifecycle` loop itself is not
   driven directly — the existing stubs throw for the launch pipeline. A
   fake-pipeline harness is the natural next test investment.
2. **Evidence-lifetime bound on window appearance.** Verification requires the
   observed window to appear within the 15 s `EvidenceLifetime` of the marker
   (`Evaluate` flags `evidence.stale` later). Reasonable for a launching game,
   but a slow-to-window child could surface stale instead of verified.
3. **`_lastLaunchCorrelation` is intentionally sticky** across session types
   and terminal states; it is replaced only by the next `RecordManagedLaunch`.
   State + reason code disambiguate, but a plain (non-replay) game launch does
   not clear it — documented behavior, worth knowing before the next session.
4. **Fixes #4/#5 still need a live session** — single-instance probe and
   log-path-vs-user-data-root verification cannot be exercised from code.

## Assumptions and unknowns

- `OD-RECOVERY-003` remains `Planned` in the offset-discovery ledger; fixes
  #1–#3 are the code-side prerequisites and are now committed.
- No live game process was attached this session; the game remains
  single-instance *unverified*.
- The observed identity (real window handle/path/version/hash) must match the
  trusted executable lease for verification — correct by construction, and
  covered by the identity-mismatch tests.

## Commits (newest first)

```
ef53903 docs(operations): amend diagnosis status after fixes #1-#3 landed
d764285 feat(game): bound and observe managed-launch evidence
163590b feat(game): expose bounded launch correlation on session state
22c3565 docs(research): redact absolute replay paths from research notes
cb6f18b docs(roadmap): mark overlay game-path refactor resolved by M5 removal
098c3b5 docs(operations): record managed-launch timeout diagnosis
```

## Recommended next steps

1. **Push the branch / open a PR** when ready — six commits, gate-green,
   reviewer-clean, nothing pushed yet.
2. **End-to-end monitor-loop tests** — build minimal fake pipeline stubs
   (preparer, stager, suspended platform, correlation registrar, thread
   resume) so a fake feed + fake observer can drive `StartMonitoringLifecycle`
   and exercise the pending-marker verify path and the confirmed-exit path.
3. **Offset file hygiene** — align `executableSha256` state in
   `memory-offsets/11.8.0.7.json` / `11.18.0.7.json` with `schema.json` and
   `report-offset-evidence.ps1`.
4. **Live-session recovery prep** — with fixes #1–#3 in place, plan
   `OD-RECOVERY-003`: serve + launch + poll `GET /api/v1/game/state` for
   `launch.no_evidence` / `process.exited_after_launch` / verified, and
   attribute via `LaunchCorrelation`. Fixes #4 (single-instance probe) and #5
   (log-path verification) belong to that session.
