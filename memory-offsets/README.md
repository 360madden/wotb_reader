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
| `none`   | No offset evidence; placeholder only |
| `low`    | Preliminary confidence summary; not a promotion decision |
| `medium` | Multiple observations may support investigation; still not a promotion decision |
| `high`   | High-level summary only; `fieldValidation.status: "Verified"` and all required evidence still control runtime promotion |

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

Offset discovery follows a **four-phase pipeline**: static analysis → dynamic analysis →
struct/layout analysis → automated verification. The current committed 11.19.0.10
table contains one hash-bound static-analysis candidate (`playerYaw`); candidate
fields remain discovery-only until promotion evidence is complete.

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
│  3. STRUCT/LAYOUT ANALYSIS (ILSpy + x64dbg) → Field mapping      │
│     • Trace access instructions and register-held struct bases   │
│     • Map neighboring HP/position/yaw/pitch fields               │
│     • Cross-check layout against static candidates               │
│                                                                  │
│  4. AUTOMATED VERIFICATION (GameHarness + Treader) → Candidate   │
│     • Run the built-in scanner to verify candidates              │
│     • Validate across multiple battles and restarts              │
│     • Promote only after complete evidence requirements          │
│     • Commit redacted evidence summaries to memory-offsets/      │
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

### Phase 4 — Automated Verification with GameHarness

GameHarness exposes the guarded `discover*` commands through the web host. The
commands first require the `OfflineReplayVerified` session gate; native scanning
and snapshot comparison are implemented by `GameIntegration`, not by the harness
itself. These commands produce discovery evidence, not runtime-supported offsets.

#### Quick workflow

```powershell
# 1. Import and launch a known pre-recorded replay through the managed host path.
#    Continue only after the host reports OfflineReplayVerified.

# 2. Scan for a known value (field type is Float, Int32, or Double)
dotnet run --project tools/src/WotBTreader.GameHarness -- discover playerHP Int32 1500

# 3. Create a filtered snapshot for changed/unchanged comparison
dotnet run --project tools/src/WotBTreader.GameHarness -- discover-snapshot 4 --int-min 0 --int-max 3000

# 4. Advance the replay, then compare the snapshot
dotnet run --project tools/src/WotBTreader.GameHarness -- discover-compare 000001 changed

# 5. Inspect fields adjacent to a known candidate
dotnet run --project tools/src/WotBTreader.GameHarness -- discover-nearby 0x0317A810 --window 256

# 6. Discard temporary snapshot state when finished
dotnet run --project tools/src/WotBTreader.GameHarness -- discover-discard 000001
```

Use `probe` or `scan` only as read-only gate/status reports. They do not accept
field values or narrow a scan. Candidate output must be normalized through
`tools/discover-offsets.ps1`; ambiguous results remain report-only.

#### Cross-phase validation

| Discovery Phase | Tool | Output | Validated By |
|----------------|------|--------|-------------|
| Static | Ghidra | Candidate addresses from binary analysis | Cheat Engine dynamic verification |
| Dynamic | Cheat Engine | Candidate addresses, pointer chains, and write traces | GameHarness discovery commands |
| Struct/layout | ILSpy + x64dbg | Field mapping and access instructions | GameHarness and CE |
| Automated | GameHarness | Gate-checked scan/snapshot/compare candidates | Independent launches, replays, and invariants |

#### Converting to offset JSON

When you have one or more independently corroborated candidate offsets:

1. Open `memory-offsets/<version>.json` (or create a new one for a new game version)
2. Fill in the discovered offsets
3. Set appropriate confidence level
4. Add notes about how the offset was discovered
5. Record provenance and per-field validation in `fieldValidation`.
6. Promote a field to `Verified` only after the schema's independent-launch,
   independent-replay, harness, static-analysis, and approval requirements pass.
   A global `confidence` value does not override a field's status.

```json
{
  "schemaVersion": 1,
  "gameVersion": "11.19.0.10",
  "executableSha256": "<64-hex SHA-256 of the exact executable>",
  "discoveredAtUtc": "2026-07-31T14:30:00Z",
  "offsets": {
    "replayTime": 0,
    "playerHP": 0,
    "playerPositionX": 0,
    "playerPositionY": 0,
    "playerPositionZ": 0,
    "playerYaw": 51808784,
    "cameraPitch": 0,
    "aliveTankCount": 0
  },
  "fieldValidation": {
    "playerYaw": {
      "status": "Candidate",
      "evidence": [
        {
          "provenanceKind": "StaticAnalysis",
          "sourceTool": "Ghidra",
          "notes": "Candidate only; dynamic verification is still required."
        }
      ],
      "independentProcessLaunches": 0,
      "independentReplays": 0,
      "harnessInvariantsPassed": false,
      "leadApproved": false,
      "decoderAuditorApproved": false
    }
  },
  "confidence": "low",
  "notes": "Candidate evidence is discovery-only. Do not promote from a global confidence value."
}
```

---

## Current evidence status

| Version | Executable hash | Known offsets | Runtime status |
|---|---|---:|---|
| `11.19.0.10` | `1cda5c31919c9784a41bee7f3270ec1b4536b124c51e8b36f2221b381760307d` | 1/8 | `playerYaw` is Candidate; runtime reads remain unsupported |

The hash identifies the installed executable used for this evidence snapshot; it is
not proof that the candidate offset is correct. Dynamic verification must use a
positively verified offline replay and preserve evidence summaries without committing
raw dumps or scan files.

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
- **Use the approved gate** — Cheat Engine and GameHarness scanning are restricted to positively verified offline replay sessions; elevation depends on the local Windows security context

## Never commit

- `scanner-state.json` — runtime state, added to .gitignore
- Offsets with `confidence: "none"` (placeholders only — update when real data exists)
- Absolute file paths or machine-specific paths in notes
