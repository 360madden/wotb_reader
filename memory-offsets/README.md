# Memory Offset Data

Discovered WoT Blitz memory offsets for replay state reading.
Each file maps to one game version.

## Directory structure

```
memory-offsets/
├── README.md           ← this file
├── schema.json         ← JSON schema for validation
├── <version>.json      ← one file per discovered game version (e.g. 11.8.0.7.json)
└── scanner-state.json  ← last scanner state (gitignored, generated at runtime)
```

## Offset file format

```json
{
  "schemaVersion": 1,
  "gameVersion": "11.8.0.7",
  "executableSha256": "abc123...",
  "discoveredAtUtc": "2026-07-28T12:00:00Z",
  "offsets": {
    "replayTime": 0,
    "playerHP": 0,
    "playerPositionX": 0,
    "playerPositionY": 0,
    "playerPositionZ": 0,
    "playerYaw": 0,
    "cameraPitch": 0,
    "aliveTankCount": 0
  },
  "confidence": "none",
  "notes": ""
}
```

## Confidence levels

| Level    | Meaning |
|----------|---------|
| `none`   | No offsets discovered (placeholder) |
| `low`    | Scanner found candidates, unverified |
| `medium` | Scanner found 1-3 candidates, matches game behavior in one battle |
| `high`   | Verified across multiple battles and game restarts |

## How offsets are discovered

1. Start WoT Blitz with a replay active
2. Run the GameHarness scanner: `dotnet run --project tools/src/WotBTreader.GameHarness -- scan int32 <HP value>`
3. Narrow by scanning again with a changed value
4. Repeat for each field (HP=int32, position=float, replay time=double)
5. Update the version file and set confidence to `high` once verified

## Never commit

- `scanner-state.json` — runtime state, added to .gitignore
- Offsets with `confidence: "none"` (placeholders only — update when real data exists)
