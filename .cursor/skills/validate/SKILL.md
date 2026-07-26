---
name: validate
description: Run the repo validation gate before a milestone or handoff commit. Use when finishing a unit, before commit, or when the user asks to validate.
---

# Validate

## Steps

1. From repo root, run:
   `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\validate.ps1`
2. If only a narrow change and full validate already passed this session, you may run targeted `dotnet test` on touched test projects — say so explicitly.
3. Report: exit code, failing projects, skipped checks, test totals if present.
4. Do not commit from this skill unless the user also invoked `commit-unit` / asked to commit.

## Done

- Clear PASS/FAIL
- List anything that could not be run
