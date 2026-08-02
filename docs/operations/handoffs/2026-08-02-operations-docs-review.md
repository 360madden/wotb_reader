# Session handoff — 2026-08-02: operations-docs review and fixes

**Author:** Codex Agent

**Branch:** `main`

**Baseline:** `40fb6b5` (`fix(game): require real managed replay windows`)

**Commit unit:** review of `docs/operations/` and follow-up fixes: a numbering
note amendment closing the BLK-0008–0011 visibility gap (with deep-dive
back-references), deduplication of the offset-discovery guide's install
sections, a new `docs/operations/README.md` document map, and a
blocker-numbering contiguity check wired into `scripts/python/offline_check.py`
so the gate and CI enforce the convention. Nothing is committed yet; the
worktree holds all changes locally and was not pushed.

## Outcome

The `docs/operations/` review found the folder healthy (append-only compliant,
cross-doc consistent, no TODO/FIXME, format-compliant handoffs) with three
actionable items, all now fixed:

1. **BLK-0008–0011 were invisible from the main log.** `blocker-log.md`
   jumped from BLK-0007 to BLK-0012 with no pointer; the numbers live only in
   `docs/operations/blockers/2026-07-26-replay-decoder.md`, and no top-level
   doc referenced the `blockers/` subfolder. Historical confusion was already
   recorded (a handoff once called BLK-0010 "fabricated / not in blocker log"
   while it exists in the deep-dive). Fix: an `## Amendment — Numbering note`
   appended before BLK-0012 (chronologically consistent with the file's
   existing inline amendment pattern), the deep-dive gained a back-reference
   to `../blocker-log.md`, and the companion relationship of
   `blockers/2026-07-26-command-execution-gate.md` (BLK-0007) is documented.
2. **Offset-discovery guide drift.** `Last updated` was 2026-07-31 despite
   2026-08-02 content, and x64dbg + managed-artifact instructions appeared
   twice (manual section vs PowerShell one-liner section). Fix: the One-time
   Setup Commands section now points to the canonical `§1` / `§3` sections
   instead of duplicating them; date bumped to 2026-08-02.
3. **No folder index existed.** New `docs/operations/README.md` provides the
   document map, blocker-numbering convention, append-only rules, and privacy
   guidance; linked from `knowledge.md`.

## Implementation in the worktree

- `docs/operations/blocker-log.md` — numbering note amendment (append-only,
  additive; no prior evidence rewritten).
- `docs/operations/blockers/2026-07-26-replay-decoder.md` — back-reference to
  the main log and the new README.
- `docs/operations/offset-discovery-guide.md` — dedupe + date bump.
- `docs/operations/README.md` — new document map / conventions index.
- `knowledge.md` — pointer to the operations README.
- `scripts/python/offline_check.py` — new `check_blocker_numbering()`: the
  union of `## BLK-XXXX` headers across `blocker-log.md` and `blockers/*.md`
  must be exactly `0001..N` (no gaps, no intra-file duplicates, no two
  deep-dives sharing a number without a main-log owner, main register must
  exist). Runs in every mode, including the gate's `--check-fresh` and CI.
- `offline/README.md` — maintenance rule documenting the contiguity check.
- `docs/operations/offset-discovery-ledger.md`,
  `docs/operations/offset-discovery-workflow.md` — unchanged this session
  (touched only by the earlier uncommitted budget-closeout work).

## Validation performed

- Full `scripts/validate.ps1` gate passed twice (before and after the
  reviewer-hardened check): **520 passed, 2 opt-in skips**; repository scan
  clean (531 tracked files); `file-tree.md` fresh; 70 links resolve; and the
  new check printed `Blocker numbering OK: BLK-0001..BLK-0025 contiguous
  across 3 record file(s)` inside the gate.
- Negative fixtures verified fail-closed behavior: gap (missing BLK-0002),
  intra-file duplicate, missing main register, and two deep-dives sharing a
  number all return exit 1; a valid companion deep-dive + deep-dive extension
  fixture returns 0.

## Reviewer findings addressed

Code review (code-reviewer-glm) confirmed no information was lost in the
guide dedupe and all links resolve, then raised four points — all fixed:

- New tracked file `docs/operations/README.md` will make `file-tree.md` stale
  at commit time: the snapshot comes from `git ls-files`, so the commit must
  stage the new file, run `python scripts/python/offline_check.py --refresh`,
  and commit both. Flagged here as the one required commit-step.
- `collect_blk_numbers()` silently skipped a missing `blocker-log.md`; the
  main register is now a hard requirement.
- Cross-file duplicates were unchecked; a number now may appear in the main
  log + one companion deep-dive, or in one file, but not in two deep-dives.
- Numbering note originally sat at the top of the log; moved to an Amendment
  section at the BLK-0007→BLK-0012 boundary, matching the file's convention.
- Regex tightened from `\b` to `(?:\s|—|:|$)` after the four digits.

## Assumptions and unknowns

- The numbering check treats a deep-dive number that also exists in the main
  log as a legitimate companion repeat; the only current instance is BLK-0007.
- `file-tree.md` remains valid in the worktree because the README is
  uncommitted (by design, the snapshot reflects committed paths only).

## Integration risks

- **Commit order matters:** stage `docs/operations/README.md`, then run
  `offline_check.py --refresh`, then commit — otherwise the gate fails on a
  stale file-tree after the commit.
- The offline pack content (`offline/api-surface.md`, `offline/offset-discovery.md`)
  was already updated by the earlier uncommitted budget work and remains in
  sync; no further pack edits were needed this session.

## Next move

Commit the accumulated uncommitted work (budget closeout + this review
session) in one or two conventional-commit units, following the staging →
`--refresh` → commit order above. The `docs/operations` folder is now
self-indexing and gate-enforced.
