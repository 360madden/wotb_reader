# Live-match gate — design (Phase 5, X1-unlocked)

**Date:** 2026-08-11
**Status:** DESIGN — no code. Unlocked by the X1 approval (Option A,
read-only live overlay, replay-proven fields only;
`docs/operations/x1-live-game-policy-memo.md`). This document designs HOW
the scanner path's `OfflineReplayVerified` requirement relaxes for a live
online match while preserving every fail-closed invariant. It authorizes
no live testing and changes no code.

## The problem

Every memory read today requires `GameSessionVerificationState.OfflineReplayVerified`:
the exact-build process identity (`GameProcessEvidence`: PID, start
identity, canonical path, product version, SHA-256, owned window) PLUS a
`ReplayLifecycleEvidence` `START_REPLAY_LOCAL` marker from the blitz native
log with live provenance and a monotonic cursor. A live online match has
**no replay lifecycle marker** — the game does not emit one, and
fabricating a replay marker to satisfy the replay gate would be exactly the
evidence fabrication the repo forbids.

## What the gate is actually for (honest decomposition)

| Today's gate input | What it proves | Needed in live mode? |
|---|---|---|
| Process identity (PID/start/path/version/SHA-256/window) | the reads hit the exact verified build's own process | **yes — unchanged** |
| Build identity (`layout` match) | the pinned offsets/chains apply | **yes — unchanged** |
| `START_REPLAY_LOCAL` lifecycle marker | the process is playing an offline replay | **no — there is no live equivalent to observe** |
| User context | — | **yes — the USER knows the match is live; the tool cannot and must not guess** |

The key insight: for a read-only overlay, the "is this a live match"
question is a **user assertion, not a tool discovery**. The field reads
(position/yaw/HP at the pinned offsets) are identical in replay playback
and live play — the tool does not need to distinguish them. What the gate
must keep proving is that the reads hit the exact verified build's own
process, under the user's explicit live-mode confirmation, on a read-only
surface. Nothing safety-critical is weakened.

## The design

### New state: `GameSessionVerificationState.LiveMatchVerified`

Additive enum member (never reuses `OfflineReplayVerified`; the two states
are mutually exclusive and a live session can never be mislabeled as a
replay session or vice versa).

### Evidence record

```csharp
internal sealed record LiveMatchAssertionEvidence(
    DateTimeOffset ConfirmedAtUtc,     // when the operator confirmed live mode
    int ProcessId,                     // must match GameProcessEvidence.ProcessId
    long ProcessStartIdentity,         // must match — no cross-launch reuse
    string LaunchCorrelation,          // ties the assertion to this process launch
    TimeSpan ValidityWindow);          // bounded, re-confirmed like evidence expiry
```

`GameProcessEvidence` is unchanged and still REQUIRED. The lifecycle marker
is replaced by the assertion — honestly, because no live marker exists. The
assertion is: (a) single-flight (only one live assertion at a time, tied to
one launch correlation), (b) time-bounded (same expiry/liveness heartbeat
as the replay gate — a stale assertion is `EvidenceStale`, never silently
kept), (c) fail-closed on ANY identity drift (PID/start identity change →
`Denied`, never a silent re-grant).

### Gate invariants (unchanged from the replay path)

- `GetScanAuthorization` requires the state + a fresh assertion + the exact
  build identity; the guarded reader lease, the module-base resolution, and
  the authorization expiry mechanics are byte-for-byte the replay path's.
- The `OfflineReplayVerified` requirement in every EXISTING surface
  (`/discover/*`, camera, batch, live-frame) is untouched — live mode is a
  NEW surface, never a relaxation of an old one. The replay gate stays
  strict so the offline sessions (OD-RECOVERY-086/087/088) are unaffected.
- Read-only: live mode reaches ONLY the read surfaces (positions, yaw, HP).
  The launch/input paths (managed launch, replay dialog clicking, allowlisted
  controls) are unreachable in `LiveMatchVerified` — the state machine
  routes them to `Denied` by construction.
- Replay-proven fields only: a field may be read live only after its live
  session passed (position: G0-proven; hull yaw: after L2; HP: after L1).
  The X4 honest-limits table is the authority for what live mode may read.

### New surface

`POST /api/v1/game/live/confirm` (or the existing session-state surface)
carries the operator assertion: verified process identity + explicit
live-mode confirmation + launch correlation. It is a mutation and sits
behind the loopback capability like every other write. The endpoint sets
`LiveMatchVerified`; a subsequent `/discover/live-frame` (or a dedicated
live variant) reads under that state. No launch, no input, no automation —
the overlay renders.

### Honest limits (recorded)

- The tool cannot detect "is this an online match" — the assertion is
  user-supplied. The overlay renders the same fields whether the process is
  in replay playback or a live match; the user's confirmation is the
  context.
- Anti-cheat risk is unchanged from the X1 memo: read-only observation of
  the user's own process, detectable in principle, enforcement posture
  unknown. The memo's Option A scope carries over verbatim.

## Sequencing (nothing here is live-gated yet)

1. **This design** (this doc) — approved for the design track by X1.
2. Implement `LiveMatchVerified` + assertion endpoint + state machine
   routing (pure offline code, unit-testable with the existing fakes).
3. Gated on: the 086/087/088 live sessions proving each field live (a
   field is only readable live after its replay-proven value passes its
   live session), AND a **separate operator approval** to run an actual
   online-match session (the X1 memo explicitly does not authorize live
   testing).
4. The item-7 hardware-atomicity proof stays LAST, untouched.

## Relationship to the roadmap

| Item | Status |
|---|---|
| X1 | ✅ APPROVED 2026-08-11 (Option A) — unlocks this design |
| X2/X2b/X4 | implemented offline; live sessions 086/087/088 pre-staged (their approvals are separate) |
| Live gate | THIS design; implementation + online-match session remain separately gated |
| X5 spotting | policy-gated, still out of scope |
