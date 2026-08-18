# PN — live-aim override seam, third-replay validation, and research closures (2026-08-14)

## What landed

1. **Live-aim override seam** (`IPenOfflineScorer.ScoreAsync` gains an
   optional `IReadOnlyList<AimSample>`). The scorer now resolves the
   viewpoint tank's entity id from `projection.Session.ViewpointParticipantId`
   and, for shots whose attacker IS the viewpoint tank, takes the aim from
   the nearest `AimSample` at-or-before the shot time (falling back to the
   center-line when none exists). Every other shot keeps the center-line
   proxy. This is the decisive PN-4 primitive: the live CAM-013 chase-camera
   aim (the viewer's aim) is the one aim source the replay stream cannot
   provide, and it now plugs into the existing `PenValidation.Score` core
   with no other change. 2 new tests (`UsesAimOverrideForViewpointShot`,
   `IgnoresAimOverrideForNonViewpointShot`); endpoint keeps passing `null`
   (unchanged surface).

2. **Third-replay validation** — the karieri artifact (`019fc447`) was
   re-decoded (`01a0007f`, 100 `ShotImpact`, 13/14 enriched) and scored:
   **71.9% band accuracy** (32 classified / 23 agree, ricochetPrecision 0/3).
   Three content-distinct replays now span 38.9% → 69.6% → 71.9%, all
   pointing at the same conclusion: the center-line aim is the confound,
   not the pen model.

3. **Outlier verdict (#7)** — the 87.5° (savanna) / 72.5° (karieri)
   predicted-ricochet hits are CONFIRMED center-line artifacts, not mesh-face
   misclassification: the mesh raycast hits the right faces (the side-hit
   Unknowns prove the geometry), the center-line incidence is simply not the
   true shot angle. Only the live camera aim fixes this.

4. **Research closures (#3, #4)** —
   - Loaded shell is NOT recoverable: the type-32/subtype-8 6-byte signature
     is an effect-entity id (0x30xxxx), not a stat reference; the manual
     shell selector stays the honest path.
   - The `17425` holdout is VERSION DRIFT, not a DLC gap: `ResourceOverlay`
     is already DLC-first, and `17425` (ussr vehicle-type 68) is absent from
     every install list.xml (the DLC packs carry no ussr list). No DLC-list
     change helps it.

## Verification

- Build green, 0 warnings.
- `WotBTreader.Application.Tests` 94 pass (2 new), `WotBTreader.Host.Web.Tests`
  173 pass.

## Remaining (the only open pen work)

- **Live PN-4 session** (1 launch): capture the CAM-013 chase-camera aim at
  each shot time and feed `ScoreAsync(…, aimOverrides, …)`. The scorer + seam
  are ready; only the live aim capture remains. Live badge regression folds
  into the same launch.
