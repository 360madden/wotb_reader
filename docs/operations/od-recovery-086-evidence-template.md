# OD-RECOVERY-086 live-run evidence template (pre-staged 2026-08-11)

Fill this in after the next approved live session. The static values below
are already known; only the `<<...>>` placeholders need evidence from the
run. The session composes TWO rehearsals that share the same approved
launch (they are the same seam family and same replay):

1. **X2 batch rehearsal** — the whole-roster batch read through
   `/discover/entity-regions` per replay time, cross-checked against
   decoded positions, measuring the read-pass window (feeds item 7).
2. **X3 live-roster enumeration** — `/discover/entity-roster` enumerated
   ids verdict against the decoded participants roster (matched/missing/
   extra + movement-filter precision), the measurement that decides
   whether the enumerated avatar family IS the decoded roster.

Run (one command; `-EnumerateLive` + `-LiveAcquire` compose both, with the
**enumerated** ids driving the batch dumps):

```text
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/invoke-batch-rehearsal.ps1 `
  -SessionId <decoded-session-guid> -EnumerateLive -LiveAcquire `
  -Times 90,150,220 -FailOnMiss
```

Evidence lands in `.data/`:
- `roster-enum-<session>-<stamp>.json` (schema
  `wotbtreader.od.batch-rehearsal.roster-enum.v1`) — the enumeration
  evidence (ids + candidatesSeen/filteredOut + status).
- `batch-rehearsal-<session>-<stamp>.json` (schema
  `wotbtreader.od.batch-rehearsal.dumps.v1`) — the per-time batch dumps
  with one G2 clock attestation each.
- The verdict exits: enumeration cross-check 0 = exact set match; position
  cross-check 0 = all pairs within tolerance.

## Known static values (do not change without re-verifying)

| Item | Value |
|---|---|
| Target build | 11.19.0.10 |
| Executable SHA-256 | `1cda5c31919c9784a41bee7f3270ec1b4536b124c51e8b36f2221b381760307d` |
| Replay | Oasis Palms (savanna), 1,045,525 B, battle 2026-08-02T21:15:07 (content-distinct; one of the two proven 11.19.0 replays) |
| Batch caps | ≤ 16 entities / ≤ 16 KB total per batch (`EntityRegionsReadRequest`) |
| Region anchor / length | `ring-record`, 64 B (covers the 0x38 ring record + position float32 triple at +0x10) |
| Tolerance | 2.0 m (position cross-check) |
| G2 bound | `SameDecodedClockUncertaintyLimit` = 2 s; every batch must attest `sameDecodedClockProven=true` (fail-closed) |
| Decoded roster | from `batch-rehearsal-crosscheck.py --roster` (participants table) |
| Cross-check tool | proven 42/42 on real decoded data (position); enumeration mode self-tested (exact / missing / extra / traversal-limited) |

## Ledger section skeleton — `OD-RECOVERY-086`

Append to `docs/operations/offset-discovery-ledger.md` (and add the index
row + Last-updated + status-line amendment in the same change). YAML block:

```yaml
sessionId: OD-RECOVERY-086
status: <<Hit / Partial / Miss>> (X2 batch rehearsal + X3 live-roster
  enumeration, one composed session)
mode: invoke-batch-rehearsal.ps1 -EnumerateLive -LiveAcquire -Times 90,150,220
  -FailOnMiss on Oasis Palms: launcher to OfflineReplayVerified, then
  /discover/entity-roster (X3) -> enumerated ids drive /discover/entity-regions
  batch dumps per replay time (one G2 attestation per batch) -> decoded
  cross-checks
targetBuild:
  version: 11.19.0.10
  executableSha256: 1cda5c31919c9784a41bee7f3270ec1b4536b124c51e8b36f2221b381760307d
liveRun:
  launcherExit: 0
  gate: OK OfflineReplayVerified
  gamePid: <<pid>>
  decodedSessionId: <<guid - the decoded battle session used for the roster>>
  enumeratedCount: <<n - X3 avatar ids>>
  candidatesSeen: <<n - pre-filter deduped map count>>
  filteredOut: <<n - movement-filter gate rejects>>
  filterPrecision: <<matched/enumerated - from the enumeration verdict>>
  filterRecall: <<matched/decoded - from the enumeration verdict>>
  missingIds: <<[] - decoded participants NOT enumerated>>
  extraIds: <<[] - enumerated ids NOT in the decoded roster>>
  enumerationVerdict: <<0 expected (exact set match)>>
  batchTimes: <<[90, 150, 220]>>
  batchesResolved: <<n/3 - every batch must be status Resolved>>
  allBatchesClockAttested: <<true expected - sameDecodedClockProven per batch>>
  positionPairsCompared: <<n>>
  positionPairsMatched: <<n>>
  positionVerdict: <<0 expected with -FailOnMiss>>
  readPassWindowMs: <<<<first resolve -> last read per batch, from Measurement>>>
proof:
  batchSurfaceLive: <<claimable if batchesResolved = 3 with clock attestation
    and position verdict 0>>
  rosterEnumerationMatchesDecoded: <<claimable if enumerationVerdict = 0
    (exact set match) - the X3 filter precision measurement>>
  readPassWindowMeasured: <<claimable if readPassWindowMs captured per batch
    (item-7 prerequisite, not a proof of atomicity)>>
```

## What the verdict decides (branch on the evidence)

- **Enumeration verdict 0 (exact set match)** → the movement-filter vtable
  gate alone separates the avatar family from shells/effects; the
  per-frame live loop (X4) can enumerate once per battle and trust the
  roster. Record `rosterEnumerationMatchesDecoded: true`.
- **Enumeration verdict 1 (missing or extra ids)** → recorded honestly;
  the X4 loop must re-enumerate per tick or add a second discriminator
  (the open question in the X3 design). Do NOT edit any offsets or the
  read surface — the measurement is evidence, not a promotion.
- **Position verdict 0** → the batch surface reads ring records aligned to
  decoded ground truth live; `batchSurfaceLive: true`, window measured.
- **Any fail-closed exit (enumeration non-Resolved / TraversalLimited,
  batch not clock-attested, position miss)** → the session records the
  honest negative and the next attempt retries the same one command;
  no plan change without a new diagnosis.

## Files touched (this template only — fill after the run)

- `docs/operations/od-recovery-086-evidence-template.md` (this file)
- `docs/operations/offset-discovery-ledger.md` (append the OD-RECOVERY-086
  section + index row + Last-updated)
