# WotB Treader Repository Guidelines

## Project status

WotB Treader is a Windows-first .NET 10 modular monolith for evidence-backed
World of Tanks Blitz offline replay telemetry. The alpha is deliberately local:
it has no runtime AI, cloud, Python, Node.js, Rust, Electron, or container
dependency.

## Working agreements

- Keep changes focused on the requested task.
- Preserve existing user changes and avoid destructive Git operations.
- Prefer small, reviewable commits with clear messages.
- Add or update tests when behavior changes.
- Keep secrets out of the repository; document required variables in `.env.example`.
- Update `README.md` when setup, build, test, or run instructions change.
- Commit locally on `main` as `Codex Agent <codex@local.invalid>`; never push
  unless the user explicitly changes that instruction.
- The lead agent owns shared contracts, solution configuration, migrations,
  integration, documentation, validation, staging, and commits. Subagents own
  only their assigned projects and must not stage or commit.
- Before a subagent changes a shared contract, it must report the proposed
  change to the lead. Handoffs list changed files, contracts, tests, commands,
  assumptions, unknowns, and integration risks.

## Architecture boundaries

- `Core` is pure domain code and has no project dependencies.
- `Application` contains orchestration and ports and references only `Core`.
- Source and storage adapters reference `Application` and `Core`.
- `Bootstrap` is the composition root shared by executable hosts.
- Hosts reference `Bootstrap`; the WPF overlay is a loopback web client and
  must not reference parser or storage projects.
- Replay decoding is evidence-first. Unknown values stay unknown, unknown
  binary records retain byte-range/hash provenance, and reprocessing creates a
  new immutable decode run.

Architecture dependency tests must be updated when project boundaries change.

## Safety and privacy

- Only offline replay files and positively verified offline replay sessions are
  in scope. Never automate an online match.
- Game input is disabled by default, explicitly armed, allowlisted, bounded,
  audited, and denied unless process identity, foreground window, integrity
  level, and offline-replay log state pass.
- Never modify or redistribute the WotB installation or game-derived assets.
- Private replays, raw captures, screenshots, databases, logs, identifiers,
  diagnostic bundles, and installed tool binaries remain ignored.
- Do not infer bot status from a name. Use `unknown` without explicit evidence.
- Never log raw replay bytes, tokens, full paths, player names, account IDs,
  chat, or screenshots.

## Binary and parser rules

- Every parser has explicit limits for input size, entry count, decompression,
  allocations, recursion, field length, packet count, resynchronization,
  cancellation, and timeouts.
- Parse pickle as data only. Never execute pickle opcodes or import Python.
- Comments explain binary evidence, security limits, compatibility behavior,
  concurrency invariants, and Windows interop hazards; avoid narrative comments.
- Public extension points require XML documentation.

## Durable blocker records

Record every major blocker in `docs/operations/blocker-log.md` with an immutable
UTC timestamp, impact, evidence, cause, resolution, why that resolution was
chosen, validation, and prevention/follow-up. Append corrections rather than
silently rewriting history.

## Validation

Before a milestone commit, run its applicable formatter, analyzer, build, and
tests. Before handoff, run `scripts/validate.ps1` and report every check that
could not be run. CI uses only synthetic or explicitly sanitized fixtures;
private-game tests are opt-in and local.
