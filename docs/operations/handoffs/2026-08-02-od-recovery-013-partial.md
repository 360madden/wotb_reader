# Session handoff — 2026-08-02: OD-RECOVERY-013 second-launch RT Partial

**Author:** Codex Agent

**Branch:** `main`

**Baseline:** `c160030` (OD-RECOVERY-012)

**Commit unit:** live OD-RECOVERY-013 closeout — ledger, workflow, BLK-0022
    amendment, offsets notes, this handoff. Not pushed unless requested.

## Outcome

`OD-RECOVERY-013` is **Partial**.

- Fresh Host.Web child (distinct PID); agent clicked **WATCH OFFLINE** →
  `OfflineReplayVerified`.
- `replayTime` Double increased + rollingBaseline:
  **193 → 60 → 15 → 4** (`private-mapping`).
- Same source replay artifact as OD-012 → `independentReplays` still not met
  (BLK-0019).
- Session discarded; cleaned.

No field promoted. Next: **OD-RECOVERY-014** — root/neighborhood/interactive
debugger on the ≤4 set; obtain a second distinct replay when available.

## Next move

1. Commit this closeout.
2. OD-014 classify/root the ≤4 rolling survivors.
3. Second distinct replay before promotion.
