# OD M3 — FRESH45 IMMEDIATE POSITION TRIPLE (2026-08-08)

**Session outcome:** FRESH45 completed one positively verified offline round and
tested four candidate-derived contiguous XYZ layout hypotheses immediately
after correlation. All 12 requested Float32 reads succeeded, but none produced
a complete decoded XYZ match. This is an honest negative for those four layouts
at that sampled instant and a successful validation of the immediate-read
instrumentation. No offset is promoted.

## Live result

| Evidence | Result |
|---|---|
| Safety gate | `OfflineReplayVerified` |
| Correlate verdict | `evidence-strong` |
| Addresses / samples | 866 / 13,856 |
| Strong survivors | 22 (x=19, y=0, z=3) |
| Immediate hypotheses | four `candidate-0x1C` proposed bases |
| Immediate reads | 12 requested, 12 readable |
| Complete XYZ matches | 0 / 4 |
| Immediate verdict | `no-hypothesis-match` |
| Dispatch / request / completion gap | 81.838 / 2.28 / 102.2 ms |
| Provenance flags | object base=false, atomic=false, same clock=false |
| Delayed write trace | intentionally skipped |

One candidate's X value was within the 6-unit exploratory tolerance, but its Y
and Z neighbors missed by 28.6 and 10.5 units. Two high-scoring candidates had
zero-valued neighbors. The 2.2 ms target overrun cannot reasonably account for
those mismatches, so another latency-only repeat is not justified.

## Accepted conclusions

- The one-call immediate Float32 read path works under the positive offline
  gate and persists explicit timing, shift-range, candidate, and proof-limit
  metadata.
- The four tested `candidate-0x1C` contiguous layouts are rejected at this
  sampled instant.
- The static transform layout is not globally refuted. No actual transform
  object pointer was captured, and the selected addresses remain transient
  candidate copies.
- M3 cross-battle correlation repeatability remains satisfied for the transient
  viewpoint-x phenomenon. BLK-0019 remains resolved.
- The position fields remain `0` / `Unknown`; no promotion count, provenance,
  approval, or offset-table field changes.

## Tooling and safety changes

- `scripts/od-048-monitor-correlate-session.ps1` can select a bounded number of
  strong viewpoint-x candidates, derive explicitly hypothetical bases, batch
  read the proposed XYZ members before report serialization/trace launch, and
  preserve measured timing and proof-limit flags.
- `tmpwotb-e2e/od-049-autoloop.ps1` passes the immediate-read switch and can
  omit the delayed trace path entirely.
- `scripts/invoke-fresh44-crossbattle.ps1` now supports dedicated fresh result
  and log paths, fails closed on stale/malformed/instrumentation-failed output,
  skips interceptor preflight when tracing is disabled, and never reports stale
  trace artifacts in skip mode.
- The accepted artifacts contain no replay bytes, full paths, hashes,
  capability material, account/player fields, chat, or screenshots.
- The game, research host, and interceptor were all stopped after the run.

## Validation

- `tmpwotb-e2e/test-immediate-position-triple.ps1`: PASS, 10 cases.
- `tmpwotb-e2e/test-viewpoint-filter.ps1`: PASS.
- PowerShell 5.1 parse and ASCII checks for changed scripts: PASS.
- Focused PSScriptAnalyzer review: zero gate violations.
- FRESH45 preflight `-CheckOnly`: PASS.
- Accepted live FRESH45 round: exit 0; result classified independently of the
  process exit code.
- Dedicated result/log privacy scan: zero full paths, replay names, long hashes,
  capability fields, account/player fields, or chat fields.
- Full `scripts/validate.ps1`: PASS — locked restore, formatting, Release build
  (0 warnings/errors), 620 passed tests + 2 opt-in skips, repository scan,
  PowerShell hygiene over 112 tracked scripts, offline links/freshness, blocker
  numbering, and ledger consistency.

## Next admissible proof

Do not repeat FRESH45 unchanged. Offline/static-only, derive and synthetically
validate a bounded capture anchored at the already evidenced game-code
transform-fill instruction/register path. The next live round is justified only
when it can preserve the actual register-derived object pointer and immediately
read that object's `+0x1C/+0x20/+0x24` members against decoded ground truth.

Raw replay, database, result, log, and memory evidence remain private, local,
ignored, and uncommitted. The unrelated `.freebuff/worktrees/` directory was
left untouched.
