# Project knowledge

WotB Treader is a **Windows-first offline replay telemetry reader** for World of Tanks Blitz. It parses replay evidence, stores versioned telemetry projections, and presents a local Blazor dashboard + WPF overlay with SignalR push-based updates.

The project owner identifies as a junior developer at Wargaming.net. This is
user-provided background for a personal, independently maintained project;
see [Project context](docs/project-context.md).

The overlay is a **transparent heads-up display (HUD)** designed to sit on top
of the WoT Blitz game while it plays back a pre-recorded replay. It shows
decoded position plots and telemetry that the game's built-in viewer does not
expose. See `docs/architecture/overview.md#overlay--hud-design-intent` for the
full design specification.

- **Stack:** .NET 10 (C#), WPF, ASP.NET Core Blazor Web App, SQLite, SignalR
- The overlay is a loopback client only. It hosts no HTTP control plane; the legacy
  embedded Kestrel listener on port 9190 was removed, and the old endpoint/state files
  were deleted. `OverlayControlPlaneContainmentTests` keeps the second control plane out.
- **No runtime dependency on:** Python, Node.js, Rust, Electron, containers, cloud
  services, runtime AI, or dynamic decoder DLLs. Python remains available only for
  offline validation and test tooling. The Rust `wotbreplay-inspector` oracle is a
  hash-pinned dev-time artifact (see below) — never shipped, never loaded by product
  code.
- **Replay-decode cross-validation:** `scripts/invoke-replay-crosscheck.ps1` runs the
  C# `Replays` decoder and the independent Rust `wotbreplay-inspector` (built on
  `wotbreplay-parser` 0.4.2) on the same `.wotbreplay` and compares battle timestamp,
  participants, and packet clocks; `-GoldenVector` validates the oracle against the
  parser's published fixtures. Known documented divergences: bot-account sentinels
  (Rust uint32-truncates, C# rejects sign-extended IDs) and battle-time source
  (client `meta.json` vs server protobuf tag 2). See `tools/external/README.md`.
- **Current offset evidence:** `11.19.0.10` is hash-bound to
  `1cda5c31919c9784a41bee7f3270ec1b4536b124c51e8b36f2221b381760307d`;
  the `playerYaw` hypothesis is quarantined/Stale because its representations
  conflict; seven fields remain unknown and no field is runtime-supported.
- **Replay-start flake (OD-044) fixed:** the ~50% launch deaths were two defects —
  a watch_offline round-2 double-click + SW_RESTORE churn into the live replay HUD
  (become hidden → OnBackground), and mid-battle `OfflineReplayEvidenceLifetime`
  expiry terminating the managed game. The click script stops on the blitz-log
  `Start replay event` marker (round 1 still dismisses a visible dialog), and the
  coordinator refreshes verified authorization via a liveness heartbeat while the
  process identity stays healthy. See
  [`docs/operations/handoffs/2026-08-04-replay-start-flake-fix.md`](docs/operations/handoffs/2026-08-04-replay-start-flake-fix.md).
- **The game AUTO-LOOPS the offline replay (2026-08-05, blitz-log evidence):**
  after a battle ends (`onLeaveWorld`), the Watch-Offline viewer starts the SAME
  replay again (`Start replay event` fires again) with only ~10s between battles
  — one game launch yields repeated battle windows with no operator input. Each
  battle boundary revokes the offline-session gate (reads then fail closed with
  400 `discover.gate_not_satisfied`), so memory reads must be anchored inside a
  single battle, and staging scans (~65s for 9 axis scans) must not span a
  boundary. M1 can be re-run per loop iteration without relaunching the game.
- **Type-10 (`0x0A`) position packet layout is verified end-to-end** against the
  decoded DB ground truth (33 281 packets, all 49 bytes, byte-identical floats):
  `entity int32 | space int32 | vehicle int32 | x f32 | y f32 | z f32 | 12B zeros |
  vx f32 | vy f32 | vz f32 | flags byte`. Every participant's full position history
  (19 entities: 14 named + 5 effect/shell) is present from `LoadGameScene` to
  `onLeaveWorld`. The velocity triple is physics velocity (eases after stops), NOT
  the position derivative. No yaw/pitch field exists in any packet type — the yaw
  hypothesis stays quarantined. See `offline/replay-format.md`.
- **Discovery workflow:** use [`docs/operations/offset-discovery-workflow.md`](docs/operations/offset-discovery-workflow.md)
  and append every attempt to [`docs/operations/offset-discovery-ledger.md`](docs/operations/offset-discovery-ledger.md).
- **M2/M3 offset-discovery state (FRESH43-OD-RECOVERY-068):** the C# guard-page
  interceptor (`tools/WriteInterceptor`, x86 self-contained publish) is the
  live write-trace path; x64dbg write-BP is closed. FRESH43 captured and
  module-mapped the game transform fill path. The original static reading
  misidentified `[entity+0x3C]+0x1C/0x20/0x24` as position; live instruction
  evidence now proves that triple is scale. The proposed global root was later
  refuted. FRESH44 repeated the viewpoint-position
  correlation on a second independent replay (`0.9375`, with durable sampled
  series), resolving BLK-0019 and satisfying cross-battle correlation
  repeatability. Its bounded write trace was an honest zero-hit result. The
  matching addresses remain transient heap copies: no stable pointer chain,
  module-relative field, or same-clock live read of the position triple exists,
  so no offset is runtime-supported or ready for promotion. FRESH45 then tested
  the candidate-derived immediate-read hypothesis: all 12 floats for four
  proposed `candidate-0x1C` layouts were readable, but none matched the complete
  decoded XYZ triple (102.2 ms completion gap). That is an honest negative for
  those four layouts at that instant, not a refutation of the static transform
  layout. Do not repeat it for latency alone. The durable pivot is now
  **instruction-first**: the coordinator-authorized x86 helper sets an execute
  breakpoint on the exact hash-bound transform-fill instruction
  (`wotblitz.exe+0x7C39AB`, bytes `8B83A0000000`), captures EBX at the held
  debug event. The first live process had 164 threads, so the fail-closed
  coverage cap was raised from 128 to 256. Seven reads at
  EBX+0x1C/0x20/0x24 were exactly `(1,1,1)`, proving scale. Seven subsequent
  reads at EBX+0x10/0x14/0x18 produced a changing local translation but no
  exact decoded-participant match across all axis/sign conventions (best
  time-agnostic viewpoint fit: mean 7.374, max 10.272). Hash-verified
  `FUN_00d1a0f0` copies that local translation into matrix row `+0x30`, and
  `FUN_00bc3940` stores the composed world matrix at EBX+0x60. OD-RECOVERY-066
  then read its translation row at EBX+0x90/0x94/0x98 once: seven finite hits
  from one opaque object, with the exact fingerprint and cleanup proven. A
  clock-aligned search over every decoded participant, all 48 axis/sign
  mappings, playback speeds 0.5x through 8x, and the scene-marker uncertainty
  found no identity: the best coherent absolute fit missed by mean 10.850 and
  max 12.556 units, with 0/7 samples within 1 unit. A free constant-offset fit
  required a 250.832-unit origin shift and 6.26x playback, so it is not identity
  evidence. The matrix arithmetic is statically correct; the sampled render
  object or coordinate space is not the decoded player trajectory. This closes
  unchanged reads at the transform-fill site. Callers cannot
  supply a PID, address, module, register, or displacement; the helper is
  a separate no-legacy-mode binary, bounded to five seconds/64 accepted hits,
  and hard-pins both the game target and the exact Host.Web EXE+DLL parent.
  A controlled publish manifest plus fresh nonce response prevents a candidate
  helper from self-attesting its own identity.
  The post-attach process event is revalidated before thread arming; cleanup
  failure revokes the session and terminates the exact managed child even
  across a normal authorization refresh. Synthetic capture, max-hit cleanup,
  timeout cleanup, and non-pinned-parent rejection pass. The durable pivot is
  now offline/static-only: locate the hash-bound code path that consumes or
  applies the verified type-10 replay position packet, identify its entity and
  position-member provenance, and synthetically validate a bounded capture plan
  before requesting another live round. Do not widen the render-transform read
  or promote an offset. No viewpoint identity, stable root, or offset is claimed.
  OD-RECOVERY-067's first hash-bound static triage scanned 526,935 executable
  functions. Displacement-layout ranking produced thousands of noisy matrix,
  copy, serializer, and destructor matches; its highest-ranked function was
  manually refuted as local matrix/grid code. No function directly compared a
  common record base against both length `49` and type `10`. Eight nearby
  initialized-data `{10,49,code-pointer}` candidates were all MSVC exception
  metadata (`0x19930522`), not replay dispatch. Do not repeat literal/layout
  searches unchanged. OD-RECOVERY-068 then re-evaluated the repository's
  UnknownCheats-derived `VehicleGameLogic +0x04 -> entity +0x68/+0x6C/+0x70`
  family against the same exact executable. The claimed module root
  `0x03E91978` remains refuted. A full scan found 111,693 generic `+0x04`
  pointer loads, 68 structurally related candidates, and one complete chained
  triple; that sole exact match is a matrix/pose interpolation-copy function,
  not a `VehicleGameLogic` entity. The current `VehicleGameLogic` vtable is
  statically named at RVA `0x0327DA50`, and slot `+0x04` resolves to the real
  entity getter at RVA `0x0031B560` (`MOV EAX,[ECX+0x04]`). Across its 79
  virtual methods, 17 getter-using methods access 23 distinct entity members,
  but none accesses the claimed position triple. This makes the community
  layout a useful naming/relationship clue, not a live-read candidate. The
  OD-RECOVERY-069 completed that data-flow trace. The `ReplayPlayer`
  constructor installs type-10 handler RVA `0x00FE31C0`; it reads the exact
  `4,4,4,12,12,4,4,4,1` byte sequence and dispatches through
  `BlitzServerMessageHandler` into `BWEntities::handleEntityMoveWithError`.
  The resolver compares `[entity+0x1C]` with the packet entity ID, proving that
  member is the entity identifier. At entity-apply RVA `0x022FA780`, the
  instruction at RVA `0x022FA78D` (bytes `F30F7E00`) executes with `ESI` equal
  to the resolved entity and `EAX` pointing at the packet-derived XYZ vector.
  The downstream `BW::AvatarFilterHelper` stores that vector in an 8-entry,
  `0x38`-stride ring at helper-relative `+0x18`; this is a transient movement
  sample buffer, not a stable position member. The new candidate family is
  therefore **entity-bound instruction event**: one held debug event can read
  `[ESI+0x1C]` and the contiguous 12 bytes at `EAX`, then align the entity ID
  and XYZ with decoded type-10 ground truth. The hash-bound Ghidra verifier
  passes 40/40 checks. OD-RECOVERY-070 now implements the fixed two-source
  helper contract and passes its synthetic x86 proof: entity ID `4242`, four
  changing finite XYZ samples, exact target fingerprint, hit bound, parent
  rejection, cleanup, and detach all pass. The public response calls the value
  `replayEntityId` and suppresses process/entity/vector addresses. After the
  full repository gate and a fresh pinned publish, one bounded positively
  verified offline capture is recommended to test exact replay-entity/XYZ
  equality. No stable polling offset or player identity is yet claimed.
  Detail:
  [`offline/offset-discovery.md`](offline/offset-discovery.md) and
  [`docs/operations/handoffs/2026-08-08-type10-entity-movement-anchor.md`](docs/operations/handoffs/2026-08-08-type10-entity-movement-anchor.md).

## Quickstart

**Requirements:** Windows 10/11, .NET SDK 10.0.302

**Convenience wrappers (repo root .cmd files, run from any directory):**

| Wrapper | What it does |
|---------|-------------|
| `build` | Build the solution (Release) |
| `validate` | Full gate: restore → format → build → test → audit → scan |
| `test` | Run all tests (skip build) |
| `serve` | Publish + start web host at http://127.0.0.1:9182 |
| `everything` | One-shot: launch serve then overlay (the full HUD experience) |
| `overlay` | Launch the WPF overlay (needs web host running) |
| `import <file>` | Import a .wotbreplay file |
| `watch <dir>` | Watch directory and auto-import new replays |
| `sessions` | List decoded battle sessions (JSON) |
| `doctor` | Run environment health checks (JSON) |
| `compare list` | List comparison runs |
| `compare inspect <id>` | Inspect one comparison run |
| `compare create <leftId> <rightId>` | Create a comparison run from two decode runs |
| `export sessions <id>` | Export session events as JSON |
| `export positions <id>` | Export position samples as JSON |
| `crosscheck [-Replay <file> | -GoldenVector]` | **Operator-run** replay-decode cross-validation (C# vs Rust oracle); not part of validate/CI — needs real replays. See `docs/operations/replay-crosscheck.md` |
| `treader <cmd> [args]` | General CLI passthrough for any command |

All CLI wrappers store data under `.data\` in the repo root (gitignored).
Publish output goes to `.build\publish\` (also gitignored).

### Startup Sequence

The overlay (HUD) is a loopback web client — it has no data of its own.
It discovers the web host via a rendezvous file. The correct order is:

```
┌─────────────────────────────────────────────────────┐
│  1. import  a .wotbreplay  (one-time per replay)   │
│     or watch a folder to auto-import new replays    │
│                         ↓                           │
│  2. serve   start the web host (keep it running)   │
│                         ↓                           │
│  3. overlay  launch the HUD                        │
│     (or open http://127.0.0.1:9182 in a browser)   │
└─────────────────────────────────────────────────────┘
```

- **Step 1** decodes replays into a SQLite database under `.data\`.
- **Step 2** serves that database via REST + Blazor + SignalR at `127.0.0.1:9182`
  and writes a rendezvous file so the overlay can auto-discover it.
- **Step 3** launches the WPF overlay which finds the host, loads the session
  list, and plots position data.

You can re-run step 1 later (import more replays) while the host is running —
the overlay refreshes to show new sessions.

**One-command launch:** `everything.cmd` starts both `serve` and `overlay`
in separate windows with a short wait for the host to be ready.

**Full gate (run before milestone commits):**
```powershell
./scripts/validate.ps1                     # locked restore → format → build → test → scan → PS hygiene
./scripts/validate.ps1 -AuditPackages      # above + transitive vulnerability audit
```

**Decoder milestones additionally run the replay cross-check** (operator-run;
needs real replays, so it is deliberately not in validate/CI):
```powershell
./crosscheck.cmd -GoldenVector             # trust the oracle first
./crosscheck.cmd -Replay <real-replay>     # C# decoder vs Rust oracle on real 11.18/11.19 battles
```
See `docs/operations/replay-crosscheck.md` for exit codes and known divergences.

**Single test project:**
```powershell
dotnet test tests/WotBTreader.Core.Tests -c Release
dotnet test tests/WotBTreader.Core.Tests -c Release --filter "FullyQualifiedName~SomeTest"
```

- Tests are MSTest 4 on Microsoft.Testing.Platform. Some installed-game tests skip by default (local opt-in).
- 12 test projects, 412 tests: 410 passed, 0 failed, 2 opt-in skips (as of 2026-08-01).
- All architecture hardening milestones (M0–M7) are complete. The alpha release
  (`v0.1.0-alpha`) passed the full gate; later changes added offset evidence tooling
  and stricter cancellation/hash validation without promoting candidate offsets.

### Keyboard shortcuts

| Key | Action |
|-----|--------|
| Space | Play / Pause |
| ← | Scrub back 5 seconds |
| → | Scrub forward 5 seconds |
| 1 | Speed 0.5× |
| 2 | Speed 1× |
| 3 | Speed 2× |
| 4 | Speed 4× |
| 5 | Speed 8× |
| Esc | Close overlay |

## Architecture

```
Core (no project refs)
 └── Application → Core only
      ├── Replays → Application + Core      (replay parsing: .wotbreplay, pickle, protobuf)
      ├── CaptureLogs → Application + Core  (telemetry capture log reading)
      ├── GameIntegration → Application + Core (installed-game discovery, DVPL reading,
      │                                         offline session gate, guarded Win32, launch)
      ├── UltimateScanner → Application + Core (standalone guarded memory
      │                                         scan module: multi-scan snapshot/compare,
      │                                         pattern/neighborhood scans, guarded VM reads)
      │                     referenced ONLY by GameIntegration
      ├── Storage.Sqlite → Application + Core (SQLite storage)
      └── Bootstrap (composition root; all DI registration)
           ├── Host.Cli (net10.0 console)
           ├── Host.Web (net10.0 Blazor Web App, loopback-only, port 9182)
           └── (tools) GameHarness / ReplayInspector / ReplaySanitizer
                       resolve product ports through Bootstrap only

ApiContracts (net10.0; NO project refs, NO package refs — empty lock file)
 ├── ReadContracts.cs   (session/participant/position/event/comparison read shapes)
 ├── GameContracts.cs   (game status, launch, memory-observation shapes)
 └── HudContracts.cs    (HUD command/status shapes)
      ├── referenced by Host.Web  (serializes them on the wire)
      └── referenced by Overlay   (its ONLY project reference)

Overlay (net10.0-windows WPF, transparent HUD; loopback client only)
 ├── Discovery/RendezvousLocator     (finds host via owner-only rendezvous file)
 ├── Services/TreaderApiClient       (read API HTTP client)
 ├── Services/TelemetryStreamService (SignalR push client, auto-reconnect)
 ├── ViewModels/MainViewModel        (session list, positions, events, stats, playback)
 ├── Views/PositionPlot              (canvas scatter plot, velocity trails, minimap grid)
 └── MainWindow                      (transparent borderless topmost HUD, P/Invoke window tracking)
```

**Overlay control plane:** the old `Endpoints/OverlayApiEndpoints.cs` and
`Services/OverlayApiState.cs` implementation was removed. Nothing binds port 9190;
`Host.Web` is the only control plane. `OverlayControlPlaneContainmentTests` guards
this boundary.

**Key rules:**
- Adapters (Replays, CaptureLogs, GameIntegration, Storage.Sqlite) never reference each other
- `UltimateScanner` is a scanner-only module: it references `Application`/`Core` only, and only `GameIntegration` may reference it
- `Overlay` references only `ApiContracts` — never a host, adapter, `Application`, or `Core`
- `ApiContracts` is serialization-only: no domain behavior, no project refs, no package refs
- Hosts and tools compose exclusively through `Bootstrap`; tools do not build their own adapters
- Only the `Overlay` and `tools/GameHarness` production surfaces and their tests target `net10.0-windows`; everything else is portable `net10.0`
- New DI ports must be added to `CompositionRootTests` published-port list or no host starts
- Warnings are errors (`TreatWarningsAsErrors`), NuGet audit mode is `all` — fix with central pins, never suppress
- Package versions are centrally managed in `Directory.Packages.props` with committed lock files

`WotBTreader.Architecture.Tests` (16 tests) enforces the reference graph, the TFM
allowlist, and the native-access boundary — including the `UltimateScanner`
native-access allowlist (its VM-read interop is the only sanctioned surface).
Breaking any rule above fails the build. The scanner remains behind the offline
replay gate; candidate-only offset evidence cannot authorize runtime reads.

## Conventions

- **Testing:** MSTest 4, synthetic fixtures only in CI. Private replays/captures/DBs stay in gitignored paths.
- **Evidence-first:** unknown stays unknown. Reprocess = new immutable decode run. Pickle = data only; never execute opcodes.
- **Privacy:** never log raw replay bytes, tokens, full paths, account IDs, chat, or screenshots. Player names and bot status are public Wargaming statistics, not private.
- **Bot status:** may be inferred from a player name; `unknown` remains the no-evidence default.
- **Game automation:** developer-only, offline-replay-only, denied by default, fully audited.
- **Commits:** author as `Codex Agent <codex@local.invalid>` unless user says otherwise. Never force-push. Push only when asked.
- **Operations docs:** index and numbering convention in `docs/operations/README.md`.
- **Blockers:** append `docs/operations/blocker-log.md` (immutable UTC).
- **Handoffs:** append under `docs/operations/handoffs/` per format in the handoff README. Correct with amendments, never rewrite.

## Gotchas

- `.gitignore` patterns match **case-insensitively on Windows**. Runtime-data patterns (`*.sqlite`, `diagnostics/`, `dist/`) can hide real source folders. Add explicit `!` unignore rules when creating paths that collide with runtime-data patterns.
- In `validate.ps1`, route every native command through `Invoke-CheckedNative`; `$ErrorActionPreference='Stop'` does NOT catch non-zero exit codes.
- `NuGetAuditMode=all` fails restore on vulnerable transitive packages. Fix with a central version pin — never suppress.
- `scan-repository.ps1` checks for secrets (API keys, private keys, connection strings, absolute replay paths) and ignored files in source trees.
- SignalR callbacks run on non-UI threads without a `SynchronizationContext`. Any ObservableCollection mutations from SignalR callbacks must be marshalled via `SynchronizationContext.Post`.

- **cmd.exe wrapper scripts** have failure modes that survive casual review — delayed expansion corrupts `!` in filenames, unquoted `%~dp0` breaks on paths with spaces, whitespace input crashes arithmetic checks, and missing `setlocal` leaks env vars. See `docs/operations/cmd-wrapper-gotchas.md` for the full catalogue and review checklist. Always route cmd/batch reviews through a thinker agent.
- **PowerShell scripts must pass the PSScriptAnalyzer gate** (`scripts/invoke-scriptanalyzer.ps1`, wired into `validate.ps1` + CI). Pinned 1.25.0 installs via `scripts/install-psscriptanalyzer.ps1`; settings in `tools/psscriptanalyzer-settings.psd1`; repo custom rules (ban `[double]::IsFinite` and PS7-only operators `??`/`&&`/`||`) in `tools/psscriptanalyzer-custom-rules.psm1`. The 5.1 host reads **BOM-less UTF-8 as ANSI**: non-ASCII bytes can silently corrupt strings at runtime (an em-dash's trailing byte `0x94` parses as `"` and terminates the string — this actually broke `tools/compute-exe-hash.ps1`). Keep every `.ps1` ASCII-only, or the gate's `PSUseBOMForUnicodeEncodedFile` + runtime mojibake will bite. Also: custom script rules must be typed with a **concrete AST node** (e.g. `[ScriptBlockAst]`), never the abstract `[Ast]` — PSScriptAnalyzer matches rules to nodes by type-name substring and silently never invokes `[Ast]`-typed rules; `-CustomRulePath` **replaces** the default rule set unless `-IncludeDefaultRules` is passed.
- **Basher (terminal agent) timeouts are a recurring waste pattern.** Default 30s timeout is never enough for .NET commands. Use these timeouts: `dotnet build` → 300s, `dotnet test` (full suite) → 300s, `dotnet test` (single project) → 120s, `dotnet publish` → 180s. Never run interactive `.cmd` wrappers through basher — use direct `dotnet` commands. Verify prerequisites (CLI built, packages restored) before running dependent commands.
