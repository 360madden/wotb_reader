# Session handoff — 2026-07-31: Offset discovery evidence reporting

**Status:** implementation and validation complete; ready for commit

## Repository state

- Branch: `main`
- Baseline commit: `52a3235` (`feat(tools): add offset discovery automation and playerYaw candidate evidence`)
- Existing unrelated untracked files were left untouched: `.freebuff/` and
  `research/reforged-ue5.md`.

## What changed

- `tools/discover-offsets.ps1` now accepts both Cheat Engine result formats:
  - `autoDiscover()` output under `fieldResults`;
  - legacy `saveDiscovered()` output with `fieldName` and `candidates`.
- Candidate offsets are normalized from either decimal or hexadecimal relative
  offsets. Unknown fields, invalid offsets, zero offsets, and offsets above the
  supported 2 GB range are ignored.
- A field is written to the versioned offset table only when exactly one valid
  candidate remains. Ambiguous or conflicting results are report-only and do
  not write offsets, hashes, timestamps, or existing evidence.
- Dynamic results are always recorded as `Candidate` with `DynamicScan`
  provenance. The pipeline never promotes a field to `Verified`.
- Existing table validation is fail-closed for version/hash mismatches,
  malformed or out-of-range offsets, incomplete `Verified` evidence, and invalid
  provenance/source-tool entries.
- Added `tools/report-offset-evidence.ps1`, a read-only report of executable-hash
  state, field status, offsets, provenance, and evidence counts.
- Updated `docs/operations/offset-discovery-guide.md` with the output contracts,
  conservative publication rule, report command, and runtime-promotion gate.

## Evidence state

`memory-offsets/11.19.0.10.json` still contains only the existing static-analysis
candidate for `playerYaw` (`0x0317A810`). Its executable hash remains empty until
computed from the installed binary. No live-game result was fabricated or
promoted during this session.

## Validation

- PowerShell AST parsing: `tools/discover-offsets.ps1` and
  `tools/report-offset-evidence.ps1` pass.
- Discovery self-test: `tools/discover-offsets.ps1 -SelfTest` passes CE shape,
  ambiguity, conflict preservation, evidence de-duplication, requested/table
  version checks, executable-hash checks, malformed-table checks, and legacy
  decimal-to-hex fallback.
- Report self-test: `tools/report-offset-evidence.ps1 -SelfTest` passes malformed
  offsets, malformed `fieldValidation.evidence`, malformed provenance/source-tool
  items, incomplete `Verified` evidence, required-field checks, and subprocess
  nonzero-exit validation.
- Read-only evidence report: `11.19.0.10` reports 0 verified, 1 candidate,
  and 7 unknown fields; malformed offsets are reported as `Invalid` instead of
  terminating the report, and malformed files cause a nonzero exit without
  modifying the table.
- Offset validator: `python scripts/python/offset_check.py --check-schema`; the
  only current issue is the intentionally empty executable hash in the
  11.19.0.10 table.
- Release build: `dotnet build WotBTreader.sln -c Release --no-restore` — 0
  warnings, 0 errors.
- Focused tests: Application — 14 passed; GameIntegration — 142 passed, 2
  opt-in skips.
- `git diff --check` passes.

## Assumptions and unknowns

- Cheat Engine's Lua output is treated as untrusted discovery evidence; unique
  does not mean verified.
- Candidate values remain unusable by runtime memory reads until the exact
  executable hash and all promotion evidence are present.
- No live WoT Blitz process was attached or modified by repository validation.
- Native scan cancellation remains covered by pre-cancellation and code-path
  checks; portable fake-platform cancellation tests remain deferred.

## Integration risks

- The PowerShell orchestrator still requires a human to run Cheat Engine's Lua
  function; CE does not provide a supported headless workflow in this repository.
- A unique candidate can still be a false positive; cross-process, cross-replay,
  x64dbg/static corroboration, and GameHarness invariants are mandatory.
- The offset validator intentionally reports a failure until a real executable
  SHA-256 is recorded; do not bypass this gate for convenience.

## Recommended next steps

1. Run `autoDiscover()` during an approved offline replay and preserve its JSON
   report outside tracked source paths.
2. Use `report-offset-evidence.ps1` before and after each evidence session.
3. Cross-check any unique candidate with x64dbg and GameHarness across two process
   launches and two independent replays.
4. Update the versioned offset table only with evidence-backed Candidate data;
   promote to Verified only when every reader requirement is satisfied.
5. Revisit the other seven fields only after the playerYaw workflow is proven.
