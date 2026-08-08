# Handoff - OD-071 first live type-10 equality (2026-08-08)

## Outcome

The fixed type-10 instruction capture produced the first direct live equality
proof for player position in the current 11.19.0.10 executable.

After the full repository gate, a fresh identity-pinned helper publish, and a
passing synthetic x86 test, one managed replay reached
`OfflineReplayVerified`. A single five-second request at the fixed
`wotblitz.exe+0x022FA78D` / `F30F7E00` target completed with 49 hits. Every hit
had a readable replay-local entity ID and finite XYZ, with fingerprint and
cleanup proven.

Seven captured vehicle entities matched their decoded type-10 XYZ exactly at
Float32 precision. One exact match was independently marked as the replay
viewpoint entity. The eighth opaque object was a zero vector with no decoded
trajectory and was excluded. No private IDs or coordinates are repeated here.

This proves event-based player-position identity for this replay window. It is
not yet motion-fresh, cross-replay-repeatable, same-clock, hardware-atomic, or a
stable polling offset.

## Evidence boundary

- Gate: `OfflineReplayVerified`
- Capture: one request, five seconds, maximum 64 hits
- Accepted hits: 49
- Successful replay-ID and finite XYZ reads: 49/49
- Opaque objects: 8
- Decoded vehicle-entity matches: 7
- Exact Float32 XYZ matches: 7/7 matched vehicles
- Exact replay-viewpoint match: 1
- Excluded non-trajectory object: 1 zero vector
- Instruction fingerprint: matched
- Cleanup: proven
- Processes remaining after shutdown: 0

The displayed decimal values were normalized back to Float32 before the exact
comparison. Before normalization, every matched entity was already within
`0.00001` world units of decoded ground truth. The proof used entity ID as the
join key; no cross-entity nearest-neighbor match was accepted.

## What this proves

1. The static OD-069 register contract survives in the live current build:
   `ESI` is the resolved replay entity and `EAX` points to the applied XYZ.
2. `[ESI+0x1C]` is the same replay entity ID used by decoded type-10 telemetry.
3. The captured XYZ is the same Float32 position triple present in decoded
   ground truth for that entity.
4. One exact entity is the replay viewpoint, so the path can read the player's
   replay position at the instruction event.
5. Parent binding, exact target identity, fingerprint, bounded capture,
   restoration, detach, and post-session shutdown all held.

## What remains unknown

- The captured vehicle positions were static during the five-second window.
  Current-motion freshness is not yet proven.
- No decoded replay clock was captured with the hits; same-clock identity stays
  false even though value/entity identity is exact.
- Entity ID and XYZ are two reads during one suspended debug event, not a
  hardware-atomic transaction.
- The exact event has not yet repeated on the other content-distinct replay and
  fresh process.
- The event is not a stable pointer chain or continuously pollable offset.
- `memory-offsets/11.19.0.10.json` remains unchanged.

## Privacy and durability

Tracked evidence contains aggregates and module-relative static identity only.
It contains no process addresses, replay entity IDs, XYZ values, names, account
data, replay filenames/paths, capability values, screenshots, or raw replay or
memory bytes. A sanitized aggregate is retained only under the ignored local
data tree.

The first launch attempt failed closed before Host/game start because the
freshness guard saw a newer generated source file. Release Host was rebuilt,
the helper was republished and synthetically revalidated, and only then was the
successful managed session started.

## Next admissible work

`OD-RECOVERY-072` is one bounded motion/repeatability proof:

1. Use the other content-distinct replay in a fresh managed process.
2. Keep the exact target, registers, displacement, five-second duration, and
   64-hit limit unchanged.
3. Trigger the capture only after verified vehicle movement is underway.
4. Require a changing replay-viewpoint XYZ series and same-entity decoded
   matches. Preserve aggregate counts/errors only.
5. Stop all live processes after the result. Do not scan or change target
   parameters in the same session.

A positive OD-072 result establishes motion freshness and cross-replay
repeatability for event-based player-location reading. Designing a stable
polling resolver remains a separate later pivot.
