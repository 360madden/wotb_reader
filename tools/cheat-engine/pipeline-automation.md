# Discovery Pipeline Automation

Last updated: 2026-07-31

## Goal

Automate the manual x64dbg + Cheat Engine offset discovery workflow into a
single evidence pipeline that normalizes candidates conservatively after the
operator launches an approved offline replay. It does not promise to discover all
8 fields automatically or promote candidates into runtime support.

## Current Bottleneck

Right now, finding offsets requires:

1. Launch WoT Blitz with a replay
2. Attach Cheat Engine manually
3. Scan for values manually (position, HP, yaw)
4. Open x64dbg, set breakpoints, trace instructions
5. Map struct offsets by hand
6. Type them into `memory-offsets/11.19.0.10.json`

This takes 30-60 minutes per session and must be repeated when the game updates.

## Current operator-assisted pipeline

```powershell
# With an approved offline replay running and CE output ready:
tools\discover-offsets.ps1 -GameVersion 11.19.0.10
# Output: uniquely publishable Candidate evidence; ambiguity remains report-only
```

## Architecture

```
┌──────────────────────────────────────────────────────────┐
│                    discover-offsets.ps1                   │
│                                                          │
│  1. Operator verifies the approved offline replay gate   │
│  2. Operator runs CE multiscan and exports JSON          │
│  3. Normalize and validate candidate shape/ranges        │
│  4. Publish only one unique candidate per field          │
│  5. Preserve conflicts and ambiguous results as reports  │
│  6. Record DynamicScan evidence as Candidate             │
│  7. Validate the table; never mark fields Verified       │
└──────────────────────────────────────────────────────────┘
```

## Phase 1: CE Lua Automation

The current `tools/cheat-engine/multiscan.lua` already provides interactive scans
and the unattended `autoDiscover()` entry point. The operator must establish the
approved `OfflineReplayVerified` session through the host before attaching CE.
The script writes the `fieldResults` shape consumed by
`tools/discover-offsets.ps1`; `saveDiscovered()` remains available for one-field
interactive scans.

The following snippets are retained as **historical design notes**, not as
instructions to add another implementation. The operator must establish the
approved offline-replay session first, keep raw CE output untracked, and pass
results through the conservative normalizer.

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

### Future/experimental timer-based refinement

The following is exploratory and is not part of the current publication path.
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

## Phase 2: Future/experimental x64dbg automation

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

## Phase 3: Struct and evidence review

CE candidate addresses and write traces are inputs to a human review, not an
automatic table builder. Use x64dbg or Ghidra to identify the relevant module or
struct context, then record only bounded, redacted evidence summaries. The current
publication path is:

1. `tools/discover-offsets.ps1` validates the installed version, executable hash,
   candidate shape, and existing table.
2. It publishes only one unique module-relative candidate per field and retains
   conflicts or ambiguity as report-only results.
3. It adds `DynamicScan` provenance while leaving the field at `Candidate`.
4. `tools/report-offset-evidence.ps1` and `scripts/python/offset_check.py` validate
   the resulting table.

Do not launch Cheat Engine from the PowerShell script, overwrite all fields, or
set global `confidence` to `high` as a substitute for per-field promotion
requirements.

## Recommended Implementation Order

### Step 1: Run the CE scan (operator-led)
Use `multiscan.lua` during a positively verified offline replay and save its JSON
output. Keep raw CE output local and untracked.

### Step 2: Normalize and publish conservatively
Run `tools/discover-offsets.ps1` after the operator has established the approved
replay gate. The script validates local process/tool prerequisites, the requested
and executable versions, the executable hash, candidate ranges, and existing
evidence before writing. It publishes only unique candidates and records them as
`Candidate`; it does not independently establish lifecycle evidence.

### Step 3: Report and verify
Use `tools/report-offset-evidence.ps1` for a read-only summary, then validate with
`python scripts/python/offset_check.py --check-schema`. Dynamic verification across
independent launches/replays and promotion approval are separate work; do not use
`GET /api/v1/game/memory` as proof while the field remains a Candidate.

## Edge Cases

| Issue | Mitigation |
|-------|-----------|
| Game not running | Pre-checks in PowerShell; clear error message |
| CE cannot read the process | Stop and re-check the approved offline-replay gate, executable identity, and local Windows permissions; do not bypass the gate |
| Replay not loaded | Check if game window title contains "Replay" |
| Zero candidates after 5 rounds | Relax filter to wider range; fall back to manual mode |
| Struct offsets change between versions | Store per-version; pipeline detects version mismatch |
| CE 7.5 vs 7.7 API differences | Detect CE version at runtime; use compatible API calls |
