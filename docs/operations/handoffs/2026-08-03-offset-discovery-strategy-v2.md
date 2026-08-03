# Handoff — offset-discovery strategy v2 + delta-compare + static root-finder (2026-08-03)

## Sessions worked

- Strategy reformulation: three unified tracks (offline static, hangar state,
  replay-marker live) — `docs/operations/offset-discovery-strategy-v2.md`.
- Delta-compare mode (engine → API → contracts) — the one real code gap that
  unblocks replay-marker live scanning.
- `tools/find-static-roots.py` — committed offline PE/RTTI analysis tool.
- Static verification runs against the hash-bound 11.19.0.10 binary (reproduced
  the community-root rules-out; xref discovery; RTTI back-door).

## What shipped

| Artifact | Purpose | Status |
|---|---|---|
| `docs/operations/offset-discovery-strategy-v2.md` | Unified three-track operating strategy | New |
| `tools/find-static-roots.py` | Offline root verification / xref discovery / RTTI walk | New, run-verified |
| `MemoryScanEngine.cs` | `delta` compare mode + `PassesDelta` + Bytes-kind rejection | Implemented |
| `GameSessionContracts.cs` | `CompareAsync` gains `deltaTarget`/`deltaTolerance` | Implemented |
| `GameSessionCoordinator.cs` | Delta pass-through | Implemented |
| `GameApiEndpoints.cs` | Delta endpoint validation + forwarding | Implemented |
| `OffsetDiscoveryContracts.cs` | `DeltaTarget`/`DeltaTolerance` on `OffsetCompareRequest` | Implemented |
| `GameApiEndpointsTests.cs` | 4 new delta endpoint tests + stub update | Passing |
| `UltimateScannerUnitTests.cs` | 6 new engine delta tests | Passing |

## Evidence produced (static, no game process)

- Community root `0x03E91978`: **not a root** in 11.19.0.10 — string bytes, not
  a reloc target, **0 .text references** (now the tool's `--chain` verdict).
- Xref discovery: 2,398 store slots / 5,661 load targets; 163 candidates ≥3
  refs; top zero-initialized RMW slots `0x03FA0C74` / `0x03FA012C` are
  runtime-written singleton-shape candidates.
- RTTI back-door: `AvatarContextBattle` mangled name `.?AVAvatarContextBattle@@`
  at `0x03E7DF30` (td `0x03E7DF28`); **0 statically-reachable vtable roots**
  (honest negative — class likely has no emitted vtable/COL in this build).
  Bonus: embedded source path `C:/ba/tc/work/t/client/Classes/Battle/
  AvatarContextBattle.cpp` at `0x0327735A` — a Ghidra anchor.
- Hot-slot false positive confirmed: `0x03FBCC74` (98K refs) = MSVC `/GS`
  `__security_cookie` — filter class documented in the tool/strategy.

## Validation

- `dotnet build WotBTreader.sln -c Release`: 0 warnings, 0 errors.
- `UltimateScannerUnitTests`: 47/47 pass. `Host.Web` compare filter: 6/6 pass.
- Architecture tests: 19/19 pass. GameIntegration full: 227 pass, 2 opt-in skips.
- `offline_check.py`: 22 files, 85 links, 0 broken; BLK-0001..0025 contiguous;
  ledger 25 result sections / 39 index rows.
- Tool runs: `--chain`, `--xref-data`, `--rtti` all execute clean with file logs.

## Decisions & review findings addressed

- **Build vs. third-party:** static analysis built in-repo (stdlib Python, no
  new deps); x64dbg stays the only sanctioned external for write-capture;
  `Ghidra-Cpp-Class-Analyzer` deferred (Python RTTI walk covers the offline
  ground first). Full matrix in the strategy doc §7.
- **Code-review fixes applied:** mangled-name filtering before TypeDescriptor
  interpretation; proper COL hop (`vtable[-1] → COL`, `COL+12 → pTypeDescriptor`);
  delta-on-Bytes snapshot rejected at compare time instead of silent 0.

## Next steps (ranked)

1. **Track C2 pilot:** run `delta` compare live with a replay-derived position
   delta; measure survivor collapse vs `increased`.
2. **Track C3/C4:** X/Y/Z value-equality intersection at synchronized replay
   time; target ≤2–4 survivors for interactive Find-what-writes.
3. **Track B pilot:** hangar-state known-truth scan (HP number, tank name).
4. **Track A:** RTTI walk for `EntityList`/`VehicleGameLogic`/`Vehicle`;
   xref the `AvatarContextBattle.cpp` source-path anchor.
5. `tools/find-static-roots.py --rtti EntityList` and friends on the next static
   pass; add a `--chain` batch mode for the candidate list from `--xref-data`.

## Open items

- `independentReplays` still 0 (BLK-0019) — no content-distinct second replay.
- Reforged/UE5 migration (announced 2026-06-17, postponed) — DAVA offsets are
  time-limited; strategy doc §9 tracks the risk.
- Rolling driver (`roll-replay-time-increased.ps1`) still uses `increased`; a
  `-DeltaTarget`/`-DeltaTolerance` pass-through is a small follow-up.
