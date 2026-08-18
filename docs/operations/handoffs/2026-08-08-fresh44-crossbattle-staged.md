# OD M3 — FRESH44 CROSS-BATTLE ROUND STAGED: BLK-0019 UNBLOCKED (2026-08-08)

**Session outcome:** BLK-0019 (`independentReplays`) is unblocked — a second,
genuinely independent 11.19.0 replay was found in the user's own replay folder
and decoded through the import pipeline (OD-RECOVERY-058). The correlate's M3
evidence is now made durable (`seriesEvidence`), and the whole cross-battle
round is reduced to one command (`fresh44.cmd` / `scripts/invoke-fresh44-crossbattle.ps1`).
**No live round was run this session; FRESH44 is staged and validated, awaiting
operator go-ahead.**

## Repository state

- Branch: `freebuff/regain-ccontext-ef8b8a29-…` (worktree), 18 commits ahead of
  `origin/main`, working tree clean.
- Head: `7b550b3` (feat: fresh44.cmd — one-command cross-battle round wrapper).
- Commits this session:
  - `d7aa292` — docs(od): OD-RECOVERY-057 (FRESH43 arm-snapshot anomaly)
  - `c588f42` — feat(od): persist strong-survivor sampled series as durable M3 evidence
  - `15ccb4f` — docs(od): OD-RECOVERY-058 (BLK-0019 unblocked)
  - `7b550b3` — feat(od): fresh44.cmd — one-command cross-battle round wrapper

## What happened this session

### 1. OD-RECOVERY-057 — the 0.933 staging match vs arm-snapshot anomaly (`d7aa292`)

The FRESH43 contradiction was resolved evidence-first: the correlate scored both
family members at **0.933 (14/15 samples, tolerance 0.001) against decoded world
coordinates** (the provider reads the same `raw_x/raw_y/raw_z` columns), yet the
interceptor's arm-time snapshots (~1s later) read values **outside the whole
battle envelope** (x=274.0 vs session max |x|=130; z=296.3 vs max |z|=251; tank
top speed 14.8 m/s makes a ~300-unit jump in 1s impossible). Conclusion: the
member addresses are **transient multi-copy buffers** that hold position data
only during the staging window, then get reused. M3 stable-read is not satisfied
by trace-time snapshots — the correlate-time match is the real M3 evidence.

### 2. feat: durable M3 evidence (`c588f42`)

The 057 finding exposed that correlate summaries discarded the exact
(memory float, wall-time) samples that constituted the match. The od-048 report
now persists a `seriesEvidence` block: raw sampled series (bounded 240
samples/address) for every strong-survivor address, so the next live round's M3
evidence survives the run and can be re-aligned against decoded ground truth
offline. Unit-tested with a StrictMode mock; full PSScriptAnalyzer gate green.

### 3. OD-RECOVERY-058 — BLK-0019 unblocked (`15ccb4f`) — the session's breakthrough

Probing `AppData/Local/wotblitz/DAVAProject/replays/` found **18 saved 11.19.0
battles**: 16 are re-recordings of the FRESH43 medvedkovo payload (identical sha
`59c3b92e…`), but **two files are a distinct, independently recorded savanna
battle from 2026-08-02** — the game-named save
`20260802_1615__mrkool1138_GB08_Churchill_I`. Imported via the CLI
(`WotBTreader.Host.Cli`) as session **`019fdff7-8dcf-7426-8547-9fb8cc3eb07b`**
(sha `0fae5612…`, map savanna, 14 participants, 26 822 position samples).

- **Same player (mrkool1138) and same tank (GB08_Churchill_I) as FRESH43** —
  ideal for cross-battle M3 validation; viewpoint confirmed real
  (`bot_status = 0`).
- Ground-truth envelope x ∈ [−254, 198], z ∈ [−248, 186] — real world
  coordinates in the expected M3 sanity band.
- Distinctness verified by hash: `0fae5612…` ≠ `59c3b92e…`; the DB previously
  held exactly one 11.19.0 session, now two.
- BLK-0019 resolution note appended to `docs/operations/blocker-log.md`
  (append-only per convention).

### 4. feat: one-command cross-battle round (`7b550b3`)

- **`fresh44.cmd`** (repo root) — thin wrapper following the `crosscheck.cmd`
  pattern (setlocal, quoted `%~dp0`, pwsh→powershell fallback, `exit /b
  %ERRORLEVEL%`, ASCII-only + CRLF).
- **`scripts/invoke-fresh44-crossbattle.ps1`** — tracked runner, fail-closed
  gates before anything touches the game:
  1. **Replay gate** — savanna replay must exist, be exactly 1 045 525 bytes,
     and hash to `0fae5612…` (wrong/tampered file refused, exit 1).
  2. **Artifact preflight** — driver, launch script, and a *fresh* Host.Web
     build (newer-source check).
  3. **Decoded ground-truth probe** — read-only SQLite probe confirms session
     `019fdff7` is in `treader.db` before launching.
  4. **Launch** — proven FRESH43 invocation through the od-049 driver; driver
     stream teed to the log and shown live.
  5. **Report** — result JSON, autoloop log, newest `od-048-autotrace-*.json`
     capture/family files, survivor summary (address, axis, score, shift,
     match count).
- `fresh44.cmd -CheckOnly` runs every gate without launching the game — verified
  end-to-end (hash OK, ground truth OK, exit 0). Negative gates verified: wrong
  size → refusal, exit 1.
- A code review caught two real bugs, both fixed: the claimed log file was never
  written (now `Tee-Object`), and python stderr merging could break the parse
  under StrictMode (now `2>$null`).

### 5. FRESH44 staged (offline prep only)

- Savanna replay copied to `.data/launch/savanna-20260802-crossbattle.wotbreplay`
  (sha verified above).
- `.data/launch-fresh44-crossbattle.ps1` written with the proven invocation:
  `-PlaybackSpeedEstimate 2.4 -StageMinBattleSeconds 30 -AutoTraceSeconds 25
  -ArmSourceOnFirstHit`.
- Verified: driver accepts all args, interceptor publish is current (Aug 7
  08:38 matches last source edit — no republish needed), host resolves exactly
  one session by sha `0fae5612`.

## Changed files

| Path | Change |
|---|---|
| `docs/operations/offset-discovery-ledger.md` | +OD-RECOVERY-057, +OD-RECOVERY-058 rows/sections (52 → 53 rows) |
| `docs/operations/blocker-log.md` | BLK-0019 resolution note appended |
| `docs/operations/handoffs/2026-08-08-fresh44-crossbattle-staged.md` | this handoff |
| `scripts/od-048-monitor-correlate-session.ps1` | `seriesEvidence` persistence block |
| `fresh44.cmd` | **new** — thin cmd wrapper |
| `scripts/invoke-fresh44-crossbattle.ps1` | **new** — cross-battle round runner |
| `.data/launch-fresh44-crossbattle.ps1` | **new** (untracked) — FRESH44 launch script |
| `.data/launch/savanna-20260802-crossbattle.wotbreplay` | **new** (untracked) — staged replay copy |
| `offline/file-tree.md` | refreshed for new tracked files |

## Validation run

- PSScriptAnalyzer gate: **green** (111 files, 0 violations; zero findings on the
  new runner).
- `offline_check.py`: **green** (39 sections / 53 rows; file-tree refreshed).
- Runner functional tests: preflight OK (exit 0, no python warning), wrong-size
  refusal (exit 1), `-CheckOnly` through the cmd wrapper (exit 0, no game
  launch).
- `ReplayInspector` probe: 18 user replays are 11.19.0; savanna battle distinct
  and independently recorded.

## Assumptions and unknowns

- The savanna replay is treated as independent because it is a different file
  with a different map/time and a different sha — it was recorded by the same
  player in the same game version, which is the realistic definition of
  "independent replay" for this project (per BLK-0019's intent).
- M3 stable-read is **not** satisfied by trace-time snapshots (057); the
  correlate-time match (0.933, tolerance 0.001) is the strongest current M3
  evidence, and it is now persisted durably for the next round.
- No static root exists for the entity container (055 refutation stands); the
  candidate layout `[entity+0x3C]+0x1C/20/24` (position) and `+0x60` (matrix)
  is unchanged and promotion goes through the M3 live-read path.

## Integration risks

- **Live launch pending operator approval** — `fresh44.cmd` (no flags) launches
  the game; denied-by-default policy applies. `-CheckOnly` is safe to run any
  time.
- `seriesEvidence` adds a key to the od-048 result JSON — no shape-asserting
  validator exists (checked), so no breakage; bounded to 240 samples/address to
  cap output size.

## Recommended next steps (priority order)

1. **Run FRESH44 live** (`fresh44.cmd` with operator go-ahead) — the first
   cross-battle round. If the correlate match repeats on the savanna battle
   (0.9-floor family + member scores), that is cross-battle repeatability on
   `independentReplays = 1`, clearing the remaining M3 evidence bar.
2. **On a family hit, arm the position triple `[obj+0x1C..0x24]`** and compare
   captured values against decoded ground truth at the same replay clock —
   the direct M3 live-read (057's recommended shrink of the staging→arm gap).
3. If repeatability holds, **frame promotion** around correlate-match +
   cross-battle repeatability + `independentReplays`, and open the promotion
   review.
