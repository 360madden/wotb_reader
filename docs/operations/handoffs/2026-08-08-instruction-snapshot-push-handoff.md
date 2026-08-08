# Instruction snapshot publication handoff (2026-08-08)

Outcome: the validated instruction-first discovery pivot and its live layout
correction are ready for publication to `origin/main`. This handoff adds no
runtime behavior and performs no live game access.

## Repository state before this handoff

- Branch: `main`.
- HEAD: `ac404dc` (`feat(od): correct instruction snapshot layout`).
- The working tree was clean.
- Local `main` was three commits ahead of `origin/main` and zero commits behind.
- Publication target: a normal, non-force push to `origin/main`.

The three completed units queued before this handoff are:

1. `9b08a22` - immediate position-triple hypothesis validation.
2. `ea40204` - instruction-first discovery mechanism and durable policy pivot.
3. `ac404dc` - live layout correction, bounded helper hardening, and next
   world-translation hypothesis.

## Delivered discovery state

- The coordinator-managed instruction snapshot mechanism is live-proven under
  `OfflineReplayVerified` authorization.
- The EBX+`0x1C/+0x20/+0x24` triple is scale, observed as `(1,1,1)`, not player
  position.
- EBX+`0x10/+0x14/+0x18` is a changing local translation, but the captured
  samples did not establish viewpoint identity or decoded-clock identity.
- Static composition evidence identifies EBX+`0x90/+0x94/+0x98` as the next
  bounded world-matrix translation hypothesis.
- Capture and per-hit UTC are now exposed for the next decoded-time alignment.
- No offset was promoted. The offset table remains unchanged, and hardware
  atomicity, viewpoint identity, same decoded clock, and stable root remain
  unproven.

The detailed live evidence, safety contract, and correction are preserved in
[`2026-08-08-instruction-snapshot-live-correction.md`](2026-08-08-instruction-snapshot-live-correction.md).

## Validation carried forward

The `ac404dc` unit completed the full `scripts/validate.ps1` gate:

- Release build: zero warnings and zero errors.
- Automated tests: 631 passed; 2 installed-game opt-in tests skipped.
- Repository secret and ignore-policy scan: pass.
- PowerShell analyzer and custom-rule gate: pass.
- Offline pack freshness, link, and ledger checks: pass.

The controlled helper publish and synthetic execute-breakpoint capture/cleanup
test also passed after the final source correction. The documentation-only
handoff repeated the full repository validation gate before publication with
the same result: 631 tests passed, 2 expected opt-in tests skipped, and every
remaining phase passed.

## Next admissible live proof: OD-RECOVERY-066

Run one freshly published, five-second capture under a new
`OfflineReplayVerified` coordinator-managed replay. Read only the fixed
EBX+`0x90/+0x94/+0x98` triple, preserve capture and hit UTC, align the one
opaque-object trajectory to decoded ground truth, and stop after the result.

Only a timestamp-aligned match permits repeating that exact
instruction/member relationship on the second independent replay. Do not
resume broad scans, delayed tracing, raw-PID attachment, or offset promotion.

## Publication note

This handoff records the state queued for `origin/main`. The push result and
the resulting publication commit are reported by the task that performs the
push; this document does not claim remote success in advance.
