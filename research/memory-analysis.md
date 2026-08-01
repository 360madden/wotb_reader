# Memory Analysis Techniques for Replay Data

## Goal

Find replay-related data structures in the running `wotblitz.exe` process memory.
This would enable:
- Reading the current replay file path from memory
- Writing a new replay path to trigger reload
- Detecting replay playback state without log monitoring
- Finding memory offsets for telemetry data

## Technique 1: String Scanning for Replay Path

### Step-by-step:

1. **Attach Cheat Engine to wotblitz.exe**
2. **Start a replay playback** (via in-game UI or command line)
3. **Scan for the replay file path string:**
   - Value Type: String
   - Encoding: UTF-16 (Windows wide strings)
   - Search for: `\wotbreplay` or the full path
4. **Find what accesses this address:**
   - Right-click → "Find out what writes to this address"
   - This reveals the assembly instruction writing the path
5. **Back-trace to the function:**
   - The instruction is part of a replay-loading or file-I/O function
   - This function's base pointer is the replay manager object

### Expected Findings:
- A global `ReplayManager` singleton
- The replay file path string pointer
- Replay playback state (playing, paused, stopped)
- Current replay time/tick counter

## Technique 2: Pointer Chain Derivation

Once a replay-related address is found:

1. **Save the address** in Cheat Engine
2. **Restart the game** and re-attach
3. **Find the new address** of the same variable
4. **Pointer scan** targeting both addresses
5. **Rescan** to filter false pointers
6. **Repeat** until a stable chain remains

Example chain:
```
wotblitz.exe + 0x01234567 → offset 0x48 → offset 0x10 → target
```

This chain can be read via `ReadProcessMemory` from external code.

## Technique 3: Static Analysis with Ghidra

The project already has Ghidra scripts in `tools/ghidra-scripts/`:

1. **Open wotblitz.exe in Ghidra**
2. **Search for strings:** "replay", ".wotbreplay", "START_REPLAY"
3. **Cross-reference (Xrefs):** Press `X` on interesting strings
4. **Trace callers:** Follow the function calls to find the replay subsystem
5. **Document offsets:** Record function addresses and data structure layouts

### Key strings to search for:
- `START_REPLAY_LOCAL`
- `STOP_REPLAY_LOCAL`
- `.wotbreplay`
- `replays/`
- `Uploaded`
- `DAVAProject`

## Technique 4: ReClass.NET for Structure Mapping

ReClass.NET can reconstruct C++ class layouts at runtime:

1. Attach to wotblitz.exe while a replay is playing
2. Navigate to the replay manager's memory region
3. Map out surrounding bytes:
   - VTable pointers (identify class type)
   - Member variables (booleans for state, integers for counters)
   - Nested pointers (to replay data, scene data)
   - String pointers (file paths, map names)

## Technique 5: Memory Write for Replay Switching

**Hypothesis:** The game stores the "current replay path" in a string variable.
If we can:
1. Find the string pointer via scanning
2. Allocate new memory for a different replay path
3. Update the pointer to point to the new path
4. Call the replay-loading function (or trigger a scene transition)

Then we could achieve live replay switching!

**Risks:**
- Anti-cheat may detect memory writes
- String length mismatch could cause buffer overflow
- The game may validate the replay file before loading

## Integration with Existing Codebase

The project already has:
- `MemoryScanEngine` — snapshot/compare/filter memory values
- `MemoryScanDiscoverer` — neighborhood scanning around known offsets
- `GuardedMemoryReader` — Reads process memory with identity verification
- `GameHarness` CLI — `discover`, `snapshot`, `compare`, `nearby` commands

These tools can be used to search for replay-related data in the running game:

```bash
# Scan for the replay file path in memory
treader discover replayPath String "%LOCALAPPDATA%\wotblitz\DAVAProject\replays\battle.wotbreplay"

# Take a snapshot before/after replay starts
treader snapshot 4
treader compare <sessionId> changed
```

## Priority Targets for Memory Discovery

| Data | Type | Utility |
|------|------|---------|
| Current replay file path | UTF-16 string | Trigger replay switch |
| Replay playback state | int32/bool | Detect playing/paused/stopped |
| Replay time/tick | float/int32 | Sync overlay position plot |
| Scene state | enum | Detect hangar vs replay vs loading |
| Game version string | UTF-16 string | Version validation |
