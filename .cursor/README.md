# Cursor harness (token-lean)

Library + card catalog — not a PDF dumped into every turn.
Inspired by Cursor docs (rules / skills / subagents) and progressive-disclosure setups.

## Always-on (keep tiny)

| Rule | Why |
|------|-----|
| `rules/safety-privacy.mdc` | Non-negotiable safety/privacy |
| `rules/session-budget.mdc` | Scope, tokens, model routing, git |

## Auto-attached (by file)

| Rule | Globs |
|------|-------|
| `rules/architecture-boundaries.mdc` | `src/**/*.cs` |
| `rules/binary-parser.mdc` | Replays, CaptureLogs, tools |

## Skills (on demand)

| Skill | When |
|-------|------|
| `skills/validate` | Before milestone/handoff commit |
| `skills/handoff-amend` | Closing a unit; append dated handoff |
| `skills/commit-unit` | Staging + commit (+ push if user asked) |

## Subagents (delegate; isolate context)

| Agent | Model | Role |
|-------|-------|------|
| `agents/implementer-glue.md` | `cursor-grok-4.5-high-fast` | UI/DTO/tests/docs against existing contracts |
| `agents/verifier.md` | `composer-2.5-fast` | Run validate/tests; report pass/fail only |
| `agents/decoder-auditor.md` | `claude-opus-5-thinking-max[effort=high]` | Replay/binary/evidence gaps |
| `agents/security-auditor.md` | `claude-fable-5-thinking-xhigh` | Readonly safety/privacy/hub trust |

Built-in Explore/Bash/Browser already handle noisy search/shell — prefer them over inventing duplicates.

## Local Cursor Agent CLI

The subscription-backed CLI adapter is
`scripts/invoke-cursor-agent.ps1`. It intentionally exposes only the two
read-only hard-review roles:

| Role | Verified model |
|------|----------------|
| `decoder-auditor` | `claude-opus-5-thinking-max` |
| `security-auditor` | `claude-fable-5-thinking-xhigh` |

Example:

```powershell
.\scripts\invoke-cursor-agent.ps1 -Role security-auditor -Prompt 'Audit the loopback trust boundary.'
```

The adapter runs in Ask mode against a temporary standalone export of tracked
`HEAD` contents, with all shell and write tools denied. Policy files must
already be committed, so uncommitted source, Git worktree links, and private
runtime data are absent. Windows Cursor sandboxing is unavailable; the source
export, `.cursor/cli.json`, and `.cursorignore` are mandatory controls. Never
add `--force`, `--yolo`, `--approve-mcps`, current-worktree, Git-worktree, or
cloud-handoff modes. Fable 5 is marked `NO ZDR` by Cursor and must receive only
tracked, non-private source. The adapter removes the export after each run.

## Reference (do not open by default)

| File | When to open |
|------|----------------|
| `reference/model-routing.md` | Choosing model / writing delegation prompts |
| `reference/canonical-paths.md` | Need the short map of important docs/scripts |

## Anti-patterns

- Do not paste handoffs or blocker log into always-on rules.
- Do not add a fifth always-on rule for convenience.
- Do not create generic “helper” subagents.
- Do not call the ambiguous bare `agent` command; it may resolve to another
  installed agent CLI. Use `cursor-agent` through the adapter.
