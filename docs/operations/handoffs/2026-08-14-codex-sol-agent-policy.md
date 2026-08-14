# Codex Sol-only agent policy and token discipline

**Date:** 2026-08-14 (UTC)

**Status:** implemented and verified

## Decision

The only baseline and subagent model for this repository is
`gpt-5.6-sol`. Task complexity changes reasoning effort, not model family.

The global Codex configuration and the trusted project configuration both pin
the lead to Sol at medium effort. The project also pins every reviewed
subagent role to Sol and caps concurrently open subagents at two.

This deliberately trades away Terra/Luna cost selection in favor of one model
baseline whose behavior can be compared across iterations. Token efficiency
comes from lower reasoning for narrow work, lead-only execution by default,
bounded delegation, concise summaries, and reduced response verbosity.

## Reasoning matrix

| Role | Effort | Boundary |
|---|---:|---|
| lead, `default`, `worker` | `medium` | normal planning, implementation, integration |
| `explorer`, `verifier` | `low` | one read-only lookup or the smallest sufficient check |
| `implementer_glue` | `medium` | bounded mechanical UI/DTO/HTTP/tests/docs work |
| `decoder_auditor` | `high` | replay, binary, decoder, provenance, and contract analysis |
| `security_auditor` | `xhigh` | loopback, mutation, privacy, ACL, and fail-closed review |

`max` and `ultra` are not repository defaults. A different model or
spawn-time reasoning override requires a new explicit owner instruction and a
reviewed policy change.

## Token and context discipline

- Run on the lead alone unless the user asks for delegation, an applicable
  skill requires it, or at least two independent units justify the overhead.
- Do not delegate trivial answers, one-file changes, serial dependencies, or
  work that would duplicate the lead's reads.
- Start one specialist when one is enough; use at most two concurrent
  subagents.
- Prefer read-heavy parallel work. Give writers disjoint ownership and keep
  overlapping writes sequential.
- Give every subagent one bounded question or unit, the smallest useful
  history fork, and a compact return contract.
- Keep the main thread on decisions and evidence summaries rather than raw
  search, test, or log output.
- Keep `AGENTS.md` as a compact, stable routing and safety guide. Volatile
  workstream history belongs in handoffs, the roadmap, and the ledger, loaded
  only when relevant.

## Enforcement layers

1. `~/.codex/config.toml` now pins the personal default and default subagent to
   `gpt-5.6-sol`, medium effort, two-subagent concurrency, concise reasoning
   summaries, and low response verbosity.
2. `.codex/config.toml` repeats those settings at trusted-project precedence
   and enables the stable hooks and multi-agent features.
3. Every reviewed `.codex/agents/*.toml` file pins Sol plus the role's effort;
   custom `default`, `worker`, and `explorer` files close the built-in-role gap.
4. `.codex/hooks.json` checks the active model on `SessionStart` and intercepts
   subagent spawns. The hook stops a non-Sol root session and denies a
   non-Sol or spawn-time reasoning override.
5. `scripts/codex-agent-config-check.ps1` fails the repository gate when the
   model, role matrix, concurrency cap, hook files, or canonical AGENTS policy
   drift.
6. Five Pester cases pin the allow/deny behavior on Windows PowerShell 5.1 and
   PowerShell 7.
7. The always-loaded `AGENTS.md` was reduced from 39,841 bytes / 541 lines to
   8,350 bytes / 154 lines by replacing duplicated historical state with
   links to the canonical handoff, roadmap, ledger, and offline index.

The repository no longer routes work to OpenCode, OpenRouter, or Grok in its
active `AGENTS.md` instructions. Their legacy tracked configuration files were
not deleted; they are outside the Codex policy surface and remain dormant
unless a user explicitly invokes those separate tools.

## Honest enforcement limit

Official Codex configuration precedence still places explicit CLI/config
overrides above project configuration. The current official managed
requirements schema does not document a model allowlist. Project hooks are a
strong guardrail, not an administrator-enforced model entitlement boundary;
specialized tool paths can also opt out of normal hook coverage.

Within ordinary trusted-project Codex runs, the project defaults, per-role
pins, spawn hook, instruction policy, tests, and milestone gate make accidental
model or effort drift observable and fail closed.

## Verification

- Fresh `codex exec --ephemeral --sandbox read-only --json` session returned
  `SOL_POLICY_OK` with no model-policy denial, proving the new session loaded
  the Sol policy and hooks.
- Before compacting `AGENTS.md`, that smoke consumed 31,232 input tokens and
  warned that the project instructions were truncated. After compaction it
  consumed 24,236 input tokens and no longer reported AGENTS truncation: 6,996
  fewer input tokens (22.4%) on the same seven-token response.
- The remaining fresh-session warning concerns the broad machine-wide skill
  and plugin catalog. Prune unused global skills/plugins only after confirming
  they are not needed across the owner's other projects; it is not a
  repository-local model-policy defect.
- `scripts/validate.ps1` passed: Release build produced 0 warnings / 0 errors;
  1,206 tests passed with 7 local opt-in skips; all 5 Codex policy tests passed;
  repository scan, PowerShell analysis, offline pack, and offset schema/chains
  checks were green.

## Official basis

- OpenAI's Codex model guidance recommends Sol for complex, open-ended work,
  medium as the balanced default, and the lowest sufficient reasoning effort.
- OpenAI's subagent guidance states that each subagent performs its own model
  and tool work, so subagent workflows use more tokens than comparable
  single-agent work.
- OpenAI documents project `.codex/config.toml`, per-role agent files,
  `AGENTS.md` precedence, and lifecycle hooks as the applicable durable
  configuration surfaces.

Sources:

- <https://developers.openai.com/codex/models/>
- <https://developers.openai.com/codex/config-reference/>
- <https://developers.openai.com/codex/agent-configuration/subagents/>
- <https://developers.openai.com/codex/agent-configuration/agents-md/>
- <https://developers.openai.com/codex/hooks/>

## Next

Use the role matrix without explicit spawn overrides and compare token use,
latency, and correction rate over several representative tasks before changing
an effort tier. If the machine-wide skill warning remains costly, audit actual
skill/plugin usage and disable only the unused entries in user configuration.
