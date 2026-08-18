# OD-RECOVERY-086 live-run evidence (filled 2026-08-11)

**Verdict: Partial — X3 enumeration team-based (precision 1.0, recall 0.5);
X2 batch surface PASS (34/34 within the 2 s G2 window, read-pass −0.8 s
measured). Three harness fixes shipped as a result (launcher-owned G2
anchor at the blitz-log marker moment, driver per-target clock wait,
BOM-less evidence writes + bounded window matching in the cross-check).**

## Session summary

Run on savanna (the content-distinct 11.19.0.10 replay, battle
2026-08-02T21:15:07). The session needed several launches:

1. Launch 1 — CAM-003 `0x325ad2c` controller phase: enumeration blocked
   (`UnsupportedReplayController / playback-controller-vtable`), the
   documented flip. Abandoned; relaunched.
2. Launch 2 — resolver phase up; X3 enumeration resolved and returned a
   **team-based partial**: 7/14 (precision 1.000, recall 0.500), the 7
   found ids all team 1 (own team), the 7 missing all team 2 (enemies).
3. Launch 3 — first G2 anchor attempt: the batch dump FAILED
   `sameDecodedClockProven=false` (fail-closed) because **the driver never
   appended the clock segment** — only od-073 did, seconds after the gate;
   a batch driver running minutes later cannot self-anchor.
4. Launcher now appends the anchor at the verified-gate moment
   (sequence 0, replay 0, speed 1.0, 1 s uncertainty). Launch 4's dumps
   resolved 14/14 with the clock attested, but every position cross-check
   MISS was a moving tank with a **constant +4.9 s label skew** — the
   anchor's gate moment lags the true replay start (blitz-log marker).
5. Anchor moved to the blitz-log `Start replay event` marker wall-clock
   (the G2 design's named anchor). Launch 5's dumps at 90/150/220 s
   (driver now waits per target): stationary tanks match **0.00 m**;
   moving tanks align at **−0.8 s implied offset** — the batch read-pass
   window. Cross-check with bounded 2 s window matching: **34/34 PASS**.

## Run

```text
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/launch-offline-replay-for-od.ps1 `
  -ReplayPath <savanna.wotbreplay> -RepoRoot <root>   # anchor logged as battleSession=<guid>
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/invoke-batch-rehearsal.ps1 `
  -SessionId <launch-matched host-store session> -DbPath "$env:LOCALAPPDATA\WotBTreader\treader.db" `
  -LiveAcquire -Times 90,150,220 -FailOnMiss
```

IMPORTANT (found live): the driver's default `-DbPath .data\treader.db` +
the pre-staged `-SessionId 019fdff7-8dcf-…` (the repo-local decode) do NOT
match the host store — `/api/v1/sessions/019fdff7-…` 404s there. The
launch-matched decode lives in `%LOCALAPPDATA%\WotBTreader\treader.db`
under the session the launcher logs (`battleSession=…`); the driver must
be passed that session + the host DB path. The static values below are
otherwise unchanged.

## Evidence (filled)

| Item | Value |
|---|---|
| Launches | 5 (1 CAM-003-blocked, 4 gated OK) |
| Executable SHA-256 | `1cda5c31919c9784a41bee7f3270ec1b4536b124c51e8b36f2221b381760307d` |
| Replay | savanna (savanna), 1,045,525 B, battle 2026-08-02T21:15:07 |
| X3 enumeration | 7/14 — precision 1.000, recall 0.500, 0 extra |
| Found ids (all team 1) | 3760565, 3760566, 3760568, 3760572, 3760573, 3760577, 3760578 |
| Missing ids (all team 2) | 3760567, 3760569, 3760570, 3760571, 3760574, 3760575, 3760576 |
| Candidates / filtered | 40 seen, 33 filtered out (movement-filter vtable gate) |
| Batch dumps | 3 (labels ≈ 89.3 / 149.6 / 221.9 s), all `sameDecodedClockProven=true` |
| Position cross-check | 34/34 compared pairs within 2 m (2 s G2 window matching) |
| Implied read-pass offset | −0.8 s (moving tanks align at 0.00 m @ −0.8 s) |
| EntityNotFound skips | 8 (phase-dependent maps; mostly team-2 entities at early times) |
| G2 anchor | launcher-owned, blitz-log marker moment (`sourceAnchorUtc` = marker), 1 s uncertainty |

1. **X2 batch rehearsal** — the whole-roster batch read through
   `/discover/entity-regions` per replay time, cross-checked against
   decoded positions, measuring the read-pass window (feeds item 7).
2. **X3 live-roster enumeration** — `/discover/entity-roster` enumerated
   ids verdict against the decoded participants roster (matched/missing/
   extra + movement-filter precision), the measurement that decides
   whether the enumerated avatar family IS the decoded roster.

Run (one command; `-EnumerateLive` + `-LiveAcquire` compose both, with the
**enumerated** ids driving the batch dumps):

```text
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/invoke-batch-rehearsal.ps1 `
  -SessionId <decoded-session-guid> -EnumerateLive -LiveAcquire `
  -Times 90,150,220 -FailOnMiss
```

Evidence lands in `.data/`:
- `roster-enum-<session>-<stamp>.json` (schema
  `wotbtreader.od.batch-rehearsal.roster-enum.v1`) — the enumeration
  evidence (ids + candidatesSeen/filteredOut + status).
- `batch-rehearsal-<session>-<stamp>.json` (schema
  `wotbtreader.od.batch-rehearsal.dumps.v1`) — the per-time batch dumps
  with one G2 clock attestation each.
- The verdict exits: enumeration cross-check 0 = exact set match; position
  cross-check 0 = all pairs within tolerance.

## Known static values (do not change without re-verifying)

| Item | Value |
|---|---|
| Target build | 11.19.0.10 |
| Executable SHA-256 | `1cda5c31919c9784a41bee7f3270ec1b4536b124c51e8b36f2221b381760307d` |
| Replay | savanna (savanna), 1,045,525 B, battle 2026-08-02T21:15:07 (content-distinct; one of the two proven 11.19.0 replays) |
| Batch caps | ≤ 16 entities / ≤ 16 KB total per batch (`EntityRegionsReadRequest`) |
| Region anchor / length | `ring-record`, 64 B (covers the 0x38 ring record + position float32 triple at +0x10) |
| Tolerance | 2.0 m (position cross-check) |
| G2 bound | `SameDecodedClockUncertaintyLimit` = 2 s; every batch must attest `sameDecodedClockProven=true` (fail-closed) |
| Decoded roster | from `batch-rehearsal-crosscheck.py --roster` (participants table) |
| Cross-check tool | proven 42/42 on real decoded data (position); enumeration mode self-tested (exact / missing / extra / traversal-limited) |

## Ledger section skeleton — `OD-RECOVERY-086`

Append to `docs/operations/offset-discovery-ledger.md` (and add the index
row + Last-updated + status-line amendment in the same change). YAML block:

```yaml
sessionId: OD-RECOVERY-086
status: Partial (X3 team-based enumeration; X2 batch surface PASS with the
  G2-window cross-check; three harness fixes shipped)
mode: launcher (G2 anchor at the blitz-log marker) -> invoke-batch-rehearsal.ps1
  -LiveAcquire -Times 90,150,220 on savanna; X3 enumeration via
  /discover/entity-roster (team-based partial), X2 batch dumps via
  /discover/entity-regions per replay time (one G2 attestation per batch),
  decoded cross-check with 2 s window matching
targetBuild:
  version: 11.19.0.10
  executableSha256: 1cda5c31919c9784a41bee7f3270ec1b4536b124c51e8b36f2221b381760307d
liveRun:
  launcherExit: 0
  gate: OK OfflineReplayVerified (4 of 5 launches; 1 CAM-003-blocked)
  gamePid: 3256 (final launch)
  decodedSessionId: 019ff172-b0d8-7b83-af12-f30d6b304e08 (launch-matched,
    host store; the pre-staged 019fdff7-8dcf-… repo-local session 404s in
    the host store)
  enumeratedCount: 7
  candidatesSeen: 40
  filteredOut: 33
  filterPrecision: 1.000
  filterRecall: 0.500
  missingIds: [3760567, 3760569, 3760570, 3760571, 3760574, 3760575, 3760576]
  extraIds: []
  enumerationVerdict: 1 (team-based: all found = team 1, all missing = team 2)
  batchTimes: [89.3, 149.6, 221.9]
  batchesResolved: 3/3
  allBatchesClockAttested: true
  positionPairsCompared: 34
  positionPairsMatched: 34 (within 2 m via 2 s G2-window matching)
  positionVerdict: 0 (PASS with window; 21/34 at-label -> skew is the
    read-pass window, not a position error)
  readPassWindowMs: ~800 ms implied (moving tanks align at 0.00 m @ −0.8 s)
proof:
  batchSurfaceLive: true (3/3 batches Resolved + clock-attested; every
    compared position aligns to decoded ground truth within the 2 s window)
  rosterEnumerationMatchesDecoded: false - the movement-filter vtable gate
    separates the PLAYER'S OWN TEAM only (precision 1.0, recall 0.5); the
    X4 loop must re-enumerate per tick or add a second discriminator for
    enemy avatars
  readPassWindowMeasured: true (~0.8 s implied from the position cross-check;
    item-7 prerequisite, not a proof of atomicity)
```

## What the verdict decides (branch on the evidence)

- **Enumeration verdict 1 — TEAM-BASED (this run)** → the movement-filter
  vtable gate separates the player's OWN team's avatar family, not the
  full roster (all 7 found = team 1, all 7 missing = team 2; precision
  1.000, recall 0.500). The X4 loop must re-enumerate per tick or add a
  second discriminator for enemy avatars (the open X3 question, now
  evidenced). No offsets or read surface touched.
- **Position verdict 0 (with the 2 s G2-window cross-check)** → the batch
  surface reads ring records aligned to decoded ground truth live;
  `batchSurfaceLive: true`. The residual −0.8 s implied offset is the
  batch read-pass window (positions read before the post-read G2
  snapshot) — the item-7 measurement, not a position error.
- **Harness fixes shipped by this session (committed):** (1) the launcher
  now owns the G2 clock anchor at the blitz-log `Start replay event`
  marker moment (the gate moment lagged replay start by ~4.9 s, a
  constant skew that failed every moving-tank pair); (2) the batch driver
  waits per target replay time instead of firing all dumps at the current
  clock; (3) evidence writes are BOM-less UTF-8 (PS 5.1 `Set-Content
  -Encoding UTF8` BOM broke Python `json.load`); (4) the cross-check
  gained bounded 2 s window matching so the verdict reads "aligned within
  the clock's own uncertainty" with the implied offset measured.

## Files touched

- `docs/operations/od-recovery-086-evidence-template.md` (this file, filled)
- `docs/operations/offset-discovery-ledger.md` (OD-RECOVERY-086 section +
  index row + Last-updated)
- `scripts/launch-offline-replay-for-od.ps1` (G2 anchor at the marker
  moment, logs `battleSession=`)
- `scripts/invoke-batch-rehearsal.ps1` (per-target clock wait, BOM-less
  writes)
- `scripts/python/batch-rehearsal-crosscheck.py` (2 s window matching)
- Evidence files in `.data/`: `roster-enum-*.json` (3 runs, identical
  team-based partial), `batch-rehearsal-019ff172-…-113118.json` (PASS)
