# WotB Treader

WotB Treader is a Windows-first offline replay telemetry reader for World of
Tanks Blitz. It imports replay evidence, preserves unknown records, builds
versioned telemetry projections, and presents a local Blazor dashboard +
WPF/WebView2 overlay with SignalR push-based updates.

The project owner identifies as a junior developer at Wargaming.net. This is
a personal, independently maintained project; see
[Project context](docs/project-context.md).

The project is intentionally local and evidence-first:

- replay parsing and storage run on .NET 10;
- the dashboard is an ASP.NET Core Blazor Web App bound only to loopback;
- the overlay is a transparent WPF HUD with position plot, velocity trails,
  event feed, battle stats, timeline scrubber, and keyboard shortcuts;
- metadata may be resolved read-only from the installed game when its exact
  version is supported;
- bot status and unsupported replay semantics remain `unknown` unless the
  source provides explicit evidence;
- game automation is developer-only, offline-replay-only, denied by default,
  and fully audited.

Python, Node.js, Rust, Electron, containers, cloud services, runtime AI, and
dynamic decoder DLLs are not part of the alpha runtime.

## Quickstart

See [knowledge.md](knowledge.md) for the full quickstart guide including
convenience wrappers, startup sequence, and keyboard shortcuts.

```powershell
# Full gate: restore → format → build → test → audit → scan
./scripts/validate.ps1
```

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

**Current status:** 269 tests (267 passed, 2 skipped), 0 warnings, 0 errors.
12 test projects. See [ROADMAP](docs/ROADMAP.md) for completed and deferred work.

## License and third-party material

Project source is MIT licensed, copyright WotB Treader contributors. Replay
fixtures, Wargaming-derived resources, user data, and separately licensed
third-party material are excluded from that grant; see
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
