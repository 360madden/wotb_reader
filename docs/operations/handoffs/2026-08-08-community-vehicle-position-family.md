# Handoff — community Vehicle position-family triage (2026-08-08)

## Outcome

`OD-RECOVERY-068` is complete as an offline/static investigation.

The historical UnknownCheats-derived layout was useful as a **class and
relationship clue**, but it did not yield a current player-position candidate:

- historical module root `0x03E91978` remains refuted in the exact installed
  `11.19.0.10` executable;
- the real current `VehicleGameLogic` vtable and its entity getter were found;
- the claimed returned-entity `+0x68/+0x6C/+0x70` position triple was not used
  by any getter-using `VehicleGameLogic` virtual method;
- the only complete generic chained triple and the strongest unanchored float
  fallback were decompiled as matrix/pose structures.

No live process, replay, database, debugger, memory reader, or private artifact
was opened. No offset was promoted. A live capture is **not recommended yet**.

## Exact evidence boundary

- Game version: `11.19.0.10`
- Executable SHA-256:
  `1cda5c31919c9784a41bee7f3270ec1b4536b124c51e8b36f2221b381760307d`
- Historical family:
  `VehicleGameLogic +0x04 -> entity +0x68/+0x6C/+0x70`
- Current `VehicleGameLogic` vtable RVA: `0x0327DA50`
- Vtable method count: `79`
- Entity getter slot: `+0x04`
- Entity getter RVA: `0x0031B560`
- Getter bytes: `8B4104C3` (`MOV EAX,[ECX+0x04]; RET`)

`tools/find-static-roots.py --vtable-root VehicleGameLogic` independently
resolved the RTTI/COL/vtable relationship. A Ghidra dump of all 79 vtable
functions found 17 methods that call the getter. Their decompiled uses covered
23 returned-entity offsets:

`0x10, 0x11, 0x1C, 0x28, 0x38, 0x7C, 0x7E, 0xB8, 0xBA, 0xBC, 0xD4, 0xD8,
0xDC, 0xE0, 0xEC, 0xF8, 0xFC, 0x110, 0x11C, 0x11E, 0x128, 0x134, 0x16C`.

None was `0x68`, `0x6C`, or `0x70`. `+0x1C` occurred in seven getter-using
methods and is an **identifier hypothesis only**. `VehicleGameLogic::onEnterWorld`
corroborates health-like behavior at returned-entity `+0xB8`; that is useful
for class tracing, not position publication.

## Full structural scan

`FindVehiclePositionFamily.java` scanned all 526,935 executable functions:

| Measure | Result |
|---|---:|
| Generic `[reg+0x04]` loads | 111,693 |
| Direct structurally related candidates | 68 |
| Complete direct `+0x68/+0x6C/+0x70` triples | 1 |
| Complete direct non-matrix triples | 0 |
| Same-base triple fallbacks | 662 |

The sole complete direct result is `FUN_00c1ad60` (RVA `0x0081AD60`). It copies
and interpolates a larger record across `+0x60..+0x80`; it is matrix/pose-like,
has no `VehicleGameLogic` anchor, and is not an entity-position candidate.

The strongest unanchored float-looking fallback is `FUN_01ebf860` (RVA
`0x01ABF860`). Its caller `FUN_01de0b00` copies 16 contiguous values into
`+0x6C..+0xA8`, proving that apparent triple is the first row of a 4x4 matrix.

Same-base exact triples in `VehicleGameLogic::onEnterWorld` and
`VehicleGameLogic::showDamageFromShot` are fields on the logic object itself;
decompilation shows pointers/state rather than three floats.

## Durable changes

- Added `tools/ghidra-scripts/FindVehiclePositionFamily.java`.
- Corrected unsupported “current/verified” wording in
  `research/memory-offsets-unknowncheats.md` and `research/community-tools.md`.
- Updated the ledger, workflow, roadmap, offline discovery pack, commands, and
  `knowledge.md` with the static result and next boundary.

Generated Ghidra reports remain ignored under `.build/ghidra-evidence/`; they
contain reproducible local static detail and are not committed.

## Next admissible work

`OD-RECOVERY-069` is offline/static-only:

1. Continue the generic replay reader/framer data-flow trace from
   OD-RECOVERY-067.
2. Converge it with the exact `VehicleGameLogic` entity getter rather than the
   stale module root or stale member triple.
3. Treat returned-entity `+0x1C` only as a candidate bridge to the type-10
   entity identifier; use health-like `+0xB8` only to corroborate object class.
4. Find the exact entity-bound XYZ application/write and preserve module RVA,
   instruction bytes, register/entity provenance, and one fixed contiguous
   member read/write.
5. Only then review a bounded synthetic capture plan and request another
   positively verified offline replay session.

Do not run the old root, direct `+0x68/+0x6C/+0x70` read, a broader heap scan,
or an unchanged render-transform capture.
