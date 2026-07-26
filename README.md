# WotB Treader

WotB Treader is a Windows-first offline replay telemetry reader for World of
Tanks Blitz. It imports replay evidence, preserves unknown records, builds
versioned telemetry projections, and presents a local dashboard and optional
external minimap overlay.

The project is intentionally local and evidence-first:

- replay parsing and storage run on .NET 10;
- the dashboard is an ASP.NET Core Blazor Web App bound only to loopback;
- the external overlay is WPF with WebView2;
- metadata may be resolved read-only from the installed game when its exact
  version is supported;
- bot status and unsupported replay semantics remain `unknown` unless the
  source provides explicit evidence;
- game automation is developer-only, offline-replay-only, denied by default,
  and fully audited.

Python, Node.js, Rust, Electron, containers, cloud services, runtime AI, and
dynamic decoder DLLs are not part of the alpha runtime.

## Development

Requirements:

- Windows 10/11
- .NET SDK 10.0.302
- Microsoft Edge WebView2 Runtime for the external overlay

```powershell
dotnet restore WotBTreader.sln --locked-mode
dotnet build WotBTreader.sln -c Release --no-restore
dotnet test WotBTreader.sln -c Release --no-build
```

Run all repository checks with:

```powershell
./scripts/validate.ps1
```

Host and tool commands will be documented as each executable slice becomes
available. See [architecture](docs/architecture/overview.md), the
[blocker log](docs/operations/blocker-log.md), and the
[fixture policy](docs/testing/fixture-policy.md) for durable project rules.

## License and third-party material

Project source is MIT licensed, copyright WotB Treader contributors. Replay
fixtures, Wargaming-derived resources, user data, and separately licensed
third-party material are excluded from that grant; see
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
