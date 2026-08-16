# Rendezvous `web.json` file-level ACL hardening

**Date:** 2026-08-16 (UTC)

**Roadmap:** next-10-actions item 7 — security follow-up to BLK-0014

## Result

The rendezvous directory was already re-secured to a protected owner-only DACL
before every publish, and the temporary file the publisher renames inherited
that restriction. Two gaps remained at the *file* layer:

1. The published `web.json` carried only an *inherited* ACL, so it depended on
   the parent directory staying owner-only, and no explicit descriptor was
   pinned to the file.
2. The final file was never re-verified after the rename, so a reparse-point
   substitution or a weakened DACL at the final path would not fail the publish.

`LocalApplicationPaths` now exposes two cross-platform, tested helpers:

- `ProtectRendezvousFile(path)` — rejects a missing file or reparse point, then
  applies an explicit protected owner-only descriptor (Windows: current-user
  owner plus one non-inherited FullControl Allow ACE with inheritance severed;
  other platforms: mode `0600`) and positively re-reads it.
- `VerifyRendezvousFile(path)` — re-reads the final file after the rename and
  fails closed unless it is a real (non-reparse) file with exactly the
  protected owner-only DACL/mode above.

`RendezvousPublisher.PublishAsync` now calls `ProtectRendezvousFile` on the
temporary file before it becomes the record and `VerifyRendezvousFile` on the
final `web.json` after `File.Move`. A verification failure propagates through
the existing `IOException`/`UnauthorizedAccessException` catch, so the publish
fails closed and the lease is rotated again on the next cycle. The publisher
never accepts a published record it has not positively verified.

The capability is still never logged or persisted anywhere except the record
itself; no path, token, or ACL text enters diagnostics.

## Validation

- Bootstrap regression tests: a permissive inherited parent ACL is severed on
  the file; the post-move verifier accepts the freshly protected file, rejects
  a file carrying an extra WorldSid ACE, rejects a missing file, and rejects a
  symbolic-link reparse point where the environment can create one.
- `scripts/validate.ps1` passed end to end: 0 build warnings/errors, all test
  projects green (Bootstrap 21), repository/privacy scan, Codex policy,
  PowerShell hygiene, offline links/file-tree, blocker/ledger consistency, and
  offset schema/chains validation.

## Next

- Post-contract two-replay batch witness (item 7 in the renumbered list).
- HUD live visual ship review (owner-supervised).
