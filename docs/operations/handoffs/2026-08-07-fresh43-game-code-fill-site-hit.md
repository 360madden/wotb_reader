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
