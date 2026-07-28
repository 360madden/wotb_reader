# Session handoff — 2026-07-28: Rendezvous ACL fixes + overlay HTTP API design

**Author:** Codex Agent  
**Branch:** `main`  
**Head:** `7d23be9` — 5 commits ahead of `origin/main` (all pushed)  
**Working tree:** Clean (only untracked `.data.bak/` is a backup of the old data dir)  
**Tests:** 253 passed, 0 failed, 2 skipped across all 12 projects  
**Build:** 0 errors, 0 warnings (Release)

---

## What was accomplished this session

### 1. Rendezvous ACL crash (3 commits: `42c659e`, `39f8712`, `e0c1bad`)

When an elevated admin previously ran the CLI, it created `.data\rendezvous\` with admin-only ACLs. Every subsequent standard-user invocation crashed with `UnauthorizedAccessException` during `SetAccessControl`, killing the entire CLI before any command could execute.

**Layer 1 — catch `UnauthorizedAccessException`** (`39f8712`):
Wrapped `directory.SetAccessControl(security)` in try-catch. Prevents the crash but silently accepts existing ACLs.

**Layer 2 — attempt delete+recreate** (`e0c1bad`):
Enhanced the catch block to attempt `directory.Delete(recursive: false)` then `directory.Create(security)`. Works when the standard user has delete-child on the parent `.data\`.

**Remaining problem:** If the directory has orphaned files (from admin session), `Delete(recursive: false)` fails with `IOException`. The admin-owned directory cannot be deleted from a standard-user shell. The user needs to run **once as admin**: `rmdir /s C:\work\wotb_reader\.data\rendezvous`. After that, the code fix takes over.

### 2. `everything.cmd` correction (`7d23be9`)

User reported that `everything.cmd` didn't launch the game/replay — the HUD appeared over an empty desktop. Initial incorrect fix auto-launched "most recent replay" which is wrong (user must choose which replay). Final fix: reverted game-launching code, updated header to honestly document that the script starts web host + overlay, and directs users to the overlay's **"Pick & Launch"** button for specific replay selection.

### 3. Overlay HTTP API — design deep context gathering (no code written)

Spent significant time gathering comprehensive context across the overlay and web host codebases to design a practical automation API for the opaque WPF overlay.

---

## Context gathered (files read & understood)

### Overlay structure
| File | Role |
|------|------|
| `src/WotBTreader.Overlay/WotBTreader.Overlay.csproj` | `net10.0-windows`, WPF app, only 2 packages: SignalR client + WebView2. No ASP.NET Core packages. |
| `src/WotBTreader.Overlay/App.xaml.cs` | Empty partial class — application entry point, ready for startup logic |
| `src/WotBTreader.Overlay/MainWindow.xaml.cs` | Full HUD logic: window tracking (P/Invoke), `QuickLaunchWithPathAsync` orchestration (start host → import → launch game → track), keyboard shortcuts, drag-and-drop |
| `src/WotBTreader.Overlay/ViewModels/MainViewModel.cs` | All observable state: Status, BaseUri, Sessions, SelectedSession, playback (IsPlaying, CurrentTime, Duration, PlaybackSpeed), Points, MapName, stats |
| `src/WotBTreader.Overlay/Services/TreaderApiClient.cs` | Read-only HTTP client to web host, loopback-only enforced, JSON deserialization |
| `src/WotBTreader.Overlay/Discovery/RendezvousLocator.cs` | Finds web host via rendezvous JSON in `%LOCALAPPDATA%\WotBTreader\rendezvous\web.json` |
| `src/WotBTreader.Overlay/Contracts/ReadApiDtos.cs` | All DTOs: SessionPageResponse, SessionDetailResponse, ParticipantResponse, PositionSampleResponse, EventResponse, MapBoundaryResponse |

### Web host patterns (to mirror)
| File | Pattern to reuse |
|------|-----------------|
| `src/WotBTreader.Host.Web/Program.cs` | Kestrel on `IPAddress.Loopback`, port 9182, `AddServerHeader = false` |
| `src/WotBTreader.Host.Web/Endpoints/ReadApiEndpoints.cs` | `MapGroup("/api/v1")`, `Results.Ok()`, `Results.Problem()` with correlation IDs |
| `src/WotBTreader.Host.Web/Infrastructure/LocalMutationSecurity.cs` | 5-minute rotating capability tokens for write endpoints, `FixedTimeEquals` validation |
| `src/WotBTreader.Host.Web/Infrastructure/LoopbackOnlyMiddleware.cs` | Rejects non-loopback remote IPs, validates Host/Origin headers |
| `src/WotBTreader.Host.Web/Infrastructure/MutationProtectionMiddleware.cs` | GET/HEAD/OPTIONS pass through; POST/PUT/DELETE require capability header + antiforgery |
| `src/WotBTreader.Host.Web/Services/RendezvousPublisher.cs` | Background service: creates `.web.json` with instance ID, capability, loopback address |

---

## Overlay HTTP API — design state

### Decision: embedded Kestrel in WPF (not CLI args)

CLI args alone give fire-and-forget (launch → exit code). An HTTP API enables full lifecycle scripting: query state, control playback, verify the HUD is tracking the game window.

### Practical challenge: Kestrel in a WPF project

The overlay targets `net10.0-windows` with `<UseWPF>true</UseWPF>` and the `Microsoft.NET.Sdk`. It does NOT use `Microsoft.NET.Sdk.Web`. Adding Kestrel requires individual ASP.NET Core packages rather than the framework reference:

```
PackageReference Include="Microsoft.AspNetCore.Server.Kestrel"
PackageReference Include="Microsoft.AspNetCore.Routing"
PackageReference Include="Microsoft.AspNetCore.Http"  (implied by WPF SDK?)
```

This is the main unresolved design question: what's the minimal set of packages, and does the WPF SDK already include enough of ASP.NET Core?

### Proposed endpoints (not yet implemented)

```
GET  /api/v1/status
     → { connected, baseUri, sessionsCount, selectedMap, isPlaying,
         currentTime, duration, playbackSpeed, gameWindowFound }

POST /api/v1/sessions/refresh       → triggers RefreshSessionsAsync
POST /api/v1/launch                 → { replayPath } → QuickLaunchWithPathAsync
POST /api/v1/playback/play          → IsPlaying = true
POST /api/v1/playback/pause         → IsPlaying = false
POST /api/v1/playback/seek          → { seconds } → CurrentTime = TimeSpan.FromSeconds
POST /api/v1/playback/speed         → { speed: 0.5|1|2|4|8 }
POST /api/v1/sessions/{id}/select   → SelectedSession = row
```

### Security model decision

The overlay's API is for local automation only. The web host's full `LocalMutationSecurity` + antiforgery stack is **not needed** here — a simple `IPAddress.IsLoopback` check suffices since the overlay holds no replay data, only viewport/playback state. Keep it minimal.

### Architecture fit

The overlay API is one of **three practical candidates** for embedded HTTP APIs in the repo:

| Component | Priority | Why |
|-----------|----------|-----|
| **Overlay** | **High** — this session's focus | Currently unscriptable; no query/control surface. Adding Kestrel enables full automation from `curl`/basher. |
| GameHarness (`tools/`) | Medium | Would let test runners command game launches remotely |
| Directory watcher | Medium | Would make the background import process observable |

Host.Web, Host.Cli, and the class libraries do NOT need embedded HTTP — the web host IS the HTTP server, the CLI is one-shot, and libraries have no process boundary.

---

## Key files modified this session

| File | Change | Commit |
|------|--------|--------|
| `src/WotBTreader.Bootstrap/Configuration/LocalApplicationPaths.cs` | `SetAccessControl` wrapped in try-catch + delete+recreate fallback | `39f8712`, `e0c1bad` |
| `docs/operations/cmd-wrapper-gotchas.md` | Added rendezvous ACL finding | `42c659e` |
| `everything.cmd` | Removed incorrect game-launching, updated header | `7d23be9` |
| `docs/operations/handoffs/2026-07-28-cmd-bug-sweep-acl-fix.md` | First session handoff | `704f40d` |

### No code written for the overlay HTTP API yet — design only.

---

## Unresolved

1. **Admin-owned `.data\rendezvous` directory** still exists on the user's machine. Needs one-shot admin deletion. Until resolved, `RendezvousPublisher` can't write the `.web.json` file and the overlay can't discover the web host.

2. **Kestrel package strategy for WPF** — need to determine minimal ASP.NET Core packages for `net10.0-windows` WPF project. Options:
   - `Microsoft.AspNetCore.Server.Kestrel` + `Microsoft.AspNetCore.Routing` (minimal)
   - Framework reference `<FrameworkReference Include="Microsoft.AspNetCore.App" />` (heavy, may conflict with WPF SDK)
   - Alternative: `HttpListener` (built into Windows, zero new packages, but low-level — no routing, no DI, no JSON helpers)

3. **Port allocation** — the overlay needs a second loopback port (suggested: 9190). Should it be configurable? Should the overlay publish its own rendezvous record for discovery?

4. **Thread safety** — `MainViewModel` properties are mutated on the WPF UI thread via `SynchronizationContext.Post`. Kestrel request handlers run on threadpool threads. All ViewModel access from HTTP handlers must marshal via `SynchronizationContext`.

---

## Recommended resume steps

1. **Fix the rendezvous directory** — delete `.data\rendezvous` as admin, re-run `everything.cmd` to verify the full flow works end-to-end.

2. **Decide Kestrel vs HttpListener** — research what the minimal ASP.NET Core packages are for WPF on .NET 10, and whether `HttpListener` would be a simpler, more robust choice given zero new dependencies.

3. **Implement the overlay HTTP API** — start with `GET /api/v1/status` (read-only, lowest risk). Prove Kestrel starts in the WPF process. Then add `POST /api/v1/launch`. Then playback control.

4. **Add overlay API tests** — once the API exists, `WotBTreader.Overlay.Tests` should cover endpoint behavior with a TestServer.
