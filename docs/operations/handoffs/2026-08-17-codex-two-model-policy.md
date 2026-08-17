# Handoff: two-model Codex policy (deepseek-v4-pro bounded lanes) — 2026-08-17

**Date:** 2026-08-17
**Status:** Complete — owner-approved policy amendment committed (`d5a4fd4`);
all enforcement re-run green
**Context:** owner requested parallel-agent capability under the deepseek-v4-pro
session model; the repo's 2026-08-14 Sol-only contract (AGENTS.md, `.codex/`,
`enforce-sol-model.ps1` hook) blocked it by policy.

## Repository state

- **Branch:** `main`
- **Head:** `d5a4fd4` — `fix(codex): allow deepseek-v4-pro for bounded agent lanes`
- **Working tree:** clean except the pre-existing CRLF-only phantom
  (`2026-08-02-od-recovery-014-partial.md`).

## What was done

Allowed model set amended (two-model contract):

- `gpt-5.6-sol` — all lanes (unchanged).
- `deepseek-v4-pro` — bounded lanes only: lead `default`/`worker`,
  `explorer`, `verifier`, `implementer_glue` (effort medium or less).

Mechanical changes:

- `.codex/config.toml` — `model` and `default_subagent_model` →
  `deepseek-v4-pro`.
- `.codex/agents/{default,worker,explorer,verifier,implementer_glue}.toml` —
  model pin → `deepseek-v4-pro` (descriptions de-“Sol”-ed); the seven
  specialist tomls (code_reviewer, evidence_analyst, systems_analyst,
  decoder_auditor, strategist, security_auditor, memory_researcher) stay
  `gpt-5.6-sol`.
- `.codex/hooks/enforce-sol-model.ps1` → renamed
  `enforce-allowed-models.ps1`; allowed set = both models; ANY spawn-time
  model or reasoning override and any unreviewed role remains denied
  (role files own each lane's model). `hooks.json` updated.
- `scripts/codex-agent-config-check.ps1` — per-role model expectations,
  new hook path, canonical AGENTS.md statement check.
- `scripts/codex-agent-policy.Tests.ps1` — 13 Pester tests (added
  deepseek-v4-pro positive cases: root session, bounded-lane spawn, glue
  spawn; overrides/reasoning/unreviewed-role denials retained).
- `.opencode/agents/deepseek-glue.md` — model re-pinned
  `opencode/deepseek-v4-flash-free` → `opencode/deepseek-v4-pro`.
- `AGENTS.md` (policy section + table with Model column + Last verified
  amended), `docs/operations/codex-agent-roster.md`, `offline/file-tree.md`
  refreshed.

## Validation

```powershell
scripts/codex-agent-config-check.ps1      # PASS: allowed-models=gpt-5.6-sol,deepseek-v4-pro; 12 roles
scripts/invoke-codex-agent-policy-tests.ps1  # Passed: 13, Failed: 0
scripts/invoke-scriptanalyzer.ps1       # SCRIPT HYGIENE GATE PASSED (145 tracked .ps1)
python scripts/python/offline_check.py --refresh && --check-fresh   # 0 broken links; BLK-0001..0027; ledger OK; file-tree fresh
```

## Assumptions and unknowns

- The FreeBuff client of this session does not expose a subagent spawn tool,
  so native parallel spawns still require a harness that honors `.codex`
  (Codex CLI) or the OpenCode glue config — the repo contract now permits
  both models, the harness is the remaining lever.
- No live game window was involved in this change (pure policy/config work).

## Recommended next steps

1. Run a Codex CLI or OpenCode session with the amended config to confirm a
   real `deepseek-v4-pro` subagent spawn end-to-end.
2. If the deepseek-v4-pro bounded lanes underperform on a concrete bounded
   unit, revisit the lane assignment (keep the specialists Sol-only).