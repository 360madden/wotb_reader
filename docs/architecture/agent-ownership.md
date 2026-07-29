# Agent ownership map

Last updated: 2026-07-29

The lead owns the solution, root build files, `Core`, shared `Application`
contracts, SQL migration ordering, `Bootstrap`, integration, documentation,
validation, staging, and commits.

| Area | Normal owner | Shared-contract rule |
|---|---|---|
| `WotBTreader.Replays` | replay agent | Propose port/domain changes to lead |
| `WotBTreader.CaptureLogs` | telemetry agent | Propose comparison/clock contracts to lead |
| `WotBTreader.GameIntegration` | integration agent | Propose metadata/harness ports to lead |
| `WotBTreader.Storage.Sqlite` | storage agent | Lead reviews schema and migration numbers |
| `WotBTreader.ApiContracts` | lead | Shared wire surface. Every change is a breaking-change review; the assembly must keep zero project and zero package references |
| `WotBTreader.Host.Web` | UI agent | Consumes `ApiContracts`; it does not own the wire shapes. Propose contract changes to lead first |
| `WotBTreader.Host.Cli` | CLI agent | New verbs and output envelopes coordinated with lead |
| `WotBTreader.Overlay` | overlay agent | Consumes loopback API and `ApiContracts` only; no other project reference |
| `tools/*` | assigned tool agent | Resolve product ports through `Bootstrap`; never reference an adapter directly and never duplicate library business logic |

Each handoff reports changed files, public contracts, tests, commands run,
assumptions, unknowns, and integration risks. Agents never stage or commit.
