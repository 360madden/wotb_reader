# Handoff — enemy-tracking focus: capability map + type-7 packet survey (2026-08-11)

## Focus shift

Temporary focus: reliable enemy-tank information for the HUD/overlay — enemy
position, HP, hull facing, turret facing, and any lock-on / auto-aim /
"has me targeted" state — plus the question of whether same-match replays
from other players' perspectives are beneficial.

## Capability map (evidence-grounded)

**Replay decode — available NOW (all enemies, not just the viewpoint):**
- Position x/y/z ~10 Hz per entity — type-10 → `position_samples` (642k rows).
- HP + max HP + alive/dead — type-8/subtype-1 + type-5 + destroy markers;
  the HP ledger is proven exact against battle_results.
- Hull yaw/pitch/roll — type-10 payload +36/+40/+44, canonicalized as
  `position_samples.yaw/pitch/roll` (newer decodes; older battles predate
  the migration).
- Team, tank, name, bot status — `participants`.
- Distance + "hull-aim-line" (is an enemy's hull pointed at me) — computed
  from position + hull yaw.

**Replay decode — PROVEN ABSENT (new survey):**
- Turret facing — the full type inventory covers 100% of the stream; no
  packet carries a second per-tank angle.
- Lock-on / auto-aim / targeted state — client-side UI state, not in the
  server-authoritative stream (same class as the V3 no-spotting finding).

**Live memory path (later):** the entity-collection walk already in the
camera script reaches all entities' ring records; per-entity HP, turret,
and target fields are discovery targets; the batch `entity-regions` surface
is the pre-staged read mechanism.

## Type-7 entity-status survey (new evidence, 2026-08-11)

Ran a payload-level survey of the 19 040 type-7 packets on Oasis Palms
11.19.0 (raw evidence bytes vs canonicalized hull yaw):

- Layout: entity-id u32 + 2 entity-specific state int32s + fast-rotating
  16-bit tail.
- The tail sweeps the full 16-bit circle at 2 000–3 600 deg/s across every
  roster entity — a tick counter/bitfield, NOT an angle (real turret
  traverse is ~20–60 deg/s). No scale (fixed-point or half-float) makes it
  track hull yaw.
- One effect entity (4124165, non-roster) carries a 16-byte layout with a
  rotating float X that moves while hull yaw is static; X ≠ yaw and X ≠
  pitch — an effect parameter, not a tank field.
- Type-32 damage-mirror variants carry shell signatures + ids, no bearing
  float aligned to the shot victim (checked damage events vs payload
  floats; no match).

**Conclusion:** the replay carries no turret angle and no lock/target
state. The enemy-track HUD is built on position + HP + hull yaw; turret and
true lock state require live-memory discovery later. Replay-format doc
updated (type-7 row + a new NO-turret/lock paragraph).

## Same-match replays from other players — assessment

**Beneficial, as an audit + camera-calibration multiplier, not a
requirement:** every replay carries the full battle (all entities), so
enemy tracking needs nothing extra. A second player's file of the same
battle gives (a) a free cross-validation of the per-entity position/HP/yaw
timeline (the `comparison_runs` scaffolding exists and is empty — built for
this), and (b) a second camera trajectory for W2S calibration. It cannot
reveal the other player's lock state (same absence reason). Sourcing is
the constraint: WoTB saves each player's own perspective only; the other
player must share the file.

## Round 2 (same session) — AimGeometry + real-data hull-aim validation

- **`AimGeometry` shipped** (`src/WotBTreader.Core/Overlay/AimGeometry.cs`,
  9 synthetic tests): `HullAimErrorRadians` + `HullAimsAt` — the hull-arc
  check in the proven packet yaw convention (yaw 0 = +Z, heading =
  atan2(dx, dz)), fail-closed on non-finite/zero-distance/out-of-range
  tolerance. The enemy-track frame ALREADY carried position + hull yaw +
  HP per tank (`OverlayTankState`), so the aim-line was the only missing
  piece; it stays client-computable (no shared-contract change).
- **Real-data validation (78 shots, Oasis Palms):** at fire instants the
  attacker's hull is 48–68° off the bearing to the victim on average
  (moving attackers med 48°, static med 68°); only 15–20% of shots land
  within a 15° hull arc. Convention itself re-verified (hull yaw vs motion
  heading med 0.0° moving forward; the p90 180° is the known reversal
  case). **Conclusion: the turret fires independently of the hull — hull-
  only "aims at" is a WEAK proxy and must not be presented as aim
  detection.** The utility stays as the honest necessary-condition layer
  (a hull pointed at you is a real, weak threat signal; a hull pointing
  away means the turret cannot be on you within the hull arc). True aim
  detection needs turret data — absent from the replay, a live-memory
  discovery target.

## Next units

1. ~~Enemy-track overlay frame~~ — already present in the frame contract;
   the aim-line is now a tested Core utility.
2. Multi-perspective comparison via `comparison_runs` when a second
   player's file is obtainable.
3. ~~Live enemy-roster read design~~ — DESIGNED AND IMPLEMENTED 2026-08-11:
   `docs/operations/live-roster-read-design.md` (X3, status ADOPTED).
   Closes the one gap between the batch rehearsal and live frames: the
   batch surface takes entity ids, but live mode has no decoded participants
   table, so the ids are enumerated from the game's own entity maps via a
   **full-tree walk** (both children per node, vs the resolver's
   branch-pruned search), deduped across cache + three maps, filtered by the
   movement-filter vtable set, and returned **ids only** through a new
   `POST /discover/entity-roster` endpoint that feeds the unchanged
   `entity-regions` batch. Shipped: `Type10EntityPositionResolver
   .EnumerateEntities` (the resolver's gated member-path extracted into a
   shared `TryResolveEntitiesAddress` so search + enumeration share one
   walker; per-tree MaxTreeNodes bound fails closed as
   `TraversalLimitExceeded`; movement-filter gate → avatar family),
   coordinator `EnumerateEntitiesAsync` (gate → build identity → guarded
   reader; addresses die inside, ids only out), endpoint + contract, and 14
   new tests (7 resolver, 4 coordinator, 3 endpoint). The `-EnumerateLive`
   rehearsal mode is IMPLEMENTED too — `invoke-batch-rehearsal.ps1
   -EnumerateLive` calls `/discover/entity-roster`, writes the enumeration
   evidence (schema `...roster-enum.v1`), and verdicts it against the
   decoded roster via the new `--enumeration` mode of
   `batch-rehearsal-crosscheck.py` (matched/missing/extra + precision/
   recall; self-test extended); with `-LiveAcquire` the ENUMERATED ids
   drive the batch dumps (full X3 rehearsal in one command). It
   **measures the movement-filter precision** against the decoded roster
   on the next approved session. Turret/target/lock fields ride on that
   per-entity surface.

## Coordinator extraction audit (2026-08-11, PASS)

Read-only semantic audit of the `475f042` core-extraction refactor: the
pre-refactor (`187664f`) bodies of `ReadEntityRegionsAsync` /
`EnumerateEntitiesAsync` / `ReadCameraPoseAsync` were diffed against the
current wrappers + private cores. Every string literal and enum member
preserved (the two "moved" items, `avatar-vftable-anchor` /
`AnchorNotFound`, live in the `CameraAnchorNotFound` helper);
`IsScanAuthorizationCurrent` / `GateCheck` / `Success` / `Failure` counts
match per method. The camera's +1 auth check is the intended post-
`FindAvatarAnchorAsync` disambiguation — the helper's (0,0) sentinel on
authorization revocation resolves to `GateCheck` in the caller (never
`AnchorNotFound`), exactly the old immediate-bail semantics; the frame's
single-lease composition and anchor-before-lease ordering were verified
against the design.

## X4 step 3 implemented — composed live frame (2026-08-11)

The coordinator now serves the whole per-frame read under **ONE guarded
reader lease**: `POST /discover/live-frame` runs roster enumeration
(`EnumerateEntitiesCoreAsync`) → ring-record batch (`ReadEntityRegionsCoreAsync`,
position `+0x10` + hull yaw `+0x2C`) → CAM-001 camera pose
(`ReadCameraPoseCoreAsync`) — one G2 clock attestation per frame, honest
`hp: null` until L1, ids-only privacy boundary, camera `AnchorNotFound`
reported without failing the frame. To enable this without duplicating any
walker, the three public methods were split into thin gate/create wrappers
+ private cores that take the pre-created reader; the camera anchor scan
moved into `FindAvatarAnchorAsync` (shared by both paths) and the
anchor-missing result into `CameraAnchorNotFound`. `RingRecordRegion`
(Core) decodes the ring region purely (finite fail-closed). 25 new tests
(10 Core decoder, 4 coordinator frame incl. ONE-lease `CreateCount == 1`,
3 endpoint, 8 resolver/roster); full suite green.

## X4 seam complete — `LiveFrameSource` (2026-08-11)

The overlay can now consume live frames through the same render path as
replay frames: `LiveFrameProjector` (Application, pure) maps
`LiveFrameReadResult` → the SAME `OverlayFrameProjection` shape, and
`GET /api/v1/live/frame` serves it projected to viewport pixels via the
single shared `ToOverlayFrameResponse` mapping (extracted from the replay
handler — both sources serialize identically). HP is the DTO's honest
"unknown" representation (empty bar, no readout) until L1;
pips/kills/scoreboard absent; non-resolved frames fail closed with 409 and
a failed read with 503 (the HUD keeps last-good-frame).
`TreaderApiClient.GetLiveFrameAsync` added. 12 new tests (8 projector, 4
endpoint). The overlay's live-mode UI toggle is DONE too: an `IsLiveMode`
checkbox in the HUD toolbar flips `RefreshOverlayFrameAsync` between the
replay fetch and `GetLiveFrameAsync`; the render path is unchanged, the
kill feed / scoreboard stay empty in live mode (decode-projection
features), and a non-resolved/failed live read keeps last-good.
Remaining: the live-ids → decoded-name join — now DESIGNED (pre-staged,
`docs/operations/live-roster-name-join-design.md`: server-side
entity-id → decoded-participant join feeding `LiveFrameProjector`, honest
fail-closed per id, own-nameplate suppression riding the same join). No
code until X2b's `-EnumerateLive` rehearsal proves the id mapping
(exact set match). The frame read-window
measurement is IMPLEMENTED: `LiveFrameReadMeasurement` (anchor-scan start
→ camera-pose read end + the ONE G2 snapshot moment from the batch) flows
through `LiveFrameReadResult` → the `/discover/live-frame` DTO; the loop's
per-frame timing budget (item-7 groundwork) only needs the approved
session to record a real window. 2 new tests (coordinator compose
measurement shape + endpoint DTO flow).

## L1 HP session pre-staged (2026-08-11)

`docs/operations/od-recovery-087-evidence-template.md` pre-stages the L1
live session the same way OD-RECOVERY-086 was pre-staged: static values
pinned (entity-base anchor, region ≥ 0x120, signed-int16 correlate,
VerifyPlayerHpChain 26/26 map: current `+0xB8`, alive `+0xBA`, max
`+0x11C`, healing `+0x11E`; Oasis Palms victim 3760578, Dead Rail victim
2549399 for the Phase-4 two-replay rule), the runnable one-command
`invoke-hp-diffing-session.ps1` invocation, the ledger YAML skeleton, and
branch-on-evidence rules (hit at `+0xB8` → live frame HP becomes real;
different confirming offset → live finding wins; no-hit → widen the
anchor). Ledger next-session row + workflow references updated. The L2 facing
session is likewise pre-staged (`docs/operations/od-recovery-088-evidence-template.md`):
ring-record dumps (region ≥ 0x40, `+0x2C..+0x37` probe first), the
runnable `invoke-facing-session.ps1` invocation, ledger YAML skeleton,
and branch-on-evidence rules (hit at `+0x2C` → live frame yaw
live-verified; different offset → live finding wins; no-hit → widen the
probe; Dead Rail's 5 seam crossings exercise the wrap-aware matcher).

## X1 policy memo APPROVED (2026-08-11)

The owner approved the X1 memo **Option A** (read-only live overlay,
replay-proven fields only) on 2026-08-11: the memo is ticked and dated,
the roadmap X1 row shows the gate passed, and the Phase-5 design track is
unlocked (live-mode gate relaxation design, live session drivers). The
approval authorizes NO live testing — every live session remains
separately gated, and the code-enforced `OfflineReplayVerified` gate is
unchanged until a subsequent operator-approved change. The first unlocked
deliverable is DESIGNED: `docs/operations/live-match-gate-design.md` — a
new `LiveMatchVerified` state whose evidence is the unchanged
process/build identity PLUS a single-flight, time-bounded, launch-correlated
user live assertion (no replay lifecycle marker exists for live play, so
none is fabricated); the replay gate and every existing surface stay
untouched, live mode is read-only by state-machine construction,
and a field is only readable live after its own live session passes
(086/087/088). Implementation + the online-match session remain separately
gated.

## X1 policy memo drafted (2026-08-11)

`docs/operations/x1-live-game-policy-memo.md` is the Phase-5 gate document
for the live-online track (roadmap X1, gate = explicit user approval):
three options (A read-only live overlay, A+B + local-only record, C
replays-only), honest ToS risk analysis (read-only observation of the
owner's own client; no writes/injection/automation; account risk is the
real exposure), scope pinned to replay-proven fields (position, yaw after
L2, HP after L1), and an approval checklist for the owner. It explicitly
records that it authorizes NO live testing and changes NO code-enforced
gate; it only clears the Phase-5 design track if approved. Roadmap X1 row
updated to reference the draft.

## Files touched

- `src/WotBTreader.Core/Discovery/RingRecordRegion.cs` (pure ring-region
  decoder: position +0x10, yaw +0x2C, finite fail-closed) + tests
- `docs/operations/x1-live-game-policy-memo.md` (X1 gate draft)
- `docs/operations/od-recovery-087-evidence-template.md` (L1 pre-staged
  evidence template)
- `src/WotBTreader.Application/Replay/LiveFrameProjector.cs`
  (live-frame → `OverlayFrameProjection`, reuses `WorldToScreen`) + tests
- `src/WotBTreader.Host.Web/Endpoints/ReadApiEndpoints.cs`
  (`GET /api/v1/live/frame` + shared `ToOverlayFrameResponse`) + tests
- `src/WotBTreader.Overlay/Services/TreaderApiClient.cs`
  (`GetLiveFrameAsync`)
- `src/WotBTreader.GameIntegration/Session/GameSessionCoordinator.cs`
  (`ReadLiveFrameAsync` + `EnumerateEntitiesCoreAsync` /
  `ReadEntityRegionsCoreAsync` / `ReadCameraPoseCoreAsync` /
  `FindAvatarAnchorAsync` / `CameraAnchorNotFound`)
- `src/WotBTreader.Application/Game/GameSessionContracts.cs` +
  `src/WotBTreader.ApiContracts/OffsetDiscoveryContracts.cs` +
  `src/WotBTreader.Host.Web/Endpoints/GameApiEndpoints.cs`
  (`LiveFrameReadRequest/Result`, `POST /discover/live-frame`)
- `docs/operations/live-frame-loop-design.md` (step 3 status → DONE;
  `LiveFrameSource` seam remains)
- `offline/replay-format.md` (type-7 row + no-turret/lock finding)
- `docs/operations/live-roster-read-design.md` (X3 — designed + implemented)
- `docs/operations/live-frame-loop-design.md` (X4 — this session's design)
- `scripts/invoke-batch-rehearsal.ps1` + `scripts/python/
  batch-rehearsal-crosscheck.py` (`-EnumerateLive` + `--enumeration`)
- `offline/api-surface.md` (entity-roster row)
- `docs/operations/od-recovery-086-evidence-template.md` (pre-staged
  evidence template for the composed X2+X3 session — ledger row +
  workflow next-session rows updated to the `-EnumerateLive -LiveAcquire`
  command)
- `docs/operations/handoffs/2026-08-11-enemy-tracking-focus.md` (this file)
