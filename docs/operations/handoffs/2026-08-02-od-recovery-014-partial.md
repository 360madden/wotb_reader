# Session handoff — 2026-08-02: OD-RECOVERY-014 neighborhood Partial

**Author:** Codex Agent

**Branch:** `main`

**Baseline:** `b715fae` (OD-RECOVERY-013)

**Commit unit:** live OD-RECOVERY-014 closeout — ledger, workflow, BLK-0022
    amendment, this handoff. Not pushed unless requested.

## Outcome

`OD-RECOVERY-014` is **Partial**.

- Agent **WATCH OFFLINE** → verified.
- Rolling RT Doubles to ≤10 (`private-mapping`).
- Neighborhood on `relativeOffset`: **4/4 OK**, dense ~1253 aggregate hits
  (noisy).
- Pointer AOB: unstable (1 hit then 0 on rebuild).
- Cleaned.

No promotion. Next: **OD-RECOVERY-015** — interactive debugger or second
distinct replay.

## Next move

1. Commit this closeout.
2. OD-015 interactive root or second replay when available.
