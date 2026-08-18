# Handoff — batch rehearsal pre-staged: driver + cross-check tool (2026-08-11)

## Summary

Pre-staged item 3 of the batch entity-region read design (the X2 replay
rehearsal): `scripts/invoke-batch-rehearsal.ps1` (the approved-session
driver) + `scripts/python/batch-rehearsal-crosscheck.py` (the cross-check
tool). The rehearsal is now **one command away**: with the web host serving a
verified offline replay, one `-LiveAcquire` run dumps the whole roster per
replay time through the new batch endpoint and verifies every memory position
against decoded ground truth.

## What landed

**`scripts/python/batch-rehearsal-crosscheck.py`** (stdlib only):
- `--roster`: the decoded roster (participant entity ids in team order) +
  session duration from the treader SQLite store — proven on savanna:
  14 entities, 279.9 s.
- `--compare`: reads a dumps file (schema
  `wotbtreader.od.batch-rehearsal.dumps.v1`), decodes each ring-record
  dump's float32 triple at **+0x10** (the published chain's
  `PositionRecordOffset`), and matches it to the decoded `position_samples`
  nearest the batch's replay-clock label. Per-time/per-entity delta table;
  PASS = every compared pair within tolerance, MISS rows printed with the
  deltas. Exit 0 PASS / 1 any miss / 2 no-verdict.
- `--self-test`: synthetic fixture of the decode + tolerance logic (no DB).
- Proven on real decoded data: 42/42 pairs matched (3 times x 14 entities),
  a deliberately corrupted position was detected (50 m delta, exit 1).

**`scripts/invoke-batch-rehearsal.ps1`** (ASCII-only, mirrors the hp-diffing
driver's rendezvous/API pattern):
- QUALIFY: python `--roster`; validates the roster fits the batch cap (16)
  and the dump times sit inside (0, duration); default 5 evenly spaced times
  when `-Times` is empty.
- DUMP (gated): one `POST /api/v1/game/discover/entity-regions` per time
  with the WHOLE roster, requiring `status == Resolved` and
  `sameDecodedClockProven` on every batch (fail-closed); writes the dumps
  file with the response's actual replay-clock labels. Exits 3 with the
  contract when no host/no `-LiveAcquire`/no existing dumps.
- VERDICT: python `--compare --tolerance`; `-FailOnMiss` exits 1 on any
  miss or no-verdict. Exit paths verified live: 0 (clean), 1 (corrupt +
  FailOnMiss), 3 (no dumps/no acquire).
- PSScriptAnalyzer hygiene: 0 findings on the new script (121-file run,
  gate passed).

## Files touched

- `scripts/python/batch-rehearsal-crosscheck.py` (new)
- `scripts/invoke-batch-rehearsal.ps1` (new)
- `docs/operations/batch-entity-read-design.md` (item 3 → PRE-STAGED)
- `docs/operations/resolver-path-consolidation.md` (item 6 rehearsal note)

## Verification

- Self-test PASS; real-data cross-check 42/42 + corruption detected; driver
  exits 0/1/3; analyzer clean. Full `scripts/validate.ps1` gate green
  (936 passed, 3 local opt-in skips, 0 warnings, 0 errors); file-tree
  regenerated.

## Remaining

- One approved live session: start the web host, verify the offline replay
  (savanna session `019fdff7-8dcf-7426-8547-9fb8cc3eb07b`), then
  `pwsh -File scripts/invoke-batch-rehearsal.ps1 -SessionId
  019fdff7-8dcf-7426-8547-9fb8cc3eb07b -LiveAcquire -Times 90,150,220
  -FailOnMiss` — the verdict closes item 3 and measures the batch window for
  item 4 (which feeds item 7, still LAST).
