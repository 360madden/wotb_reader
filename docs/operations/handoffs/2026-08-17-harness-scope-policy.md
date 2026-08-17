# Handoff — Harness-scope clause: Freebuff sessions are single-agent

**Date:** 2026-08-17 (UTC)
**Branch:** `main` — commit `8d991c8` (policy) + this handoff
**Owner decision:** apply the approved harness-scope clause; `.codex/` enforcement left intact.

## What changed

1. **AGENTS.md — new "## Harness scope (owner-approved 2026-08-17)"**
   block in the delegation policy: the 12-role roster, two-model contract, and
   `.codex/` enforcement apply to harnesses that run delegation (Codex
   TUI/desktop, OpenCode with a configured provider). Sessions driven by
   Freebuff are single-agent lead sessions: the lead performs all roles as
   read-only reasoning framing and never spawns, claims spawns, or launches
   Codex/OpenCode runs except on explicit owner request.

2. **`docs/operations/codex-agent-roster.md`** — matching "## Harness scope
   (Freebuff sessions)" section; names the 2026-08-17 harness drill as the
   first recorded explicit-owner-request exception.

## Why

The first real harness drill (see `2026-08-17-codex-drill.md`) proved the
delegation machinery cannot currently run on this machine: ChatGPT-account
Codex rejects `deepseek-v4-pro` (model whitelist) and `gpt-5.6-sol` (usage
limit until ~Aug 20), and OpenCode has no provider configured. The policy now
matches the harness reality without deleting the owner-approved, docs-verified
Codex configuration, which remains the contract for any future Codex-harness
session.

## Validation

- `codex-agent-config-check.ps1` — PASS ("roles=12 registered
  low/medium/high/xhigh"); the AGENTS.md canonical-policy regex still matches.
- Policy Pester tests — 13/13 passed (enforcement untouched by design).
- `offline_check.py` — links/blocker/ledger gates green.

## Next steps

- Re-run the bounded drill with `gpt-5.6-sol` after the usage limit resets
  (~2026-08-20 20:46 UTC) to capture the first fully admitted live session.
- Owner decision still open: project `.codex/config.toml` session default
  (`deepseek-v4-pro`) fails under ChatGPT-account auth; options are a
  user-level provider for deepseek models, `-m gpt-5.6-sol` at invocation, or
  changing the default while keeping the role contract.
- Optional repo hygiene: make `.freebuff/` fully repo-inert (untrack the two
  tracked stubs + ignore rule) — proposed, not yet approved.