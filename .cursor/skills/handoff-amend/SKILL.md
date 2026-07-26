---
name: handoff-amend
description: Append a short dated amendment to the active session handoff when a unit finishes. Use at end of a committable unit or when the user asks for a handoff update.
---

# Handoff amend

## Steps

1. Prefer the newest file under `docs/operations/handoffs/` unless the user names another.
2. Append (do not rewrite history):

```markdown
## Amendment — U? short title (`YYYY-MM-DDTHH:mm:ssZ`)

What closed / what changed (facts).
Validation evidence (command + result).
What remains deferred.
```

3. Keep LF, no BOM. Short — not a second README.
4. Include the amendment in the same commit as the unit when practical.

## Done

- Amendment present with UTC timestamp
- No silent edits to older amendments
