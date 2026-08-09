---
when_to_load: Need the map of scripts/docs/contracts; onboarding or lost agent.
do_not_load: Ordinary feature work when AGENTS.md task decision tree already answers.
---

# Canonical paths

| Need | Path |
|------|------|
| Human setup | `README.md` |
| Agent entry | `AGENTS.md` |
| Offline discovery pack | `offline/README.md` (repo map, entry points, API surface, glossary, commands, replay format, offset discovery) |
| Cursor index | `.cursor/README.md` |
| Validate | `scripts/validate.ps1` |
| Blockers | `docs/operations/blocker-log.md` |
| Session handoffs | `docs/operations/handoffs/` |
| Architecture overview | `docs/architecture/` (if present) |
| Read API contracts | `src/WotBTreader.Host.Web/Contracts/` |
| Synthetic replay fixtures | `tests/WotBTreader.TestSupport/` |
| Solution | `WotBTreader.sln` |
