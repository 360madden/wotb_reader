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
| F1 | A | Wrap-aware float32 `HeadingCorrelator` + facing rehearsal on both replays (packet-yaw ground truth) | Facing live session |
| F2 | F | `OverlayFrame`/`TankState` contract + `ReplayFrameSource` (DB → frames at replay time t, HP step function) | Nameplates, beacons, live swap-in |
| F3 | E | Velocity series from replay (finite difference) + pitch/roll validation script | `+0x28` semantics, pitch/roll tracks |
| F4 | G | This roadmap + workstream registry | All parallel work |

All four are independent, file-disjoint, and gate-green alone.

### Phase 1 — Replay overlay offline (parallel with Phase 2 prep)

| Step | WS | Deliverable |
|---|---|---|
| O1 | F | `WorldToScreen` projection module (view matrix from pos+yaw+pitch, perspective from FOV) + tests |
| O2 | F | Nameplate layer: every tank's name, team color, HP bar, distance — clock-anchored over the replay window |
| O3 | F | Beacon/POI model (world coords + label + color + replay-time tag) + placement + persistence |
| O4 | B | Capture-zone/base decode from battle_results.dat (objective markers) |
| O5 | E | `--heading-delta` extractor mode (movement-gated, wrap-aware) for plan/tooling reuse |

### Phase 2 — The live seam + first live sessions (serialized)

| Step | WS | Deliverable | Session cap |
|---|---|---|---|
| L0 | C | `EntityRecordRegionReadRequest/Result` (≤ 4 KB region, bytes + replay time only, `OfflineReplayVerified` + current auth) — the ONE product addition | — |
| L1 | D | HP live session (Oasis Palms victim 3760578 → verify `+0x48` live) | 1 |
| L2 | D | Facing live session (ring-record dump vs `position_samples.yaw`; probe `+0x2C..+0x37` first) | 1 |
| L3 | D | Damage-dealt live session (viewpoint counter; share the L1 seam) | 1 |
| L4 | D | replayTime session (OD-044 interceptor, byte-exact Double — fixed) | 1 |

Each session reuses L0; the region dump is multipurpose (one dump yields
position + velocity + rotation + HP candidates), so later sessions are
cheaper than the first.

### Phase 3 — Publications (serial, operator-gated)

| Step | WS | Deliverable |
|---|---|---|
| P1 | H | Velocity `+0x28` promotion (live-verified semantics) |
| P2 | H | HP publication (2-replay live agreement at `+0x48`) |
| P3 | H | Facing/yaw publication (replaces the quarantined candidate; reconciled against packet ground truth) |
| P4 | H | Pitch/roll publication (if the ring record holds them) |

### Phase 4 — Overlay 2.0 (data-rich replay overlay)

| Step | WS | Deliverable |
|---|---|---|
| V1 | F | Facing arrows / heading glyphs on nameplates (uses the discovered yaw offset OR the packet ground truth in replay mode) |
| V2 | F | Objective markers (capture zones), event-feed tie-ins (damage pips, death markers) |
| V3 | F | Visibility model for replay mode (spotted-reproduction as a documented option; god-view default) |

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
