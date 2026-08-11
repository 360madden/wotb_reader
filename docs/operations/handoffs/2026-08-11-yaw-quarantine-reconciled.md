# Handoff — yaw quarantine resolved-by-supersession + driver hardening (2026-08-11)

## Summary

Closed the last open offline item in the resolver-path consolidation decision
log: the `playerYaw` offset quarantine is **resolved-by-supersession** — not
by choosing one of the three conflicting legacy numbers, but by answering the
address-kind question the quarantine was waiting on. Also hardened the batch
rehearsal driver (fail-closed on a 0-resolved batch).

## Yaw quarantine reconciliation

The three legacy candidates were mutually inconsistent on their own terms
(the prior table's decimal `51808784` = `0x03168A10` does not convert to its
own notes hex `0x0317A810` = `51882000`; the Ghidra export's `56085200` /
`0x0357CAD0` is a third value), so none could ever be a trusted scan anchor —
the quarantine's rationale stands. The L2 facing track (2026-08-10/11)
answers the premise: **yaw is a runtime chain field on the movement ring
record** (`+0x2C`, ring stride 0x38, position `+0x10`, velocity `+0x28`),
reachable only through the module-rooted entity chain — the same reason
position moved to `chains` in G0. Static module-offset candidates are the
wrong address kind for this field by construction.

Status: legacy static candidates **retired**; the yaw anchor becomes the
ring-record `+0x2C` chain field, predicted and rehearsed by the facing
correlator against packet-derived yaw ground truth (`position_samples.yaw`):
27/27 turn windows on Oasis Palms, 35/35 on Dead Rail, score 1.0, flatness
1.0 (synthetic dumps — the live L2 facing session via the `entity-region`
ring-record seam confirms it). Published table keeps yaw at `0`/Stale;
a future publication would be a `chains` entry per the consolidation plan.

Docs updated: `offset-discovery-ledger.md` (status row + dated reconciliation
note), `offset-discovery-workflow.md` (quarantine paragraph), and the
consolidation plan's decision log.

## Driver hardening

`scripts/invoke-batch-rehearsal.ps1` now fails closed when a batch resolves
0 entities (wrong session/roster, or the battle not active) with a precise
message instead of writing an all-failure dumps file and reaching a
confusing NO-VERDICT. Exit paths re-verified: 0 (clean), 1 (miss +
FailOnMiss), 3 (no dumps/no acquire).

## Files touched

- `docs/operations/offset-discovery-ledger.md`
- `docs/operations/offset-discovery-workflow.md`
- `docs/operations/resolver-path-consolidation.md`
- `scripts/invoke-batch-rehearsal.ps1`

## Verification

- Driver parse + exit paths 0/1/3 re-verified; PSScriptAnalyzer clean on the
  touched script.
- Full `scripts/validate.ps1` gate green (936 passed, 3 local opt-in skips,
  0 warnings, 0 errors); offline pack + ledger consistency refreshed.

## Remaining

- The live L2 facing session confirms ring-record `+0x2C` (prediction already
  rehearsed on both replays) — same approval gate as every other live step.
- Everything else on the consolidation checklist stays as recorded: items
  1-4 done, item 6 staged, item 7 LAST.
