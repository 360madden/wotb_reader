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
  "offsets": { "replayTime": 0, "playerHP": 0, "playerPositionX": 0,
               "playerPositionY": 0, "playerPositionZ": 0, "playerYaw": 0,
               "cameraPitch": 0, "aliveTankCount": 0 },
  "confidence": "none",
  "notes": ""
}
```

Required: `schemaVersion`, `gameVersion`, `offsets` (all 8 fields,
`additionalProperties: false`). `0` = unknown.

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
| Declared `executableSha256` ≠ observed exe hash | `offset.hash_mismatch` |

Per-field: `OffsetField(Offset == 0 ? Unknown : Candidate)`, confidence
`None`/`Low`; the file-level `confidence` field maps to the table's overall
`OffsetConfidence`. Domain model: [`src/WotBTreader.Core/OffsetModels.cs`](../src/WotBTreader.Core/OffsetModels.cs)
(`OffsetField`, `OffsetTable`, `OffsetFieldStatus`, `OffsetProvenanceKind`).

The directory is resolved by `Application` DI
(`ApplicationServiceCollectionExtensions`): `AppContext.BaseDirectory` →
`memory-offsets`, falling back to repo-root discovery.

## Runtime gating (`GameSessionCoordinator`)

- `LoadOffsetTable(process)` requires an exact version + executable-hash match.
- `HasKnownOffsets(table)` is false while all fields are `0` — the coordinator
  then **refuses memory reads** (`GET /api/v1/game/memory` returns
  `Unavailable`).
- Live observation pushes only happen after the `OfflineReplayVerified` gate.

## Validation tooling

- `scripts/python/offset_check.py` — schema compliance: `schemaVersion` = 1,
  sha256 format, filename↔`gameVersion`, all 8 fields present, offset
  plausibility (not too small / > 2 GB), no extra fields, valid confidence,
  `discoveredAtUtc` present. Output: `.build/offset-check-<timestamp>.log`.

## Hard rules

- Evidence-first: never fabricate offsets; `0` stays unknown until discovered.
- `confidence: "none"` placeholders must be updated before they are treated as real.
- Never commit `scanner-state.json`, scan files, memory dumps, pointer maps,
  absolute paths, or machine-specific paths in notes.
- Cheat Engine / Ghidra are approved for **offline replay sessions only**.
