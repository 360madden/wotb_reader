# FRESH14 session plan — M1→M2 live validation with all fixes in

**Date:** 2026-08-06 · **Status:** PLAN (not yet run) · **Type:** live validation runbook
**Companion docs:** [`offset-discovery-m1-m2-choreography.md`](../offset-discovery-m1-m2-choreography.md)
(timing budget + hazard table), [`2026-08-05-od-049-session-prep.md`](2026-08-05-od-049-session-prep.md)
(pinned session facts + host cycle), [`2026-08-06-fresh12-y-survivor-audit.md`](2026-08-06-fresh12-y-survivor-audit.md)
(band-floor + solo-survivor gap).

## 1. Purpose

Run the M1→M2 pipeline live ONCE, with every fix from FRESH8/9/10/12/13 and the
two 10-round hunts in place, and produce the first **armed write-trace hit
report for a genuinely tight-band family** — or an honest, evidence-backed
negative that closes the track per the descope gate.

**This is the decisive attempt:** M1 CAP is 2 sessions (FRESH8/10 consumed the
first rounds of evidence), M2 CAP is 2 attempts, and the roadmap descope gate
says 0 write-trace hits on a clean small set = stop. FRESH14 must therefore
not be an exploration — every chunk below is independently verifiable, and the
plan fails closed at each gate so no game session is wasted on a broken
precondition.

## 2. Pinned state (verified 2026-08-06, HEAD `e90e110`)

| Fix | Where | Evidence in tree |
|---|---|---|
| Window-wait (FRESH8 blocker) | `x64dbg-write-trace.ps1` | `Wait-X64DbgWindow`, `-WindowWaitSeconds 20` (default) + `EnumWindows` fallback |
| Attach-smoke gate (FRESH9 chunk) | `od-048` | `-AttachSmokeOnFirstRound` → exit 6 on red smoke, `od-048-attach-smoke-*.json` |
| Band-width floor (FRESH13) | both gates | `-AutoTraceMaxMemberBandSeconds 20.0` (od-048) / `-MaxMemberBandSeconds` (write-trace), `Get-MemberBandWidth`, `Test-FamilyBanded` |
| Rendezvous expiry (hunt R18) | `od-048` | records `expired >30s` treated as absent |
| Anchor UTC-date (FRESH9) | `od-049-autoloop.ps1` + 4 wrappers | `Get-LogAnchorDateUtc` (filename date → LastWriteTime → UtcNow) |
| Scorer tie-break / family dedup / ≥2-sample floor | C# + endpoint | committed `c13e2d7`, 613/615 tests green |

## 3. Known gap that shapes the plan: the solo-survivor path is NOT built

FRESH12 proved `0x1FC57238` (y@1.000, tight **interior** band [−10,−8] = 2.5s)
is the single most discriminating artifact the pipeline has produced — and that
it was **structurally excluded** from the armed family because its ±16-byte
neighbors scored below the seed floor. FRESH13 added the band floor (which its
metrics pass) but **did not build solo arming**: both gates require ≥ 2 members
(`Test-UsableFamily` line 451: `$members.Count -lt 2 → return $false`).

**Consequence for FRESH14:** the live round can arm a family (≥2 members) but
cannot arm a lone tight-band survivor. The plan therefore contains a **decision
gate (Chunk 3)** before any launch: build the solo path (recommended, offline,
fully testable against the FRESH10 report) or run live accepting the
≥2-member-only limitation. Chunk 3 costs zero sessions either way.

## 4. Chunk map — each chunk is independently verifiable

### Chunk 0 — Offline: fixes present + static gates (no game, ~5 min)

**Commands**
```powershell
git log --oneline -1                    # expect e90e110 or newer
grep -n "Wait-X64DbgWindow" scripts/x64dbg-write-trace.ps1
grep -n "AttachSmokeOnFirstRound" scripts/od-048-monitor-correlate-session.ps1
grep -n "MaxMemberBandSeconds\|Test-FamilyBanded" scripts/x64dbg-write-trace.ps1
grep -n "expiresAt.*AddSeconds(-30)" scripts/od-048-monitor-correlate-session.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/invoke-scriptanalyzer.ps1
```
**Expected outputs:** all four greps non-empty; PSSA gate PASSED (0 errors);
`pwsh -NoProfile` parse of both scripts OK; `dotnet build` 0 warnings.
**Fail-close:** any grep empty or PSSA error → STOP, fix before any launch.

### Chunk 1 — Offline: dry-runs against the FRESH10 report (no game, ~10 min)

The band floor must demonstrably do its job on **real** evidence before we
spend a session on it.

```powershell
# Refuse the real FRESH10 report (degenerate y@1.0 band) — expect exit 2.
pwsh -NoProfile -File scripts/x64dbg-write-trace.ps1 -FamilyFile .data/od-049-fresh10-result.json -AutoWriteTrace -DryRun
```
**Expected outputs:**
- Exit 2 (refused) with a skip reason naming the degenerate band member.
- A synthetic tight-band family with `0x1FC57238` metrics (2s interior band,
  non-edge) **arms** (exit 0, 3× `bpm`/`w,4` lines) — proves the floor
  discriminates, not just blocks.
- `od-048` fail-closed smoke with no game: exit 1, `FAILED_gate_never_verified`,
  no report file (matches the P0 pre-flight behavior).
**Fail-close:** refusal of the real report is the required outcome; if the
synthetic tight-band family does NOT arm, STOP — the arm path is broken.

### Chunk 2 — Offline: autoloop wrapper parity (no game, ~10 min)

`tmpwotb-e2e/od-049-autoloop.ps1` is the timing-first entry point (anchor on
the Start marker, `-StageTopN 2`, `-StageDelaySeconds 2`, auto-trace on
verdict) but does **not** pass `-AttachSmokeOnFirstRound`. Add it (plus the
band-floor defaults are already in effect) and re-run Chunk 0 static gates.
**Expected outputs:** autoloop invokes M1 with `-AttachSmokeOnFirstRound`
(visible in its m1: log lines); PSSA + parse still green.
**Fail-close:** PSSA error → STOP.

### Chunk 3 — DECISION GATE: build the solo-survivor arming path? (no game)

**Recommendation: build it before the live round.** It is ~30–60 min of
offline, fully testable work (the FRESH10 report is the fixture), it directly
re-enables the pipeline's strongest evidence class, and it removes the biggest
known reason a live round could end `family_mapping_failed` on a lone
tight-band survivor. Scope:

- `x64dbg-write-trace.ps1`: new `-SoloAddress <hex> [-SoloAxis <x|y|z>]`
  family-file-less mode OR a `members: [single]` acceptance in
  `Test-UsableFamily` when the sole member clears the score + band floors.
- `od-048`: on a final verdict whose best survivor is a lone tight-band
  non-edge result, emit a solo-armable family in the report (the result
  already carries address/axis/sign/shift/band — the builder just refuses it).
- Validation: DryRun arms `0x1FC57238`-metrics solo fixture (exit 0), refuses
  the FRESH10 degenerate y@1.0 solo result (exit 2).

**Alternative (defer):** run live with the ≥2-member-only gates, accepting
that a lone tight-band survivor ends the round as `family_mapping_failed` —
the round is then a timing/verdict negative, not a field negative.
**No session is spent on this decision either way.**

### Chunk 4 — Offline: host freshness + trajectory (no game, ~5 min)

Per the session-prep Phase 0 — republish is **mandatory** before every session
(Jul-31-class stale publish blocker):

1. `serve.cmd` (republishes) — or verify `launch-offline-replay-for-od.ps1`'s
   stale-build guard passes (`bin\Release` newer than `src\*.cs`).
2. `GET /api/v1/sessions?limit=1` → newest item is medvedkovo (per-import id).
3. `GET /api/v1/game/discover/trajectory/{newestId}` → 200, 14 entities,
   viewpoint Churchill_I, real x/y/z samples.
4. Rendezvous `%LocalAPPDATA%\WotBTreader\rendezvous\web.json` present.

**Expected outputs:** all four green; the trajectory 200 with real samples is
the gate that would have caught the stale publish.
**Fail-close:** any of 1–4 fails → STOP; do not launch.

### Chunk 5 — LIVE: the round (one launch, ~6 min hands-off)

**Precondition for the operator:** hands off the game window from the moment
the launch command runs (no Space, no alt-tab into the game, no window
clicks) — the auto-trace's play-state probe and the attach smoke both assume a
clean window.

```powershell
pwsh -NoProfile -File tmpwotb-e2e/od-049-autoloop.ps1 `
  -ReplayPath .data\launch\a9aed0467d7843efb06bb3319bb52ded.wotbreplay `
  -MaxReadRounds 70 -StageTopN 2 -StageDelaySeconds 2 `
  -ResultPath .data\od-049-fresh14-result.json
```

**Expected outputs, in order (watch the log):**
1. `launch_exit=0`; `marker_found log=blitz-logs_*.txt ... -> utc=…` (the
   anchor; must be today, not a full day off — the FRESH9 bug class).
2. `attach_smoke INVOKING round=2` then `attach_smoke ok pid=0x… pause=True
   bpm=yes resume=True` + `od-048-attach-smoke-*.json` with `smoke: ok`.
   **Red smoke → exit 6, campaign aborted before the correlate** — that is
   the gate working; diagnose attach-vs-address, do NOT proceed.
3. `round=N/70 series=… samples=…` lines — budget on the **observed** rate
   (see §5 timing note).
4. `family_refined round=10 survivors=N neighbors_added=M` (provisional
   correlate seeds sibling staging).
5. Final correlate verdict — one of: `family-complete` (clean x/y/z triple),
   `family ≥ 2 members`, `family_mapping_failed`, or `evidence-strong` with
   strong survivors. Report lands at the `-ResultPath`.
6. On a usable family: `x64dbg_pid=…`, `attached pid=0x…`, `injected
   scriptload+scriptrun`, `released_detach`, `hits=N`,
   `family_verdict=family-hit`, and `odwt-0x<addr>.bin` proof files +
   `<result>.family.json`. M1's own report stays immutable; the auto-trace
   report is `od-048-autotrace-*.json`.

**Stop rules inside the chunk:** red attach-smoke → stop (exit 6); verdict
`family_mapping_failed` → stop per the M2 stop rule, do NOT burn the trace
attempt; exit 7 (replay paused) → resume with SPACE and rerun within the
window; exit 8 (stale family) → never relaunch between M1 and M2.

### Chunk 6 — Evidence interpretation (after the round, no game)

1. If `family-hit`: disassemble the first `odwt-<addr>-<rip>.bin` in
   `%TEMP%\od-wt-hits\` → the writing instruction (`movss [reg+0x28], xmm0`
   class) → the member displacement. This is the M2 exit criterion.
2. If `family-no-hit` with a tight-band armed family: read it as a timing
   negative (battle ended before the deadline) or a genuine field negative —
   the report's `family_verdict` + trace timing line distinguish them.
3. Append the outcome to `offset-discovery-ledger.md` (ledger vocabulary) and
   to a FRESH14 handoff with exit codes, verdict, per-round timing, and the
   evidence links.

### Chunk 7 — Descope check (no game)

Per the roadmap descope gate, trigger on ANY of:
- M1: no strong survivor (score ≥ 0.7) across the session(s);
- M2: `family_mapping_failed` on the survivors;
- M3: 0 write-trace hits in 2 attempts on a small clean set.

If triggered: archive the pipeline + evidence, append a closeout entry to the
ledger and blocker log, mark the track research-only. If the solo path (Chunk
3) was built and the round produced a lone tight-band survivor, that is a
candidate for ONE retry under the same budget, not a new campaign.

## 5. Timing note (the 271s vs 107s tension — calibrate, don't assume)

The choreography's timing table budgets against the 271.4s ground-truth
duration, but the session-prep's live measurements recorded a **~107s
playable battle window** (LoadGameScene → onLeaveWorld) on 08-05, and the game
**auto-loops the replay** with ~10s between battles (one launch = repeated
windows). FRESH8's monitor did complete 70 rounds, so neither number is
universal. **Rule: in Chunk 5 step 3, watch the `round=N/M` lines and budget on
the observed per-round rate.** If rounds run ~2s, 70 is safe; if ~3s+, the
final correlate may land at/after battle end and the trace window collapses —
accept `family-no-hit` as a timing negative and re-run the driver on the next
auto-loop window WITHOUT relaunching the game (the autoloop wrapper's
`-ReplayStartWallTimeUtc` anchor keeps it aligned to the next battle).

## 6. Explicit non-goals / guardrails (reaffirmed)

- No replay rewind for M2 (DAVA viewer is seek-forward-only).
- No relaunch between M1 and M2 (liveness check exit 8 exists to reject it).
- No `-SkipLivenessCheck` / `-SkipPlayProbe` on the live round.
- `roll-replay-time-increased.ps1` is a memory-scan roll, not a replay rewind.
- The 30s rendezvous-expiry and the 20s band floor are **not** to be disabled
  "to save time" on the live round — they are the discrimination that makes a
  hit report mean something.

## 7. Post-run documentation (do after Chunk 5, before Chunk 7)

- Append the FRESH14 outcome to the ledger using the ledger vocabulary.
- Append a FRESH14 handoff mirroring the FRESH8/FRESH10 format (outcome,
  root cause if any, fixes, evidence links).
- If the solo path was built (Chunk 3), document its contract + validation in
  the same handoff and update the roadmap M2 row.
