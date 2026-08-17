# Handoff — 15-round bug hunt: penetration v0.3 capture surface

Date: 2026-08-17 (UTC) · Status: Committed · Type: bug hunt + hardening

## What this session did

A 15-round deep-analysis bug hunt across the penetration v0.3 capture seam
added over the last commits: the pure evaluator, the application DTOs, the
owner-census evidence source, the coordinator capture path, the
`PenOwnershipWalk` entity-region anchor, the endpoint, the capture scripts,
the wire contracts, DI registration, and the security/privacy of the read
path. Each round was verified against the actual code, not just reasoned.

## Bugs found and fixed

1. **`GameSessionCoordinator.RunOwnershipWalkPassAsync` — gun/entity pointer
   read failure misclassified as `Mismatch` (fail-closed honesty bug).**
   Steps 1 and 2 (owner, forward round-trip) returned null on a guarded read
   that cannot complete, so the caller reported `ReadFailed`. Steps 3 and 4
   (gun pointer, entity pointer) silently set their boolean to `false` and
   continued, so an identical read failure on both passes produced
   `PenOwnershipWalkMismatch` (wrong value) instead of `ReadFailed`
   (unreadable). Both steps now return null on a pointer-read miss, matching
   the documented "never fabricate a verdict" contract. Two regression tests
   were added and **proven to fail on the old code** (`Expected:<ReadFailed>.
   Actual:<PenOwnershipWalkMismatch>`).

2. **`scripts/capture-pen-census.ps1` — dead `NotReady` branch + misleading
   promotion message.** `PenetrationCaptureStatus` has no `NotReady` member,
   so the `-eq 'NotReady'` check was dead code; a `PositiveAwaitingRepeat`
   verdict fell through to `positive_verdict (record + promote per gates)`
   even though nothing is promotion-ready until a second content-distinct
   run. Replaced with an explicit three-way switch plus an unknown-status
   fallback.

## Rounds swept clean (no change)

- **R1 evaluator (Core):** gate/build/bounds checks and count-consistency
  invariants (`HasInvalidEvidenceCounts`) coherent; distinct-repeat path
  appends `RepeatNotContentDistinct` correctly.
- **R3 census source:** fail-closed on scan failure (never fabricates zero);
  `OwnerUnique` (`gun==1 && rotator==1`) is deliberately conservative against
  the evaluator's `OwnerCandidateCount==1` — the census stays an honest
  ambiguous-ownership negative by design.
- **R5 endpoint:** opaque decode-run id validated; response is aggregate-only
  (no address/pid/path/token/raw bytes).
- **R8 resolver:** additive `PenOwnershipWalk*` enum values only.
- **R9 wire contracts:** `PenetrationCaptureRequest`/`Response` privacy-safe.
- **R10 DI:** `TryAddSingleton` registration unambiguous.
- **R11 security/privacy:** loopback + capability gating, byte-exact AOB scan
  with no float tolerance (the 2026-08-10 Double-discriminator bug class does
  not apply), walk returns `RegionBase64 = null` (no raw bytes).
- **R12 test gaps:** the R4 gap was the only untested fail-closed branch;
  now covered.

## Validation

- `dotnet build WotBTreader.sln -c Release`: 0 warnings, 0 errors.
- `GameIntegration.Tests`: 376 passed, 6 opt-in skips (2 new regression tests).
- `Host.Web.Tests`: 190 passed, 1 opt-in skip.
- PSSA gate (`scripts/invoke-scriptanalyzer.ps1`): PASSED.
- `capture-pen-census.ps1` parses under Windows PowerShell 5.1.

## Residual risk

- The live shell/aim/ray phases remain unproven; the census source still
  returns an honest negative until `Gun`/`Shell` descriptor semantics and the
  runtime shell-index link are derived (see
  `2026-08-17-pen-gun-descriptor-trace.md`).
