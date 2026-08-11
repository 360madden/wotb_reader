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

## Strategy (2026-08-11)

**Replays are the live-mode test harness.** The memory track exists for the
future live-game overlay (Phase 5, policy-gated); replays are the reliable
rehearsal ground (deterministic decoded ground truth, repeatable launches, a
real game process, no policy risk). **The resolver path (module-rooted chain
resolution) is the canonical read surface** — position is published and
walkable; the legacy offset-table observation surface is frozen (offsets 0,
chained fields excluded, emits nulls) and never extended. New discoveries
publish as chains, not offsets. **Hardware-atomicity proof is ordered LAST**
in the consolidation checklist. Full plan:
`docs/operations/resolver-path-consolidation.md`.

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

Phase 0 is complete (2026-08-10): the facing correlator rehearsed to `+0x2C` on
both replays (27/27 Oasis, 35/35 Dead Rail, flatness 1.0 — with the L2 driver's
real `--yaw-dump` schedule), the overlay frame contract and
`ReplayFrameSource` are tested, and the velocity/pitch/roll tool validates
pitch = −slope (155/155 and 113/113 windows) with the velocity series freed
of the sub-50ms duplicate-packet artifact. **2026-08-11 (OD-RECOVERY-088) the
live L2 session corrected the rehearsal's prediction: the ring tail is a
rotation triple — roll `+0x28`, pitch `+0x2C`, yaw `+0x30`** (the rehearsal
placed yaw at +0x2C by construction, so it proved the correlator, not the
layout).

### Phase 1 — Replay overlay offline (parallel with Phase 2 prep)

| Step | WS | Deliverable |
|---|---|---|
| O1 | F | ✅ `WorldToScreen` projection module (view matrix from pos+yaw+pitch, perspective from FOV) + tests; `overlay-frame` CLI preview (frame at replay time → screen pixels; `--png` schematic render + minimap inset, and `overlay-strip` contact sheet, 2026-08-11) |
| O2 | F | ✅ Nameplate layer: every tank's name, team color, HP bar, distance — clock-anchored over the game window (`W2sHudView` + `/sessions/{id}/frame` endpoint; runs while the web host serves the replay) |
| O3 | F | ✅ Beacon/POI model (world coords + label + color + replay-time tag) + placement + persistence: `beacons` table (migration 6), `IBeaconStore`, `beacon add/list/remove` CLI, projected in `/sessions/{id}/frame` + `overlay-frame` + HUD pins; FOV slider added to the toolbar |
| O4 | B | ✅ **Reframed with evidence**: capture-zone geometry does NOT exist in any replay file — full walk of `battle_results.dat` + packet types 31/35/39 (2026-08-10) proves zones are map-static game data. Delivered: complete battle_results top-level structure table, type-31/35/39 structure evidence, team-record (302/303) negative semantics, and the type-39 camera/attention-point finding as the live camera-track candidate (`offline/replay-format.md`). Objective markers therefore ride the O3 beacon layer + future map-static data |
| O5 | E | ✅ `--heading-delta` extractor mode (movement-gated, wrap-aware): motion-heading + packet-yaw per-window deltas, seam-crossing count (5 on Dead Rail, 0 on Oasis), recommended live pilot target; `pick_yaw_session` selects the yaw-bearing decode |
| O6 | E | ✅ **Max HP is IN the replay — type-5 spawn full-state broadcast** (2026-08-11): `u32 eid @ +0x00`, `u16 currentHP @ +0x33`; the first broadcast per roster entity precedes any damage (28/28 tanks both replays), so it equals max HP. Validated: author 700 == battle_results `hitpoints_left` (both replays), monotonic non-increasing across broadcasts, total damage_dealt ≤ Σ first values (8964 ≤ 12140 / 6227 ≤ 8500), same tank_id same value cross-replay. Decoder now emits `CanonicalEventKind.MaxHealthObserved` per roster entity (first broadcast only; non-roster 999-style entities filtered) + typed raw record. Real-replay check: 14/14 Oasis + 14/14 Dead Rail exact. This gives the overlay a true HP-fraction denominator without any memory read — ✅ **Damage amount SOLVED (2026-08-11)**: the real ledger is type-8 **subtype-1** (19 B): victim u32 +0x00, post-hit HP u16 +0x0C, attacker i32 +0x0E; amount = victim HP delta seeded by the type-5 max-HP broadcast; post-hit HP 0xFFFD is the destroy marker with killer attribution (remaining HP credited to the killer). `TryReadDirectDamage` (old subtype-8 amount) was WRONG — it inflated damage ~2.5–7× and dropped the real packets. Re-decode validation: per-attacker sums == battle_results damage_dealt EXACTLY on both replays for every player with battle results (9/9: 1719/1158/56/959/752 Oasis + 1598/326/489/905 Dead Rail); players without battle results (left the battle, NULL stats) still get true decoded damage. ✅ **Exact overlay HP wired (2026-08-11)**: `ReplayFrameSource` now computes `hpFraction = 1 − taken/maxHealth` (falls back to the damage arc when max is unknown), and `OverlayTankState`/API/HUD carry exact `MaxHealth`/`CurrentHealth` — the nameplate shows "438 / 700" under the HP bar. Verified on both fresh imports: HP conservation 28/28 tanks (current = max − taken never negative), total dealt == total taken (6227/6227, 8964/8964). Along the way the subtype-1 0xFFFD destroy marker now ALSO emits `Destroyed` (deduped with the position marker): it caught **3 tanks the position markers missed** (Dead Rail 2549397 @183.8s, 2549402 @271.5s; Oasis 3760576 @245.1s) — alive flags now match the HP ledger exactly (0 mismatches). The invariants are now a durable offline check: `scripts/python/verify-hp-ledger.py` (read-only vs the SQLite store, exit 0 on both fresh sessions, exit 1 on the pre-fix ledger) plus the synthetic form in the decoder regression test |

**Discovery sidecar (parallel, offline):** the parallel-workstreams runbook
(`docs/operations/parallel-workstreams.md`) + `scripts/workstream-lock.py`
serialize the Ghidra project DB, docs, and the live queue; two targeted Ghidra
passes landed. Pass 1 decompiled the FRESH43 write site `FUN_00bc3940` and
pinned a per-frame transform object — the corrected decode (verified
20/20 by `VerifyTransformRecord`, 2026-08-11) is **position float32
triple `+0x1C/0x20/0x24`** (the earlier `+0x38` candidate was the
pre-correction read), rotation region `+0x38..0x58`, and a **16-float 4×4
world matrix at `+0x60..0x9C`**. Pass 2 decompiled
`FUN_00729570` (RVA `0x329570`): it is the engine's **generic 4×4 matrix
multiply** (20+ call sites; second operand read column-major), so the `+0x60`
matrix is a per-frame composited world/view-style matrix — the live camera/VP
track's static anchor. Evidence: `tools/ghidra-scripts/writesite-ring-disasm.txt`
+ `writesite-matrix-helper-disasm.txt`. ✅ **Camera family RTTI foothold
(2026-08-11)**: `ReplayCameraController::vftable` RVA `0x326dd0c` +
`BaseCameraController::vftable` RVA `0x32dddcc` both forward-verified via
RTTI (`ResolveVftableClass`); `BaseCameraController` slot 4
(`FUN_01dd2cd0`) writes a camera-state RING at `[camera+0x28]` (entries at
`(idx+0x36)*0x10` and `0x364+idx*0x10`, 0x10-byte stride, ringIndex at
`[ring+0x320]`) — the same bounded-ring pattern as the G0 position ring,
the anchor for the VP-matrix hunt. New tools: `FindVftableForType.java`
(reverse RTTI name→vftable) + `DumpHierarchy.java` (RTTI base walk). ✅ **Camera
family: full hierarchy + factory + ring mechanics (2026-08-11)**: the three
camera vftables are forward-verified — BaseCameraController `0x32dddcc`,
**CameraController `0x32de028`** (RTTI `.?AVCameraController@@`; NOT the
stale `0x36de028`, which is an exception table), ReplayCameraController
`0x326dd0c`. Camera factory `FUN_0165fe40` dispatches on battle mode
(2 = replay → ReplayCameraController 0x60; else CameraController 0x98) and
stores the camera refcounted at `[mgr+0x2C]`; the camera-state ring object
comes from `[[mgr+0xC]+0x8C]` → `[cam+0x28]`. Ring writer = base vtable slot 4
(`FUN_01dd2cd0`): two floats per frame into 16-byte entries at
`0x360/0x364 + idx*0x10`, index at `[ring+0x320]`, mirrors `+0x324/+0x328`.
CameraController slots decoded: drag accumulator (`FUN_01dc51d0`, sens
`[cam+0x40]`), drag→ring handler (`FUN_01dc5440`, point-in-rect + ring write),
state machine (`FUN_01dc5310`). The ring's two floats are camera
screen-space/input state — the world position/VP matrix remain the open
W2S lead. ✅ **Camera math pipeline (2026-08-11)**: the camera-state object
holds live yaw/pitch `+0x58/+0x5c`, smoothed `+0x60/+0x64`, deltas
`+0x80/+0x84`; `FUN_01ddc9c0`/`FUN_01ddce80` integrate them and build
rotation matrices via the verified 4×4 multiply `FUN_00729570`;
`FUN_01dde860` reads the hash-bound transform world matrix
`[t+0x60..0x90]` and composes it with camera orientation — the
world→camera seam and the VP-matrix writer's entry point. ✅ **Camera
state object pinned (2026-08-11)**: the per-frame dispatcher
`FUN_01ddb130` integrates camera position `+0x11C/+0x120/+0x124`
(`pos += delta`) and dispatches the angle/matrix builders by mode;
composed view basis rows 0-1 at `+0xAC..0xC4` (yaw×pitch rotation ×
transform world matrix + position, by `FUN_01dde860`), yaw/pitch
`+0x58/+0x5C` (smoothed `+0x60/+0x64`), ring index `+0x320`. The W2S
camera anchor is one object: `[[mgr+0x2C]+0x28]`; the projection matrix
and the full 4×4 view composition remain before a full static
world→screen pipeline. Handoff:
`docs/operations/handoffs/2026-08-11-camera-family-hierarchy-factory.md`.
✅ **Camera ownership root resolved (2026-08-11)**: the camera factory's
`this` is the **BattleResources** object (raw-byte verified: `MOV EBX,ECX`
in `TryLoadResources` prologue → `MOV ECX,EBX; CALL FUN_0165fe40`). The
full chain is now a fixed member-path, not a signature scan:
`SessionController` (vftable `0x323d9bc`/`0x323d9f0`, ctor
`FUN_012855f0`) → `avatar = [session+0x11C]` (AvatarControllerBattle
live / AvatarControllerReplay replay, created in
`SessionController::OnAvatarBecomePlayer` `FUN_012afab0`, ctors
`FUN_016368d0`/`FUN_0163dcc0` vftable `0x3277da4`) →
`battleResources = [avatar+0x154]` (`BattleResources::Load`
`FUN_01651780`/`TryLoadResources` `FUN_01662f00` this) →
`camera = [battleResources+0x2C]` (factory `FUN_0165fe40`, mode==2 →
ReplayCameraController) → `cameraState = [camera+0x28]` (yaw/pitch
`+0x58/+0x5C`, view basis `+0xAC..0xC4`, position `+0x11C/+0x120/+0x124`).
BattleResources is embedded in the avatar controller hierarchy; the
replay variant (`AvatarControllerReplay`, vftable `0x3277e8c`, created at
`[replayCtrl+0x158]`) is reachable through the same member offsets.
Handoff: `docs/operations/handoffs/2026-08-11-camera-ownership-root.md`.
✅ **CAM-001 pre-staged + ASLR correction (2026-08-11)**: PE headers of
the hash-pinned binary: `ImageBase=0x400000`, `DllCharacteristics=0x8140`
→ **ASLR enabled**, so the runtime vftable pointer is (module base + RVA),
not the preferred-base constant; RVAs confirmed `0x3277e8c` (replay) /
`0x3277da4` (live). Pre-staged read-only session script
`scripts/invoke-camera-state-verify.ps1` (CAM-001): gate-waits, binds
launch-artifact ground truth like od-073, learns the runtime module base
from the scan response, scans `base+0x3277e8c` (LE) → walks
`[avatar+0x154]→[br+0x2C]→[cam+0x28]` → reads yaw/pitch/basis/position →
correlates camera yaw vs the decoded frame yaw (timeSeconds) and position
vs the nearest trajectory sample (third-person offset norm 1-30 m);
privacy-safe aggregate (`cam001.camera-state-verify.v1`, no raw
coordinates/addresses/bytes). Next: one approved replay launch + this
script → wire the true camera into `ReplayFrameSource.BuildCamera`.
Handoff: `docs/operations/handoffs/2026-08-11-cam001-pre-staged-aslr-correction.md`.
✅ **CAM-002 live pose layout (2026-08-11, two approved launches)**: the
CAM-001 chain walks live and both identity gates pass (avatar scan → 1
candidate; `[br+0x2C]` vftable = ReplayCameraController `base+0x326dd0c`;
`[cam+0x28]` = GameCamera `base+0x32dafa0`). The ReplayCameraController is
a **frozen shell** (no live fields); ALL live pose fields sit on the
**GameCamera**: position `+0x38/+0x3C/+0x40` (prev copy `+0x44..`),
yaw **cos/sin pair `+0x50/+0x54`** (yaw = atan2(sin,cos)), pitch `+0x58`,
basis `+0x80..0xA8`, extra pos copy `+0xB0/B4/B8` (diff-scan verified).
Memory camera yaw aligns to the decoded frame yaw at **0.0027 rad** with
the same sign convention — the memory object is the live replay camera.
Open item: camera position is 363-440 m from the decoded tank at the
aligned time → memory↔decoded **coordinate-space calibration** (read the
memory tank via `/discover/entity-position` at the same wall time; the
memory-space offset is the third-person proof). Session script is now
**v5** (GameCamera pose + memory-space correlation + decoded-yaw
timeline). Handoff:
`docs/operations/handoffs/2026-08-11-cam002-live-pose-layout.md`.
🚫 **CAM-001 verdict SUPERSEDED (CAM-010, 2026-08-11)**: GameCamera posA
`+0x38` is stored **(x, z, y) — world Y/Z swapped**; the yz-swapped posA
tracks the viewpoint tank to **2.1–3.6 m** (sub-meter on v7b+v7c), and
CAM-004's "23.57 m third-person offset" was the `√2·|tank.z − tank.y|`
artifact (z − y = 16.7 m at the read moment), NOT a chase eye. The W2S
seam must yz-swap world→camera space; the orientation convention and the
true render eye (candidate: ReplayCameraController `+0x28` ring) are the
open questions. Handoff:
`docs/operations/handoffs/2026-08-11-cam010-yz-swap-position-convention.md`.
✅ **CAM-001 v7 root-cause work (2026-08-11) still stands structurally**:
three probes on session `019ff25b` falsified wrong-instance (the
GameCamera is UNIQUE in the process and the chain reaches it),
wrong-class (same RVA 0x32dafa0 under ASLR bases 0x380000 vs 0xAB0000),
and wrong-field (no near-tank pose anywhere in +0x00..0x200 — now
explained: the eye is ON the tank, there never was a 23 m eye). The
camera is NOT yaw-locked to the tank (101–177° gaps on v7b/v7c) — a
non-chase camera STATE on the flipped phase. Validator `eye` now
yz-swaps posA; `-CaptureWindow` scalars stay diagnostic-only. See
`cam001-v7-evidence-template.md`.
The
CAM-003 resolver gate is **REFRAMED by CAM-008 (2026-08-11)**: the
"variant" `base+0x325ad2c` is RTTI-verified to be **PreLoginController**
— the game was simply not in a battle session yet during those reads (the
resolver's `0x323d9bc` `SessionController` gate was CORRECT to reject).
The resolver now reports the retryable `ReplaySessionInactive` for that
phase (callers wait for playback), and the v6 direct walk bails cleanly
instead of dereferencing garbage.
✅ **CAM-005 (2026-08-11): the host exposes the camera pose** —
`POST /discover/camera-pose` walks the CAM-001 chain under the gate
(avatar vftable anchor → br → camera → GameCamera, identity gates on
every hop, pose region double-read), version-pinned in Core
(`Type10CameraPoseLayout`), 7 unit tests, `IMemoryScanDiscoverer`
extracted for testability.
✅ **CAM-006 (2026-08-11): the pose is wired into the frame path** —
`IOverlayFrameSource.GetFrameAsync` gains an optional `OverlayCamera?`
(default null = viewpoint fallback), `ReplayFrameSource` passes it
through the tested `cameraOverride` seam, and the frame endpoint pulls
the pose from the scanner port when a gate-verified session is live
(fail-closed: any read/status problem → viewpoint). 2 new endpoint
tests.
✅ **CAM-007 (2026-08-11): the W2S projection cross-check is ready** —
`verify-camera-projection.py` projects the decoded tank through the
memory camera with the exact `WorldToScreen.Project` math and asserts
the third-person look-at property (tank near screen center across the
70–110° FOV band, look-at angle small), with a pitch-convention
diagnostic; the CAM-001 script now persists per-round pose + decoded
tank (schema v7). Self-tested with synthetic fixtures; needs one live
session for real evidence. Remaining: the live session (frame response
carries the memory pose; validator verdict on v7 evidence). Handoffs:
`docs/operations/handoffs/2026-08-11-cam004-camera-state-consistent.md`,
`docs/operations/handoffs/2026-08-11-cam005-host-camera-pose-endpoint.md`,
`docs/operations/handoffs/2026-08-11-cam006-camera-wired-into-frame-endpoint.md`,
`docs/operations/handoffs/2026-08-11-cam007-projection-cross-check.md`,
`docs/operations/handoffs/2026-08-11-cam008-prelogin-controller-rtti.md`,
`docs/operations/handoffs/2026-08-11-cam009-fov-config-found.md`.
✅ **CAM-009 (2026-08-11): the numeric FOV is in the installed config** —
read-only DVPL/LZ4 inspection of `optionsGlobal.yaml.dvpl` pins the
engine battle FOV: `default fov` **64° (horizontal**, `horizontal to
vertical radius coefficient` 0.73 ⇒ ~47° vertical), `camo/showcase fov`
64, movement FOV mult 1.0 / offset 1°; `optionsDesktop.yaml.dvpl` has the
player slider 40–60 (default 54) + `Camera backward fov offset` 8°. The
CAM-007 validator band widened to 40/47/64/90°; the live session settles
the exact convention.

### Phase 2 — The live seam + first live sessions (serialized)

| Step | WS | Deliverable | Session cap |
|---|---|---|---|
| L0 | C | ✅ `EntityRecordRegionReadRequest/Result` (≤ 4 KB region, bytes + replay time only, `OfflineReplayVerified` + current auth) — the ONE product addition; shipped 2026-08-10 with the guarded region read + replay-clock label + `RegionAnchor` (ring-record / entity-tank-record / **entity-base** — added 2026-08-11 for the statically-verified HP home; coordinator derefs `[entity+0x3C]` itself for the tank-record anchor, reads the entity base directly for entity-base), 9 coordinator tests + 5 web endpoint tests | — |
| L1 | D | ✅ **HP live session HIT 2026-08-11 (OD-RECOVERY-087)** — the entity-base current-health signed int16 is CONFIRMED LIVE at `+0xB8` on Oasis Palms (victim 3760578): 8/8 health drops == damage sums exactly, max `+0x11C` 1550 constant, alive `+0xBA` 1, healing `+0x11E` 0; automated contract **HIT score 1.0, flatness 1.0, Strict 8/8** via the new subset-sum lag attribution (`hp-diff --int16 true --lag-tolerance 4`; `VerifyPlayerHpChain` 26/26 static map confirmed live). Finding: the game applies decoded damage events with a variable ~1–3.4 s memory lag — the driver now dumps a dense span per hit and the correlator matches drops against event subsets (`eventLagToleranceSeconds`, default 0 = exact). **X4 frame HP WIRED (2026-08-11, additive):** the batch item gained an optional entity-base region (same resolve + ONE attestation), the pure `EntityBaseRegion` decoder reads current `+0xB8` / max `+0x11C` / alive `+0xBA`, `LiveFrameTankState` carries `hpCurrent`/`hpMax`/`alive`, and `LiveFrameProjector` maps real values to the HUD bar — honest per-tank null only when the entity-base read failed. Phase-4 two-replay rule still gates HP publication (Dead Rail victim 2549399) | 1 |
| L2 | D | ✅ **Facing live session HIT 2026-08-11 (OD-RECOVERY-088)** — ring-record yaw CONFIRMED LIVE at **`+0x30`** (Oasis Palms victim 3760577, 48 dumps, every `sameDecodedClockProven=true`): the ring-record tail is a live-verified rotation triple — roll `+0x28` (48/48 within 0.5°), pitch `+0x2C` (47/48), yaw `+0x30` (46/48 at fixed 5 s shared lag; median per-dump error 0.000°), `+0x34` padding — all at the ~5 s memory-apply lag (median 5.0 s). **The rehearsal's +0x2C prediction is corrected**: it was self-constructed (its synthetic dumps placed yaw at +0x2C by design — correlator mechanics, not layout); the live read wins. Automated contract **HIT score 1.0, flatness 1.0, 48/48 dumps, best shared lag 5.0 s** via the new value-match lag path (`yaw-diff --max-lag-seconds 8`; additive, default 0 = exact). `RingRecordRegion.YawOffset` corrected +0x2C → +0x30 (+ pitch/roll constants + readers). X4 frame `yawRadians` is now live-verified. **Phase-4 repeat CLOSED (OD-RECOVERY-089, 2026-08-11): Dead Rail HIT at `+0x30` — 56/56, score 1.0, flatness 1.0; `twoReplayRepeatability = true`.** The at-session verdict was honest-negative — root cause: the G2 replay-clock LABEL skew is per-dump variable and OPPOSITE in sign between replays (Oasis memory lags +4.8 s; Dead Rail leads −2.5 s), invisible to the one-directional shared lag; the additive per-dump bounded bidirectional path (`yaw-diff --per-dump-lag --memory-lead-seconds`) re-verdicts the SAME dumps to HIT on both replays (median error 0.000°). Facing/yaw is publication-READY via `g1-yaw-publication-draft.md` (operator approval + gate run only) | 1 |
| L3 | D | Damage-dealt live session — **DONE HONEST-NEGATIVE 2026-08-11 (OD-RECOVERY-090)** — no counter in the 320-byte entity-base region: session `019ff250`, 50 dumps (all 6 event windows + controls, damage totals corrected to 6/752), verdict `hit=False` — top `+0x3C` score 0.833 demoted by flatness 0.091 (it is the already-measured position-copy float; the flatness control worked exactly as designed). Re-attempt = wider region / sibling-record sweep with the same increment-correlator contract; Dead Rail repeat (attacker 2549401) remains the Phase-4 gate for any future hit. Evidence: `docs/operations/od-recovery-090-evidence-template.md` | 1 |
| L4 | D | replayTime session (OD-044 interceptor, byte-exact Double — fixed; 2026-08-10 the clock's static chain verified — `GameCore 0x04095c88 → … → [BWServerConnection+0x58]+0x90` Double — so the session can chain-resolve via L0; 2026-08-11 exhaustive write-site scan returned the copy-path negative, so `-ArmSourceOnFirstHit` is load-bearing and the first hit is expected to be a CRT copy site) | 1 |
| T1 | D | **Turret-facing + lock-on discovery (survey DONE 2026-08-11)** — the negative is THREE-WAY: type-10 (hull only), type-5 spawn broadcasts (no rotation floats; its x/y/z doc row also did NOT reproduce — tail unclassified), and memory (ring record + entity base carry hull rotation only). Turret/lock-on have NO replay ground truth anywhere, so discovery is live-behavioral: capture the entity-base region while the player traverses the turret without moving the hull (a candidate field responds; hull yaw/pitch/roll stay put). The survey also MEASURED the entity-base copy layout (id `+0x1C`, position `+0x3C/40/44`, rotation triple `+0x48/4C/50`, HP `+0xB8/BA/11C`) — a single-region per-tank read candidate (unproven as canonical). Session design PRE-STAGED: `docs/operations/t1-turret-traversal-design.md` (the replay camera IS the turret driver in playback; camera-pose + entity-base batch read under one G2 attestation; camera-yaw correlation is the discriminator). See `record-diffing-groundwork.md` §Turret-facing | 1 |

Each session reuses L0; the region dump is multipurpose (one dump yields
position + velocity + rotation + HP candidates), so later sessions are
cheaper than the first. NOTE (2026-08-11): `[entity+0x3C]` is the
TRANSFORM OBJECT under a **hash-bound verdict** — `VerifyTransformRecord`
20/20 (`transform-record-verified`): getter `FUN_00d29ea0 = return
[ECX+0x3C]` (bytes `8b 41 3c c2 04 00`); position float32
`+0x1C/20/24`, world matrix `+0x60..0x9C`, rotation `+0x38..0x58`.
SUPERSEDED for HP (2026-08-11, same day): `VerifyPlayerHpChain` **26/26**
pins the HP map at the ENTITY BASE, not the transform — current int16
`+0xB8`, alive `+0xBA`, max int16 `+0x11C`, healing int16 `+0x11E` (see
the L1 row above); the transform region is the movement/rotation home,
and `+0x48` was the test fixture's planted offset, refuted. The
entity-base anchor is the L1 live-session default.

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
| V4 | F | ✅ **God-view replay minimap** (2026-08-10): world X/Z now ride through the frame API (`ProjectedTank.WorldX/WorldZ` → `OverlayTankResponse` → CLI), the view model normalizes every roster tank against the session's map boundary (`MinimapMath.Normalize` → `MinimapItems`), and the HUD draws a bottom-right panel — team-colored dots, grey wrecks, white camera ring (`W2sHudView.BuildMinimap`). Pure god-view: tanks behind the camera still appear. ✅ **Beacons on the minimap** (2026-08-10): `ProjectedBeacon.WorldX/WorldZ` → `OverlayBeaconResponse` → `MinimapBeacons` normalized against the same boundary and drawn as colored diamonds under the tank dots. ✅ **Map texture under the dots** (2026-08-10): the minimap image loaded via `LoadMinimapAsync` now renders as the panel background (`MinimapImageSource` → `Render` → `BuildMinimap`), stretched to the panel square so normalized dots align with terrain; dot alignment is the invariant (a non-square boundary distorts texture, not dots). Fails closed when no texture is installed. ⚠️ **Texture resolution gap (2026-08-11, pinned by tests)**: decoded map IDs are numeric arena identities (Oasis = `11`, Dead Rail = `7`) that `MapMinimapFolder` passes through unchanged, so they never match the install's name-based minimap folders — and this install ships **no Oasis Palms or Dead Rail texture at all** (55 folders checked, no config references). The texture under the dots is therefore **inactive for real replays** (blank panel, dots only) and the dot-vs-terrain alignment invariant remains **unverified against a real texture** until an arena-id → folder mapping exists. Variant folders (`desert_train_02`) are also unreachable (numeric `_02` stripped). Mechanism tests: `MinimapTextureFolderTests`. ✅ **Camera facing tick** (2026-08-10): `CameraYawRadians` flows to the view model (`MinimapCameraYawRadians`) and the HUD draws a small white triangle from the camera ring toward the viewpoint's facing (packet yaw convention 0=+Z, +π/2=+X mapped to panel pixels; `CameraTickApex` pure helper). Verified on Oasis Palms: 14 tanks normalized against map-11 boundary; a center POI beacon normalizes to (0.56, 0.57); camera yaw 0.564 rad at t=100 renders a right-down tick |

### Phase 5 — Live overlay (policy-gated, later)

| Step | WS | Deliverable | Gate |
|---|---|---|---|
| X1 | G | ✅ Policy memo: ADR-0002 relaxation decision, ToS risk, scope — **APPROVED 2026-08-11 (Option A: read-only live overlay, replay-proven fields only)**, owner-approved (`docs/operations/x1-live-game-policy-memo.md`); unlocks the Phase-5 design track; authorizes NO live testing (sessions remain separately gated). First unlocked deliverable: `docs/operations/live-match-gate-design.md` (new `LiveMatchVerified` state: unchanged process/build identity + user live assertion instead of the replay lifecycle marker — no fabrication; replay gate untouched; read-only surface; replay-proven fields only) | ✅ explicit user approval (2026-08-11) |
| X2 | C | ✅ **Batch N-entity read surface PASS live 2026-08-11 (OD-RECOVERY-086)** — full-roster dumps through `/discover/entity-regions` resolve 14/14 incl. enemies with the G2 clock attested on every batch; 34/34 compared positions align to decoded ground truth within the 2 s G2 window (stationary 0.00 m, moving at the −0.8 s read-pass window). Harness fixes shipped: launcher-owned G2 anchor at the blitz-log marker moment, driver per-target clock wait, BOM-less writes, 2 s window cross-check (design `docs/operations/batch-entity-read-design.md`) | X1 |
| X2b | C | **Live roster enumeration — team-based split evidenced 2026-08-11 (OD-RECOVERY-086):** `/discover/entity-roster` full-tree walk + movement-filter vtable gate returns the player's OWN team's avatar family only (7/14, precision 1.000, recall 0.500, 0 extra — all found = team 1, all missing = team 2/enemies). The X4 loop must re-enumerate per tick or add a second discriminator for enemy avatars; design `docs/operations/live-roster-read-design.md` | X1 |
| X3 | C/D | Camera offset track (the LIVE game's camera — a new discovery target) | X1 |
| X4 | F | ✅ **LiveFrameSource — IMPLEMENTED 2026-08-11** (design `docs/operations/live-frame-loop-design.md`): coordinator-composed `POST /discover/live-frame` (roster → batch regions → camera pose, ONE guarded reader lease + ONE G2 clock label, `hp: null` until L1) → pure `LiveFrameProjector` → `GET /api/v1/live/frame` serving the SAME `OverlayFrameResponse` the replay path uses (shared `ToOverlayFrameResponse`, no overlay rewrite) → overlay `IsLiveMode` toggle → `LiveFrameReadMeasurement` read-pass window (item-7 budget). The 086 rehearsal is DONE (X2 batch PASS live + X3 team-split evidenced — the loop must re-enumerate per tick or add an enemy discriminator). L2 facing is DONE (088 Oasis HIT at `+0x30`, 089 Dead Rail Phase-4 HIT — `twoReplayRepeatability = true`), L1 HP is DONE (087 HIT at `+0xB8`; the entity-base HP read is WIRED into the frame, additive). Remaining live-gated: CAM-001 v7 (camera evidence) then OD-RECOVERY-090 (L3 damage-dealt) + its Dead Rail repeat, then the id→name join (`docs/operations/live-roster-name-join-design.md`) | X2 + X2b + X3 |
| X5 | F | Spotting model (only spotted tanks rendered; wall-hack god-view explicitly out) | X1 |

**Label note (2026-08-11):** the rehearsal design docs use `X2`/`X3`/`X4`
for the offline rehearsal tracks (`batch-entity-read-design.md` =
roadmap X2, `live-roster-read-design.md` = roadmap **X2b**, and
`live-frame-loop-design.md` = roadmap X4). They are the offline halves of
the live surfaces above; nothing is rebuilt at the live step.

## Cross-links

- Legacy research detail: `docs/operations/offset-discovery-roadmap.md`
- Record-diffing playbook + live plan: `docs/operations/record-diffing-groundwork.md`
- replayTime plan: `docs/operations/replaytime-live-attempt-plan.md`
- Publication workflow: `docs/operations/offset-discovery-workflow.md` Phase 5,
  `docs/operations/g0-operator-checklist.md`
- Overlay (current 2D surface): `src/WotBTreader.Overlay/`
- Packet rotation discovery: `offline/replay-format.md`, handoff
  `2026-08-10-facing-yaw-packet-discovery.md`
- Resolver-path consolidation plan (strategy + ordered checklist, hardware
  atomicity last): `docs/operations/resolver-path-consolidation.md`
