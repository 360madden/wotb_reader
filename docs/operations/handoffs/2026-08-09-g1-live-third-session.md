# Handoff — 2026-08-09: Third G1/G2/G3 live session (OD-RECOVERY-081)

Date: 2026-08-09
Status: milestone partial — first fully clean live read run (24/24, all
attempt-1); G2 re-proven live (3rd confirmation); verdict label blocked by a
verdict-contract conflict, now fixed in the poll (schema v4); gates not
closed on a re-derived label
Scope: one approved live session via `scripts/invoke-g1-live-poll.ps1`
`-SkipInterceptorArm` + `-PriorResultPaths` (OD-075 + OD-076) on the Oasis
Palms replay; resolver, read surface, and offset table untouched

## Session summary

Ran the corrected one-command chain end-to-end: launcher → `OK
OfflineReplayVerified` → gate-verified position-page resolve (record
`0x22CE5FC8` / page `0x22CE5000`) → the unchanged bounded od-073 poll
**un-armed** inside the active battle → clock anchor POST at the gate →
verdict → evidence. The poll resolved **24/24 — every read on attempt 1**
(the first fully clean live read run; OD-075/076 were 24/24 but from the
pre-G2 era, and the armed sessions could not reach it), with 24 distinct
triples, 15 within one world unit, 21 within three,
`allConsistentDoubleRead=true`. **Yet the verdict label came back
`honest-negative-or-inconclusive`** — a verdict-contract conflict, root
caused and fixed in this session (below).

## Evidence (all in `.data/diagnostics/g1-live-20260809-214127/`)

- `g1-evidence.json` — verdict `write-observation-skipped` (corrected
  mode), `pollSucceeded=true`, `pollExit=0`, `interceptorArmed=false`
- `od073-poll.json` — `honest-negative-or-inconclusive` **as computed**,
  resolved **24/24** (statusCounts all `Resolved`; attemptCounts `1:24` —
  zero failures), 24 distinct, 15 within-one, 21 within-three,
  `allModuleRooted=true`, `allEntityIdentityRevalidated=true`,
  `allConsistentDoubleRead=true`, `moving=true`, `trajectoryConsistent=true`,
  `sameDecodedClockProven=true`
- Priors supplied via `-PriorResultPaths`: OD-075 + OD-076 aggregates.
  **CORRECTION (2026-08-09, OD-RECOVERY-082):** the comma-joined
  `-PriorResultPaths 'a,b'` form was bound by `-File` as a SINGLE path, so
  the poll's prior validation never accepted them — and in this run it never
  exercised the block at all (the verdict was negative). The priors were
  validated directly against the poll's exact fail-closed check and pass;
  the wrapper now normalizes comma-joined paths.

## The verdict-contract conflict (root cause of the honest-negative label)

The positive verdict's condition still included `-not $anySameDecodedClock`.
That clause was written when the coordinator **hardcoded**
`SameDecodedClockProven` to false (pre-G2). The G2 wiring computes it from
replay-clock segments, so **every** read reports it true once the clock
anchor lands — which is exactly what a working G2 does. A working G2
therefore made the G1 positive verdict **unreachable by construction**: the
two gates could never both be satisfied. OD-075/076 predate the G2 anchor
(flag false), which is why they passed.

**Fix (committed with this record):** the poll no longer disqualifies runs
where the same-decoded-clock proof fired — it is orthogonal evidence
reported separately as the G2 claim and does not weaken the byte-identical
double-read stability claim. Schema bumped to
`wotbtreader.od073.entity-position-poll.v4`; `hardwareAtomicReadProven`
stays required-false (resolver never claims it; defensive fail-closed).
Validation on the stored evidence (untouched): run-081 re-derives to
`stable-resolver-positive` under the v4 logic; OD-075/076 re-derive positive
under both logics.

## Decision

- **Read evidence: complete and clean** — the exact evidence the G1
  acceptance requires (24/24 `allConsistentDoubleRead`), now under the
  corrected un-armed procedure.
- **G2: closed live, third independent confirmation**
  (`sameDecodedClockProven=true`).
- **G1 / G3: NOT closed on the re-derived label.** The workflow's
  fail-closed discipline requires the stored aggregate itself to be
  `stable-resolver-positive`; run-081's stored aggregate says
  `honest-negative-or-inconclusive` (schema v3). One more session with the
  fixed v4 poll produces the clean positive aggregate and closes G1 + G3.
- **G0: stays gated.**

## Next

The identical command with the fixed v4 poll (see the ledger's Next
planned session row for the exact invocation): 24/24 `stable-resolver-positive`
closes G1 (per-read byte-identical branch) and, with the OD-075 + OD-076
priors, G3 (`stableRootLiveRepeatabilityProven=true`); then the pre-staged
G0 publication review applies. All managed processes were stopped after the
session (0 remaining). No product code changed this session beyond the
poll's verdict-logic fix (schema v4) documented above.
