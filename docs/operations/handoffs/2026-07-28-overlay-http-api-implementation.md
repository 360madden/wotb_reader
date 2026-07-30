# Session handoff — 2026-07-28: Overlay HTTP API implementation

**Author:** Codex Agent  
**Branch:** `main`  
**Working tree:** 4 modified, 5 new files (all unstaged)  
**Tests:** 269 passed, 0 failed, 2 skipped across all 12 projects  
**Build:** 0 errors, 0 warnings (Release)

---

## What was accomplished this session

### Overlay HTTP API — from design to implementation

Implemented the embedded Kestrel HTTP API in the WPF overlay, as designed in the
previous handoff (`2026-07-28-overlay-http-api-design.md`).

### Architecture decision

**FrameworkReference over HttpListener.** Added `<FrameworkReference Include="Microsoft.AspNetCore.App" />`
to the WPF project. This brings in Kestrel, routing, DI, and `WebApplication.CreateBuilder`
with **zero new NuGet packages**. HttpListener was rejected because it lacks routing,
middleware, and JSON helpers, requiring more hand-written infrastructure.

### New files (5)

| File | Purpose |
|------|---------|
| `Overlay/Contracts/OverlayApiDtos.cs` | `OverlayStatusResponse`, `LaunchRequest/Response`, `SeekRequest`, `SpeedRequest`, `SelectSessionRequest` |
| `Overlay/Endpoints/OverlayApiEndpoints.cs` | 8 endpoints under `/api/v1`: `GET /status`, `POST /sessions/refresh`, `/launch`, `/playback/{play,pause,seek,speed}`, `/sessions/select` |
| `Overlay/Services/OverlayApiState.cs` | Thread-safe singleton bridge. All ViewModel mutations marshaled via `SynchronizationContext.Post`. Process-wide singleton — pragmatic for single-window desktop app. |
| `Overlay.Tests/OverlayApiEndpointsTests.cs` | 16 tests: all endpoints + non-loopback rejection for every write endpoint |
| `Overlay.Tests/OverlayApiStateTests.cs` | 12 tests: GetStatus, PostPlay, PostPause, PostSeek, PostSetSpeed, PostRefresh, PostSelectSession, PostLaunch, Register replacement, state reflection |

### Modified files (4)

| File | Change |
|------|--------|
| `WotBTreader.Overlay.csproj` | Added `<FrameworkReference Include="Microsoft.AspNetCore.App" />` |
| `App.xaml.cs` | `OnStartup`: registers ViewModel with OverlayApiState, starts Kestrel on port **9190** with `ContinueWith(OnlyOnFaulted)` error logging. `OnExit`: stops + disposes web host. |
| `MainWindow.xaml.cs` | Added `ViewModel` property, `IsTrackingGameWindow` property, `QuickLaunchWithPathViaApiAsync` public method |
| `WotBTreader.Overlay.Tests.csproj` | Added `<FrameworkReference Include="Microsoft.AspNetCore.App" />` |

### Code review findings — all addressed

| Finding | Resolution |
|---------|-----------|
| Duplicate P/Invoke `FindWindowW` | Replaced with `MainWindow.IsTrackingGameWindow` property |
| Kestrel fire-and-forget with no error handling | Added `ContinueWith(OnlyOnFaulted)` + `Debug.WriteLine` |
| Redundant `IPAddress.IPv6Loopback` check | Removed — `IPAddress.IsLoopback()` already covers IPv6 |
| Test ordering fragility with static singleton | All Register()-calling tests now self-contained. One order-dependent test acknowledged in class-level comment. |

### API endpoints

```
GET  /api/v1/status              → { connected, baseUri, sessionsCount, selectedMap,
                                     isPlaying, currentTimeSeconds, durationSeconds,
                                     playbackSpeed, gameWindowFound, status }

POST /api/v1/sessions/refresh    → dispatches RefreshSessionsAsync
POST /api/v1/launch              → { replayPath } → dispatches QuickLaunchWithPathAsync
POST /api/v1/playback/play       → ensures playing (idempotent)
POST /api/v1/playback/pause      → ensures paused (idempotent)
POST /api/v1/playback/seek       → { seconds } → sets CurrentTimeSeconds
POST /api/v1/playback/speed      → { speed: 0.5|1|2|4|8 }
POST /api/v1/sessions/select     → { battleSessionId } → selects session
```

All endpoints check `IPAddress.IsLoopback(context.Connection.RemoteIpAddress)`.
Kestrel listens on `IPAddress.Loopback:9190` with `AddServerHeader = false`.

### Security model

Simple `IPAddress.IsLoopback` check — no capability tokens, no antiforgery. The
overlay holds no replay data, only viewport/playback state. Keep it minimal.

---

## Unresolved

1. **Admin-owned `.data\rendezvous` directory** still exists on this machine.
   Attempted `rm -rf` — permission denied. Needs one-shot `rmdir /s` as admin.
   The code fix (`delete+recreate` in `LocalApplicationPaths`) handles it going
   forward once the directory is removed.

2. **PostPlay idempotency is untested** with a real non-zero Duration. The
   existing tests have Duration=0 (no session loaded), so the toggle is a no-op.
   A future test with a mock ViewModel or a synthetic session could cover this.

3. **E2E verification** — the overlay API has not been smoke-tested with a
   running overlay process. Build the solution, launch, then:
   ```
   curl http://127.0.0.1:9190/api/v1/status
   ```

---

## Recommended resume steps

1. Delete `.data\rendezvous` as admin, run `everything.cmd` to verify full flow
2. Smoke-test the overlay API with `curl http://127.0.0.1:9190/api/v1/status`
3. Consider adding a `POST /api/v1/game/stop` endpoint to close the game
4. Add E2E integration test with a real WebApplication host (requires `Microsoft.AspNetCore.TestHost` package)

---

## Amendment — superseded by M0 single-control-plane (`2026-07-29T21:00:00Z`)

The embedded Kestrel listener on port 9190 was removed in Milestone 0 (commit `94e349b`/`47d3945`). Host.Web is the single loopback control plane. `OverlayApiEndpoints.cs` and `OverlayApiState.cs` remain as unreachable dead code and are deleted in Milestone 3 — do not extend them.

- Resume step 1 (`.data\rendezvous` admin cleanup) remains live.
- Resume steps 2–4 are superseded (port 9190 is no longer bound).
- The overlay now receives automation commands via the authenticated SignalR channel from Host.Web.
