# Discovery Pipeline Automation

Last updated: 2026-07-30

## Goal

Automate the manual x64dbg + Cheat Engine offset discovery workflow into a
single scripted pipeline that can discover all 8 game-state offsets without
manual intervention beyond launching the game and replay.

## Current Bottleneck

Right now, finding offsets requires:

1. Launch WoT Blitz with a replay
2. Attach Cheat Engine manually
3. Scan for values manually (position, HP, yaw)
4. Open x64dbg, set breakpoints, trace instructions
5. Map struct offsets by hand
6. Type them into `memory-offsets/11.19.0.10.json`

This takes 30-60 minutes per session and must be repeated when the game updates.

## Target: One-Command Pipeline

```powershell
# With game running a replay:
tools\discover-offsets.ps1 -GameVersion 11.19.0.10
# Output: memory-offsets/11.19.0.10.json fully populated
```

## Architecture

```
┌──────────────────────────────────────────────────────────┐
│                    discover-offsets.ps1                   │
│                                                          │
│  1. Verify prerequisites (game running, tools installed) │
│  2. Launch CE plugin: scan known patterns                │
│  3. CE finds candidate addresses via multiscan           │
│  4. Export candidates to JSON                            │
│  5. Analyze struct layout from CE candidate set          │
│  6. Write memory-offsets/<version>.json                  │
│  7. Verify via GameHarness health check                  │
└──────────────────────────────────────────────────────────┘
```

## Phase 1: CE Lua Automation (Already Possible)

The existing `tools/cheat-engine/multiscan.lua` already supports the core
workflow through its interactive Lua API. To automate it:

### What we'd change in multiscan.lua

Add an `autoDiscover()` function that:

```lua
function autoDiscover()
  attach()
  local results = {}

  -- HP: scan as int32 with known range
  results.playerHP = autoScan(
    "playerHP", vtDword, "exact",
    minHP, maxHP,  -- known from game context
    "unchanged"    -- filter: HP doesn't change unless damaged
  )

  -- Position: 3 consecutive floats, scan as unknown initial
  results.playerPosition = autoScanTriple(
    "playerPosition", vtSingle, { min=-500, max=500 }
  )

  -- Yaw: float, camera motion changes it
  results.playerYaw = autoScan(
    "playerYaw", vtSingle, "unknown",
    -3.15, 3.15,
    "changed"      -- filter: moves when camera turns
  )

  -- Validate struct contiguity: positions should be adjacent in memory
  results = validateStructContiguity(results)

  saveAutoResults(results)
  return results
end
```

The key automation function:

```lua
function autoScan(fieldName, valueType, mode, minVal, maxVal, filterMode)
  local scan = createMemScan()
  -- Initial scan
  if mode == "unknown" then
    scan.firstScan(soUnknownValue, valueType, nil, tostring(minVal), tostring(maxVal),
                   "", 0, 0x7FFFFFFFFFFF, "", fsmNotAligned, "1",
                   false, false, false, false, false, false, "")
  else
    scan.firstScan(soExactValue, valueType, nil, tostring(minVal), tostring(maxVal),
                   "", 0, 0x7FFFFFFFFFFF, "", fsmNotAligned, "1",
                   false, false, false, false, false, false, "")
  end
  scan.waitTillDone()

  -- Iterative refinement: sleep 2s, scan again with filter
  for i = 1, 5 do  -- max 5 iterations
    if (scan.resultCount or 0) <= 5 then break end
    sleep(2000)
    scan.nextScan(filterMode == "changed" and soChangedValue or soUnchangedValue,
                  nil, "", "", false, false, false, "")
    scan.waitTillDone()
  end

  -- Collect top candidates
  local count = math.min(scan.resultCount or 0, 10)
  local candidates = {}
  for i = 1, count do
    candidates[i] = {
      address = scan.getResultAddress(i - 1),
      value = scan.getResultValue(i - 1)
    }
  end
  scan.destroy()
  return candidates
end
```

### Timer-based refinement

The core insight for automation: instead of prompting the user to "change the
value in-game," we can leverage the replay's natural progression:

- **HP**: Trigger damage events in the replay (or wait for the battle to progress)
- **Position**: The tank is always moving in a replay — no interaction needed
- **Yaw**: Camera auto-rotates or can be scripted via game input
- **Replay time**: Always advancing — scan as "increased value" automatically

The script would:

```lua
-- Phase 1: Snapshot current state
firstScan(soUnknownValue, ...)    -- capture all candidates
-- Wait 3 seconds for replay to advance
sleep(3000)
-- Phase 2: Filter to changed values
nextScan(soChangedValue, ...)     -- narrows dramatically
-- Wait 2 more seconds
sleep(2000)
-- Phase 3: Further narrow
nextScan(soChangedValue, ...)     -- typically < 20 candidates
```

## Phase 2: x64dbg Automation (More Complex)

Automating x64dbg is harder because it lacks a native Lua API. Options:

### Option A: x64dbgpy (Python Plugin)

x64dbg ships with an optional Python plugin. Install it, then:

```python
import x64dbgpy
from x64dbgpy import *

# Attach to process
system_attach("wotblitz.exe")

# Set breakpoint on a CE-discovered address
bp_set(0x01234567, BP_TYPE_HARDWARE)

# Wait for breakpoint hit
def on_bp(hit):
    # Read the register holding the struct base
    ecx = reg_get("ecx")
    # Read nearby memory to discover struct layout
    hp = mem_read_int(ecx + 0x10)
    pos_x = mem_read_float(ecx + 0x28)
    print(f"ecx={ecx:x}  hp={hp}  pos_x={pos_x}")
    # Log the offsets
    return True

# Run until breakpoint
run()
```

### Option B: Named Pipe + Scriptable Debugger

A simpler approach: use x64dbg's built-in script via a JSON command pipe.

1. Start x64dbg with: `x64dbg.exe /a wotblitz.exe`
2. Load a script via: `scriptload discover.txt`
3. The script reads CE's candidate list, sets breakpoints, dumps register state

### Option C: Bypass x64dbg Entirely

The **simplest automation path**: skip x64dbg automation and use Cheat Engine's
own "Find out what writes to this address" feature, which already logs
instruction addresses and register values. CE can dump this to a file, and a
script can parse the output:

```
CE → Right-click address → "Find out what writes"
  → [x] Log to file
  → Log: "writes.txt"
  → Move camera
  → Parsed output:
      Instruction: movss [ecx+0x34], xmm0
      Struct base register: ecx
      Max nearby offset: 0x50
```

**Recommended for Phase 1:** Use CE's built-in logging instead of automating
x64dbg. The information you need (instruction address + register + offset) is
already available through CE's UI.

## Phase 3: Struct Builder

Once CE provides candidate addresses and CE's "what writes" log provides
struct base + field offsets, a script can automatically build the offset table:

```powershell
# discover-offsets.ps1 (Phase 1 draft)
param(
  [string]$GameVersion = "11.19.0.10",
  [string]$CeScript = "tools/cheat-engine/auto-discover.lua"
)

Write-Host "=== WoTB Offset Discovery Pipeline ==="
Write-Host "Game version: $GameVersion"
Write-Host ""

# 1. Verify game is running
if (-not (Get-Process wotblitz -ErrorAction SilentlyContinue)) {
  Write-Error "wotblitz.exe is not running. Start a replay first."
  exit 1
}

# 2. Verify CE is available
$cePath = "C:\Program Files\Cheat Engine\cheatengine-x86_64.exe"
if (-not (Test-Path $cePath)) {
  Write-Error "Cheat Engine not found at $cePath"
  exit 1
}

# 3. Launch CE with the auto-discovery script
Write-Host "Launching Cheat Engine auto-discovery..."
Start-Process -FilePath $cePath -ArgumentList "-l $CeScript" -Wait

# 4. Read CE output
$candidatesPath = "tools/cheat-engine/discovered-offsets.json"
if (-not (Test-Path $candidatesPath)) {
  Write-Error "CE discovery produced no output"
  exit 1
}

$candidates = Get-Content $candidatesPath | ConvertFrom-Json
Write-Host "CE found $($candidates.candidates.Count) candidates"

# 5. Read CE "what writes" log
$writesLog = "tools/cheat-engine/writes.txt"
if (Test-Path $writesLog) {
  $instructions = Get-Content $writesLog
  Write-Host "Found $($instructions.Count) write instructions"
  # Parse instruction format: "movss [ecx+0x34], xmm0"
  # Extract struct base register and field offset
  foreach ($line in $instructions) {
    if ($line -match '\[(\w+)\+0x([0-9a-fA-F]+)\]') {
      $register = $matches[1]
      $offset = [Convert]::ToInt32($matches[2], 16)
      Write-Host "  Struct base: $$register, field offset: 0x$($matches[2])"
    }
  }
}

# 6. Build offset table
$offsetFile = "memory-offsets/$GameVersion.json"
$existing = Get-Content $offsetFile | ConvertFrom-Json

# Merge discovered offsets into existing table
$existing.offsets.playerYaw = $candidates.playerYaw
$existing.offsets.playerHP = $candidates.playerHP
$existing.offsets.playerPositionX = $candidates.playerPositionX
# ... etc

$existing.confidence = "low"
$existing.notes = "Discovered via automated CE pipeline on $(Get-Date -Format 'yyyy-MM-dd')"

$existing | ConvertTo-Json -Depth 5 | Set-Content $offsetFile
Write-Host "Wrote $offsetFile with $(($existing.offsets.PSObject.Properties | Where-Object { $_.Value -ne 0 }).Count)/8 offsets"
```

## Recommended Implementation Order

### Step 1: Auto-scan Lua (30 min)
Add `autoDiscover()` function to `tools/cheat-engine/multiscan.lua`
- Timer-based refinement (no user interaction needed)
- Saves candidates to JSON automatically
- **Deliverable:** CE finds candidate addresses while replay plays

### Step 2: CE Write Logger (15 min)
Use CE's built-in "Find out what writes" with file logging
- Configure CE to log writes to a file
- Auto-parse the file to extract register + offset info
- **Deliverable:** Script extracts struct base and field offsets

### Step 3: Offset Builder (15 min)
Create `tools/discover-offsets.ps1` PowerShell wrapper
- Orchestrates CE launch → wait → parse → write offset file
- Merges discovered offsets into versioned JSON
- **Deliverable:** Single-command pipeline

### Step 4: Validation Hook (15 min)
After writing offset file, trigger `GameHarness discover` to verify:
```powershell
treader discover --game-version 11.19.0.10
treader discover-snapshot
treader discover-compare --offset playerYaw
```

## Edge Cases

| Issue | Mitigation |
|-------|-----------|
| Game not running | Pre-checks in PowerShell; clear error message |
| Anti-cheat blocking CE | Recommend running as Administrator; detect by checking ReadProcessMemory access |
| Replay not loaded | Check if game window title contains "Replay" |
| Zero candidates after 5 rounds | Relax filter to wider range; fall back to manual mode |
| Struct offsets change between versions | Store per-version; pipeline detects version mismatch |
| CE 7.5 vs 7.7 API differences | Detect CE version at runtime; use compatible API calls |
