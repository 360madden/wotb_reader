# Resolver-path consolidation plan adopted (2026-08-11)

**Status:** ✅ plan committed and pushed (tree clean, full gate green).

## What happened

The strategy question ("which read surface is canonical?") was settled in
discussion and written into the docs as an actionable, ordered checklist —
**plan only; no code/behavior changes** beyond the new doc, the roadmap
strategy note + cross-link, and this handoff.

## Decisions locked

1. **Replays = the live-mode test harness.** The memory track exists for the
   future live overlay; replays give deterministic ground truth,
   repeatability, a real game process, and no policy risk. Reads transfer
   as-is to live mode; only the `OfflineReplayVerified` gate is
   replay-specific.
2. **Resolver path (module-rooted chain resolution) is canonical.** Position
   is published and walkable (OD-RECOVERY-083/084); the legacy observation
   surface stays frozen + deprecated, never extended.
3. **Hardware-atomicity proof is ordered LAST** — it needs the batch
   read-surface design (X2) and per-frame read discipline to exist first.
4. Deferred: observation-contract promotion decision (resolver endpoints vs
   observation DTO) → the X2/X4 proposal; yaw stays quarantined.

## Deliverables

- `docs/operations/resolver-path-consolidation.md` — strategy, the 7-item
  ordered checklist (publish-as-chains → single walker → phase tolerance as
  standard → freeze/deprecate legacy → L1–L4 mapping → live-mode alignment →
  **hardware atomicity LAST**), the per-target `Type10<X>Layout` convention
  (hash-bound, replay+live variants, gated hops, double-read), and the
  decision log.
- `docs/operations/product-roadmap.md` — new "Strategy (2026-08-11)" section
  after Principles + Cross-links entry.

## Next (execution, separate sessions)

- Execute checklist items 1–4 (offline conventions + deprecation note).
- L-track live sessions (L1–L4) remain approval-gated; each maps onto the
  pipeline per the checklist table.
- Batch N-entity read rehearsal (item 6) before any hardware-atomicity work.
