# API surface

The web host (`WotBTreader.Host.Web`) is the **single control plane**:
loopback-only, Blazor + REST + SignalR at `127.0.0.1:9182`. The overlay is a
loopback client only and hosts no listener; nothing binds port 9190. The former
overlay endpoint/state implementation was deleted.

## Discovery / rendezvous

- Host writes an owner-only rendezvous file; the overlay finds it via
  `RendezvousLocator`. Search for `*.rendezvous.json` in the data root.

## Read API — `GET /api/v1/*` (loopback, read-only)

| Route | Purpose |
|-------|---------|
| `/doctor` | Environment health checks |
| `/sessions` | Paged session list (`offset`, `limit`; default 50, max 200) |
| `/sessions/{battleSessionId}` | Session detail: participants, positions (max 5 000), events (max 2 000), warnings |
| `/maps/boundaries` | Map boundary list |
| `/maps/{mapId}/minimap` | Minimap PNG texture (from installed game's DVPL WebP) |
| `/decode-runs/{decodeRunId}` | Decode run summary |

## Game API — `/api/v1/game/*` (loopback-gated; write endpoints require the mutation capability)

| Route | Method | Purpose |
|-------|--------|---------|
| `/state` | GET | Game/session verification state |
| `/memory` | GET | Memory observation snapshot (HP, position, yaw, camera, alive tanks) |
| `/launch` | POST | Launch a replay (`sourceArtifactId`) |
| `/start` | POST | Launch the installed game process without a replay |
| `/discover` | POST | Single offset scan (field, expected hex value, tolerance mask) |
| `/discover/snapshot` | POST | Create a memory snapshot for multi-scan (optional `maxBytes` retained-byte budget; above the 512 MiB engine ceiling is rejected) |
| `/discover/compare/{sessionId}` | POST | Compare snapshot (changed/unchanged/increased/decreased) |
| `/discover/session/{sessionId}` | DELETE | Discard a scanner session |
| `/discover/neighborhood` | POST | Scan a memory window around a known offset |

Mutation protection: write endpoints require the local mutation capability
(`LocalMutationSecurity` / `MutationProtectionMiddleware`). Everything is
loopback-gated by `LoopbackOnlyMiddleware`.

## SignalR

- Hub: `TelemetryHub` — pushes telemetry/event updates to connected clients
  (overlay `TelemetryStreamService`, dashboard pages).

## Contract shapes

- `src/WotBTreader.ApiContracts/` — `ReadContracts.cs`, `GameContracts.cs`,
  `HudContracts.cs`, `OffsetDiscoveryContracts.cs` (serialization-only; the
  overlay's ONLY project reference).

## Pagination / response caps

- Positions capped at 5 000 per session detail response (`PositionsTruncated` flag)
- Events capped at 2 000 (`EventCount` reports the true total)
