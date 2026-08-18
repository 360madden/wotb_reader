# Handoff — 2026-08-10: O3 beacons + parallel guardrails + first Ghidra pass

**Branch:** `main` — clean tree before this phase, gate green after.

## What landed

### 1. Parallel-workstream guardrails (coordination layer)
- `scripts/workstream-lock.py` — cooperative serialization for the three
  single-writer lanes: `ghidra-project`, `docs`, `live-session`. Token-based
  ownership (pid alone can't survive a shell timeout), Windows-correct
  liveness check (`OpenProcess`/`GetExitCodeProcess`; `os.kill(pid,0)` is
  broken on Windows), stale-lock breaking with `--force`/`break`, worker-pid
  tracking so a surviving child (e.g. the Ghidra JVM) keeps the lane marked
  LOCKED.
- `docs/operations/parallel-workstreams.md` — the coordinator runbook: the
  funnel (parallel offline → serialized gated live), the three serialization
  rules, acceptance criteria an artifact must meet before it may consume a
  live session, and the live-queue rules.

### 2. O3 — Beacon/POI layer (overlay)
- `OverlayBeacon` (Core), `IBeaconStore` + `SqliteBeaconStore`, `beacons`
  table = **migration 6** (keyed `(battle_session_id, name)`, upsert).
- `OverlayFrameProjector` projects beacons with the same camera as tanks;
  time-tag filtering (VisibleFrom/Until) happens at projection, so CLI, web,
  and HUD share one truth.
- Web `/sessions/{id}/frame` and CLI `overlay-frame` return projected beacons;
  CLI gained `beacon add/list/remove`.
- `W2sHudView` renders colored pins + labels under the nameplates; FOV slider
  added to the toolbar (feeds the existing `HudFovDegrees` property).
- 13 new tests (Application 4, Web 1, CLI 3, Storage 4 incl. migration-count
  bumps 5→6, Overlay 2); smoke-tested against the real savanna session
  (add → projected → remove).

### 3. First targeted Ghidra pass (Workstream B) — a real static finding
Decompiled the FRESH43 write site `0x7C39AB` (function `FUN_00bc3940`):
evidence committed at `tools/ghidra-scripts/writesite-ring-disasm.txt`. It is
a per-frame transform updater iterating a scene-record list, and the object it
writes (reached via global accessor `FUN_00d29ea0(0)`) carries:

| Offset | Layout |
|---|---|
| `+0x1c/+0x20/+0x24` | three floats — the active guard (write only when non-zero) |
| `+0x38` | position (3 floats) |
| `+0x44..+0x5c` | 3×3 rotation |
| `+0x60..+0x9c` | **16-float 4×4 matrix**, refreshed per frame via `FUN_00729570` (matrix-transform helper) |
| `+0xa0` | pointer to a sub-object with its own transform |

**Meaning for the camera track:** the `+0x60` matrix slot is the live
VP-matrix discovery target's static anchor — the in-game-replay validation
(scan candidates while playing a known replay, compare against the packet
camera path) should watch this object instead of blind scanning. Honest
limits: this is a *write-site* hypothesis; which camera/entity the object is
still needs the gated live confirmation.

### 4. Workstream C — the matrix helper is a 4×4 matmul (completed same day)
Second targeted pass decompiled `FUN_00729570` (RVA `0x329570`): it is the
engine's **generic 4×4 matrix multiply** — every output element is the
row-column dot product over 16 floats, the second operand read with
column-major stride, and it has 20+ call sites across the binary. So the
`+0x60` matrix written by `FUN_00bc3940` is a per-frame **composited 4×4
matrix** (world/view-style), not a raw copy. Evidence:
`tools/ghidra-scripts/writesite-matrix-helper-disasm.txt`.

**Consequence for the live validation:** the in-game-replay scan should watch
the `+0x60` matrix of the `FUN_00d29ea0(0)` object and compare it against the
replay's packet camera path; a match confirms both the object identity and the
row/column-major convention in one session.

## Next session queue
1. **O4** — capture-zone/base decode from battle_results.dat (objective
   markers on the HUD beacon layer).
2. **L2** — the facing live session (ring-record dump vs
   `position_samples.yaw`) stays behind the approval gate; the static finds
   do not change its scope.

## Rules reaffirmed
- One Ghidra headless session at a time (`ghidra-project` lock).
- Docs/handoffs: coordinator only (`docs` lock).
- Live sessions: serialized, gated, one at a time (`live-session` lock).
