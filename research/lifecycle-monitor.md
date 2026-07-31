# Native Log Lifecycle Monitor System

## Overview

The `BlitzReplayLogMonitor` is the codebase's mechanism for detecting replay
playback state from the running game. It watches the game's native text logs
for lifecycle markers without needing to read game memory.

## Log File Location

The game writes log files to:
```
%LOCALAPPDATA%/wotblitz/DAVAProject/blitz-logs_*.txt
```

Pattern: `blitz-logs_*.txt` — rotates with timestamps/indices.

## Lifecycle Markers

Parsed by `BlitzReplayLifecycleParser`:

| Native Log Marker | Parsed Kind | Meaning |
|-------------------|-------------|---------|
| `START_REPLAY_LOCAL` | `OfflineReplayStarted` | Replay playback began |
| `STOP_REPLAY_LOCAL` | `OfflineReplayStopped` | Replay playback ended |
| `ReplayRecorder::StartRecording` | `ReplayRecordingStarted` | Recording started (online?) |
| `ReplayRecorder::StopRecording` | `ReplayRecordingStopped` | Recording stopped |

## Monitor Architecture

```
BlitzReplayLogMonitor
  ├── FileSystemWatcher (low-latency hints)
  │     Watches: DAVAProject/ for blitz-logs_*.txt
  │     Events: Changed, Created, Deleted, Renamed
  ├── Periodic Reconciliation (source of truth)
  │     Interval: configurable (LogReconciliationInterval)
  │     Enumerates files, sorts by LastWriteTimeUtc
  └── Channel-based event pipeline
        └── IAsyncEnumerable<ReplayLogEvent> → consumers
```

## How It Detects Replay State

1. Monitor starts watching `DAVAProject/` directories
2. When a log file is created/modified, wakes up reconciliation
3. Reconciliation reads new bytes from tracked log files
4. Each line is parsed against the marker allowlist
5. Matching markers are emitted as `ReplayLogEvent` with sequence number

## Key Configuration

- `MaxLogLineCharacters` — max line length before discard (privacy)
- `MaxInitialLogScanBytes` — how far back to scan on first detection
- `MaxLogReadBytesPerPass` — bytes per reconciliation pass
- `LogReconciliationInterval` — periodic check interval
- `MaxTrackedLogFiles` — max concurrent log files tracked

## Privacy Guarantees

- Only exact marker matches are retained
- Unrecognized log content is discarded at the parse boundary
- Raw log bytes are never persisted or logged
- Line length is bounded before parsing

## Integration with Offline Gate

The lifecycle monitor feeds into `GameSessionCoordinator`:
1. Managed launch creates game process with replay
2. `BlitzReplayLifecycleFeed` watches for `START_REPLAY_LOCAL`
3. When detected: `GameSessionCoordinator` transitions to `OfflineReplayVerified`
4. Memory scanning is now permitted (for 15 seconds per evidence refresh)
5. When `STOP_REPLAY_LOCAL` detected: gate closes, authorization revoked

## For Live Replay Switching

If we can make the running game play a new replay, the lifecycle monitor will
automatically detect:
- `STOP_REPLAY_LOCAL` when the old replay ends
- `START_REPLAY_LOCAL` when the new replay begins

This means the gate system ALREADY supports sequential replays — it just needs
the game to actually start playing them.
