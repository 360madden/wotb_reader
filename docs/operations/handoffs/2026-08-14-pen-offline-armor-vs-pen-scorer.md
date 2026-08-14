# 2026-08-14 — Pen: offline armor-vs-pen scorer + raw-descriptor resolution, first model-vs-ground-truth result

## Summary

The pen feature's offline PN-4 lane advanced from "blocked" to a shipped,
real-data scoring pipeline with its first honest model-vs-ground-truth
result — the same scoring core the live CAM-013 validation will feed.

## What shipped

1. **Per-tank penetration lane** — `IOverlayPenetrationData.ResolveTankAsync`
   (new `PenetrationTankData` record) resolves armor + collision mesh + stock
   shells for ANY roster tank (not just the viewer). `PenetrationDataService`
   gained the shared `BuildShellOptionsAsync` (the viewer-shell lane now
   delegates to it) and an identity-ensure refactor.
2. **Raw compact-descriptor resolution** — the store's `tank_id` is a mix of
   `nation:tank` and un-enriched integer descriptors; the service now resolves
   integer descriptors through `IInstalledGameMetadataProvider`
   (descriptor → `nation:tank`), which is what makes the 69-shot session
   resolvable at all. `PenetrationDataService` ctor gained the metadata
   provider (DI + 4 test sites updated).
3. **`PenOfflineScorer` (Application)** — for every decoded `ShotImpact` event
   with an attacker: resolve both tanks, nearest at-or-before position
   samples, attacker→victim center-line aim (+1.5 m gun height), attacker's
   stock shell, victim mesh+armor, and score with the existing
   `PenValidation.Score`. Fail-closed per shot with a localization reason
   (no attribution / unresolvable tank / no sample / coincident positions).
4. **`GET /api/v1/game/discover/pen-offline-score/{sessionId}`** — loads the
   projection and returns the report (validation summary + per-shot rows).
   Registered as `IPenOfflineScorer` (Application DI) + published port.
5. **Tests** — 5 scorer tests (Pen/NoPen front-quad geometry, skip paths,
   empty), 4 service tests (per-tank, descriptor lane, unknown tank, no
   install), 2 endpoint tests (report + 404).

## First real result (session `019ffdcd`, the 69-shot ground truth)

- 67 scored / 2 skipped (no position sample at shot time).
- 46 Unknown — the struck faces are SIDE hits; the nominal side armor is
  0/unknown by design (install XML declares front only). Verified the mesh
  raycast itself hits those faces correctly (Python re-raycast with the exact
  scorer inputs: side-face hits at 62–75° incidence).
- 18 determinate Pen/NoPen → **7 agreements, 38.9% band accuracy**.
- All 6 predicted ricochets (steep sloped-plate hits, incl. 87.5° at ~30 m)
  actually PENETRATED (ricochet precision 0).

The disagreements are exactly the documented offline limits (center-line aim,
stock-shell proxy, front-only nominal armor) — now quantified instead of
asserted. The endpoint is the harness the live CAM-013 aim will feed with the
aim source swapped.

## Honest limits

- The offline center-line aim cannot validate the ricochet rule (angle
  unknowable offline — reconfirmed numerically); live PN-4 remains the proof.
- Side/rear nominal armor stays unknown (fail-closed Unknown verdicts).
- The attacker's loaded shell is unknown; the stock first shell is the proxy.

## Validation

Focused suites green: GameIntegration 7 (1 opt-in skip), Application 5,
Host.Web 2, Bootstrap 14. Full `scripts/validate.ps1` gate: **exit 0** —
1088 tests passed, 0 warnings, 0 errors (see the gate run in the commit).

## Next

Live PN-4 (CAM-013 aim at shot time → same scorer) is the single remaining
pen proof; it needs one approved launch. Re-decode of other sessions would
extend the offline dataset.
