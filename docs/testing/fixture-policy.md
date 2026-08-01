# Replay fixture policy

Last updated: 2026-07-26

Private replays are local compatibility inputs and must remain outside tracked
paths. CI uses synthetic fixtures and a single redacted full replay only after
the sanitizer passes every required check.

For a candidate full replay fixture:

1. Work from the smallest suitable private replay in an ignored temporary
   directory outside tracked paths.
2. Deterministically replace clans, account/database identifiers, arena
   identity, timestamps, chat, and every known binary encoding while retaining
   the full packet timeline. Player names are public Wargaming statistics and
   do not require replacement.
3. Scan archive bytes and decompressed entries against a private denylist in
   UTF-8, UTF-16LE/BE, decimal text, fixed-width integers, varints, and
   supported pickle encodings.
4. Decode the result and compare it with a pseudonymous expected-output
   manifest.
5. Commit only the sanitized replay, sanitized hash, expected manifest, format
   provenance, permission statement, and residual-risk notice.

Never record the original path, original hash, or private identifier list in
Git, logs, test output, or CI artifacts. A remaining denylist token blocks the
fixture commit. The fixture has a separate notice and is excluded from the
project MIT license.
