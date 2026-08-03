# Offset-discovery strategy v2 — unified three-track plan

**Status:** Active (2026-08-03). Supersedes the value-first rolling-only approach
as the campaign's operating strategy; the rolling pipeline remains one track, not
the whole plan.

## 1. Common goal

Produce **usable, evidence-classified offsets** for the HUD telemetry fields
(`playerPositionX/Y/Z`, `playerYaw`, `playerHP`, `cameraPitch`,
`aliveTankCount`, `replayTime`) against the **hash-bound 11.19.0.10**
`wotblitz.exe`, where each promoted field meets the evidence bar:

- **Address kind** classified (module RVA / member displacement / heap-dynamic)
- **Hash-bound** to the exact executable
- **Dynamically confirmed** in a positively verified offline replay session
- **Not fabricated**: unknown stays unknown; candidates stay candidate-only

Every offset published to `memory-offsets/*.json` must trace to one of the three
tracks below. Tracks share one ledger, one workflow doc, one evidence bar.

## 2. The three tracks

```
Track A (offline static)          Track B (hangar state)          Track C (replay-marker live)
┌─────────────────────────┐       ┌───────────────────────┐       ┌────────────────────────────┐
│ PE/RTTI analysis of     │       │ Game running, NO      │       │ Game running, replay       │
│ wotblitz.exe on disk    │       │ match — stable,       │       │ verified (existing gate)  │
│ - no game process       │       │ known ground truth    │       │ - lease-bound, monitored  │
│ - no lease, no flake    │       │                       │       │                            │
├─────────────────────────┤       ├───────────────────────┤       ├────────────────────────────┤
│ • RTTI back-door:       │       │ • Scan for values     │       │ • replayTime rolling      │
│   mangled name →        │       │   shown in hangar UI  │       │   (proven 39M→11)         │
│   TypeDescriptor →      │       │   (HP number, tank    │       │ • NEW delta-compare       │
│   vtables → .data roots │       │   name string, map    │       │   (replay Δ as filter)    │
│ • Xref-driven .data     │       │   names)              │       │ • X/Y/Z value-equality    │
│   slot discovery        │       │ • Stable snapshot     │       │   at synchronized time   │
│ • Chain-root verify     │       │   baseline (kills     │       │ • Multi-axis intersection│
│   (reloc + code refs)   │       │   66M spike problem)  │       │ • Find-what-writes on    │
│ • Source-path strings   │       │ • Known-truth offsets │       │   ≤2–4 survivors          │
│   (Ghidra anchors)      │       │   (HP float at known  │       │                            │
│                         │       │   value)              │       │                            │
└─────────────────────────┘       └───────────────────────┘       └────────────────────────────┘
```

**Data flows between tracks:**

- **A → C:** static roots and RTTI names give C its pointer-chain *hypotheses* to
  confirm live; A's rules-outs prevent C from repeating failed scans.
- **B → C:** hangar-discovered offsets (HP, tank name) are confirmed under the
  replay gate; B's stable baseline replaces the load-transition snapshot that
  produced the 66M-candidate failure (OD-025/026).
- **C → A:** live-confirmed addresses feed back as new static xrefs to hunt.
- **All → ledger:** every attempt, partial, and rules-out is appended.

## 3. Why this strategy (evidence since OD-001…038)

| Observed failure | Root cause | Track that fixes it |
|---|---|---|
| Rolling plateaus at 11–17 survivors (OD-032…038) | Many frame-ticking values pass "increased" | C: delta + multi-axis intersection |
| No static root found by AOB (OD-007/008/009) | Absolute-pointer AOBs, wrong tool | A: xref-driven .data discovery (proven 2,398 store slots) |
| Community chain root 0x03E91978 invalid (verified 2026-08-03) | Claimed root is a string, 0 code refs | A: `find-static-roots.py --chain` proves/refutes in seconds |
| Automated CE write-BPs: 0 hits (OD-009/010/011/020) | Heap-dynamic writes invisible to automated CE | C: shrink survivors ≤2–4, then *interactive* Find-what-writes |
| 66M-candidate snapshot during load (OD-025) | Snapshot at game-load transition | B: stable hangar baseline |
| Login failure misread as blocker (OD-037→038 correction) | Log noise mistaken for cause | A/C: evidence-first diagnosis |

## 4. Track A — offline static (no game process)

**Tooling (built, committed):** `tools/find-static-roots.py` — pure stdlib,
strong file logging, three capabilities:

| Capability | Command | Output |
|---|---|---|
| Chain-root verification | `--chain 0x03E91978` | section, reloc target?, .text refs, on-disk shape, verdict |
| Xref .data discovery | `--xref-data --min-refs N` | ranked store/load/reloc/shape candidates |
| RTTI back-door | `--rtti A,B,C` (comma-separated batch) | per-class mangled name → TypeDescriptor → vtables → .data roots |

**Proven results (2026-08-03 runs against hash-bound binary):**
- Community root `0x03E91978`: **not a root** (in a string, 0 code refs).
- Xref discovery: 2,398 store slots / 5,661 load targets; 163 candidates at
  ≥3 refs. Top zero-initialized store slots (`0x03FA0C74`, `0x03FA012C`) are
  runtime-written RMW candidates — the singleton shape.
- **Batch chain verification (OD-039-STATIC):** `0x03FA0C74` has **9 .text
  references** and `0x03FA012C` has **6** — both fail the reloc test, which is
  the *expected* signature of a code-initialized root (not in the reloc table
  because startup writes it). These are the campaign's strongest static root
  candidates to date. The `AvatarContextBattle` TypeDescriptor (`0x03E7DF28`)
  has 0 code refs — dead slot, expected for RTTI metadata.
- **Reference-site decode (OD-040-STATIC):** `tools/find-static-roots.py`
  `--refs`/`--fields` confirms both candidates are **read-write
  code-initialized globals** — `0x03FA0C74`: 9 refs (5 load + 4 store;
  A1/A3 `mov eax,[abs]` + 8B/89 `mov r32,[m+disp32]` ecx) across 3 disjoint
  code clusters (`0x0005D5xx`, `0x006E52xx`, `0x006F18xx`); `0x03FA012C`:
  6 refs (2 load + 4 store; all A1/A3) across 2 clusters (`0x005F7Bxx`,
  `0x006017xx`). The store mix is the offline Find-what-writes equivalent —
  runtime code writes these slots, so they are not dead data. Field dump
  pre-computes member displacements: the `.rdata` pointer `0x037F3054`
  repeats at `+0xFFFFFFB4`/`+0x4`/`+0x54` around `0x03FA0C74` (likely a
  shared vtable/type-descriptor pointer) — a prepared offset list for the
  live probe.
- **Batch RTTI walk (all chain classes):** TypeDescriptors located for
  `VehicleGameLogicComponent` (`0x03C24F4C`), `AppContextImpl` (`0x03E356F4`),
  `ScreensFlow` (`0x03E35C74`), `GameScene` (`0x03DB9AAC`),
  `GameSceneController` (`0x03F1064C`), `GameCameraComponent` (`0x03C19E98`),
  `GameCameraSingletonComponent` (`0x03E216B8`), `VehicleDescr`
  (`0x03DBC468`), and the whole `Vehicle*Component` family (809 TDs under
  `Vehicle`). `EntityList` has **0 RTTI name hits** — it is a plain struct
  (or its vtable is not name-referenced), so it is *not* reachable via the
  RTTI back-door; it must be found via xref/store-slot discovery instead.
- RTTI: `AvatarContextBattle` mangled name at `0x03E7DF30` plus **embedded
  source path** `C:/ba/tc/work/t/client/Classes/Battle/AvatarContextBattle.cpp`
  at `0x0327735A` — direct Ghidra anchors.
- The hottest load slot (98K refs, `0x03FBCC74`) is the MSVC `/GS`
  `__security_cookie` — a proven false-positive class to filter in tooling.

**Rules:**
- Every static claim is classified (module RVA / member displacement /
  heap-dynamic / string / false-positive) before any live work.
- Candidate roots must satisfy ≥1 of: reloc target, ≥1 .text reference,
  zeroed runtime-written slot.
- Never publish a static-only offset; static output is hypothesis-generating.

## 5. Track B — hangar state (game running, no match)

The game at hangar/menu is a **stable, flake-free** process with known ground
truth visible in the UI (current tank name, its HP bar number, player name,
map names in the replays list).

| Source | Ground truth | What it yields |
|---|---|---|
| Hangar tank | tank name string, HP number | value-scan anchors; float/int offset discovery |
| Replays list | map names, timestamps | string anchors for scene/map structs |
| DAVAProject logs | version, session markers | free telemetry (already monitored) |
| Uploaded-replays storage | in-game tab source | Approach F: replay delivery without restarts |

**Protocol (offline-gated, hangar = no replay gate required beyond process
presence):**
1. Launch game to hangar via `IGameProcessLauncher` (no replay argv).
2. Read the UI-known values (name string, HP number) from the screen/state.
3. Snapshot-filter scan for the exact value (string bytes, float/int).
4. Walk back to the owning struct (neighborhood scan + pointer chains).
5. Classify and confirm later under Track C.

**Why it works when replay rolling struggled:** no replay-clock ticking, no
multi-copy clocks, no load transition — a clean baseline with a known value is
the classic CE "find what you already know" that the campaign never used.

## 6. Track C — replay-marker live scanning (existing gate)

The proven rolling pipeline (gate → snapshot → compare → survivors) is retained
and extended with replay-derived markers.

### C1. replayTime rolling (unchanged, proven)
Double "increased" rolling 39M → 11 survivors across 30 verified launches.

### C2. NEW: delta-compare (implemented 2026-08-03)
The replay knows the exact change of a field between two frames. `delta` compare
mode keeps candidates whose value changed by ≈K within tolerance — far more
selective than the four boolean modes.

- Engine: `MemoryScanEngine.Compare` mode `"delta"` + `PassesDelta`.
- API: `POST /api/v1/game/discover/compare/{sessionId}` with
  `CompareMode: "delta"`, `DeltaTarget`, `DeltaTolerance`.
- Replay-derived deltas to feed it: position Δ (X/Y/Z per frame), speed
  (|Δpos|/Δt), HP (damage-delta series).
- Files changed: `MemoryScanEngine.cs`, `GameSessionContracts.cs`,
  `GameSessionCoordinator.cs`, `GameApiEndpoints.cs`,
  `OffsetDiscoveryContracts.cs`, tests, and the rolling driver
  (`roll-replay-time-increased.ps1` gained `-CompareMode`/`-DeltaTarget`/
  `-DeltaTolerance` pass-through).

**Driver usage (OD-040):**
`-CompareMode delta -DeltaTarget <replayDelta> -DeltaTolerance <tol>` — the
rolling baseline advances each round, so the delta is measured against the
*previous* round, not the original snapshot. Non-delta modes reject stray
delta params (engine validation). First live validation is the Track C2 pilot.

### C3. Value-equality at synchronized time
At known replay-time T (established by C1), snapshot-filter `FloatMin=FloatMax≈
X(T)`. Only the true copy holds exactly X(T) — more selective than "increased".

### C4. Multi-axis intersection
Filter X(T), then Y(T), then Z(T) on the same session; survivors must pass all
three. X alone ≈ 10⁶ hits; X+Y+Z simultaneously → only the real position copy.
Expected outcome: ≤2–4 survivors → ready for interactive Find-what-writes.

### C5. Interactive Find-what-writes (operator-owned, final step)
With ≤2–4 staged survivors, the operator runs Find-what-writes in the held
green window to capture the writing instruction + struct base. This is the
missing RIP/root evidence the whole campaign has lacked (all automated CE
write-BP attempts: 0 hits).

## 7. Tooling decisions: build vs. third-party

| Need | Decision | Rationale |
|---|---|---|
| Static PE/RTTI analysis | **Build** (`tools/find-static-roots.py`) | stdlib-only, no install; proven results; repo convention (Python = offline tooling only) |
| True instruction decoding | **Build** (A1/A3, 89/8B/8D/C7 + ModRM patterns) | covers the absolute-reference forms; capstone would be a later optimization, not a dependency |
| Debugger for write-capture | **Third-party** (x64dbg, already locked in `tools.lock.json`) | repo cannot set breakpoints by design (no injection); x64dbg is the sanctioned external |
| Dynamic RE / class layout | **Third-party** (ReClass.NET, CE — documented in `research/community-tools.md`) | interactive tools; not replaceable in-repo |
| Ghidra class recovery | **Deferred** (`Ghidra-Cpp-Class-Analyzer`) | beneficial but needs Ghidra install; Python RTTI walk covers the same ground offline first |
| DVPL asset decode | **Third-party** (rifsxd/dvpl_lz4 etc.) | asset decoding is out of scope for offsets; DvplReader already covers the game path |

Policy: add a third-party tool only after documenting a concrete capability
failure (per `tools/external/README.md`); every addition registers in
`tools.lock.json`.

## 8. Execution order (next sessions)

1. **Track C2 pilot:** run delta-compare live with a replay-derived position
   delta against a verified session; measure survivor collapse vs "increased".
2. **Track C3/C4:** value-equality X/Y/Z intersection on the same session;
   target ≤2–4 survivors.
3. **Track B pilot:** hangar-state known-truth scan (HP number, tank name).
4. **Track A:** batch RTTI walk is *done* (OD-039-STATIC) — TypeDescriptors
   located for the whole chain; next step is xref-discovery from the `Vehicle`
   component family and live-confirming the two static root candidates
   (`0x03FA0C74`, `0x03FA012C`) against a `replayTime`-anchored session
   (validate them as the viewpoint-vehicle struct chain, not bare globals).
5. **C5:** operator interactive Find-what-writes on the smallest staged set.

Each session: same ledger entry, workflow stop rules, handoff, and commit.

## 9. Risks & watch items

- **Reforged/UE5** (announced 2026-06-17, postponed): all DAVA offsets are
  time-limited. Track A (replay decode, static analysis) survives format-agnostic
  pipeline; deep DAVA struct mapping may be obsoleted.
- **Independent replay content (BLK-0019):** `independentReplays` is still 0;
  promotion remains blocked until a content-distinct second replay exists.
- **False positives:** security cookie, RTTI strings, and reference-count
  ranking all produce decoys — every candidate passes the classification bar.
- **Lease walls:** Track C work stays inside the 120s research lease; B and A
  are lease-free by construction.

## 10. Conventions compliance

- Evidence-first: no fabricated promotion; candidates stay candidate-only.
- Ledger/workflow/handoffs: append-only; every attempt recorded.
- Privacy: no raw replay bytes, tokens, paths, or account IDs in records.
- Warnings are errors; new code is tested; architecture tests still pass.
