# Cheat Engine Tools

Scripts for discovering WoT Blitz memory offsets using Cheat Engine.

## Prerequisites

- [Cheat Engine 7.5+](https://www.cheatengine.org/) installed
- WoT Blitz running with a replay actively playing
- Use Cheat Engine only during a positively verified offline replay session; elevation may be required by the local Windows security context

## Scripts

### `discover-offsets.lua` — Neighborhood Scanner

Auto-attaches to `wotblitz.exe`, reads memory around the known `playerYaw` offset
(`0x0317A810` — Ghidra candidate), and reports all neighboring float/int32/double
values. Saves results to `discovered-offsets.json`.

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
1. Start WoT Blitz with a replay actively playing
2. Load this script in CE (Ctrl+Alt+L → Execute)
3. Type: `autoDiscover()`
4. Wait ~30-60 seconds per field — the script scans with timer-based
   refinement, using the replay's natural progression (tank movement,
   time advancement) to narrow candidates without user interaction.
5. Results auto-saved to `discovered-offsets-multiscan.json`

**Currently active fields in autoDiscover():**
- `playerYaw` — enabled (float, changed filter)
- All other fields — deferred (use `scanInteractive()` manually)
- Enable more fields by editing the `AUTO_FIELDS` table in the script
  (remove the `if field.fieldName ~= "playerYaw" then goto continue` guard)

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

Then validate the candidate through the complete offline evidence workflow. A
candidate-only table is not runtime-supported. Use
`tools/report-offset-evidence.ps1` and `python scripts/python/offset_check.py
--check-schema`; the memory API remains unknown/unavailable until exact executable
identity and per-field promotion evidence are complete.
