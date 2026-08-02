# Session handoff — 2026-08-02: OD-RECOVERY-005 partial classification

**Author:** Codex Agent

**Branch:** `main`

**Baseline:** `9931916` (`docs(operations): record OD-RECOVERY-004 partial aggregates`)

**Commit unit:** live OD-RECOVERY-005 trial closeout — ledger, workflow next-session
    protocol, BLK-0022 amendment, and this handoff. Left uncommitted unless the
    owner asks to commit. Not pushed.

## Outcome

`OD-RECOVERY-005` is **Partial**.

- Research host started with `Research__OfflineReplayEvidenceLifetimeSeconds=120`
  and `Research__LifecycleEvidenceTimeoutSeconds=120`.
- Managed launch produced a Host.Web-parented `wotblitz` that reached
  `OfflineReplayVerified` / `session.offline_replay_verified` after an
  owner-authorized foreground click on the Watch Offline dialog region.
- State A (Float32, bounds [-500, 500], bounded 64 MiB window): previous≈**757,016**.
- Owner-authorized Space pause → ~2.5 s resume → Space pause for state B.
- State B `changed` compare: previous=757,016, current=**905**, changed=**905**,
  unchanged=**756,111**, increased=495, decreased=409, truncated=true,
  returnedCandidates=100.
- Address-kind histogram on returned sample: **private-mapping=100** (0 other).
  Survivor set classified **`heap-dynamic` pending a stable root**.
- Scanner session discarded. Absolute addresses, values, and session ids were
  not committed. Game and research host stopped; port 9182 free.

No field was promoted. Next session is **OD-RECOVERY-006** (pointer-chain /
structural root into the private-mapping set; second distinct replay still
required — BLK-0019).

## Live notes

1. Win32 UI Automation exposed zero buttons (custom-rendered client). Watch
   Offline was activated by an owner-authorized click in the lower-center dialog
   region of the game window.
2. Pause/resume used Space on the foreground verified window under the same
   explicit owner authorization. The guarded GameHarness input adapter remains
   unregistered.
3. Unbounded `MaxBytes` still hard-fails; bounded windows remain required.
4. Narrowing improved vs OD-RECOVERY-004 (~26k → ~905 changed), consistent with
   a tighter pause/resume transition near match start.

## Validation performed

- Live gate: `OfflineReplayVerified` on the Host.Web-managed child.
- A→B compare completed; session DELETE succeeded.
- Cleanup confirmed: zero `wotblitz`, zero `WotBTreader.Host.Web`, nothing
  listening on 127.0.0.1:9182.

## Cleanup state

- Scanner session discarded.
- `wotblitz` count: zero.
- Research host stopped; 9182 free.
- Temp capability/session files removed; aggregate counts used only for this
  handoff/ledger.
- Nothing was pushed.

## Next move

1. Commit this closeout when the owner asks.
2. Start `OD-RECOVERY-006`: pointer-chain / structural root for private-mapping
   survivors; keep aggregates-only in the repo; require a second distinct replay
   before promotion.
3. Optionally soft-cap `MaxBytes` (truncate instead of `size_limit`).
