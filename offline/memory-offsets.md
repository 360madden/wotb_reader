# Memory-offset evidence

How versioned game-memory offsets are stored, validated, and gated.
Canonical detail: [`memory-offsets/README.md`](../memory-offsets/README.md)
(discovery pipeline + confidence levels) and
[`docs/operations/offset-discovery-guide.md`](../docs/operations/offset-discovery-guide.md)
(4-phase tool workflow).

## What lives in `memory-offsets/`

| File | Purpose |
|------|---------|
| `schema.json` | JSON schema (draft 2020-12) every version file must satisfy |
| `<gameVersion>.json` | One file per game version, e.g. `11.19.0.10.json` |
| `scanner-state.json` | Runtime scanner state — **gitignored, never committed** |

## Version-file format (`schema.json` + `OffsetFileJson`)

```json
{
  "schemaVersion": 1,
  "gameVersion": "11.19.0.10",
  "executableSha256": "<sha256 of wotblitz.exe>",
  "discoveredAtUtc": "2026-07-30T…Z",
  "fieldValidation": {
    "playerYaw": {
      "status": "Candidate",
      "evidence": [],
      "independentProcessLaunches": 0,
      "independentReplays": 0,
      "harnessInvariantsPassed": false,
      "leadApproved": false,
      "decoderAuditorApproved": false
    }
  },
  "offsets": { "replayTime": 0, "playerHP": 0, "playerPositionX": 0,
               "playerPositionY": 0, "playerPositionZ": 0, "playerYaw": 0,
               "cameraPitch": 0, "aliveTankCount": 0 },
  "confidence": "none",
  "notes": ""
}
```

Required: `schemaVersion`, `gameVersion`, `offsets` (all 8 fields,
`additionalProperties: false`). `executableSha256` is required for candidate
or promoted evidence. An intentional placeholder has `confidence: "none"`, an
empty hash, `discoveredAtUtc: null`, and all eight offsets set to `0`;
placeholders are never runtime-supported. `fieldValidation` is optional; when
present, its keys are the eight known fields and each entry records promotion
evidence. `0` = unknown.

| Field | Type | Semantics |
|-------|------|-----------|
| `replayTime` | double | Replay timeline seconds |
| `playerHP` | int32 | Current hit points |
| `playerPositionX/Y/Z` | float | World units (Y = height) |
| `playerYaw` | float | Radians |
| `cameraPitch` | float | Radians |
| `aliveTankCount` | int32 | Tanks still in battle |

## Confidence levels

| Level | Meaning |
|-------|---------|
| `none` | Placeholder, nothing discovered |
| `low` | Candidates found, unverified |
| `medium` | 1–3 candidates, matches game behaviour in one battle |
| `high` | Verified across multiple battles and game restarts |

## Reader + validation (`OffsetTableReader`)

`Application/Replay/OffsetTableReader.cs` loads `<gameVersion>.json` and fails
with a specific code when anything is off:

| Check | Failure code |
|-------|--------------|
| File missing | returns `null` (no table — callers treat as unsupported) |
| Unreadable / bad JSON | `offset.read_failed` / `offset.empty_file` |
| `schemaVersion` ≠ 1 | `offset.unsupported_schema` |
| `gameVersion` ≠ file name | `offset.version_mismatch` |
| Declared `executableSha256` missing or malformed | `offset.hash_missing` |
| Observed executable hash missing or malformed | `offset.invalid_observed_hash` |
| Declared `executableSha256` ≠ observed exe hash | `offset.hash_mismatch` |

Per-field: `OffsetField(Offset == 0 ? Unknown : Candidate)` unless a
`Verified` declaration also contains at least two independent process launches,
two independent replays, passing GameHarness invariants, lead approval, decoder
auditor approval, and both static-analysis and GameHarness provenance. Only then
is the field `Verified`; runtime reads reject candidate fields. The file-level
`confidence` field maps to the table's overall `OffsetConfidence`. Domain model:
[`src/WotBTreader.Core/OffsetModels.cs`](../src/WotBTreader.Core/OffsetModels.cs)
(`OffsetField`, `OffsetTable`, `OffsetFieldStatus`, `OffsetProvenanceKind`).

The directory is resolved by `Application` DI
(`ApplicationServiceCollectionExtensions`): `AppContext.BaseDirectory` →
`memory-offsets`, falling back to repo-root discovery.

## Runtime gating (`GameSessionCoordinator`)

- `LoadOffsetTable(process)` requires an exact version + executable-hash match.
- `HasKnownOffsets(table)` is true only for fields promoted to `Verified` with
  complete evidence; placeholders and candidate-only tables are discovery-only.
  Otherwise the coordinator **refuses memory reads** (`GET /api/v1/game/memory`
  returns `Unavailable`).
- Live observation pushes only happen after the `OfflineReplayVerified` gate.

## Chains (pointer-chain verification)

Since OD-RECOVERY-083 (2026-08-10), version files may carry a top-level
`chains` object mapping a field name to an ordered array of hops, each
`{ "kind": "rootRva" | "memberOffset" | "recordOffset", "value": <non-negative int>, "note": <text> }`.
The chain is the module-relative dereference path the resolver walks to the
field (verified against `Type10EntityPositionResolver.TryResolveOnce`/
`FindEntity`); it is documentation + evidence, never a runtime read plan.

Chained fields keep their `offsets` value `0` **by design**: the runtime
observation path computes `moduleBase + field.Offset` (no chain concept) and
the ring record is battle-scoped heap (never publishable), so a non-zero
`offsets` entry would make the legacy observation path read a bogus address.
The resolver reads position through its own hash-bound layout; chained fields
are excluded from observation reads (pinned by
`ChainedFields_AreExcludedFromObservationReads`).

The position family — `playerPositionX/Y/Z` (float32 triple at record
`+0x10/+0x14/+0x18`) — is `Verified` via the module-rooted chain
(`GameCoreRootRva 0x04095C88` = 67722376), published in
`11.19.0.10.json`. `scripts/python/offset_check.py` validates the `chains`
object (chained field offsets must be 0; hops must be non-empty with valid
`kind`/non-negative `value`); absent `chains` is a no-op.

## Validation tooling

- `scripts/python/offset_check.py` — schema compliance: `schemaVersion` = 1,
  placeholder-aware sha256/date rules, filename↔`gameVersion`, all 8 fields present, offset
  plausibility (not too small / > 2 GB), no extra fields, valid confidence,
  `discoveredAtUtc` present. Run with `--check-schema` to also cross-verify
  this page's documented contract against `schema.json` and the validator's
  own constants (CROSS-CHECK issues) plus each version file's keys and
  confidence value (DOC-CHECK issues). Output:
  `.build/offset-check-<timestamp>.log`.

## Hard rules

- Evidence-first: never fabricate offsets; `0` stays unknown until discovered.
- `confidence: "none"` placeholders must be updated before they are treated as real.
- Never commit `scanner-state.json`, scan files, memory dumps, pointer maps,
  absolute paths, or machine-specific paths in notes.
- Cheat Engine / Ghidra are approved for **offline replay sessions only**.
