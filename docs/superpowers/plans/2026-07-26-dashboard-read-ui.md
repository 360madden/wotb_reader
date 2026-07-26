# Dashboard Read UI Implementation Plan

> **For agentic workers:** Execute inline in this session. Prefer small commits.
> SignalR / mutation-middleware work is **out of scope**.

**Goal:** Replace the Blazor template with sessions list + session detail + diagnostics pages that consume the same wire DTOs as the U8 read API.

**Architecture:** Blazor Server pages call a scoped `IDashboardReadClient` that uses the existing storage/doctor ports and maps through `WotBTreader.Host.Web.Contracts` (same shapes as HTTP). No HttpClient round-trip, no hub, no mutations. Overlay route stays a stub.

**Tech Stack:** .NET 10, Blazor Server, MSTest, existing `ApiContracts` + `ISessionQueryRepository` / `IDecodeRunRepository` / `IDoctorService`.

## Global Constraints

- Loopback-only host; read-only UI (no import/mutate from pages).
- Never display or log account IDs; bot status stays `Unknown` unless evidence says otherwise.
- Do not touch SignalR / `MutationProtectionMiddleware` exemptions.
- No private replay files required.
- Validate with `scripts/validate.ps1` before commit; push to `origin/main` after green commit.
- Model fit: mechanical UI + thin DI client — safe for Grok 4.5 High.

## Explicitly deferred

- Hub negotiate / antiforgery / capability headers.
- Import UI, overlay WebView2, private replay pass.
- CLI identifier JSON converter / NDJSON versioning.

---

### Task 1: Dashboard read client

**Files:**
- Create: `src/WotBTreader.Host.Web/Services/IDashboardReadClient.cs`
- Create: `src/WotBTreader.Host.Web/Services/DashboardReadClient.cs`
- Modify: `src/WotBTreader.Host.Web/Infrastructure/WebSurfaceServiceCollectionExtensions.cs`
- Test: `tests/WotBTreader.Host.Web.Tests/DashboardReadClientTests.cs`

**Interfaces:**
- Consumes: `ISessionQueryRepository`, `IDecodeRunRepository`, `IDoctorService`, `ApiContracts`
- Produces:
  - `Task<SessionPageResponse> ListSessionsAsync(int offset, int limit, CancellationToken)`
  - `Task<SessionDetailResponse?> GetSessionAsync(Guid battleSessionId, CancellationToken)`
  - `Task<DoctorReport> GetDoctorAsync(CancellationToken)`
  - Same paging/position caps as `ReadApiEndpoints` (`DefaultPageSize`, `MaximumPageSize`, `MaximumPositionSamples`)

- [ ] **Step 1:** Add client interface + implementation mirroring endpoint mapping (reuse `DecodeRunResponse.From`, etc.; cap positions).
- [ ] **Step 2:** Register as scoped in `AddWebSurface`.
- [ ] **Step 3:** Unit tests with fakes (empty page, detail mapping, not-found → null, paging rejection optional if client validates).
- [ ] **Step 4:** `dotnet test tests/WotBTreader.Host.Web.Tests`

---

### Task 2: Sessions list + detail + diagnostics pages

**Files:**
- Modify: `src/WotBTreader.Host.Web/Components/Pages/Home.razor` → sessions list at `/`
- Create: `src/WotBTreader.Host.Web/Components/Pages/SessionDetail.razor` (`/sessions/{BattleSessionId:guid}`)
- Create: `src/WotBTreader.Host.Web/Components/Pages/Diagnostics.razor` (`/diagnostics`)
- Create: `src/WotBTreader.Host.Web/Components/Pages/Overlay.razor` (`/overlay` stub)
- Modify: `src/WotBTreader.Host.Web/Components/_Imports.razor` (add Contracts + Services)
- Modify: `src/WotBTreader.Host.Web/Components/Layout/NavMenu.razor` if needed (already labels Sessions)
- Delete or leave unused: `Weather.razor`, `Counter.razor` (remove to avoid template noise)

**Behavior:**
- Home: load first page of sessions; show map/version/status/counts; link to detail; empty state when none.
- Detail: participants table (no account id); position truncation notice; decode-run status/failure; back link.
- Diagnostics: render doctor report fields already returned by `IDoctorService`.
- Overlay: short “not implemented” message only.

- [ ] **Step 1:** Implement pages with `@inject IDashboardReadClient` and `@rendermode InteractiveServer` as needed.
- [ ] **Step 2:** Build web project; fix analyzer issues.
- [ ] **Step 3:** Optional smoke: `dotnet run` + hit `/` (empty list OK).

---

### Task 3: Handoff, validate, commit, push

**Files:**
- Modify: `docs/operations/handoffs/2026-07-26-validated-integration-milestone.md` (U9 amendment; mark step 3 partially/done for read UI only; note hub still deferred)
- Keep this plan file in the commit if useful

- [ ] **Step 1:** `scripts/validate.ps1`
- [ ] **Step 2:** Commit as Codex Agent
- [ ] **Step 3:** `git push origin main` (no force)

---

## Success criteria

- Nav Sessions / Diagnostics / Overlay routes resolve (no 404 for declared links).
- Empty DB shows an empty sessions state, not an exception.
- Session detail uses U8 DTO field names/semantics.
- No SignalR client code introduced.
- Validation green; pushed to GitHub.
