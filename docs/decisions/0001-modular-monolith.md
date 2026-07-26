# ADR 0001: Windows-first modular monolith

- Status: accepted
- Decision date: 2026-07-26

## Context

The alpha must decode offline WotB replays, preserve evidence, compare capture
logs, serve a local dashboard, and host an external overlay. It must remain
simple to install and maintain without Python, Node.js, containers, cloud
services, runtime AI, or a distributed deployment.

## Decision

Use .NET 10 projects with explicit dependency boundaries, ASP.NET Core Blazor
Interactive Server, SQLite through `Microsoft.Data.Sqlite`, and a WPF WebView2
shell. Register decoders explicitly through dependency injection. Keep
business behavior behind versioned application ports so source adapters can be
upgraded independently.

## Consequences

One runtime and one deployment model cover parsing, storage, web, CLI, and
Windows integration. The overlay stays replaceable because it consumes only
the loopback surface. Decoder upgrades remain bounded, but this deliberately
does not promise cross-platform overlay or remote dashboard support in alpha.
