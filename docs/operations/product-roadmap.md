# Product roadmap — offset discovery → replay overlay → live overlay

**Date:** 2026-08-10
**Owner:** product track (this doc lives in `docs/operations/`; see
`offset-discovery-roadmap.md` for the legacy research-track detail this
supersedes as the product plan).

## Purpose

Unify two ambitions into one dependency-ordered plan:

1. **Discover the game's memory fields** (position ✓, facing/yaw, HP,
   damage-dealt, replayTime, velocity, pitch/roll, camera) — each discovery
   lowering the cost of the next.
2. **Deliver an overlay** that renders nameplates, HP bars, beacons, and
   points of interest **on top of the replay window first** (data-driven,
   offline-only), with a **live-game overlay later** behind the same
   contract (policy-gated; see Phase 5).

The plan is written to be **agentic-friendly**: parallel workstreams own
disjoint files, share small seams, and compound discoveries. Several agents
can work at once without conflict; the integration point is the gate.

## Principles

1. **Discoveries compound.** Each milestone either produces data the next
   milestone consumes, or a seam the next milestone fills. Nothing is built
   that a later step rebuilds.
2. **Seams over rewrites.** The replay overlay and the future live overlay
   share one `OverlayFrame` contract and one projection module. The offset
   table feeds both discovery validation and the overlay.
3. **Offline first, live later.** Every field is proven on replays before
   any live read; the live overlay is a deliberate, policy-gated step
   (ADR-0002 relaxation), not an accident.
4. **One gate, many lanes.** `validate.ps1` is the integration point.
   Workstreams are file-disjoint; only the seams and the live process are
   serialized.

## The compounding map (dependency graph)

| Step | Produces | Consumed by |
|---|---|---|
| Packet rotation discovery ✅ (2026-08-10) | `position_samples.yaw/pitch/roll` ground truth | Facing memory discovery, camera (viewpoint), overlay nameplate direction |
| Position X/Y/Z published ✅ | Walkable ring-record chain, per-entity resolver | All region dumps, overlay projection anchor, live batch reads |
| HP + damage-dealt correlation core ✅ | Rehearsed record-diffing playbook (both replays HIT `+0x48`) | HP/damage-dealt live sessions |
| **Region-read seam** (Phase 2) | ONE bounded `EntityRecordRegionReadRequest` | HP, facing, damage-dealt, velocity — all dump the same seam |
| Facing memory offset (Phase 2/3) | Ring-record rotation offset | Overlay facing arrows, live overlay per-tank yaw, camera model |
| Velocity semantics (Phase 1) | Replay finite-difference velocity → validates memory `+0x28` | Overlay speed labels, `+0x28` promotion |
| `OverlayFrame` contract + `ReplayFrameSource` (Phase 0/1) | DB → per-replay-time frames | Nameplates, beacons, capture-zone markers, LIVE overlay later |
| `WorldToScreen` projection (Phase 1) | Camera (viewpoint pos+yaw+pitch) + FOV → screen | Every 3D marker/nameplate; reused verbatim by live |
| Capture-zone decode (Phase 1) | Objective points from battle_results | Beacon layer, objective markers |
| Camera offset track (Phase 5) | The LIVE game's camera (not the viewpoint tank) | Live overlay projection |

## Workstream registry (agent-friendly ownership)

Each workstream owns specific paths. **An agent never edits another
workstream's owned files.** The registry is the source of truth for who may
touch what.

| WS | Workstream | Owned paths | Entry gate | Handoff file |
|---|---|---|---|---|
| A | Correlation core | `src/WotBTreader.Core/Discovery/*`, `tests/WotBTreader.Core.Tests/*` | No migration, no live | `handoffs/YYYY-MM-DD-ws-a-*.md` |
| B | Decode + storage | `src/WotBTreader.Replays/*`, `src/WotBTreader.Storage.Sqlite/*` (**migration lock**, see below), `tests/WotBTreader.Replays.Tests/*`, `tests/WotBTreader.Storage.Sqlite.Tests/*`, `tests/WotBTreader.TestSupport/*` | Migration changes serialized | `handoffs/YYYY-MM-DD-ws-b-*.md` |
| C | Live seam (gated product addition) | `EntityRecordRegionReadRequest/Result`, coordinator, guarded reader, Host.Web endpoint | One owner; no live session without this | `handoffs/YYYY-MM-DD-ws-c-*.md` |
| D | Live sessions | `scripts/invoke-*-session.ps1`, session evidence | **Serialized** (one game process) | `handoffs/YYYY-MM-DD-ws-d-*.md` |
| E | Python tooling | `scripts/python/*.py`, `scripts/offline_check.py` | No C# changes | `handoffs/YYYY-MM-DD-ws-e-*.md` |
| F | Overlay | `src/WotBTreader.Overlay/*`, `src/WotBTreader.Core/Overlay/*` (projection, frame contract), `tests/WotBTreader.Overlay.Tests/*` | No Discovery/ changes | `handoffs/YYYY-MM-DD-ws-f-*.md` |
| G | Docs/plans | roadmap, plan docs, **this file**, handoff README | Append-only handoffs | `handoffs/YYYY-MM-DD-ws-g-*.md` |
| H | Publication/operator gates | `memory-offsets/*.json`, `docs/operations/g0-*.md` (**offset-table lock**) | Gate first, one owner | ledger entries, `g0-*` checklists |

### Locks (the only serialized things)

1. **Migration lock (WS-B):** `SqliteMigrations.cs` — append-only; one agent
   at a time; the next version number is the lock. An agent picks it up only
   when no migration is in flight, and announces the version it takes.
2. **Offset-table lock (WS-H):** `memory-offsets/*.json` — one operator gate
   at a time (G0 checklist first).
3. **Live-session lock (WS-D):** the game process is single; sessions are
   strictly serial. No two agents run live sessions concurrently.
4. **Seam ownership:** `OverlayFrame` (WS-F) and
   `EntityRecordRegionReadRequest` (WS-C) are defined once; consumers may
   propose changes, WS-F/WS-C own them.

### Agent operating rules (pick-up to merge)

1. **Pick a step** from the phase plan below; check the registry for the
   owning WS and the locks.
2. **Read the latest handoff** for that WS (newest dated file) before
   starting — the session ritual.
3. **Do the work in owned files only.** Update the WS's plan/evidence docs
   if the step needs them.
4. **Run `validate.ps1`** before committing; if files were added, run
   `python scripts/python/offline_check.py --refresh` and commit the tree.
5. **Write a dated handoff** (`YYYY-MM-DD-ws-X-<topic>.md`), append-only,
   with the standard fields (repo state, changes, validation, unknowns,
   next steps).
6. **Commit + push.** The gate is green or the commit is explicitly
   docs-only with the gate failure unrelated.

## Phase plan

### Phase 0 — Parallel offline foundations (now)

| Step | WS | Deliverable | Unlocks |
|---|---|---|---|
| F1 | A | ✅ Wrap-aware float32 `HeadingCorrelator` + facing rehearsal on both replays (packet-yaw ground truth) | Facing live session |
| F2 | F | ✅ `OverlayFrame`/`TankState` contract + `ReplayFrameSource` (DB → frames at replay time t, HP step function) | Nameplates, beacons, live swap-in |
| F3 | E | ✅ Velocity series from replay (finite difference) + pitch/roll validation script | `+0x28` semantics, pitch/roll tracks |
| F4 | G | ✅ This roadmap + workstream registry | All parallel work |
| F5 | F | ✅ **Per-session projection cache** (2026-08-10): `IProjectionCache` (bounded, LRU-ish, capacity 4) sits at the `ReplayFrameSource` projection boundary so the ~580k position/event/raw records load from SQLite once per session instead of on every frame request. Warm frame latency drops ~250 ms → ~10 ms (measured on Oasis Palms at the `/sessions/{id}/frame` endpoint); sessions are immutable post-decode so the cache cannot go stale. ✅ **Playback-speed rehearsal** (2026-08-10): full 252 s battle at the HUD's real 20 fps tick — 5040/5040 frames resolved, 0 failures, max latency 39 ms, 0 frames over the 50 ms tick budget. ✅ **Cache warming** (2026-08-10): the ingestion service warms the cache on decode (in-process invariant) and `ProjectionCacheWarmer` warms the most recent session at web-host startup (the CLI decodes in a separate process, so the host must warm itself) — first frame for the warmed session: 33 ms instead of ~370 ms. ✅ **Headless overlay consistency check** (2026-08-10): `scripts/python/overlay-consistency-check.py` walks both replays at 1 s steps against a live host and validates the full frame contract — finite camera/tank fields, no tank resurrection, minimap normalization within the map boundary, pip positions, and the append-only kill-log invariant (8 kills Oasis, 7 Dead Rail, stable on every later frame). PASS on both replays; the first run's false-fail proved the checker detects real violations | Playback-speed HUD ✅ |

Phase 0 is complete (2026-08-10): the facing correlator rehearses to `+0x2C` on
both replays (27/27 Oasis, 35/35 Dead Rail, flatness 1.0 — with the L2 driver's
real `--yaw-dump` schedule), the overlay frame contract and
`ReplayFrameSource` are tested, and the velocity/pitch/roll tool validates
pitch = −slope (155/155 and 113/113 windows) with the velocity series freed
of the sub-50ms duplicate-packet artifact.

### Phase 1 — Replay overlay offline (parallel with Phase 2 prep)

| Step | WS | Deliverable |
|---|---|---|
| O1 | F | ✅ `WorldToScreen` projection module (view matrix from pos+yaw+pitch, perspective from FOV) + tests; `overlay-frame` CLI preview (frame at replay time → screen pixels) |
| O2 | F | ✅ Nameplate layer: every tank's name, team color, HP bar, distance — clock-anchored over the game window (`W2sHudView` + `/sessions/{id}/frame` endpoint; runs while the web host serves the replay) |
| O3 | F | ✅ Beacon/POI model (world coords + label + color + replay-time tag) + placement + persistence: `beacons` table (migration 6), `IBeaconStore`, `beacon add/list/remove` CLI, projected in `/sessions/{id}/frame` + `overlay-frame` + HUD pins; FOV slider added to the toolbar |
| O4 | B | ✅ **Reframed with evidence**: capture-zone geometry does NOT exist in any replay file — full walk of `battle_results.dat` + packet types 31/35/39 (2026-08-10) proves zones are map-static game data. Delivered: complete battle_results top-level structure table, type-31/35/39 structure evidence, team-record (302/303) negative semantics, and the type-39 camera/attention-point finding as the live camera-track candidate (`offline/replay-format.md`). Objective markers therefore ride the O3 beacon layer + future map-static data |
| O5 | E | ✅ `--heading-delta` extractor mode (movement-gated, wrap-aware): motion-heading + packet-yaw per-window deltas, seam-crossing count (5 on Dead Rail, 0 on Oasis), recommended live pilot target; `pick_yaw_session` selects the yaw-bearing decode |

**Discovery sidecar (parallel, offline):** the parallel-workstreams runbook
(`docs/operations/parallel-workstreams.md`) + `scripts/workstream-lock.py`
serialize the Ghidra project DB, docs, and the live queue; two targeted Ghidra
passes landed. Pass 1 decompiled the FRESH43 write site `FUN_00bc3940` and
pinned a per-frame transform object with **position `+0x38`, 3×3 rotation
`+0x44..+0x5c`, and a 16-float 4×4 matrix at `+0x60`**. Pass 2 decompiled
`FUN_00729570` (RVA `0x329570`): it is the engine's **generic 4×4 matrix
multiply** (20+ call sites; second operand read column-major), so the `+0x60`
matrix is a per-frame composited world/view-style matrix — the live camera/VP
track's static anchor. Evidence: `tools/ghidra-scripts/writesite-ring-disasm.txt`
+ `writesite-matrix-helper-disasm.txt`.

### Phase 2 — The live seam + first live sessions (serialized)

| Step | WS | Deliverable | Session cap |
|---|---|---|---|
| L0 | C | ✅ `EntityRecordRegionReadRequest/Result` (≤ 4 KB region, bytes + replay time only, `OfflineReplayVerified` + current auth) — the ONE product addition; shipped 2026-08-10 with the guarded region read + replay-clock label + `RegionAnchor` (ring-record / entity-tank-record — the L1 wiring correction; coordinator derefs `[entity+0x3C]` itself), 8 coordinator tests + 4 web endpoint tests | — |
| L1 | D | HP live session (Oasis Palms victim 3760578; dump the `[entity+0x3C]` transform-object region and correlate — the `+0x48` from rehearsals is a SYNTHETIC FIXTURE, not a verified location) | 1 |
| L2 | D | Facing live session (ring-record dump vs `position_samples.yaw`; probe `+0x2C..+0x37` first) | 1 |
| L3 | D | Damage-dealt live session (viewpoint counter; `invoke-hp-diffing-session.ps1 -Track damage-dealt` wired + two-replay driver rehearsal HIT `+0x48` 5/5 both) | 1 |
| L4 | D | replayTime session (OD-044 interceptor, byte-exact Double — fixed; 2026-08-10 the clock's static chain verified — `GameCore 0x04095c88 → … → [BWServerConnection+0x58]+0x90` Double — so the session can chain-resolve via L0; 2026-08-11 exhaustive write-site scan returned the copy-path negative, so `-ArmSourceOnFirstHit` is load-bearing and the first hit is expected to be a CRT copy site) | 1 |

Each session reuses L0; the region dump is multipurpose (one dump yields
position + velocity + rotation + HP candidates), so later sessions are
cheaper than the first. NOTE (2026-08-10 cross-check): `[entity+0x3C]` is
static evidence for the TRANSFORM OBJECT (getter `FUN_00d29ea0 = return
[ECX+0x3C]`; position `+0x1C/20/24`, world matrix `+0x60..0x9C`, rotation
`+0x38..0x5C` per FRESH43). HP's actual location is UNKNOWN — the
record-diffing playbook scans the dumped region for whichever int32 drops
with damage; `+0x48` was the test fixture's planted offset. If the
transform region contains no HP-like field, the live session returns an
honest no-hit and the anchor widens (entity base / ring record).

### Phase 3 — Publications (serial, operator-gated)

| Step | WS | Deliverable |
|---|---|---|
| P1 | H | Velocity `+0x28` promotion (live-verified semantics) |
| P2 | H | HP publication (2-replay live agreement at the correlator-found offset — `+0x48` was the synthetic fixture) |
| P3 | H | Facing/yaw publication (replaces the quarantined candidate; reconciled against packet ground truth) |
| P4 | H | Pitch/roll publication (if the ring record holds them) |

### Phase 4 — Overlay 2.0 (data-rich replay overlay)

| Step | WS | Deliverable |
|---|---|---|
| V1 | F | ✅ Facing arrows on nameplates (2026-08-10, replay mode: packet yaw ground truth — `OverlayTankState.YawRadians` was already in the frame; now threaded through projection → API → HUD as `ScreenHeadingDegrees`, perspective-correct two-point probe in `WorldToScreen`, arrow drawn above each nameplate; live mode reuses the same seam once the yaw offset is discovered) |
| V2 | F | Objective markers (capture zones) ride the O3 beacon layer; event-feed tie-ins ✅ (2026-08-10): transient damage pips + death markers from the decoded event stream, windowed 2 s, projected over the affected tank's nameplate (`OverlayEventPip` → `Pips` in frame/API/HUD). ✅ **Kill-feed ticker** (2026-08-10): persistent kill list from the Destroyed events with killer attribution (attacker of the victim's last damage within the 3 s posthumous window), bottom-left HUD panel (`OverlayKill` → `Kills` in frame/API/HUD) — verified on Oasis Palms: 8 kills with real player names. ✅ **Playback progress bar** (2026-08-10): a thin bottom-centre overlay bar fills with `CurrentTimeSeconds / Duration` and shows a `m:ss / m:ss` time label (`BuildPlaybackBar`, pure `PlaybackFillWidth`/`FormatPlaybackLabel` helpers) — the replay position is now visible on the game overlay itself, complementing the session panel's existing scrubber. ✅ **Scoreboard panel** (2026-08-10): every roster tank's cumulative damage dealt + kills at the frame time, top-right overlay panel sorted by damage (`OverlayTankState.DamageDealt/Kills` — one damage-dealt pass in `BuildFrame`, kills counted from the same attribution as the kill feed — → `ProjectedTank` → API → `ScoreboardItems` → `BuildScoreboard`). ✅ **Damage taken + nameplate totals** (2026-08-10): `DamageTaken` (already computed for `hpFraction`, now reused) threads to a scoreboard column, and each nameplate shows a compact "1,200 dmg · 2 kills" totals line under the HP bar. Verified on Oasis Palms t=250: 14 rows, kills sum (8) == kill feed, dead tanks keep final totals, and **total dealt (22094) == total taken (22094)** — every damage event balanced |
| V3 | F | ✅ **Reframed with evidence** (2026-08-10): the full 11.19.0 packet-type inventory (all 20 types) carries **no spotting/reveal packet** — spotted-reproduction is not data-possible from replays, so replay mode renders god-view (the default already in the HUD). The live spotting model is an X5 policy-gated deliverable, not a replay one. ✅ **Destroy signal found** (2026-08-10): the type-10 destroy marker (per-entity constant zeroed + flags cleared) emits `Destroyed` events — the HUD's `Alive` flags and death pips now run on real replays (`offline/replay-format.md`) |
| V4 | F | ✅ **God-view replay minimap** (2026-08-10): world X/Z now ride through the frame API (`ProjectedTank.WorldX/WorldZ` → `OverlayTankResponse` → CLI), the view model normalizes every roster tank against the session's map boundary (`MinimapMath.Normalize` → `MinimapItems`), and the HUD draws a bottom-right panel — team-colored dots, grey wrecks, white camera ring (`W2sHudView.BuildMinimap`). Pure god-view: tanks behind the camera still appear. ✅ **Beacons on the minimap** (2026-08-10): `ProjectedBeacon.WorldX/WorldZ` → `OverlayBeaconResponse` → `MinimapBeacons` normalized against the same boundary and drawn as colored diamonds under the tank dots. ✅ **Map texture under the dots** (2026-08-10): the minimap image loaded via `LoadMinimapAsync` now renders as the panel background (`MinimapImageSource` → `Render` → `BuildMinimap`), stretched to the panel square so normalized dots align with terrain; dot alignment is the invariant (a non-square boundary distorts texture, not dots). Fails closed when no texture is installed. ✅ **Camera facing tick** (2026-08-10): `CameraYawRadians` flows to the view model (`MinimapCameraYawRadians`) and the HUD draws a small white triangle from the camera ring toward the viewpoint's facing (packet yaw convention 0=+Z, +π/2=+X mapped to panel pixels; `CameraTickApex` pure helper). Verified on Oasis Palms: 14 tanks normalized against map-11 boundary; a center POI beacon normalizes to (0.56, 0.57); camera yaw 0.564 rad at t=100 renders a right-down tick |

### Phase 5 — Live overlay (policy-gated, later)

| Step | WS | Deliverable | Gate |
|---|---|---|---|
| X1 | G | Policy memo: ADR-0002 relaxation decision, ToS risk, scope | Explicit user approval |
| X2 | C | Batch N-entity read surface (positions + yaw + HP per frame; walker already resolves any entity id) | X1 |
| X3 | C/D | Camera offset track (the LIVE game's camera — a new discovery target) | X1 |
| X4 | F | `LiveFrameSource` behind the same `OverlayFrame` contract — **no overlay rewrite** | X2 + X3 |
| X5 | F | Spotting model (only spotted tanks rendered; wall-hack god-view explicitly out) | X1 |

## Cross-links

- Legacy research detail: `docs/operations/offset-discovery-roadmap.md`
- Record-diffing playbook + live plan: `docs/operations/record-diffing-groundwork.md`
- replayTime plan: `docs/operations/replaytime-live-attempt-plan.md`
- Publication workflow: `docs/operations/offset-discovery-workflow.md` Phase 5,
  `docs/operations/g0-operator-checklist.md`
- Overlay (current 2D surface): `src/WotBTreader.Overlay/`
- Packet rotation discovery: `offline/replay-format.md`, handoff
  `2026-08-10-facing-yaw-packet-discovery.md`
