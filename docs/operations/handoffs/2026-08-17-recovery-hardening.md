# Handoff: RECOVERY module hardening pass (2026-08-17)

**Date:** 2026-08-17 (UTC)
**Status:** Complete — triage exit-code contract fixed, build-drift reason
wired into the capture endpoint, playbook anchor inventory made concrete
**Campaign:** build-drift readiness (follow-up to `2026-08-17-recovery-module.md`)

## What was done

1. **Triage exit-code contract now honored end-to-end.** `RECOVERY/invoke-build-drift-triage.ps1`
   previously threw on a bad `-GameExePath` (exit 1) instead of the documented
   exit 2, and any unexpected failure (e.g. a missing `-OffsetDir`) leaked as
   exit 1 instead of 3. The executable-discovery path now exits 2 with an
   actionable message, and the table-read/verdict/report phases are wrapped so
   any failure exits 3 with the exception text. Verified matrix:
   same-build 0, drifted (notepad.exe) 1, bad path 2, bad offset dir 3.

2. **`capture.decode_build_mismatch` now points at the RECOVERY module.**
   The `POST /discover/pen-capture` endpoint previously returned only the bare
   error code (the coordinator's message never reached callers). When the
   coordinator rejects with `capture.decode_build_mismatch`, the endpoint now
   adds an additive `reason` field: `build drift: the game build changed -
   re-verify per RECOVERY/README.md`. Code unchanged (tests pin it); success
   response shape unchanged. New endpoint test
   `CapturePenetrationAsync_BuildMismatch_PointsAtRecoveryModule`.

3. **Playbook anchor inventory made concrete.** The Gun/Shell descriptor row
   in `RECOVERY/build-drift-recovery.md` now cites the exact RVAs
   (`Gun 0x31a7080`, `Shell 0x31a1e14`, `piercingPower` curve at `Gun +0x34`)
   and the correct handoff references (the proof-protocol doc does not list
   those RVAs; the layout handoff does). All other route-table anchors were
   cross-checked against `memory-offsets/11.19.0.10.json` chains and
   `GameSessionContracts.cs` constants — accurate (root `0x04095C88`,
   `vftableScan 0x032752A4`, rotation 48/44/40, HP 184, pen vftables
   `0x32dacf4/0x32eeb40/0x324dae8`).

## Validation

- Triage matrix: exit 0/1/2/3 all correct (above).
- `dotnet build WotBTreader.sln -c Release`: 0 warnings / 0 errors.
- `dotnet test tests/WotBTreader.Host.Web.Tests -c Release --no-build
  --filter FullyQualifiedName~CapturePenetration`: 5/5 passed.
- PSScriptAnalyzer gate: PASS (145 tracked .ps1 files).
- `offline_check.py`: links green, blocker numbering contiguous, ledger
  consistent (regenerated file-tree for the new handoff).

## Notes

- The triage script remains read-only and never mutates evidence; the fix is
  purely the exit-code contract and error hygiene.
- No new auto-recovery lane was added (still deliberately out of scope per
  the module README — the first real update decides whether it earns its
  cost).

## Next steps

- Drill the playbook end-to-end when the first real game update lands
  (unchanged from the module handoff).
- Otherwise treat `RECOVERY/` as the standing answer to "the game updated,
  what now?" — triage now reports cleanly in every failure mode.