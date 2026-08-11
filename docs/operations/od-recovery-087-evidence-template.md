# OD-RECOVERY-087 live-run evidence template — L1 HP (pre-staged 2026-08-11)

Fill this in after the next approved L1 live session. The static values
below are already known (the entity-base map is statically verified
26/26); only the `<<...>>` placeholders need evidence from the run. L1
closes the HP field for the live frame: after this session the live
nameplate HP bar can become real (`hp: null` today, by design, until L1
lands — `docs/operations/live-frame-loop-design.md`).

Run (one command; QUALIFY → DUMP → VERDICT, per
`docs/operations/record-diffing-groundwork.md` §Live session plan):

```text
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/invoke-hp-diffing-session.ps1 `
  -SessionId <decoded-session-guid> -VictimEntityId 3760578 `
  -LiveAcquire -ControlTimes 30,230 -SnapshotsPath .data/hp-snapshots.json
```

Evidence lands in `.data/`:
- `hp-snapshots-<session>-<stamp>.json` (schema
  `wotbtreader.od.hp-diff.snapshots.v1`) — the entity-base region dumps,
  one per scheduled replay-clock time (before/after each damage event
  ±0.2 s + flat control dumps), every dump requiring
  `sameDecodedClockProven=true` (fail-closed, G2 bound ≤ 2 s).
- The verdict output from `wotbtreader-cli hp-diff <snapshots.json>
  --session <id> --victim <entity> --mode lenient [--int16 true]`:
  buckets, Lenient correlates, Strict confirmation, score / flatness.
- Without a reachable web host the driver exits 3 with the contract;
  `-SnapshotsPath` replays an existing dump file through the verdict.

## Known static values (do not change without re-verifying)

| Item | Value |
|---|---|
| Target build | 11.19.0.10 |
| Executable SHA-256 | `1cda5c31919c9784a41bee7f3270ec1b4536b124c51e8b36f2221b381760307d` |
| Replay 1 | Oasis Palms, victim **3760578** (takes real damage — viewpoint player took 0 in both replays) |
| Replay 2 (Phase-4 repeatability) | Dead Rail, victim **2549399** (18 events / 4,647 damage) |
| Region anchor | `entity-base` (HP lives on the entity base record, NOT the tank record `[entity+0x3C]` transform) |
| Region length | ≥ 0x120 (driver default 320 = 0x140, covers healing `+0x11E`) |
| Correlate | **signed int16** (`hp-diff --int16 true`, on by default for the HP/decrement direction) |
| G2 bound | `SameDecodedClockUncertaintyLimit` = 2 s; every dump must attest `sameDecodedClockProven=true` (fail-closed) |
| Static map (VerifyPlayerHpChain 26/26, 2026-08-11) | current int16 `+0xB8`, alive byte `+0xBA`, max int16 `+0x11C`, healing int16 `+0x11E` |
| Expected live correlate | `+0xB8` current-health int16 (region-relative 0x80 dump-relative → absolute `+0xB8`) |
| Verdict contract | ≥ 2 matched damage windows, score 1.0 Lenient, **flatness 1.0** (control windows), Strict confirmation ≥ 2 exact-sum windows |

## Ledger section skeleton — `OD-RECOVERY-087`

Append to `docs/operations/offset-discovery-ledger.md` (and add the index
row + Last-updated + status-line amendment in the same change). YAML block:

```yaml
sessionId: OD-RECOVERY-087
status: <<Hit / Partial / Miss>> (L1 HP live session, entity-base int16)
mode: invoke-hp-diffing-session.ps1 -SessionId <guid> -VictimEntityId 3760578
  -LiveAcquire -ControlTimes 30,230 on Oasis Palms: launcher to
  OfflineReplayVerified, then /discover/entity-region entity-base dumps
  (region >= 0x120, replay-clock labeled, sameDecodedClockProven required)
  before/after each damage event + flat controls -> hp-diff --int16 true
  verdict (Lenient then Strict, flatness 1.0)
targetBuild:
  version: 11.19.0.10
  executableSha256: 1cda5c31919c9784a41bee7f3270ec1b4536b124c51e8b36f2221b381760307d
liveRun:
  launcherExit: 0
  gate: OK OfflineReplayVerified
  gamePid: <<pid>>
  decodedSessionId: <<guid - the decoded battle session qualifying the victim>>
  victimEntityId: 3760578
  damageWindows: <<n - extractor --hp-delta windows; require >= 2>>
  dumpsTaken: <<n>>
  allDumpsClockAttested: <<true expected - sameDecodedClockProven per dump>>
  candidateOffset: <<int16 offset that drops with damage - +0xB8 expected>>
  lenientScore: <<1.0 expected>>
  flatness: <<1.0 expected - unchanged in every zero-damage control window>>
  strictWindowsMatched: <<n >= 2 expected - exact damage-sum drops>>
  verdict: <<HIT / no-hit>>
proof:
  hpLiveAtEntityBaseOffset: <<claimable if candidateOffset = +0xB8 with
    flatness 1.0 + >= 2 Strict exact-sum windows>>
  twoReplayRepeatability: <<claimable after the identical flow on Dead Rail
    victim 2549399 agrees on the offset - Phase-4 rule>>
  liveFrameHpBecomesReal: <<claimable after hpLiveAtEntityBaseOffset: the
    X4 live frame can fill hp (additive contract change, no shape break)>>
```

## What the verdict decides (branch on the evidence)

- **Candidate = `+0xB8` with flatness 1.0 and ≥ 2 Strict exact-sum
  windows** → the static 26/26 map is confirmed live; `hpLiveAtEntityBaseOffset`
  claimable, and the X4 frame contract can gain `hpCurrent/hpMax`
  additively (empty bar becomes real; `docs/operations/live-frame-loop-design.md`
  honest-limits table row for HP flips from ⏳ L1 to ✅).
- **Candidate is a DIFFERENT int16 that drops with damage and confirms** →
  recorded honestly; the static map was wrong at that offset — the live
  finding wins (evidence-first), and the L1 row / groundwork map are
  corrected with the new offset before any promotion.
- **No candidate (no-hit)** → the entity-base region does not contain the
  HP field as read; record the honest negative, widen the anchor per the
  playbook (ring record / tank record), no offsets edited.
- **Any fail-closed exit (dump not clock-attested, gate failure, verdict
  not a HIT with -FailOnNoHit)** → the session records the honest negative
  and the next attempt retries the same one command; no plan change
  without a new diagnosis.
- **Two-replay agreement (Phase-4 rule)** → only after BOTH replays
  (Oasis Palms + Dead Rail) agree on the offset can the HP publication
  (roadmap P2) proceed through the operator gate.

## Files touched (this template only — fill after the run)

- `docs/operations/od-recovery-087-evidence-template.md` (this file)
- `docs/operations/offset-discovery-ledger.md` (append the OD-RECOVERY-087
  section + index row + Last-updated)
- `docs/operations/live-frame-loop-design.md` (honest-limits HP row, if HIT)
