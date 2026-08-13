# Pen-chance — shell selector, scoring harness, and HUD polish (2026-08-13)

**Phase 6** (`docs/operations/pen-chance-design.md`). Offline, no launch.
Continues `2026-08-13-pen-collision-mesh-raycast-shipped.md`.

## What shipped since the last pen handoff

- `c72362a` — **per-part armor**: `CollisionMeshParser.ParseAll` reads all three
  `.scg` polygon groups (`#id` 1/3/5 = hull/turret/gun, identity transforms → one
  shared Z-up space) and `EvaluateAgainstMesh` raycasts them as a union, scoring
  turret/gun hits against the turret's frontal `primaryArmor`. New
  `SceneFileParser` walks the `.sc2` SFV2 KeyedArchive v2 (u32-length-prefixed
  nested archives, hash+type+value entries) to *verify* the identity transforms.
  Also fixed the shared `ref struct` reader passed by value (position advances
  silently lost) and the steep-glacis face misclassification (horizontal
  projection, not vertical-dominance).
- `07637d7` + `581e654` — **shell kind + manual selector**: `ShellSpec.Kind`
  (from shells.xml) excludes HEAT from the 3-caliber overmatch rule;
  `PenetrationContext.Shells` offers every shot of the viewer's stock gun
  (`?shell=` → `PenShells`/`PenShell` on the frame), with the Q hotkey cycling
  AP/APCR/HE/HEAT.
- `ebb7e36` — **PN-4 scoring harness** (`PenValidation.Score` + `ScoredShot`,
  6 tests): ricochet agreement (predicted ricochet ⇒ non-penetrating) + band
  accuracy (Pen ⇒ penetrated, NoPen ⇒ not) vs the decoded `penetrated` outcome.
  The aim *source* is deliberately the caller's concern.
- `63e4af7` — **sidebar shell selector**: the pen badge's shell choice is a
  visible ComboBox, not just the Q hotkey; selection changes re-score via a
  `SelectedPenShellName` change handler, and a stale selection is dropped when a
  new session's gun no longer offers it.
- `89a9f1f` — **struck face + anchored badge**: the badge renders the struck
  face (FRONT/SIDE/REAR; wire `Back` → REAR) and anchors to the aimed tank's
  nameplate when it is on screen (reticle fallback otherwise), so the readout
  is tied to the tank being aimed at.

OD hardening in the same session (not pen, listed for completeness):

- `8391314` — Pester smoke tests (7) for `od-replay-completion.ps1`'s
  never-throw / fail-open / clean-run contracts, wired into `validate.ps1`.
- `162d9b9` + `2061f73` — fresh-eyes review of launcher/clicker/chain
  (no defects) and dedupe of the launcher's four `Test/Set-OwnerOnly*` ACL
  helpers onto the shared `Test/Set-OdOwnerOnly*` definitions.

## Honest limits (unchanged)

- Thickness stays **nominal** (mesh normal × front/side/rear value); the
  armor-group → mesh-face mapping is still the open accuracy gap.
- Front-only armor for side/rear of turret/gun (0 → Unknown, never guessed).
- Loaded shell not decodable — the selector covers the stock gun's ammo.

## `pen-score` CLI re-investigated and confirmed deferred

Re-traced the offline aim reconstruction this session: the type-32 `ShotImpact`
mirror has no attacker id, the type-8 subtype-8 bounce-attribution is partial
(28 vs 69 shots), and the attacker→victim center-line incidence is empirically
near-uniform (uniform 0–80°, 15% ≥70° on a real 121-damage session) — so it
cannot validate the ricochet rule. The CLI stays deferred: it needs the live
CAM-013 camera aim at shot time, which PN-4 delivers.

## Verified

Full `scripts/validate.ps1` gate green after each change (incl. the new Pester
step); Overlay 117/117 after the face test.

## Remaining (all live- or owner-gated)

1. PN-4 live validation (CAM-013 aim at shot time → `PenValidation.Score`).
2. Single-launch cluster: completion-marker verify + batch rehearsal + Branch-B
   camera double-reads + `DamageDealt` E2E.
3. `ConsistentDoubleRead` flag-flip approval (owner).
4. L4 replayTime + T1 turret-facing sessions.
