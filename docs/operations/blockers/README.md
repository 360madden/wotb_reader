# Major blocker records

Major engineering blockers are recorded here so later maintainers and coding
agents can distinguish evidence-backed compatibility decisions from accidental
implementation details.

Every record must include:

- the first-observed and documented UTC timestamps;
- the affected milestone and user-visible impact;
- evidence and root cause, with sensitive values redacted;
- the resolution and why it was chosen;
- validation performed and any validation still pending;
- prevention or follow-up work; and
- links to superseding records when a later discovery changes the decision.

Do not record private replay paths, replay hashes, clan names, account
identifiers, chat, screenshots, credentials, or machine-specific secrets.
Reference stable error codes, tests, and public source paths instead. Player
names and bot status are public Wargaming statistics and may be recorded.

A blocker is major when it prevents a milestone, reveals an incorrect format or
security assumption, requires an architectural change, or invalidates a prior
acceptance result. Routine compiler errors with an obvious local correction do
not need standalone records unless they expose a recurring design hazard.

Records are append-only historical evidence. Correct an error by adding a dated
amendment or superseding record rather than rewriting what was known at the
time.

