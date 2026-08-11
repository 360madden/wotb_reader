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
| `/discover/read` | POST | Re-read a bounded staged set of absolute addresses under the offline gate |
| `/discover/entity-position` | POST | Resolve one decoded replay entity ID through the exact-build, server-owned module root and return a sanitized newest-ring XYZ result |
| `/discover/entity-region` | POST | One bounded region dump (≤ 4 KB) of a decoded entity — bytes + replay-clock label only; anchors: `ring-record` / `entity-tank-record` / `entity-base` (the L1–L4 seam) |
| `/discover/entity-regions` | POST | **Batch** region dumps (≤ 16 entities, ≤ 16 KB total) in one round trip with ONE replay-clock attestation — the per-frame live read surface (design: `docs/operations/batch-entity-read-design.md`); response carries the read-pass measurement window |
| `/discover/position-page` | POST | Resolve the entity address + region page (diagnostic-only; feeds the guarded poll) |
| `/discover/camera-pose` | POST | Gate-verified GameCamera world pose (CAM-001 chain, live-verified 2026-08-11) |
| `/discover/clock-segment` | POST | Append a replay-clock segment (G2 same-decoded-clock anchor) |
| `/discover/instruction-snapshot` | POST | Run the server-pinned instruction-first XYZ capture; caller controls only duration/hit bounds, never PID/module/address/register |
| `/discover/trajectory/{battleSessionId}` | GET | Return decoded ground-truth trajectories for correlation |
| `/discover/correlate` | POST | Score staged value series against decoded trajectory ground truth |

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
