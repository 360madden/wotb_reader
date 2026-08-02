# Session handoff — 2026-08-02: OD-RECOVERY-012 replayTime Partial

**Author:** Codex Agent

**Branch:** `main`

**Baseline:** `e3c3270` (Watch Offline standing rule)

**Commit unit:** live OD-RECOVERY-012 closeout — ledger, workflow, BLK-0022
    amendment, offsets notes, this handoff. Not pushed unless requested.

## Outcome

`OD-RECOVERY-012` is **Partial** (strongest dynamic anchor so far).

- Agent clicked **WATCH OFFLINE** → `OfflineReplayVerified`.
- **replayTime** Double increased filter: **increased=1**,
  `addressKind=private-mapping` (heap-dynamic pending root).
- **playerHP** Int32 [100,3500] unchanged-during-move: unchanged≈**4441**,
  returned sample `mapped-mapping`.
- CE write-BP (3 targets): **hitCount=0**.
- Absolute/truncated pointer AOB on the RT survivor: **0** hits.
- Session discarded; cleaned.

No field promoted (offset stays 0). Next: **OD-RECOVERY-013** — second
independent launch to reproduce the unique increased Double; then
neighborhood/root or interactive debugger (BLK-0019).

## Next move

1. Commit this closeout.
2. Immediately start OD-013 second launch for RT reproducibility.
3. Second distinct replay still required before promotion.
