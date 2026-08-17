# Handoff — review: scan-authorization and observation lease lifecycle

Date: 2026-08-17 (UTC) · Status: Committed · Type: bug hunt + hardening

## What this session did

Follow-on to `2026-08-17-scan-lease-bughunt.md`: a focused review of the
coordinator's authorization/lease lifecycle — `GetScanAuthorization`,
`IsScanAuthorizationCurrent`, `IsObservationAuthorizationCurrent`,
`IsCaptureAuthorizationCurrent`, `RefreshVerifiedEvidence`, `Revoke`/
`RevokeSession`, `ExpireAuthorizationIfNeeded`, and the Generation / ReadGate /
`_authorizationCts` handoff. Every path was checked against the actual code.

## Bug found and fixed

1. **`IsObservationAuthorizationCurrent` used whole-record `ReferenceEquals`,
   so a benign liveness heartbeat flickered every overlapping observation
   poll to `Unknown`.** `RefreshVerifiedEvidence` (the ~500ms monitor
   heartbeat) keeps a verified session alive by replacing the
   `AuthorizedObservation` record via `with { ExpiresAtUtc = ... }` — same
   generation, same read gate, only the expiry extended. The observation
   path captured `auth` at the start of `ObserveAsync`, performed slow
   memory reads, then re-checked `ReferenceEquals(_authorization, auth)`.
   Because the heartbeat replaced the record mid-read, the check returned
   false and `ObserveAsync` downgraded an otherwise-valid observation to
   `Unknown`. The scan and capture paths already compared generation + read
   gate, so only the observation path was affected. The check now compares
   `Generation` and `ReadGate` reference, matching the other two paths; the
   heartbeat's expiry extension is no longer mistaken for revocation. One
   regression test was added and **proven to fail on the old code**
   (`Expected:<Available>. Actual:<Unknown>`).

## Rounds swept clean (no change)

- **R1 `GetScanAuthorization`:** captures `_authorization` and the CTS token
  under `_gate`, re-resolves the module base outside the lock, and fails the
  request closed on a transient zero base; re-grant and revocation are both
  caught downstream.
- **R2 `IsScanAuthorizationCurrent`:** generation + read-gate comparison plus
  `authorizationToken.ThrowIfCancellationRequested()` — revocation fails
  closed via cancellation, re-grant via generation mismatch.
- **R3 `IsCaptureAuthorizationCurrent`:** already generation + read-gate
  (launch reference + state + generation + gate) — correct.
- **R4 `RefreshVerifiedEvidence`:** identity drift is denied; pre-verification,
  superseded-launch, and cancelled-monitor heartbeats are all no-ops.
- **R5 `Revoke`/`RevokeSession`:** ReadGate revocation, `_authorization` null,
  CTS cancel, and scan-session discard happen together; the canceled CTS is
  intentionally retained (not disposed) to avoid a dispose/register race.
- **R6 `ExpireAuthorizationIfNeeded`:** revokes and marks `EvidenceStale`
  when the authorization expiry passes.

## Fail-open/fail-closed verdict

No fail-open path found. The one bug fixed was fail-closed in the wrong
direction (spurious `Unknown`), so it never risked leaking telemetry — it
degraded the overlay's availability during every heartbeat overlap instead.

## Validation

- `dotnet build WotBTreader.sln -c Release`: 0 warnings, 0 errors.
- `GameIntegration.Tests`: 378 passed, 6 opt-in skips (1 new regression test).
- Regression proven to fail against the old whole-record `ReferenceEquals`.
