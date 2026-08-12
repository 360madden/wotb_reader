# L3 damage-dealt — avatar/player-stats family discovery plan

**Date:** 2026-08-12
**Status: PLAN (pre-staged).** Gated behind the operator-approved publication
applies (HP then yaw) — the project's serial order; this document pre-stages
the methodology so the lane can run immediately after. No code changed, no
memory touched.

## Goal

Find the LIVE damage-dealt counter for the player's own tank in memory and
prove it against the decoded damage events to the Phase-4 standard (two
content-distinct replays, strict exact sums within the bounded memory-lag
window). The live overlay frame currently carries `DamageDealt: 0` honestly
(no read exists); a verified counter would fill the own row of the live
scoreboard (V2's panel in live mode) and, if published, the live frame's
`DamageDealt` field.

## Why the entity records are dead (append-only reference)

OD-RECOVERY-090 (2026-08-11) ran three honest sweeps, all negative:

1. **320-byte entity-base** (session `019ff250`, 50 dumps, 6 event windows +
   controls): top candidate `+0x3C` scored 0.833 but demoted by flatness
   0.091 — it is the already-measured position-copy float.
2. **4096-byte entity-base wide sweep**: candidate `+0x7EC` demoted —
   moving-float decoy.
3. **Sibling entity-tank-record anchor**: live-verified dead —
   `[entity+0x3C]` is not a stable pointer (`0x36CE3AE8` at 32.8 s vs
   `0xC36046AA` at 59.8 s).

Conclusion (kept): damage-dealt is NOT reachable via the per-entity records.
It lives in a per-PLAYER object family (the avatar / player-stats object),
which this plan targets.

## Candidate object family

- The game keeps per-player battle statistics (the values the battle-results
  screen shows: damage dealt, kills, damage blocked, ...) in an object keyed
  by the player, not by the victim entity. In a replay, the memory-side
  counter must track the decoded `battleStats.damageDealt` events as the game
  applies them (same variable ~1–3.4 s memory lag established by HP/yaw
  discovery).
- **Runtime reachability already exists:** the live frame's camera pose read
  resolves the player's avatar address (`avatarAddress`, from the
  camera-helper walk; `GameSessionCoordinator.ReadLiveFrameAsync` branches on
  it). The player-stats sub-object is a candidate child of that avatar (or a
  module-rooted chain reaching it), NOT a new read surface class — reads ride
  the existing guarded reader lease + ONE G2 attestation.
- **Static search signature:** Ghidra hash-bound scan of the avatar /
  player-stats family for a counter that the damage-application code writes
  on the ATTACKER side (the HP path writes the victim; damage-dealt is the
  attacker-side write — the two writes share the same damage-application
  method, so the attacker-stats write is discoverable near the victim-HP
  write).

## Static candidate search — DONE (2026-08-12, hash-bound `1cda5c31…`)

Ghidra listing work (evidence `.build/ghidra-evidence-l3-damage/`;
tooling `FindVtableDispatch.java`, `FindDispatchCallers.java`,
`DumpRawWindow.java` — new, tracked):

1. **All 13 `+0xB8`/`+0x11E` word-store sites classified.** The two vehicle
   STATE DECODERS `FUN_0166b9f0`/`FUN_01675f60` (Vehicle vtable
   `0x36755d0`, slots 12/13, dispatch offsets `+0x30`/`+0x34`) read a
   serialized stream (virtual slot reads) and write the whole record —
   they ARE the victim-HP write path. The other 11 sites are constructors
   of unrelated families writing constants (`MOV word [reg+0xb8],0/1/0x100`
   — EquipmentModel, TextureDataSettings, UIShadowRectCutterSystem, a
   shadow-layout object, LoginHandler-family) plus the Vehicle constructor
   zero-init (`FUN_01639560`). **No attacker-side counter write exists at
   any `+0xB8` site** — damage-dealt is NOT on the entity/vehicle record
   (consistent with OD-RECOVERY-090's sweeps).
2. **The entity-family object factory** `FUN_01669c90` (jump table): case 0
   Account, 1 **Avatar**, 2 Vehicle, 3 AreaDestructibles, 4 OfflineEntity,
   5 Flock, 6 FlockExotic, 7 Login, 8 DetachedTurret, 9 KineticObject.
   Avatar = `0x128` bytes, vtable base **`0x36752a4`**.
3. **The Avatar object carries the battle-stats block.** Its property-change
   dispatcher `FUN_01670de0` maps property indices to avatar offsets:
   a contiguous **uint32 quad at `+0x118`/`+0x11c`/`+0x120`/`+0x124`**
   (indices 0xA–0xD), plus uint32 `+0xf8` (0x15), `+0xdc` (0x8), `+0xe4`
   (0x9), uint16 `+0xe8`, bytes `+0xda/+0xe0/+0xe1/+0xea/+0xfc` — each
   case reads the current value and fans out to listener slots (`+0x188`
   for index 0xA … `+0x1a0` for 0xD).
4. **Live increment-correlator target (Phase-4 hypothesis):** the
   damage-dealt counter is one of the Avatar uint32 props — most plausibly
   the `+0x118..+0x124` quad (cumulative own-attacker damage, increments
   only on own hits, zero-initialized at construction). The live session
   dumps ALL of them + controls over each own-damage window and scores with
   the increment contract (score 1.0, flatness 1.0, control windows flat).
   This SUPERSEDES the plan's "player-stats sub-object child" wording — the
   stats live ON the Avatar object itself.

## Reachability — CORRECTED 2026-08-12 (do not reuse the camera anchor!)

**The camera chain's `avatarAddress` is NOT this object.** The CAM-001
camera anchor scans for vftable RVA `0x03277e8c` (replay) / `0x03277da4`
(live) — static listing identifies those as **`AvatarControllerReplay`**
(Ghidra: vftable `0x3677e8c`, 0x1a8-byte object, `+0x154` → BattleResources;
installers `FUN_016369f0`/`FUN_0163ddb0`). The L3 target is the
**entity-factory Avatar** (`FUN_01669c90` case 1, vftable `0x36752a4` =
RVA `0x032752a4`, 0x128-byte object; class ctor `FUN_0163da30` + factory
case 1 are the only installers). DIFFERENT objects: the camera anchor is the
controller; the stats quad is on the player-stats entity. The camera
anchor's `+0x118..` region is NOT the stats block — reading it would be a
hunt on the wrong object.

**Reach path for the live session (established technique):** the SAME
gated vftable AOB scan the camera chain already uses
(`ScanForCameraAnchorAsync`: `FieldName avatar-vftable`, AOB = LE dword
`moduleBase + avatarRva`, `MaxCandidates 4`, under the guarded reader lease
+ ONE G2 attestation) — with the TARGET dword = `moduleBase + 0x032752a4`
(the entity Avatar vftable). The scan resolves the own player's Avatar
object; `+0x118..+0x124` are direct offsets on it. The scan endpoint needs
NO new read surface: it rides the existing `discover` AOB path. The
increment correlator then discriminates WHICH candidate is the own
Avatar's stats (only the own counter increments on own-attacker events;
the others stay flat = built-in control windows).

**Session pre-check (fail-closed):** before scoring, gate the resolved
object's vftable dword == `moduleBase + 0x032752a4` (identity), read the
quad + `+0xda/+0xe0/+0xe1/+0xdc/+0xe4/+0xe8` neighbors, and require the
`+0x118..` quad to be battle-zeroed at construction semantics. If the scan
returns no candidate or the identity gate fails → honest-negative for the
session (never widen the read surface on a hunch).

## Session seam — PRE-STAGED (2026-08-12, no launches yet)

The live session's read seam is IMPLEMENTED and gated — only the approved
launches remain:

- The existing `/discover/entity-region` endpoint gained the **`avatar-stats`**
  region anchor (`EntityRecordRegionAnchor.AvatarStats`): it IGNORES
  `entityId` and, under the SAME authorization + build identity + guarded
  reader lease + ONE G2 attestation, runs the gated vftable AOB scan for
  `moduleBase + 0x032752a4` (FieldName `avatar-stats-vftable`, MaxCandidates
  4, alignment 4), re-gates the chosen candidate's vftable dword, and dumps
  `regionLength` bytes at `candidate + 0x118` (the quad, default 16). The
  entity-ID resolver is SKIPPED for this anchor (the stats object is not an
  entity record).
- Per-candidate enumeration: the request carries an optional
  `avatarCandidateIndex` (0..3, default 0) and the response reports
  `avatarCandidateCount`. Fail-closed statuses:
  `AvatarAnchorNotFound` (`avatar-scan-not-found` /
  `avatar-candidate-out-of-range`) and `AvatarIdentityMismatch` — never
  read the quad off an unauthenticated object.
- Driver: `scripts/invoke-hp-diffing-session.ps1 -Track damage-dealt
  -RegionAnchor avatar-stats` — the FIRST dump of each tick learns the
  candidate count, then dumps EVERY candidate (churn shrink tolerated,
  growth fail-closed), writes one snapshots file PER candidate
  (`hp-snapshots-<session>-<stamp>-candN.json`), and runs the increment
  verdict per candidate. The OWN counter is discriminated at scoring time:
  only it increments in sync with own-attacker events; the other candidates
  stay flat = built-in control windows. ALL candidates flat → honest-negative
  for the session (never widen the read surface on a hunch).
- Tests: 6 new (5 coordinator + 1 endpoint) — scan + identity gate + quad
  success, no-candidate fail-closed, identity mismatch fail-closed,
  candidate-out-of-range fail-closed, invalid index fails before the gate,
  endpoint anchor/candidate forwarding.

## Rehearsal — PASSED (2026-08-12, offline, real decoded session)

`scripts/invoke-avatar-stats-rehearsal.ps1` proves the per-candidate verdict
protocol end-to-end BEFORE any live launch, on a REAL decoded session
(`019fa44a-b226-…`, attacker 2852449, 7 hit windows / 2835 damage): it
qualifies the damage-dealt ground truth, synthesizes ONE snapshots file PER
scan candidate (candidate 0 = the perfect own counter — dword0 at +0x0
rises by exactly each window's damage sum across the before/after dump
pairs; candidates 1..3 = all-zero flat quads), and runs the increment
verdict per candidate with the SAME CLI the live session uses
(`hp-diff --direction increment --data-root .data`).

Result: **candidate 0 HIT (score 1.0, flatness 1.0, matched 7/7,
offset 0x0 — "HIT: score 1.0, flatness 1.0, >= 2 exact-sum Strict
matches"); candidates 1..3 NOT hit ("no candidate matched any damage
window")** — REHEARSAL_EXIT=0 with `-FailOnNoHit`. This proves the 16-byte
quad snapshots schema, the increment correlator's own-counter
discrimination, and the per-candidate file protocol the live session
reuses. The script fails closed (exit 1) if the incrementer misses or any
flat candidate hits — the protocol must be right before the first launch.

**PHASE-4 SIMULATION — PASSED (same day, offline):** with
`-Phase4SessionId 019fb86c-…` (Dead Rail, attacker 2549401, 4 hit windows /
1569 damage) the same QUALIFY → SYNTHESIZE → VERDICT flow runs on the second
replay and requires the matched offset to AGREE. Result: Dead Rail
incrementer HIT (score 1.0, flatness 1.0, matched 5/5, offset 0x0), flat
candidates NOT hit, and **matched offset 0x0 agrees across both replays** —
the two-replay repeat rule proven offline before any launch. (The rehearsal
also caught a real flow bug in its own first phase-4 run: the verdict
function read the GLOBAL primary session id instead of the phase-4 session,
so the phase-4 verdict queried the wrong ground truth — fixed by passing
session + victim explicitly. Exactly the class of error the rehearsal
exists to catch before live launches.)

## Verification methodology (increment correlator)

HP/yaw discovery matched value DROPS and static reads; damage-dealt
INCREMENTS. The correlator family is the same attribution machinery
(`HpDamageCorrelator` / `RecordDiffing`) with an increment direction:

- Ground truth: the decoded `HpDamageEvent` timeline — events where
  `AttackerEntityId == own entity id`, summed per window
  (`HpDamageEvent.Damage`). The own entity id is already resolved live
  (`OwnEntityId`, name-join step 4).
- Hypothesis: `counter(t)` == cumulative Σ(own attacker damage events applied
  by t), within the bounded bidirectional lag window (`--lag-tolerance` /
  `--lag-lead-seconds`, default 0 = exact; the OD-089/091 finding: Dead Rail's
  memory clock LEADS the decoded clock ~2.5 s while Oasis LAGS ~4.8 s — the
  window family must stay additive and per-replay).
- Score: same contract as HP (score 1.0, flatness 1.0, strict exact sums per
  window; control windows flat).

## Bounded live-session protocol

1. Approved launches via `scripts/launch-offline-replay-for-od.ps1`
   (monitor-2 placement, gate, G2 anchor — all current machinery reused).
2. Driver mode: dense dumps of the candidate counter over each damage window
   (mirror `hp-diff`'s dense-span dumping), ONE guarded reader lease + ONE
   G2 clock label per batch.
3. Candidate scoring offline: increment-vs-events correlation, score +
   flatness, control windows (no own-damage windows must stay flat).
4. **Honest-negative discipline (OD-090's rule):** bounded sweeps — a fixed
   candidate budget per session; if the family yields no surviving candidate
   after three sweeps (e.g. the counter is on a differently-rooted object),
   declare the family dead append-only and stop — never widen the read
   surface on a hunch.

## Live session attempts — 2026-08-12 (recorded; environment-blocked after sweep 1)

**Sweep 1 (Oasis/Copperfield battle, launched 10:47 UTC, monitor-2
placement verified live):** launch reached OfflineReplayVerified + G2 anchor;
the driver QUALIFIED (7 windows / 2835 dmg / attacker 2852449) and its
FIRST `avatar-stats` probe 400'd mid-battle (70 s in, gate verified). The
fail-closed STATUS WAS SWALLOWED by `Invoke-RestMethod` (no body capture),
so the exact code is unknown — the two candidates are `avatar-scan-not-found`
(the entity-factory Avatar instance, vftable `0x36752a4`, was not found by
the gated Private+Mapped scan — the most likely, given the camera scan
machinery is proven and the gate was verified) or a scan `ReadFailed`
(guard-page class, OD-RECOVERY-080 precedent). NOT a confirmed
honest-negative: INCONCLUSIVE — the object may exist in other battle states.

**Sweeps 2-3 (same battle, 10:58/11:01 UTC): ENVIRONMENT-BLOCKED.** The
second monitor detached mid-session (resize fell back to primary top-left;
first launch had placed the window at -1920,0), and the game landed in the
ReplayList + an ErrorDialog (`blitz-logs` shows `Controller activated:
ReplayList` then `Dialog activated: ErrorDialog` after LoginOnReplayDialog
— the managed replay arg did not take effect), so the gate never flipped
(`launch.awaiting_evidence`, `gamePresent: false`). The blitz log errors are
cosmetic sprite/analytics noise; the dialog is the game-level launch
failure. Mitigation for the next attempt: re-attach monitor 2 / verify the
display config, clean the game's replay staging state, and relaunch.

**Driver diagnosis fix (2026-08-12, pre-staged for the re-run):**
`invoke-hp-diffing-session.ps1`'s `Invoke-HpApi` now captures the HTTP
error body on non-2xx (the fail-closed code, e.g. `avatar-scan-not-found`)
and the wait-probe logs status + `avatarCandidateCount` + clock on its first
iteration — the next live run is fully diagnosable. A chained launcher→driver
orchestration (`.data/l3-run-chain.ps1`, scratch) also removes the
start-latency gap that previously delayed the first probe.

**Session-id correction (2026-08-12, applies to the NEXT attempt):** sweep 1
passed the `.data`-store DECODE id (`019fa44a-b226-…`) as `-SessionId` —
WRONG for the live seam. The OD-088/089 pattern (authoritative): the
launcher's clock anchor reports the LIVE battle session
(`od_launch: clock_anchor … battleSession=019ff5xx-…`), and the driver's
`-SessionId` must be THAT id with `-DataRoot "$env:LOCALAPPDATA\WotBTreader"`
(the host store, which holds the launch-matched decode — OD-089 used
`019ff209-…`, equal to its launcher anchor). With the wrong id the G2 clock
attestation cannot attach even when the scan succeeds. The re-run command
is therefore: launch → read `battleSession=` from the anchor →
`invoke-hp-diffing-session.ps1 -SessionId <that id> -Track damage-dealt
-RegionAnchor avatar-stats -LiveAcquire -DataRoot
"$env:LOCALAPPDATA\WotBTreader" -FailOnNoHit` (the attacker defaults to the
session's viewpoint entity, 2852449 for this battle).

**Sweep 4 — the live lane RESOLVED (2026-08-12, launches 6/7/9; sessions
`019ff5cc` / `019ff5dc` / `019ff5f1`, Oasis Palms).** The playback mechanism
changed and is now PROVEN: with the Wargaming Game Center running +
authenticated, the launcher (full protocol — window move/resize + orange
Watch Offline click) reaches `OfflineReplayVerified` reliably (3/3
launches) when given the **top-level original replay in the game's replays
folder** (`…\replays\20260802_1615__…_Churchill_I_….wotbreplay`, the
OD-075/076 ground truth). The earlier "Error 126 = staging copy"
attribution was REFINED 2026-08-12: the discriminator is the replay's
CLIENT VERSION, not its location — the `.data/launch` Copperfield copy was
an **11.18.0** replay and the 11.19.0.10 game refuses that family with
"Replay Error code: 126" (the 11.19.0 `savanna-…` copy in the same folder
plays fine). The launcher now probes pre-flight (`$cli probe` family
verdict) and fails fast on mismatch. File-association alone still stalls
at `LoginOnReplayDialog` (no orange button) — the launcher's
window-move/resize + orange-click steps remain mandatory. The driver ran with the CORRECTED session id (from the launcher
anchor) + host DataRoot and the **avatar-stats seam RESOLVED live**: every
probe `status='Resolved' candidates=1` across the whole battle (the
entity-factory Avatar instance IS reachable via the gated vftable scan —
sweep 1's inconclusive is now answered: wrong replay + wrong session id).
Session `019ff5f1` captured 20 region dumps spanning the damage windows
(181–282 s). **Live verdict (increment, offset-0x0 quad): top candidate
offset 0x0, score 1.0, matched 3/3 damage windows with EXACT sums (152 /
170 / 1 — the decoded hits), but flatness 0.333 < 1.0 (the quad changed in
control windows too) → HONEST-NEGATIVE under the strict Phase-4 contract**
(`twoReplayRepeatability` not claimed; exact-sum matches at 0x0 are a
strong partial signal but the counter is not flat where it must be).

**Driver fixes shipped with sweep 4 (2026-08-12):** (a) dump-stage
end-of-replay skip + `AvatarAnchorNotFound` added to the teardown list —
previously a battle-end `avatar-scan-not-found` on the LAST dump target
threw and discarded every captured dump (the OD-RECOVERY-090 skip existed
only in the probe stage); (b) the battle-end gate flip
(`discover.gate_not_satisfied`, HTTP 400) is now surfaced as a synthetic
`GateNotSatisfied` teardown status instead of an unhandled throw, so the
probe/dump skips handle it uniformly (still fail-closed outside the
last-40 s window); (c) removed a redundant pre-loop verdict that invoked
`hp-diff` against the suffix-less base `$SnapshotsPath` (which does not
exist in avatar-stats mode) — that was the `ArgumentOutOfRangeException`
after the write phase. Verified: parse clean + ASCII-only; session 9 wrote
its 20 dumps before the gate flip and the verdict ran offline on the
captured file.

## Definition of done (Phase-4 standard)

`twoReplayRepeatability = true`: the same counter agrees on BOTH Oasis Palms
and Dead Rail (strict exact sums, score 1.0, flatness 1.0, controls flat).
Then a publication package (`g2-damage-dealt-publication-draft.md`) +
operator-gated apply, mirroring HP/yaw. The live scoreboard's own row and the
frame's `DamageDealt` field are the consumers; enemy/teammate per-row damage
stays honest-unknown (their stats objects are not in the player's memory
map) — the live scoreboard is own-row-only, documented as such.

## Sequencing

1. Operator-approved publication applies (HP then yaw) — pre-requisite order. **DONE 2026-08-12 (OD-RECOVERY-092).**
2. Static candidate search (avatar/player-stats family). **DONE 2026-08-12** (above).
3. Bounded live sessions + increment correlation (this plan) — seam PRE-STAGED + REHEARSED 2026-08-12 (`-RegionAnchor avatar-stats` + per-candidate driver + 6 tests + offline rehearsal PASSED on a real decoded session, incl. the PHASE-4 two-replay simulation with offsets agreeing across Oasis + Dead Rail, `invoke-avatar-stats-rehearsal.ps1`); next is the approved launch run (monitor-2 placement, gate, G2 anchor all reused).
4. Phase-4 repeat on the second replay.
5. Publication package (operator-gated).
6. Item 7 (hardware atomicity) remains LAST regardless.
