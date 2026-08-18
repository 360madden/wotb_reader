# Batch rendezvous + post-contract witness hardening

**Date:** 2026-08-14 (UTC)

**Status:** offline prerequisite complete; post-contract live pass remains

## Scope boundary

This hardening is Item-7 offset-discovery infrastructure. It does **not**
advance the penetration UI feature. The penetration prototype and its
two-replay evidence are already committed in `1a899eb`; the numeric arena
minimap follow-up is committed in `ceced00`. The remaining PN action is the
owner ship review and manual HUD smoke documented in
`2026-08-14-pn4-second-replay-regression.md`.

This batch change was already implemented and fully validated when the
workflow was re-anchored. It is being checkpointed intact, but must not be
cited as penetration-feature progress.

## Result

The savanna live batch schedule in OD-RECOVERY-100 lost one run when the host's
rendezvous publisher briefly replaced its JSON file. The driver treated that
single missing read as definitive and threw `rendezvous_unavailable` even
though the same host and game were still healthy immediately afterward.

The live-support helper now performs a bounded five-attempt / 100 ms retry.
Every candidate still must be fresh, HTTP loopback-only, and carry both a base
URI and capability; exhaustion still fails closed. No capability or path is
persisted.

The same helper validates the owner-approved batch witness before any dump is
written:

- all six post-contract fields must exist with the expected types;
- every resolved item must carry `ConsistentDoubleRead=true` and at least one
  region attempt;
- an unresolved item may not claim the stable-pair flag;
- a tear flag requires at least two attempts;
- the retained aggregate contains counts/maxima only, never entity ids,
  addresses, region bytes, paths, or capability material.

This deliberately accepts and records a torn-then-settled read. The later live
verdict—not the driver—requires zero tears and attempt count one.

## Validation before milestone gate

- Batch measurement + live-support Pester suite: 9/9 passed.
- Synthetic coverage: transient third-attempt recovery, remote URI rejection,
  stable/torn aggregate, old-host missing fields, and false stable-witness
  rejection.
- No host or game process was started.

Milestone gate:

- `scripts/validate.ps1`: passed;
- Release build: 0 warnings, 0 errors;
- tests: 1,206 passed, 7 local opt-in skips, 0 failed;
- completion/replay-selection/camera/batch PowerShell suites: 14/14, 16/16,
  4/4, 9/9;
- repository scan, script hygiene, offline pack, blocker/ledger consistency,
  and offset schema/chains checks: passed.

## Next

Resume with the PN owner ship review, which is now first in the durable action
list. When Item 7 resumes, run the post-contract two-replay batch pass only
after the PowerShell 7 marker normalization and stored savanna clock-window
re-verdict. Require the retained witness aggregates to show attempts one and
zero tears for every resolved item before changing
`HardwareAtomicReadProven`.
