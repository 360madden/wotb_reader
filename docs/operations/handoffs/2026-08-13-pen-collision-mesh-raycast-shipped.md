# Pen-chance — collision-mesh raycast shipped (2026-08-13)

**Phase 6** (`docs/operations/pen-chance-design.md`). Offline, no launch.

## What shipped (commit `cbda666`, plus the prior `677719d` parser)

- `677719d` — `CollisionMeshParser` reads the install's
  `Data/3d/Tanks/CollisionMeshes/{nation}-{tank}.scg.dvpl` (DAVA SCPG →
  KeyedArchive `PolygonGroup`) into a `CollisionMesh` of positions + surface
  normals + triangle indices (uint16/uint32). Fail-closed on malformed
  input; verified against the real Churchill I mesh (476 verts / 304 tris,
  opt-in local-game test).
- `cbda666` — `CollisionRaycast` (Möller–Trumbore) turns the aim ray into the
  nearest struck triangle's **outward** surface normal (flipped to agree with
  the per-vertex normals, so winding can't invert it). `PenetrationAim` gains
  `EvaluateAgainstMesh` (world aim → tank-local raycast → face-classified
  nominal thickness → `ArmorPenetration.Evaluate`) and `ResolveBadge` uses the
  mesh when one is parsed, falling back to the four-face box otherwise. The
  badge's `Face` now reports the mesh-classified face. `PenetrationDataService`
  resolves the mesh per roster entity (best-effort, cached); `ReplayFrameSource`
  threads `PenetrationContext.MeshesByEntity` through.

## What this fixed

PN-4's honest negative: the four-face box's facing-derived normal washed out
plate-specific incidence (uniform 0–80° on a real 121-damage session). The
mesh supplies the true struck-plate normal, so the effective-armor ANGLE is
now geometric — while the nominal THICKNESS is still the front/side/rear
value (see the remaining gap below).

## Honest limits (unchanged / new)

- Thickness stays **nominal**: the XML armor groups don't declare their face
  mapping, so the mesh normal is still paired with the nominal
  front/side/rear thickness. True per-plate thickness needs an armor-group →
  mesh-face mapping.
- Front-only armor (side/rear = 0 → Unknown, never guessed); stock AP shell
  (loaded shell not decodable).
- The raycast is Möller–Trumbore with no backface cull — correct for a closed
  tank mesh (a ray from behind strikes the rear plate first), but a partial
  mesh would report a backface hit. The parser emits the full mesh, so this is
  fine; noted for future partial-mesh uses.

## Verified

- Focused: `CollisionRaycastTests` 7/7, `PenetrationAimTests` (mesh) 5/5.
- Full `scripts/validate.ps1` gate green: Core 256/256, Application 84/84,
  GameIntegration 325/325 (3 opt-in skips), format/analyzers/build/scan/
  PSScriptAnalyzer/offline-pack/offset-schema all pass.

## Remaining (PN-4 is still the proof)

1. **Per-shot attacker aim** — the center-line proxy is too coarse; PN-4
   scoring needs the decoded attacker aim (a fresh re-decode surface) or the
   live camera aim at shot time.
2. **Type-8 flag-byte / type-32 decode-lane surface** — disambiguate
   bounce-vs-absorb per shot (archive-relative evidence offsets ⇒ a
   decoder-side change).
3. **Armor-group → mesh-face thickness mapping** — closes the last accuracy
   gap (true per-plate thickness, not just the true normal).
