# Penetration v0.3 — semantic-field live snapshot (first replay)

**Date:** 2026-08-16 (UTC)
**Status:** two-replay elevation isolation positive; published-marker
direction matches the hull-to-victim line after a Y/Z swap (Oasis 4/5,
Dead Rail 2/2); nothing promoted
**Blocker:** `BLK-0027` (open — muzzle origin unproven; loaded shell and
exact armor remain no-go)

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
