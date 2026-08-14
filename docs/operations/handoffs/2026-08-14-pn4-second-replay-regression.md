# PN-4 second-replay live regression — PASS

**Date:** 2026-08-14 (UTC)
**Roadmap:** Phase 6 (`docs/operations/product-roadmap.md`)
**Feature:** armor-penetration chance badge
**Live session:** Oasis Palms / Churchill ground-truth replay; battle session `01a00168-8dad-7e23-a1fd-e23b3e712b37`
**Related evidence:** `2026-08-14-pn4-live-aim-validation.md`

## Result

The second content-distinct live smoke passed. It exercised the same managed
launcher, verified Watch Offline gate, CAM-013 capture, terminal completion
recognition, aim feed, and scorer against the Oasis/Churchill session after the
Dead Rail proof. This removes the concern that the first result was specific
to one roster or one replay.

| Input | Total shots | Classified | Skipped | Band accuracy | Ricochet precision | Predicted ricochets |
|---|---:|---:|---:|---:|---:|---:|
| Center-line baseline (`{}`) | 67 | 18 | 2 | 38.889% | 0.000% (0/6) | 6 |
| CAM-013 aim overrides | 67 | 15 | 2 | **46.667%** | 0.000% (0/0) | 0 |

The true-aim report changed three rows at approximately 260.3 s, 267.1 s, and
274.0 s. All three were the same center-line artifact family: the baseline
predicted an 87.5-degree ricochet, while the CAM-013 aim removed that false
ricochet prediction. The three rows became honest deterministic classifications
rather than unsupported ricochet claims. The classified-row reduction is
expected: the true aim exposed additional unsupported nominal armor faces,
which remain Unknown instead of being guessed.

## Capture reliability

- Managed client-version pre-flight passed for the installed 11.19.0 family.
- Window move/resize and Watch Offline click reached `OfflineReplayVerified`.
- No second monitor was attached; the 640x360 window used primary top-left.
- Capture recorded **161 samples** over replay time **7.2–287.4 s**.
- 165 live-frame attempts were made; 4 clock/camera skips were fail-closed.
- One terminal 400 was recognized as `evidence.replay_completed`; the tool
  wrote the output and exited `OK`.
- No raw aim coordinates, replay bytes, capability tokens, or full paths were
  written to the repository.

## Ship-readiness conclusion

Together with the Dead Rail live proof, this confirms the PN prototype's
replay/live aim seam is repeatable across two content-distinct replays. The
remaining accuracy limits are the documented product tradeoffs: nominal
armor-face thickness, manual stock-shell selection, and deterministic rather
than exact penetration RNG. No further construction work is required for the
ship-ready prototype; future work is regression, exact turret-lock discovery,
or optional model-fidelity research.

## Final repository validation

After this regression evidence was recorded, `scripts/validate.ps1` passed:
Release build 0 warnings/0 errors; all test suites green; Pester 7/7 and
16/16; offline pack 65 files, 118 links, 0 broken; and offset-chain validation
passed for 8 fields.

## Final correctness audit — 2026-08-14

The adversarial PN review found and fixed eight genuine correctness/fail-closed defects:

1. Static armor, collision-mesh, and stock-gun shot caches are now keyed by
   `nation:tank`, so equal bare filenames from different nations cannot reuse
   one another's armor or shell profile.
2. A collision group whose part id is not the proven hull/turret/gun set
   (`1/3/5`) still localizes the struck face but returns an `Unknown`
   penetration verdict instead of silently inheriting hull armor.
3. When a known collision mesh misses the ray (or the ray hits an unsupported
   deck face), the badge now reports `Face=Unknown` instead of leaking the
   four-face fallback label beside an Unknown verdict.
4. Tank ids are constrained to known nation and single path components before
   install-resource probes, and the capture tool skips a zero/non-finite camera
   basis instead of emitting a sample the scorer would later replace.
5. Stock-gun shell profiles now join by both gun identity and shell name, so a
   shell resource reused by another gun cannot select the wrong piercingPower
   pair.
6. The overlay now notifies `SelectedPenShellName` and `PenShell` whenever a
   frame establishes or clears the server-selected default, so the ComboBox
   cannot remain visually blank while the badge uses a valid shell.
7. The overlay now compares the effective shell values before raising those
   notifications; a stable server default no longer makes `MainWindow` fetch
   another frame indefinitely through its shell-change handler.
8. Detail loading now publishes the initial timeline position after the
   selected session state is assembled, so `MainWindow` requests the first
   W2S frame immediately; the pen badge no longer waits for playback or a
   manual scrub to appear.

Regression coverage pins the cache partition, unknown-part verdict, mesh-miss
face, path rejection, invalid aim override behavior, gun-identity join,
shell-selector default notification, stable-default notification
quiescence, and initial-session timeline publication.
The staged diff is ready for owner ship review.

## Final offline verification — 2026-08-14

After the audit and stock-gun identity hardening, the focused penetration
checks passed: Core penetration tests 50/50 and GameIntegration parser/service
checks 23/23 plus one opt-in installed-game skip. The full `scripts/validate.ps1`
gate then passed with a zero-warning/zero-error Release build, 344
GameIntegration tests passed with 5 opt-in installed-game skips, all other
suites green, Pester 7/7 and 16/16, PSScriptAnalyzer hygiene passed, offline
pack freshness passed, and 8 published offset chains validated. The current
staged package remains uncommitted and unpushed for owner review.

The five `[TestCategory("LocalGame")]` installed-game checks were also run
read-only against the configured 11.19.0 install after the gate and passed
5/5, including the end-to-end Churchill armor, stock-gun shell, collision-mesh,
and SFV2 identity-transform checks. They remain opt-in in the normal gate.

## Manual HUD ship smoke checklist

This is the short local-replay check to run immediately before release; it is
not claimed as executed by the offline validation pass above:

1. Select a decoded replay while paused. The first W2S frame and pen badge
   should appear without starting playback or scrubbing.
2. Cycle the shell in the sidebar and with `Q`; the selected shell name and
   badge label should change together, without a repeated request loop.
3. Aim at a supported front face and confirm the badge shows a colored verdict
   plus the `penetration/effective-armor` millimeter readout.
4. Aim at an unsupported side, rear, deck, or mesh-miss case and confirm the
   badge disappears rather than showing a colored or guessed verdict.
5. Move the aimed enemy off the viewport and confirm the badge falls back to
   the reticle; switch sessions and confirm the prior shell choice cannot
   survive when the new stock gun does not offer that shell.
