# OD M3 — FRESH44 LIVE CROSS-BATTLE CORRELATION (2026-08-08)

**Session outcome:** FRESH44 repeated the viewpoint-position correlation on a
second independent replay in a fresh positively verified offline launch. The
selected x family scored 0.9375 (15/16), with 21 durable sampled series and
several perfect 16/16 survivors. BLK-0019 is resolved. The bounded write trace
stayed live but captured zero writes, so no offset is promoted.

## Repository state

- Branch: `main` at `802a2ac` before this session's uncommitted changes.
- Working tree: intentionally dirty with the focused safety/reporting/docs
  changes listed below; the existing `.freebuff/worktrees/` remains untouched.
- All private replay, database, capture, and log artifacts remain local and
  ignored.

## Live result

| Evidence | Result |
|---|---|
| Safety gate | `OfflineReplayVerified` |
| Replay independence | second content-distinct replay, fresh process |
| Correlate verdict | `evidence-strong` |
| Addresses / samples | 812 / 12,992 |
| Strong survivors | 21 |
| Durable series | 21 × 16 samples |
| Selected family | x-only solo, four members |
| Selected score | 0.9375 (15/16), span 249.2, band 4.0s |
| Trace | complete, 25s, liveness `running`, three pages armed |
| Trace result | zero hits, zero write sites, zero source pages |

The correlation repeats FRESH43's 0.933 (14/15) result on an independent
battle. The zero-hit trace is valid negative evidence for this bounded window;
it does not reproduce FRESH43's game-code matrix-fill hit and does not refute
the correlation.

## Accepted conclusions

- BLK-0019 is resolved: cross-battle replay independence is now demonstrated,
  not merely available.
- M3 cross-battle repeatability is satisfied for the transient
  viewpoint-position correlation phenomenon.
- No publishable offset exists yet. FRESH44 is not `family-complete`; the
  selected family is x-only, the addresses are transient heap copies, no stable
  module RVA/pointer chain exists, and `[entity+0x3C]+0x1C/0x20/0x24` has not
  been read live at the same decoded clock across replays.
- Do not repeat the delayed trace unchanged. The smallest changed hypothesis is
  an immediate (<100 ms) position-triple read at correlate completion with
  object/displacement provenance.

## Safety and reporting fixes

The first launch attempt was stopped after a read-only audit found that the
published interceptor predated source-arm support. Its incomplete log was
deleted and no evidence was accepted. Before retrying:

- the x86 interceptor was republished and its source-arm/fail-closed synthetic
  tests passed;
- FRESH44 gained an interceptor-freshness preflight;
- durable output now redacts full paths, replay identity, hashes, and raw marker
  lines, and JSON path metadata is leaf-only;
- the runner now stops the research host after the bounded campaign;
- OD-048 reports the total strong-survivor count and whether its 20-item summary
  array was truncated.

## Changed files

- `scripts/invoke-fresh44-crossbattle.ps1`
- `tmpwotb-e2e/od-049-autoloop.ps1`
- `scripts/launch-offline-replay-for-od.ps1`
- `scripts/od-048-monitor-correlate-session.ps1`
- `scripts/invoke-csharp-write-trace.ps1`
- `tmpwotb-e2e/test-csharp-write-trace.ps1`
- `knowledge.md`
- `offline/offset-discovery.md`
- `docs/operations/blocker-log.md`
- `docs/operations/offset-discovery-ledger.md`
- this handoff

## Validation

- WriteInterceptor Release `win-x86` self-contained publish: PASS.
- `tmpwotb-e2e/test-guard-interceptor.ps1`: PASS capture, source-arm capture,
  and invalid-target fail-closed controls.
- `tmpwotb-e2e/test-csharp-write-trace.ps1`: PASS durable capture/family report.
- PSScriptAnalyzer hygiene gate: PASS (111 tracked PowerShell files).
- FRESH44 `-CheckOnly`: PASS after freshness/privacy hardening.
- Accepted live FRESH44 round: exit 0; evidence inspected independently of exit
  code; all game/interceptor/research-host processes stopped afterward.
- Privacy scan over the accepted result, trace, capture, family, and log files:
  zero full paths, replay hashes, or player-name matches.
- Full `scripts/validate.ps1`: PASS — locked restore, formatting, Release build
  (0 warnings/errors), 620 passed tests + 2 opt-in skips, repository scan,
  PowerShell hygiene, offline links/freshness, blocker numbering, and ledger
  consistency.

## Integration risks

- The interceptor publish is ignored build output; future live runs depend on
  the new freshness preflight remaining in the entrypoint.
- A driver exit code of 0 means the bounded campaign completed, not that M3 or
  promotion succeeded. Always inspect correlation, `seriesEvidence`, and trace
  verdicts.
- The accepted trace's zero hits apply only to the observed 25-second window.
- `offline/file-tree.md` is current for tracked `HEAD`; refresh it again after
  staging this new handoff and before committing, because the generator uses
  `git ls-files` and cannot include an untracked file.

## Recommended next steps

1. Run a read-only promotion review. Record correlation repeatability as
   satisfied, but keep publication blocked on addressability and same-clock
   position-triple evidence.
2. Validate any immediate-read tooling synthetically before another live round.
3. Before committing, stage the new handoff, refresh `offline/file-tree.md`,
   and rerun the freshness check so the staged file appears in the snapshot.
