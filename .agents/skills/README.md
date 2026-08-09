# Agent skills (project-scoped)

Skills installed for this repository, shared with every agent that reads
`.agents/skills/` (Codex CLI, OpenCode, Grok). Skills are folders with a
`SKILL.md` whose YAML frontmatter (`name`, `description`) lets the agent
decide when to load them; the body loads only on trigger, keeping context
lean.

## Installed

| Skill | Invoke | What it does |
|-------|--------|--------------|
| `grill-me` | user-invoked only (`grill-me`, "grill me on this plan") | A stateless, relentless interview that sharpens a plan or design before anyone acts on it. Writes no files. |
| `grilling` | `grilling`, or model-invoked as the core of `grill-me` | The interview mechanism itself: design tree, frontier rounds, `❓`/`➡️` question format, facts-vs-decisions split, confirmation gate. |

`grill-me` deliberately does nothing without the `grilling` core — install
them together (both ship here).

## Usage notes for this repo

- Invoke before acting on anything ambiguous: feature designs, refactors, the
  BLK-0026 launch-diagnosis plan, offset-discovery decisions.
- The skill is a conversation: the **user** owns scope and decisions. The
  agent must never answer its own decisions, and must not start implementing
  until the user confirms the understanding is shared.
- To switch to one-question-at-a-time, add the line from
  `grilling/SKILL.md`'s FAQ to `AGENTS.md`.
- Upstream marks `grill-me` `disable-model-invocation: true` (user-invoked
  only); Codex may not honor that field, so the description is written to
  trigger only when the user asks to be grilled.

## Source and license

Adapted from `mattpocock/skills` (Matt Pocock), MIT License
Copyright (c) 2026 Matt Pocock — https://github.com/mattpocock/skills

Adaptations: `CLAUDE.md` references switched to this repo's `AGENTS.md`;
invocation wording adapted for Codex; glossary hyperlinks removed; the wider
grilling family (`grill-with-docs`, `wayfinder`, `triage`, `ask-matt`) is
upstream-only and intentionally not vendored.
