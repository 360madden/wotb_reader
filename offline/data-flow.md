# Telemetry data flow

How decoded telemetry travels from a `.wotbreplay` to the screens and the
comparison engine. Canonical contracts live in
[`src/WotBTreader.Application/Storage/StorageContracts.cs`](../src/WotBTreader.Application/Storage/StorageContracts.cs)
and [`src/WotBTreader.Core/TelemetryModels.cs`](../src/WotBTreader.Core/TelemetryModels.cs).

## One-line flow

```
import → probe → decode → CommitAsync (SQLite) → PublishCommittedAsync (in-memory stream)
                                                            ↓
                    read API (GET /api/v1/*) ← SQLite    SignalR hub ← (push)
                                                            ↓
                     Blazor dashboard  ←─ ISessionQueryRepository
                     Overlay (HUD)     ←─ TreaderApiClient (HTTP) + TelemetryStreamService (SignalR)
                     CaptureLogs       ←─ NdjsonTelemetrySource → TelemetryComparator
```

## 1. Ingestion (Application layer)

`ReplayIngestionService.ImportAsync` / `ReprocessAsync`:

1. `ISourceArtifactStore.ImportAsync` — content-addressed copy (SHA-256); dedupes by hash.
2. `IReplayProbe.ProbeAsync` → `ReplayDecoderRegistry.Select` → a decoder (`WotbReplayDecoder`).
3. `IDecodeRunRepository.StartAsync` — a decode run is **immutable**; reprocessing is a new run.
4. Decode → `ReplayDecodeProjection` (session, participants, positions, events, raw records).
5. `IDecodeRunRepository.CommitAsync` — persists everything atomically; returns `DecodeRunSummary`.
6. **Post-commit publication:** `ITelemetryEventPublisher.PublishCommittedAsync` is
   fire-and-forget (15 s timeout) — a publication failure never fails or rewrites the
   already-durable decode run.

## 2. Streaming (in-memory, host-side)

`SequencedTelemetryEventPublisher` (Application/Streaming):

- Assigns a global monotonic `Sequence` per event; keeps a bounded **history**
  (4 096 by default) so late subscribers can catch up.
- `SubscribeAsync(afterSequence, …)` — emits history > afterSequence, then live
  deltas via a bounded channel per subscriber. If the requested sequence is
  older than history, it emits a **Gap** message — the client must re-fetch a
  snapshot (never guesses missing events).
- Host-side consumers track stream cursors with `StreamSequenceTracker`
  (`Host.Web/Services/StreamSequenceTracker.cs`) — it marks `RequiresSnapshot`
  on any gap so callers re-fetch rather than guess.
- The overlay's `TelemetryStreamService` does **not** track sequences: it
  refreshes the session list on `event`/`snapshot` stream kinds and falls back
  to polling on connection failure (the Overlay references only
  `ApiContracts`, so it cannot use host-side types).

## 3. Serving (Host.Web)

### Read API (HTTP GET)

`ReadApiEndpoints` → `ISessionQueryRepository` (`SqliteSessionQueryRepository`) →
`ReplayDecodeProjection`. Caps: 5 000 positions, 2 000 events, page 50–200.

Blazor pages use `DashboardReadClient` (same repo, same caps) — see
[`api-surface.md`](api-surface.md).

### SignalR

- Hub: `TelemetryHub` (`/api/v1/stream`) — `subscribe` server-streaming method
  (`IAsyncEnumerable<TelemetryStreamEnvelope>`), optional session filter, wraps
  `ITelemetryEventPublisher.SubscribeAsync`.
- `MemoryObservationPublisher` (BackgroundService, 500 ms poll) pushes live
  memory observations (`GameMemoryResponse`) to `Clients.All` as
  `MemoryObservation` when the gate is `OfflineReplayVerified` and values
  changed (dedupe). Only pushes when memory offsets are known.

## 4. Consumers

### Overlay (WPF HUD)

- `TreaderApiClient` — loopback-only HTTP client (GETs without capability,
  mutations carry `X-WotBTreader-Capability`; never logs the token).
- `TelemetryStreamService` — SignalR client at `{baseUri}/api/v1/stream`;
  auto-reconnect; raises `SessionListChanged` (event/snapshot kinds) and
  `MemoryObservationReceived`. Connection failures are silent — the VM polls as
  fallback.
- `MainViewModel` — refreshes the session list from `GET /api/v1/sessions`,
  loads detail, applies time filter → `Points` (PlotPoint), live observation →
  `LivePlayerTrail` (team 9) + live HUD properties. SignalR callbacks are
  marshalled to the UI thread via `SynchronizationContext.Post` (ObservableCollection rule).
- `PositionPlot` — canvas scatter plot; `PlotTransform.Fit` normalizes against
  map boundaries (from `GET /api/v1/maps/boundaries`) or per-session extents;
  minimap PNG from `GET /api/v1/maps/{mapId}/minimap`.

### Blazor dashboard

- Pages: `Home.razor` (session list), `SessionDetail.razor`,
  `Comparisons.razor`, `Diagnostics.razor`. Read via `IDashboardReadClient`;
  SignalR for live refresh.

### CaptureLogs comparison engine

- `NdjsonTelemetrySource` reads NDJSON telemetry captures
  (`docs/formats/telemetry-capture-ndjson-v1.md`); `SegmentedReplayClockSource`
  correlates capture time → replay time via
  `IReplayClockSegmentRepository` (`ReplayClockSegment` rows in SQLite).
- `TelemetryComparator.CompareAsync` — deterministic event matching by
  (event type, entity id / participant identity, timestamp window):
  - Exact (identity + values + zero delta), Tolerant (within window/tolerance),
    Mismatch (field differs), Missing (left only), Extra (right only),
    Uncomparable (no identity/time).
  - Output `TelemetryComparison` (run + summary + items) persisted via
    `IComparisonRunRepository`; surfaced by `compare` CLI and `Comparisons.razor`.

## Storage (SQLite)

- `SqliteStorageContext` / `SqliteStorageInitializer` — schema + migrations.
- Repositories: `SqliteDecodeRunRepository` (decode runs + canonical_events +
  positions + participants), `SqliteSessionQueryRepository` (read models),
  `SqliteComparisonRunRepository`, `SqliteReplayClockSegmentRepository`,
  `ContentAddressedSourceArtifactStore` (blobs by hash).
- Read path: `SqliteDomainReaders.ReadCanonicalEvent` etc. maps rows → Core models.

## Key rules

- Decode runs are immutable; publication is a separate delivery concern.
- Gaps never guess: clients re-fetch a snapshot (`RequiresSnapshot`).
- SignalR callbacks are non-UI threads — ObservableCollection mutations must
  marshal via `SynchronizationContext.Post`.
- Live memory pushes only when the gate is `OfflineReplayVerified` and offsets
  are known (`HasKnownOffsets`).
