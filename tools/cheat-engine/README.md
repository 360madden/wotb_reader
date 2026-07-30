# Cheat Engine Tools

Scripts for discovering WoT Blitz memory offsets using Cheat Engine.

## Prerequisites

- [Cheat Engine 7.5+](https://www.cheatengine.org/) installed
- WoT Blitz running with a replay actively playing
- Run Cheat Engine as Administrator

## Scripts

### `discover-offsets.lua` — Neighborhood Scanner

Auto-attaches to `wotblitz.exe`, reads memory around the known `playerYaw` offset
(`0x0317A810`), and reports all neighboring float/int32/double values. Saves results
to `discovered-offsets.json`.

**Usage:**
1. Start WoT Blitz and play a replay
2. Open Cheat Engine, attach to wotblitz.exe
3. Press Ctrl+Alt+L, paste the script, click Execute
4. Check `tools/cheat-engine/discovered-offsets.json` for results

### `multiscan.lua` — Interactive Multi-Scan Engine

The classic Cheat Engine workflow: scan for unknown values → change value in-game →
filter to changed/unchanged values → repeat until candidates are isolated.

**Usage:**
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

**Value types:**
- `vtSingle` — 4-byte float (positions, camera angles)
- `vtDword` — 4-byte integer (HP, tank counts)
- `vtDouble` — 8-byte double (replay time)

## Integrating results

After discovering candidate offsets, update `memory-offsets/<version>.json`:

```json
{
  "offsets": {
    "playerYaw": 51808784,
    "cameraPitch": 51808788,
    "playerPositionX": 51808272,
    ...
  }
}
```

Then re-validate by launching a replay through the web host and checking
`GET /api/v1/game/memory` — telemetry fields should show non-null values.
