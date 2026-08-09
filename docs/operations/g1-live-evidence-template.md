# G1/G2 live-run evidence template (pre-staged 2026-08-09)

Fill this in after the next approved live session. The static values below
are already known; only the `<<...>>` placeholders need evidence from the
run. The run is one command (runbook:
`docs/operations/offset-discovery-workflow.md` → G1/G2 live run):

```text
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/invoke-g1-live-poll.ps1 `
  -ReplayPath <replay> -WindowWaitSeconds 240
```

Evidence lands in `.data/diagnostics/g1-live-<stamp>/`:
`g1-evidence.json` (schema `wotbtreader.g1.write-observation.v1`),
`interceptor-report.json`, `od073-poll.json` (schema
`wotbtreader.od073.entity-position-poll.v3`).

## Known static values (do not change without re-verifying)

| Item | Value |
|---|---|
| Target build | 11.19.0.10 |
| Executable SHA-256 | `1cda5c31919c9784a41bee7f3270ec1b4536b124c51e8b36f2221b381760307d` |
| Replay | Oasis Palms (savanna), 1,045,525 B, battle 2026-08-02T21:15:07 (content-distinct; the two proven 11.19.0 replays are Dead Rail + Oasis Palms) |
| G2 anchor | sequence 0, replay anchor 0, speed 1.0, source `CaptureLog`, uncertainty 1 s (gate-loop cadence, within the 2 s coordinator bound) |
| Coordinator bound | `SameDecodedClockUncertaintyLimit` = 2 s |

## Ledger section skeleton — `OD-RECOVERY-078`

Append to `docs/operations/offset-discovery-ledger.md` (and add the index
row + Last-updated + status-line amendment in the same change). YAML block:

```yaml
sessionId: OD-RECOVERY-078
status: <<Hit / Partial / Miss>> (G1 live write-observation + G2 live anchor)
mode: invoke-g1-live-poll.ps1 one-command session - launcher to
  OfflineReplayVerified, position-page resolve, guard-page interceptor armed
  on the ring-record page, unchanged bounded od-073 poll inside the capture
  window, clock anchor POST at the gate
targetBuild:
  version: 11.19.0.10
  executableSha256: 1cda5c31919c9784a41bee7f3270ec1b4536b124c51e8b36f2221b381760307d
liveRun:
  launcherExit: 0
  gate: OK OfflineReplayVerified
  coldBootNote: -WindowWaitSeconds 240 used (90 s default can fail a cold boot)
  gamePid: <<pid>>
  battleSessionId: <<guid>>
  entityId: <<id>>
  armedRecordAddress: <<0x... from g1-evidence.json>>
  armedPageAddress: <<0x... from g1-evidence.json>>
  interceptorExit: <<0 expected>>
  pollExit: <<0 expected>>
  pollSucceeded: <<true expected>>
  writeObservationVerdict: <<write-observation-clean | write-observation-observed>>
  readWindowHits: <<n>>
  livenessBefore: <<n>>
  livenessAfter: <<n>>
  pollAggregateVerdict: <<stable-resolver-positive expected>>
  resolvedReads: <<24 expected>>
  distinctPositions: <<n>>
  withinOneWorldUnit: <<n>>
  withinThreeWorldUnits: <<n>>
  allModuleRooted: <<true expected>>
  allEntityIdentityRevalidated: <<true expected>>
  allConsistentDoubleRead: <<true expected>>
  sameDecodedClockProven: <<true | false - true only if the anchor POST landed>>
proof:
  moduleRootedResolver: true
  hardwareAtomicRead: <<claimable if verdict clean, OR observed + 24/24
    byte-identical double-reads (the per-read branch); see checklist>>
  sameDecodedClock: <<claimable if sameDecodedClockProven=true>>
  numericOffsetPublication: false
  offsetPromoted: false
evidence:
  g1Evidence: .data/diagnostics/g1-live-<<stamp>>/g1-evidence.json
  interceptorReport: .data/diagnostics/g1-live-<<stamp>>/interceptor-report.json
  pollAggregate: .data/diagnostics/g1-live-<<stamp>>/od073-poll.json
privacy:
  publicProcessAddressesOrRawBytes: false
  aggregatePersistsIdsOrCoordinates: false
  trackedPrivateArtifactValues: false
shutdown:
  gameHostHelperDebuggerProcessesRemaining: <<0 expected - stop wotblitz,
    Host.Web, and the interceptor after the session>>
```

Index row (append to the ledger index table):

```text
| `OD-RECOVERY-078` | 2026-08-09 | G1 live write-observation + G2 live anchor (one session) | invoke-g1-live-poll.ps1: gate -> position-page -> interceptor arm -> unchanged bounded poll -> verdict | <<status>> | <<one-line result>> | <<one-line remaining>> |
```

## Handoff skeleton

Copy `docs/operations/handoffs/2026-08-09-g1-live-<stamp>.md`:

```markdown
# G1 hardware-atomic read proof + G2 same-clock alignment: live evidence

Date: 2026-08-09
Status: <<milestone status>>
Scope: one approved live session via invoke-g1-live-poll.ps1; resolver, read
surface, and offset table untouched

## Session summary

<<2-4 sentences: gate reached, interceptor armed on the ring-record page,
unchanged bounded poll verdict, write-observation verdict, clock anchor.>>

## Evidence

- Launcher: exit 0, `OK OfflineReplayVerified` (cold boot covered by
  -WindowWaitSeconds 240).
- Position page: record <<recordAddress>> / page <<pageAddress>> (entity
  <<entityId>>, session <<battleSessionId>>).
- Interceptor: exit <<0>>, <<hits>> page writes captured; read window
  [<<start>>, <<end>>] had <<n>> hits, liveness <<before>>/<<after>> ->
  verdict <<clean | observed>>.
- Poll: `verdict=<<stable-resolver-positive>>`, <<resolved>>/24 resolved,
  <<distinct>> distinct, <<within1>> within one unit, <<within3>> within
  three, `allModuleRooted=<<true>>`, `allConsistentDoubleRead=<<true>>`,
  `sameDecodedClockProven=<<true|false>>`.

## Decision

<<G1: claimable/not, why (clean window, or observed + per-read byte-identical
branch). G2: claimable/not (anchor POSTed, flag true). G0: not yet - the
publication review follows.>>

## Next

<<G0 publication review if both gates closed; otherwise the specific
remaining evidence.>>
```

## Field mapping (g1-evidence.json / poll aggregate → ledger)

| Evidence field | Source | Lands in ledger |
|---|---|---|
| `verdict.clean` / `verdict.inWindow` | g1-evidence.json | `writeObservationVerdict`, `readWindowHits` |
| `verdict.before` / `verdict.after` | g1-evidence.json | `livenessBefore` / `livenessAfter` |
| `gamePid`, `battleSessionId`, `entityId` | g1-evidence.json | `liveRun` |
| `armedRecordAddress`, `armedPageAddress` | g1-evidence.json | `liveRun` |
| `pollExit`, `pollSucceeded` | g1-evidence.json | `liveRun` |
| `verdict`, `resolvedReads`, `distinctPositionCount` | od073-poll.json | `liveRun` / `proof` |
| `withinOneWorldUnit`, `withinThreeWorldUnits` | od073-poll.json | `liveRun` |
| `allModuleRooted`, `allEntityIdentityRevalidated`, `allConsistentDoubleRead` | od073-poll.json | `liveRun` |
| `sameDecodedClockProven` | od073-poll.json | `proof.sameDecodedClock` |
| `interceptor-report.json` `exitCode` | interceptor-report.json | `liveRun.interceptorExit` |

## Fill-in checklist (all must hold before claiming flags)

1. `g1-evidence.json` exists, `pollSucceeded=true`, `pollExit=0`.
2. Poll aggregate is `stable-resolver-positive` with 24/24 resolved and all
   three `all*` flags true.
3. Interceptor report `exitCode=0` with hits (liveness) recorded.
4. G1: clean verdict, or observed with 24/24 `allConsistentDoubleRead`
   (byte-identical double-reads) — attach both evidence files to the row.
5. G2: `sameDecodedClockProven=true` in the poll aggregate (anchor POSTed).
6. No process left running (`shutdown` row = 0).
7. No resolver/read-surface/offset-table change in the same change.
