# OD M2 — FRESH43: FIRST DURABLE GAME-CODE FILL-SITE HIT (2026-08-07)

**Session outcome:** the band-weighted emission (OD-RECOVERY-050) + proven
invocation + `-ArmSourceOnFirstHit` produced the **first module-mapped
game-code write-site hit**. The source-arm caught `wotblitz.exe+0x7C39AB`
writing into the memcpy source page, the SSE `MOVDQU` fill at
`VCRUNTIME140.dll+0xED49`, and the `REP MOVSB` propagation at
`VCRUNTIME140.dll+0xE8AE` landing on the armed member `0x22AB0F90`.
Verdict `family-hit` (`hit_members=1`, `liveness=running`,
`values_changed=true`), `trace_window_completed`, clean exit.

## The emission (band-weighted floor FIRED live for the first time)

| Metric | Value |
|---|---|
| Verdict | `evidence-strong` |
| Strong survivors | 23 |
| Families | 1 (solo) |
| Emitted family | `0x23A4C490` axes z+x, members=2, **score 0.933**, span 177.9, band 50.5s |
| Member 1 | `0x23A4C490` z, sign −1, shift 8.5s, band [8.5, 59], score 0.933, span 177.9 |
| Member 2 | `0x22AB0F90` x, sign +1, shift 11s, band [11, 58.5], score 0.933, span 46.8 |
| Addresses scored | 626 |
| Total samples | 9,390 |

Score 0.933 ≥ 0.9 → passed the strict wide-band path, so emission was
legitimate under both the old flat floor and the new band-weighted floor. This
is the first live emission since FRESH38 (0.933) and the **first live
`source_arm ON` execution** (FRESH39/40/41 never armed — no candidate emitted).

## The 6 interceptor hits (all thread 45272, one game)

| # | Address | Kind | RIP / RVA | Instruction | Value |
|---|---|---|---|---|---|
| 1 | `0x22AB0F90` | **member** | `0x5B24E8AE` / `VCRUNTIME140.dll+0xE8AE` | `F3A4` REP MOVSB | 0 |
| 2 | `0x28FFCF10` | source | `0x5B24ED49` / `VCRUNTIME140.dll+0xED49` | `F30F7F07` MOVDQU | 0.000457 |
| 3 | `0x28FFCF14` | source | `0x5B24ED49` / same | MOVDQU | 2.5736 |
| 4 | `0x28FFCF18` | source | `0x5B24ED49` / same | MOVDQU | −0.1119 |
| 5 | `0x28FFCF1C` | source | `0x5B24ED49` / same | MOVDQU | −0.1119 |
| 6 | `0x2C5C8A90` | **source** | `0x013F39AB` / **`wotblitz.exe+0x7C39AB`** | `8B83A0000000`… | 0.008282 |

**Module bases (from the capture's own module table):** `wotblitz.exe`
base `0x00C30000` (size 0x4482000), `VCRUNTIME140.dll` base `0x5B240000`
(size 0x15000). RVA math independently re-verified: `0x013F39AB − 0x00C30000 =
0x7C39AB` ✓; `0x5B24E8AE − 0x5B240000 = 0xE8AE` ✓; `0x5B24ED49 − 0x5B240000 =
0xED49` ✓.

## The write-chain (the answer to FRESH37's question)

1. **Hit 1 is the propagation:** `REP MOVSB` at `VCRUNTIME140.dll+0xE8AE`
   wrote into the armed member `0x22AB0F90` (the x-position candidate). Its
   `esi` register captured at hit time = **687,853,328 = `0x28FFCF10` exactly**
   — the copy-source pointer.
2. **Hits 2–5 are the refill:** `MOVDQU` at `VCRUNTIME140.dll+0xED49` stored 4
   floats into that exact source buffer (`0x28FFCF10`..`1C`).
3. **Hit 6 is the game fill:** **`wotblitz.exe+0x7C39AB`** — game code —
   wrote a float into a second armed source page at `0x2C5C8A90`
   (`sourcePagesArmed=2`).

**So the game's real per-frame write path is:** game code fills a staging
buffer (`wotblitz.exe+0x7C39AB`) → CRT vectorized copy stages 4-float chunks
(`VCRUNTIME140.dll+0xED49`) → `memcpy` propagates into the tracked position
field (`VCRUNTIME140.dll+0xE8AE` → member `0x22AB0F90`). The member address is
a **copy destination**; the *fill* happens at `wotblitz.exe+0x7C39AB`.

## Durable artifacts

- `od-048-autotrace-20260807-123621.json.capture.json` — 6 hits, full
  register state + RIP + instruction bytes, module table (135 modules)
- `od-048-autotrace-20260807-123621.json.family.json` — 3 write sites with
  member RVAs + per-site `registersSample`, verdict `family-hit`
- `od-048-autotrace-20260807-123621.json` — trace-complete report
- `od-049-fresh43-bandweighted-result.json` — M1 correlate (23 strong
  survivors, family above)

## Discipline notes

- Band-weighted floor **observed firing live** for the first time: it emitted
  a legitimate 0.933 family (would also have passed the flat 0.9 floor), and
  the 0.800 x survivors (FRESH39/42 class) were correctly refused.
- `complete=False` on the family: z member `0x23A4C490` was armed but not hit
  in the 25s window; only the x member `0x22AB0F90` took the copy. Multi-copy
  family (same source → several destinations) confirmed: only ONE destination
  observed in-window.
- Next hypothesis is already in hand: verify `wotblitz.exe+0x7C39AB`'s
  function via Ghidra (FindOffsets.py), then trace the source-page pointer
  chain to see whether the staging buffer holds x/y/z consecutively (the
  `MOVDQU` writes 4 floats = x, y, z + one more, at `0x28FFCF10`).

## Ghidra follow-through (same-session offline, 2026-08-07)

`wotblitz.exe` verified hash-bound to the 11.19.0.10 evidence
(`1cda5c31…`, exact match) before disassembly, so the decode applies to the
binary that produced the FRESH43 hit. Headless scripts:
`tools/ghidra-scripts/DumpWriteSite.java` + `DumpChain.java` (run with
`-noanalysis` against the existing `WotBlitz` project; ~90s each). Raw
dumps in `tools/ghidra-scripts/writesite-disasm.txt` +
`functions-disasm.txt` + `chain-disasm.txt` (71 KB).

### The write-site function: FUN_00bc3940 (RVA 0x7C3940, 0x2E6 bytes)

The RIP `wotblitz.exe+0x7C39AB` (`MOV EAX,[EBX+0xA0]`) sits inside a
**per-frame tank/entity transform update**, called per entity from the
entity-list iteration `FUN_00bb9b30` (flag-gated: `[entity+0x20] & 0x800`).
Structure:

- **Object source:** `EBX = FUN_00d29ea0(0)` where `FUN_00d29ea0` =
  `return *(int *)(param_1 + 0x3c)` — a **single-indirection getter**; the
  updated object is `[entity + 0x3C]`.
- **Validity gate:** `[obj+0xA0]` non-zero (the exact RIP instruction).
- **Position gate:** checks `[obj+0x1C]`, `[obj+0x20]`, `[obj+0x24]` floats
  non-zero — **a position triple (x, y, z) at obj+0x1C/0x20/0x24**.
- **World transform:** refills `[obj+0x60 .. 0x9C]` (16 floats = 4×4 matrix)
  via 4× `MOVUPS xmmword` stores — **the exact SSE 16-byte pattern captured
  in hits 2–5** — from `FUN_00729570` (a 4×4 matrix multiply) composed from
  `FUN_00d1a0f0` (quaternion→matrix build from `[obj+0x10]`) and the
  per-entity basis from `FUN_00e6a690`/`FUN_00d155c0`
  (orthonormal-basis normalizer: sqrt of squared sums).
- **Second block:** `[obj+0x38 .. 0x5C]` written from `FUN_00d16380` output
  (identity-with-diagonal + quaternion product terms) — a second
  rotation/position representation.

### What this means (offline milestone, no promotion)

1. **The write site is real game transform code, not a CRT blob.** The
   captured `REP MOVSB`/`MOVDQU` hits were the CRT copy of a **64-byte
   transform block** whose producer is `FUN_00bc3940` — a tank world-matrix
   update. The member address the correlator found (`0x22AB0F90`) is one
   destination copy of this block.
2. **Candidate stable layout for the player tank:**
   `position x/y/z = [entity+0x3C] + 0x1C / 0x20 / 0x24` and
   `world matrix = [entity+0x3C] + 0x60` (translation column likely carries
   world x/y/z). The 4-float `MOVDQU` values captured
   (0.000457 / 2.5736 / −0.112 / −0.112) are a rotation/scale row of that
   matrix, not world coordinates — consistent with FRESH37's hit values being
   matrix rows, not direct positions.
3. **Promotion still blocked (evidence-first):** no offset is promoted.
   M3 requires (a) a live read of `[entity+0x3C]+0x1C/20/24` at a known replay
   instant matched to decoded ground truth, (b) repeatability across battles,
   and (c) `independentReplays >= 1` (BLK-0019). The entity pointer chain
   above `FUN_00bb9b30` (the array container) is not yet rooted to a global.

### Next (offline-first, no live round without a changed hypothesis)

1. Dump `FUN_00bb9b30`'s caller to find the entity-array container and walk
   it to a stable global root → candidate static pointer chain.
2. Interceptor experiment (offline-validated, live round): on the next
   family-hit, arm `[obj+0x1C..0x24]` (the position triple) instead of the
   whole block and verify the captured values match decoded ground truth at
   the same replay clock.
3. Keep `0x7C39AB` (and the +0x38/+0x60 blocks) as the anchor evidence for
   the transform-object hypothesis.

## Amendment 2026-08-08: entity container walked to a stable global (OD-RECOVERY-054)

### Caller chain decoded (Ghidra headless -noanalysis, hash-verified 11.19.0.10 binary)

The entity-array container for `FUN_00bb9b30` is rooted at a **stable global singleton**:

```
DAT_043f516C  (BattleController singleton, RVA 0x3FF516C; lazy-init getter
               FUN_008ee9f0 = return DAT_043f516C; built by FUN_008e8e90)
   -> [BattleController + 0x4]  = AvatarController        (candidate hop: thunk
       FUN_016673a0 does MOV ECX,[ECX+4]; JMP ReloadScreenForRewind; the thunk
       `this` is vtable-dispatched, so this hop is UNVERIFIED)
   -> [AvatarController + 0x154] = BattleResources         (call site 0x165DB56:
       MOV ECX,[EDI+0x154]; CALL FUN_01651780 = BattleResources::Load)
   -> [BattleResources + 0x8]   = GameScene                (call site 0x16528D4:
       MOV ECX,[EDI+0x8]; CALL FUN_00765670 = GameScene::OnLoadingFinished)
   -> [GameScene + 0x88]        = TransformSystem          (call site 0x765723:
       MOV ECX,[EBX+0x88]; CALL FUN_00bbffb0 = TransformSystem update)
   -> [TransformSystem + 0x30]  = entity vector            (FUN_00bbffb0 feeds the
       list FUN_00bb9b30 walks; populated from a world container at
       [*(TS+0x28) + 0xFC])
   -> entity[i] : gate [entity+0x20] & 0x800               (FUN_00bb9b30 DFS)
   -> [entity + 0x3C]            = transform object        (getter FUN_00d29ea0
       = return [ECX+0x3C])
   -> [transform + 0x1C/0x20/0x24] = position x/y/z         <<< CANDIDATE TARGET
   -> [transform + 0x60..0x9C]    = 4x4 world matrix        (4x MOVUPS fills)
```

### How the chain was walked

- `FUN_00bc3940` (write site, RVA 0x7C3940) <- `FUN_00bb9b30` (entity DFS,
  RVA 0x7B9B30, call @0x7B9B75, gate `[entity+0x20] & 0x800`)
- `FUN_00bb9b30` <- `FUN_00bbffb0` (TransformSystem::update) with ECX =
  `[GameScene+0x88]`
- `FUN_00bbffb0` <- `FUN_00765670` (GameScene::OnLoadingFinished) with ECX =
  `[BattleResources+0x8]`
- `FUN_00765670` <- `FUN_0165247c` (BattleResources::LoadGameScene, RVA
  0x125247C) <- `FUN_01651bd0` (LoadGameScene wrapper) <- `FUN_01662f00`
  (BattleResources::TryLoadResources) <- `FUN_01651780` (BattleResources::Load)
  <- `FUN_0165d9b0` (AvatarControllerBase::ReloadScreenForRewind) <- thunk
  `FUN_016673a0` (vtable-dispatched; AvatarController = [thunk-this + 0x4])
- BattleController singleton `DAT_043f516C` reached from every function in the
  chain via `FUN_008ee9f0()` (thread-safe lazy singleton).

### Status

- **Candidate static pointer chain exists** (above), but the
  `BattleController -> AvatarController` hop is UNVERIFIED (vtable dispatch, no
  static caller). The remainder from BattleResources down is
  call-site-verified member offsets.
- **No promotion.** M3 still requires a live read of the position triple matched
  to decoded ground truth + cross-battle repeatability + `independentReplays`
  (BLK-0019).
- **Next:** interceptor-arm `[obj+0x1C..0x24]` on the next family-hit and compare
  captured values to decoded ground truth at the same replay clock; optionally
  verify the AvatarController hop by dumping `FUN_008dfaa0` (the object built
  into the singleton) and hunting `MOV [BattleController+0x4], X` writes.
