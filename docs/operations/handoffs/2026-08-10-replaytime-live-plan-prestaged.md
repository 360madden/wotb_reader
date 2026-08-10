# replayTime live plan pre-staged + OD-044 driver built (2026-08-10)

Status: PRE-STAGED (no live session, no product change). Next in preference
order after the published position family; HP offline complete.

## What was done

1. **`docs/operations/replaytime-live-attempt-plan.md`** — the turnkey OD-044
   plan. Inventory: the rolling increased-Double campaign (OD-012..038, 30
   launches, tail 11–17), the closed CE/x64dbg write-BP routes, and the
   **C# guard-page interceptor** as the proven capture route (FRESH36/43).
   The core problem this plan solves: the OD-016/031/036 handoff gap — the
   roll consumed the ~120 s research lease and the operator window was
   `EvidenceStale` by the time survivors ≤ 10 were staged. The changed
   hypothesis: arm the interceptor the moment the roll lands, same process
   and lease, no operator keystrokes. Session flow, verdict contract
   (write on an armed survivor during advancing clock, RIP → module RVA,
   repeatability across the 2×2 rule), and known failure modes documented.

2. **`scripts/invoke-od-044-replaytime-session.ps1`** — the one new
   executable the plan needs: gate wait → roll (`-AddressFile`) → stage
   check (≥ 2 hex tokens, KUSER drop re-check, mismatch warn) → interceptor
   arm (`-Addresses <csv>`, gate re-verified before arming, window budgeted
   against the battle tail) → verdict with a durable `.capture.json`
   promoted next to the result (FRESH36 lesson). Privacy: aggregate counts +
   module-RVA sites only.

3. **`tmpwotb-e2e/test-od-044-driver-logic.ps1`** — offline probe (17
   checks): AST-extracted `ConvertTo-HexToken`, KUSER drop, and the
   write-site RIP → module RVA resolution.

## What the probe caught (real bug, fixed)

The driver's first version resolved a write-site RIP by **first module whose
base ≤ RIP** — which mis-attributes a `VCRUNTIME140.dll` write (RIP
`0x6F10E8AE`) to `wotblitz.exe` (base `0x400000` is also ≤ RIP, listed
first). Fixed to the module with the **highest base ≤ RIP** (the module that
actually contains the address). The probe's CRT-attribution test is
load-bearing.

## Validation

- Driver parses clean under PowerShell 5.1 and 7.
- PSSA gate: 0 findings on the new script (117 files scanned, 0
  violations).
- Probe: 17/17 pass.
- The interceptor itself remains proven by `test-offline-write-observation.ps1`;
  a full synthetic-counter run through the driver needs a live gated host
  (the driver's gate wait is a live precondition).

## Next (approval-gated)

One approved live launch: `scripts/launch-offline-replay-for-od.ps1` →
`scripts/invoke-od-044-replaytime-session.ps1 -TargetSurvivors 10`. If the
capture shows CRT-copy RIPs, a second session with `-ArmSourceOnFirstHit`
(FRESH43 dynamic source-arm). Alternative: the HP live session (pre-staged in
`docs/operations/record-diffing-groundwork.md`).
