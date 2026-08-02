# Session handoff — 2026-08-02: OD-RECOVERY-011 second-pass + CE Partial

**Author:** Codex Agent

**Branch:** `main`

**Baseline:** `2f0eabb` (OD-RECOVERY-010 Partial)

**Commit unit:** live OD-RECOVERY-011 closeout — ledger, workflow, BLK-0022
    amendment, offsets notes, this handoff. Not pushed unless requested.

## Outcome

`OD-RECOVERY-011` is **Partial**.

- Host.Web-managed child; **WATCH OFFLINE** click required on the not-logged-in
  dialog → `OfflineReplayVerified` (never LOG IN AND WATCH).
- Second-pass Float: probe changed≈**3129** → pass1≈**2899** → pass2≈**1929**
  (private-mapping candidates returned).
- CE Windows debugger: **3** `bptWrite` breakpoints set (list count 3);
  overlapping Space resume → **hitCount=0**.
- Session discarded; game/host/CE cleaned.

No field promoted. Next: **OD-RECOVERY-012** (HP/replayTime pivot or
interactive x64dbg/CE GUI). Do not repeat automated CE write-BP on Float
survivors expecting RIP alone.

## Next move

1. Commit this closeout.
2. OD-RECOVERY-012 field pivot or interactive debugger.
3. Second distinct replay before promotion (BLK-0019).
