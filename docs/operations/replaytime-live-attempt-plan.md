# replayTime live-attempt plan — pre-staged for OD-044 (2026-08-10)

Purpose: make the next `replayTime` milestone a turnkey approved session. The
preference-order goal is `replayTime` (position family published; HP offline
complete). The rolling-survivor campaign already proved the **candidate set**
(OD-012..038); what never landed is the **write-site capture** (OD-044 —
operator-present Find-what-writes). This doc records the exact session flow,
the verdict contract, and the known failure modes, so the live step is
decisions-only.

## 2026-08-11 update: write-site negative — copy-path expectation

An exhaustive byte-level scan of every direct 8-byte store encoding with
displacement 0x90 in wotblitz.exe found **zero sites** (FSTP/FST m64fp
disp8+disp32, MOVSD, MOVQ, MOVLPD, SIB, absolute, split-double MOV pairs;
`.build/ghidra-evidence-player21/scan-clock-store-bytes.txt`, v2, 0 hits;
corroborated by the instruction-iterator scan in
`.build/ghidra-evidence-player18/scan-all-clock-offset-stores.txt` — 0 qword
stores, 3088 other loads). The clock at `[subobj+0x90]` is therefore written
by a **copy path**, not a direct store — the same synchronized-multi-copy
reality FRESH37/38/43 proved for position (CRT `memcpy`/`rep movsd` landing
on the field, or a DAVA Any store through a computed address).

**Session consequence:** expect the first interceptor hit to be a copy site
(CRT/VCRUNTIME RIP shape), not the logical write. `-ArmSourceOnFirstHit` is
therefore load-bearing for replayTime, not speculative: capture the first
hit, arm the copy-source page in the same window, and resolve the real write
one level up — mirroring FRESH43 for position. The chain-resolve path (below)
is unchanged; it lands the interceptor on `[subobj+0x90]` directly.

**Mechanism refinement (2026-08-11, same day):** the copy is a **DAVA `Any`
store**, not a bare CRT memcpy. The connection ctor installs the initial
time through `FUN_0270f430` = DAVA `Any::cast` machinery (called with
`ECX=[conn+0x58]`, the sub-object, and the float time value), and its
caller chain (`FUN_027063d0` ← `FUN_02721490`) all sits in the DAVA
Any/TLS region (RVA 0x2700000–0x2720000). The extended store scan (now
covering 16-byte MOVUPS/MOVAPS stores that could straddle +0x90) still
finds **zero direct stores**, so the write is `Any::Set`-style: type-erased
buffer + computed address. Expected first-hit RIP: DAVA Any region
(0x2700000–0x2720000) or CRT copy, resolving to a module RVA via
`WriteSiteAnalysis`; `-ArmSourceOnFirstHit` then follows the source one
level up, exactly as FRESH43 did for position.

## 2026-08-10 update: replay-clock chain statically verified

`tools/ghidra-scripts/TraceReplayClock.java` (v3, hash-bound, 10/10 checks)
now pins the clock's full ownership chain for the 11.19.0.10 build:

```
GameCore 0x04095c88 -> AppController +0xc -> SessionController +0x124
  -> AccountController +0x118 -> PlaybackController +0x128
  -> BWServerConnection +0x120 (vftable 0x34400d0)
  -> replay-player sub-object +0x58 -> clock +0x90 (Double, seconds)
```

Verified via three independent anchors: (1) the entity-movement resolver
`BWEntities::handleEntityMoveWithError` (0x022fc850) calls a virtual on the
connection back-pointer and threads the Double into the movement-ring time
field (record +0x0); (2) connection vtable slot 10 is the direct getter
`FUN_026f9140 = MOV EAX,[ECX+0x58]; FLD double [EAX+0x90]; RET`;
(3) slot 18 corroborates the semantics with `MOVSD XMM0,[EAX+0x1270];
SUBSD XMM0,[EAX+0x90]` (duration anchor minus current clock). Evidence:
`.build/ghidra-evidence-clock34/trace-replay-clock.txt`
(verdict `replay-clock-chain-verified`, sha256
`1cda5c31…1760307d`).

**What this changes for the live session:** the field is no longer a
rolling-scan unknown. The session can resolve the chain live via L0 region
reads (deref `GameCore→…→[Connection+0x58]+0x90`) and arm the interceptor
directly on the resolved address — turning the ~120 s rolling campaign into
a ~10 s chain deref. The write site is still unpinned (the interceptor
capture is the point of the session), and the chain root `0x04095c88` must
be re-verified against the live module base at session start.

## What exists (evidence inventory)

| Piece | State |
|---|---|
| Rolling increased-Double campaign | `scripts/roll-replay-time-increased.ps1` — `CompareMode increased`, two-phase pulse, 401 refresh, KUSER clock drop, `-AddressFile` staging. Proven across **30 verified process launches** (OD-012..038). |
| Survivor convergence | TARGET ≤ 10 reached 3× (OD-031 ×2, OD-036); tail plateau 11–17 (value-bound; **11 best under budget**). |
| CE staging | OD-031/036 staged all survivors + 4 HW write-BPs armed; **no RIP hit** — the CE/Windows write-BP route is closed (guardrail). |
| x64dbg route | **CLOSED** (FRESH26–33): `bpm`/`bph` never fire on worker-thread writes; no live session may be spent on it. |
| **C# guard-page interceptor** | `tools/WriteInterceptor` → `.build/publish/write-interceptor/WotBTreader.WriteInterceptor.exe`. Arms PAGE_GUARD on the armed pages, catches `STATUS_GUARD_PAGE_VIOLATION`, records RIP + address per write. **The proven capture route** (FRESH36: 51 hits/4 RIPs; FRESH43: first durable game-code fill-site hit `wotblitz.exe+0x7C39AB`). |
| Write-site resolution | `WriteSiteAnalysis` (Core/Discovery) resolves RIP → module+RVA, ranks object-base candidates from registers. |
| replayTime field identity | Double (8 bytes), elapsed replay seconds, monotonically increasing, advances every frame. `memory-offsets/11.19.0.10.json` — `replayTime` offset **0 / Unknown** (no root/RIP yet). |

## The one-window problem (why this plan exists)

OD-016/031/036 showed the hard failure mode: the roll consumes the ~120 s
research lease, and by the time survivors ≤ 10 are staged the window to
interact (CE/x64dbg) is `EvidenceStale` — the game is terminated and the
candidates are gone. The fixes that already landed (two-phase tail pulses,
candidate-count optimization, 401 refresh, KUSER drop) shrank the roll but
could not close the handoff gap.

**The changed hypothesis:** arm the **C# guard-page interceptor** — not a
debugger — so capture starts the moment the roll lands, inside the same
process and same lease. The interceptor attaches in seconds (no x64dbg
attach-freeze, no DR0–DR3 limit, no operator keystrokes), arms the pages
holding the ≤ 11 survivor addresses, and records every write with its RIP
while the replay keeps playing. The operator step that OD-044 originally
required (interactive Find-what-writes) is replaced by an automated capture
with a durable report — the same route that caught FRESH36/43's real write
sites.

## Session flow (one approved launch)

```powershell
# 1. Launch the offline replay (canonical pipeline, one content-distinct
#    replay — same Churchill/savanna artifacts as the OD campaign).
scripts/launch-offline-replay-for-od.ps1

# 2. As soon as the gate is OfflineReplayVerified, run the driver:
#    roll → stage survivors → arm interceptor → capture → verdict.
scripts/invoke-od-044-replaytime-session.ps1 `
    -TargetSurvivors 10 -AddressFile %TEMP%\od-survivors.txt `
    -TraceSeconds 60 -ResultPath .data\od-044-<timestamp>.json
```

Driver sequence (mirrors `od-018-session.ps1` + the interceptor wiring from
`invoke-csharp-write-trace.ps1`):

1. **Gate wait** — poll `/api/v1/game/state` until `OfflineReplayVerified`
   (fail-closed exit 3 after timeout).
2. **Roll** — `roll-replay-time-increased.ps1 -TargetSurvivors 10
   -AddressFile %TEMP%\od-survivors.txt` (two-phase pulses, 401 refresh,
   KUSER clock drop all built in). Exit nonzero → diagnose gate (lease vs
   API) and stop — do not arm on a stale set.
3. **Stage check** — read the address file; require ≥ 2 survivor addresses,
   all `0x…` hex tokens, none on the `0x7FFE0xxx` KUSER page (the roll
   already drops them; re-check defensively), and warn on
   count ≠ survivors.
4. **Arm + capture** — invoke
   `WotBTreader.WriteInterceptor.exe --interceptor -Pid <game pid>
   -Addresses <csv> -Seconds <trace> -Out <capture.json>` where the game pid
   is auto-discovered (`Get-Process -Name wotblitz`, windowed). The capture
   window is budgeted against the battle tail (mirror FRESH20: `-TraceSeconds`
   capped to `battleEnd − 15 s` margin, floored 10 s) and the gate is
   re-verified before arming (FRESH30 lesson: never arm after battle end).
5. **Verdict** — parse the capture report:
   - **HIT**: ≥ 1 captured write on an armed survivor address, with RIP(s)
     resolving to a module RVA (`WriteSiteAnalysis`) and the durable
     `.capture.json` promoted next to the result (FRESH36 lesson: never lose
     modules/rva/registers to an ephemeral TEMP path).
   - **no-write** (clean exit, zero hits): honest negative — the armed
     addresses were not written in the window. Record it; do not repeat
     unchanged (descope per guardrails).
   - **gate lost / attach failed / stale build**: fail-closed exit, no
     verdict consumed.

## Verdict contract

A `replayTime` HIT requires:

1. ≥ 1 captured write on an armed survivor address during a window where the
   replay clock is provably advancing (play-state not paused, values
   changing — mirror the value-liveness discriminator).
2. The write-site RIP resolves to a **module RVA** (never publish the raw
   address), and the instruction expresses a Double (8-byte) store to the
   survivor (or a CRT copy landing on it — the synchronized-multi-copy
   reality FRESH37/38/43 proved for position; `movsd`/`fld`+`fstp`/`rep
   movsd` are all consistent).

   **Discriminator (2026-08-10 fix, load-bearing):** the interceptor must be
   armed with `-ValueSize 8`, and its write discriminator is **byte-exact on
   the tracked 8 bytes** (each hit carries `valueHex`, the exact bytes). A
   float-epsilon compare would miss every replayTime write: the low dword of
   a monotonic Double reinterpreted as float is a ~1e-38 denormal (60.0 →
   60.016 changes `0x00000000` → `0x9374BC6A`), far below any epsilon — and
   can be NaN/Infinity, which JSON refuses. Proven offline by
   `test-offline-write-observation.ps1`'s `--double` phase (126/126 distinct
   8-byte patterns on a 0.016 s/frame replayTime-mimic).
3. Repeatability **across the 2-launch × 2-replay rule** only after the first
   HIT: the matched offset/RVA repeats on the second content-distinct replay
   (BLK-0019 is resolved — both Churchill and savanna decode).
4. The evidence record keeps `publicProcessAddressesOrRawBytes: false`;
   publish only the module-RVA + instruction form through the operator gate.

If the interceptor captures writes but the RIPs are all CRT/`VCRUNTIME` copies
(FRESH37/38 shape), the field is a synchronized copy and the *real* write is
one level up (the `esi` copy-source) — arm the source page on first hit via
`-ArmSourceOnFirstHit` (FRESH43's dynamic source-arm, proven for position) in
a **second** session. Do not attempt cross-battle arming of captured sources
(battle-scoped heap, FRESH38 ruled it out).

## Known failure modes (do not repeat)

- **Lease wall after roll** — the reason for this plan: arm the interceptor
  in the same driver call, no operator handoff gap. If the roll still
  exhausts the lease, tighten `-SnapshotMaxBytes`/tail pulses before the
  next session (OD-031/035 knobs), not after.
- **KUSER clock survivor** (`0x7FFE0010`, OD-044) — dropped by the roll +
  WARN; the driver re-checks. A surviving clock page is a dying-game signal,
  not a field.
- **CE/x64dbg write-BPs** — closed routes; no live session on them.
- **Arming after battle end / stale family** — FRESH20/30 lessons: budget the
  window against the battle tail and re-verify the gate at arm time.
- **Playing a paused replay** — a paused replay writes no clock field;
  fail-closed on the play-state probe.

## State

- 2026-08-10: static clock chain verified (see update above); plan updated
  with the chain-resolve session option.
- 2026-08-11: exhaustive write-site scan returned the copy-path negative;
  `-ArmSourceOnFirstHit` promoted from optional to expected in the session
  flow.
- 2026-08-11: mechanism refined — the copy is a DAVA `Any` store (ctor calls
  `Any::cast` machinery `FUN_0270f430` on the sub-object with the time
  value; callers all in the Any/TLS band 0x2700000–0x2720000). Expected
  first-hit RIP region narrowed accordingly.
- Pre-staged plan only; **no live session run, no product change**.
- Next: decide chain-resolve (L0 reads, ~10 s) vs rolling campaign (proven,
  ~120 s) at the approved session; both feed the same interceptor verdict
  contract.
- **Driver built and offline-validated (2026-08-10):**
  `scripts/invoke-od-044-replaytime-session.ps1` (gate wait → roll → stage
  check → interceptor arm → verdict) — built from the proven
  `od-018-session.ps1` + `invoke-csharp-write-trace.ps1` pieces. The probe
  `tmpwotb-e2e/test-od-044-driver-logic.ps1` (AST-extracted
  `ConvertTo-HexToken` + mirrored resolution, 17 checks) caught and fixed a
  real bug in the driver's write-site resolution: a first-match
  `base <= RIP` loop mis-attributes a CRT write to `wotblitz.exe` (its low
  base also satisfies the test) — the resolver now picks the module with the
  HIGHEST base ≤ RIP (module that actually contains the address). PS 5.1 + 7
  parse clean; PSSA gate 0 violations on the script. The one offline
  validation not done is a synthetic-counter interceptor run through the
  full driver (the driver requires a live gated host) — the interceptor
  itself is already proven by `test-offline-write-observation.ps1`.
- Related: `docs/operations/record-diffing-groundwork.md` (HP live plan),
  `memory-offsets/11.19.0.10.json` (replayTime 0/Unknown).
