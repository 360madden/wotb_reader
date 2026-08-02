# Session handoff — 2026-08-02: OD-RECOVERY-004 partial A→B narrowing

**Author:** Codex Agent

**Branch:** `main`

**Baseline:** `a0f3753` (`docs(operations): index ops docs and enforce BLK numbering`)

**Commit unit:** live OD-RECOVERY-004 trial closeout — ledger/workflow/handoff plus
    GameHarness `discover-snapshot` `valueKind=Float` fix when float bounds are
    supplied. Not pushed.

## Outcome

`OD-RECOVERY-004` is **Partial**.

- An earlier attempt hit `EvidenceStale` after the 120 s research lease; the
  only live `wotblitz` was WGC-parented and was stopped.
- A clean relaunch produced a Host.Web-parented child that reached
  `OfflineReplayVerified` / `session.offline_replay_verified`.
- State A (Float32, bounds [-500, 500], bounded 64 MiB window via API):
  previous≈**1,229,051**.
- State B `changed` compare: previous=1,229,051, current=**26,284**,
  changed=**26,284**, unchanged=**792,666**.
- Scanner session discarded; no addresses, values, or session ids were
  committed. Game process count after stop: zero.

No field was promoted. Next session is **OD-RECOVERY-005** (structural
classification of survivors with an explicit operator state-B ack).

## Live defects found

1. `discover-snapshot` omitted `valueKind`, so the host defaulted to **Int32**
   and ignored `--float-min`/`--float-max`. The trial used the loopback API
   with `valueKind=Float`. The CLI now sends `Float` when float bounds are set.
2. Unbounded `MaxBytes` still returns `discover.snapshot.size_limit` instead of
   completing a truncated budgeted snapshot, so a bounded window was required.
3. WGC can spawn a parallel `wotblitz` that steals operator attention; only a
   Host.Web-parented child is admissible for the research lease.

## Validation performed

- Live gate: `OfflineReplayVerified` on the managed relaunch.
- Aggregate A→B compare completed; session DELETE succeeded.
- Focused GameHarness tests after the `valueKind` fix (run before commit).

## Cleanup state

- Scanner session discarded.
- `wotblitz` count after stop: zero.
- Research host may still be running locally; stop it before the next cold start
  if a fresh research env is required.
- Nothing was pushed.

## Next move

1. Commit this closeout (`Codex Agent`) when the owner asks.
2. Start `OD-RECOVERY-005`: reproduce narrowing, classify address kind, keep
   aggregates-only in the ledger, require a second distinct replay before
   promotion (BLK-0019).
3. Optionally make `MaxBytes` truncate-at-budget instead of hard-failing so
   unbounded private/mapped campaigns need no address windows.
