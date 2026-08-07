# OD M2 offline — durable write-site evidence + pure analysis (2026-08-07)

**Session outcome:** closed the FRESH36 evidence-durability gap offline. The
interceptor already resolved RVA/registers at hit time but the pipeline
discarded them; FRESH37 will now keep modules, RVA, instruction bytes, and
write-site aggregates. Pure Core analysis ranks object-base candidates and
sibling-read plans without a live process.

## Repository state

- Branch `main` (worktree dirty until committed).
- No live game session this unit.

## What changed

| Area | Change |
|---|---|
| `tools/WriteInterceptor/Interceptor.cs` | Attach-time module snapshot in report (`modules[]` basename-only); per-hit `instructionHex` (≤16 bytes at RIP); diagnostics for module count / instruction RPM failure |
| `scripts/invoke-csharp-write-trace.ps1` | Copy TEMP capture → `ResultPath.capture.json`; promote `modules`, `writeSites`, member `rvas`, `capturePath` into `.family.json` (additive) |
| `src/WotBTreader.Core/Discovery/WriteSiteAnalysis.cs` | Pure RIP→module+RVA, instruction hint, object-base ranking, sibling plan, resolver classification |
| `tests/WotBTreader.Core.Tests/WriteSiteAnalysisTests.cs` | 7 unit tests |
| `tmpwotb-e2e/test-guard-interceptor.ps1` | Assert modules non-empty + instructionHex present |
| `tmpwotb-e2e/test-csharp-write-trace.ps1` | Assert durable capture + family modules/writeSites |
| Roadmap + interceptor spec | Document durability + FRESH37 checklist |

## Tests and validation (offline)

- `dotnet test tests/WotBTreader.Core.Tests -c Release --filter FullyQualifiedName~WriteSiteAnalysis` → **7/7 pass**
- `dotnet publish tools/WriteInterceptor -c Release -r win-x86 --self-contained true -o .build/publish/write-interceptor` → ok
- `tmpwotb-e2e/test-guard-interceptor.ps1` → PASS (modules=30, hits, instructionHex)
- `tmpwotb-e2e/test-csharp-write-trace.ps1` → PASS (durable capture, family modules/writeSites)

## Assumptions and unknowns

- FRESH36 absolute RIPs **cannot** be module-mapped offline (no durable module
  map survived). Treat FRESH36 as mechanism proof only.
- Object-base consensus requires registers at hit time; when registers fail,
  kind stays `heap-dynamic` / `unknown` (fail-closed).
- Instruction hints are evidence-only; never sole promotion evidence.
- Core analysis is unit-tested; live family merge currently aggregates write
  sites in the wrapper. Re-run Core analysis offline on a durable capture when
  wiring a CLI helper later.

## Integration risks

- Forgetting to **republish** the interceptor before FRESH37 reverts to the
  pre-modules binary (stale publish class).
- `.family.json` shape is additive; greps for existing fields remain valid.
- Full `validate.ps1` not run this unit (focused offline OD work); run before
  milestone commit. Refresh `offline/file-tree.md` if committing new paths.

## Recommended next steps (FRESH37 live)

1. Republish interceptor + fresh Host Release build; kill stale host.
2. `powershell -File tmpwotb-e2e\od-049-autoloop.ps1 -AttachSmokeOnFirstRound -StageViewpointOnly -PlaybackSpeedEstimate 2.4`
3. Success: `family-hit` + durable `.capture.json` with modules covering the
   write-site RIPs (or honest `jit`) + `.family.json` `writeSites`/`rvas`.
4. Offline after hit: feed RVAs into Ghidra / `find-static-roots.py`; run
   sibling `discover/read` if battle tail remains; classify — do not publish
   offsets until M3 repeatability.
