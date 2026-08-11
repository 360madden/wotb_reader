# 2026-08-11 — Transform record under a hash-bound verdict (VerifyTransformRecord, 20/20)

## Summary

The transform object `[entity+0x3C]` was only "candidate layout" evidence
from the FRESH43 write-site decode (OD-RECOVERY-053). It is now a
**hash-bound static verdict**: `VerifyTransformRecord.java` (new, same
evidence standard as `VerifyPlayerHpChain`) asserts every hop of the chain
against the analyzed 11.19.0.10 project and passes **20/20** with verdict
`transform-record-verified`.

## Verified chain (FUN_00bc3940, per-frame fill, RVA 0x7c3940)

| Hop | Evidence |
|---|---|
| Getter | `FUN_00d29ea0` = `MOV EAX,[ECX+0x3C]; RET 0x4` — **byte-verified `8b 41 3c c2 04 00`** |
| Caller gate | entity list `FUN_00bb9b30` calls the fill when `[entity+0x20] & 0x800` (`TEST EAX,0x800` → `CALL 0x00bc3940`) |
| Position triple | gated non-zero float32 reads `[t+0x1C/0x20/0x24]` (via `MOVSS [EDX+0xc/0x10/0x14]`, EDX = t+0x10) |
| Quaternion source | `[t+0x10..0x1C]` fed to quaternion→matrix `FUN_00d1a0f0` |
| World matrix | 4×4 at `[t+0x60..0x9C]`, composed by matrix multiply `FUN_00729570`, stored via **4× `MOVUPS`** (`[ESI]`, `+0x10`, `+0x20`, `+0x30`; ESI = t+0x60) |
| Rotation region | writes at `[t+0x38]`, `[t+0x40]`, `[t+0x44]`, `[t+0x4c]`, `[t+0x50]` (+ basis normalizer `FUN_00d155c0`) |

The fill recomputes the matrix when the position triple changes and again
through a second multiply path (turret/weapon composition, `FUN_00e6a690`
gate) — both land in the same `+0x60` matrix.

## Notes

- The earlier "position `+0x38`" wording from Pass 1 is corrected: the
  position triple is `+0x1C/0x20/0x24`; `+0x38..0x58` is the rotation
  region. Roadmap + groundwork docs reconciled.
- **Static-only evidence, nothing promoted.** No live read; the published
  position chains stay resolver-bound.
- Evidence: `.build/ghidra-evidence/functions-disasm.txt` (transform-family
  dump: fill, getter, quaternion→matrix, normalizer, entity-list caller) +
  `verify-transform-record.txt` (20 PASS / 0 FAIL). Dump args used the
  absolute→RVA conversion (base 0x00400000): `0x7c3940 0x929ea0 0x91a0f0
  0x9155c0 0x7b9b30`.

## Files touched

`tools/ghidra-scripts/VerifyTransformRecord.java` (new),
`docs/operations/record-diffing-groundwork.md`, `memory-offsets/README.md`,
`docs/operations/product-roadmap.md`.

## Next steps

- The transform record is the overlay's world-space anchor: its `+0x60`
  matrix is a composited world matrix per frame. A natural next static lead
  is the camera/VP hunt — same standard, new verifier — for the W2S math.
- The entity base + transform record + health block now form a three-record
  verified map; future live sessions interpret dumps instead of scanning.
