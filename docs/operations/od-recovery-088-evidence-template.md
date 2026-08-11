# OD-RECOVERY-088 live-run evidence template — L2 facing/yaw (pre-staged 2026-08-11)

Fill this in after the next approved L2 live session. The static values
below are already known (the ring-record `+0x2C` yaw field rehearsed
27/27 Oasis + 35/35 Dead Rail against packet ground truth); only the
`<<...>>` placeholders need evidence from the run. L2 closes the facing
field for the live frame: the live nameplate arrow and the frame's hull
yaw (`LiveFrameTankState.YawRadians` from ring `+0x2C`) become
live-verified rather than rehearsal-only.

Run (one command; QUALIFY → DUMP → VERDICT, per
`docs/operations/record-diffing-groundwork.md` §L2 live-session plan):

```text
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/invoke-facing-session.ps1 `
  -SessionId <decoded-session-guid> -LiveAcquire -ControlTimes 20,240 `
  -SnapshotsPath .data/facing-snapshots.json
```

Evidence lands in `.data/`:
- `facing-snapshots-<session>-<stamp>.json` (schema
  `wotbtreader.od.hp-diff.snapshots.v1` family — ring-record dumps, one
  pair per turn segment ≥ 0.1 rad + flat control dumps), every dump
  requiring `sameDecodedClockProven=true` (fail-closed, G2 bound ≤ 2 s).
- The verdict from `wotbtreader-cli yaw-diff <snapshots.json> --session
  <id> --victim <entity>`: candidate offset, score, matched/total turn
  windows, flatness over control windows (wrap-aware matcher — Dead Rail
  has 5 seam crossings the naive delta would read ~2π wrong).
- Without a reachable web host the driver exits 3 with the contract;
  `-SnapshotsPath` replays an existing dump file through the verdict.

## Known static values (do not change without re-verifying)

| Item | Value |
|---|---|
| Target build | 11.19.0.10 |
| Executable SHA-256 | `1cda5c31919c9784a41bee7f3270ec1b4536b124c51e8b36f2221b381760307d` |
| Replay 1 | Oasis Palms (1 644 turn windows, yaw Δ p90 24.4°, 0 seam crossings) |
| Replay 2 (Phase-4 repeatability) | Dead Rail (1 728 turn windows, yaw Δ p90 48.5°, **5 seam crossings** — wrap-awareness evidence) |
| Region anchor | `ring-record` (the movement ring record the position resolver reads; stride 0x38) |
| Region length | ≥ 0x40 (covers the `+0x2C..+0x37` yaw tail) |
| Predicted offset | `+0x2C` (rehearsed HIT 1.0/1.0 on both replays, 27/27 + 35/35) |
| Probe first | `+0x2C..+0x37` tail of the ring record (per the plan) |
| G2 bound | `SameDecodedClockUncertaintyLimit` = 2 s; every dump must attest `sameDecodedClockProven=true` (fail-closed) |
| Verdict contract | candidate offset, score, matched/total turn windows, flatness 1.0 over control windows (stationary segments prove exactly constant packet yaw) |

## Ledger section skeleton — `OD-RECOVERY-088`

Append to `docs/operations/offset-discovery-ledger.md` (and add the index
row + Last-updated + status-line amendment in the same change). YAML block:

```yaml
sessionId: OD-RECOVERY-088
status: <<Hit / Partial / Miss>> (L2 facing live session, ring-record yaw)
mode: invoke-facing-session.ps1 -SessionId <guid> -LiveAcquire
  -ControlTimes 20,240 on Oasis Palms: launcher to OfflineReplayVerified,
  then /discover/entity-region ring-record dumps (region >= 0x40,
  replay-clock labeled, sameDecodedClockProven required) per turn segment
  + flat controls -> yaw-diff verdict (wrap-aware, flatness 1.0)
targetBuild:
  version: 11.19.0.10
  executableSha256: 1cda5c31919c9784a41bee7f3270ec1b4536b124c51e8b36f2221b381760307d
liveRun:
  launcherExit: 0
  gate: OK OfflineReplayVerified
  gamePid: <<pid>>
  decodedSessionId: <<guid - the decoded battle session qualifying the yaw schedule>>
  victimEntityId: <<entity - the tracked participant>>
  turnWindows: <<n - extractor --yaw-dump schedule; require >= 2>>
  dumpsTaken: <<n>>
  allDumpsClockAttested: <<true expected - sameDecodedClockProven per dump>>
  candidateOffset: <<ring-relative offset that tracks packet yaw - +0x2C expected>>
  score: <<1.0 expected>>
  matchedTurnWindows: <<n/total>>
  flatness: <<1.0 expected - unchanged in stationary control windows>>
  verdict: <<HIT / no-hit>>
proof:
  yawLiveAtRingOffset: <<claimable if candidateOffset = +0x2C with flatness
    1.0 + matched turn windows>>
  twoReplayRepeatability: <<claimable after the identical flow on Dead Rail
    agrees on the offset (seam-crossing wrap-awareness included) - Phase-4>>
  liveFrameYawBecomesLive: <<claimable after yawLiveAtRingOffset: the X4
    live frame's hull-yaw row flips from rehearsal-only to live-verified>>
```

## What the verdict decides (branch on the evidence)

- **Candidate = `+0x2C` with flatness 1.0 and matched turn windows** →
  the rehearsed facing field is confirmed live; `yawLiveAtRingOffset`
  claimable, and the X4 live frame's `YawRadians` (from ring `+0x2C`) is
  live-verified — the nameplate arrow and frame yaw ride on proven reads.
- **Candidate is a DIFFERENT ring offset that tracks yaw** → recorded
  honestly; the live finding wins over the rehearsal prediction, and the
  yaw chain field (`+0x2C`) is corrected before any publication (P3).
- **No candidate (no-hit)** → the ring record as read does not carry yaw;
  record the honest negative, widen the probe per the plan (`+0x2C..+0x37`
  was the first place to look), no offsets edited.
- **Any fail-closed exit (dump not clock-attested, gate failure, verdict
  not a HIT with -FailOnNoHit)** → honest negative recorded; next attempt
  retries the same one command; no plan change without a new diagnosis.
- **Two-replay agreement (Phase-4 rule)** → only after BOTH replays agree
  can the facing/yaw publication (roadmap P3, replacing the quarantined
  static candidate) proceed through the operator gate.

## Files touched (this template only — fill after the run)

- `docs/operations/od-recovery-088-evidence-template.md` (this file)
- `docs/operations/offset-discovery-ledger.md` (append the OD-RECOVERY-088
  section + index row + Last-updated)
- `docs/operations/live-frame-loop-design.md` (honest-limits hull-yaw row,
  if HIT)
