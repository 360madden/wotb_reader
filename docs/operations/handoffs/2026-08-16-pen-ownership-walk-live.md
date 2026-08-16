# Penetration v0.3 — ownership-walk live validation (H1 CONFIRMED)

**Date:** 2026-08-16 (UTC)
**Status:** H1 confirmed live — the viewpoint-vehicle → `VehicleGun` /
`VehicleGunRotator` ownership chain is proven on the exact build
**Blocker:** `BLK-0027` (stays open until the phase 2–4 shell/aim/ray fields
are derived; the ownership-walk discriminator itself is now closed)

## What ran

One exact-build managed offline replay (Churchill I, Oasis Palms, build
`11.19.0.10` hash `1cda5c31…`; battle session
`01a00ba4-91b8-783a-a894-b800cb35407e`) reached `OfflineReplayVerified`, and
`scripts/capture-pen-ownership-walk.ps1` drove the new `pen-ownership-walk`
entity-region anchor.

## Verdict

```
status=Resolved
rotator_candidate_count=1
owner_pointer_readable=True
forward_round_trip=True
gun_vtable=True
entity_hp_plausible=True
two_pass_stable=True
region_base64_is_null=True
```

**H1 CONFIRMED.** The AOB scan found exactly one live `VehicleGunRotator`
(matching the census's 42/1/1 signal), its `+0x10` resolved to the owner,
the owner's `+0x1fc` round-tripped to the same rotator, `+0x204` pointed to a
live `VehicleGun` whose first dword is the `0x32dacf4` vftable, and `+0x04`
resolved to an entity with plausible HP at `+0xB8` — identically across both
passes. No raw region bytes left the coordinator.

## Honest notes

- `same_decoded_clock=False`: the optional same-decoded-clock attestation did
  not fire on this probe (no fresh replay-clock segment was available at read
  time). It is a label, not part of H1 — the five-read chain + two-pass
  stability is the ownership verdict, and it is fully positive.
- Nothing was promoted. This is an investigation read: the ownership chain is
  proven, but no shell/aim/ray field and no `OwnerChainProven` capture flag
  were flipped (that remains the separate, later shared-contract change).

## What this unblocks

The plan ordered the phase 2–4 semantic field derivation *after* the walk's
live validation. That gate is now passed: the next work is deriving
configured gun / loaded shell / turret yaw / gun elevation / muzzle ray
against the now-proven live owner objects, followed by the live controlled
transitions for the field semantics.

## Cleanup

The game and host processes were stopped after the read; the environment is
clean. The replay completion markers remain in their prior set-aside state.
