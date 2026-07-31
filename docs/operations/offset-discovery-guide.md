# Offset Discovery Guide

Last updated: 2026-07-31

## Current state

| Item | Status |
|------|--------|
| Game version installed | 11.19.0.10 (`C:\Games\World_of_Tanks_Blitz\wotblitz.exe`, ~71MB) |
| Offset file | `memory-offsets/11.19.0.10.json` — only `playerYaw` found; 7 fields unknown |
| Ghidra 12.1.2 | Installed at `C:\work\tools\ghidra_12.1.2_PUBLIC` |
| Cheat Engine 7.7 | Installed at `C:\Program Files\Cheat Engine\` |
| x64dbg | Installed at `C:\work\tools\x64dbg` — snapshot 2026.05.27 (see Phase 2 below) |
| ILSpy | **Not yet installed** — see Phase 3 below |
| GameHarness scanner | `scan`/`probe` check the offline-session gate via HTTP |
| Ghidra headless script | `tools/ghidra-scripts/FindOffsets.java` — ready to run |
| Cheat Engine Lua scripts | `tools/cheat-engine/discover-offsets.lua`, `multiscan.lua` |
| Discovery orchestrator | `tools/discover-offsets.ps1` — normalizes CE outputs and publishes unique candidates only |
| Evidence report | `tools/report-offset-evidence.ps1` — read-only Candidate/Verified/Unknown summary |
| System Informer | **Not yet installed** — see installation below |

## Tool Installation Guide

### 1. Install x64dbg

x64dbg is the primary dynamic debugger for tracing memory accesses at the
assembly level. It is essential for finding the *instructions* that read/write
game values.

**Download:** https://github.com/x64dbg/x64dbg/releases

**Installation (done 2026-07-31):**
1. Download the latest release zip (e.g., `snapshot_2026-05-27_12-11.zip`)
2. Extract to `C:\work\tools\x64dbg\` (or any path without spaces)
3. Run `release\x64\x64dbg.exe` (for 64-bit)
4. No installer needed — portable executable

**Verification:**
1. Launch `x64dbg.exe`
2. File → Open → select any `.exe` to confirm the UI loads
3. Confirm the **Dump**, **Registers**, and **Disassembler** panes are visible

### 2. Install System Informer (Process Hacker successor)

System Informer is a free, open-source Task Manager replacement with powerful
features for game memory reverse engineering. It replaces the original
Process Hacker project.

**Download:** https://github.com/winsiderss/systeminformer/releases

**Installation options:**
- **WinGet (recommended):** `winget install --id=WinsiderSS.SystemInformer -e`
- **Portable:** Download `SystemInformer-<version>-x64.zip`, extract anywhere
- **Microsoft Store:** Search "System Informer" in the Store

**Why it helps with offset discovery:**

| Feature | How it helps |
|---------|-------------|
| **Process memory view** | See the full memory map of `wotblitz.exe` — which regions are readable/writable, addresses, and sizes without writing any code |
| **Suspend/Resume** | Freeze the game process mid-replay so memory values don't change while you inspect them in CE or x64dbg |
| **Module list** | View all loaded DLLs, their base addresses, and sizes — confirms ASLR-adjusted module bases for offset calculation |
| **Handle explorer** | See what files wotblitz.exe has open (helps verify replay file is loaded) |
| **GPU/CPU monitoring** | Confirm the replay is actively rendering (GPU usage spike) vs. idling on the menu |

**Verification:**
1. Launch `SystemInformer.exe`
2. Find `wotblitz.exe` in the process list
3. Right-click → Properties → Memory tab — you should see memory regions
4. Right-click → Suspend (then Resume) to test process freezing

### 3. Install ILSpy

ILSpy decompiles .NET assemblies into readable C#. WoT Blitz (Unity) ships
`Assembly-CSharp.dll` and other managed DLLs that contain the game's struct
definitions. This is the fastest way to find field offsets without scanning.

**Download:** https://github.com/icsharpcode/ILSpy/releases

**Installation:**
1. Download the latest release zip (e.g., `ILSpy_binaries_9.0.x.zip`)
2. Extract to `C:\tools\ILSpy\`
3. Run `ILSpy.exe`

**Verification:**
1. Launch ILSpy
2. File → Open → select `C:\Games\World_of_Tanks_Blitz\wotblitz_Data\Managed\Assembly-CSharp.dll`
3. Browse the tree — you should see namespaces and classes
4. Search (Ctrl+F) for "Player" or "Tank" to find relevant classes

**Note:** Unity game assemblies are often **obfuscated** (Beebyte, Obfuscator, etc.).
If class names are gibberish (e.g., `a`, `b`, `aa.bc`), ILSpy is still useful for
struct layout — the field order and types remain intact even if names are mangled.

## Full Discovery Pipeline (4 Phases)

```
Phase 1: Ghidra (static) → Phase 2: x64dbg (dynamic)
                              → Phase 3: ILSpy (struct layout)
                              → Phase 4: Validation
```

### Phase 1 — Ghidra static analysis (string → cross-reference → candidate offset)

**Purpose:** Find candidate offsets by searching for known strings and tracing
their cross-references in the compiled binary.

#### Headless (preferred, but must run on desktop — 45-90 min):

**Step 1 — Import + auto-analyze:**
```cmd
.build\ghidra-offsets.bat
```
**Step 2 — Run FindOffsets.java:**
```cmd
.build\ghidra-scan.bat
```

Output goes to `tools\ghidra-scripts\ghidra-offset-candidates.json`.

#### GUI (alternative):
1. Launch Ghidra via `ghidraRun.bat` (set `JAVA_HOME` to JDK 21 first)
2. File → Import File → `wotblitz.exe`
3. Run auto-analysis (default options)
4. Search → Program Text for strings: `health`, `position`, `replayTime`, `yaw`, `pitch`, `alive`
5. Trace cross-references (instruction `Ctrl+Shift+F` on string data refs)
6. Note candidate offsets relative to image base

**Current results (from 2026-07-30 Ghidra run):**
| Field | Strings | Xrefs | Top Offset | Status |
|-------|---------|-------|------------|--------|
| playerYaw | 67 matches | 5 unique | `0x0317A810` (51,808,784) | ✅ Found |
| playerHP | 5,000 matches | 56 xrefs | Noisy — needs dynamic filter | ❌ Noise |
| playerPositionX | 3 matches | 0 xrefs | No candidates | ❌ Obfuscated |
| playerPositionY | 2 matches | 0 xrefs | No candidates | ❌ Obfuscated |
| playerPositionZ | 1 match | 0 xrefs | No candidates | ❌ Obfuscated |
| replayTime | 0 matches | 0 | No candidates | ❌ Not found |
| cameraPitch | 1 match | 0 xrefs | No candidates | ❌ Not found |
| aliveTankCount | 0 matches | 0 | No candidates | ❌ Not found |

### Phase 2a — System Informer quick checks

Before diving into the debugger, use System Informer for rapid sanity checks:

**1. Confirm the replay is running**
```
System Informer → Find wotblitz.exe → CPU column
  → If CPU > 0% and GPU > 0%, the replay is actively playing
  → If CPU near 0%, the game may be on the menu (no replay loaded)
```

**2. Suspend the game for stable scanning**
```
Right-click wotblitz.exe → Suspend
  → Game freezes → CE values stop changing → easier to narrow candidates
  → Right-click → Resume when ready to advance the replay
```

**3. Verify module base addresses**
```
Right-click wotblitz.exe → Properties → Modules tab
  → Note the base address: e.g., wotblitz.exe = 0x00400000
  → If your CE offset is absolute 0x0317A844:
     Relative offset = 0x0317A844 - 0x00400000 = 0x02D7A844
  → This confirms the module-relative offset for the offset file
```

### Phase 2b — x64dbg dynamic tracing (value → instruction → struct base)

**Purpose:** After finding a value in Cheat Engine, x64dbg tells you *which
instruction* writes to it and *which register* holds the player struct base.
This is the most reliable way to discover all nearby offsets (HP, X/Y/Z, yaw,
pitch) since they're all fields of the same struct.

#### Step-by-step workflow:

**1. Find the yaw value in Cheat Engine**
```
CE: Scan for float yaw (unknown initial value → changed on camera move)
    → narrow to 1-3 candidates
    → select one, add to address list
```

**2. Find what writes to that address**
```
CE: Right-click the address → "Find out what writes to this address"
    → Attach dialog → OK
    → Move the camera in-game
    → An instruction appears: movss [ecx+0x34], xmm0
    → Note the address of this instruction
```

**3. Open x64dbg and set a breakpoint**
```
x64dbg: File → Attach → select wotblitz.exe
    → Ctrl+G → paste the instruction address
    → F2 (toggle breakpoint)
    → Resume the game (F9)
    → When it hits: look at the ecx register value
    → ecx = player struct base address
```

**4. Map the player struct**
```
The instruction was: movss [ecx+0x34], xmm0  (yaw = ecx + 0x34)
Nearby offsets to check (from Ghidra hints):
  HP:        Check ecx+0x10, ecx+0x14 (int32, ~500-2000 range)
  PositionX: Check ecx+0x28 (float, world units)
  PositionY: Check ecx+0x2C (float, height)
  PositionZ: Check ecx+0x30 (float, world units)
  Yaw:       ecx+0x34 (confirmed)
  Pitch:     Check ecx+0x38 (float, -1.5 to 1.5)

In x64dbg Dump pane: right-click → "Follow in dump" → enter ecx value
    → Scroll through memory to visually inspect nearby values
    → Look for patterns: HP (int32 ~500-2000), position (3 consecutive floats)
```

**5. Record the offsets**
```
Relative offset = absolute_struct_address - module_base_address
Example:
  GameAssembly.dll base = 0x10000000  (from x64dbg Modules pane)
  Struct address = 0x10317A44
  playerYaw offset = 0x10317A44 + 0x34 - 0x10000000 = 0x0317A878
  → Compare with Ghidra candidate: 0x0317A810
  → If close, it's confirmed. The 0x68 difference may be struct versioning.
```

### Phase 3 — ILSpy struct decompilation (fastest for obfuscated-lite binaries)

**Purpose:** If the Unity DLLs are not heavily obfuscated, ILSpy will show you
the exact struct layout with field names and offsets.

#### Step-by-step:

**1. Open the game's managed assemblies**
```
ILSpy: File → Open → C:\Games\World_of_Tanks_Blitz\wotblitz_Data\Managed\
    → Select ALL .dll files (or at least Assembly-CSharp.dll)
```

**2. Search for player-related types**
```
Ctrl+F → "Player" → look for classes containing:
  - HP / health / hitPoints
  - position / Position / m_position (Unity Vector3)
  - yaw / Yaw / m_yaw
  - pitch / Pitch / m_pitch
  - isAlive / AliveCount / tankCount

If class names are obfuscated (single letters), look for:
  - Classes with multiple float fields (likely position = 3 consecutive floats)
  - Classes with int + float mix (likely player state)
  - Base classes with names like "MonoBehaviour"
```

**3. Read the struct layout**
```csharp
// Example (conceptual — actual names vary by obfuscation):
public class PlayerStats : MonoBehaviour
{
    public int    maxHp;          // offset +0x10
    public int    currentHp;      // offset +0x14
    public Vector3 position;      // offset +0x28 (X: +0x28, Y: +0x2C, Z: +0x30)
    public float  yaw;            // offset +0x34
    public float  pitch;          // offset +0x38
    public float  cameraPitch;    // offset +0x3C
    public int    aliveTankCount; // offset +0x40 (or in a separate BattleState class)
    public float  replayTime;     // offset +0x44 (or in a separate Timeline class)
}
```

**4. Cross-reference with x64dbg**
```
Check that [ecx+0x28] reads as a float matching the player's in-game X position.
Check that [ecx+0x10] reads as an int32 matching the player's HP.
If they match, the struct offsets are confirmed.
```

### Phase 4 — Offset validation

Once you have candidate offsets from any combination of tools:

1. **CE test:** Add all offsets as manual addresses in Cheat Engine. Verify each
   value changes plausibly during gameplay (HP decreases on damage, yaw changes
   on camera turn, etc.)

2. **Session test:** Close and restart the game. Re-attach CE. Re-verify the
   offsets still point to valid values (handles ASLR).

3. **Cross-battle test:** Load a different replay. Verify offsets still work.

4. **Update the offset file:**
   ```
   memory-offsets/11.19.0.10.json
   ```
   Set all 8 fields to their discovered offsets. Set `confidence` to `"medium"`
   (or `"high"` after 3+ battle tests).

5. **Run the application test:**
   ```
   Serve → overlay → check that GET /api/v1/game/memory returns non-null values
   ```

### Discovery output and publication rules

`multiscan.lua` has two output shapes:

- `autoDiscover()` writes `fieldResults`, containing one result object per
  scanned field.
- `saveDiscovered()` writes the older single-field `fieldName` + `candidates`
  shape for interactive scans.

`tools/discover-offsets.ps1` accepts both shapes. It rejects invalid or unknown
fields and writes a field into the versioned offset table only when exactly one
valid module-relative candidate remains. Multiple candidates are report-only;
they never overwrite existing evidence. Published values remain `Candidate`,
never `Verified`, and receive `DynamicScan` provenance. The executable hash is
updated only from the local binary and is still required before runtime reads.

Use the read-only status report at any time:

```powershell
.\tools\report-offset-evidence.ps1
.\tools\report-offset-evidence.ps1 -GameVersion 11.19.0.10
```

The report does not modify offset tables, scanner state, or CE output. A field
is runtime-promotable only after the reader's complete evidence requirements are
met: exact executable hash, two independent process launches, two independent
replays, passing harness invariants, lead approval, decoder-auditor approval,
and both static-analysis and GameHarness provenance.

## Preferred Approach for Maximum Efficiency

Since Ghidra found only `playerYaw` (the other fields are obfuscated in native
code), the fastest path to all 8 offsets is:

```
1. ILSpy → Open Assembly-CSharp.dll → Find the PlayerStats struct
   ↓ If successful, you get all offsets in 5 minutes
2. x64dbg → Attach to wotblitz.exe while replay is playing
   ↓ Confirm the offsets by setting breakpoints on suspected fields
3. CE → Verify by watching the values change in real-time
4. Update memory-offsets/11.19.0.10.json
```

If ILSpy shows obfuscated names, fall back to:
```
1. CE → Find any one value (yaw or HP using known value scan)
2. x64dbg → "Find out what writes to this address" → get struct base
3. x64dbg Dump → Scroll through struct to discover neighboring fields
4. Map all offsets → verify in CE
```

## One-time Setup Commands

### Install x64dbg (run in PowerShell; no admin needed for `C:\work\tools`):
```powershell
# Download latest snapshot
$url = "https://github.com/x64dbg/x64dbg/releases/download/2026.05.27/snapshot_2026-05-27_12-11.zip"
$out = "$env:TEMP\x64dbg.zip"
Invoke-WebRequest -Uri $url -OutFile $out
# Extract
Expand-Archive -Path $out -DestinationPath "C:\work\tools\x64dbg" -Force
# Create desktop shortcut
$wshell = New-Object -ComObject WScript.Shell
$shortcut = $wshell.CreateShortcut("$env:USERPROFILE\Desktop\x64dbg.lnk")
$shortcut.TargetPath = "C:\work\tools\x64dbg\release\x64\x64dbg.exe"
$shortcut.Save()
Write-Host "x64dbg installed to C:\work\tools\x64dbg"
```

### Install ILSpy (run in PowerShell as Admin):
```powershell
# Download latest release
$url = "https://github.com/icsharpcode/ILSpy/releases/latest/download/ILSpy_binaries.zip"
$out = "$env:TEMP\ILSpy.zip"
Invoke-WebRequest -Uri $url -OutFile $out
# Extract
Expand-Archive -Path $out -DestinationPath "C:\tools\ILSpy" -Force
# Create desktop shortcut
$wshell = New-Object -ComObject WScript.Shell
$shortcut = $wshell.CreateShortcut("$env:USERPROFILE\Desktop\ILSpy.lnk")
$shortcut.TargetPath = "C:\tools\ILSpy\ILSpy.exe"
$shortcut.Save()
Write-Host "ILSpy installed to C:\tools\ILSpy"
```

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
