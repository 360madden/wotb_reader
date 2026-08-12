# Entry points

The first files to read for a task, in order. Every file below is committed and
safe to open; none of it is private or runtime data.

## Orient fast (any task)

1. [`knowledge.md`](../knowledge.md) — the single best summary (stack, quickstart, architecture, conventions, gotchas)
2. [`AGENTS.md`](../AGENTS.md) — agent rules, task decision tree, delegation index
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

1. [`ultimate-scanner/`](../ultimate-scanner/) — the standalone memory-scan module (multi-scan engine, pattern/neighborhood scanner, guarded VM reader)
2. [`src/WotBTreader.GameIntegration/Session/`](../src/WotBTreader.GameIntegration/Session/) — coordinator + gate, process launcher, identity
3. [`docs/operations/offset-discovery-guide.md`](../docs/operations/offset-discovery-guide.md)
4. [`memory-offsets/`](../memory-offsets/) — evidence JSON + `schema.json`
5. [`docs/operations/resolver-path-consolidation.md`](../docs/operations/resolver-path-consolidation.md) — the module-rooted chain-resolution plan (publish-as-chains, phase tolerance, freeze legacy; hardware atomicity LAST)
6. [`docs/operations/batch-entity-read-design.md`](../docs/operations/batch-entity-read-design.md) — the batch N-entity read surface (`/discover/entity-regions`) + rehearsal driver `scripts/invoke-batch-rehearsal.ps1`
7. [`docs/operations/live-roster-read-design.md`](../docs/operations/live-roster-read-design.md) — live roster enumeration (`/discover/entity-roster`, X3) + `-EnumerateLive` rehearsal mode
8. [`docs/operations/live-frame-loop-design.md`](../docs/operations/live-frame-loop-design.md) — the per-frame live HUD loop (X4, composition of the approved seams) — **IMPLEMENTED + LIVE-VERIFIED 2026-08-11/12**: mid-battle frames carry the CAM-013 chase camera, L1 HP, exact decoded-name joins (enemy ids included), the own-nameplate marker (`OwnEntityId`), and a real G2 replay clock (endpoint forwards the session id; launcher anchors the clock with a midnight-safe date roll); handoffs `2026-08-11-live-frame-end-to-end.md` / `2026-08-11-cam013-aim-point-convention.md`
8b. [`docs/operations/l3-damage-dealt-avatar-family-plan.md`](../docs/operations/l3-damage-dealt-avatar-family-plan.md) — L3 damage-dealt discovery plan (avatar/player-stats family, increment correlator; runs after the operator-approved publications)
8c. [`docs/operations/item7-hardware-atomicity-proof-plan.md`](../docs/operations/item7-hardware-atomicity-proof-plan.md) — item-7 hardware-atomicity proof plan (LAST by design: Branch A static write-size proof + Branch B double-read extension to the batch surface)
9. `tools/cheat-engine/`, `tools/ghidra-scripts/` — approved offline tooling
10. [`../research/README.md`](../research/README.md) — game internals research (replay loading, IPC, memory analysis)

## Add a DI port or service

1. [`tests/WotBTreader.Bootstrap.Tests/CompositionRootTests.cs`](../tests/WotBTreader.Bootstrap.Tests/CompositionRootTests.cs) — new ports MUST be added to the published-port list (BLK-0013)
2. [`src/WotBTreader.Bootstrap/DependencyInjection/FoundationServiceCollectionExtensions.cs`](../src/WotBTreader.Bootstrap/DependencyInjection/FoundationServiceCollectionExtensions.cs)

## Validate / gate a change

1. [`scripts/validate.ps1`](../scripts/validate.ps1) — full gate (restore → format → build → test → scan)
2. [`offline/commands.md`](commands.md) — exact commands + basher timeouts
