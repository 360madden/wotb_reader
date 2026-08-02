# Session handoff — 2026-08-02: managed replay window recovery

**Author:** Codex Agent

**Branch:** `main`

**Baseline:** `ca96a6a` (`docs(scanner): clarify guarded transition protocol`)

**Commit unit:** visible managed replay launch, exact-window authorization,
research-only lifecycle startup timeout, tests, and operations documentation;
saved in one local checkpoint and not pushed

## Outcome

Managed replay launch is visible again and the offline scanner gate no longer
accepts fabricated window evidence. The launch path now uses normal Windows
display defaults. The coordinator waits for a real visible top-level window
owned by the exact managed PID, verifies the complete process identity, carries
the real window handle into evidence, and revalidates the window throughout the
authorization lease. Window loss, ambiguity, incomplete enumeration, or
identity mismatch fails closed.

The web host also accepts the research-only
`Research:LifecycleEvidenceTimeoutSeconds` setting. The production default
remains 45 seconds; research values from 5 through 300 seconds are validated
and propagated independently from the 5–120 second post-verification replay
evidence lifetime.

Several fresh private-replay launches live-proved the corrected path: exactly
one visible game window appeared, **Watch Offline** was selected, and the
loopback gate reached `OfflineReplayVerified` with
`session.offline_replay_verified`. The user later explicitly authorized the
agent to operate the visible replay controls, so the final transition trials
were performed through the foreground game window.

No offset was found or promoted. Unbounded readable private/mapped Float32
snapshots exceeded the existing 512 MiB retained-data ceiling. A bounded
0–64 MiB address window completed but contained no eligible retained values;
its A→B aggregate remained `previous=0`, `current=0`, `changed=0`, and its
scanner session was discarded. A follow-up attempt to select a populated
private/mapped 64 MiB window internally was interrupted before a result could
be established. The research host was then stopped, which released all
remaining in-memory scanner sessions.

No replay bytes, replay filename, artifact identifier, account data, player
data, screenshot, memory address, observed value, scanner-session identifier,
or candidate was written to the repository. The runtime offset table was not
changed.

## Implementation in the worktree

- `WindowsSuspendedProcessPlatform` no longer sets `STARTF_USESHOWWINDOW` or
  `SW_HIDE`; a regression pins normal default startup flags.
- `GameSessionCoordinator` now depends on `IGameProcessIdentityObserver` and
  does not synthesize `WindowHandle: 1`. A correlated lifecycle marker remains
  pending until a matching real window is observed.
- Managed observation passes the exact expected PID. The Windows platform uses
  `EnumWindows` for that PID and accepts only visible, root, ownerless windows
  with a non-empty client area. It stops after a second match so ambiguity is
  reported without scanning the rest of the desktop.
- Generic, unmanaged observation retains the historical `SDL_app` class
  filter. Exact managed launches deliberately do not rely on that class name,
  because the installed client exposed a valid differently classified window.
- Process start identity, canonical executable path, product version, SHA-256,
  PID, owner PID, and nonzero handle must all match before authorization.
- Once verified, absence or mismatch of the exact window is terminal and
  revokes authorization immediately.
- `TreaderBootstrapOptions`, Bootstrap registration, and Host.Web configuration
  now carry the independent research lifecycle startup timeout.
- Operations and offline discovery guidance document both research bounds.

## Live defects found and corrected

1. The original managed process was deliberately hidden by startup flags.
2. The coordinator substituted a sentinel window handle instead of observing
   the native window.
3. The original window observer hard-coded `SDL_app`; the installed client had
   fresh replay-start evidence and a visible window but did not satisfy that
   class restriction.
4. An intermediate exact-PID enumerator capped total desktop enumeration and
   falsely reported an incomplete observation on a busy desktop. The current
   implementation enumerates the finite OS window list and stops only when
   ambiguity is proven.
5. The 45-second fixed lifecycle startup wait could expire while the offline
   confirmation dialog and client loading were still in progress. The
   research-only 120-second override was live-proven without changing the
   production default.
6. The read API field is `verificationState`, not `state`. Pollers must require
   both `OfflineReplayVerified` and
   `session.offline_replay_verified` before any native read.
7. The snapshot ceiling applies to retained readable memory, before candidate
   filtering. Narrow value bounds do not make an unbounded private/mapped
   snapshot safe. Address windows or a future scanner-side region budget are
   required.
8. Scanner session identifiers are six-digit private engine tokens, not GUIDs.
   They must remain suppressed and be discarded on every completed path.

## Validation performed

- Bootstrap focused suite after timeout composition changes: 14 passed.
- GameIntegration focused suite after exact-PID window changes: 206 passed,
  2 expected local opt-in skips.
- Host.Web Release builds after the composition and observer changes: passed
  with 0 warnings and 0 errors.
- Live visible managed launches: repeatedly reached the exact offline gate;
  zero online-match actions were performed.
- Bounded low-address A→B trial: completed with aggregate zero counts and
  explicit scanner-session discard.
- `git diff --check` at handoff time: passed.
- Final `scripts/validate.ps1` checkpoint gate: passed with locked restore,
  formatting, Release build with 0 warnings and 0 errors, 508 passed tests,
  2 expected local opt-in skips, repository scan, offline freshness, and
  70-link validation.

## Cleanup state

- Game process count after the stop request: zero.
- The exact loopback research host and its `dotnet run` parent are stopped.
- Stopping the host disposed the scanner engine and released any session that
  may have been created by the interrupted final request.
- The launcher was outside the managed research process and was not modified.
- The checkpoint is local only; nothing was pushed.

## Uncommitted files

- `docs/operations/blocker-log.md`
- `docs/operations/offset-discovery-workflow.md`
- `offline/file-tree.md`
- `offline/offset-discovery.md`
- `src/WotBTreader.Bootstrap/Configuration/LocalApplicationPaths.cs`
- `src/WotBTreader.Bootstrap/DependencyInjection/FoundationServiceCollectionExtensions.cs`
- `src/WotBTreader.GameIntegration/Session/GameProcessIdentityObserver.cs`
- `src/WotBTreader.GameIntegration/Session/GameSessionCoordinator.cs`
- `src/WotBTreader.GameIntegration/Session/SuspendedGameProcessLaunch.cs`
- `src/WotBTreader.GameIntegration/Session/WindowsGameProcessQueryPlatform.cs`
- `src/WotBTreader.Host.Web/Program.cs`
- `tests/WotBTreader.Bootstrap.Tests/CompositionRootTests.cs`
- `tests/WotBTreader.GameIntegration.Tests/GameProcessIdentityObserverTests.cs`
- `tests/WotBTreader.GameIntegration.Tests/GameSessionCoordinatorTests.cs`
- `tests/WotBTreader.GameIntegration.Tests/SuspendedGameProcessLaunchTests.cs`
- this handoff

## Next move

1. Amend `BLK-0023` with the live resolution: normal visible launch, real
   nonzero window evidence, continuous exact-window revalidation, and repeated
   `OfflineReplayVerified` proof.
2. Amend `BLK-0024` with the live resolution: the explicit 120-second research
   startup wait permitted offline confirmation and loading while the 45-second
   default remained unchanged.
3. Append a new immutable blocker entry for the stale `SDL_app` restriction and
   the intermediate desktop-enumeration cap. Record the exact-PID `EnumWindows`
   correction and its fail-closed behavior; do not rewrite earlier entries.
4. Decide whether the zero-count low-address trial belongs in
   `OD-RECOVERY-004` as bounded negative setup evidence. Do not classify the
   interrupted populated-slice attempt as scan evidence.
5. Before another live trial, implement or formally design a privacy-safe
   scanner-side way to select/page bounded private/mapped regions without
   exposing process-specific addresses. Prefer a retained-byte/region budget or
   an internal bounded-slice selector over raising the 512 MiB ceiling.
6. Add regression coverage for a busy desktop with more than the old cap before
   the exact matching window, plus post-verification exact-window loss.
7. The offline refresh and full repository gate passed during checkpoint
   preparation. Rerun them after any further source or documentation changes.
8. Continue from the local checkpoint; do not push unless the owner asks.
