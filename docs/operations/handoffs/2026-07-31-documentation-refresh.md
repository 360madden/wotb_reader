# Session handoff — 2026-07-31: Documentation refresh

**Status:** documentation refresh complete; handoff ready for commit

## Repository state

- Branch: `main`
- Head before this handoff: `6f62422` (`docs(offsets): refresh evidence and architecture guidance`)
- The working tree was clean apart from unrelated untracked files: `.agents/skills/`,
  `.freebuff/`, and `research/reforged-ue5.md`. Those files were left untouched and
  are not part of this handoff.

## What changed in the preceding session

- Refreshed live README, knowledge, architecture, roadmap, offset, research, and
  Cheat Engine documentation to match the current implementation.
- Documented the hash-bound `11.19.0.10` executable evidence and the single
  static-analysis `playerYaw` `Candidate`; seven fields remain unknown.
- Replaced stale hard-denied scanner wording with the current fail-closed
  `OfflineReplayVerified` gate and exact executable/per-field evidence requirements.
- Reworked offset instructions around the actual GameHarness `discover*`, snapshot,
  compare, nearby, and discard commands.
- Clarified that `confidence` is summary metadata only; `fieldValidation.status` and
  its evidence requirements control runtime promotion.
- Removed stale claims about the embedded WebView2 dashboard, retained overlay HTTP
  handlers, unsupported scanner classes, automatic CE orchestration, and direct
  memory-API validation.
- Updated current decoder support, test counts, wrapper counts, and research links.
  Historical handoffs were intentionally not rewritten.

## Current offset evidence

- Installed product version: `11.19.0.10`
- Executable SHA-256: `1cda5c31919c9784a41bee7f3270ec1b4536b124c51e8b36f2221b381760307d`
- Known table fields: `1/8`
- Candidate: `playerYaw` at `0x0317A810`, static-analysis provenance only
- Runtime promotion: unsupported; no field is `Verified`

## Validation

- `python scripts/python/offline_check.py --check-fresh` — passed; 67 links resolve
  and `offline/file-tree.md` is current.
- `python scripts/python/offset_check.py --check-schema` — passed for all 3 offset
  tables; documented schema, JSON schema, and validator constants agree.
- `pwsh -NoProfile -File tools/discover-offsets.ps1 -SelfTest` — passed.
- `pwsh -NoProfile -File tools/report-offset-evidence.ps1 -SelfTest` — passed.
- `dotnet test WotBTreader.sln -c Release --no-build` — 409 passed, 0 failed, 2
  local opt-in skips.
- `git diff --check` — passed.

## Assumptions and unknowns

- Candidate evidence remains untrusted discovery evidence until independent process,
  replay, static, harness, and approval requirements are complete.
- No live game process was attached or modified during repository validation.
- Installed-game HUD smoke testing and dynamic promotion of offsets remain future work.
- The local Reforged / UE5 research note remains untracked and intentionally outside
  this commit.

## Recommended next steps

1. Commit this handoff together with the regenerated offline file-tree metadata.
2. For offset discovery, establish an approved offline replay session before using
   Cheat Engine or GameHarness discovery commands.
3. Cross-check `playerYaw` across two process launches and two independent replays;
   do not promote it from static evidence alone.
4. Keep `.agents/skills/`, `.freebuff/`, and `research/reforged-ue5.md` out of unrelated
   commits unless explicitly requested.
