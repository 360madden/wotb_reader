# WotB Treader — agent entry

Windows-first .NET 10 modular monolith for **offline** WoTB replay telemetry.
No runtime AI, cloud, Python, Node.js, Rust, Electron, or containers.

Cursor layout and asset index: [`.cursor/README.md`](.cursor/README.md).
Do **not** load `.cursor/reference/*` unless the task needs that catalog.

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

## Model stance (one line)

Opus/Fable high|xhigh for decoder/security/hard contracts only; Grok/Composer/fast for glue, explore, verify. Details: `.cursor/reference/model-routing.md`.
