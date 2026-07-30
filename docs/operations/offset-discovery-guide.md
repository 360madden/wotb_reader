# Offset Discovery Guide

Last updated: 2026-07-30

## Current state

| Item | Status |
|------|--------|
| Game version installed | 11.19.0.10 (`C:\Games\World_of_Tanks_Blitz\wotblitz.exe`, ~71MB) |
| Offset file | `memory-offsets/11.19.0.10.json` — placeholder (all zeros, version-matched) |
| Ghidra 12.1.2 | Installed at `C:\work\tools\ghidra_12.1.2_PUBLIC` |
| Cheat Engine 7 | Installed at `C:\Program Files\Cheat Engine\` |
| GameHarness scanner | `scan`/`probe` now check the offline-session gate via HTTP |
| Ghidra headless script | `tools/ghidra-scripts/FindOffsets.py` — ready to run |

## Quickstart — Ghidra headless (Phase 1)

**This must be run on the desktop** — full auto-analysis of a 71MB binary takes
45-90 minutes and exceeds remote terminal timeouts. The pipeline is split into
two steps so you can let the long step finish and run the short step whenever
ready.

### Step 1 — Import + auto-analyze (long: 45-90 min)

```cmd
.build\ghidra-offsets.bat
```

**What it does:** Imports `wotblitz.exe` into a Ghidra project at
`C:\work\tools\ghidra-projects\WotBlitz` and runs full auto-analysis.
Keep the window open until it finishes — it will show "Import + auto-analysis
complete!" when done. The `-overwrite` flag starts fresh each time; remove it
from the `.bat` file to resume a partial analysis after an interruption.

### Step 2 — Run FindOffsets.py (short: 3-5 min)

```cmd
.build\ghidra-scan.bat
```

**What it does:** Runs `FindOffsets.py` on the analyzed project. Searches for
game-state strings (health, position, replayTime, yaw, pitch, alive), traces
cross-references, and outputs candidate offsets to
`tools\ghidra-scripts\ghidra-offset-candidates.json`.

**What you get:** Candidate base-relative offsets for `playerHP`,
`playerPositionX/Y/Z`, `replayTime`, `playerYaw`, `cameraPitch`, and
`aliveTankCount`, ranked by cross-reference count.

### Why not remote?

Full Ghidra auto-analysis of a 71MB binary consumes 2+ GB RAM and takes 45-90
minutes. The remote analysis agent has a 20-minute timeout — when the timer
expires, the parent script is killed but the Java analysis process keeps running
in the background as a zombie, consuming memory without producing output. Run
the `.bat` files directly on the desktop where there's no timeout constraint.

If the analysis is interrupted, remove `-overwrite` from the `.bat` file to
resume from the incomplete project instead of starting over.

## Desktop manual pipeline

### Phase 1 — Ghidra GUI (alternative to headless)

1. Launch Ghidra: set `JAVA_HOME=C:\Program Files\Eclipse Adoptium\jdk-21.0.11.10-hotspot` then run `C:\work\tools\ghidra_12.1.2_PUBLIC\ghidraRun.bat`
2. File → Import File → select `C:\Games\World_of_Tanks_Blitz\wotblitz.exe`
3. Run auto-analysis (default options)
4. Search → Program Text for strings: `health`, `position`, `replayTime`, `yaw`, `pitch`, `alive`
5. Trace cross-references (Ctrl+Shift+F) to find struct layouts
6. Note candidate offsets relative to image base

### Phase 2 — Cheat Engine dynamic scanning

1. Start a WoT Blitz replay in the game client
2. Launch Cheat Engine as Administrator
3. Attach to `wotblitz.exe` process
4. **HP scan:** value type `4 Bytes`, scan exact HP value, take damage, scan new value, repeat until 1-3 candidates remain
5. **Position scan:** value type `Float`, scan X coordinate while moving, narrow to 1-3 candidates (positions are typically contiguous X/Y/Z floats)
6. **Replay time scan:** value type `Double`, scan elapsed time as replay advances
7. **Pointer scan:** for each found address, run pointer scan to find static base offsets
8. **Structure dissection:** right-click address → Dissect data/structures to map surrounding fields

### Phase 3 — Offset validation

Once you have candidate offsets:
1. Open offset GUI plugin to verify values change as expected
2. Cross-reference Cheat Engine findings with Ghidra's disassembly view
3. Test across multiple battles and game restarts
4. Run the Doctor health check to confirm the game version the offset file applies to
5. Update `memory-offsets/<version>.json` with discovered offsets
6. Set `confidence: "high"` only after cross-battle validation

## Offset file format

```json
{
  "schemaVersion": 1,
  "gameVersion": "11.19.0.10",
  "executableSha256": "<sha256 of wotblitz.exe>",
  "discoveredAtUtc": "<ISO 8601 timestamp>",
  "offsets": {
    "replayTime": 0,
    "playerHP": 0,
    "playerPositionX": 0,
    "playerPositionY": 0,
    "playerPositionZ": 0,
    "playerYaw": 0,
    "cameraPitch": 0,
    "aliveTankCount": 0
  },
  "confidence": "none",
  "notes": ""
}
```

## GameHarness M2 gate — ✅ WIRED

The `scan` and `probe` commands in GameHarness now check the offline-session
gate via `GET /api/v1/game/state` (read from the rendezvous file). They are no
longer hard-denied. The full flow is:

1. `POST /api/v1/game/launch` → Coordinator orchestrates the M2 suspended-process
   pipeline (prepare → executable lease → artifact staging → suspended process →
   correlation → resume → record context).
2. Lifecycle evidence arrives via `ApplyEvidence()` → coordinator evaluates →
   `OfflineReplayVerified`.
3. GameHarness `scan`/`probe` reads the rendezvous file, calls
   `GET /api/v1/game/state`, and reports scan availability when the gate is
   satisfied.

The M2 components (`SuspendedGameProcessLaunch`, `WindowsTrustedExecutableLaunchLease`,
`ManagedReplayArtifactStager`, `ManagedLaunchPreparer`, `ManagedLaunchCorrelationRegistrar`,
`ThreadResumePlatform`) are fully wired in `GameSessionCoordinator.LaunchAsync()`
as of commit `c590e61`.

To launch a replay and reach the verified state:
```
1. import a .wotbreplay via CLI
2. serve (start the web host)
3. POST /api/v1/game/launch with the source artifact ID
4. GameHarness scan  (reports "gate satisfied" when OfflineReplayVerified)
```
