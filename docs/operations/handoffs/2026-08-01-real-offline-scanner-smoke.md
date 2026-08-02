# Session handoff — 2026-08-01: real offline scanner smoke and WOW64 support

**Author:** Codex Agent
**Branch:** `main`
**Baseline:** `9e0aadd` (`fix(scanner): harden lifecycle and overlay integration`)
**Commit unit:** focused source, test, offline-pack, and operations-documentation changes; no push requested

## Outcome

The previously deferred real-process smoke is complete. A private replay was
imported without publishing its filename, path, bytes, or player data; the
managed launch reached `OfflineReplayVerified`; and the guarded scanner
completed a bounded neighborhood, snapshot, comparison, and discard cycle.
Authorization expiry then moved the session to `EvidenceStale` and terminated
the managed child. No game or web-host process remained after cleanup.

## Problems found and fixed

### Multi-source lifecycle correlation

- Launch correlation previously selected one arbitrary native-log source from
  a baseline containing multiple active sources.
- Deleted-source tombstones could remain in the launch baseline.
- A new native log created after a healthy reconciliation was marked
  historical, causing a legitimate replay-start marker to fail cursor checks;
  the first repair was too broad because first observation alone could also
  bless stale prepopulated bytes.

The journal now snapshots active sources only and records the completion time of
each successful reconciliation. The managed launch context owns a defensive
copy of every source baseline. A newly enumerated generation-one source is live
only when its native file creation time and parsed marker timestamp are both at
or after that barrier; the coordinator independently rechecks the timestamp,
journal sequence, generation, and byte offset. A healthy zero-source baseline
is supported because its completed-time anchor still makes a later source
positively correlatable.

### Exact managed process identity

The launch context now preserves the exact process ID and raw creation FILETIME
captured from the suspended child before resume. Lifecycle evidence and scanner
authorization use that immutable launch identity rather than rediscovering it
later through a separate process abstraction. First evidence must match that
exact suspended PID/start pair even when its process and lifecycle halves agree
with each other.

### WOW64 x86 scanner support

The installed client was measured as a WOW64 x86 process on AMD64. The guarded
lease previously accepted native x64 only and surfaced the architecture denial
as `discover.identity_mismatch`.

The scanner now supports native x64 and WOW64 x86 targets from its 64-bit host.
The guarded lease records target architecture, pointer width, and maximum user
address; region enumeration is target-bounded; and pointer-chain reads decode
four- or eight-byte pointers according to the target rather than the host.
Snapshot base/minimum/maximum filters are revalidated against the measured
target bound after opening the identity lease, including one complete value.

### Diagnostic privacy

Canonical executable paths, caller-controlled field labels, expected/mask
bytes, query values, memory addresses, decoded values, and observed candidate
bytes were removed from persistent scanner logs. Aggregate counts,
truncation/read-failure status, elapsed time, process ID, version, hash,
architecture, and stable reason context remain available. Logger-capture tests
and architecture source guards prevent those sensitive templates from
returning.

## Real offline smoke evidence

The final smoke used only the managed replay API and its short-lived offline
authorization:

- gate: `OfflineReplayVerified` / `session.offline_replay_verified`;
- measured target architecture: `x86`;
- neighborhood: one region, 128 bytes;
- snapshot: bounded to 1 MiB of the main image with four-byte aligned floats;
- comparison: 147,365 previous and current candidates, all unchanged during
  the immediate comparison; 100 candidates returned with truncation reported;
- retained snapshot session: discarded successfully;
- cleanup: authorization expired to `EvidenceStale`; managed game and web host
  process counts both returned to zero.

The candidate count is smoke evidence only. No address, observed value, memory
dump, pointer map, scan file, replay data, or offset was committed or promoted.

After the specialist-audit fixes, the managed smoke was repeated against the
final time-anchored lifecycle contract. The exact launched `wotblitz.exe` PID
was observed as the sole game process, the session again reached
`OfflineReplayVerified`, and one gated x86 neighborhood read covered one region
and 128 bytes. It returned zero candidates because all value decoders were
disabled for this read-only authorization proof. Expiry moved the session to
`EvidenceStale`, terminated that exact game PID, and cleanup left zero game and
web-host processes.

## Validation

- `dotnet format WotBTreader.sln --verify-no-changes --no-restore` — passed.
- `dotnet build WotBTreader.sln -c Release --no-restore` — 0 warnings, 0 errors.
- `dotnet test WotBTreader.sln -c Release --no-build` — 489 passed, 0 failed,
  2 expected local opt-in skips.
- Focused GameIntegration suite — 194 passed, 2 expected local opt-in skips.
- Architecture suite — 19 passed.
- Real managed offline smoke and post-audit repeat — passed as recorded above.
- `scripts/validate.ps1` — passed after the handoff and offline file-tree
  refresh, including locked restore, format, Release build, all tests,
  repository scan, and offline freshness/link checks.

## Files in this unit

- `src/WotBTreader.GameIntegration/Logs/BlitzReplayLifecycleFeed.cs`
- `src/WotBTreader.GameIntegration/Logs/LifecycleEventJournal.cs`
- `src/WotBTreader.GameIntegration/Logs/LifecycleFeedContracts.cs`
- `src/WotBTreader.GameIntegration/Session/GameSessionCoordinator.cs`
- `src/WotBTreader.GameIntegration/Session/ManagedLaunchCorrelationRegistrar.cs`
- `ultimate-scanner/GuardedMemoryReader.cs`
- `ultimate-scanner/MemoryScanDiscoverer.cs`
- `ultimate-scanner/MemoryScanEngine.cs`
- lifecycle, coordinator, scanner, and architecture regression tests
- `docs/operations/offset-discovery-guide.md`
- blocker log and this handoff

## Next move

Begin a separate offset-evidence campaign using controlled value transitions
and at least two launches and two replays before promoting any candidate. The
stalled SignalR transport seam and synthetic native pointer-chain traversal
remain useful independent follow-ups; neither blocks the now-proven offline
launch-to-scan path.
