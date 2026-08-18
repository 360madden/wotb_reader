# OD-RECOVERY-087 live-run evidence — L1 HP (filled 2026-08-11)

**Verdict: HIT — the entity-base current-health int16 is confirmed LIVE at
`[entity+0xB8]`.** Two independent live datasets prove it: (1) the byte-level
track — every one of the 8 health drops equals its damage sum EXACTLY
(1550 → 1401 → 1228 → 1054 → 890 → 722 → 580 → 382 → 367), max HP `+0x11C`
constant at 1550, alive byte `+0xBA` constant 1, healing `+0x11E` constant 0;
(2) the automated contract — with the new subset-sum lag attribution
(`hp-diff --lag-tolerance 4`), the verdict is **HIT: score 1.0, flatness 1.0,
Strict 8/8 exact-sum windows at offset 0xB8**. `hpLiveAtEntityBaseOffset`
claimable; the X4 live frame's `hp: null` can become real (additive).

## Session summary

Approved live session on savanna (content-distinct 11.19.0.10 replay,
battle 2026-08-02T21:15:07), victim **3760578** (takes real damage; the
viewpoint player took 0 in both replays). Four launches were needed:

1. Launch 1 (`019ff182-…`) — gate OK; the first driver run (pre-fix pacing)
   dumped only 3 snapshots (30 s control + the 90.25/90.65 pair) then died
   with `rendezvous_unavailable`: the wait probe had no transient retry, so a
   single missed rendezvous read killed the session. Harness finding #1.
2. Launch 2 — gated; driver hardened (transient retry on the wait-probe path,
   `-f` precedence cosmetic fix). 20 dumps acquired, but the automated verdict
   was an honest negative: **top candidate 0x3C (the tank-record pointer
   field), score 0.714, flatness 0** — a spurious pointer match. The raw
   bytes told the real story: `+0xB8` dropped EXACTLY with every damage sum,
   but the game applies decoded damage events to the health field with a
   **variable ~1–10 s memory-apply lag** (sparse dumps made it look up to
   10 s), so the tool's time-window attribution could not see the lagged
   events. Harness finding #2 (the big one).
3. Harness fix shipped: the driver now dumps a **dense span** around every
   hit (hit−1 s then every ~2 s to hit+13 s — 74 dumps total), and the
   correlator gained the **subset-sum lag attribution**
   (`--lag-tolerance`, default 0 = unchanged; matches each drop against the
   sum of a subset of its candidate events, each event consumed once), with
   5 new tests. Launch 3 (`019ff1a2-…`) — the confirmation run: 74 dumps
   acquired, and the automated verdict **HIT at 0xB8 (score 1.0, flatness
   1.0, Strict 8/8)** with `--lag-tolerance 4`.
4. The dense span also measured the TRUE per-event lag: **~1.0–3.4 s**
   (the earlier 10 s readings were sparse-dump artifacts) — each event's
   write now lands in its own ~3 s change window.

## Run

```text
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/launch-offline-replay-for-od.ps1 `
  -ReplayPath <savanna.wotbreplay> -RepoRoot <root>   # logs battleSession=<guid>
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/invoke-hp-diffing-session.ps1 `
  -SessionId <launch-matched host-store session> -VictimEntityId 3760578 `
  -LiveAcquire -ControlTimes 30,230 -SnapshotsPath .data/hp-snapshots.json `
  -DataRoot "$env:LOCALAPPDATA\WotBTreader"
```

IMPORTANT (found live, same class as 086): the driver's QUALIFY extractor
defaulted to the repo-local `.data\treader.db`, which does NOT hold the
launch-matched session — `-DataRoot` now derives the extractor `--db` and the
`hp-diff --data-root` from the SAME host store. The driver's default
`-LagToleranceSeconds 12` was tightened to **4** for the confirmation run
(the measured bound ~3.4 s + margin); 12 over-attributes and dilutes the
score.

## Evidence (filled)

| Item | Value |
|---|---|
| Launches | 4 (3 gated OK, 1 rendezvous-fragility abort on the pre-fix driver) |
| Executable SHA-256 | `1cda5c31919c9784a41bee7f3270ec1b4536b124c51e8b36f2221b381760307d` |
| Replay | savanna (savanna), 1,045,525 B, battle 2026-08-02T21:15:07 |
| Victim | 3760578 (Churchill I, team 1) — 9 damage events, 1,183 total damage |
| Dumps (confirmation run) | 74 (`hp-snapshots4.json`, dense span hit−1…hit+13 + 30/230 s controls) |
| Candidate offset | **0xB8** (entity-base current-health signed int16) |
| Automated verdict | **HIT** — score 1.000, flatness 1.000, matched 8/8 Lenient, Strict 8/8 exact sums (`--lag-tolerance 4`) |
| Byte-level track | 8 drops, every drop == its damage sum exactly (149, 173, 174, 164, 168, 142, 198 = 41+157, 15) |
| Max HP `+0x11C` | constant 1550 across all 74 dumps |
| Alive byte `+0xBA` | constant 1 |
| Healing `+0x11E` | constant 0 |
| Measured memory-apply lag | ~1.0–3.4 s per event (dense-span measurement; variable, not constant) |
| G2 anchor | launcher-owned, blitz-log marker moment (every dump `sameDecodedClockProven=true`) |
| Region | entity-base, 320 B (0x140, covers healing +0x11E) |

1. **QUALIFY (offline, real):** `replay-delta-extractor.py --hp-delta
   --victim-entity 3760578` → 9 events / 1,183 damage / 6 windows with the
   dense-span dump schedule.
2. **DUMP (live, GATED seam):** `POST /discover/entity-region`
   (entity-base anchor, 320 B, replay-clock labeled,
   `sameDecodedClockProven` required, fail-closed) at the dense span around
   each hit + flat controls.
3. **VERDICT (offline, real):** `wotbtreader-cli hp-diff <snapshots.json>
   --session <id> --victim 3760578 --mode lenient --int16 true
   --lag-tolerance 4` → **HIT** (score 1.0 + flatness 1.0 + Strict ≥ 2).

Evidence lands in `.data/`:
- `hp-snapshots-<session>-<stamp>.json` (schema
  `wotbtreader.od.hp-diff.snapshots.v1`) — the region dumps (confirmation
  run: `.data/hp-snapshots4.json`, 74 dumps; earlier run:
  `.data/hp-snapshots.json`, 20 dumps — both exact-correlation).
- The verdict output: `VERDICT: hit=True reason='HIT: score 1.0, flatness
  1.0, >= 2 exact-sum Strict matches'`; top candidate 0xB8, score 1.0,
  flatness 1.0, matched 8/8; strict 0xB8 8/8.
- Without a reachable web host the driver exits 3 with the contract;
  `-SnapshotsPath` replays an existing dump file through the verdict.

## Known static values (do not change without re-verifying)

| Item | Value |
|---|---|
| Target build | 11.19.0.10 |
| Executable SHA-256 | `1cda5c31919c9784a41bee7f3270ec1b4536b124c51e8b36f2221b381760307d` |
| Replay 1 | savanna, victim **3760578** (takes real damage — viewpoint player took 0 in both replays) |
| Replay 2 (Phase-4 repeatability) | medvedkovo, victim **2549399** (18 events / 4,647 damage) |
| Region anchor | `entity-base` (HP lives on the entity base record, NOT the tank record `[entity+0x3C]` transform) |
| Region length | ≥ 0x120 (driver default 320 = 0x140, covers healing `+0x11E`) |
| Correlate | **signed int16** (`hp-diff --int16 true`, on by default for the HP/decrement direction) |
| G2 bound | `SameDecodedClockUncertaintyLimit` = 2 s; every dump must attest `sameDecodedClockProven=true` (fail-closed) |
| Static map (VerifyPlayerHpChain 26/26, 2026-08-11) | current int16 `+0xB8`, alive byte `+0xBA`, max int16 `+0x11C`, healing int16 `+0x11E` |
| Expected live correlate | `+0xB8` current-health int16 — **CONFIRMED LIVE** |
| Lag tolerance | measured memory-apply lag ~1.0–3.4 s; driver default 4 s (the HP path only; damage-dealt keeps 0) |

## Ledger section skeleton — `OD-RECOVERY-087`

Append to `docs/operations/offset-discovery-ledger.md` (and add the index
row + Last-updated + status-line amendment in the same change). YAML block:

```yaml
sessionId: OD-RECOVERY-087
status: Hit - the entity-base current-health int16 is CONFIRMED LIVE at
  +0xB8 (byte-level exact track + automated contract HIT with the subset-sum
  lag attribution); the X4 live frame hp: null can become real
mode: invoke-hp-diffing-session.ps1 -SessionId <launch-matched> -VictimEntityId
  3760578 -LiveAcquire -ControlTimes 30,230 on savanna (4 launches, 3 gated
  OK): launcher to OfflineReplayVerified (G2 anchor at the blitz-log marker),
  /discover/entity-region entity-base dumps (region 320, replay-clock labeled,
  sameDecodedClockProven required) on a DENSE SPAN around each hit (hit-1 to
  hit+13 every ~2 s) + flat controls -> hp-diff --int16 true --lag-tolerance 4
  verdict (Lenient then Strict, subset-sum attribution)
targetBuild:
  version: 11.19.0.10
  executableSha256: 1cda5c31919c9784a41bee7f3270ec1b4536b124c51e8b36f2221b381760307d
liveRun:
  launcherExit: 0 (3 gated launches)
  gate: OK OfflineReplayVerified
  decodedSessionId: 019ff1a2-0249-764c-9216-146cfeb88075 (confirmation run,
    launch-matched host store)
  victimEntityId: 3760578
  damageEvents: 9 (1,183 total damage)
  dumpsTaken: 74 (confirmation run; 20 in the earlier run)
  allDumpsClockAttested: true
  candidateOffset: 0xB8 (entity-base current-health signed int16)
  lenientScore: 1.000 (8/8)
  flatness: 1.000
  strictWindowsMatched: 8 (exact sums; 8/8)
  byteTrackDrops: 8 (every drop == its damage sum exactly:
    149, 173, 174, 164, 168, 142, 198=41+157, 15)
  maxHpAt11C: 1550 constant
  aliveAtBA: 1 constant
  healingAt11E: 0 constant
  memoryApplyLagSeconds: ~1.0-3.4 (measured, variable per event)
  verdict: HIT
proof:
  hpLiveAtEntityBaseOffset: true (candidate = +0xB8 with flatness 1.0,
    Strict 8/8 exact-sum windows, plus the exhaustive byte-level track)
  twoReplayRepeatability: false - pending the identical flow on medvedkovo
    victim 2549399 (Phase-4 rule; the harness fixes from this session make
    the identical flow work there)
  liveFrameHpBecomesReal: true - the X4 live frame can fill hpCurrent/hpMax
    additively (contract change proposed with the evidence)
```

## What the verdict decides (branch on the evidence)

- **Candidate = `+0xB8` with flatness 1.0 and ≥ 2 Strict exact-sum windows
  → this is the confirmed branch.** Score 1.0, flatness 1.0, Strict 8/8
  exact sums, AND the byte-level track is exhaustive (every drop explained,
  no unexplained drops, max/alive/heal constants). `hpLiveAtEntityBaseOffset`
  claimable; the X4 frame contract can gain `hpCurrent/hpMax` additively
  (empty bar becomes real; `docs/operations/live-frame-loop-design.md`
  honest-limits table row for HP flips from ⏳ L1 to ✅).
- **Harness findings shipped by this session (committed):** (1) the wait
  probe got a bounded transient retry on `rendezvous_unavailable` — a single
  missed rendezvous read previously killed the whole session; (2) the driver
  dumps a DENSE SPAN around each hit instead of one before/after pair, and
  the correlator gained the **subset-sum lag attribution**
  (`--lag-tolerance`, default 0 = exact behavior unchanged) — the game
  applies decoded damage events to the health field with a variable
  ~1–3.4 s lag, so time-window attribution alone could never match the live
  writes; the subset model matches each drop against the sum of its event
  group (each event consumed once), which is also what makes the medvedkovo
  repeatability session work; (3) `-DataRoot` now feeds BOTH the QUALIFY
  extractor `--db` and `hp-diff --data-root` (the repo-local DB 404s in the
  host store — same class as 086).
- **Two-replay agreement (Phase-4 rule)** → still pending: the identical flow
  on medvedkovo (victim 2549399, 18 events / 4,647 damage) must agree on
  `+0xB8` before the HP publication (roadmap P2) proceeds through the
  operator gate.

## Files touched

- `docs/operations/od-recovery-087-evidence-template.md` (this file, filled)
- `docs/operations/offset-discovery-ledger.md` (OD-RECOVERY-087 section +
  index row + Last-updated + Next-planned → 088)
- `src/WotBTreader.Core/Discovery/RecordDiffing.cs` (subset-sum lag
  attribution, additive `eventLagToleranceSeconds` default 0)
- `src/WotBTreader.Host.Cli/Cli/CliCommandRouter.cs` +
  `src/WotBTreader.Host.Cli/Cli/CliInvocation.cs` (`hp-diff --lag-tolerance`)
- `tests/WotBTreader.Core.Tests/RecordDiffingTests.cs` (4 new lag tests)
- `scripts/invoke-hp-diffing-session.ps1` (dense-span schedule,
  `-LagToleranceSeconds`, transient rendezvous retry, `-DataRoot` → extractor
  `--db`, BOM-less snapshots write)
- `docs/operations/live-frame-loop-design.md` (HP honest-limits row → ✅)
- Evidence files in `.data/`: `hp-snapshots.json` (20 dumps, earlier run),
  `hp-snapshots4.json` (74 dumps, confirmation run)
