# Offset discovery walkthrough

End-to-end flow for discovering game memory offsets. Canonical detail:
[`docs/operations/offset-discovery-workflow.md`](../docs/operations/offset-discovery-workflow.md)
(timeboxed operating workflow),
[`docs/operations/offset-discovery-ledger.md`](../docs/operations/offset-discovery-ledger.md)
(append-only experiment tracking),
[`docs/operations/offset-discovery-guide.md`](../docs/operations/offset-discovery-guide.md)
(detailed tool reference), and
[`memory-offsets/README.md`](../memory-offsets/README.md) (evidence format).

## What offsets are

Versioned, module-relative offsets into the game process that let the reader
observe live replay state (replay time, player HP/position/yaw/pitch, alive
tank count). Evidence lives in `memory-offsets/<gameVersion>.json`, validated
against `memory-offsets/schema.json`. 8 fields, each with an expected type:

| Field | Type |
|-------|------|
| `replayTime` | double (seconds) |
| `playerHP` | int32 |
| `playerPositionX/Y/Z` | float (world units / height) |
| `playerYaw` | float (radians) |
| `cameraPitch` | float (radians) |
| `aliveTankCount` | int32 |

`0` = unknown. `confidence`: none/low/medium/high is a summary only; per-field
`fieldValidation.status` and its required evidence control promotion. The offset file is read by
`Application/Replay/OffsetTableReader.cs` and consumed in
`GameSessionCoordinator` (which refuses memory reads without a known,
version-matched offset table — `HasKnownOffsets`).

## Software flow (what the app does)

```
serve (web host on 127.0.0.1:9182, loopback)
  ├─ POST /api/v1/game/start        → GameProcessLauncher (plain wotblitz.exe launch, no replay)
  ├─ POST /api/v1/game/launch       → GameSessionCoordinator M2 suspended-process pipeline
  │    (prepare → executable lease → artifact staging → suspended process →
  │     correlation → resume) and lifecycle evidence → OfflineReplayVerified gate
  ├─ GET  /api/v1/game/state        → gate check (OfflineReplayVerified required for scans)
  ├─ POST /api/v1/game/discover                    → single known-value scan
  ├─ POST /api/v1/game/discover/pattern            → bounded AOB/wildcard scan
  ├─ POST /api/v1/game/discover/pointer-chain      → bounded pointer-chain evidence
  ├─ POST /api/v1/game/discover/snapshot           → snapshot all committed memory
  ├─ POST /api/v1/game/discover/compare/{sessionId} → compare vs snapshot (changed/unchanged/increased/decreased)
  ├─ POST /api/v1/game/discover/neighborhood       → scan a window around a known offset
  └─ DELETE /api/v1/game/discover/session/{sessionId} → discard a snapshot session
```

- Scanner: `ultimate-scanner/MemoryScanEngine.cs` (snapshot/compare) +
  `MemoryScanDiscoverer.cs` (pattern scans, neighborhood scans), surfaced
  through `IGameMemoryScanner` on `GameSessionCoordinator` (which stays in
  `GameIntegration` and delegates into the standalone `UltimateScanner` module).
- The 64-bit scanner host accepts native x64 and WOW64 x86 targets after exact
  architecture measurement; target address bounds and pointer width are carried
  by the guarded process lease. Snapshot address filters are revalidated against
  the measured target bound after the lease opens.
- A newly enumerated native-log source can authorize only when its file creation
  time and parsed marker timestamp are both at or after the last completed
  healthy reconciliation. A healthy zero-source baseline is safe because that
  completed-time anchor is retained; stale prepopulated files remain historical.
- Persistent scanner diagnostics contain aggregate counts and non-sensitive
  identity metadata only, never caller labels, expected bytes, memory addresses,
  decoded values, or observed process-memory bytes.
- **Safety gate:** every discover command requires an `OfflineReplayVerified`
  session (`GET /api/v1/game/state`); never scan an online match.

## GameHarness CLI (tools/src/WotBTreader.GameHarness)

All commands discover the host and its short-lived capability via the rendezvous
file, send `X-WotBTreader-Capability` on unsafe requests, and check the gate:

| Command | What it does |
|---------|--------------|
| `start` / `start-game` | POST `/api/v1/game/start` — plain game launch (no replay) |
| `state` | Show saved scanner state (read-only, local `ScannerStateStore`) |
| `scan` | Gate check + offset field status (X/Y fields known) |
| `probe` | Gate check + field status + raw offset table |
| `discover <field> <Float\|Int32\|Double> <value> [tolerance]` | Known-value scan with numeric Float tolerance |
| `discover-pattern <field> <patternHex> [mask]` | Bounded AOB/wildcard scan; API `includeImageRegions` adds private/mapped+image, `imageRegionsOnly` restricts to MEM_IMAGE when both are true |
| `discover-pointer-chain <rootOffset> <offset1,offset2,...>` | Bounded pointer-chain evidence probe |
| `discover-snapshot 4 [--float-min/--float-max/--int-min/--int-max] [--max-bytes <n>]` | Snapshot of committed memory; prints session id. Float bounds select `valueKind=Float` (alignment 4); otherwise Int32. `--max-bytes` sets an explicit retained-byte budget (0 = engine ceiling of 512 MiB); the engine soft-caps and returns a partial snapshot when the budget fills instead of failing with `size_limit` |
| `discover-compare <sessionId> [changed\|unchanged\|increased\|decreased]` | Compare current memory vs snapshot |
| `discover-nearby <refOffset> [--window <bytes>]` | Neighborhood scan around a known offset |
| `discover-discard <sessionId>` | Discard a snapshot session |
| `discover-campaign [--comparisons/--interval-seconds/--span-mib/--float-min/--float-max/--mode/--max-bytes]` | Bounded rolling Float32 reconnaissance; prints aggregate counts only and discards its private scanner session. `--max-bytes` (0–512 MiB) caps retained readable memory without address windows |

Run it like: `dotnet run --project tools/src/WotBTreader.GameHarness -c Release -- discover playerPositionX Float 42.5 1.0`
(from a directory with a `memory-offsets/` folder for the offset-status commands). Use a
controlled movement transition and record the session before treating any result as a
candidate. `discover-campaign` is reconnaissance only: repeated natural replay
changes do not identify a field or satisfy the controlled-transition,
address-classification, two-launch, or two-replay promotion requirements.

Snapshot requests also accept a `maxBytes` field on `POST
/api/v1/game/discover/snapshot`; values above the 512 MiB engine ceiling are
rejected at validation and never widen the retained-data bound.

For an operator-controlled Cheat Engine transition, the loopback web host alone
accepts the explicit `Research:OfflineReplayEvidenceLifetimeSeconds` setting.
The normal value is 15 seconds; validation accepts 5–120 seconds. This changes
only the maximum age of correlated replay-start evidence while the lifecycle
monitor is healthy. Replay stop, feed failure/gap, process exit, identity
change, cancellation, and expiry still fail closed through monitor revocation,
reported evidence, or per-read identity revalidation. The guarded input adapter
is not registered, so the operator—not automation—must perform the replay
transition. See `docs/operations/offset-discovery-workflow.md` for the full
abort and privacy protocol.

Replay startup that requires an operator's **Watch Offline** confirmation may
also opt into `Research:LifecycleEvidenceTimeoutSeconds`. The normal startup
wait remains 45 seconds; validation accepts 5–300 seconds. This setting only
extends the bounded wait for a fresh correlated replay-start marker and never
authorizes a scanner before that marker exists.

The preferred privacy-safe controlled-transition path uses the guarded
snapshot/compare endpoints with Float32, alignment 4, readable private/mapped
regions, a finite value range, and the engine's 512 MiB snapshot ceiling. The
operator pauses state A, briefly resumes movement, and pauses state B. Request
at most one comparison candidate, discard the response candidate without
rendering it, retain aggregate counts only, use a rolling baseline, and always
discard the scanner session. Cheat Engine is optional local structural
follow-up and never replaces the loopback offline gate.

## M2 write-site evidence (strategy v4 tail)

Primary path after M1 `evidence-strong` / solo-family arm. Full milestones:
[`docs/operations/offset-discovery-roadmap.md`](../docs/operations/offset-discovery-roadmap.md),
choreography:
[`docs/operations/offset-discovery-m1-m2-choreography.md`](../docs/operations/offset-discovery-m1-m2-choreography.md).

| Piece | Role |
|-------|------|
| `tools/WriteInterceptor` | x86 helper: PAGE_GUARD + debug attach; records RIP, RVA, registers, instruction bytes, attach-time `modules[]` |
| `scripts/invoke-csharp-write-trace.ps1` | Arms family members, runs interceptor, writes durable `ResultPath.capture.json` + `.family.json` (`modules`, `writeSites`, member `rvas`) |
| `src/WotBTreader.Core/Discovery/WriteSiteAnalysis.cs` | Pure offline: RIP→module+RVA, object-base candidates, sibling-read plan, resolver kind (unknown stays unknown) |
| `tmpwotb-e2e/test-guard-interceptor.ps1` / `test-csharp-write-trace.ps1` | Offline mechanism + wrapper e2e (no game) |

**Flow:** `od-049-autoloop` / `od-048` auto-trace → interceptor → **keep both**
`.capture.json` and `.family.json` under local `.data` (never commit) → optional
`WriteSiteAnalysis` on the capture for classification → Ghidra/static on RVAs
when module ownership is known → sibling `POST /api/v1/game/discover/read`
only under `OfflineReplayVerified`.

**Do not:** reopen x64dbg write-BP; invent image bases for absolute RIPs without
a module map; promote to `memory-offsets/` before M3 repeatability.

**FRESH36 lesson:** first live hit report proved the mechanism; module map was
ephemeral. Always republish the interceptor before the next live round.
Spec: [`docs/superpowers/specs/2026-08-06-guard-page-write-interceptor.md`](../docs/superpowers/specs/2026-08-06-guard-page-write-interceptor.md).

**Current M3 state (FRESH44/FRESH45):** the viewpoint-position correlation repeated on
a second independent replay (`0.9375`, with durable sampled series), so
cross-battle correlation repeatability is satisfied and BLK-0019 is resolved.
The 25-second trace stayed live but captured zero writes. The matching addresses
remain transient heap copies, not a stable module RVA or pointer chain; no
offset is promoted. FRESH45 tested four candidate-derived
`address-0x1C` base hypotheses with one immediate 12-float batch read. Every
read succeeded, but no complete XYZ triple matched decoded ground truth; the
completion gap was 102.2 ms against a 100 ms target. This rejects only those
four proposed contiguous layouts at that sampled instant. It does not refute
the static transform layout because the object base, atomicity, and same-clock
identity remain unproven. Do not repeat either the delayed trace or FRESH45
unchanged. The next live round requires a synthetically validated,
provenance-changing capture of the actual object pointer from the known
game-code transform-fill instruction/register path. See the
[`FRESH45 handoff`](../docs/operations/handoffs/2026-08-08-fresh45-immediate-triple.md).

## Instruction-first position pivot (OD-RECOVERY-062 through 065)

Scan-first discovery is closed for the next player-position proof. The new
path starts from the already evidenced game-code instruction and captures the
object pointer before reading members:

1. `GameSessionCoordinator` admits the operation only while the exact managed
   child remains `OfflineReplayVerified`; authorization generation and
   cancellation stay live for the entire capture.
2. The target policy is fixed to executable version/hash `11.19.0.10`, module
   `wotblitz.exe`, RVA `0x7C39AB`, bytes `8B83A0000000`, and register EBX. The
   next single read is fixed at EBX+`0x90` (the composed world-matrix
   translation row; X/Y/Z at +0/+4/+8).
3. A separate x86 helper receives the sensitive plan through inherited pipes.
   It contains no legacy raw-PID mode, hard-pins the game target, and verifies
   its actual parent against build-pinned Host.Web EXE+DLL hashes before target
   access.
4. A hardware execute breakpoint captures registers and the contiguous read
   while the matching debug event holds the process. The helper preserves
   unrelated debug-register state, arms new threads, caps the run at 5 seconds,
   64 accepted hits, 256 threads, and 64 KiB, then restores and detaches.
5. Host output projects heap addresses to local `object-NN` keys, which lets a
   later correlation group short XYZ trajectories without exposing addresses.

Synthetic x86 validation proves exact instruction hits, changing finite XYZ,
max-hit cleanup, timeout cleanup, raw-PID/legacy-mode rejection, and non-pinned
parent rejection. Live evidence then corrected two assumptions: the first game
had 164 threads, requiring a still-bounded 256-thread cap, and seven
EBX+`0x1C` hits were exactly `(1,1,1)`, proving that triple is scale.
Hash-verified `FUN_00d1a0f0` copies EBX+`0x10/+0x14/+0x18` into local-matrix
translation; a seven-hit live capture there changed but did not exactly match
any decoded participant under all axis/sign conventions. The best
time-agnostic viewpoint fit was mean 7.374 / max 10.272 units, so viewpoint
identity remains unknown. Because `FUN_00bc3940` copies the composed matrix to
EBX+`0x60`, the next provenance-bearing hypothesis is the world translation at
EBX+`0x90/+0x94/+0x98`. Capture UTC is now printed for clock alignment. Stop
after one such capture; do not fall back to candidate scanning. See the
[`live correction handoff`](../docs/operations/handoffs/2026-08-08-instruction-snapshot-live-correction.md).

## Evidence publication

1. Discover candidate offsets (Ghidra `FindOffsets.py`/`.java`,
   `tools/cheat-engine/*.lua`, or the scanner flow above).
2. Update `memory-offsets/<gameVersion>.json` — all 8 fields, set
   `confidence`, `executableSha256` (SHA-256 of wotblitz.exe for exact
   matching), `discoveredAtUtc`, `notes`.
3. Normalize and publish conservatively with `tools/discover-offsets.ps1`.
   It accepts both `autoDiscover()` (`fieldResults`) and legacy
   `saveDiscovered()` (`fieldName` + `candidates`) output. Only exactly one
   candidate with consistent decimal/hex forms, exact module identity, and a
   module-relative address is written, always as `Candidate`; ambiguous, heap-only,
   stale, or legacy-unclassified results remain report-only. Use
   `tools/report-offset-evidence.ps1` for a read-only status summary.
4. Validate: `scripts/python/offset_check.py` checks schema compliance
   (format, sha256, filename↔gameVersion match, plausibility, confidence).
   `memory-offsets/scanner-state.json` is generated runtime state — never commit.
5. Verify evidence without promoting it: run
   `tools/report-offset-evidence.ps1 -GameVersion 11.19.0.10` and
   `python scripts/python/offset_check.py --check-schema`. Candidate values are
   not runtime-supported; the memory API remains unknown until exact executable
   identity and complete per-field promotion evidence are present.

Current state (2026-07-31): the installed game is 11.19.0.10 and its executable
hash is recorded as
`1cda5c31919c9784a41bee7f3270ec1b4536b124c51e8b36f2221b381760307d`. The prior
static-analysis `playerYaw` hypothesis is now `Stale` with a published value of
`0`; 7 fields remain unknown and no field is runtime-supported. Full tool status in
[`docs/operations/offset-discovery-guide.md`](../docs/operations/offset-discovery-guide.md).

## Hard rules

- **Offline only.** Gate must be `OfflineReplayVerified`; never during an
  online match.
- Cheat Engine 7.7 is an approved local diagnostic tool for offline replay
  sessions only.
- Never commit scan files, memory dumps, pointer maps, or game-derived data.
- `memory-offsets/` evidence files are committed; `scanner-state.json` is not.
