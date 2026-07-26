---
name: commit-unit
description: Stage and commit a finished unit with Codex Agent authorship. Use only when the user asked to commit. Push only if the user also asked to push.
---

# Commit unit

## Steps

1. Confirm user requested commit. If unclear, stop and ask.
2. `git status` / `git diff` / recent `git log` for message style.
3. Stage only relevant source/docs (never secrets, never `bin/`/`obj/`).
4. Commit with:
   `git -c user.name="Codex Agent" -c user.email="codex@local.invalid" commit -m "..."`  
   Message: why over what; 1–2 sentences.
5. Push with `git push origin HEAD` **only** if the user asked to push. Never `--force`.
6. Show resulting `git status -sb` and commit hash.

## Done

- Clean or intentionally leftover unstaged files listed
- No amend unless user requested and amend rules in user policy are met
