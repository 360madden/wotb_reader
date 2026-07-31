# Cheat Engine Tools

Scripts for discovering WoT Blitz memory offsets using Cheat Engine.

## Prerequisites

- Confirm the host reports `OfflineReplayVerified` before attaching. These Lua
  scripts cannot enforce the host gate themselves.
- [Cheat Engine 7.5+](https://www.cheatengine.org/) installed
- WoT Blitz running with a replay actively playing
- Use Cheat Engine only during a positively verified offline replay session; elevation may be required by the local Windows security context

## Scripts

### `discover-offsets.lua` — Neighborhood Scanner

This script is currently **quarantined** because the recorded `playerYaw`
representations disagree. Do not run it with `0x0317A810`. It may be re-enabled
only after the ledger reconciles the raw Ghidra candidate, decimal/hex conversion,
and address kind. Until then, use the controlled interactive scans described
below with a fresh position, replay-time, or HP hypothesis.

**Usage:**
1. Start WoT Blitz and play a replay
2. Open Cheat Engine, attach to wotblitz.exe
3. Press Ctrl+Alt+L, paste the script, click Execute
4. Check `tools/cheat-engine/discovered-offsets.json` for results

### `multiscan.lua` — Interactive + Auto-Discover Multi-Scan Engine

The classic Cheat Engine workflow, plus an unattended auto-discover mode.

**Interactive mode:**
1. Load this script in CE (Ctrl+Alt+L → Execute)
2. In the Lua Engine window, type:
   ```
   scanInteractive("playerPositionX", vtSingle, -500, 500)
   ```
3. In the WoT Blitz replay, move the camera or let the tank move
4. Type: `nextScan("changed")`
5. Repeat steps 3-4 until < 10 candidates remain
6. Type: `showCandidates()` to inspect
7. Type: `saveDiscovered()` to save to JSON

**Auto-discover mode (unattended):**
1. Start WoT Blitz with a replay actively playing and verify the offline gate.
2. Load this script in CE (Ctrl+Alt+L → Execute).
3. Type `autoDiscover("playerPositionX")` (or explicitly choose another configured field).
4. Wait ~30-60 seconds for that one field's timer-based refinement.
5. Results are saved to `discovered-offsets-multiscan.json`.

The default is deliberately `playerPositionX`, not the quarantined `playerYaw`
hypothesis. Auto-discovery is a candidate generator only: use a controlled
operator transition and record the session ledger before publication. For a field
that is stationary or ambiguous, stop and use `scanInteractive()` with a known
transition rather than repeatedly running unattended scans.

The script records `moduleName`, `moduleBase`, and `moduleSize` in auto-discovery output so the publication tool can reject candidates that are not inside the named image module. Being inside that range is only a publication prerequisite, not proof of field identity or correctness.

**Value types:**
- `vtSingle` — 4-byte float (positions, camera angles)
- `vtDword` — 4-byte integer (HP, tank counts)
- `vtDouble` — 8-byte double (replay time)

## Integrating results

After a session produces candidates, do **not** hand-edit the versioned table
from this example. Append the session to
`docs/operations/offset-discovery-ledger.md`, classify each address kind, and
pass CE output through `tools/discover-offsets.ps1`. A decimal/hex mismatch,
ambiguous result, heap-only address, or unresolved field identity remains
report-only.

Then validate the candidate through the complete offline evidence workflow. A
candidate-only table is not runtime-supported. Use
`tools/report-offset-evidence.ps1` and `python scripts/python/offset_check.py
--check-schema`; the memory API remains unknown/unavailable until exact executable
identity and per-field promotion evidence are complete.
