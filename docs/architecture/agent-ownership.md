# Agent ownership map

Last updated: 2026-07-26

The lead owns the solution, root build files, `Core`, shared `Application`
contracts, SQL migration ordering, `Bootstrap`, integration, documentation,
validation, staging, and commits.

| Area | Normal owner | Shared-contract rule |
|---|---|---|
| `WotBTreader.Replays` | replay agent | Propose port/domain changes to lead |
| `WotBTreader.CaptureLogs` | telemetry agent | Propose comparison/clock contracts to lead |
| `WotBTreader.GameIntegration` | integration agent | Propose metadata/harness ports to lead |
| `WotBTreader.Storage.Sqlite` | storage agent | Lead reviews schema and migration numbers |
| `WotBTreader.Host.Web` | UI agent | API DTO changes coordinated with lead |
| `WotBTreader.Overlay` | overlay agent | Consumes loopback API only |
| `tools/*` | assigned tool agent | No business logic duplicated from libraries |

Each handoff reports changed files, public contracts, tests, commands run,
assumptions, unknowns, and integration risks. Agents never stage or commit.
