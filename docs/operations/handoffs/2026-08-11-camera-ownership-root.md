# Camera ownership root — BattleResources → avatar controller → SessionController

Date: 2026-08-11. Binary: wotblitz.exe 11.19.0.10 (hash `1cda5c31…1760307d`).
Static-only; nothing promoted, resolver/read surface untouched.

## Question

The camera-state W2S anchor was pinned as `[[mgr+0x2C]+0x28]` (handoff
2026-08-11-camera-family-hierarchy-factory.md) but "mgr" — the camera
factory's `this` — was unnamed, and the read plan still needed a signature
scan. This session resolves who `mgr` is and the full fixed member-path
down to the session controller.

## Verdict: `mgr` = BattleResources; full chain is a fixed member-path

Raw-byte verified on the hash-verified binary (all addresses absolute VA):

| hop | RVA | evidence |
|---|---|---|
| `BattleResources::Load` `FUN_01651780` | `0x1251780` | string `"BattleResources::Load"` in body; called with `this` = avatar-controller member |
| `TryLoadResources` `FUN_01662f00` | `0x1262f00` | string `"BattleResources::TryLoadResources"`; `MOV EBX,ECX` in prologue (`0x1662f2e`) |
| camera factory `FUN_0165fe40` | `0x125fe40` | call site `MOV ECX,EBX; CALL 0x0165fe40` (`0x166339b/9d`) — **this = BattleResources** |
| camera stored at | — | `[this+0x2C]` (refcounted), ring from `[[this+0xC]+0x8C]` → `[cam+0x28]` (confirmed in factory body) |

### Ownership chain (fixed member offsets)

```
SessionController                     vftable 0x323d9bc / 0x323d9f0 (RTTI .?AVSessionController@@)
  ctor FUN_012855f0 (0x12855f0, sets [this]=SessionController::vftable)
  [session + 0x11C] → AvatarControllerBattle (live) / AvatarControllerReplay (replay)
      created in SessionController::OnAvatarBecomePlayer FUN_012afab0 (0xeafab0)
      ctors FUN_016368d0 / FUN_0163dcc0, main vftable 0x3277da4 (RTTI .?AVAvatarControllerBattle@@)
  [avatar + 0x154] → BattleResources
      this of BattleResources::Load / TryLoadResources (ReloadScreenForRewind
      FUN_0165d9b0 passes it; MOV ECX,[EDI+0x154] before CALL 0x01651780)
  [battleResources + 0x2C] → camera (ReplayCameraController when mode==2)
  [camera + 0x28] → cameraState (W2S anchor)
      yaw/pitch +0x58/+0x5C, view basis +0xAC..0xC4, position +0x11C/+0x120/+0x124
```

BattleResources is embedded in the avatar controller hierarchy — reached by
fixed member offsets from the SessionController, so the replay/live read
plan can walk `[session+0x11C]+0x154 → +0x2C → +0x28` instead of
signature-scanning. (The SessionController instance itself still needs a
process-global anchor or a targeted scan in the offline session — the
chain *after* it is now fixed.)

### Replay variant resolved

`AvatarControllerReplay` RTTI name abs `0x427e184`, main vftable abs
`0x3677e8c` (COL `0x3959438`; secondary tables `0x3677edc`/`0x3677f34`).
Ctor `FUN_016369f0` (writes `0x3677e8c` at `[this]`), called from
`FUN_013d59a0` (a replay-screen controller vftable slot at `0xfc5123`),
which stores the result refcounted at `[replayCtrl + 0x158]`. The same
`+0x154` BattleResources member and `+0x2C`/`+0x28` camera chain apply —
replay mode reaches the W2S anchor through the same member offsets.

## Evidence trail

- `BattleResources::Load` callers: `0x1214673` (BattleLoadingController::
  LoadingThreadFunc `FUN_01614500`) and `0x125db56` (AvatarControllerBase::
  ReloadScreenForRewind `FUN_0165d9b0`, this = `[EDI+0x154]`).
- `TryLoadResources` (`FUN_01662f00`) prologue `MOV EBX,ECX` (0x1662f2e) and
  camera-factory call `MOV ECX,EBX; CALL FUN_0165fe40` (0x166339b) — the
  factory's `this` is the BattleResources object.
- AvatarControllerBattle RTTI: name abs `0x427e15c`, main vftable abs
  `0x3677da4` (COL `0x3959368`); ctor refs in .text at `0x1236957` and
  `0x123dcf2` (both write vftable `0x3677da4`).
- AvatarControllerReplay RTTI: name abs `0x427e184`, main vftable abs
  `0x3677e8c` (COL `0x3959438`); ctor `FUN_016369f0`, creator
  `FUN_013d59a0` stores at `[replayCtrl+0x158]`.
- Avatar creation site: `SessionController::OnAvatarBecomePlayer`
  `FUN_012afab0` (string at 0x12afab0 body; alloc 0x1a8, ctor
  `FUN_016368d0`) stores at `[param_1 + 0x11C]` (refcounted).
- SessionController RTTI: name abs `0x4235910`, vftables `0x323d9bc`/
  `0x323d9f0` (COLs `0x351c9cc`/`0x351ca48`); ctor `FUN_012855f0`.
- OnAvatarBecomePlayer is vftable slot at `0xe96f1f` (inside
  `0x323d9bc`-region table), confirming it is a SessionController method.

## Files touched

- `docs/operations/product-roadmap.md` (camera track: ownership-root entry)
- This handoff

## Next steps

- Offline verification session (pre-staged in record-diffing-groundwork.md):
  the SessionController instance anchor can be found by scanning for the
  avatar's vftable `0x3677da4` (or the BattleResources layout) — then the
  member-path is fixed. Deliverable: true camera for
  `ReplayFrameSource.BuildCamera`.
- Projection/FOV measurement (offline session) — unchanged from prior
  handoff.
