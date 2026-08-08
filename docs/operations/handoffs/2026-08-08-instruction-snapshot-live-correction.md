# Instruction snapshot live correction (2026-08-08)

Outcome: the instruction-first mechanism is live-proven and two member-layout
assumptions are now closed. No offset was promoted. All game, Host, helper, and
debugger processes were stopped after each result.

## Live sequence

All attempts used a freshly published helper, passed the synthetic x86 test,
launched a new coordinator-managed replay, and reached
`OfflineReplayVerified` before memory access.

1. The first capture returned a cleanup-proven helper failure. The runner was
   found to discard the helper's stable diagnostic list.
2. A fixed allowlist projection exposed
   `thread_bound_or_target_invalid`. Aggregate inspection measured 164 game
   threads against the 128-thread complete-coverage cap. No value evidence was
   accepted.
3. The cap was raised to 256 while retaining one execute breakpoint, five
   seconds, 64 accepted hits, one 12-byte read per hit, and a 64 KiB report.
4. The original EBX+`0x1C/+0x20/+0x24` read then completed with seven finite
   hits from one opaque object. Every vector was exactly `(1,1,1)`.
5. Hash-verified `FUN_00d1a0f0` was re-read. It copies
   EBX+`0x10/+0x14/+0x18` into local-matrix translation and consumes
   EBX+`0x1C/+0x20/+0x24` as scale. The old position interpretation was wrong.
6. A provenance-corrected EBX+`0x10` capture completed with seven changing,
   finite vectors from one opaque object. Fingerprint and cleanup were proven.

## Decoded comparison

The EBX+`0x1C` result is conclusively scale: the decoded viewpoint has 2,812
samples, moves substantially on all axes, and never equals or comes within six
units on all axes of `(1,1,1)` anywhere in the replay.

For the EBX+`0x10` result, offline comparison tested the seven captured vectors
against all 26,822 decoded position samples, every decoded participant, all six
axis permutations, and all eight sign conventions. There was no exact match.
The best time-agnostic viewpoint mapping remained 7.374 units mean / 10.272
units maximum from its nearest samples. This proves a changing local
translation with register/member provenance, not viewpoint identity or decoded
clock identity.

## Durable correction

The hash-verified matrix path now reads as:

```text
EBX + 0x10/0x14/0x18  local translation
EBX + 0x1C/0x20/0x24  local scale (live value 1,1,1)
FUN_00d1a0f0           builds local matrix; translation row +0x30
FUN_00729570           composes parent and local matrices
EBX + 0x60..0x9C       stored composed world matrix
EBX + 0x90/0x94/0x98  composed translation row (next hypothesis)
```

The production helper is now pinned to EBX+`0x90`. GameHarness also prints
capture start/finish UTC and per-hit UTC, allowing the next result to be aligned
to decoded replay time rather than compared only by whole-session proximity.

## Safety and evidence limits

- Production still has no raw PID, address, register, module, RVA, or
  displacement input.
- The target remains exact version/hash/module/RVA/instruction-byte pinned.
- Parent EXE+DLL identity, owner-only manifest, post-attach process identity,
  live authorization cancellation, exact debug-register restore, and
  kill-on-debugger-exit containment remain mandatory.
- Public output remains opaque-object keys, UTC, values, and conservative proof
  flags. It contains no process/object address, path, token, account/player
  data, chat, screenshot, or raw replay bytes.
- Hardware atomicity, viewpoint identity, same decoded clock, stable root, and
  offset publication all remain false.

## Validation

- Focused diagnostic policy/target policy tests: pass.
- Controlled helper publish: pass after each source change.
- Synthetic execute-breakpoint capture and cleanup test: pass with the
  256-thread bound and corrected displacement.
- Full `scripts/validate.ps1`: pass (Release build with zero warnings/errors,
  631 tests passed, 2 installed-game tests skipped, repository scan,
  PowerShell analyzer, and offline pack/link/ledger checks).

## Next: OD-RECOVERY-066

Run one freshly published, five-second capture under a new
`OfflineReplayVerified` managed replay. The fixed read is
EBX+`0x90/+0x94/+0x98`. Preserve UTC, align the one opaque-object trajectory to
decoded ground truth, and stop after the result. Only a timestamp-aligned match
permits repeating that exact instruction/member relationship on the other
independent replay. Do not fall back to broad scans, delayed tracing, raw-PID
attach, or offset promotion.
