# OD-RECOVERY-089 live-run evidence — L2 facing Phase-4 repeat on Dead Rail (PRE-STAGED)

**Status: PRE-STAGED (2026-08-11)** — the template below is the fill-in
contract for the approved Phase-4 repeat session. The Phase-4 rule requires
the yaw offset found live on Oasis Palms (OD-RECOVERY-088: ring-record
`+0x30`) to **agree on a second, content-distinct replay** — Dead Rail, whose
5 seam crossings exercise the wrap-aware matcher (yaw Δ p90 48.5° vs
Oasis 24.4°). Closing this session sets `twoReplayRepeatability = true` for
the facing/yaw candidate; the Phase-4 two-replay HP rule (Dead Rail victim
2549399, `hp-diff`) separately gates HP publication.

Expected outcome (to be confirmed, never assumed): the `yaw-diff` verdict
lands on offset `0x30` with score 1.0, flatness 1.0, matched dumps ≥ 48,
best shared lag ≈ 5 s — the SAME live-verified ring-record rotation triple
(roll `+0x28` / pitch `+0x2C` / yaw `+0x30`). Any deviation is a template
"candidate is a DIFFERENT offset / no hit" branch: record it, do NOT edit
the chain field, and re-open the offset question.

## Run (one command; QUALIFY → DUMP → VERDICT, per
`docs/operations/record-diffing-groundwork.md` §L2 live-session plan)

```text
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/invoke-facing-session.ps1 `
  -SessionId <decoded-session-guid> -LiveAcquire -ControlTimes 20,240 `
  -SnapshotsPath .data/facing-snapshots-089.json `
  -DataRoot "$env:LOCALAPPDATA\WotBTreader" -MaxLagSeconds 8
```

Preconditions (same class as 087/088):
- The launcher reached `OK OfflineReplayVerified` with a launch-matched
  host-store session (`battleSession=` logged at the G2 anchor moment) —
  the driver consumes the SAME session id and `-DataRoot` feeds the QUALIFY
  extractor `--db`.
- No other host/game processes running (one guarded lease).

## Evidence to land in `.data/`

- `facing-snapshots-089.json` (schema
  `wotbtreader.od.hp-diff.snapshots.v1` family — ring-record dumps, one
  pair per turn segment ≥ 0.1 rad + flat control dumps), every dump
  requiring `sameDecodedClockProven=true` (fail-closed, G2 bound ≤ 2 s).
- The verdict from `wotbtreader-cli yaw-diff <snapshots.json> --session
  <id> --victim <entity> --max-lag-seconds 8`: candidate offset, score,
  matched/total dumps, flatness over control dumps, and the best shared lag
  (wrap-aware matcher).

## Known static values (do not change without re-verifying)

| Item | Value |
|---|---|
| Target build | 11.19.0.10 |
| Executable SHA-256 | `1cda5c31919c9784a41bee7f3270ec1b4536b124c51e8b36f2221b381760307d` |
| Replay 1 (OD-RECOVERY-088) | Oasis Palms — yaw HIT at ring-record `+0x30` (48 dumps, score 1.0, flatness 1.0, best shared lag 5.0 s) |
| Replay 2 (THIS session) | Dead Rail (1 728 turn windows, yaw Δ p90 48.5°, **5 seam crossings** — wrap-awareness evidence) |
| Region anchor | `ring-record` (the movement ring record the position resolver reads; stride 0x38) |
| Region length | 256 (covers the full record + headroom) |
| Expected layout | roll `+0x28`, pitch `+0x2C`, yaw `+0x30` (OD-RECOVERY-088 live-verified; the rehearsal's +0x2C yaw was self-constructed) |
| G2 bound | `SameDecodedClockUncertaintyLimit` = 2 s; every dump attested `sameDecodedClockProven=true` |
| Verdict contract | candidate offset, score, matched/total dumps, flatness 1.0 over control dumps, best shared lag (value-match lag path, wrap-aware) |

## Ledger section — `OD-RECOVERY-089` (fill on completion)

```yaml
sessionId: OD-RECOVERY-089
status: PENDING
replay: Dead Rail (content-distinct, 5 seam crossings)
verdict: <Hit at +0x30 | different offset | no hit>
score: <0..1>
matchedDumps: <n/total>
bestSharedLagSeconds: <s>
flatness: <0..1>
twoReplayRepeatability: <true|false>
```

## After this session

- On HIT at `+0x30`: `twoReplayRepeatability = true` for yaw — the facing
  candidate is publication-ready (gates: `offset_check.py --check-schema`,
  `validate.ps1`, operator-approved numeric publication if promoted).
- The Phase-4 two-replay HP rule (Dead Rail victim 2549399, `hp-diff`
  session) still gates HP publication separately.
- CAM-001 v7 remains the next camera workstream; item 7 (hardware
  atomicity) stays LAST by design.
