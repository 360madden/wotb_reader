# WotB Treader — agent entry

Windows-first .NET 10 modular monolith for **offline** WoTB replay telemetry.
No runtime AI, cloud, Python, Node.js, Rust, Electron, or containers.

## Project owner context

- The project owner identifies as a junior developer at Wargaming.net.
- This is user-provided background for agents working in the repository. The
  repository remains a personal project unless official status is documented
  separately.
- Canonical wording: [`docs/project-context.md`](docs/project-context.md).

Cursor layout and asset index: [`.cursor/README.md`](.cursor/README.md).
Do **not** load `.cursor/reference/*` unless the task needs that catalog.

Offline discovery: a focused, self-contained repo index lives in
[`offline/`](offline/README.md) (repo map, entry points, API surface, glossary,
commands, replay format, offset discovery, data flow, memory-offset evidence,
file-tree snapshot). Load `offline/README.md` to re-orient fast without scanning
the tree; it links to canonical docs instead of duplicating them. Keep it in
sync whenever the layout, API surface, or commands change — and run
`python scripts/python/offline_check.py` after editing the pack (wired into
`scripts/validate.ps1` and CI).

## Commands (exact)

- SDK pinned by `global.json` to 10.0.302. Package versions are central in `Directory.Packages.props` with committed lock files; restore is always `--locked-mode`.
- Full gate, required before milestone commits: `scripts/validate.ps1` — locked restore → `dotnet format --verify-no-changes` → Release build → all tests → `scripts/scan-repository.ps1` (secret + ignore-policy scan). Add `-AuditPackages` for the transitive vulnerability audit.
- Single test project: `dotnet test tests/WotBTreader.Core.Tests -c Release`. Focused filter: add `--filter "FullyQualifiedName~SomeTest"`. Tests are MSTest 4 on Microsoft.Testing.Platform; a few installed-game tests are local opt-in and skip by default.
- Warnings are errors (`TreatWarningsAsErrors`), and `NuGetAuditMode=all` fails restore on vulnerable transitive packages — fix with a central pin, never suppress (BLK-0002).

## Architecture (enforced by `tests/WotBTreader.Architecture.Tests`)

- `Core`: no project refs. `Application` → `Core` only. Adapters (`Replays`, `CaptureLogs`, `GameIntegration`, `Storage.Sqlite`) → `Application`+`Core`, never each other. `Bootstrap` is the composition root; hosts reference `Bootstrap`. `Overlay` (WPF) is a loopback web client — no parser/storage refs. **The overlay is designed to be a transparent, borderless, topmost HUD that sits on top of the WoT Blitz game during replay playback. It is NOT a generic session viewer. See `docs/architecture/overview.md` for the full design spec.**
- Only `Overlay` and `tools/GameHarness` target `net10.0-windows`; keep everything else on portable `net10.0` (BLK-0003).
- Any new DI port must be added to the published-port list in `CompositionRootTests`, or the solution can compile and unit-test green yet no host starts (BLK-0013).
- Diagram, evidence lifecycle, and loopback trust boundary: `docs/architecture/overview.md`.

## Hard constraints (always)

- Offline / positively verified offline sessions only. Never automate online matches.
- Cheat Engine 7.7 is an approved local diagnostic tool for dynamic memory
  analysis, offset discovery, and other repository tasks where it is a
  practical fit, but only during positively verified offline replay sessions.
  Never attach it during an online match, and never commit scan files, memory
  dumps, pointer maps, or game-derived data.
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
- **cmd.exe wrapper scripts** have well-known failure modes (delayed expansion + `!` in filenames, unquoted `%~dp0` in nested `cmd /c`, whitespace input → arithmetic crash, env var leaking). Full catalogue and review checklist in `docs/operations/cmd-wrapper-gotchas.md`. Any non-trivial cmd/batch review **must** be routed through a thinker agent with the actual file contents — do not rely on manual reading.
- **Basher timeouts waste time on every session.** Never use the default 30s timeout for .NET commands. Minimums: build → 300s, full test suite → 300s, single test project → 120s, publish → 180s. Never run interactive `.cmd` wrappers through basher (import, everything, serve) — they expect TTY or spawn windows. Use direct `dotnet build` / `dotnet test` / `dotnet publish` instead. Always verify prerequisites (CLI built, packages restored) before running dependents.

## Route by task

| Task | Load |
|------|------|
| Cursor harness / model routing | `.cursor/README.md`, `.cursor/reference/model-routing.md`; local read-only CLI adapter: `scripts/invoke-cursor-agent.ps1` |
| Replay format / decode internals | `offline/replay-format.md` |
| Telemetry data flow (decode → UI / comparison) | `offline/data-flow.md` |
| Offset / memory evidence | `offline/memory-offsets.md`, `offline/offset-discovery.md` |
| Architecture / project refs | rule `architecture-boundaries` (auto on `src/**/*.cs`) |
| Replay / binary / harness tools | rule `binary-parser`; agent `decoder-auditor` / Codex `decoder_auditor` |
| Loopback / mutation / privacy audit | agent `security-auditor` / Codex `security_auditor` (readonly) |
| Validate / commit / handoff | skills `validate`, `handoff-amend`, `commit-unit` |
| UI / DTO / smoke / docs glue | agent `implementer-glue` / Codex `implementer_glue` (fast) |
| Prove work after a unit | agent `verifier` / Codex `verifier` (fast) |
| Human setup | `README.md` |

## Delegation (Cursor and Codex)

- Use `explore` subagents for multi-file search/read rounds and `general` subagents for mechanical units against frozen contracts (UI/DTO/tests/docs). Subagents must not stage, commit, or push.
- Codex project agents live in `.codex/agents/*.toml`; use built-in `explorer` for broad read-heavy searches and the four named agents above for specialist work.
- Codex concurrency is capped at three spawned threads in `.codex/config.toml`. Prefer read-heavy parallel work; keep overlapping write units sequential.
- The local Cursor Agent CLI is subscription-backed and authenticated separately
  from the desktop app. Invoke it only through
  `scripts/invoke-cursor-agent.ps1`, which exposes read-only decoder/security
  audits in a temporary standalone export of committed `HEAD`, with shell and
  write tools denied. Windows sandboxing is not available. Never bypass the
  adapter with current-worktree/Git-worktree, force/yolo, MCP-auto-approval, or
  cloud-handoff flags. Cursor labels Fable 5 `NO ZDR`;
  never send it private replays, captures, databases, screenshots, memory
  offsets, tokens, account data, or other game-derived/runtime data.
- OpenCode project agents live in `.opencode/agents/*.md`. Prefer
  `deepseek-glue` for one bounded mechanical implementation unit and
  `free-reviewer` for a bounded read-only second opinion when variable
  free-model latency is acceptable.
- `opencode.json` pins this repository's default OpenCode model to DeepSeek V4
  Flash so an earlier session choice cannot silently select the random free
  router.
- `deepseek-glue` uses OpenCode Go's DeepSeek V4 Flash at its reliable default
  reasoning setting. Request `max` manually only for a hard second opinion;
  do not make it the routine default.
- `free-reviewer` is pinned to an explicit OpenRouter free model instead of
  the variable `openrouter/free` route.
- Grok Build project agents live in `.grok/agents/*.md` and use the owner's
  grok.com subscription login, not an API key. Use `grok-glue` for one bounded
  mechanical unit and `grok-reviewer` for a dependable read-only second
  opinion, including every non-trivial `.cmd` or `.bat` review.
- Invoke Grok agents headlessly with `--no-subagents --disable-web-search
  --no-memory`. Do not run `grok-glue` concurrently with another writer on the
  same worktree. For `grok-reviewer`, also pass `--permission-mode dontAsk
  --allow Read --allow Grep --deny Edit --deny "Bash(*)"`. For `grok-glue`,
  use `--permission-mode dontAsk` with explicit allows for `Read`, `Grep`,
  `Edit`, `Bash(dotnet *)`, `Bash(git status *)`, and `Bash(git diff *)`;
  leave every other shell command denied.
- Keep on the lead model: replay/binary/decoder decisions, loopback/mutation/privacy review, and shared-contract changes.
- The Cursor role briefs in `.cursor/agents/*.md` (decoder-auditor, security-auditor, implementer-glue, verifier) are worth pasting into delegation prompts for hard tasks; attach `.cursor/rules/binary-parser.mdc` or `.cursor/rules/safety-privacy.mdc` as task rules.

## Model stance (one line)

Opus/Fable high|xhigh for decoder/security/hard contracts only; Grok/Composer/fast for glue, explore, verify. Details: `.cursor/reference/model-routing.md`.
