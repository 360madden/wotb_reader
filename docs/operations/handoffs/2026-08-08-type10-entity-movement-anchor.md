# Handoff - type-10 entity movement anchor and synthetic capture (2026-08-08)

## Outcome

`OD-RECOVERY-069` completed the offline/static data-flow pivot and found the
first exact entity-bound position event in the current executable.
`OD-RECOVERY-070` then implemented and synthetically proved its fixed
two-source capture contract.

The verified replay type-10 packet now has a continuous static chain from the
replay handler to engine entity resolution and XYZ application. At
`wotblitz.exe+0x022FA78D`, the resolved entity is in `ESI`, its type-10 entity
ID is at `[ESI+0x1C]`, and `EAX` points at the packet-derived contiguous XYZ
vector. The instruction bytes are `F30F7E00`.

This is a strong **instruction-event candidate**, not a stable polling offset.
No live game process, replay, database, or private artifact was opened. No
offset was promoted. The synthetic helper captured replay entity ID `4242`
with four changing finite XYZ vectors; fingerprint, bounds, parent rejection,
cleanup, and detach passed. After the full repository gate and a fresh pinned
publish, one bounded positively verified offline capture is recommended.
The ID and XYZ are two reads made while one debug event holds the process;
hardware atomicity, same-decoded-clock identity, and local-player identity are
not proven.

## Exact evidence boundary

- Game version: `11.19.0.10`
- Executable SHA-256:
  `1cda5c31919c9784a41bee7f3270ec1b4536b124c51e8b36f2221b381760307d`
- Replay type: `10`
- Payload: 49 bytes
- Type-10 handler RVA: `0x00FE31C0`
- Handler read sizes: `4,4,4,12,12,4,4,4,1`
- `BlitzServerMessageHandler` movement RVA: `0x00F7A610`
- Engine forwarding RVA: `0x022F9710`
- Entity resolver RVA: `0x022FC850`
- Entity application RVA: `0x022FA780`
- Candidate instruction RVA: `0x022FA78D`
- Candidate bytes: `F30F7E00` (`MOVQ XMM0,[EAX]`)
- Resolved entity register: `ESI`
- Entity ID member: `ESI+0x1C`
- XYZ pointer register: `EAX`
- XYZ layout: `EAX+0/+4/+8`, three Float32 values

`TraceType10MovementPosition.java` passes 40/40 fixed checks against that
exact executable hash. The ignored local report records the full check list;
only the aggregate and module-relative evidence belong in tracked docs.

## How the broad clue converged

`FindReplayEntityBridges.java` enumerated replay/entity strings, vtables,
construction references, and bounded direct-call relationships. The useful
cluster was the named `BlitzServerMessageHandler` replay entity callbacks and
the `ReplayPlayer` construction path.

`ReplayPlayer` builds a 40-entry normalized event-handler table. Constructor
instruction RVA `0x00FBC1D4` places RVA `0x00FE31C0` at index 10. This explains
why OD-RECOVERY-067 found no raw same-base `{length=49,type=10}` comparison:
the replay is indexed first and playback dispatches through normalized event
metadata.

The type-10 handler reads three 4-byte identifiers, two 12-byte vectors, three
4-byte velocity values, and one flag byte. Its vtable call reaches
`BlitzServerMessageHandler`, then the engine entity resolver. The resolver's
direct comparison of the packet ID with `[entity+0x1C]` proves the identifier
semantics that OD-RECOVERY-068 only suspected.

## Position-store corroboration

Entity movement application uses the entity's movement-filter pointer at
`+0x38`. The `BW::AvatarFilter` implementation forwards to
`BW::AvatarFilterHelper`, whose store method maintains an 8-entry ring:

| Helper-relative layout | Meaning |
|---|---|
| `+0x1C8` | current ring index |
| `+0x08 + index*0x38` | sample timestamp |
| `+0x10/+0x14 + index*0x38` | movement metadata IDs |
| `+0x18 + index*0x38` | position XYZ |
| `+0x24 + index*0x38` | zero/padding vector |
| `+0x30 + index*0x38` | velocity XYZ |

The corresponding readback method returns the same record fields. This is
independent semantic support for the position vector, but the ring is dynamic
and transient. Do not publish it as a stable member offset or root.

## Durable changes

- Added `tools/ghidra-scripts/FindReplayEntityBridges.java` for broad,
  relationship-first mapping.
- Added `tools/ghidra-scripts/TraceType10MovementPosition.java` for the exact
  40-check hash-bound semantic proof.
- Added the Ghidra fail-closed policy to `offline/commands.md`: native exit zero
  is insufficient without a clean script log, fresh report, and explicit
  verifier verdict.
- Re-pinned the production instruction-snapshot target to RVA `0x022FA78D`,
  bytes `F30F7E00`, with fixed `ESI+0x1C` replay-entity-ID and `EAX` XYZ reads.
- Advanced the private helper schema to v2 and the public DTO to expose only
  `replayEntityId`, opaque object key, UTC, values, and proof flags.
- Reworked the synthetic x86 target to set `ESI` and `EAX`, execute the exact
  instruction, and assert entity ID, changing XYZ, hit cap, parent rejection,
  cleanup, and non-overclaim flags.
- Updated `knowledge.md`, the offline discovery pack, research notes, workflow,
  roadmap, and discovery ledger with the candidate class and next boundary.

Generated Ghidra reports remain ignored under `.build/ghidra-evidence/` and
are not committed.

## Synthetic proof

- Helper publish: pass with build-pinned Host.Web EXE+DLL hashes.
- Exact synthetic target fingerprint: pass.
- Captured replay entity ID: `4242` on every accepted hit.
- Captured XYZ: four finite, changing samples.
- Bound: four accepted hits, then truncated/max-hit stop.
- Non-Host caller-created pipe plan: rejected before target access.
- Debug-register restoration, cleanup, and detach: proven.
- Public response: replay-local entity ID and values only; no process, entity,
  vector, or instruction-byte fields.
- Proof flags remain false for hardware atomicity and same decoded clock; no
  local-player identity is inferred from a replay entity ID.

## Next admissible work

`OD-RECOVERY-071` is one bounded live equality test, only after the full gate
and a fresh identity-pinned helper publish:

1. Start a managed positively verified offline replay with instruction
   snapshot enabled.
2. Capture for five seconds with at most 64 accepted hits.
3. Retain only hits where replay entity ID and XYZ reads both succeeded and XYZ
   is finite.
4. Match each captured replay entity ID only to that entity's decoded type-10
   trajectory at the aligned clock.
5. Stop after the result. Do not change the target, register, displacement, or
   start a scan in the same live session.

An exact entity/XYZ match proves reliable event-based entity-location reading.
Player-location reading additionally requires independent evidence that the
matched replay entity ID is the local player. It still does not prove a stable
polling root or authorize offset-table promotion.
