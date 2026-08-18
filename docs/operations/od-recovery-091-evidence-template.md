# OD-RECOVERY-091 live-run evidence — HP Phase-4 repeat on medvedkovo (COMPLETE)

**Status: COMPLETE (2026-08-11) — `twoReplayRepeatability = true` for HP.**

The Phase-4 rule requires the entity-base current-health signed int16 found
live on savanna (OD-RECOVERY-087, `[entity+0xB8]`) to agree on a second
content-distinct 11.19.0.10 replay (medvedkovo). It now does: the automated
contract re-verdicts the session's immutable dumps to **HIT** — score 1.0,
flatness 1.0, **Strict 4/4 exact-sum matches at `+0xB8`** — via the
bounded **lead-side** attribution window shipped this session
(`hp-diff --lag-lead-seconds`). The at-session verdict was an honest
negative (Strict top candidate 0x4E ≠ 0xB8); root cause = the medvedkovo
G2 label skew (memory clock LEADS the decoded clock by ~2.5 s —
OD-RECOVERY-089 measured the identical sign for yaw), which the
one-directional (From − lag, To] attribution window structurally cannot see
(an event whose WRITE landed in a window has a decoded time POSTDATING that
window's `To`). Fixed additively (default 0 = unchanged), re-verdict on the
SAME immutable dumps → HIT; the savanna dumps re-verify unchanged
(regression-free). HP publication is now READY (operator approval only) via
`docs/operations/g1-hp-publication-draft.md`.

**Victim RE-SCOPED (2026-08-11, pre-session):** the originally-planned
2549399 (vandal13) is TEAM 2 — the resolver's entity-map trees are the
movement-filter family (player's own team only), so `EntityNotFound` at
30/60/113 s was structural, not timing (probes: 2549399 `EntityNotFound
entity-maps` while 2549408 `Resolved` at the same instant; participants
confirm team 2). The qualified team-1 victim is **2549395 (dudster_2015,
Pz.Kpfw. II)** — 7 hits / 520 dmg / 4 windows at 92.7–104.7 s, probed
`Resolved` live at replay 177 s. Control times 130,200 (after the last hit
at 104.69 s, inside the ~271 s battle).

## Session summary

Approved live session on medvedkovo (content-distinct 11.19.0.10 replay),
victim **2549395** (team 1, dudster_2015). Four launches: three were the
2549399 team-2 dead ends (30 s / 60 s / 113 s `EntityNotFound` — structural,
correctly failing closed), the fourth carried the re-scoped victim to
completion.

- Launches: 4 (3 honest aborts on the team-2 victim, 1 completed run)
- `battleSession=` (completed run): `019ff2f7-b958-7cc4-8a9d-5761ba4a55f9`
- Driver verdict: at-session `hit=False` (honest negative — Strict top
  candidate 0x4E vs 0xB8); **re-verdict after the additive lead-side fix:
  HIT**
- Dumps: 58 (`hp-phase4-091-snapshots.json`)
- Candidate offset: **0xB8** (entity-base current-health signed int16;
  int32-at-0xB8 lenient 4/4 includes the killing window's alive-byte flip)
- Automated contract: score 1.0 / flatness 1.0 / **Strict 4/4** at
  `+0xB8` (`--lag-tolerance 4 --lag-lead-seconds 4`)
- Byte-level track: 520 → 443 → 303 → 227 → 55 → 0, drops 140 = 36+104,
  76 = 76, 172 = 94+78, 55 = 55 — every drop equals its damage subset
  EXACTLY (443 = 520 − 77 pre-window); max `+0x11C` 520 constant, alive
  `+0xBA` flips exactly at 0, healing `+0x11E` 0 constant
- Measured memory-apply skew: medvedkovo LEADS the decoded clock by ~2.5 s
  (per-dump, spread ~5.6 s — the OD-089 yaw finding, now reproduced on the
  HP field)
- G2 anchor: launcher-owned, blitz-log marker moment (every dump
  `sameDecodedClockProven=true`)

## Run

```text
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/launch-offline-replay-for-od.ps1 `
  -ReplayPath <dead-rail.wotbreplay> -RepoRoot <root>   # logs battleSession=<guid>
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/invoke-hp-diffing-session.ps1 `
  -SessionId <launch-matched host-store session> -VictimEntityId 2549395 `
  -LiveAcquire -ControlTimes 130,200 -SnapshotsPath .data/hp-phase4-091-snapshots.json `
  -DataRoot "$env:LOCALAPPDATA\WotBTreader"
```

Re-verdict (offline, same immutable dumps — the sanctioned OD-089 playbook):

```text
dotnet src/WotBTreader.Host.Cli/bin/Debug/net10.0/WotBTreader.Host.Cli.dll hp-diff `
  .data/hp-phase4-091-snapshots.json --session 019ff2f7-b958-7cc4-8a9d-5761ba4a55f9 `
  --victim 2549395 --mode lenient --int16 true --lag-tolerance 4 --lag-lead-seconds 4 `
  --json --data-root "$env:LOCALAPPDATA\WotBTreader"
```

IMPORTANT (087 class): `-DataRoot` must be the HOST store (the repo-local
`.data\treader.db` 404s in the host); the launcher now places the resized
game window at the second monitor's top-left when one is attached
(`resize_window second_monitor` — verified live on this session's
launches, 2026-08-11). The driver's `-LagLeadSeconds` (default 4) covers
the medvedkovo lead; `-LagToleranceSeconds` default 12 covers the savanna-side
lag.

## Verdict contract (unchanged from 087 + additive lead side)

- QUALIFY: `--hp-delta --victim-entity 2549395` requires >= 2 damage
  windows (known good: 4 windows / 520 dmg / 92.7–104.7 s).
- DUMP: entity-base region (320 B), `-RegionAnchor entity-base`, every dump
  requires `sameDecodedClockProven=true` (fail-closed).
- VERDICT: `hp-diff` with subset-sum lag attribution — score 1.0, flatness
  1.0, >= 2 exact-sum Strict matches; control windows from the flat control
  times (130, 200). **Lead side (NEW):** `--lag-lead-seconds` extends the
  attribution window forward to (From − lag, To + lead] for replays whose
  memory clock LEADS the decoded clock (medvedkovo −2.5 s); default 0 =
  exact, unchanged; validated to require `--lag-tolerance > 0`; 3 new
  correlator tests (lead exact match in Strict, default-zero unchanged,
  no fabricated match for a larger event).

## Success definition (met)

`twoReplayRepeatability: true` for HP at `+0xB8` (agrees with
OD-RECOVERY-087 on savanna) — the Phase-4 two-replay HP rule CLOSES and
the HP publication package (`entity-base +0xB8` current / `+0x11C` max
chains) is pre-staged at `docs/operations/g1-hp-publication-draft.md` for
the operator gate. Honest-negative lessons preserved: a non-team-1 Dead
Rail victim can never resolve via entity-region (resolver entity-map trees
= movement-filter family, own team only) — future Phase-4 re-attempts must
pick team-1 victims; and one-directional lag attribution cannot see a
memory-LEAD replay — the bounded bidirectional window is required.

## Evidence

| Item | Value |
|---|---|
| Launches | 4 (3 team-2 aborts + 1 completed) |
| Executable SHA-256 | `1cda5c31919c9784a41bee7f3270ec1b4536b124c51e8b36f2221b381760307d` |
| Replay | medvedkovo |
| Victim | 2549395 (team 1, dudster_2015) |
| Battle session (completed) | `019ff2f7-b958-7cc4-8a9d-5761ba4a55f9` |
| Dumps | `hp-phase4-091-snapshots.json` (58) |
| Candidate offset | **0xB8** (signed int16, entity base) |
| Automated verdict | at-session `hit=False` (Strict 0x4E vs 0xB8 — matcher limitation); re-verdict **HIT** — score 1.0, flatness 1.0, Strict 4/4 |
| Byte-level track | 520 → 443 → 303 → 227 → 55 → 0; drops 140, 76, 172, 55 == damage subsets (36+104), 76, (94+78), 55 |
| Max HP `+0x11C` | 520, constant |
| Alive byte `+0xBA` | 1 → 0 exactly at 0 |
| Healing `+0x11E` | 0, constant |
| Measured memory-apply skew | medvedkovo leads decoded clock ~2.5 s (OD-089-class; per-dump spread 5.6 s) |
| G2 anchor | launcher-owned, blitz-log marker moment |
| Region | entity-base, 320 B (covers +0x11E) |

## Ledger + workflow updates (done this session)

- Register row `OD-RECOVERY-091` in `docs/operations/offset-discovery-ledger.md`
  (Historical experiment index) + result section appended (immutable).
- `docs/operations/offset-discovery-workflow.md`: Session ID -> next
  session; 091 marked done (HIT via additive matcher fix, OD-089 playbook).
- Handoff: `docs/operations/handoffs/2026-08-11-hp-phase4-closed.md`.
- HP publication draft: `docs/operations/g1-hp-publication-draft.md`
  (READY, operator approval only).
