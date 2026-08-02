# Session handoff — 2026-08-02: OD-RECOVERY-006 partial root search

**Author:** Codex Agent

**Branch:** `main`

**Baseline:** `e2df161` (`docs(operations): record OD-RECOVERY-005 heap-dynamic partial`)

**Commit unit:** live OD-RECOVERY-006 trial closeout — ledger, workflow
    next-session protocol, and this handoff. Committed with the autonomous
    continuation; not pushed unless requested.

## Outcome

`OD-RECOVERY-006` is **Partial**.

- Research host with 120s evidence + lifecycle research bounds; Host.Web-managed
  child reached `OfflineReplayVerified` after owner-authorized Watch Offline
  click (lower-center dialog region).
- First A→B Float32 (bounds [-500, 500], 64 MiB window): previous≈**1,242,451**
  → changed≈**434** / unchanged≈**996,336** (truncated; 100 returned; **100/100
  `private-mapping`**).
- Pointer-byte root search: value discover rejected 8-byte patterns
  (`discover.invalid_value_width`). `/discover/pattern` on 8 survivors:
  **1/8 with hits**; hit address kinds **`private-mapping` only** (4 hits) —
  no image/module static root.
- Second A→B used only to refresh candidates for probes (changed≈1175);
  sessions discarded. Absolute addresses/values/session ids not committed.
- Game + research host stopped; 9182 free; temp probe files removed.

No field was promoted. Next session is **OD-RECOVERY-007** (module-image-scoped
static root search; second distinct replay still required — BLK-0019).

## Live notes

1. Owner authorization from the prior autonomous continuation covered
   foreground Watch Offline + Space pause/resume for this session.
2. Private→private AOB hits are not a durable pointer-chain root.
3. Unbounded `MaxBytes` still hard-fails; bounded windows remain required.

## Validation performed

- Live gate `OfflineReplayVerified` on Host.Web-managed child.
- Pattern probes completed under the lease; sessions DELETE succeeded.
- Cleanup: zero `wotblitz`, zero `WotBTreader.Host.Web`, nothing on 9182.

## Next move

1. `OD-RECOVERY-007`: image/module-scoped static root finding (or CE/x64dbg
   under offline rules) without committing absolute addresses.
2. Optional: soft-cap `MaxBytes` (truncate instead of `size_limit`).
3. Second distinct replay before any promotion review.
