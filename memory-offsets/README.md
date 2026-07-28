# Memory Offset Data

Discovered WoT Blitz memory offsets for replay state reading.
Each file maps to one game version.

## Directory structure

```
memory-offsets/
├── README.md           ← this file
├── schema.json         ← JSON schema for validation
├── <version>.json      ← one file per discovered game version (e.g. 11.8.0.7.json)
└── scanner-state.json  ← last scanner state (gitignored, generated at runtime)
```

## Offset file format

```json
{
  "schemaVersion": 1,
  "gameVersion": "11.8.0.7",
  "executableSha256": "abc123...",
  "discoveredAtUtc": "2026-07-28T12:00:00Z",
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

## Confidence levels

| Level    | Meaning |
|----------|---------|
| `none`   | No offsets discovered (placeholder) |
| `low`    | Scanner found candidates, unverified |
| `medium` | Scanner found 1-3 candidates, matches game behavior in one battle |
| `high`   | Verified across multiple battles and game restarts |

## External tools

These tools are registered in `tools/external/tools.lock.json` and available at:

| Tool | Path | Phase |
|------|------|-------|
| **Cheat Engine 7.7** | `C:\Program Files\Cheat Engine\` (prebuilt) | Dynamic analysis |
| **Cheat Engine source 7.5** | `c:\work\tools\cheat-engine-master\Cheat Engine\` | Build from source |
| **Ghidra 12.1.2** | `c:\work\tools\ghidra_12.1.2_PUBLIC\` (prebuilt) | Static analysis |
| **Ghidra source 12.2 DEV** | `c:\work\tools\ghidra-master\` | Build from source |
| **AITools** | `c:\work\tools\AITools-main\tools\aitools.lua` | Cheat Engine plugin |
| **GameHarness Scanner** | `tools/src/WotBTreader.GameHarness/` | Automated scanning |

---

## Offset discovery workflow

Offset discovery follows a **three-phase pipeline**: static analysis → dynamic analysis → automated verification.

```
┌──────────────────────────────────────────────────────────────────┐
│                    OFFSET DISCOVERY PIPELINE                      │
├──────────────────────────────────────────────────────────────────┤
│                                                                  │
│  1. STATIC ANALYSIS (Ghidra)          →  Initial offset list     │
│     • Load wotblitz.exe into Ghidra                              │
│     • Auto-analyze + identify globals                            │
│     • Search for known values / strings                          │
│     • Run Ghidra scripts for pattern matching                    │
│     • Export candidate addresses                                 │
│                                                                  │
│  2. DYNAMIC ANALYSIS (Cheat Engine)  →  Refined pointer offsets  │
│     • Attach CE to running replay                                │
│     • Value scan for HP, positions, time                         │
│     • Pointer scan to find static root refs                      │
│     • Structure dissection to map surrounding fields              │
│     • Cross-reference with Ghidra findings                       │
│     • AITools for AI-assisted pattern matching                   │
│                                                                  │
│  3. AUTOMATED VERIFICATION (GameHarness + Treader) → Confirmed   │
│     • Run the built-in scanner to verify candidates              │
│     • Validate across multiple battles                           │
│     • Update offset JSON with confidence level                   │
│     • Commit to memory-offsets/                                  │
│                                                                  │
└──────────────────────────────────────────────────────────────────┘
```

---

### Phase 1 — Static Analysis with Ghidra

Ghidra reverse-engineers the game binary without running it, identifying global variables, static addresses, and cross-references.

#### Setup
```cmd
REM Ghidra 12.1.2 is installed prebuilt at:
REM   C:\work\tools\ghidra_12.1.2_PUBLIC
REM JDK 21 is installed at:
REM   C:\Program Files\Eclipse Adoptium\jdk-21.0.11.10-hotspot

REM Launch Ghidra:
set JAVA_HOME=C:\Program Files\Eclipse Adoptium\jdk-21.0.11.10-hotspot
C:\work\tools\ghidra_12.1.2_PUBLIC\ghidraRun.bat

REM Source code at c:\work\tools\ghidra-master is version 12.2 DEV.
REM To build from source (requires JDK 25):
cd /d c:\work\tools\ghidra-master
gradlew.bat buildGhidra
```

#### Steps

1. **Load wotblitz.exe** into Ghidra
   - File → Import File → select `wotblitz.exe`
   - Run auto-analysis (default options, wait for completion)

2. **Identify known strings** for anchor points
   - Search → Program Text: search for strings like `"health"`, `"hp"`, `"position"`, `"replayTime"`
   - Look at cross-references (Ctrl+Shift+F) to find where these strings are used
   - Note the addresses and surrounding data structures

3. **Find global data sections**
   - Window → Memory Map: examine `.data` and `.bss` sections
   - Ghidra's Data Type Manager can help reconstruct struct layouts

4. **Run Ghidra scripts for pattern matching**
   - Use Script Manager (Window → Script Manager) with Python/Java
   - Approach: write a script that iterates the `.data` section looking for aligned float triples (X/Y/Z positions) or adjacent int32+float combinations (HP + positions)
   - Key Ghidra API classes for script development:
     - `currentProgram.getListing()` — iterate code/data units
     - `currentProgram.getMemory()` — access raw memory blocks
     - `currentProgram.getAddressFactory()` — construct address ranges
     - `ghidra.app.script.GhidraScript` — base class for all scripts
   - The [GhidraDev plugin](https://github.com/NationalSecurityAgency/ghidra/tree/master/GhidraBuild/EclipsePlugins/GhidraDev) provides Eclipse IDE support for script authoring with code completion

5. **Export candidate addresses**
   - Mark discovered addresses and note their context
   - Export as a Ghidra bookmark set or annotate in the listing
   - Transfer interesting offsets to Cheat Engine for dynamic verification

---

### Phase 2 — Dynamic Analysis with Cheat Engine

Cheat Engine attaches to the running game process and performs value-based scanning to pinpoint exact memory locations.

#### Setup
```powershell
# Cheat Engine 7.5 is at c:\work\tools\cheat-engine-master
# Build with Lazarus 2.2.2 + FPC 3.2.2, or download prebuilt from releases
# Or just use the already-installed Cheat Engine binary if you have one
```

#### Steps

##### 2a — Value scanning (HP, positions, time)

1. **Start a WoT Blitz replay** in the game client
2. **Launch Cheat Engine** (run as Administrator for process access)
3. **Attach to `wotblitz.exe`** process
   - Click the computer icon → select wotblitz.exe → Open
4. **Scan for Player HP** (int32, known value)
   - Value Type: `4 Bytes`
   - Scan Type: `Exact Value`
   - Value: your current HP (e.g. `1500`)
   - Click "First Scan" → typically ~5000+ results
   - Take damage → HP changes → scan with `1200` (new value)
   - Repeat until 1-3 candidates remain
   - Note the offset = `candidate_address - module_base`
5. **Scan for Position X** (float)
   - Value Type: `Float`
   - Scan for position changes (move in game, scan new value)
   - Narrow to 1-3 candidates
6. **Scan for Replay Time** (double)
   - Value Type: `Double`
   - Scan for elapsed seconds → narrow as replay advances

##### 2b — Pointer scanning (static root discovery)

Once you find a dynamic address for HP or position:

1. Right-click the address → `Pointer scan for this address`
2. Set reasonable limits (max offset, max level)
3. Start pointer scan — this finds static chains like:
   ```
   wotblitz.exe + 0x12345678 → +0xA0 → +0x10 → HP value
   ```
4. The base offset (`0x12345678` from `wotblitz.exe`) is the static offset you want
5. Re-run pointer scan after game restart to verify the chain is stable

##### 2c — Structure dissection

1. After finding candidate HP offset, right-click the address → `Dissect data/structures`
2. The structure dissector shows surrounding memory as typed fields
3. Look for:
   - Other int32 values nearby (team ID, tank ID)
   - Float triples nearby (position X/Y/Z)
   - Double values (replay time)
4. This maps the entire game state struct in one session
5. Export the structure definition (right-click → Save structure)

##### 2d — AITools for pattern matching (optional)

1. Copy `c:\work\tools\AITools-main\tools\aitools.lua` to Cheat Engine's `Extensions/` folder
2. Enable the plugin: Extensions → AITools
3. Use AI-assisted pattern scanning when value scans produce too many candidates
4. The plugin learns byte patterns from known/unknown memory regions

##### 2e — Assembly-level verification

1. Right-click an address → `Find out what writes to this address`
2. Execute the action in game (take damage) → Cheat Engine shows the writing instruction
3. Right-click that instruction → `Show in disassembler`
4. The disassembly context reveals the struct field offset being accessed
5. Cross-reference with Ghidra's disassembly view to confirm the struct layout

---

### Phase 3 — Automated Verification with GameHarness

The built-in `MemoryOffsetScanner` and `GameMemoryReader` in `tools/src/WotBTreader.GameHarness/` provide CLI-driven verification.

#### Quick workflow

```powershell
# 1. Start WoT Blitz replay

# 2. Probe to confirm the game is running
dotnet run --project tools/src/WotBTreader.GameHarness -- probe

# 3. Scan for HP (int32) — use value visible on screen
dotnet run --project tools/src/WotBTreader.GameHarness -- scan int32 1500

# 4. After HP changes, narrow the results
dotnet run --project tools/src/WotBTreader.GameHarness -- scan int32 1200 --narrow

# 5. Repeat until 1-3 candidates remain
# (these are your base-relative offsets)

# 6. Same process for position (float) and replay time (double)
dotnet run --project tools/src/WotBTreader.GameHarness -- scan float -45.23
dotnet run --project tools/src/WotBTreader.GameHarness -- scan double 12.5
```

#### Cross-phase validation

| Discovery Phase | Tool | Output | Validated By |
|----------------|------|--------|-------------|
| Static | Ghidra | Candidate addresses from binary analysis | Cheat Engine dynamic verification |
| Dynamic | Cheat Engine | Confirmed offsets + pointer chains | GameHarness scanner re-verification |
| Automated | GameHarness | CLI-scanned candidates | Cross-battle validation with Treader |

#### Converting to offset JSON

When you have 1-3 confirmed offsets:

1. Open `memory-offsets/<version>.json` (or create a new one for a new game version)
2. Fill in the discovered offsets
3. Set appropriate confidence level
4. Add notes about how the offset was discovered
5. Run the Treader HUD to validate the offsets produce sensible readings
6. Set `confidence: "high"` after multiple successful battles

```json
{
  "schemaVersion": 1,
  "gameVersion": "11.8.0.7",
  "executableSha256": "abc123...",
  "discoveredAtUtc": "2026-07-28T14:30:00Z",
  "offsets": {
    "replayTime": 3948572,
    "playerHP": 123456,
    "playerPositionX": 234567,
    "playerPositionY": 234571,
    "playerPositionZ": 234575,
    "playerYaw": 789012,
    "cameraPitch": 789016,
    "aliveTankCount": 345678
  },
  "confidence": "high",
  "notes": "Discovered via Cheat Engine pointer scan, verified with Ghidra static analysis. Cross-validated across 3 battles."
}
```

---

## Quick reference — common field types

| Field | Type | Size | Game context |
|-------|------|------|-------------|
| Player HP | int32 | 4 bytes | Current hit points, changes on damage/heal |
| Position X/Y/Z | float | 4 bytes each | World coordinates, changes when tank moves |
| Player Yaw | float | 4 bytes | Rotation around vertical axis (radians) |
| Camera Pitch | float | 4 bytes | Camera vertical angle (radians) |
| Replay Time | double | 8 bytes | Elapsed replay seconds, monotonically increasing |
| Alive Tank Count | int32 | 4 bytes | Number of tanks still in battle, decrements on kill |

## Tips

- **HP is the easiest starting point** — visible on screen, changes frequently, int32 type narrows quickly
- **Positions often form a contiguous float triple** (X, Y, Z at consecutive offsets) — finding one finds all three
- **Replay time is double precision** — many scanners default to int32/float, ensure you select `Double`
- **Yaw and Camera Pitch** are typically adjacent floats near the position data
- **Pointer scan after game restart** — offset chains that survive restart are robust static offsets
- **Ghidra string references** — strings like `"health"` or `"replayTime"` in the binary often cross-reference to the structs containing those values
- **Admin rights required** — both Cheat Engine and GameHarness scanner need elevation to read wotblitz.exe memory

## Never commit

- `scanner-state.json` — runtime state, added to .gitignore
- Offsets with `confidence: "none"` (placeholders only — update when real data exists)
- Absolute file paths or machine-specific paths in notes
