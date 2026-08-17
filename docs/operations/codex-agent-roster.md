# Codex agent roster and routing contract

**Date:** 2026-08-15 (UTC)

**Status:** active project policy

## Purpose and authority

This is the stable, detailed roster for Codex development in this repository.
`AGENTS.md` carries the compact always-loaded rules; `.codex/config.toml`, the
files under `.codex/agents/`, `.codex/hooks.json`, and
`scripts/codex-agent-config-check.ps1` mechanically define and check the setup.

Two models are allowed (owner-approved 2026-08-17): `gpt-5.6-sol` for all
lanes, and `deepseek-v4-pro` for the bounded lanes only (lead
`default`/`worker`, `explorer`, `verifier`, `implementer_glue` — effort
`medium` or less). The specialist lanes (`code_reviewer`, `evidence_analyst`,
`systems_analyst`, `decoder_auditor`, `strategist`, `security_auditor`,
`memory_researcher`) stay `gpt-5.6-sol` only. A role file owns its model and
its reasoning effort; roles change effort, instructions, and default sandbox,
not their model. Spawn-time model/reasoning overrides are denied by
`.codex/hooks/enforce-allowed-models.ps1`. A role file's sandbox is the
intended project default; a stricter parent permission still wins, and Codex
may reapply a live parent permission override to a child.

## Selection algorithm

1. Keep bounded work on the medium lead unless isolation or a specialist adds
   clear value.
2. Classify the question by ambiguity, consequence, evidence interaction, and
   reversibility. File type alone does not determine effort.
3. Choose one primary specialist. Add a second only for a genuinely independent
   lane or a distinct required review dimension.
4. Give each specialist a bounded question, the smallest useful context fork,
   explicit evidence to return, and a stop condition.
5. The lead integrates results, owns shared-contract decisions, runs the final
   gate, and alone stages, commits, pushes, publishes, or operates live sessions.

Subagents cost additional tokens. The project session cap is six concurrent
agent threads, but one specialist is the normal case. Parallelism is for
independent read-heavy work or disjoint writers, not for duplicating the same
investigation.

## Reviewed roster

| Role | Effort | Default sandbox | Use | Do not use for |
|---|---:|---|---|---|
| `default` | medium | workspace-write | bounded unit without a narrower role | architecture, security policy, or an unbounded task |
| `worker` | medium | workspace-write | disjoint implementation with explicit file ownership | shared-contract design or overlapping edits |
| `explorer` | low | read-only | one exact codebase question or evidence lookup | causal diagnosis, broad review, or implementation |
| `verifier` | low | workspace-write | smallest focused test or the full milestone gate | source edits or speculative diagnosis |
| `implementer_glue` | medium | workspace-write | frozen UI, DTO, HTTP, tests, docs, and smoke wiring | new architecture, memory semantics, or trust policy |
| `code_reviewer` | high | read-only | correctness review of a diff, branch, commit range, or bounded scope | security-only, decoder-safety, or experiment adjudication |
| `evidence_analyst` | high | read-only | decoded/live evidence, ephemeral read-only scorer runs, clock windows, controls, and cross-replay claims | raw-format safety, evidence mutation, unknown root discovery, or live operation |
| `systems_analyst` | high | read-only | multi-component causal trace, lifecycle failure, ownership, or contract interaction | a simple lookup, implementation, or long-range roadmap |
| `decoder_auditor` | high | read-only | replay/binary limits, pickle/protobuf safety, provenance, and immutable decode contracts | general code review or memory-root discovery |
| `strategist` | xhigh | read-only | consequential long-range roadmap, architecture, or evidence campaign | implementing the selected route |
| `security_auditor` | xhigh | read-only | loopback trust, ACLs, mutation, privacy, and fail-closed boundaries | general maintainability or product prioritization |
| `memory_researcher` | xhigh | read-only | hardest unknown offset/root/vtable/AOB and conflicting ownership hypotheses | known-chain implementation, live scanning, or routine review |

`verifier` receives workspace-write only because .NET and repository checks write
ignored build outputs. Its instructions prohibit tracked source changes. All
analysts and reviewers are read-only by both configuration and instruction.

## Boundary tests

- `explorer` locates a call site; `systems_analyst` proves why the path fails.
- `systems_analyst` traces code and state; `code_reviewer` judges a defined
  change for regressions and test gaps.
- `decoder_auditor` validates raw-format and decode safety; `evidence_analyst`
  decides whether an experiment or scorer result proves its claim.
- `evidence_analyst` works from known immutable artifacts; `memory_researcher`
  resolves an unknown ownership root or conflicting reverse-engineering model.
- `strategist` selects milestones and gates; `worker` or `implementer_glue`
  executes a frozen bounded unit.
- `security_auditor` owns adversarial trust analysis even when the affected code
  also crosses multiple projects.

## Repository examples

| Question | Primary route |
|---|---|
| Where is the live-frame session id forwarded? | `explorer` / low |
| Add a frozen nullable response field and focused tests | `implementer_glue` / medium |
| Why does teardown lose a captured dump across driver and host? | `systems_analyst` / high |
| Review the current PN UI diff for regressions | `code_reviewer` / high |
| Does a bounded lead/lag re-verdict establish two-replay repeatability? | `evidence_analyst` / high |
| Audit pickle bounds and unknown-record provenance | `decoder_auditor` / high |
| Plan the next three overlay/evidence milestones under scarce launches | `strategist` / xhigh |
| Audit rendezvous ACL and loopback rebinding denial | `security_auditor` / xhigh |
| Resolve competing roots for an unknown camera or entity family | `memory_researcher` / xhigh |
| Run the focused tests after an implementation unit | `verifier` / low |

## Escalation and token discipline

- Low is for deterministic retrieval and checks. Medium is the balanced default
  for bounded implementation and integration. High is for complex logic,
  assumption checking, edge cases, review, and interacting evidence. Xhigh is
  for consequential planning, security tradeoffs, and the hardest one-problem
  memory discovery with competing hypotheses.
- Do not escalate because access, a replay, approval, or another serial input is
  missing. More reasoning does not remove an external blocker.
- A failed high or xhigh pass may escalate only with a changed hypothesis,
  explicit falsification, stop conditions, and outcome-to-decision mapping.
- Ultra is not a saved project role. It is owner-requested only and useful when
  at least two deep lanes are truly independent; a single hard investigation
  stays on the xhigh `memory_researcher` path.
- Return compact conclusions and evidence references, not raw search output or
  complete logs. Close a specialist after its bounded question is answered.

## Lead-only and intentionally unspecialized work

The lead alone launches or attaches to the game, performs live reads, controls
the live-session lock, applies an offset publication, decides shared contracts,
stages and commits, pushes, versions, releases, and writes the final integrated
handoff.

No separate roles exist for the following because adding them would overlap the
reviewed roster and spend tokens without a distinct decision boundary:

- UI designer: `strategist` defines consequential HUD behavior,
  `implementer_glue` implements frozen UI, `code_reviewer` checks correctness,
  and the owner/lead performs the native visual smoke.
- Test engineer: `code_reviewer` identifies risk-based gaps and `verifier` runs
  the smallest sufficient checks.
- Architecture agent: `strategist` selects architecture and `systems_analyst`
  proves current cross-component behavior.
- Documentation or release agent: `implementer_glue` handles bounded docs;
  durable handoff, version, Git, and release integration remain lead-owned.
- Live operator: the safety, lifecycle, and single-game locks require one lead,
  not a delegated role.

## Enforcement and evaluation

The repository gate requires exactly the reviewed role files, pins every role to
Sol, validates its effort and sandbox, enforces the six-thread project session
cap, and proves the spawn hook accepts every reviewed role. Hook tests also deny
another model, spawn-time effort overrides, and unreviewed roles.
Natural-language routing cannot be made perfectly
fail-closed by a hook, so representative routing smokes and real-task outcome
tracking remain necessary.

Evaluate the roster on actual tasks by recording task success, correction count,
latency, token use, whether the selected role changed evidence quality, and
whether a second agent added non-duplicative value. Change a role or effort only
from those results, not from intuition alone.

Official basis:

- <https://developers.openai.com/codex/agent-configuration/subagents/>
- <https://developers.openai.com/codex/config-reference/>
- <https://developers.openai.com/codex/models/>
