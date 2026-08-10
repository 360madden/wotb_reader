# G0 — Operator-approval gate (one page)

Everything below is ready. The offset table is frozen until you run this
gate. Sources: `docs/operations/g0-offset-table-draft.md` (the change),
`docs/operations/g0-post-publication-regression.md` (the contract + test),
`docs/operations/offset-discovery-ledger.md` OD-RECOVERY-082 (the evidence).

## 1. Pre-flight facts (already verified 2026-08-09)

| Fact | Value |
|---|---|
| G1 (hardware-atomic read) | CLOSED — stored v4 aggregate 24/24 `stable-resolver-positive`, `allConsistentDoubleRead=true` |
| G2 (same-decoded-clock) | CLOSED — `sameDecodedClockProven=true`, 4 live confirmations |
| G3 (repeatability) | CLOSED — positive verdict + validated OD-075/076 priors |
| Executable identity | `wotblitz.exe` v11.19.0.10 = `1cda5c31919c9784a41bee7f3270ec1b4536b124c51e8b36f2221b381760307d` (re-measured) |
| Promotion scope | `playerPositionX/Y/Z` float32 triple at record `+0x10/+0x14/+0x18` |
| NOT promoted | velocity `+0x28`, `playerYaw` (Stale), `replayTime`, `playerHP`, `cameraPitch`, `aliveTankCount` |

## 2. Apply the change (from the draft, §1–§4)

1. `memory-offsets/schema.json` — add the top-level `chains` property
   (draft §1); `schemaVersion` stays 1.
2. `memory-offsets/11.19.0.10.json`:
   - `confidence: "high"`, `discoveredAtUtc` = now, `notes` + G0 summary.
   - `offsets` UNCHANGED (all 0 — chained fields must stay 0).
   - `fieldValidation.playerPositionX/Y/Z` → `status: "Verified"`,
     APPEND the 3 verification evidence entries to the existing history,
     `independentProcessLaunches: 4`, `independentReplays: 2`,
     `harnessInvariantsPassed: true`, approvals set by the operator.
     **`playerPositionY` gets a NEW fieldValidation entry** (missing today).
   - New `chains` section: X/Y/Z module-relative chains (draft §2b; root RVA
     `0x04095C88` = decimal **67722376** — not 67518856).
3. `offline/memory-offsets.md` — document the `chains` section (draft §3).
4. `scripts/python/offset_check.py` — chains validation (ALREADY MERGED,
   commit `7814d27`; no-op until `chains` exists).

## 3. Post-edit gates (in order; stop on any failure)

```text
python scripts/python/offset_check.py --check-schema        # PASS expected
tools/report-offset-evidence.ps1 -GameVersion 11.19.0.10     # runs clean
python scripts/python/offline_check.py --refresh             # file-tree + links
dotnet test tests/WotBTreader.GameIntegration.Tests -c Release --filter "FullyQualifiedName~ChainedFields"   # exclusion test passes
scripts/validate.ps1                                         # full gate, exit 0
```

Expected spot-checks:
- `offset_check.py`: "PASS: All offset files are valid" + the file logs
  "chains validated (3 field(s))".
- Exclusion test: `ChainedFields_AreExcludedFromObservationReads` — Passed
  (chained field never read as `moduleBase + 0`; position stays null).
- `validate.ps1`: exit code 0 (all test projects, repo scan, PSSA baseline).

## 4. Commit (ONE change)

Files: `memory-offsets/11.19.0.10.json`, `memory-offsets/schema.json`,
`offline/memory-offsets.md`, `offline/file-tree.md` (regenerated),
`docs/operations/offset-discovery-ledger.md` (OD-RECOVERY-083:
`numericOffsetPublication: true` + the re-measured hash + timestamp), and a
handoff. Message: conventional commit, e.g.
`feat(od): publish playerPositionX/Y/Z as Verified via the module-rooted
position-ring chain (OD-RECOVERY-083)`. Do NOT include any resolver/read-
surface change, `playerYaw`, or velocity in the same change.

## 5. After the commit

- The legacy observation path still emits position nulls (chained fields
  excluded — pinned by the regression test).
- The resolver path is authoritative for position and is untouched.
- No further live sessions are required unless a gate fails.
