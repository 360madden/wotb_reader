# Reforged Update — DAVA → Unreal Engine 5 Migration (STRATEGIC RISK)

**Status:** Verified 2026-07-31 against official Wargaming announcements + community sources.
**Bottom line:** Wargaming (MS-1 Studio) is migrating WoT Blitz from the proprietary DAVA
engine to **Unreal Engine 5**. The Reforged update was announced for **June 17, 2026** and
was **postponed indefinitely** days before launch. The current live client (11.19/11.20)
still runs on DAVA — but every replay-format, memory-offset, and log-monitor assumption in
this pack is tied to DAVA and **will break when Reforged ships**.

## What Reforged Is

Reforged is a full engine + content overhaul, not a normal patch:

- **Engine:** DAVA → **Unreal Engine 5** (leveraging UE 5.3–5.5 features)
- **Assets:** 700+ tanks re-created to an HD PBR standard; garages re-crafted
- **New effects/physics:** object destruction, main-gun shockwaves, heat distortion,
  tank-flipping mechanics
- **Economy/UI rework:** tech-tree restructure (tier 1–3 → "classic/vintage" tier),
  credits renamed to "silver", research coins, equipment/matchmaking/tank-role rework

## Timeline (verified)

| Date | Event |
|------|-------|
| 2025 | Reforged "Ultra/Preview" test weekends on test servers |
| Early 2026 | Continued test builds (community calls them "UT3/UT4") |
| 2026-06-17 | **Announced release date** — coinciding with game anniversary |
| Early June 2026 | **Postponed indefinitely** (≥3 months) — optimization, mobile perf/battery, community feedback |
| 2026-07 | Live client still on DAVA; 11.19 released, 11.20 in progress |

## Why This Matters to This Project

Every subsystem this project relies on is DAVA-coupled and will be invalidated or changed
by Reforged:

1. **Replay format (.wotbreplay)** — DAVA scenario/event stream. UE5 client will almost
   certainly record a different format (or none at launch). The `wotb-11.x-strict`
   decoder, pickle/protobuf boundary, and event-packet offsets all assume DAVA.
2. **Memory research** — historical DAVA candidates such as entity-list
   `Base+0x03E91978` and position `+0x68/+0x6C/+0x70` would be invalidated
   wholesale. Those candidates are already unverified/refuted for the current
   DAVA build and are not published offsets.
3. **Native logs / lifecycle markers** — `blitz-logs_*.txt`, `START_REPLAY_LOCAL` /
   `STOP_REPLAY_LOCAL` markers are DAVA log plumbing. Unreal uses a different logging
   pipeline and file layout.
4. **Paths** — `%LOCALAPPDATA%\wotblitz\DAVAProject\` may be renamed/reorganized
   ("DAVAProject" is itself a DAVA-ism).
5. **Command-line/file association** — likely preserved (industry-standard), but replay
   argv handling may change with the new launcher/UI.

## What This Means for Strategy

- **Short term (now–Reforged):** DAVA-era research has a finite shelf life. Continue
  the 11.19 work, but do not invest in long-lived DAVA-only tooling (memory
  manipulation, deep struct mapping) that Reforged will obsolete.
- **Replay pipeline:** The managed-launch + lifecycle-monitor pipeline is
  format-agnostic at its core (launch exe → watch logs → read markers). It may survive
  Reforged with only the marker/format tables updated.
- **Watch items:**
  - New Reforged release date (wotblitz.com news)
  - Any announcement about replay support in the UE5 client
  - Changes to `DAVAProject` path / log file naming
- **Decision matrix impact:** Approaches D (memory manipulation) and deep offset
  research (Ghidra/DVA structs) drop in ROI. Approaches A/C/E/F (re-invoke, managed
  pipeline, fast restart, uploaded delivery) are more future-proof because they lean on
  process launch + file association + logs rather than DAVA internals.

## Sources

- Official Reforged date announcement: `na.wotblitz.com/en/news/common/reforged-date/`
- Tanks in Reforged: `wotblitz.com/en/news/updates/tanks-wot-blitz-reforged-update/`
- Reforged release notes (EU): `eu.wotblitz.com/en/content/reforged-update/`
- NamuWiki (Reforged overview): <https://en.namu.wiki/w/월드 오브 탱크 블리츠/Reforged 업데이트>
- Reddit postponement thread: `reddit.com/r/WorldOfTanksBlitz/comments/1u02wdi/`

## Open Questions

1. Will the UE5 client keep `.wotbreplay` (converted) or introduce a new extension?
2. Will `START_REPLAY_LOCAL`/`STOP_REPLAY_LOCAL` markers survive the log rewrite?
3. Will the "Uploaded" replay tab and file-association flow survive the new launcher?
4. When does Reforged actually ship (was "delayed 3+ months" from June 17)?
