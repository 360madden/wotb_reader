# Penetration v0.3 owner-census evidence source

**Date:** 2026-08-16 (UTC)
**Status:** exact-build owner-census source + capture endpoint implemented; the
live census capture itself remains the next owner-approved step
**Blocker:** `BLK-0027` (open — this is the first discriminator step, not a
resolution)

## What changed

The managed-offline capture contract's "smallest proven exact-build evidence
implementation" is now in place. The production `IPenetrationCaptureEvidenceSource`
is no longer the neutral stub; it performs the Phase-1 owner census through the
same gated vftable AOB scan the avatar/camera anchors already use.

### Static evidence (hash-bound)

Derived the three weapon-family vftable RVAs from their RTTI complete object
locators with the existing `FindVftableViaCol.java` tooling (executable
SHA-256 `1cda5c31…`, 11.19.0.10):

| Family | RTTI COL RVA | vftable RVA (object's first dword) |
|---|---|---|
| `VehicleGun` | `0x35ce9e0` | `0x32dacf4` |
| `VehicleGunRotator` | `0x35e06a8` | `0x32eeb40` |
| `AvatarGunAgent` | `0x35317c0` | `0x324dae8` |

Evidence: `.build/ghidra-evidence-vehiclegun/`, `-vehiclegunrotator/`, and
`-avatargunagent/find-vftable-via-col.txt` (each `vftables=1`).

### Source

`ExactBuildOwnerCensusCaptureEvidenceSource`
(`src/WotBTreader.GameIntegration/Session/PenetrationCaptureEvidenceSource.cs`)
now runs two census passes over Private|Mapped regions for each of the three
vftable image addresses (`moduleBase + rva`, 4-byte LE exact match, alignment 4,
max 64 candidates) and reports only privacy-safe counts. It reports every live
instance it cannot exclude as the viewpoint owner:

- `OwnerCandidateCount` = VehicleGun + VehicleGunRotator instances (the
  contract's two owner candidates);
- `OwnerUnique` = exactly one of each;
- `OwnerStable` = identical counts across the two passes;
- `AvatarGunAgent` is census context only (a bridge candidate, never required).

The shell/aim/ray phases remain honestly unproven, so the pure evaluator keeps
every real capture `NotReady` until those fields are proven. No address, id,
path, token, or raw observation leaves the source; the one log line carries
only family counts and the stability flag.

### Trigger

`POST /api/v1/game/discover/pen-capture` (`CapturePenetrationAsync`) accepts only
an opaque `{ "decodeRunId": "<guid>" }` and returns the privacy-safe
`PenetrationCaptureResponse` (status, reasons, proven flags, summary counts). It
is loopback- and capability-gated by the existing middleware, and the
coordinator still re-validates the offline gate, managed artifact/session
association, completed decode run, process identity, and exact build before the
source is called.

## Validation

- New source tests: 6 (unique/stable, ambiguous, unstable, zero, fail-closed,
  pinned-RVA request shape).
- New endpoint tests: 4 (invalid decode-run id, mapped evaluation, source
  failure).
- DI registration assertion now pins the concrete census source.
- Full `scripts/validate.ps1` gate: **0 warnings/errors**; 1,346 passed, 11
  opt-in skips, 0 failed across the solution.

## Next step (live, owner-approved)

The source and endpoint are the offline prerequisite. The actual census capture
now needs one fresh `OfflineReplayVerified` managed launch:

1. Launch an exact-build managed offline replay (existing launcher + dialog
   clicker).
2. Resolve the replay's `decodeRunId` from the sessions list.
3. `POST /api/v1/game/discover/pen-capture` with that id.
4. Adjudicate the returned count/uniqueness/stability.

The expected honest result is `Rejected` with `OwnerNotUnique` (a battle has one
gun per tank, so the census alone cannot attribute viewpoint ownership). The
per-family counts are the research signal that drives the next static step:
the viewpoint-vehicle → `VehicleGun`/`VehicleGunRotator` ownership walk, then
the shell/aim/ray field offsets for phases 2–4.

## Live attempt — honest negative (2026-08-16)

The first live census attempt did not reach the capture. The Dead Rail replay
was re-launched (its OD completion marker was preserved under a `.bak` suffix
for the re-launch and restored afterward), and the managed launch reached
`lifecycle_evidence outcome=verified` in the host log — but the game then
assert-crashed during replay playback. The newest blitz log ends with an
`ASSERT_END` from `ListenerHolderBase.cpp:15` and an AkAudio (`AK::`) write
stack, and the coordinator correctly denied the session with
`evidence.monitor_unhealthy` because the `wotblitz.exe` process was gone. The
launcher then hung past its bounded steps rather than failing fast (a
pre-existing launcher robustness gap for mid-flow game death, not a census
bug), so the run was stopped and the environment cleaned up: host stopped,
completion marker restored, no stray game/host processes.

This is an environment negative, not a code negative: the census source and
endpoint remain verified offline, and no census aggregate was produced or
promoted. The next live attempt should either retry after a clean game restart
(try the Oasis replay as the alternate battle) or first diagnose the
`ListenerHolderBase` assert.

## Launcher robustness fix (2026-08-16)

The pre-existing hang on mid-flow game death was fixed in
`scripts/launch-offline-replay-for-od.ps1`: the launcher now checks for a live
`wotblitz.exe` right after the Watch Offline success and again before the
clock-anchor HTTP calls. A playback assert that kills the game now exits with
`FAILED_game_died_after_watch` (exit 3) in seconds, and the clock-anchor block
degrades to `clock_anchor skipped_game_died` (non-fatal, flag stays false)
instead of hanging on the sessions/clock HTTP calls. The bounded-step timeout
remains as the outer safety net. Full `scripts/validate.ps1` gate green.
