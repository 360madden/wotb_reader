# Operations documentation

Index and conventions for the `docs/operations/` folder. These records are
the repository's append-only operational history: blocker decisions, session
handoffs, offset-discovery evidence, and the offset-discovery operating guide.

## Document map

| Document | Purpose |
|---|---|
| [`blocker-log.md`](blocker-log.md) | Main immutable blocker register (BLK-0001…0025). Append-only; correct with dated amendments. |
| [`blockers/`](blockers/README.md) | Deep-dive records for major blockers. Holds BLK-0008–0011 (replay-decoder) and a companion record for BLK-0007 (command-execution-gate). |
| [`cmd-wrapper-gotchas.md`](cmd-wrapper-gotchas.md) | Canonical catalogue of cmd.exe wrapper failure modes and the review checklist. |
| [`offset-discovery-workflow.md`](offset-discovery-workflow.md) | Timeboxed operating protocol: identity/safety gate, pivots, address-kind classification, next-session plan. |
| [`offset-discovery-strategy-v3.md`](offset-discovery-strategy-v3.md) | Superseded strategy decision (exact-value pause scan — blocked by its human-precision requirement; kept as history). |
| [`offset-discovery-strategy-v4.md`](offset-discovery-strategy-v4.md) | Current strategy decision: replay-guided trajectory correlation (stage → monitor → correlate), stop rules, guardrails. |
| [`offset-discovery-roadmap.md`](offset-discovery-roadmap.md) | Organized milestone plan (M0–M4) with session caps, exit criteria, and the descope gate. |
| [`offset-discovery-m1-m2-choreography.md`](offset-discovery-m1-m2-choreography.md) | M1→M2 same-launch live runbook: no-rewind constraint, phase sequence, timing budget, fail-closed edge-case table. |
| [`offset-discovery-ledger.md`](offset-discovery-ledger.md) | Append-only experiment ledger: status vocabulary, decision register, experiment index, session template, results. |
| [`offset-discovery-guide.md`](offset-discovery-guide.md) | Detailed tool reference: x64dbg, System Informer, Ghidra, offset file format, scanner API and CLI matrix. |
| [`replay-crosscheck.md`](replay-crosscheck.md) | Operator-run replay-decode cross-validation: when/how to run `crosscheck.cmd`, exit codes, known divergences. |
| [`handoffs/`](handoffs/README.md) | Dated session handoffs, `YYYY-MM-DD-<topic>.md`; the newest by date is current. Append-only. |

## Blocker-numbering convention

BLK numbers are assigned sequentially and are contiguous across the main log
**and** the `blockers/` deep-dives:

- `blocker-log.md` holds BLK-0001…0007 and BLK-0012…0025.
- `blockers/2026-07-26-replay-decoder.md` holds BLK-0008…0011 (the decoder
  deep-dive continues the main numbering).
- `blockers/2026-07-26-command-execution-gate.md` is a companion record for
  BLK-0007 (it documents the same blocker in more depth, not a new number).

`scripts/python/offline_check.py` enforces this: the gate fails if the union
of BLK headers across `blocker-log.md` and `blockers/*.md` is not exactly
`BLK-0001..N` with no gaps; if a number repeats within one file; or if two
deep-dives share a number without a main-log owner. A companion deep-dive
may repeat a main-log number once.

## Append-only rules

- **Blockers:** append `docs/operations/blocker-log.md` (immutable UTC). Correct
  an error with a dated amendment, never rewrite what was known at the time.
- **Handoffs:** append under `docs/operations/handoffs/` per the format in the
  handoff README. Correct with amendments, never rewrite.
- **Ledger:** append every discovery attempt (including partials and failures)
  to `offset-discovery-ledger.md`; record blocked results as `*-BLOCKED`.

## Privacy

Never record private replay paths, replay hashes, clan names, account
identifiers, chat, screenshots, credentials, or machine-specific secrets.
Player names and bot status are public Wargaming statistics and may be
recorded. Reference stable error codes, tests, and public source paths instead.
