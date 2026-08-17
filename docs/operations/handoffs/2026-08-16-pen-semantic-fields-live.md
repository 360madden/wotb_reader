# Penetration v0.3 — semantic-field live snapshot (first replay)

**Date:** 2026-08-16 (UTC)
**Status:** two-replay elevation isolation positive; published-marker
dir Y/Z convention applied at read; rotator+0x11C matrix T live no-go
(two replays); magazine count=3 / mixed kinds 2/3/5; Oasis full-hunt
932 samples index stayed 0 / kind 3 while reload toggled; type-32
6-byte field is per-shot unique (not eShellKind); nothing promoted
**Blocker:** `BLK-0027` (open — start pose is stack-only; mixed
occupancy is not a proven selection; exact armor remains no-go)

## What changed

Additive `EntityRecordRegionAnchor.PenSemanticFields` on the existing
entity-region endpoint (no new public route). After the ownership walk
resolves, the coordinator two-pass-reads:

- published gun-marker at `rotator+0x50` (7 float32)
- VehicleGun reload/state from `+0x3C` (20 bytes)
- entity-base hull yaw at `+0x50`

`RegionBytes` stays null. The response carries only walk booleans, finite /
unit / range / stability flags, the reload enum, and investigation
yaw/pitch diagnostics. Script
`scripts/capture-pen-semantic-fields.ps1` samples the anchor and prints
counts plus 16-sector yaw bins (no addresses or raw coordinates).

## What ran

One exact-build managed offline replay reached `OfflineReplayVerified`
(battle session `01a00cf2-358b-75b4-8186-579afba06758`). Two capture
passes ran in-session:

| Pass | Samples | Walk | Reload in 0..9 | Marker finite/unit/stable | Marker yaw bins | Hull yaw bins | Independent windows |
|---|---:|---:|---:|---:|---:|---:|---:|
| 1 | 16 | 16 | 16 (enums 0,9) | 16/16/16 | 1 | 1 | 0 |
| 2 | 40 | 40 | 40 (enums 0,3) | 40/40/40 | 4 | 1 | 8 |

Pass 2 is the T1 window: hull yaw stayed in one 16-sector bin while the
published marker direction crossed four bins, with eight same-hull /
changed-marker steps. Reload enum changed (`0`/`3`/`9`) and is therefore
not a loaded-shell identity.

## Honest limits

- One replay only. A second content-distinct positive is required.
- Elevation was not isolated (no pitch-only window).
- No decoded shot join. The marker is a live client gun-marker, not a
  proven muzzle origin.
- CAM-013 was not used as a success criterion.
- Nothing was promoted. The badge stays `NotReady`.

## Validation

- Coordinator: 11 focused tests (walk + two new semantic-field cases).
- Host.Web: 1 new endpoint parse/echo test.
- Live: `OfflineReplayVerified` + 56/56 snapshots, 0 sample errors.
- Game and host were stopped after the reads.

## Next step

Repeat the same script on a second content-distinct replay. Then add
pitch-only isolation and a decoded shot join before any G2 contract.

## Amendment — second content-distinct replay (`2026-08-16`)

A second managed offline replay (distinct file size 1045525 vs 1100265;
battle session `01a00cf7-895b-7653-8fa1-07d0fff73310`) reached
`OfflineReplayVerified`. One 40-sample pass:

| Samples | Walk | Reload in 0..9 | Marker finite/unit/stable | Marker yaw bins | Hull yaw bins | Independent windows |
|---:|---:|---:|---:|---:|---:|---:|
| 40 | 38 | 38 (enum 0) | 38/38/38 | 6 | 1 | 10 |

Two samples were honest non-confirms (walk not fully Resolved). The 38
positives reproduce the first replay: published marker stays finite/unit/
two-pass-stable and its yaw bins move while hull yaw stays in one bin.
`twoReplayRepeatability` for that hull-independent marker-yaw signature is
now true. Elevation isolation, shot join, and loaded shell remain open.
Nothing was promoted. Game and host were stopped after the read.

## Amendment — elevation isolation + first shot-join attempt (`2026-08-16`)

The capture script now bins published marker pitch over `[-pi/2, pi/2]` and
persists G2-attested yaw/pitch samples. `PublishedMarkerShotJoin` plus CLI
`marker-join` count viewpoint-attacker ShotImpact joins (10 deg, default
250 ms lag). This is an angular/clock join, not ExactGunRay.

One denser pass (battle `01a00d08-b886-7f86-b97d-6eb1eb60d0e1`, 64 samples
at 200 ms request cadence):

- walk 62/64; marker finite/unit/stable 62; turret-independent windows 9
- **elevation_independent_windows = 5** (hull 1 yaw bin, marker pitch 8 bins)
- g2_clock_samples = 60; persisted sample replay-time span ~8-119 s
- marker-join: viewpointShots=5, joined=0, lagExceeded=5 at 250 ms and at
  10 s. The 5 viewpoint shots did not fall inside a 10 s at-or-before
  window of the persisted G2 span. Honest: shot-synchronous join is not
  shown; the AOB-per-sample capture is too coarse and/or ended before
  those shots.

Nothing promoted. Elevation isolation is a first-replay script positive
and still needs a content-distinct repeat. Shot join remains open.

## Amendment — walk cache, second-replay elevation, shot-window join (`2026-08-16`)

The coordinator now reuses a confirmed ownership-walk rotator across
samples: process-local cache keyed by authorization generation, module
base, candidate index, and expected vftable. The next sample re-reads
the rotator's vftable under the lease and re-runs the five-read chain;
mismatch drops the cache and AOB-scans again. Addresses never leave.
`marker-shots` lists viewpoint ShotImpact replay-seconds. The capture
script takes an elevation stretch, then waits on the G2 clock for the
first viewpoint shot.

One content-distinct Oasis Palms managed offline replay (1045525-byte
original vs the Dead Rail elevation pass on `01a00d08`; battle
`01a00d22-c7b8-73ce-94a7-adc9ff853fdf`) reached `OfflineReplayVerified`.

Elevation pass (24 samples, then a later 128-sample window):

- walk 63/64 then 125/128; marker finite/unit on every sample
- **elevation_independent_windows = 6** then **8** (hull 1 yaw bin,
  marker pitch 5 bins)
- `twoReplayRepeatability` for hull-stable / pitch-changing isolation
  is now true (Dead Rail 5 windows, Oasis 6+)

Shot join on the merged G2 persist (188 samples, span ~23-262 s; 7
viewpoint shots at 177.8, 245.4, 253.2, 253.2, 260.3, 267.1, 274.0 s):

- 5/7 shots had an at-or-before sample within 250 ms (118, 71, 18, 18,
  158 ms)
- 2/7 late shots were outside the persist span (`lagExceeded`)
- `marker-join` at 250 ms and 1000 ms: viewpointShots=7, joined=0,
  lagExceeded=2. The five clock-covered shots reached the 10° check and
  all missed. Honest: capture can now be shot-synchronous; the published
  marker is not the 10° attacker-hull-to-victim center line. Not
  ExactGunRay. CAM-013 was not a success criterion.

Nothing promoted. Badge stays `NotReady`. Game and host were stopped
after the reads. Reload enums `0`/`3` again — still not a shell id.

## Amendment — Y/Z convention + Dead Rail repeat (`2026-08-16`)

`marker-join` now emits count-only degree diagnostics (center-1.5,
center-1.9, Y/Z-swapped marker dir, median/max). ShotImpact has no
decoded hit point; the center-line remains a proxy, not ExactGunRay.

Oasis persist (session `01a00d22`, 5 compared shots):

| Hypothesis | Joined ≤10° | Median deg | Max deg |
|---|---:|---:|---:|
| Center 1.5 m / 1.9 m | 0 / 5 | 22.5 | 60.3 |
| Marker dir Y/Z swapped vs 1.5 m | **4 / 5** | **3.8** | 33.9 |

Dead Rail managed replay (1100265-byte original, battle
`01a00d32-fef4-7b9c-b726-e96758ad9c53`, `OfflineReplayVerified`; 128
walk-confirmed samples; 2/5 shots inside 250 ms):

| Hypothesis | Joined ≤10° | Median deg | Max deg |
|---|---:|---:|---:|
| Center 1.5 m / 1.9 m | 0 / 2 | 87.5 | 88.4 |
| Marker dir Y/Z swapped vs 1.5 m | **2 / 2** | **1.9** | 3.6 |

The published `rotator+0x50` direction floats are in the CAM-010 engine
convention `(x, z, y-up)`. After swapping Y/Z they match decoded
`(x, y-up, z)` hull-to-victim at shot time on two content-distinct
replays (6/7 clock-covered shots ≤10°). One Oasis miss stayed at 33.9°
even after the swap — honest (proxy target or a shot not aimed at hull
center).

Not ExactGunRay: muzzle origin is unread; the join target is still the
center-line proxy; coordinator yaw/pitch diagnostics stay on the raw
byte convention until an owner-reviewed read-time swap. CAM-013 was not
a success criterion. Nothing promoted.

Loaded shell: `+0x3C` remains a reload enum. Exact armor remains a v0.3
no-go until a producer is named. Game and host were stopped after the
Dead Rail read.

## Amendment — read-time Y/Z swap + hull-relative origin (`2026-08-16`)

The coordinator now converts published marker pos/dir from engine
`(x, z, y-up)` to decoded `(x, y-up, z)` before yaw/pitch (the
two-replay shot-join convention). It also two-pass-reads entity-base
hull position and reports only hull-relative origin scalars: height,
horizontal |XZ|, in-band (0.3–4.0 m height, ≤6 m horizontal), and
rel X/Y/Z for an origin-to-victim join. World XYZ are not logged.

`marker-join` accepts optional `originRel*` on persist samples and
counts `joinedOriginToVictim`. Not ExactGunRay until a live origin
band + origin-to-victim repeat. Nothing promoted.

## Amendment — first live origin read (`2026-08-16`)

Oasis Palms battle `01a00d40-5be8-7e46-addb-a6935ca968ea`
(`OfflineReplayVerified`, 64/64 walk-confirmed, marker finite/unit/stable).
After the read-time Y/Z swap:

- `origin_in_band=0` on all 64 G2 samples
- hull-relative height median ~41 m (min ~38, max ~43)
- hull-relative horizontal median ~133 m (min ~79, max ~231)

The published `+0x50` **position** is not a muzzle next to the hull. It
behaves like a distant gun-marker / aim point (GetGunMarkerPosition).
One clock-covered viewpoint shot joined at 33.9° to the hull center-line
(same order as the prior Oasis miss). `joinedOriginToVictim=0`.

Honest: direction convention stands; muzzle origin is **not** the
published pos3. A different origin field is required. Nothing promoted.
Game and host were stopped.

## Amendment — GetGunMarkerPosition start reconstruction (`2026-08-16`)

Hash-bound `FUN_01ec12b0` (`GetGunMarkerPosition`) ray-marches from a
start pose produced by `FUN_0133a410` and publishes the **hit** at
`rotator+0x50`. The decompile writes
`scalar = 2 * |hit-start| * param3`. `GunMarkerMuzzle.TryReconstructStart`
implements that formula. The coordinator scores two param3 hypotheses
(1.0 ⇒ distance=scalar/2, 0.5 ⇒ distance=scalar) as hull-relative
in-band flags. The start pose itself is stack-local in `FUN_01ed2040`
and is not a second published field at `+0x50`.

Dead Rail `01a00d53` (`OfflineReplayVerified`, 24/24 walk-confirmed):

- published-pos in-band 0/24
- reconstruct param3=1 in-band **0/24**
- reconstruct param3=0.5 in-band **0/24**
- one snapshot: published height ~27.5 m / horizontal ~207 m; both
  reconstructions differed by centimeters (published 7th float is not a
  long `2*|hit-start|` range)

Honest: start reconstruction from the published scalar is a **no-go**.
Next static follow is `FUN_0133a410` / rotator `+0xEC` matrix — do not
live-read an unproven translation slot. Nothing promoted. Game and host
stopped.

## Amendment — rotator+0x11C matrix T live no-go (`2026-08-17`)

Hash-bound `FUN_0133a410` (RVA `0xF3A410`) writes:

- start **position** at output+0 (result of `FUN_0271a330` transforming a
  derived point by the matrix at output+0x18)
- start **direction** at output+0xC
- the 64-byte matrix at output+0x18

`FUN_01ed2040` / `FUN_01ec1030` copy that matrix to `rotator+0xEC`.
`FUN_0271a330` treats matrix+0x30 as translation, so T sits at
`rotator+0x11C`. The start pose itself is **not** stored on the rotator:
`FUN_01ed2040` feeds it to `GetGunMarkerPosition`; `FUN_01ec1030` writes
the six floats into a caller buffer (camera path `FUN_01dd7830` keeps
that buffer on the stack).

The coordinator now two-pass-reads the named 12-byte T slot, converts
engine `(x, z, y-up)` to decoded space, and reports hull-relative
height / horizontal / in-band only. 6 coordinator + 1 HTTP tests cover
the read. Capture prints `matrix_origin_in_band`.

Oasis Palms original (1045525 bytes; battle
`01a00d60-3170-7473-b2bd-92cd6ae19314`) reached
`OfflineReplayVerified`. One 32-sample unaligned pass:

| Samples | Walk | Marker finite/unit/stable | G2 | origin_in_band | muzzle reconstruct | matrix_origin_in_band |
|---:|---:|---:|---:|---:|---:|---:|
| 32 | 32 | 32/32/32 | 32 | 0 | 0 / 0 | **0** |

Hull-relative matrix T was constant: height −145.08 m, horizontal
226.88 m (published +0x50 origin stayed far: height 37.62 m /
horizontal 231 m). That is not a muzzle. Magnitude is consistent with a
near-zero T minus world hull, but that zero-T reading is a hypothesis
— the persist does not carry raw T.

Honest: `rotator+0x11C` is **not** ExactGunRay origin. Do not promote
it. Do not re-read it as a muzzle. Next static is the
`rotator+0x130` object graph `FUN_0133a410` uses to build the
transformed start point (`[+0x10]+0x124` and `[[+0x0c]+0x20]`).
Loaded shell and exact armor remain no-go. Nothing promoted. Game and
host were stopped after the read.

## Amendment — rotator+0x130 is vehicleTypeDescriptor (`2026-08-17`)

Hash-bound follow of `FUN_01eb6ba0` (VehicleGunRotator ctor) and its
only caller `FUN_016d1b60` (avatar-marker `+0x200` path):

- ctor `param_4` is stored at `rotator+0x130` and asserted
  `nullptr != vehicleDescr`
- that pointer is `vehicle+0x68`, the same object
  `AmmoController::ResetAmmo` (`FUN_015eff70`) copies into
  `AmmoController+0x40` under the string `vehicleTypeDescriptor`
- AmmoController is embedded at `AvatarGameLogic+0x4B4`

`FUN_0133a410` then reads static descr nodes, not a live pose:

| Slot | Use |
|---|---|
| descr+0x0c | node with float3* at +0x20 and quaternion at +0x2c |
| descr+0x10 | node with translation at +0x124/+0x128/+0x12C |
| descr+0x1c | turret-like descr; XML `gunPosition` is written at +0x140 (`FUN_007c2ca0` / `FUN_007fe8e0`, `VehicleDescr.cpp`) |
| descr+0x20 | weapons/ammo tables (`AmmoController` uses +0x19c/+0x1a0/+0x1b0) |

Start world pose remains stack-only: live aim matrix composed with
those static points. Do not live-read `+0x130` as a muzzle.

`AmmoController::ProcessCurrentShells` (`FUN_015ef402`) writes the
matched compact-shell **index** at `AmmoController+0x38` (`owner+0x4EC`).
0 is also the no-match default. The coordinator now fail-open
two-pass-reads that index and a descr pointer round-trip
(`AmmoController+0x40` == `rotator+0x130`). Addresses never leave.
7 coordinator + 1 HTTP tests. Capture prints `ammo_index_*`.

Not a loaded-shell identity (no A->B->A, no named +0x20/+0x24 compact
fields). Exact armor remains no-go. Nothing promoted. Next live is the
named index snapshot only.

## Amendment — AmmoController index live (`2026-08-17`)

Dead Rail original (1100265 bytes; battle
`01a00d6f-6c13-79f5-a4f0-a6955140263c`) reached
`OfflineReplayVerified`. One 32-sample unaligned pass:

| Samples | Walk | Marker finite/unit/stable | G2 | matrix_origin_in_band | ammo_index_in_range | ammo_descr_round_trip | ammo_index_values |
|---:|---:|---:|---:|---:|---:|---:|---|
| 32 | 32 | 32/32/32 | 32 | 0 | **32** | **32** | **0** |

The descr pointer round-trip is live: `AmmoController+0x40` equals
`rotator+0x130` on every sample. The current-shell index is stably 0,
which `ProcessCurrentShells` also writes on no-match. Honest: this
replay/window does not distinguish "first compact shell" from
"unmatched". `+0x11C` remains out of band (second-replay repeat of
the T no-go).

Not loaded-shell identity. Do not promote. Next is compact-shell
`+0x20/+0x24` static (the compare keys) or an A->B->A window. Exact
armor remains no-go. Game and host were stopped after the read.

## Amendment — compact-item nation/id named + live (`2026-08-17`)

`VehicleDescr::MakeDescrWithBestAvailableItems` prints a compact item
as `type` (`+0x04`), `nation` (`+0x20` via `FUN_007f8d30`, table
0..8/10/15), and `id` (`+0x24`). Those are the same two dwords
`ProcessCurrentShells` compares. They are **not** `eShellKind`
(AP/HE/HEAT).

The coordinator fail-open walks
`descr+0x20 -> weapons+0x1B0[index] -> +0x1C -> +0x20/+0x24` and
reports nation/id only. 7 coordinator + 1 HTTP tests.

Oasis Palms original (1045525 bytes; battle
`01a00d7a-7095-7ed2-8c8c-e824d9941dea`) reached
`OfflineReplayVerified`. One 32-sample unaligned pass:

| Samples | Walk | descr RT | index | ident readable | nation in range | nation | id nonzero |
|---:|---:|---:|---|---:|---:|---|---:|
| 32 | 32 | 32 | `{0}` | **32** | **32** | **5** | **32** (id **71**) |

Slot 0 is a real compact item (nation 5, id 71), not an empty
no-match. Index 0 is still ambiguous as a *selector*, but the slot
itself is populated. Not eShellKind. Not ExactLoadedShell. `+0x11C`
still 0/32. Nothing promoted. Game and host were stopped.

Next: map (nation, id) to a shell kind, or find `eShellKind` on the
ident object. Exact armor remains no-go.

## Amendment — eShellKind at Shell+0x114 (`2026-08-17`)

`FUN_00840570` (Guns.cpp) parses XML `kind` through the `eShellKind`
table (`FUN_007c6780` / `FUN_0090b930`) and stores the enum at
`Shell+0x114` (`MOV [EDI+0x114], ESI`). Registry:

| Value | Name |
|---:|---|
| 0 | kUnknown |
| 1 | kHollowCharge |
| 2 | kHighExplosive |
| 3 | kArmorPiercing |
| 4 | kArmorPiercingHe |
| 5 | kArmorPiercingCr |

The compact ident already live-proven for nation/id is that Shell
(VehicleComponent base). The coordinator now two-pass-reads `+0x114`.
7 coordinator + 1 HTTP tests.

Dead Rail original (1100265 bytes; battle
`01a00d86-a310-7965-a2a2-0c4248b0951b`) reached
`OfflineReplayVerified`. 32/32 walk, ident, kind in-range, kind
**known** (not 0):

| Index | Nation | Id | Kind |
|---|---|---|---|
| `{0}` | 5 | 71 | **3 (kArmorPiercing)** |

Same nation/id as Oasis slot 0 (likely the same first magazine shell).
This is the **first-slot XML kind**, not a proven current-selection
identity: index stayed 0 and there was no A->B->A. Not
ExactLoadedShell. Nothing promoted. Game and host were stopped.

Next: a content-distinct vehicle or an A->B->A selection window.
Exact armor and stack-only start remain.

## Amendment — magazine count live (`2026-08-17`)

`AvatarGameLogic::updateVehicleSetting` (`FUN_016f3b20`) uses
`owner+0x4EC` as the compact-shell index after
`ProcessCurrentShells`. The weapons table `+0x1B0..+0x1B4` pointer
array length is the magazine count (capped at 16). The coordinator
now reports that count even when the current index is 0.

Dead Rail original (1100265 bytes; battle
`01a00d92-f1e1-7235-a56c-175731916b70`) reached
`OfflineReplayVerified`. One 32-sample unaligned pass:

| Samples | Walk | Index | Nation | Kind | Magazine count |
|---:|---:|---|---|---|---|
| 32 | 32 | `{0}` | 5 | 3 | **3** |

These Churchills are not a single-shell loadout. Index 0 is one of
three slots. Persist 32/32 count=3. Still no A->B->A. Not
ExactLoadedShell. `+0x11C` 0/32. Nothing promoted. Game and host
were stopped after the read.

## Amendment — mixed magazine occupancy (`2026-08-17`)

The coordinator fail-open walks every compact-shell slot and reports
an eShellKind occupancy mask plus the number of in-range readable
slots. That is XML magazine occupancy, not the current selection.

Oasis Palms original (1045525 bytes; battle
`01a00d96-5abe-7281-8c14-f03689d560aa`) reached
`OfflineReplayVerified`. Three unaligned bursts (32, then 64, then
64 after 90 s):

| Burst | Walk / G2 | Count | Readable slots | Kind mask | Kinds present | Index | Current kind |
|---|---:|---|---|---|---|---|---|
| 1 | 32/32 | 3 | 3 | 44 | 2, 3, 5 | `{0}` | 3 |
| 2 | 60/64 | 3 | 3 | 44 | 2, 3, 5 | `{0}` | 3 |
| 3 | 59/64 | 3 | 3 | 44 | 2, 3, 5 | `{0}` | 3 |

Kind 2 = kHighExplosive, 3 = kArmorPiercing, 5 = kArmorPiercingCr.
Slot 0 stays AP; the other two slots are HE and APCR. Magazine
count=3 now has `twoReplayRepeatability` (Dead Rail `01a00d92` +
Oasis `01a00d96`). Occupancy mixed is one-replay (Oasis) — the Dead
Rail host did not yet emit the mask.

Last persist: 59 G2 samples, all count=3 / mask=44 / index=0 /
kind=3. Mid-battle windows still did not switch. ShotImpact JSON
still has no decoded shell type.

Not ExactLoadedShell. Do not promote. Next is an A->B->A window on
this mixed magazine (index `0 -> n -> 0` with kind `3 -> 2|5 -> 3`)
or a content-distinct vehicle. Exact armor and stack-only start
remain. Game and host were stopped after the reads.

## Amendment — type-32 signature is per-shot (`2026-08-17`)

ShotImpact now carries additive `shellSignatureHex` (the pinned 6-byte
type-32 field at +0x0C / +0x0D). ReplayInspector and `marker-shots`
report distinct viewpoint-signature counts.

Both Churchill originals decode as **one unique hex per viewpoint
shot**:

| File | Map | Viewpoint shots | Distinct signatures |
|---|---|---:|---:|
| 1045525 | savanna | 7 | 7 |
| 1100265 | medvedkovo | 5 | 5 |

That field is a per-shot token, not eShellKind. It cannot prove a
shell switch. ShotImpact still has no decoded shell type.

## Amendment — Oasis full-battle index hunt (`2026-08-17`)

Capture gained `-HuntIndexChange` (keep sampling until two distinct
indexes, clock stall, or max). Oasis original (1045525; battle
`01a00e21-ae6b-7539-bbbe-1c033e086ad5`) reached
`OfflineReplayVerified`. One hunt:

| Samples | Walk / G2 | Index | Kind | Count | Mask | Reload enums | Clock span |
|---:|---:|---|---|---|---|---|---|
| 932 | 926 | `{0}` only | 3 only | 3 | 44 (2/3/5) | 0 (804) and 3 (122) | 14.1–219.2 s |

They fired (reload 0/3, 122 samples in state 3) while the compact-shell
index stayed 0 and current kind stayed AP. Persist 926 G2 samples, all
index 0 / kind 3. Hunt hit the 900-sample cap at 219 s — later
decoded viewpoint shots on this file sit near 245–274 s and were not
covered.

Not A->B->A. Not ExactLoadedShell. These two local originals are the
only replays on disk; neither has shown an index change. Next is a
late-window hunt (245 s+) on this file, or a new replay that actually
switches. Exact armor and stack-only start remain. Game and host were
stopped after the read.
