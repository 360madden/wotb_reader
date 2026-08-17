# Handoff — First real multi-agent drill under the two-model Codex config

**Date:** 2026-08-17 (UTC)
**Branch:** `main` (commits `efa4b63` + `ceff7f1` already landed; this session adds a roster note + this handoff)
**Prior context:** `docs/operations/handoffs/2026-08-17-codex-docs-verification.md`

## Why this session ran

After the two-model policy (deepseek-v4-pro bounded lanes, gpt-5.6-sol all
lanes) was verified against the official Codex docs, the owner asked for a
first real drill: spawn an explorer and a verifier on the RECOVERY build-drift
triage outputs, confirm the hook admits them with role-owned model/effort and
denies an override, and report what the harness accepted.

## What was actually executed

1. **Harness inventory:** `codex-cli 0.147.0` and `opencode 1.18.10` are both
   installed; Codex `auth.json` is ChatGPT-account mode; OpenCode configs are
   bare (no provider configured). The Freebuff session itself has no spawn
   tool, so the drill used the real hook script and the real Codex CLI.

2. **Enforcement dry-run (the live hook, 8 envelopes, exact JSON shapes):**
   `SessionStart` deepseek-v4-pro and gpt-5.6-sol → admit; `SessionStart`
   terra-beta → deny (`continue:false` + stopReason); `spawn_agent` explorer
   and verifier → admit; `spawn_agent` with a model override, a reasoning
   override, or an unreviewed role → deny with the documented
   `hookSpecificOutput.permissionDecision=deny` shapes. 8/8 correct.

3. **Real harness runs (codex exec):**
   - Session with project config (model `deepseek-v4-pro`, hooks enabled):
     `thread.started` fired and no hook denial occurred — the SessionStart
     hook admitted the model. The platform then rejected it:
     `400 invalid_request_error: The 'deepseek-v4-pro' model is not supported
     when using Codex with a ChatGPT account.`
   - Retry with `-m gpt-5.6-sol` (allowed all lanes): rejected by the
     account's usage limit — "Upgrade to Pro ... or try again at Aug 20th,
     2026 8:46 PM".

## What the harness accepted — honest verdict

- The enforcement layer admits exactly the reviewed set and denies
  overrides/out-of-set models, including inside the real harness (hook ran at
  SessionStart; no denial event; thread started).
- The platform layer accepted **no model this session**: deepseek-v4-pro is
  not in the ChatGPT account's model catalog, and gpt-5.6-sol is
  usage-limited until ~2026-08-20 20:46 UTC.
- `codex exec` has no agent-role selection (`--agent`/role flags absent), so
  the role-owned model/effort path (explorer low, verifier low) could not be
  observed on a live run; role files apply only via the interactive
  TUI/desktop spawn path.
- OpenCode, the harness that ran the flash-era parallel agents, currently has
  no provider configured, so it cannot serve deepseek-v4-pro as-is.

## Consequences and decisions for the owner

- The project `.codex/config.toml` session default `model = "deepseek-v4-pro"`
  fails every Codex session in this repo under ChatGPT-account auth. Options:
  (a) configure a user-level provider that serves deepseek models
  (project-scoped config cannot set `model_providers` per the docs),
  (b) run sessions with `-m gpt-5.6-sol` once credits allow, or
  (c) change the project session default to `gpt-5.6-sol` while keeping the
  two-model role contract unchanged (owner decision — not done here).
- The framework is not broken; the machine's harness is not yet capable of
  the deepseek lane. No enforcement change was made.

## Validation

- The 8-envelope dry-run is deterministic and re-runnable from
  `.build/drill/env1..8.json` (git-ignored .build outputs).
- Hook + policy gates were green before this session and were not modified by
  it; the only repo change is this handoff plus the roster compatibility note.

## Next steps

- After Aug 20 (or after credits), repeat the drill with `-m gpt-5.6-sol` to
  capture a full end-to-end admitted session and the two bounded answers.
- If the owner wants the deepseek lane live: configure the provider at user
  level (outside the repo), then re-run the same drill.
- First true spawn drill (role-owned model/effort) requires running the
  interactive TUI/desktop per the spawn path.