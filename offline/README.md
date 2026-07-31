# Offline discovery pack

A **focused, self-contained index** of this repository, curated for fast
orientation. Read this folder when you need to understand the repo (or re-orient
in a fresh session) without scanning the full tree, and without network access.

It is intentionally **not** a dump: it points at canonical sources
(`docs/`, `knowledge.md`, `AGENTS.md`) instead of duplicating them, and keeps
every file small and high-signal.

## What's inside

| File | When to read |
|------|--------------|
| [`repo-map.md`](repo-map.md) | You want the annotated project layout (src / tests / tools / docs / scripts) |
| [`entry-points.md`](entry-points.md) | You want the *first files to read* for a task, and in what order |
| [`api-surface.md`](api-surface.md) | You need the web host's HTTP endpoints, SignalR hub, ports, and rendezvous |
| [`glossary.md`](glossary.md) | You hit a domain term you don't recognize (decode run, DVPL, artifact, …) |
| [`commands.md`](commands.md) | You need the exact commands to build / test / run / import |
| [`replay-format.md`](replay-format.md) | Deep dive: `.wotbreplay` structure, pickle/protobuf boundary, event packets |
| [`offset-discovery.md`](offset-discovery.md) | Deep dive: game launch → multi-scan → memory-offsets evidence publication |
| [`data-flow.md`](data-flow.md) | Deep dive: telemetry from decode → SQLite → read API/SignalR → overlay & comparison |
| [`memory-offsets.md`](memory-offsets.md) | Deep dive: offset evidence schema, reader validation, runtime gating |
| [`file-tree.md`](file-tree.md) | Physical snapshot of all committed files (`git ls-files`) for path resolution |

## Suggested reading order

1. [`README.md`](../README.md) (human quickstart) or [`knowledge.md`](../knowledge.md) (agent knowledge)
2. This folder — `repo-map.md` → `entry-points.md` → `api-surface.md`
3. Deep dives only when needed: [`docs/architecture/overview.md`](../docs/architecture/overview.md),
   [`docs/architecture/roadmap.md`](../docs/architecture/roadmap.md),
   [`docs/operations/offset-discovery-guide.md`](../docs/operations/offset-discovery-guide.md)

## Validation

```powershell
python scripts/python/offline_check.py
```

Checks that every internal link in `offline/*.md` resolves (external URLs and
fragment anchors are skipped). Exit 0 = all links resolve. Run it after any
edits to this pack, and before committing changes that touch these files.
`file-tree.md` is generated from `git ls-files`; re-run the snippet in its
header when the layout changes.

## Maintenance rules

- **Keep it focused.** One small file per concern; no wall-of-text sections.
- **Link, don't copy.** If the canonical version lives in `docs/`, `knowledge.md`,
  or `AGENTS.md`, point at it. Only restate what's needed for quick orientation.
- **Stay honest.** When the repo layout, API surface, or commands change, update
  these files in the same change. A stale discovery pack is worse than none.
- **Never put private/runtime data here.** This folder is committed and scanned
  by `scripts/scan-repository.ps1` like the rest of the repo.
