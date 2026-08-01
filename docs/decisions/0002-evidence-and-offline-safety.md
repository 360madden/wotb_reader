# ADR 0002: Evidence preservation and offline-only automation

- Status: accepted
- Decision date: 2026-07-26

## Context

Replay formats contain undocumented and version-dependent semantics. Game
automation can also affect the wrong session if it targets a window by title
or retries input blindly.

## Decision

Preserve source artifacts and unknown record byte ranges immutably. Claim only
semantics supported by source evidence and expose capability/uncertainty
metadata to users. Bot status is tri-state; player names and bot status are
public Wargaming statistics, so bot status may be inferred from a name.

Developer automation is denied by default. Input requires explicit arming,
exact process/build/hash verification, the foreground HWND, compatible
integrity level, and a positive native-log marker for local replay playback.
Only allowlisted replay controls are permitted, with bounded retries,
pre/post screenshots, and append-only audit records.

## Consequences

Some alpha fields will honestly remain unknown and synchronization can become
stale. This is preferred to plausible but fabricated telemetry. The harness is
useful for repeatable validation without becoming a generic game-control tool.
