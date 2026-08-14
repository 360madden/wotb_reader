# Item-7 Branch-B camera double-read instrumentation — ready for live measurement

**Date:** 2026-08-14 (UTC)

**Roadmap:** resolver consolidation item 7, Branch B step 4

**Base commit:** `ed02e74` (`test(od): pin completion check before replay probe`)

## Result

The camera read discipline was already implemented by CAM-005, despite the
Item-7 plan's stale statement that the read itself remained: the coordinator
reads the complete pose region twice, requires byte-for-byte equality, fails
closed at `pose-double-read`, and reports `ConsistentDoubleRead: true` only
for a matching pair. Existing coordinator tests pin both the resolved witness
and exactly two region reads.

The remaining offline gap is now closed. `invoke-camera-state-verify.ps1`
calls the sanctioned `/api/v1/game/discover/camera-pose` endpoint once per
round and adds an independent Branch-B witness without changing the CAM-001
v7 schema or its established camera verdict. Per-round evidence is an
explicit whitelist: status, failure stage, three identity gates,
`moduleRooted`, and `ConsistentDoubleRead`. Endpoint process addresses and
its duplicate pose coordinates/basis are never copied.

The aggregate reports planned/completed probes, resolved probes, identity-
verified probes, consistent pairs, `pose-double-read` failures, and
`allResolvedConsistent`. Acceptance requires the full planned schedule,
every probe resolved and identity/module-root verified, every pair stable,
and zero observed pose mismatches. A pure PowerShell helper and four synthetic
Pester cases pin positive, torn, privacy-whitelist, and incomplete-schedule
behavior; the suite is part of `scripts/validate.ps1`.

## Scope boundary

- No process addresses, raw bytes, capabilities, or new coordinate copies are
  persisted.
- No runtime read offset, resolver path, API/DTO, or camera verdict changed.
- `HardwareAtomicReadProven` remains false.
- The shared batch `ConsistentDoubleRead` proposal remains unapproved and
  unapplied.

## Validation

- Camera double-read Pester suite: 4/4 passed under Windows PowerShell 5.1.
- PSScriptAnalyzer repository gate: passed (99 non-fatal warnings reported).
- PowerShell parse check and `git diff --check`: passed.
- Full `scripts/validate.ps1`: 1,205 tests passed with 7 local opt-in skips;
  Release build 0 warnings/0 errors; repository scan, PSScriptAnalyzer,
  Pester 8/8 + 16/16 + 4/4, offline pack, blocker/ledger consistency, and
  eight-chain schema validation passed.

## Next

Run top-10 actions 1–4 in the approved clustered replay launch. For this lane,
retain the CAM aggregate only if every scheduled camera-pose probe satisfies
`allResolvedConsistent`; any `pose-double-read` is an honest negative and must
remain visible. After the live evidence, present the existing shared-contract
proposal for explicit owner review before changing batch flags.
