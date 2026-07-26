---
when_to_load: Choosing a model, writing a Task/subagent prompt, or auditing token spend.
do_not_load: Routine single-file edits, validate-only runs, or UI chrome.
---

# Model routing

Pinned IDs match Cursor’s current selectable slugs. If a slug is unavailable, fall back per Cursor plan and note it in the handoff.

| Work shape | Prefer | Reasoning |
|------------|--------|-----------|
| Parent orchestration / hard design decision | `claude-opus-5-thinking-max` or `claude-fable-5-thinking-xhigh` | high / xhigh |
| Replay decode, pickle/protobuf, private-replay evidence | `decoder-auditor` → Opus max `[effort=high]` | isolate binary context |
| Security: loopback, ACL, hub+mutation, harness online deny | `security-auditor` → Fable xhigh, `readonly: true` | no write drift |
| Implement UI/DTO/tests/docs against frozen contracts | `implementer-glue` → `cursor-grok-4.5-high-fast` | fast, cheap |
| Validate / test summary after a unit | `verifier` → `composer-2.5-fast` | minimal reasoning |
| Broad codebase search | built-in Explore | fastest search model |
| Mid complexity shared design (optional) | `claude-sonnet-5-thinking-high` | when Opus is overkill |

## Rules of thumb

1. One expensive hard question per session when possible.
2. Do not run Opus/Fable on “keep going through the list” of thin units.
3. Subagents cost their own tokens — use them to **isolate** noise, not to multiply parallel busywork on the same files.
4. Prefer skills (`validate`, `handoff-amend`, `commit-unit`) over re-explaining git/validate each time.
