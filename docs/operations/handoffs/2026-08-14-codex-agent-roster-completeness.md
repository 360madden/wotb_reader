# Codex agent-roster completeness and sandbox enforcement

**Date:** 2026-08-14 (UTC)

**Status:** implemented and milestone-validated

## Relationship to prior policy

This extends, but does not replace, the corrected effort ladder in
`2026-08-14-codex-reasoning-policy-correction.md`. The only model remains
`gpt-5.6-sol`; lead/default implementation remains medium, Plan and strategy
remain xhigh, unknown-root memory research remains max, and Ultra remains an
owner-requested decomposable-work exception rather than a saved role.

## Audit result

The 10-role policy covered exploration, implementation, verification, causal
analysis, decode safety, strategy, security, and hard memory discovery. Two
recurring repository decisions still lacked a narrow owner:

1. general correctness review of a defined diff or branch; and
2. adjudication of immutable decoded/live experiment evidence, including clock
   windows, scorer thresholds, controls, and cross-replay repeatability.

The roster now has 12 reviewed roles. `code_reviewer` and `evidence_analyst`
fill those gaps at high reasoning in a read-only sandbox. They are deliberately
separate from `systems_analyst` (causal trace), `decoder_auditor` (raw format and
decode safety), `security_auditor` (adversarial trust), and
`memory_researcher` (unknown roots and competing reverse-engineering models).

No separate UI, test, architecture, documentation, release, or live-operator
role was added. Those labels would overlap the reviewed decision boundaries:
strategy + bounded UI implementation + correctness review + owner visual smoke;
reviewer-identified test gaps + verifier execution; strategy + systems trace;
bounded docs + lead integration; and lead-only Git/live safety operations. The
full rationale and routing examples are durable in
`docs/operations/codex-agent-roster.md`.

## Setup and enforcement

- Every role file pins `gpt-5.6-sol`, its reviewed effort, and an explicit
  `sandbox_mode`.
- Analysts/reviewers are `read-only`. Default/worker/implementer roles are
  `workspace-write`. The verifier is `workspace-write` solely for ignored .NET
  outputs and is instruction-denied from changing tracked source.
- The config gate now requires exactly 12 role files, validates model, effort,
  and sandbox for each, then invokes the spawn hook for every reviewed role so
  the role-file list and hook allowlist cannot silently drift.
- The hook admits the two new roles and continues to deny non-Sol sessions,
  non-Sol spawn overrides, all spawn-time effort overrides, and unreviewed
  roles.
- `AGENTS.md` carries only the compact matrix and key routing rows; the stable
  detailed roster lives outside the always-loaded prompt to control context
  cost.
- One primary specialist is the default. A second is justified only by an
  independent workstream or a distinct required review dimension. The
  concurrency cap remains two.

Codex can reapply a parent turn's live permission override when starting a
child, so a role-file sandbox is a reviewed default rather than an absolute
runtime guarantee. Read-only specialist instructions independently prohibit
edits, evidence mutation, staging, commits, pushes, live operation, and offset
promotion. The evidence analyst may run existing scorers read-only against
immutable artifacts when the result remains ephemeral.

## Focused verification

- Windows PowerShell 5.1: config gate PASS; hook tests 10/10 PASS.
- PowerShell 7: config gate PASS; hook tests 10/10 PASS.
- Fresh Codex CLI 0.147.0 effective session: model `gpt-5.6-sol`, reasoning
  `medium`, concise summaries.
- Fresh no-tool routing smoke: 12/12 scenarios classified exactly — default and
  worker medium; explorer/verifier low; implementer medium; correctness,
  evidence, systems, and decoder high; strategy/security xhigh; memory-root
  research max; intended sandbox class correct for every role.
- `scripts/validate.ps1`: PASS — 1,206 .NET tests passed with 7 local
  opt-in skips; Release build produced 0 warnings and 0 errors; all 10 Codex
  policy tests passed; repository/privacy scan, PowerShell analysis, offline
  pack freshness, and offset schema/chains validation were green.

An older `--ignore-user-config --strict-config` smoke is not sufficient to
prove the effective reasoning default on CLI 0.147.0: it starts with reasoning
shown as `none`. Strict loading with the real desktop config currently stops on
that config's app-owned `computer_use` key, which this CLI version does not
recognize. The repository gate therefore owns exact project-file validation,
while a normal fresh session proves the real effective model/effort and the
routing smoke proves natural-language classification.

## Official basis

Current Codex guidance says custom agents can pin model, reasoning, sandbox,
and narrow instructions; high is appropriate for complex logic, assumptions,
edge cases, review, and security; medium is the balanced default; and low is for
straightforward speed-sensitive tasks. It also warns that subagents add token
cost and that parent runtime permission overrides can be reapplied to children.

- <https://developers.openai.com/codex/agent-configuration/subagents/>
- <https://developers.openai.com/codex/config-reference/>
- <https://developers.openai.com/codex/models/>

## Next

Evaluate the 12-role roster on real bounded implementation, correctness review,
experiment adjudication, long-range strategy, and root-anchor tasks. Record
success, corrections, latency, token use, evidence-quality change, and whether
a second specialist contributed non-duplicative value. Change the roster only
from those results.
