# G1/G2 live-run evidence template (pre-staged 2026-08-09; corrected 2026-08-09, OD-RECOVERY-080)

Fill this in after the next approved live session. The static values below
are already known; only the `<<...>>` placeholders need evidence from the
run. The run is one command (runbook:
`docs/operations/offset-discovery-workflow.md` → G1/G2 live run). The
**corrected procedure** (OD-RECOVERY-080) skips the guard-page interceptor
arm — the interceptor's PAGE_GUARD on the ring-record page fails the poll's
own reads (ERROR_PARTIAL_COPY 299 at the avatar-helper vtable hop), so the
G1 per-read byte-identical branch is carried by the poll's own
`allConsistentDoubleRead` (already proven 24/24 un-armed in OD-075/076):

```text
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/invoke-g1-live-poll.ps1 `
  -ReplayPath <replay> -WindowWaitSeconds 240 -SkipInterceptorArm `
  -PriorResultPaths .data/od-073-entity-position-poll-20260809-021445.json,`
    .data/od-073-entity-position-poll-20260809-165144.json
```

Evidence lands in `.data/diagnostics/g1-live-<stamp>/`:
`g1-evidence.json` (schema `wotbtreader.g1.write-observation.v1`,
`interceptorArmed=false`, verdict `write-observation-skipped`) and
`od073-poll.json` (schema `wotbtreader.od073.entity-position-poll.v4` —
since OD-RECOVERY-081 the verdict no longer disqualifies runs where the G2
same-decoded-clock proof fired).
There is **no interceptor-report.json** in the corrected mode (the
interceptor is not launched); the legacy armed mode remains available only
for evidence continuity.

## Known static values (do not change without re-verifying)

| Item | Value |
|---|---|
| Target build | 11.19.0.10 |
| Executable SHA-256 | `1cda5c31919c9784a41bee7f3270ec1b4536b124c51e8b36f2221b381760307d` |
| Replay | Oasis Palms (savanna), 1,045,525 B, battle 2026-08-02T21:15:07 (content-distinct; the two proven 11.19.0 replays are Dead Rail + Oasis Palms) |
| G2 anchor | sequence 0, replay anchor 0, speed 1.0, source `CaptureLog`, uncertainty 1 s (gate-loop cadence, within the 2 s coordinator bound) |
| Coordinator bound | `SameDecodedClockUncertaintyLimit` = 2 s |
| G3 prior positives | `.data/od-073-entity-position-poll-20260809-021445.json` (OD-075) and `-165144.json` (OD-076) — both `stable-resolver-positive` 24/24 |

## Ledger section skeleton — `OD-RECOVERY-081`

Append to `docs/operations/offset-discovery-ledger.md` (and add the index
row + Last-updated + status-line amendment in the same change). YAML block:

```yaml
sessionId: OD-RECOVERY-081
status: <<Hit / Partial / Miss>> (G1 per-read atomicity + G2 anchor + G3 prior-positive, one corrected session)
mode: invoke-g1-live-poll.ps1 one-command session (corrected, OD-RECOVERY-080):
  launcher to OfflineReplayVerified, position-page resolve, unchanged bounded
  od-073 poll un-armed (-SkipInterceptorArm) with -PriorResultPaths (OD-075 +
  OD-076 aggregates), clock anchor POST at the gate
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
  recordAddress: <<0x... from g1-evidence.json>>
  pageAddress: <<0x... from g1-evidence.json>>
  interceptorArmed: false
  pollExit: <<0 expected>>
  pollSucceeded: <<true expected>>
  writeObservationVerdict: write-observation-skipped (corrected mode; G1 per-read branch from the poll aggregate)
  pollAggregateVerdict: <<stable-resolver-positive expected>>
  resolvedReads: <<24 expected>>
  distinctPositions: <<n>>
  withinOneWorldUnit: <<n>>
  withinThreeWorldUnits: <<n>>
  allModuleRooted: <<true expected>>
  allEntityIdentityRevalidated: <<true expected>>
  allConsistentDoubleRead: <<true expected>>
  sameDecodedClockProven: <<true | false - true only if the anchor POST landed>>
  stableRootLiveRepeatabilityProven: <<true expected - requires the prior
    positives via -PriorResultPaths>>
proof:
  moduleRootedResolver: true
  hardwareAtomicRead: <<claimable if 24/24 stable-resolver-positive with
    allConsistentDoubleRead=true (per-read byte-identical branch); see checklist>>
  stableRootLiveRepeatability: <<claimable if stableRootLiveRepeatabilityProven=true>>
  sameDecodedClock: <<claimable if sameDecodedClockProven=true>>
  numericOffsetPublication: false
  offsetPromoted: false
evidence:
  g1Evidence: .data/diagnostics/g1-live-<<stamp>>/g1-evidence.json
  pollAggregate: .data/diagnostics/g1-live-<<stamp>>/od073-poll.json
  priorAggregates: [.data/od-073-entity-position-poll-20260809-021445.json,
    .data/od-073-entity-position-poll-20260809-165144.json]
privacy:
  publicProcessAddressesOrRawBytes: false
  aggregatePersistsIdsOrCoordinates: false
  trackedPrivateArtifactValues: false
shutdown:
  gameHostHelperDebuggerProcessesRemaining: <<0 expected - stop wotblitz and
    Host.Web after the session; no interceptor in the corrected mode>>
```

Index row (append to the ledger index table):

```text
| `OD-RECOVERY-081` | 2026-08-09 | G1 per-read atomicity + G2 anchor + G3 prior-positive (one corrected session, interceptor un-armed) | invoke-g1-live-poll.ps1: gate -> position-page -> unchanged un-armed bounded poll (-SkipInterceptorArm) + prior positives -> verdict | <<status>> | <<one-line result>> | <<one-line remaining>> |
```

## Handoff skeleton

Copy to `docs/operations/handoffs/2026-08-09-g1-live-<stamp>.md`:

```markdown
# G1 hardware-atomic read proof + G2 same-clock alignment + G3 repeatability: live evidence (corrected session)

Date: 2026-08-09
Status: <<milestone status>>
Scope: one approved live session via invoke-g1-live-poll.ps1 -SkipInterceptorArm;
resolver, read surface, and offset table untouched

## Session summary

<<2-4 sentences: gate reached, position-page resolved, unchanged bounded poll
un-armed (-SkipInterceptorArm) verdict, prior positives supplied for G3,
clock anchor.>>

## Evidence

- Launcher: exit 0, `OK OfflineReplayVerified` (cold boot covered by
  -WindowWaitSeconds 240).
- Position page: record <<recordAddress>> / page <<pageAddress>> (entity
  <<entityId>>, session <<battleSessionId>>); interceptor NOT armed
  (corrected procedure, OD-RECOVERY-080 — arming fails the poll's own reads).
- Poll: `verdict=<<stable-resolver-positive>>`, <<resolved>>/24 resolved,
  <<distinct>> distinct, <<within1>> within one unit, <<within3>> within
  three, `allModuleRooted=<<true>>`, `allConsistentDoubleRead=<<true>>`
  (the per-read byte-identical branch), `sameDecodedClockProven=<<true|false>>`,
  `stableRootLiveRepeatabilityProven=<<true|false>>` (priors:
  OD-075 + OD-076 aggregates).

## Decision

<<G1: claimable/not (24/24 stable-resolver-positive with
allConsistentDoubleRead=true). G3: claimable/not
(stableRootLiveRepeatabilityProven=true). G2: claimable/not (anchor POSTed,
flag true). G0: not yet - the publication review follows.>>

## Next

<<G0 publication review if G1 + G3 closed; otherwise the specific remaining
evidence.>>
```

## Field mapping (g1-evidence.json / poll aggregate → ledger)

| Evidence field | Source | Lands in ledger |
|---|---|---|
| `pollExit`, `pollSucceeded` | g1-evidence.json | `liveRun` |
| `gamePid`, `battleSessionId`, `entityId` | g1-evidence.json | `liveRun` |
| `armedRecordAddress`, `armedPageAddress` | g1-evidence.json (position-page resolve; not armed in corrected mode) | `liveRun.recordAddress` / `pageAddress` |
| `interceptorArmed=false`, `verdict.verdict` | g1-evidence.json | `liveRun.interceptorArmed`, `writeObservationVerdict` |
| `verdict`, `resolvedReads`, `distinctPositionCount` | od073-poll.json | `liveRun` / `proof` |
| `withinOneWorldUnit`, `withinThreeWorldUnits` | od073-poll.json | `liveRun` |
| `allModuleRooted`, `allEntityIdentityRevalidated`, `allConsistentDoubleRead` | od073-poll.json | `liveRun` / `proof.hardwareAtomicRead` |
| `sameDecodedClockProven` | od073-poll.json | `proof.sameDecodedClock` |
| `stableRootLiveRepeatabilityProven` | od073-poll.json | `proof.stableRootLiveRepeatability` |

## Fill-in checklist (all must hold before claiming flags)

1. `g1-evidence.json` exists, `pollSucceeded=true`, `pollExit=0`.
2. Poll aggregate is `stable-resolver-positive` with 24/24 resolved and all
   three `all*` flags true — **this is the G1 claim** (per-read byte-identical
   branch; `allConsistentDoubleRead` requires every `Resolved` read to be a
   byte-identical double-read with a stable ring index).
3. G3: `stableRootLiveRepeatabilityProven=true` in the poll aggregate (it
   flips only when the run is positive AND the `-PriorResultPaths` files are
   positive od073 aggregates; verify the two prior paths in the evidence row).
4. G2: `sameDecodedClockProven=true` in the poll aggregate (anchor POSTed).
5. `interceptorArmed=false` and no interceptor report referenced (corrected
   mode); if a legacy armed run is ever recorded, the write-observation
   claim rules of OD-RECOVERY-080 apply instead.
6. No process left running (`shutdown` row = 0).
7. No resolver/read-surface/offset-table change in the same change.
