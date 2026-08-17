# Handoff — Codex agent configuration verified against official docs

**Date:** 2026-08-17 (UTC)
**Branch:** `main` — commits `d5a4fd4`/`ff20c29` (two-model policy), `efa4b63` (this verification fix)
**Remote:** origin/main behind by these 3 commits (not pushed)

## Why this session ran

The two-model Codex agent policy (deepseek-v4-pro bounded lanes + gpt-5.6-sol
specialists, commits `d5a4fd4`/`ff20c29`) was implemented from repo knowledge.
Per owner request, the implementation was then verified against OpenAI's
official Codex documentation (learn.chatgpt.com config reference, config
sample, and hooks guide) before shipping.

## What the docs check found and fixed

1. **`model_reasoning_effort` has no `max` tier.** The documented enum is
   `minimal | low | medium | high | xhigh`. `memory_researcher.toml` declared
   `"max"`, which the Codex CLI would reject at load. Fixed to `xhigh` (the
   schema maximum) and aligned every forward-facing reference: AGENTS.md table
   + routing table + effort ladder, `docs/operations/codex-agent-roster.md`
   (roster row, example table, escalation bullets), the policy test name, and
   the hook's comment. The dated 2026-08-14 "Last verified" entry in AGENTS.md
   is a historical snapshot and was left untouched.

2. **Roles were never registered.** The config reference requires custom roles
   to be declared in `config.toml` via `[agents.<name>]` tables with
   `config_file` (relative to the declaring config); role files alone are not
   enough. Added all 12 `[agents.<name>]` + `config_file = "./agents/<name>.toml"`
   declarations. Also confirmed from the docs: `default_subagent_model`,
   `default_subagent_reasoning_effort`, `enabled`,
   `max_concurrent_threads_per_session`, `model_reasoning_summary`,
   `model_verbosity`, and `plan_mode_reasoning_effort` are all real keys with
   our values in range; "default" is not a reserved scalar agent name; and the
   SessionStart (`startup|resume|clear|compact`) and PreToolUse
   (`spawn_agent`) hook matchers and the hook response shapes
   (`continue`/`stopReason`/`systemMessage`; `hookSpecificOutput` +
   `permissionDecision`) match the hooks reference.

3. **Enforcement now locks both fixes.** `codex-agent-config-check.ps1`
   asserts each of the 12 roles is registered in config.toml with the right
   `config_file`, and checks every role effort against the schema-valid set.
   Stale "Sol-only" gate descriptions in `validate.ps1` and
   `invoke-codex-agent-policy-tests.ps1` were updated to "allowed-models".

## Validation

- TOML parse (Python tomllib) of `config.toml` + all 12 role files: OK, all
  efforts in `low|medium|high|xhigh`.
- `scripts/codex-agent-config-check.ps1` — PASSED: "roles=12 registered
  low/medium/high/xhigh".
- `scripts/invoke-codex-agent-policy-tests.ps1` — 13/13 passed, 0 failed
  (includes the renamed xhigh-effort memory-researcher case and the
  override-denial cases).
- PSScriptAnalyzer gate — PASSED (145 tracked .ps1 files).
- `offline_check.py` — 0 broken links, BLK-0001..0027 contiguous, ledger
  consistency OK.

## Assumptions and remaining unknowns

- The config shape is verified against the published docs, but this repo's
  harness (wherever it runs) has not been exercised end-to-end with a real
  spawn since the fix; the Pester suite exercises the hook contract with the
  documented JSON envelopes.
- A role layer being TOML rather than Markdown is supported per the reference
  ("Path to a TOML config layer for that role").
- The pre-existing CRLF-only phantom (`2026-08-02-od-recovery-014-partial.md`)
  remains untouched in the working tree.

## Next steps

- Push `d5a4fd4`, `ff20c29`, `efa4b63` to origin when the owner approves.
- First real multi-agent run under the fixed config: observe that spawns carry
  the role's model/effort and that overrides are denied by the hook.
- If a live Codex run rejects any key, re-check against the docs before
  changing enforcement.
