# Offset Discovery Guide

Last updated: 2026-08-02

> **Operating workflow:** use [`offset-discovery-workflow.md`](offset-discovery-workflow.md)
> for timeboxes, pivots, address-kind classification, and the next-session protocol.
> Record every attempt in [`offset-discovery-ledger.md`](offset-discovery-ledger.md),
> including partials and failures. This guide retains the detailed tool reference.

## Current state

| Item | Status |
|------|--------|
| Game version installed | 11.19.0.10 (`C:\Games\World_of_Tanks_Blitz\wotblitz.exe`, ~71MB) |
| Offset file | `memory-offsets/11.19.0.10.json` — hash-bound; `playerYaw` is Stale/quarantined; 7 fields unknown |
| Ghidra 12.1.2 | Installed at `C:\work\tools\ghidra_12.1.2_PUBLIC` |
| Cheat Engine 7.7 | Installed at `C:\Program Files\Cheat Engine\` |
| x64dbg | Installed at `C:\work\tools\x64dbg` — snapshot 2026.05.27 (see Phase 2 below) |
| Managed-artifact decompiler | **Conditional only** — verify exact installation artifacts before using; see Phase 3 below |
| GameHarness scanner | `scan`/`probe` and `discover*` check the offline-session gate via HTTP |
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

### 3. Optional managed-artifact check

The current repository treats WoT Blitz as a native DAVA-era client. Do **not**
assume that Unity, Mono, IL2CPP, `Assembly-CSharp.dll`, or
`global-metadata.dat` exists. First inspect the exact installation and record
what is actually present. Only if the client contains relevant managed artifacts
should an assembly decompiler become a discovery branch.

ILSpy is therefore **conditional, not a required phase**. If applicable:

1. Verify the artifact belongs to the exact executable/version campaign.
2. Open it read-only and record the artifact hash.
3. Search for type/field layout clues, but treat names and inferred offsets as
   hypotheses until native access behavior confirms them.
4. If the artifacts are absent or unrelated, close this branch immediately and
   continue with native Ghidra/CE/x64dbg work.

## Full Discovery Pipeline

The operating workflow is timeboxed and authoritative. This section maps the
installed tools to its stages; the managed-artifact branch is optional.

```
Phase 0: identity + offline gate
Phase 1: Ghidra static triage
Phase 2: CE controlled dynamic anchor
Phase 3: CE/x64dbg native access + layout tracing
Phase 4: two launches × two replays + GameHarness evidence
Phase 5: conservative publication and promotion review
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
| playerYaw | 67 matches | 5 unique | Conflicting representations | ⚠️ Ambiguous; quarantined |
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
  → Compare with the reconciled candidate record, not the current quarantined yaw values.
  → A nearby address is not confirmation; classify the address kind first.
```

### Phase 3 — Native instruction and layout tracing

The current client is treated as native DAVA-era code. Start with Cheat Engine's
access/write trace, then use x64dbg when the instruction or register context is
insufficient. Any managed-artifact check is optional corroboration only.

#### Step-by-step:

**1. Capture the native access path**
```
CE: right-click the best dynamic candidate → "Find out what writes"
    → record the instruction, register/pointer expression, and process/module identity
```

**2. Classify the address kind**
```
Determine whether the evidence is a member displacement, pointer chain,
or heap-dynamic address. A member displacement such as `[reg+0x34]` is not a
module RVA.
```

**3. Inspect neighboring native fields**
Inspect nearby fields from the same proven object base. Look for the expected
position triple, HP-like integer, and angle values, then repeat the same member
displacement or pointer chain after a fresh launch.

**4. Confirm with x64dbg when needed**

Use x64dbg to confirm instruction/register context when CE is insufficient. Do
not infer a module offset from a nearby address or from a conceptual layout.

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
   Set only evidence-backed fields to their discovered offsets. Keep each field
`Candidate` until the independent-launch, independent-replay, harness, static,
and approval requirements are complete; never set a field to `Verified` from one
scan or one battle.

5. **Run the read-only evidence report:**
   ```powershell
   .\tools\report-offset-evidence.ps1 -GameVersion 11.19.0.10
   ```
   A non-`Unknown` API response is not promotion evidence. Runtime reads remain
   unsupported until the exact executable hash and every per-field promotion
   requirement are satisfied.

### Discovery output and publication rules

`multiscan.lua` has two output shapes:

- `autoDiscover()` writes `fieldResults`, containing one result object per
  scanned field.
- `saveDiscovered()` writes the older single-field `fieldName` + `candidates`
  shape for interactive scans.

`tools/discover-offsets.ps1` accepts both shapes. It rejects invalid or unknown
fields and writes a field into the versioned offset table only when raw, reported,
and normalized candidate counts are all exactly one, decimal/hex forms agree,
and the candidate is inside the named `wotblitz.exe` module range. Multiple,
heap-only, stale, legacy-unclassified, or otherwise ambiguous results are
report-only; they never overwrite existing evidence. Published values remain
`Candidate`, never `Verified`, and receive `DynamicScan` provenance. The
executable hash is updated only from the local binary and is still required
before runtime reads.

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

Since the current static run produced no reconciled runtime anchor, the fastest
path to all 8 fields is to establish one controlled native dynamic anchor first:

```
1. CE → establish one controlled dynamic anchor (position, replay time, or HP)
   ↓ Capture candidate counts and state transitions
2. CE → trace accesses/writes for the best candidates
   ↓ Identify a member displacement or pointer-chain root
3. x64dbg → confirm instruction/register context when CE is insufficient
   ↓ Reconstruct neighboring fields from the same object
4. Repeat across two launches and two replays before publication
```

If the native path produces only heap-dynamic addresses, use x64dbg or a
pointer scan to find a stable root. If no stable root appears within the
session timebox, record the result as a partial and pivot to another field;
do not publish a heap address as a module-relative offset.

## One-time Setup Commands

### Install x64dbg (quick PowerShell path; full steps and verification in [§1](#1-install-x64dbg))

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

### Managed-artifact decompiler

See [§3 — Optional managed-artifact check](#3-optional-managed-artifact-check): do not
install or use a managed-assembly decompiler unless Phase 0 confirms this exact
WoT Blitz installation contains relevant managed artifacts.

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

## UltimateScanner phases 1–4 — implemented, evidence-only

The standalone `ultimate-scanner/` module now provides four bounded capabilities
behind the coordinator's positively verified offline-replay gate. The coordinator
resolves the trusted executable's main-module base immediately before each scan
(and before verified runtime observation), rather than caching a startup lookup.
A transient Windows `MainModule` failure therefore denies only that operation and
can recover on the next request without weakening the replay, PID, start-identity,
path, version, or SHA-256 checks. The base lookup is performed outside the session
lock so lifecycle revocation and monitor callbacks are not blocked by Windows
process-module enumeration. Authorization cancellation is propagated through the
module lookup and every scanner read. A read authorization gate linearizes
revocation with each `ReadProcessMemory` admission: an operation admitted before
revocation may complete, but an operation admitted after revocation is denied and
does not call Win32. Process identity and region-query calls remain separate
read-only checks. This boundary does not guarantee that an already-running
native API call can be interrupted mid-call.

| Phase | Capability | Boundary |
|---|---|---|
| 1 | identity-bound typed scans, architecture checks, cancellation, candidate caps | x64-hosted read-only process lease for native x64 or WOW64 x86 targets; no runtime offset promotion |
| 2 | 1 MiB chunked scans, reusable read buffers, 512 MiB snapshot cap, expiring sessions | failed reads are skipped or retained explicitly; results remain evidence |
| 3 | alignment and private/mapped/image region filters, typed snapshot comparisons, bounded neighborhood reads | module name is executable metadata only; `ModuleSize=0` means unavailable; address kinds preserve private/mapped/image mapping |
| 4 | wildcard AOB scans, optional best-effort working-set classification, bounded pointer chains | COW means private working-set evidence with COW-compatible protection, not proof of a COW event |

HTTP surfaces:

- `POST /api/v1/game/discover/pattern` — hex pattern plus optional hex wildcard mask;
- `POST /api/v1/game/discover/pointer-chain` — one root and at most four offsets;
- existing snapshot/compare/neighborhood endpoints expose alignment, region,
  rolling-baseline, truncation, address-kind, and architecture metadata. Snapshot
  ranges align upward from an unaligned minimum relative to the trusted module
  base, so a narrow range does not skip valid aligned values. Scan
  candidates expose `baseDisplacement`; it is not a main-module RVA unless
  image ownership is independently proven. The former `relativeOffset` and
  `relativeOffsetDecimal` JSON names remain compatibility aliases.

The GameHarness routes these as `discover-pattern` and
`discover-pointer-chain` (aliases: `pattern`, `pointer-chain`). Pattern input
uses even-length hexadecimal strings; `00FF00` is a mask example. A non-zero
mask byte is a wildcard. The scanner never writes process memory, never treats
candidate evidence as a verified runtime offset, and does not scrub or publish
private game-derived dumps. Scanner discovery resolves the trusted executable
module base independently of the offset table; verified offsets are still
required before the separate runtime telemetry observer reads fields. API
alignment, range, candidate-cap, window-size, and comparison-mode options are
rejected when invalid rather than silently normalized. A lifecycle-feed gap or
producer failure immediately revokes scanner authorization. Monitor callbacks are bound to the active managed-launch generation, so a canceled monitor from an older launch cannot revoke or overwrite a replacement session. Rolling comparisons expose `RetainedCount` separately
when an unreadable prior chunk is carried forward; it is not included in the
changed/unchanged counters or returned candidate list.

Pointer-chain semantics are deliberately explicit: each configured offset is
added to the current address before reading the next pointer; chains are limited
to four dereferences, reject invalid user addresses and cycles, and report
`pointer-chain` evidence only. The current results are not sufficient to promote
an offset into `memory-offsets/`; follow the two-launch/two-replay requirements
below.

## External scanner access

The Ultimate Scanner is exposed through the single loopback web host and the
Windows GameHarness CLI. Other local programs should use the HTTP surface when
they need structured results; use the CLI for operator workflows and shell
automation. Both surfaces use the same coordinator authorization boundary and
never open a process handle themselves.

### Connection and authorization

1. Start `serve` and wait for its rendezvous file.
2. Read `%LOCALAPPDATA%\\WotBTreader\\rendezvous\\web.json` as a local,
   owner-only capability record. It contains `baseUri`, `capability`,
   `expiresAtUtc`, and the publishing `processId`.
3. Restrict the base URI to loopback and reject expired/dead-process records.
4. Send the capability as `X-WotBTreader-Capability` on every `POST` or
   `DELETE`. Loopback reachability alone is not authorization.
5. Launch a managed offline replay and wait for `OfflineReplayVerified`; the
   coordinator still rechecks process identity and revokes in-flight work when
   lifecycle evidence becomes stale or invalid.

Read-only state routes do not require the capability header, but scanner routes
fail closed when the offline-session gate is not satisfied.

### HTTP route matrix

| Method | Route | Purpose |
|---|---|---|
| GET | `/api/v1/game/state` | Query the evidence-backed session gate |
| GET | `/api/v1/game/memory` | Read the safe telemetry observation |
| POST | `/api/v1/game/discover` | Typed value scan (`Float`, `Int32`, `Double`) |
| POST | `/api/v1/game/discover/pattern` | Bounded AOB/wildcard scan |
| POST | `/api/v1/game/discover/pointer-chain` | Bounded pointer-chain evidence probe |
| POST | `/api/v1/game/discover/snapshot` | Create a bounded snapshot and receive `sessionId` |
| POST | `/api/v1/game/discover/compare/{sessionId}` | Compare a snapshot (`changed`, `unchanged`, `increased`, `decreased`) |
| POST | `/api/v1/game/discover/neighborhood` | Scan a bounded window around a reference offset |
| DELETE | `/api/v1/game/discover/session/{sessionId}` | Discard retained snapshot state |

Example requests from a trusted local client:

```powershell
$record = Get-Content "$env:LOCALAPPDATA\\WotBTreader\\rendezvous\\web.json" | ConvertFrom-Json
$headers = @{ 'X-WotBTreader-Capability' = $record.capability }
Invoke-RestMethod "$($record.baseUri)/api/v1/game/state"
Invoke-RestMethod "$($record.baseUri)/api/v1/game/discover/pattern" `
  -Method Post -Headers $headers -ContentType 'application/json' `
  -Body (@{
    fieldName = 'signature'; expectedValueHex = '488B90';
    toleranceMaskHex = '00FF00'; maxCandidates = 200;
    minRegionSize = 4096; alignment = 1
    # includeImageRegions=true scans private/mapped+image; imageRegionsOnly=true with includeImageRegions restricts to MEM_IMAGE only
  } | ConvertTo-Json)
```

Responses contain evidence metadata such as `addressKind`, `baseDisplacement`,
architecture, truncation, and candidate counts. They do not promote offsets into
the runtime table. A `401` means the capability is missing/expired; a `400`
with `discover.gate_not_satisfied` means the offline evidence gate is closed.

### GameHarness CLI matrix

```text
discover <field> <Float|Int32|Double> <value> [tolerance]
discover-pattern <field> <patternHex> [toleranceMaskHex] [--alignment 1|2|4|8]
discover-pointer-chain <rootOffset> <offset1,offset2,...>
discover-snapshot 4 [--float-min <f>] [--float-max <f>] [--int-min <n>] [--int-max <n>]
                  [--max-bytes <n>]   # explicit retained-byte budget; 0 = engine ceiling (512 MiB); soft-caps (partial snapshot) when full
                  # Float bounds send valueKind=Float (alignment 4); otherwise Int32.
discover-compare <sessionId> [changed|unchanged|increased|decreased]
discover-nearby <refOffset> [--window <64-4096>]
discover-discard <sessionId>
discover-campaign [--comparisons <1-4>] [--interval-seconds <1-5>]
                  [--span-mib <1-64>] [--float-min <f>] [--float-max <f>]
                  [--mode <changed|unchanged|increased|decreased>]
                  [--max-bytes <0-512 MiB>]   # retained-byte budget; 0 = engine ceiling
```

The CLI validates the rendezvous capability before making requests, sends it on
all unsafe calls, uses invariant numeric parsing, rejects malformed ranges, and
returns non-zero exit codes for host, HTTP, timeout, and input failures. It
prints bounded summaries rather than raw memory dumps.

`discover-campaign` is the privacy-safe first pass for a fresh managed launch.
It privately derives the main-module range, snapshots aligned Float32 values in
at most 64 MiB, performs rolling comparisons, requests only one candidate in
each loopback response, suppresses that candidate's address and value, and
always attempts to discard the retained scanner session. Defaults are two
comparisons, two-second intervals, a 16 MiB cap, `-500..500`, and `changed`.
The configured waits may total at most eight seconds so the workflow leaves
room inside the coordinator's 15-second authorization lifetime for bounded
native reads and cleanup.

Interactive controlled-transition work may explicitly start the loopback web
host with `Research:OfflineReplayEvidenceLifetimeSeconds` set from 5 through
120 seconds. The default remains 15 seconds. This bounded research lease does
not delay monitor revocation for replay stop, unhealthy or gapped evidence,
reported process exit or identity change, cancellation, or expiry. Guarded
scanner reads also revalidate the exact process identity independently. Follow
the protocol in `offset-discovery-workflow.md`; never use the longer lease for
an uncorrelated process or an online session.

Campaign output is aggregate variability reconnaissance. Natural replay
changes cannot identify `playerPositionX`, prove a member displacement or RVA,
classify an address, or satisfy the controlled-transition requirement. Record
launch independence and replay independence separately in the ledger; repeated
launches of one replay do not satisfy the two-replay promotion rule.

## Managed-launch diagnostics

The managed replay launch emits bounded structured stage logs so a failed launch
can be located without exposing replay bytes or memory contents. Stages are
`prepare`, `executable_lease`, `artifact_staging`, `suspended_process`,
`correlation`, `resume`, `handoff`, and `lifecycle_evidence`. Normal transitions
use event `3135` (`ManagedLaunchStage`); operation-result failures use event
`3136` (`ManagedLaunchStageFailed`).

Scanner diagnostics are aggregate-only: operation kind, bounded counts,
truncation/read-failure status, elapsed time, measured architecture, and
non-sensitive executable identity metadata. They never persist field labels,
expected/mask bytes, query values, candidate addresses, decoded values, or
observed process-memory bytes.

After resume, lifecycle correlation is bounded by
`GameIntegrationOptions.LifecycleEvidenceTimeout`, which defaults to 45 seconds
and accepts 5 seconds through 5 minutes. Missing correlated evidence fails closed
with state `Denied` and reason `launch.lifecycle_evidence_timeout`; the active
handed-off child is terminated when its identity-bound lease is still available.
Replacement launches and coordinator disposal likewise terminate unverified
handed-off children, while verified children remain alive. Lease disposal retries
a termination request before releasing the process handle.
Event `3140` (`ManagedLaunchLifecycleEvidenceTimeout`) records the process ID,
timeout, and termination result. A timeout is a launch-gate failure, not a valid
process-selection signal; do not attach to a process from that attempt.

After verification, `GameIntegrationOptions.OfflineReplayEvidenceLifetime`
limits the age of that exact correlated live start marker. It defaults to 15
seconds and accepts 5 seconds through a hard maximum of two minutes. Only the
web host's explicit `Research:OfflineReplayEvidenceLifetimeSeconds` setting
changes it; ordinary CLI and web-host startup retain the default.

The reconciled launch baseline can legitimately contain zero active native-log
sources. Its completed UTC time remains an authorization anchor: a newly
enumerated generation-one source is live only when both its native file creation
time and parsed marker timestamp are at or after that barrier. Stale
prepopulated files and markers without timestamps remain historical.

## GameHarness M2 gate — ✅ WIRED

The `scan` and `probe` commands in GameHarness check the offline-session gate
via `GET /api/v1/game/state` (read from the rendezvous file). They are read-only
status reports; the `discover*` commands perform gated discovery. The full flow is:

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
in the current managed launch implementation.

To launch a replay and reach the verified state:
```
1. import a .wotbreplay via CLI
2. serve (start the web host)
3. POST /api/v1/game/launch with the source artifact ID
4. GameHarness scan  (reports "gate satisfied" when OfflineReplayVerified)
```
