# WotB Treader — agent entry

Windows-first .NET 10 modular monolith for **offline** WoTB replay telemetry.
No runtime AI, cloud, Python, Node.js, Rust, Electron, or containers.

Cursor layout and asset index: [`.cursor/README.md`](.cursor/README.md).
Do **not** load `.cursor/reference/*` unless the task needs that catalog.

## Commands (exact)

- SDK pinned by `global.json` to 10.0.302. Package versions are central in `Directory.Packages.props` with committed lock files; restore is always `--locked-mode`.
- Full gate, required before milestone commits: `scripts/validate.ps1` — locked restore → `dotnet format --verify-no-changes` → Release build → all tests → `scripts/scan-repository.ps1` (secret + ignore-policy scan). Add `-AuditPackages` for the transitive vulnerability audit.
- Single test project: `dotnet test tests/WotBTreader.Core.Tests -c Release`. Focused filter: add `--filter "FullyQualifiedName~SomeTest"`. Tests are MSTest 4 on Microsoft.Testing.Platform; a few installed-game tests are local opt-in and skip by default.
- Warnings are errors (`TreatWarningsAsErrors`), and `NuGetAuditMode=all` fails restore on vulnerable transitive packages — fix with a central pin, never suppress (BLK-0002).

## Architecture (enforced by `tests/WotBTreader.Architecture.Tests`)

- `Core`: no project refs. `Application` → `Core` only. Adapters (`Replays`, `CaptureLogs`, `GameIntegration`, `Storage.Sqlite`) → `Application`+`Core`, never each other. `Bootstrap` is the composition root; hosts reference `Bootstrap`. `Overlay` (WPF/WebView2) is a loopback web client — no parser/storage refs. **The overlay is designed to be a transparent, borderless, topmost HUD that sits on top of the WoT Blitz game during replay playback. It is NOT a generic session viewer. See `docs/architecture/overview.md` for the full design spec.**
- Only `Overlay` and `tools/GameHarness` target `net10.0-windows`; keep everything else on portable `net10.0` (BLK-0003).
- Any new DI port must be added to the published-port list in `CompositionRootTests`, or the solution can compile and unit-test green yet no host starts (BLK-0013).
- Diagram, evidence lifecycle, and loopback trust boundary: `docs/architecture/overview.md`.

## Hard constraints (always)

- Offline / positively verified offline sessions only. Never automate online matches.
- Never infer bot status from a name; use `unknown` without evidence.
- Never log raw replay bytes, tokens, full paths, player names, account IDs, chat, screenshots.
- Never modify/redistribute the WotB install or game-derived assets.
- `Core` has no project refs; `Application` → `Core` only; overlay is loopback web client (no parser/storage refs).
- Evidence-first decode: unknown stays unknown; reprocess = new immutable decode run.
- Pickle = data only; never execute opcodes / import Python.
- Focused diffs; no destructive git; no secrets in repo.
- Commits as `Codex Agent <codex@local.invalid>` unless user says otherwise.
- Push only when the user asks; never force-push.
- Lead stages/commits; subagents must not. Propose shared-contract changes before editing them.
- Milestone: format/analyzers/build/tests. Handoff: `scripts/validate.ps1`.
- CI: synthetic fixtures only; private-game tests opt-in local.
- Blockers: append `docs/operations/blocker-log.md` (immutable UTC). Handoffs: append under `docs/operations/handoffs/`.

## Repo gotchas (each has bitten before)

- `.gitignore` unanchored patterns (`*.sqlite`, `diagnostics/`, `dist/`) match **case-insensitively on Windows** and have hidden real source folders (BLK-0005, BLK-0012). `scan-repository.ps1` fails validation if any ignored file exists under `src`, `tests`, `tools/src`, `scripts`, or `docs` — add explicit `!` unignore rules when creating paths that collide with runtime-data patterns.
- In `validate.ps1`, route every native command through `Invoke-CheckedNative`; `$ErrorActionPreference='Stop'` does not catch non-zero exit codes, and the script once returned success after failed phases (BLK-0006).
- Fixtures: synthetic only in CI. Private replays, captures, DBs, and screenshots stay in ignored paths and are never committed; the full sanitization process is `docs/testing/fixture-policy.md`.

## Route by task

| Task | Load |
|------|------|
| Cursor harness / model routing | `.cursor/README.md`, `.cursor/reference/model-routing.md` |
| Architecture / project refs | rule `architecture-boundaries` (auto on `src/**/*.cs`) |
| Replay / binary / harness tools | rule `binary-parser`; agent `decoder-auditor` |
| Loopback / mutation / privacy audit | agent `security-auditor` (readonly) |
| Validate / commit / handoff | skills `validate`, `handoff-amend`, `commit-unit` |
| UI / DTO / smoke / docs glue | agent `implementer-glue` (fast) |
| Prove work after a unit | agent `verifier` (fast) |
| Human setup | `README.md` |

## Delegation (OpenCode sessions)

- Use `explore` subagents for multi-file search/read rounds and `general` subagents for mechanical units against frozen contracts (UI/DTO/tests/docs). Subagents must not stage, commit, or push.
- Keep on the lead model: replay/binary/decoder decisions, loopback/mutation/privacy review, and shared-contract changes.
- The Cursor role briefs in `.cursor/agents/*.md` (decoder-auditor, security-auditor, implementer-glue, verifier) are worth pasting into delegation prompts for hard tasks; attach `.cursor/rules/binary-parser.mdc` or `.cursor/rules/safety-privacy.mdc` as task rules.

## Model stance (one line)

Opus/Fable high|xhigh for decoder/security/hard contracts only; Grok/Composer/fast for glue, explore, verify. Details: `.cursor/reference/model-routing.md`.
