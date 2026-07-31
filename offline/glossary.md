# Glossary

Domain terms you'll meet in this codebase, defined briefly. Canonical detail
lives in `docs/` (linked where it exists).

| Term | Meaning |
|------|---------|
| **Replay evidence** | A `.wotbreplay` file — the immutable input. Parsed, never executed. |
| **Source artifact** | A stored copy of a replay (content-addressed in SQLite). |
| **Decode run** | One immutable parse of a replay. Reprocessing = a NEW decode run; evidence-first (unknown stays unknown). |
| **Battle session** | The decoded projection of one battle (participants, positions, events). |
| **Projection** | The read model produced from a decode run and served to clients. |
| **Telemetry capture** | NDJSON-formatted telemetry capture logs (see `docs/formats/telemetry-capture-ndjson-v1.md`). |
| **Replay clock** | Segmented replay clock for correlating capture logs to replay time. |
| **Comparison run** | Left/right telemetry comparison (Exact/Tolerant/Mismatch/Missing/Extra). |
| **DVPL** | Wargaming's packed asset format; `DvplReader` unpacks game data (e.g. minimap WebP textures). |
| **Offsets** | Versioned game memory offsets (evidence in `memory-offsets/*.json`, `memory-offsets/schema.json`). |
| **Rendezvous** | The host-written owner-only file the overlay uses to discover the running host. |
| **HUD / Overlay** | The transparent WPF overlay that sits on top of the game during replay playback. |
| **Pickle** | Python pickle inside replays — read as DATA ONLY, opcodes never executed. |
| **Protobuf** | Wire-format used inside replay event streams (`ProtobufWireReader`). |
| **Loopback trust** | Host binds loopback only; the overlay is a loopback web client with no listener. |
| **Mutation capability** | Local capability required to call write endpoints (mutation protection). |
| **Multi-scan engine** | Cheat-Engine-like snapshot/compare memory scanning for offset discovery. |

## Architecture abbreviations

| Term | Meaning |
|------|---------|
| M0–M7 | Architecture hardening milestones (see `docs/architecture/roadmap.md`) |
| BLK-xxxx | Blocker-log entries (`docs/operations/blocker-log.md`) |
| BLK-0002 / 0003 / 0005 / 0006 / 0013 | Frequently-cited blockers: NuGet audit pins, portable TFM rule, case-insensitive .gitignore, validate.ps1 exit codes, CompositionRootTests port list |
