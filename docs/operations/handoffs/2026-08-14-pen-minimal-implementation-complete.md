# Pen feature — minimal implementation COMPLETE; live PN-4 aim feed wired

**Date:** 2026-08-14
**Commits:** (staged, not yet committed) — aim-feed endpoint + runbook
**Roadmap:** Phase 6 (`docs/operations/product-roadmap.md`, row PN-4)

## The one-liner

The pen-chance HUD's **minimal implementation is complete**: badge + model +
parsers all shipped across PN-1/2/3/5, and the offline scorer has validated the
model against three content-distinct replays. The single remaining item is the
**live PN-4 validation proof**, and this turn closed the last code gap before
that session — the aim-override feed is now reachable over HTTP, and the turnkey
runbook is pre-staged.

## What changed

1. **Aim-override feed endpoint** — `GET /discover/pen-offline-score/{id}` is
   now `POST` with an optional body. `PenOfflineScoreRequest.AimOverrides`
   (a list of `{replayTimeTicks, originX/Y/Z, directionX/Y/Z}`) maps to the
   scorer's `AimSample` list. The scorer already re-normalizes non-unit
   directions and only consumes overrides for the VIEWPOINT tank's own shots
   (non-viewpoint shots keep the center-line proxy, fail-closed). Empty body /
   no body = the unchanged offline center-line behavior.
2. **DTOs** — `PenOfflineScoreRequest` + `AimSampleRequest` in
   `WotBTreader.ApiContracts/HudContracts.cs` (self-contained primitives, no
   Core/Application dependency).
3. **Tests** — the fake scorer now records `LastAimOverrides`; the two existing
   endpoint tests pass `request: null` (asserting the center-line path is
   unchanged), and a new `ScorePenOffline_ForwardsAimOverridesToScorer` test
   pins the full mapping (ticks → `TimeSpan`, origin/direction values).
4. **Runbook** — `docs/operations/pn4-live-aim-capture-runbook.md` is the
   turnkey live plan: poll `/discover/camera-pose` (~10 Hz), reconstruct the
   aim exactly as `LiveFrameProjector.BuildCamera` does (eye = (X,Z,Y) yz-swap,
   forward = −row1 = −basis[3..5]), key each by the G2-anchored live-frame
   clock, then POST the accumulated aims after the battle. Pass criteria =
   `ricochetPrecision` improving over the offline center-line baseline.

## Why this closes the feature (minimal scope)

- The badge renders in BOTH replay and live frames (PN-3/PN-5).
- The pen math + install-data parsers are done and unit/opt-in-tested (PN-1/PN-2).
- The offline scorer already produced the first model-vs-ground-truth numbers
  (38.9% / 69.6% / 71.9% across savanna / medvedkovo / karieri), quantifying
  the documented center-line aim limit instead of asserting it.
- The ONLY remaining proof (true-aim validation) now has its feed path wired
  and its runbook pre-staged — a live session needs no code change.

## Remaining (launch-gated)

- **The live PN-4 session itself** (1 launch): run the capture loop, POST the
  aims, record the per-shot table and the improved `ricochetPrecision` /
  `bandAccuracy` vs the offline baseline. The runbook is the exact plan.
- Optional stretch (still CLOSED): loaded-shell decode — the 6-byte signature
  is an effect-entity id, not a stat reference; the manual shell selector
  remains the honest path.

## Validation

- `dotnet build` 0 warnings / 0 errors.
- Host.Web tests 174 passed (+1 new aim-override passthrough); full
  `scripts/validate.ps1` gate **exit 0** (all suites green: Core 274,
  Application 96, GameIntegration 340+5 opt-in skips, Host.Web 174, etc.;
  Pester 16/16 + 7/7; offline pack 65 files / 118 links / 0 broken).
