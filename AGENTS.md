# WotB Treader — agent entry

Windows-first .NET 10 modular monolith for **offline** WoTB replay telemetry.
No runtime AI, cloud, Python, Node.js, Rust, Electron, or containers.

## Where we are now (2026-08-09)

- **Active workstream:** offset discovery — module-rooted player-position
  polling. Latest handoff (OD-075 + BLK-0026 resolution, 2026-08-09):
  continuous polling is positive in **two distinct 11.19.0 replays** (Dead
  Rail 24/24; Oasis Palms 24/24, stable-resolver-positive) — cross-replay
  repeatability is proven. The open gates before any offset-table change are
  hardware-atomic read proof, same-decoded-clock alignment, and numeric-
  offset publication; none may be skipped.
- **BLK-0026 resolved and validated (2026-08-09):** root cause was a launcher
  regression — .NET `Set-Acl` threw `PrivilegeNotHeldException` on the
  persisted owner-only marker ACL, mapped by the catch-all to
  `FAILED_unexpected` before the gate (every launch after the ACL code landed).
  Fixed with `icacls` in both owner-only ACL functions; the launcher now
  reaches `OfflineReplayVerified` and exactly one unchanged bounded OD-075
  poll returned positive on the content-distinct replay (24/24 resolved,
  stable-resolver-positive) — **cross-replay continuous polling is now proven
  across two distinct 11.19.0 replays** (Dead Rail + Oasis Palms, ledger
  `OD-RECOVERY-076`). See `docs/operations/blocker-log.md` BLK-0026 and the
  2026-08-09 resolution handoff. Still unproved: hardware atomicity,
  same-decoded-clock proof, numeric-offset publication, and promotion — no
  offset-table change without those.
- **Last verified gate:** 2026-08-09 — 654 tests passed, 2 local opt-in skips,
  0 warnings, 0 errors.
- **Refresh from:** the newest file in `docs/operations/handoffs/`,
  `docs/operations/offset-discovery-ledger.md` (Next planned session row),
  `docs/operations/blocker-log.md` (open blockers),
  `docs/operations/offset-promotion-checklist.md` (offset-gate status).

## Session ritual (start and end every task)

**Start**

1. Read the newest handoff in `docs/operations/handoffs/`; for offset work,
   also the ledger's *Next planned session* row.
2. `git status` + current branch; note uncommitted work that isn't yours —
   never discard, overwrite, stash, or commit it.
3. Load only what the task needs. `offline/README.md` re-orients fast and
   links to canonical docs instead of duplicating them.

**End**

1. Verify: typecheck → focused tests → full `scripts/validate.ps1` gate for
   milestone work.
2. Record: append a handoff + ledger entry where the workstream requires;
   blockers append to `docs/operations/blocker-log.md` (immutable UTC).
3. Report: what changed, what was verified, what remains. Leave the tree clean
   of stray files.

## Project owner context

- The project owner identifies as a junior developer at Wargaming.net. This is
  user-provided background for agents working in the repository; the repository
  remains a personal project unless official status is documented separately.
  Canonical wording: [`docs/project-context.md`](docs/project-context.md).
- Offline discovery pack: [`offline/README.md`](offline/README.md) — a
  focused, self-contained repo index (repo map, entry points, API surface,
  glossary, commands, replay format, offset discovery, data flow,
  memory-offset evidence, file-tree snapshot). Run
  `python scripts/python/offline_check.py --refresh` after editing the pack;
  the gate and CI run `--check-fresh` and fail if `offline/file-tree.md` is
  stale, so regenerate it in the same change that adds, renames, or removes
  files.

## Commands (exact)

| Use | Command |
|---|---|
| Restore | `dotnet restore WotBTreader.sln --locked-mode` — SDK pinned to 10.0.302 by `global.json`; package versions central in `Directory.Packages.props` with committed lock files |
| Build | `dotnet build WotBTreader.sln -c Release` |
| One test project | `dotnet test tests/WotBTreader.Core.Tests -c Release` |
| Focused test | add `--filter "FullyQualifiedName~SomeTest"` |
| Full gate (milestone) | `scripts/validate.ps1` — locked restore → `dotnet format --verify-no-changes` → Release build → all tests → `scan-repository.ps1` (secret + ignore-policy) → PSScriptAnalyzer hygiene (`install-psscriptanalyzer.ps1` + `invoke-scriptanalyzer.ps1`); add `-AuditPackages` for the transitive vulnerability audit |

Warnings are errors (`TreatWarningsAsErrors`) and `NuGetAuditMode=all` fails
restore on vulnerable transitive packages — fix with a central pin, never
suppress (BLK-0002). Tests are MSTest 4 on Microsoft.Testing.Platform; a few
installed-game tests are local opt-in and skip by default.

## Hard constraints (always)

| Rule | Why | If violated |
|---|---|---|
| Bot status may be inferred from a name; player names and bot status are public Wargaming statistics | those facts are public data, not private | privacy boundary misapplied to names |
| Never log raw replay bytes, tokens, full paths, account IDs, chat, screenshots | privacy trust boundary | privacy scan / review fails; trust broken |
| Never modify/redistribute the WotB install or game-derived assets | install is read-only, per project policy | repository scan fails; policy broken |
| `Core` has no project refs; `Application` → `Core` only; overlay is a loopback web client (no parser/storage refs) | mechanically enforced boundary | `Architecture.Tests` fails the build |
| Evidence-first decode: unknown stays unknown; reprocess = new immutable decode run | no fabrication; append-only evidence | invented semantics or mutated runs |
| Pickle = data only; never execute opcodes / import Python | decode safety | arbitrary code execution risk |
| Focused diffs; no destructive git; no secrets in repo | repo hygiene | lost work or leaked secrets |
| Commits as `Codex Agent <codex@local.invalid>` unless user says otherwise | repo convention | inconsistent history |
| Push only when the user asks; never force-push | remote safety | destructive remote history |
| Lead stages/commits; subagents must not. Propose shared-contract changes before editing them | ownership and review | unreviewed contract drift |
| Milestone: format/analyzers/build/tests pass; handoff via `scripts/validate.ps1` | release gate | unreviewable milestone |
| CI: synthetic fixtures only; private-game tests are opt-in local | fixture policy | private data reaches CI artifacts |
| Blockers/handoffs: append only, immutable UTC | audit trail | history rewritten |

## Architecture (enforced by `tests/WotBTreader.Architecture.Tests`)

- `Core`: no project refs. `Application` → `Core` only. Adapters (`Replays`,
  `CaptureLogs`, `GameIntegration`, `Storage.Sqlite`) → `Application`+`Core`,
  never each other. `Bootstrap` is the only composition root; hosts reference
  `Bootstrap`.
- **The overlay (WPF) is a transparent, borderless, topmost HUD** that sits on
  top of WoT Blitz during replay playback — a loopback web client with no
  parser/storage refs. It is **NOT** a generic session viewer. Full design
  spec: [`docs/architecture/overview.md`](docs/architecture/overview.md).
- Only `Overlay` and `tools/GameHarness` target `net10.0-windows`; everything
  else stays portable `net10.0` (BLK-0003). Every new DI port must be added to
  the published-port list in `CompositionRootTests`, or the solution compiles
  and tests green yet no host starts (BLK-0013). Diagram, evidence lifecycle,
  and loopback trust boundary: `docs/architecture/overview.md`.

## Task decision tree

| Task | Load | Allowed | STOP if |
|---|---|---|---|
| Plan / design sharpening before acting | skill `grill-me` (user-invoked, `.agents/skills/`) | interview until shared understanding; agent may fetch facts but never answer decisions | implementing before the user confirms the understanding is shared |
| Replay format / decode internals | [`offline/replay-format.md`](offline/replay-format.md) | read-only analysis; decode as data | executing pickle opcodes / importing Python |
| Telemetry data flow (decode → UI / comparison) | [`offline/data-flow.md`](offline/data-flow.md) | trace pipelines | mutating immutable decode runs |
| Offset / memory evidence | [`offline/memory-offsets.md`](offline/memory-offsets.md), [`offline/offset-discovery.md`](offline/offset-discovery.md), ledger | static/synthetic proof; bounded gated polls per ledger plan | promoting offsets or editing `memory-offsets/11.19.0.10.json` without proof; live polls while BLK-0026 is open |
| Game internals research | [`research/README.md`](research/README.md) (index) | research | touching the game install |
| Architecture / project refs | [`docs/architecture/overview.md`](docs/architecture/overview.md), `tests/WotBTreader.Architecture.Tests/` | refactor within boundaries | violating the reference graph |
| Replay / binary / harness tools | [`offline/replay-format.md`](offline/replay-format.md); Codex `decoder_auditor` | audits | shipping dynamic decoder DLLs |
| Loopback / mutation / privacy audit | Codex `security_auditor` (read-only) | read-only review | mutating shared contracts |
| Validate / commit / handoff | this file's *Definition of done + commit checklist*; `scripts/validate.ps1` | per checklist | pushing without being asked |
| UI / DTO / smoke / docs glue | Codex `implementer_glue` (fast) | bounded mechanical units | changing shared contracts without proposal |
| Prove work after a unit | Codex `verifier` (fast) | verification | staging or committing |
| Human setup | [`README.md`](README.md) | — | — |

## Repo gotchas (each has bitten before)

| Symptom | Root cause | Fix |
|---|---|---|
| `.gitignore` unanchored patterns match **case-insensitively on Windows** and hide real source folders | `*.sqlite`, `diagnostics/`, `dist/` unanchored (BLK-0005, BLK-0012) | add explicit `!` unignore rules for paths that collide with runtime-data patterns; `scan-repository.ps1` fails validation if any ignored file exists under `src`, `tests`, `tools/src`, `scripts`, or `docs` |
| `validate.ps1` reports success after a failed phase | `$ErrorActionPreference='Stop'` does not catch non-zero native exit codes (BLK-0006) | route every native command through `Invoke-CheckedNative` |
| A `.ps1` silently corrupts at runtime | PowerShell 5.1 reads BOM-less UTF-8 as ANSI (an em-dash's trailing byte `0x94` maps to `"` and terminates a string literal early) | keep every `.ps1` ASCII-only; the PSScriptAnalyzer gate (`scripts/invoke-scriptanalyzer.ps1`) and custom rules (`tools/psscriptanalyzer-custom-rules.psm1`) enforce it — they ban `[double]::IsFinite` and `??`/`&&`/`||`; type custom-rule parameters with a **concrete AST node** (`[ScriptBlockAst]`, never `[Ast]` — the analyzer matches by type-name substring and silently skips `[Ast]`); pass `-IncludeDefaultRules` (custom paths replace defaults); run `powershell -File scripts/invoke-scriptanalyzer.ps1 -SelfTest` after touching the rules module |
| cmd wrapper misbehaves | delayed expansion + `!` in filenames, unquoted `%~dp0` in nested `cmd /c`, whitespace input → arithmetic crash, env var leaking | full catalogue and review checklist in [`docs/operations/cmd-wrapper-gotchas.md`](docs/operations/cmd-wrapper-gotchas.md); route any non-trivial cmd/batch review through a thinker agent with the actual file contents — never rely on manual reading |
| .NET commands time out | basher default 30s is far too short | minimums: build 300s, full test suite 300s, single test project 120s, publish 180s; never run interactive `.cmd` wrappers (import, everything, serve) through basher — they expect a TTY or spawn windows; use direct `dotnet build` / `dotnet test` / `dotnet publish`; verify prerequisites (CLI built, packages restored) before running dependents |
| Fixture leak | private replays, captures, DBs, screenshots reach the repo | synthetic fixtures only in CI; private data stays in ignored paths; full sanitization process: [`docs/testing/fixture-policy.md`](docs/testing/fixture-policy.md) |

## Definition of done + commit checklist

**Done =** code changes typecheck and pass focused tests · docs/handoffs
updated where the workstream requires · milestone work passes the full
`scripts/validate.ps1` gate · no stray files left behind.

**Commit:** review `git diff` and recent `git log` style → stage only related
files (never broad `git add -A`) → conventional-commit message → author
`Codex Agent <codex@local.invalid>` unless the user says otherwise → push
**only when asked**, never force-push.

## Delegation index (Codex · OpenCode · Grok)

| Agent | Use for | Config / notes |
|---|---|---|
| `explorer` (built-in) | broad read-heavy searches / multi-file rounds | Codex built-in; prefer read-heavy parallel work |
| `decoder_auditor` | replay/binary/decoder audits | `.codex/agents/decoder_auditor.toml` |
| `security_auditor` | loopback/mutation/privacy audit (read-only) | `.codex/agents/security_auditor.toml` |
| `implementer_glue` | UI/DTO/smoke/docs glue (fast) | `.codex/agents/implementer_glue.toml` |
| `verifier` | prove work after a unit (fast) | `.codex/agents/verifier.toml` |
| `deepseek-glue` | one bounded mechanical implementation unit | `.opencode/agents/*.md`; DeepSeek V4 Flash at its reliable default reasoning — request `max` only for a hard second opinion |
| `free-reviewer` | bounded read-only second opinion | pinned to an explicit OpenRouter free model, not the variable `openrouter/free` route |
| `grok-glue` | one bounded mechanical unit; every non-trivial `.cmd`/`.bat` review | `.grok/agents/*.md`; owner's grok.com subscription login, not an API key |
| `grok-reviewer` | dependable read-only second opinion | same |

**Cross-cutting rules**

- Subagents must not stage, commit, or push.
- Codex concurrency is capped at three threads (`.codex/config.toml`); keep
  overlapping write units sequential.
- `opencode.json` pins this repository's default OpenCode model to DeepSeek V4
  Flash so an earlier session choice cannot silently select the random free
  router.
- Grok: invoke headlessly with `--no-subagents --disable-web-search
  --no-memory`. For `grok-reviewer` also pass `--permission-mode dontAsk
  --allow Read --allow Grep --deny Edit --deny "Bash(*)"`. For `grok-glue` use
  `--permission-mode dontAsk` with explicit allows for `Read`, `Grep`, `Edit`,
  `Bash(dotnet *)`, `Bash(git status *)`, and `Bash(git diff *)`; leave every
  other shell command denied. Never run `grok-glue` concurrently with another
  writer on the same worktree.
- Lead model keeps: replay/binary/decoder decisions, loopback/mutation/privacy
  review, and shared-contract changes.

## Model stance (one line)

Hard decoder/security/contract decisions stay on the lead model; Codex subagents (default `gpt-5.6-terra`), OpenCode DeepSeek V4 Flash, and Grok handle glue, explore, verify. Details: `.codex/config.toml`, `.opencode/agents/*.md`, `.grok/agents/*.md`.

## Last verified

- 2026-08-09 — full gate green: 654 tests passed, 2 local opt-in skips,
  0 warnings, 0 errors (fresh run).
- 2026-08-09 — AGENTS.md restructured to the table-driven layout; hard
  constraints trimmed (offline-only + Cheat Engine bullets removed), ADR 0002
  amended, README/knowledge.md reconciled.
- 2026-08-09 — Cursor references removed (subscription ended); delegation
  index now covers Codex, OpenCode, and Grok only.
