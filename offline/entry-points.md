# Entry points

The first files to read for a task, in order. Every file below is committed and
safe to open; none of it is private or runtime data.

## Orient fast (any task)

1. [`knowledge.md`](../knowledge.md) — the single best summary (stack, quickstart, architecture, conventions, gotchas)
2. [`AGENTS.md`](../AGENTS.md) — agent rules, route-by-task table, delegation
3. [`README.md`](../README.md) — human quickstart + startup sequence
4. [`docs/architecture/overview.md`](../docs/architecture/overview.md) — diagram, evidence lifecycle, HUD design intent
5. This pack — `repo-map.md` → `entry-points.md` → `api-surface.md`

## Understand the architecture / boundaries

1. [`docs/architecture/overview.md`](../docs/architecture/overview.md)
2. [`docs/architecture/roadmap.md`](../docs/architecture/roadmap.md)
3. [`tests/WotBTreader.Architecture.Tests/`](../tests/WotBTreader.Architecture.Tests/) — `ProjectReferenceTests`, `DependencyDirectionTests`, `TargetFrameworkTests`, `NativeAccessBoundaryTests`
4. [`src/WotBTreader.Bootstrap/`](../src/WotBTreader.Bootstrap/) — composition root; how hosts actually start

## Work on replay parsing

1. [`src/WotBTreader.Replays/`](../src/WotBTreader.Replays/) — `WotbReplayDecoder.cs`, `RestrictedPickleReader.cs`, `ProtobufWireReader.cs`
2. [`docs/formats/telemetry-capture-ndjson-v1.md`](../docs/formats/telemetry-capture-ndjson-v1.md)
3. [`docs/architecture/overview.md`](../docs/architecture/overview.md) (evidence lifecycle section)
4. Tests: [`tests/WotBTreader.Replays.Tests/`](../tests/WotBTreader.Replays.Tests/)

## Work on the web host / APIs

1. [`src/WotBTreader.Host.Web/Endpoints/ReadApiEndpoints.cs`](../src/WotBTreader.Host.Web/Endpoints/ReadApiEndpoints.cs)
2. [`src/WotBTreader.Host.Web/Endpoints/GameApiEndpoints.cs`](../src/WotBTreader.Host.Web/Endpoints/GameApiEndpoints.cs)
3. [`src/WotBTreader.Host.Web/Infrastructure/`](../src/WotBTreader.Host.Web/Infrastructure/) — loopback + mutation protection middleware
4. [`offline/api-surface.md`](api-surface.md) — the endpoint map
5. Tests: [`tests/WotBTreader.Host.Web.Tests/`](../tests/WotBTreader.Host.Web.Tests/)

## Work on the overlay (HUD)

1. [`src/WotBTreader.Overlay/`](../src/WotBTreader.Overlay/) — `MainWindow.xaml`, `ViewModels/MainViewModel.cs`, `Views/PositionPlot.xaml`
2. `docs/architecture/overview.md` → the **overlay / HUD design intent** section (it is a transparent HUD, not a generic viewer)
3. [`src/WotBTreader.ApiContracts/HudContracts.cs`](../src/WotBTreader.ApiContracts/HudContracts.cs)
4. Tests: [`tests/WotBTreader.Overlay.Tests/`](../tests/WotBTreader.Overlay.Tests/)

## Work on game integration / memory / offset discovery

1. [`src/WotBTreader.GameIntegration/Session/`](../src/WotBTreader.GameIntegration/Session/) — coordinator, memory scan engine, process launcher
2. [`docs/operations/offset-discovery-guide.md`](../docs/operations/offset-discovery-guide.md)
3. [`memory-offsets/`](../memory-offsets/) — evidence JSON + `schema.json`
4. `tools/cheat-engine/`, `tools/ghidra-scripts/` — approved offline tooling

## Add a DI port or service

1. [`tests/WotBTreader.Bootstrap.Tests/CompositionRootTests.cs`](../tests/WotBTreader.Bootstrap.Tests/CompositionRootTests.cs) — new ports MUST be added to the published-port list (BLK-0013)
2. [`src/WotBTreader.Bootstrap/DependencyInjection/FoundationServiceCollectionExtensions.cs`](../src/WotBTreader.Bootstrap/DependencyInjection/FoundationServiceCollectionExtensions.cs)

## Validate / gate a change

1. [`scripts/validate.ps1`](../scripts/validate.ps1) — full gate (restore → format → build → test → scan)
2. [`offline/commands.md`](commands.md) — exact commands + basher timeouts
