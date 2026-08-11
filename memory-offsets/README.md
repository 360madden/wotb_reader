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

### `fieldValidation` and `chains` (promotion evidence)

`fieldValidation` carries per-field promotion evidence (`status`:
`Unknown`/`Candidate`/`Verified`, evidence entries, launch/replay counts,
approvals). `Verified` requires the schema's complete evidence set and is
what runtime promotion consumes.

Pointer-chain fields (e.g. the position family, published 2026-08-10 via
OD-RECOVERY-083) are recorded in the additive `chains` object: field →
array of `{ "kind": "rootRva" | "memberOffset" | "inlineOffset" | "recordOffset" | "ringIndex" | "entityLookup",
"value": <non-negative int>, "note": <text> }` hops — the module-relative
dereference path the resolver walks. `rootRva` dereferences the root slot;
`memberOffset` dereferences a pointer at (object + value); `inlineOffset`
adds value WITHOUT dereferencing (an inline member); `ringIndex` selects an
INLINE ring entry at (object + value + index·stride) using the Int32 index
field at (object + `indexOffset`) — no ring pointer dereference;
`entityLookup` resolves an entity-map lookup (cached fast path +
alternative tree roots, node layout in its descriptor) with the target
entity id supplied per walk. `ringIndex` requires `indexOffset` and
`stride`; `entityLookup` requires its descriptor fields;
`OffsetChainWalker` walks chains fail-closed, and `OffsetTableReader`
parses `chains` into the model. Chained fields keep their `offsets` value
`0` **by design**: the runtime observation path computes `moduleBase +
offset` (no chain concept) and the ring record is battle-scoped heap, so a
non-zero value would corrupt that path; the resolver reads chained fields
via its own hash-bound layout. The published 11.19.0.10 position chains
are mechanically walkable since OD-RECOVERY-084 (2026-08-10): they use
`inlineOffset` (entities map, no deref), `entityLookup` (cache fast path +
alternative tree roots, node layout in the descriptor, target entity id
supplied per walk) and INLINE `ringIndex` — the same walk the
OD-RECOVERY-083 evidence verified, re-expressed with correct semantics.
`Walk_PublishedTableChains_*` proves the walker's walk of the PUBLISHED
chains equals the resolver's traversal on identical memory. The canonical
form lives in `docs/operations/g0-walkable-position-chains.draft.json`;
the pre-publication memberOffset-spelled form remains in git history
(commit `0e6bdba`) and the ledger. The resolver remains the authoritative
position reader; `OffsetChainWalker` is a proven-equivalent consumer of
the published table.
`schemaVersion` stays 1 — the additive keys are ignored by the legacy
observation path.

**playerHP static chain (2026-08-11, NOT promoted — live verification
still required):** `VerifyPlayerHpChain.java` (hash-bound, **26/26 checks**, verdict
`player-hp-chain-verified` on `1cda5c31…1760307d`) pins the entity base
record's health block: current health as a **signed int16 at
`[entity+0xB8]`**, alive byte at `[entity+0xBA]`, **max health int16 at
`[entity+0x11C]`**, healing int16 at `[entity+0x11E]`, and packed gun
angles (2 × 6-bit) at `[entity+0x7E]`. Evidence: VehicleGameLogic vftable
slot 1 (0x31b560 = byte-verified `MOV EAX,[ECX+0x4]; RET`) is the entity
getter; the `set_health`/`set_healingHealth`/`set_maxHealth`/
`set_isAlive`/`set_gunAnglesPacked` setters read their old values through
it; the state-sync writer `FUN_0166b9f0` and diff-notify twin
`FUN_01675f60` store the same offsets. This REFUTES the earlier
int32-in-tank-record expectation (`[entity+0x3C]`, the `+0x48` rehearsal
fixture): HP is int16 and lives 0x7C bytes past the transform pointer on
the entity record itself. `playerHP` remains `0`/Unknown in the table
until the L1 live session confirms the field empirically on both 11.19.0
replays; the `entity-base` region anchor + int16 correlator pass
(2026-08-11) are the session's tools.

**Entity-base record map beyond HP (2026-08-11, static-only, not
promoted):** the full VehicleGameLogic setter family extends the map for
future discovery — strafing byte `[entity+0x7C]`, engine-mode object ptr
`[entity+0xBC]` (mode + sub byte), hit-marks vector `[entity+0xC8]`,
byte-array mask pair `[entity+0xD4]`/`[entity+0xD8]`, critical-devices
list `[entity+0xE0]`, destroyed-devices list `[entity+0xEC]`,
active-equipments list `[entity+0xF8]`, debug-strings state
`[entity+0x110]` — all read through the entity getter by their setters and
covered by the same 26-check verifier run. Not promoted; they spare future
scans from re-scanning the record.

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

The operational workflow and experiment ledger live in
[`docs/operations/offset-discovery-workflow.md`](../docs/operations/offset-discovery-workflow.md)
and [`docs/operations/offset-discovery-ledger.md`](../docs/operations/offset-discovery-ledger.md).
Use them to timebox discovery, classify address kinds, preserve failures, and avoid
repeating unresolved hypotheses.

Offset discovery follows the timeboxed workflow in the canonical documents:
identity/offline gate → static triage → controlled dynamic anchor → native access
tracing → repeatability → conservative publication. The current 11.19.0.10 table
contains one hash-bound quarantined `playerYaw` (Stale) plus Unknown
`playerPositionX`/`playerPositionZ` (OD-RECOVERY-004/005/006 private-mapping /
heap-dynamic aggregates) and Unknown `replayTime` with the campaign's most
substantial evidence: rolling increased-Double convergence reproduced across
30 verified process launches (OD-012…OD-038), TARGET 10 ≤ 10 reached three
times (OD-031 ×2, OD-036), up to 10 survivors CE-staged with 4 hardware
write-BPs armed (OD-036 end-to-end), and the value-bound 11–17 survivor tail
plateau. All eight offsets remain `0` and no field is runtime-supported.

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
│  3. NATIVE LAYOUT ANALYSIS (CE + x64dbg)  → Field mapping        │
│     • Trace access instructions and register-held struct bases   │
│     • Map neighboring HP/position/yaw/pitch fields               │
│     • Optional managed-artifact check only if artifacts exist     │
│     • Cross-check native layout against static candidates        │
│                                                                  │
│  4. AUTOMATED VERIFICATION (GameHarness + Treader) → Evidence   │
│     • Run the built-in scanner to verify candidates              │
│     • Validate across multiple battles and restarts              │
│     • Promote only after complete evidence requirements          │
│     • Commit redacted evidence summaries to the ledger/table    │
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
dotnet run --project tools/src/WotBTreader.GameHarness -- discover-nearby <reconciled-module-rva> --window 256

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
| Native layout | Cheat Engine + x64dbg | Access instructions, object bases, and member displacements | GameHarness and CE |
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
    "playerYaw": 0,
    "cameraPitch": 0,
    "aliveTankCount": 0
  },
  "fieldValidation": {
    "playerYaw": {
      "status": "Stale",
      "evidence": [
        {
          "provenanceKind": "StaticAnalysis",
          "sourceTool": "Ghidra",
          "notes": "Historical hypothesis retained as Stale evidence only; dynamic verification and address-kind reconciliation are required."
        }
      ],
      "independentProcessLaunches": 0,
      "independentReplays": 0,
      "harnessInvariantsPassed": false,
      "leadApproved": false,
      "decoderAuditorApproved": false
    }
  },
  "confidence": "none",
  "notes": "The historical yaw entry is quarantined evidence only. Do not promote from a global confidence value."
}
```

---

## Current evidence status

| Version | Executable hash | Known offsets | Runtime status |
|---|---|---:|---|
| `11.19.0.10` | `1cda5c31919c9784a41bee7f3270ec1b4536b124c51e8b36f2221b381760307d` | 0/8 usable | `playerYaw` quarantined/Stale; `replayTime` Unknown with 30-launch rolling evidence (no root/RIP yet); runtime reads remain unsupported |

The hash identifies the installed executable used for this evidence snapshot; it is
not proof that a candidate offset is correct. The table intentionally preserves the
quarantined yaw evidence with offset `0` and status `Stale`; the runtime reader maps
zero-valued fields to `Unknown`, so no stale field can authorize a memory read.
Dynamic verification must use a positively verified offline replay and preserve
evidence summaries without committing raw dumps or scan files. Campaign status as
of OD-042-STATIC: the `Invalid password status=68` login failure seen since
14:48 is baseline noise (present in the OD-036 success run too), not an offline-path
blocker; the real replay-start death is `become hidden` + `GameCore::OnBackground`
~2s after `LoadGameScene` with no crash dump; the 401-refresh was hardened (750ms
settle + 4 retries) after its first-ever live failure. Static milestones
OD-039..042-STATIC confirmed the two runtime-written `.data` candidates
(`0x03FA0C74`, `0x03FA012C`) are read-write code-initialized globals, then
**re-classified them as members of a repeating 0x50-byte record family** (base
`0x03FA0C20`) — **NOT standalone gameplay roots**. `0x037F3054` is precisely the
**shared RTTI `type_info` vftable** (every TypeDescriptor's pVFTable points at
it; 48,609 aligned `.data` references). The new `--vtables` mode names
**17,133 of 18,721 vtables** via the COL chain, including `GameScene`
(`0x0319D3C4`, 26 slots, **0 .data roots** — an honest negative for the
vtable-singleton path), `BaseContext`/`RootContext`, and the Vehicle component
family; the Vehicle-family TypeDescriptor xref is negative (0 refs / 0 slots),
exhausting the RTTI name→root path. `EntityList` is a plain struct (0 RTTI hits).
OD-043-STATIC: new `--vtable-root` (class→vtable→data-root query: `GameScene`
vtable `0x0319D3C4`/COL `0x034A89F0` has **0 .data holders** — vtable-singleton
path confirmed negative; `TankComponent` → 19 matches, all `AnyFn` delegate
invokers with `roots=1` each) and `--table-map` (pointer-array decoder) modes;
`.data 0x03B7E198` reclassified as the **DAVA `AnyFn` invoker vtable table** — 34
entries, modal stride `0x2C` (44-byte vtables), 24 named `StaticAnyFnInvoker
<lambda>` vtables binding `TankComponent`/`AimingPointComponent`/`Scene`/`Entity`
(component event subscriptions), all 24 sharing dispatcher fn `0x002C4550`, each
with exactly 1 `.data` root = its own array entry (internally-closed set); 3
`.text` refs incl. a runtime write `mov [0x03B7E198],imm32` at `0x03104FAB`
repointing entry[0] — **dispatch infrastructure, NOT a gameplay root**.
Next step (OD-044): operator-present interactive Find-what-writes on a staged
≤11-survivor replayTime set (the live anchor), optionally piloting delta-compare.

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

- **Position X/Z or replay time are the preferred first anchors** when a controlled replay transition is available; HP is a fallback because damage may be infrequent
- **Positions often form a contiguous float triple** (X, Y, Z at consecutive offsets) — finding one finds all three
- **Replay time is double precision** — many scanners default to int32/float, ensure you select `Double`
- **Yaw and Camera Pitch** are typically adjacent floats near the position data
- **Pointer scan after game restart** — offset chains that survive restart are robust static offsets
- **Ghidra string references** — strings like `"health"` or `"replayTime"` are triage clues, not proof of a field or module RVA
- **Managed-artifact tools are conditional** — do not assume Unity, Mono, IL2CPP, or `Assembly-CSharp.dll` in the native DAVA-era client
- **Use the approved gate** — Cheat Engine and GameHarness scanning are restricted to positively verified offline replay sessions; elevation depends on the local Windows security context

## Never commit

- `scanner-state.json` — runtime state, added to .gitignore
- Offsets with `confidence: "none"` (placeholders only — update when real data exists)
- Absolute file paths or machine-specific paths in notes
