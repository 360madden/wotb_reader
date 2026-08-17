# Penetration v0.3 — viewpoint-vehicle → gun/rotator ownership walk

**Date:** 2026-08-16 (UTC)
**Status:** static derivation complete (hash-bound); live validation not yet run
**Blocker:** `BLK-0027` (still open — this walk is the discriminator that turns
the census's `BoundsExceeded` negative into a proven viewpoint owner)

## Question

The owner census proved there is exactly **one** live `VehicleGunRotator` and
**one** `AvatarGunAgent` in a managed offline replay (and 42 live `VehicleGun`
objects). Before any shell/aim/ray field offset inside those objects is
trusted, the walk must pin **which object owns the viewpoint vehicle's gun and
rotator, and at what field offsets**, and then live-validate that ownership.

## Static evidence (hash-bound, 11.19.0.10 `1cda5c31…`)

New tooling: `tools/ghidra-scripts/FindWeaponVtableInstallers.java` and
`tools/ghidra-scripts/ListCallers.java`. Evidence:
`.build/ghidra-evidence-weapon-install/` (weapon-vtable-installers.txt,
callers.txt, window-disasm.txt — local, ignored).

### vftable RVAs and object shapes

| Family | Primary vftable RVA | Object size | Notes |
|---|---|---|---|
| `VehicleGun` | `0x32dacf4` | `0x64` | primary vftable written only at `+0x0`; `+0x8`/`+0xC` are secondary sub-vtables (`CooldownInfoInterface`, `ListenerHolder<VehicleGunEventsListener>`) at different addresses |
| `VehicleGunRotator` | `0x32eeb40` | `0x1f0` | primary at `+0x0`; `+0x8`=0x36eeb54 (`FollowAimListener`), `+0xC`=0x36eeb64 (`DevOptionsDelegate`) |
| `AvatarGunAgent` | `0x324dae8` | `0xc` | `+0x4` stores the avatar-context reference |

The census therefore counts **distinct objects**, not vtable slots: each object
carries the primary vftable dword exactly once (the secondary slots are
different addresses). 42 `VehicleGun` = 42 live objects; 1 rotator + 1 agent
remain avatar-only.

### Constructor / installer sites

- `VehicleGun` in-place ctor `FUN_01da8bb0` (site `0x19a8c0a`) and allocating
  factory `FUN_01d9cc30`; dtor `FUN_01db0ff0`.
- `VehicleGunRotator` ctor `FUN_01eb6ba0` (site `0x1ab6c01`, source
  `VehicleGunRotator.cpp`); dtor `FUN_01eb9c50`.
- `AvatarGunAgent` ctor `FUN_01360360` (site `0xf603a0`).

### The ownership chain (proven statically)

1. **`AvatarGameLogic` ctor** `FUN_01683b00` (`AvatarGameLogic.cpp`) allocates a
   100-byte `VehicleGun` (`operator_new(100)` → `FUN_01da8bb0`) and stores the
   raw pointer at **`AvatarGameLogic + 0x204`**. It also sets byte **`+0x200` =
   1** (the avatar marker).
2. **`VehicleGameLogic::onEnterWorld`** `FUN_016ea010` (`VehicleGameLogic.cpp`)
   — only when `[this + 0x200]` is set and an id condition matches — allocates a
   0x1f0-byte `VehicleGunRotator` (`FUN_01eb6ba0`) and stores the refcounted
   pointer at **`+0x1fc`**. The `+0x200` marker is set only by the AvatarGameLogic
   ctor, so generic vehicles never get a rotator — matching the census's
   exactly-one rotator.
3. **Reverse pointer:** `VehicleGunRotator` ctor stores its owner `this` at
   **`VehicleGunRotator + 0x10`** (`param_1[4] = param_3`; the caller passes the
   game-logic object as `param_3`).
4. **Entity link (prior work, OD-068/087/091):** `VehicleGameLogic` vtable slot
   `+0x04` getter returns `[this + 0x04]` = the entity, and `[entity + 0xB8]`
   is the current-HP int16 (Verified). `onEnterWorld` uses exactly this getter
   to read `[entity + 0xB8]` for its ALIVE/DEAD check.

So the candidate chain is:

```
viewpoint entity  <-[+0x04]--  AvatarGameLogic (viewpoint vehicle game logic)
                                  |-- [+0x1fc] (refptr) --> VehicleGunRotator
                                  |-- [+0x204] (raw)    --> VehicleGun
VehicleGunRotator --[+0x10] --> AvatarGameLogic   (reverse/back-pointer)
```

## Ranked hypotheses

- **H1 (primary):** the single live rotator/agent pair is the viewpoint's; the
  rotator's `+0x10` points back to the AvatarGameLogic; that object's `+0x204`
  is the viewpoint `VehicleGun` and its `+0x04` is the viewpoint entity.
- **H2 (rejected):** the gun/rotator are owned by a different, unrelated
  object. Rejected because the ctor that installs the avatar marker (`+0x200`)
  is the same object that stores the gun, and `onEnterWorld` stores the rotator
  into the same `+0x1f8..+0x204` field block.
- **H3 (residual risk):** the `+0x04 → entity` link is inherited rather than
  re-derived here; it relies on OD-068's published getter plus OD-087/091's
  `[entity+0xB8]` HP proof. It is cheap to re-confirm in the live protocol.

## Live-validation protocol (bounded, no promotion)

All reads go through the existing coordinator-owned capture seam (loopback +
capability + `OfflineReplayVerified` + exact-build gates). Expected read plan,
executed once per battle with the two-pass stability discipline:

1. **Find the rotator.** AOB-scan Private|Mapped for `moduleBase + 0x32eeb40`
   (exact 4-byte, alignment 4). Require exactly 1 candidate (already
   live-proven by the census).
2. **Reverse pointer.** Read `[rotator + 0x10]` → `owner`. Require the owner to
   be a readable heap address (not image, not null).
3. **Forward pointer round-trip.** Read `[owner + 0x1fc]` and require it equals
   the rotator address (the refptr field points back to the same object).
4. **Gun pointer.** Read `[owner + 0x204]` → `gun`; read `[gun + 0x0]` and
   require it equals `moduleBase + 0x32dacf4` (it is a live `VehicleGun`).
5. **Entity link.** Read `[owner + 0x04]` → `entity`; read `[entity + 0xB8]`
   and require it is a plausible alive int16 consistent with the known
   viewpoint HP.

Expected honest result: steps 1–5 all succeed on the first pass and reproduce
identically on the second pass (`stable`), with no raw address/id/path leaving
the source. Success proves the walk; it does **not** promote any shell/aim/ray
field and does not change the `NotReady` badge.

Stop conditions: step 1 finds `≠ 1` rotator (walk not unique — fail closed);
any pointer does not point into Private|Mapped memory (fail closed); any
vtable check fails (wrong family — fail closed); or the two passes disagree
(fail closed, no promotion).

## What remains after the walk passes

The walk only establishes *ownership*. The next static steps are the
**phase 2–4 semantic field offsets** inside the now-proven objects:

- `VehicleGun`: configured-gun identity and loaded-shell state (controlled
  shell-swap transitions).
- `VehicleGunRotator`: turret yaw and gun elevation (the `+0x4d/+0x4e`
  `0xc7c44a00` constant and the `FollowAimListener` inheritance are leads).
- shot-synchronous muzzle origin + direction (ray).

None of these are derived yet, so `ConfiguredGunUnproven`,
`ShellTransitionUnproven`, `TurretYawUnproven`, `GunElevationUnproven`, and the
ray reasons remain honest. This document records the walk and its proof
protocol only.

## Phase 2–4 candidate field layout (static facts, semantics UNPROVEN)

A second hash-bound pass (`DumpRange.java`, evidence
`.build/ghidra-evidence-weapon-install/range-disasm.txt`) mapped the
constructor field writes to concrete byte offsets. These are **candidate
layouts only** — the constructor proves the offset and the initial value, not
what the field means at runtime. Nothing here is promoted or read.

### VehicleGun (size `0x64`)

| Offset | Init | Candidate reading |
|---|---|---|
| `+0x0` | primary vftable `0x32dacf4` | object identity |
| `+0x8` / `+0xC` | secondary sub-vtables (different addresses) | multiple inheritance |
| `+0x38` | `100.0f` | ctor/factory hardcoded default — refuted as per-instance config (see trace below) |
| `+0x3C` | `9` (int) | ctor/factory hardcoded default — refuted as per-instance config (see trace below) |
| `+0x40` | `1.0f` | ctor/factory hardcoded default — refuted as per-instance config (see trace below) |
| `+0x48` | allocated 8-byte object | listener/state; unproven |

### VehicleGunRotator (size `0x1f0`)

| Offset | Init | Candidate reading |
|---|---|---|
| `+0x0` | primary vftable `0x32eeb40` | object identity |
| `+0x10` | owner `this` (AvatarGameLogic) | reverse ownership pointer (proven) |
| `+0x84` | Vector3 `(0,0,0)` | possible aim/rotation state; unproven |
| `+0xEC` | 0x40-byte block zeroed | possible transform matrix; unproven |
| `+0x130` | ctor arg `param_4` (vehicle descr) | descriptor link |
| `+0x134` / `+0x138` | `-100500.0f` each, `+0x13C` = 0 | possible aim angle pair; unproven |
| `+0x1BC` | Vector3 `(-100500,-100500,-100500)` | possible aim/target sentinel; unproven |

The `-100500.0f` (`0xc7c44a00`) sentinel appears in both the `+0x134/+0x138`
scalar pair and the `+0x1BC` vector, so the rotator initializes aim-shaped
state to an explicit "no valid value" marker. Proving turret yaw vs gun
elevation vs aim point still requires tracing the write/read sites of these
fields through the rotator's vtable methods (the next memory-research step).

### Aim-state sync finding (refined — still not per-field proven)

A third hash-bound pass decompiled the rotator's vtable methods. Slot 1
(`FUN_01ac4f70`) reads a controller at **`rotator + 0x148`** (vtable methods
`+0x13c`/`+0x164`/`+0xa4`) and copies a struct into
**`rotator + 0x1A8..+0x1C0`**, including the constructor-sentineled `+0x1BC`.
A fourth pass decompiled the struct's producer (`FUN_01aa7140`): it is a
**batch/iteration computation** (integer `ceil(count/step)` over counts at
`param_3+0xec/+0xf4/+0xfc/+0x10c`), not clean angle math. So `+0x1A8..+0x1C0`
carries a batch-shaped state (flag + counts + list), and the per-field
semantics of the surrounding aim-shaped fields (`+0x84`, `+0x134/+0x138`,
`+0x1BC`) remain unproven. Static tracing alone cannot cleanly separate
**turret yaw vs gun elevation vs aim point**; that now requires the live
controlled-transition correlation G1 items 2/5 already mandate (downstream of
the walk's live validation).

### Configured-gun / loaded-shell static trace (2026-08-17)

A write/read-site trace (`TraceGunFieldAccess.java` and
`DumpGunLifecycle.java`, evidence `.build/ghidra-evidence-gun-fields/`,
hash-bound `1cda5c31…`) followed the `VehicleGun` field block through its
virtual methods, ctor, allocating factory, and the callers that create it.
Three findings:

1. **`+0x38 = 100.0f`, `+0x3C = 9`, `+0x40 = 1.0f` are class-level defaults,
   not per-instance configured-gun / loaded-shell identity.** Both the
   in-place ctor `FUN_01da8bb0` (`param_1[0xe] = 0x42c80000`,
   `param_1[0xf] = 9`, `param_1[0x10] = 0x3f800000`) and the allocating
   factory `FUN_01d9cc30` write the identical hardcoded constants for every
   instance, so they cannot carry the per-vehicle gun/shell state. This
   refutes the earlier "possibly reload/rate / shell count/caliber" guesses.

2. **The ownership chain is re-confirmed at the source.** `FUN_01683b00`
   (`AvatarGameLogic` ctor) sets the `+0x200` marker, `operator_new(100)` →
   `FUN_01da8bb0` → stores the gun at **`+0x204`** (`param_1[0x81]`) and
   zeroes `+0x208`. It does not overwrite the gun's `+0x38/+0x3C/+0x40` after
   construction, so the gun keeps its defaults at creation.

3. **The `GunStatusPresenter` path is a batch/iteration presenter, not the
   combat gun config.** `FUN_01da3f20` (GunStatusPresenter ctor) owns a
   `VehicleGun` (allocating-factory result stored at presenter `+0x4`) and
   computes a count through `FUN_01650750` =
   `ceil( *(desc+0x1a0) / *(desc+0x19c) )` — the same integer-ceil-over-counts
   batch pattern seen in the rotator producer, not a shell count.

`FUN_016ea010` (`VehicleGameLogic::onEnterWorld`) additionally confirms the
descriptor link: it reads the vehicle descriptor at **`+0x68`**
(`param_1[0x1a]`) and `descr->maxHealth` at `+0x34`.

**Conclusion:** the configured-gun and loaded-shell identity does **not** live
in the `VehicleGun` field block (`+0x38/+0x3C/+0x40` are defaults) nor in the
presenter's batch count. It must be applied at equip time from the gun
descriptor, or observed via the live controlled shell-swap. Per-field
configured-gun/loaded-shell semantics therefore remain unproven and now need
either a deeper gun-descriptor producer trace or the live controlled
shell-swap transitions G1 item 2 already mandates.

### Gun-descriptor architecture (2026-08-17)

An equip-writer trace (`TraceGunEquipWriters.java`) plus a symbol pass
(`ListGunSymbols.java`, evidence `.build/ghidra-evidence-gun-fields/`,
hash-bound `1cda5c31…`) resolve **where** the configured gun / loaded shell
actually live:

- **No gun-aware function writes the `VehicleGun` field block after
  construction.** The only writers of `+0x38/+0x3C/+0x40` are the ctor and
  allocating factory, which hardcode `100.0f / 9 / 1.0f`. There is therefore
  **no equip-time write** of the per-instance gun config into the 100-byte
  `VehicleGun` object (the `+0x38/+0x3C/+0x40` writes in the
  `AvatarGameLogic` ctor land on the avatar's own sub-objects, not the gun at
  `+0x204`).
- The configuration lives in separate **descriptor classes**, surfaced by
  RTTI and config-key symbols:
  - `Gun` (vftable RVA `0x31a7080`) — the gun descriptor; XML keys `maxAmmo`,
    `pumpGunMode`, `pumpGunReloadTimes`, and `Gun::GetShotsPerMinute`;
    parsed by `GunsReader::ParseBaseGunInfo`.
  - `Shell` (vftable RVA `0x31a1e14`) and `eShellKind` — the shell descriptor
    with the five shell kinds `ARMOR_PIERCING`, `ARMOR_PIERCING_CR`,
    `ARMOR_PIERCING_HE`, `HIGH_EXPLOSIVE`, `HOLLOW_CHARGE`; parsed by
    `ShellsReader`.
  - `Turret` / `TurretsReader` — the turret descriptor.
  - `VehicleDescr` (vftable RVA `0x31a3510`) — the vehicle descriptor with
    config sections `.chassi/.engine/.fuelTank/.turret/.gun` and
    `MakeConfigFromVehicle`; `s_vehicleGun`, `s_vehicleTurret`, `s_shells`.
- Aim angles are a separate concern: `CurrentGunAnglesComponent` /
  `DestinationGunAnglesComponent` and `GetGunAngle` / `SetGunAngle` /
  `GetTurretAngle` / `SetTurretAngle` / `turretRotation` are the
  yaw/elevation producers and consumers.

**Conclusion (refined):** `VehicleGun` is the runtime fire/reload state
machine; its `+0x38/+0x3C/+0x40` are hardcoded defaults (not loaded-shell
identity). The configured-gun identity and shell list live in the `Gun` /
`Shell` descriptors reachable from `VehicleDescr`. Per-field configured-gun /
loaded-shell semantics still need either the `Gun`/`Shell` descriptor field
layout + the runtime shell-index link, or the live controlled shell-swap.

### Gun/Shell descriptor field layout (2026-08-17, static)

A vtable decompile (`DumpDescriptorVtables.java`) plus a producer trace
(`TraceShellGunProducers.java`, evidence `.build/ghidra-evidence-gun-fields/`,
hash-bound `1cda5c31…`) names the descriptor fields. **Static evidence only —
nothing here is promoted; a live read is still required for promotion.**

**`Shell` descriptor** (vftable RVA `0x31a1e14`, size `0x158` = 344 bytes;
allocated inside `std::_Ref_count_obj2<Shell>`, so the `Shell` object starts
at `+0xC` of the `0x164`-byte allocation). Offsets are relative to the Shell
object base. Producer: `ShellsReader` attribute handler `FUN_00840570`
(`0x440570`), which writes the config keys below; defaults come from the
allocating factory `FUN_0083b650` (`0x43b650`).

| Offset | Type | Config key | Default | Meaning |
|---|---|---|---|---|
| `+0x114` | int (`eShellKind`) | `kind` | `0` | shell kind |
| `+0x118` | int | `caliber` | `0x7fffffff` | caliber (mm) |
| `+0x11c` | float | `damage.armor` | `-100500.0f` | HP damage to armor (per-shell damage) |
| `+0x120` | float | `damage.devices` | `-100500.0f` | device (module) damage |
| `+0x124` | float | *(none in handler)* | `0.25f` | **unproven** |
| `+0x128` | float | *(none in handler)* | `0.05f` | **unproven** |
| `+0x12c` | bool | `isTracer` | `false` | tracer shell |
| `+0x130` | string | `effects` | `""` | effects name |
| `+0x148` | float | `normalizationAngle` | `0.0` | normalization (deg→rad) |
| `+0x14c` | float | `ricochetAngle` | `0.0` | stored as `cos(angle)` |
| `+0x150` | float | `explosionRadius` | `-100500.0f` | HE splash radius (m) |
| `+0x154` | float | `piercingPowerLossFactorByDistance` | `0.0` | penetration falloff/m |

`eShellKind` (registered by `FUN_007c6780`): `0=kUnknown`,
`1=kHollowCharge` (HEAT), `2=kHighExplosive` (HE), `3=kArmorPiercing` (AP),
`4=kArmorPiercingHe` (APHE), `5=kArmorPiercingCr` (APCR).

Shell finalize (`FUN_00840480`, `0x440480`): when `kind == 2` (HE) and
`+0x150 <= 0`, derive `explosionRadius = caliber² / 5555.0f`
(`_DAT_035aa7dc == 5555.0f`). `ricochetAngle`/`normalizationAngle` are
multiplied by the degrees→radians constant `DAT_035a0e84` at parse time.

**`Gun` descriptor** (vftable RVA `0x31a7080`, size `0x21c` = 540 bytes).
Producer: `GunsReader::ParseBaseGunInfo` = `FUN_008120e0` (`0x4120e0`);
accessor `Gun::GetShotsPerMinute` = `FUN_0080bc20` (`0x40bc20`).

| Offset | Type | Config key | Meaning |
|---|---|---|---|
| `+0x114` | string | — | gun name/id |
| `+0x12c` | map | — | shell map (by id) |
| `+0x15c` | float | `impulse` | recoil impulse |
| `+0x174/+0x178/+0x17c` | Vector3 | `extraPitchLimits.front` | pitch-limit front |
| `+0x180/+0x184/+0x188` | Vector3 | `extraPitchLimits.back` | pitch-limit back |
| `+0x18c` | float | `extraPitchLimits.transition` | pitch-limit transition |
| `+0x190` | float | `rotationSpeed` | turret rotation speed |
| `+0x194` | float | — | reload parameter (fire rate) |
| `+0x19c`, `+0x1a0` | ptr (20 B) | — | burst count arrays |
| `+0x1ac` | ptr (16 B) | `turretRotation`/`afterShot`/`whileGunDamaged` | 4-float aim/recoil array |
| `+0x1b0..+0x1b8` | vector | — | `vector<Shot>` (Shot = `0x44` B) |
| `+0x1c0` | bool | `pumpGunMode` | pump/burst fire flag |
| `+0x1c4..+0x1cc` | vector<float> | `pumpGunReloadTimes` | pump reload times |

`Gun::GetShotsPerMinute` combines `+0x19c/+0x1a0` (burst counts), `+0x1c4`
(reload times), `+0x194`, and `DAT_035919f4 == 60.0f` (per-minute constant).

**Penetration is not a `Shell` field.** The `piercingPower` config key is
parsed in `GunsReader::ParseBaseGunInfo` (the `Gun` handler), not in the
`ShellsReader` handler, and is read as a space-separated float curve
(`FUN_00813a30` push into a temporary vector). The same `Gun` handler also
parses the per-shot ballistic entries of the `vector<Shot>` (`+0x1b0..+0x1b8`,
`Shot` = `0x44` B), writing `defaultPortion` → `Shot+0x24`, `speed` → `+0x28`,
`gravity` → `+0x2c`, `maxDistance` → `+0x30`, `isATGM` → `+0x40`. The exact
store offset of the parsed `piercingPower` curve is **not confirmed by this
pass** (the handler parses it into a temporary and the destination write is
not resolved), so penetration cannot yet be named to a concrete offset.

**Honest remaining gap:** (1) the runtime **shell-index link** — how the game
selects which `Shell` descriptor (and which `+0x11c/+0x120` damage pair) is
loaded into the shot path at fire time — is not yet derived; (2) the
`piercingPower` (penetration) destination offset. The static field names above
are producer-side; confirming them requires either the shot-path consumer
trace or the live controlled shell-swap.

