# 2026-08-11 — Entity-base record map: full VehicleGameLogic setter family

## Summary

The entity-base record's layout was only partially known (the health block:
`+0xB8` current HP, `+0xBA` alive, `+0x11C` max HP, `+0x11E` healing, `+0x7E`
gun angles). The VehicleGameLogic class carries a full **setter family** —
each `set_*` reads its field through the entity getter (vftable slot 1,
`MOV EAX,[ECX+0x4]; RET`). Dumping the whole family pins the record map well
past the health block, so future discovery never re-scans the entity record.

## New fields pinned (all via the entity getter, 11.19.0.10)

| Offset | Width | Field | Setter (RVA) |
|---|---|---|---|
| `+0x7C` | byte | isStrafing | `set_isStrafing` (0x12eead0) |
| `+0xBC` | ptr | engine-mode object (byte mode + sub-byte) | `set_engineMode` (0x12ee110) |
| `+0xC8` | vector | hit-marks list | `set_hitMarks` (0x12ee5a0) |
| `+0xD4`/`+0xD8` | ptr pair | byte-array mask state | `FUN_016ef1a0` |
| `+0xE0` | list | critical devices | `set_criticalDevices` (0x12edae0) |
| `+0xEC` | list | destroyed devices | `set_destroyedDevices` (0x12edf60) |
| `+0xF8` | list | active equipments | `set_activeEquipments` (0x12ecd90) |
| `+0x110` | state | debug strings | `set_debugStrings` (0x12ede90) |

Notable reads:

- `set_engineMode`: `MOV ECX,dword ptr [EAX + 0xbc]` then byte[0] and
  byte[1] of that object — an engine-mode state object, not a scalar.
- `set_hitMarks`: `ADD EAX,0xc8` — a vector copied to the UI listener.
- `FUN_016ef1a0`: two byte-array pointers at `+0xD4`/`+0xD8`, compared
  range-wise (a mask/state diff helper; no resolved string name).
- `set_criticalDevices` / `set_destroyedDevices`: device lists at `+0xE0`
  and `+0xEC`, both passed to the same copy helper `FUN_016ac710`.
- `set_activeEquipments`: `LEA EDI,[EAX + 0xf8]`.
- `set_debugStrings`: `ADD EAX,0x110`.
- `set_isStrafing`: `CMP byte ptr [EAX + 0x7c],0x0` — and writes the result
  into a UI-listener object at `+0x4f8`.

## Verifier

`VerifyPlayerHpChain.java` grew from 16 to **26 checks** (sections 9–15),
each asserting the exact disassembly pattern for the setter's field read
plus the entity-getter dereference where applicable. Headless run against
the hash-verified project: **PASS=26 FAIL=0, verdict
`player-hp-chain-verified`**, no script errors, evidence written to
`.build/ghidra-evidence/verify-player-hp-chain.txt`.

## Files touched

`tools/ghidra-scripts/VerifyPlayerHpChain.java`,
`docs/operations/record-diffing-groundwork.md` (entity-base record map
table), `memory-offsets/README.md` (static-only map note),
`docs/operations/product-roadmap.md` (L1 row).

## State / next steps

- All new fields are **static-only evidence, not promoted** — no offset in
  the table changed.
- The map directly benefits the entity-record diffing sessions: a region
  dump can now be interpreted instead of scanned blind.
- Next offline leads in preference order: extend the verifier to the
  transform-record family (position getter `FUN_00d29ea0` + matrix layout)
  for a second verified record, or start the camera/view-projection static
  hunt for the W2S overlay.
