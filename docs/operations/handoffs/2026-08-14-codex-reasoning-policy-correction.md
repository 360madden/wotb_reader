# Codex reasoning-policy correction: task risk, evidence burden, and escalation

**Date:** 2026-08-14 (UTC)

**Status:** corrected policy implemented and verified

## Supersession boundary

This handoff supersedes the reasoning-role matrix in
`2026-08-14-codex-sol-agent-policy.md`. The earlier Sol-only model decision,
hook enforcement, two-subagent cap, and compact `AGENTS.md` remain valid.

The first matrix was under-designed. It made security the only extra-high lane,
left long-range planning at medium, conflated a one-pattern lookup with a deep
cross-system trace, and had no role for unknown memory offsets or ownership
roots. Because spawn-time effort overrides were correctly denied, those
classification gaps also prevented deliberate escalation.

## Corrected decision

The model remains `gpt-5.6-sol` everywhere. Reasoning is selected by four
properties, not by file type:

1. ambiguity and novelty;
2. consequence and reversibility;
3. number of interacting evidence sources or competing hypotheses;
4. whether the work is one deep problem or cleanly decomposable.

| Effort | Repository use |
|---|---|
| `low` | deterministic lookup or smallest focused verification |
| `medium` | bounded implementation/integration after the design is frozen |
| `high` | multi-step causal tracing, known-chain proof, decoder audit, assumption and edge-case checking |
| `xhigh` | long-range roadmap/experiment strategy, consequential architecture, security/privacy/fail-closed review |
| `max` | hardest single-agent discovery: unknown offsets/root anchors, competing pointer ownership, conflicting static/live evidence, or a reasoning-limited failed xhigh attempt |
| Ultra | not a saved role; owner-requested only when at least two deep lanes are genuinely independent |

The lead stays medium for token-efficient routing and integration. Project Plan
mode is now xhigh. Three missing Sol roles were added:

- `systems_analyst` / high: difficult cross-project causal paths;
- `strategist` / xhigh: long-range product, architecture, and evidence-campaign
  planning;
- `memory_researcher` / max: unknown memory offsets, root anchors, vtables/AOBs,
  pointer ownership, and conflicting reverse-engineering evidence.

Existing narrow roles remain: explorer/verifier low, workers medium,
decoder_auditor high, and security_auditor xhigh. The security instructions
were also corrected to match repository policy: player names and name-derived
bot status are public Wargaming statistics, not private data.

## Escalation rules

- A memory-related filename does not automatically require max. Implementing a
  frozen chain is medium; proving a known chain is high; discovering the root
  or resolving competing ownership hypotheses is max.
- Do not spend more reasoning on an external blocker, missing live session, or
  absent evidence. Escalate only when the failure is caused by unresolved
  reasoning, competing hypotheses, or inadequate checking.
- A failed high/xhigh attempt is not repeated unchanged. The max lane must use
  a changed hypothesis, decisive falsification, explicit stop conditions, and
  outcome-to-decision mapping.
- Ultra is not a synonym for "very hard." It is reserved for a problem that is
  both hard and divisible into independent deep workstreams; otherwise max is
  the higher-efficiency quality-first route.

## Enforcement and focused evidence

- Project and global Plan mode defaults are xhigh; the root and default
  subagent remain medium.
- Role files pin Sol and the reviewed effort. The spawn hook permits the three
  new roles, still denies another model, an ad-hoc effort override, or an
  unreviewed role.
- The repository configuration gate now requires 10 reviewed role files and
  the low/medium/high/xhigh/max matrix.
- Eight hook tests pass under Windows PowerShell 5.1 and PowerShell 7, including
  positive strategist and memory-researcher cases plus unreviewed-role denial.
- A fresh Codex routing smoke classified eight representative tasks exactly as
  expected: explorer/low, implementer/medium, systems/high, strategist/xhigh,
  security/xhigh, memory/max, verifier/low, and decoder/high.
- A fresh `--ignore-user-config --strict-config` session loaded the project
  configuration and returned `STRICT_PROJECT_OK`, covering the custom max role
  without relying on the user's broader desktop configuration.
- `scripts/validate.ps1` passed: 1,206 tests passed with 7 local opt-in skips,
  Release build produced 0 warnings / 0 errors, all 8 policy tests passed, and
  repository scan, PowerShell analysis, offline pack, and offset schema/chains
  checks were green.

Natural-language task classification cannot be made fail-closed by a local
hook. The mechanical boundary begins after role selection: model, role, and
effort are pinned and drift is tested. The routing smoke proves that the
current Sol session understands the examples; representative-task outcome
tracking is still required before claiming a measured quality gain.

## Official basis

OpenAI's current Codex guidance says to use the lowest effort that works, use
high/xhigh for difficult multi-step or tradeoff-heavy work, reserve max for the
hardest single-model problems, and use Ultra only when meaningful independent
subagent decomposition exists. It also warns that subagents cost more tokens.

- <https://developers.openai.com/codex/models/>
- <https://developers.openai.com/codex/agent-configuration/subagents/>
- <https://developers.openai.com/codex/config-reference/>

## Next

Evaluate the matrix on real examples from three lanes: a bounded
implementation, a long-range strategy decision, and one hard
root-anchor or offset hypothesis. Record task success, corrections, total
tokens, latency, and whether the selected effort changed the evidence quality.
Do not lower or raise a tier from intuition alone.
