# Session handoff — 2026-08-02: OD-RECOVERY-007 image-root NoSignal

**Author:** Codex Agent

**Branch:** `main`

**Baseline:** `6e31eb4` (`feat(scanner): add ImageRegionsOnly for module-static root probes`)

**Commit unit:** live OD-RECOVERY-007 closeout — ledger, workflow, this handoff,
    optional memory-offsets notes sync. Not pushed unless requested.

## Outcome

`OD-RECOVERY-007` is **NoSignal for MEM_IMAGE absolute pointer roots** (tooling
Partial for soft-cap + `ImageRegionsOnly`).

- Host.Web-managed child → `OfflineReplayVerified` after owner-authorized Watch
  Offline click.
- Soft-cap snapshot (`maxBytes=64MiB`, no address window): A≈**14,469,841**.
- A→B: changed≈**201,129** / unchanged≈**14,268,712**; returned 100 all
  `private-mapping` (noisier than windowed OD-006 ~434).
- Image-only pointer AOB (`includeImageRegions` + `imageRegionsOnly`) on **12**
  survivors: **0** hits.
- Session discarded; game/host/9182 cleaned.

No field promoted. Next: **OD-RECOVERY-008** (CE/x64dbg structural or
multi-level/encoded root). Do not repeat absolute image-only pointer AOB
unchanged.

## Next move

1. Commit this closeout with ledger/workflow.
2. OD-RECOVERY-008 under offline CE/x64dbg or a changed root hypothesis.
3. Prefer bounded windows for A→B; soft-cap for recon only.
4. Second distinct replay before promotion (BLK-0019).
