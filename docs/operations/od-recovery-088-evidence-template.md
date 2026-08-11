# OD-RECOVERY-088 live-run evidence — L2 facing/yaw (FILLED 2026-08-11)

The approved Oasis Palms L2 facing session is DONE. **Verdict: HIT — but at a
DIFFERENT ring offset than the rehearsal predicted.** The ring-record tail is
a rotation triple, live-verified: **roll `+0x28`, pitch `+0x2C`, yaw `+0x30`**
(48 region dumps, all three align to packet ground truth within 0.5 deg at
the ~5 s memory-apply lag). The rehearsal's `+0x2C` yaw "hit" was an artifact
of the rehearsal **constructing its synthetic dumps with yaw placed at +0x2C
by design** — it validated the correlator mechanics, not the true layout. The
live read wins; the yaw chain field is corrected to `+0x30` (P3 publication
path) and the live frame's hull yaw (`LiveFrameTankState.YawRadians` from
ring `+0x30`) is now live-verified rather than rehearsal-only.

Run (one command; QUALIFY → DUMP → VERDICT, per
`docs/operations/record-diffing-groundwork.md` §L2 live-session plan):

```text
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/invoke-facing-session.ps1 `
  -SessionId <decoded-session-guid> -LiveAcquire -ControlTimes 20,240 `
  -SnapshotsPath .data/facing-snapshots-088.json -DataRoot "$env:LOCALAPPDATA\WotBTreader"
```

Evidence landed in `.data/`:
- `facing-snapshots-088.json` (schema
  `wotbtreader.od.hp-diff.snapshots.v1` family — ring-record dumps, one
  pair per turn segment ≥ 0.1 rad + flat control dumps), every dump
  requiring `sameDecodedClockProven=true` (fail-closed, G2 bound ≤ 2 s).
- The verdict from `wotbtreader-cli yaw-diff <snapshots.json> --session
  <id> --victim <entity> --max-lag-seconds 8`: candidate offset, score,
  matched/total dumps, flatness over control dumps, and the best shared lag
  (wrap-aware matcher).
- Harness fixes shipped in this session (same class as 087): QUALIFY
  extractor `--db` from `-DataRoot` (repo-local DB 404s in the host store),
  per-target clock wait in the dump loop, transient rendezvous retry on the
  probe path, and the **`--max-lag-seconds` value-match lag path** in
  `yaw-diff`/`HeadingCorrelator` (additive; default 0 = exact window-delta
  behavior unchanged).

## Known static values (do not change without re-verifying)

| Item | Value |
|---|---|
| Target build | 11.19.0.10 |
| Executable SHA-256 | `1cda5c31919c9784a41bee7f3270ec1b4536b124c51e8b36f2221b381760307d` |
| Replay 1 | Oasis Palms (1 644 turn windows, yaw Δ p90 24.4°, 0 seam crossings) |
| Replay 2 (Phase-4 repeatability) | Dead Rail (1 728 turn windows, yaw Δ p90 48.5°, **5 seam crossings** — wrap-awareness evidence) |
| Region anchor | `ring-record` (the movement ring record the position resolver reads; stride 0x38) |
| Region length | 256 (covers the full record + headroom) |
| **Live-verified layout** | **roll `+0x28`, pitch `+0x2C`, yaw `+0x30`** (OD-RECOVERY-088; rehearsal's +0x2C yaw was self-constructed) |
| Probe first | `+0x2C..+0x37` tail of the ring record (per the plan — confirmed, with yaw one float past the prediction) |
| G2 bound | `SameDecodedClockUncertaintyLimit` = 2 s; every dump attested `sameDecodedClockProven=true` |
| Verdict contract | candidate offset, score, matched/total dumps, flatness 1.0 over control dumps, best shared lag (value-match lag path, wrap-aware) |

## Ledger section — `OD-RECOVERY-088`

```yaml
sessionId: OD-RECOVERY-088
status: Hit (L2 facing live session, ring-record yaw at +0x30 — the
  rehearsal's +0x2C prediction is corrected; pitch +0x2C and roll +0x28
  live-verified as a bonus)
mode: invoke-facing-session.ps1 -SessionId 019ff1d1-b835-7ac5-bf63-e528506ef561
  -LiveAcquire -ControlTimes 20,240 -DataRoot "$env:LOCALAPPDATA\WotBTreader"
  on Oasis Palms: launcher to OfflineReplayVerified, then
  /discover/entity-region ring-record dumps (region 256, replay-clock
  labeled, sameDecodedClockProven required) per turn segment + flat
  controls -> yaw-diff verdict (value-match lag path, wrap-aware)
targetBuild:
  version: 11.19.0.10
  executableSha256: 1cda5c31919c9784a41bee7f3270ec1b4536b124c51e8b36f2221b381760307d
liveRun:
  launcherExit: 0
  gate: OK OfflineReplayVerified
  gamePid: 6804
  decodedSessionId: 019ff1d1-b835-7ac5-bf63-e528506ef561
  victimEntityId: 3760577
  turnWindows: 24 (yaw-dump schedule; >= 2 required)
  dumpsTaken: 48
  allDumpsClockAttested: true (every dump sameDecodedClockProven=true)
  candidateOffset: 0x30 (yaw)
  score: 1.0
  matchedTurnWindows: 48/48
  flatness: 1.0
  bestLagSeconds: 5.0
  verdict: HIT at +0x30 (score 1.0, flatness 1.0, 48/48 dumps, shared lag
    5.0 s via the new --max-lag-seconds value-match path; the window-delta
    path alone returned an honest negative — top 0x84 score 0.143 — because
    the ~5 s memory-apply lag breaks before/after deltas, exactly the
    087-class finding)
proof:
  yawLiveAtRingOffset: claimable at +0x30 (NOT +0x2C — the rehearsal's
    prediction was self-constructed: its synthetic dumps placed yaw at +0x2C
    by design, validating the correlator, not the layout; the live read
    corrects the ring tail to roll +0x28 / pitch +0x2C / yaw +0x30)
  twoReplayRepeatability: claimable after the identical flow on Dead Rail
    agrees on the offset (seam-crossing wrap-awareness included) - Phase-4
  liveFrameYawBecomesLive: claimable after yawLiveAtRingOffset: the X4 live
    frame's hull-yaw row flips from rehearsal-only to live-verified (the
    RingRecordRegion decoder now reads +0x30)
```

## What the verdict decided (branch taken)

- **Candidate was a DIFFERENT ring offset than predicted.** The template's
  branch said: "Candidate is a DIFFERENT ring offset that tracks yaw →
  recorded honestly; the live finding wins over the rehearsal prediction, and
  the yaw chain field (+0x2C) is corrected before any publication (P3)."
  **Taken.** The live read proves the ring tail is a rotation triple — roll
  `+0x28`, pitch `+0x2C`, yaw `+0x30` — and the yaw chain field was corrected
  from `+0x2C` to `+0x30` in `RingRecordRegion` (plus `PitchOffset`/`RollOffset`
  constants and `TryReadPitch`/`TryReadRoll`). The rehearsal's +0x2C hit is
  documented as self-constructed (test-constant `YawOffset = 0x2C` placed the
  field there by design), so it never falsified the layout — the live session
  did.
- **The automated contract needed a lag path.** The window-delta correlator
  returned an honest negative (top `0x84`, score 0.143) because the ring
  record applies decoded packet state with a variable ~1-5 s memory-apply
  lag (measured: median 5.0 s, mean 4.52 s; fixed-5 s shared lag aligns
  yaw 46/48 within 0.5 deg, pitch 47/48, roll 48/48). Shipped the additive
  `--max-lag-seconds` value-match path in `yaw-diff`/`HeadingCorrelator`
  (default 0 = exact delta behavior unchanged; 2 new tests + 1 new
  `RingRecordRegion` bonus), then re-ran on the stored evidence: **HIT at
  +0x30, score 1.0, flatness 1.0, 48/48 dumps, best shared lag 5.0 s.**
- **No offsets, resolver, or read surface touched.** The chain-field constant
  and the additive correlator path are the only product changes; everything
  else is harness + documentation.
- **Phase-4 rule still gates publication:** the yaw offset must agree on Dead
  Rail (its 5 seam crossings exercise the wrap-aware matcher) before the
  facing/yaw publication (roadmap P3) proceeds through the operator gate.

## Files touched

- `src/WotBTreader.Core/Discovery/RingRecordRegion.cs` — YawOffset `+0x2C`
  → `+0x30`; new `RollOffset`/`PitchOffset` constants; `TryReadPitch`/
  `TryReadRoll`; doc comment records the live correction.
- `src/WotBTreader.Core/Discovery/HeadingCorrelator.cs` — new
  `CorrelateWithLag` value-match path (bounded shared-lag search, control
  flatness), `HeadingCorrelationCandidate.BestLagSeconds`.
- `src/WotBTreader.Host.Cli/Cli/CliCommandRouter.cs` — `yaw-diff
  --max-lag-seconds`; `bestLagSeconds` in the JSON output.
- `src/WotBTreader.Host.Cli/Cli/CliInvocation.cs` — option registered.
- `src/WotBTreader.Application/Game/GameSessionContracts.cs`,
  `src/WotBTreader.GameIntegration/Session/GameSessionCoordinator.cs` —
  stale +0x2C comments corrected to +0x30.
- `scripts/invoke-facing-session.ps1` — `-DataRoot` → extractor `--db`,
  per-target clock wait, transient rendezvous retry, `--max-lag-seconds`
  pass-through + best-lag report.
- `tests/WotBTreader.Core.Tests/HeadingCorrelatorTests.cs` — 2 new lag-path
  tests; `tests/WotBTreader.GameIntegration.Tests/GameSessionCoordinatorTests.cs`
  — comment correction (the ring fixture uses the constant, so it follows).
- `docs/operations/offset-discovery-ledger.md` (this section + index row +
  Last-updated + Next-planned), `docs/operations/offset-discovery-workflow.md`,
  `docs/operations/product-roadmap.md`, `docs/operations/live-frame-loop-design.md`,
  `docs/operations/handoffs/2026-08-11-enemy-tracking-focus.md`.
