# Record-diffing groundwork — replay event inventory (2026-08-10)

Purpose: inventory the "known events" side of the record-diffing discovery
playbook (dump the entity record with a trusted reader, correlate byte
changes against authoritative replay events). This doc records what exists
today so the next discovery milestone (player HP, entity-id binding) starts
from the actual data model, not assumptions.

## What the replay decoder already exposes

`WotbReplayDecoder` builds `CanonicalEvent`s with kinds
(`src/WotBTreader.Core/TelemetryModels.cs`):

| Kind | Replay source | Carries |
|---|---|---|
| `ParticipantObserved` | arena participant packets | account/entity ids, team |
| `Position` | position packets | per-participant trajectory samples |
| `Damage` | type-8 subtype-1 health-change ledger (`TryReadHealthChange`); amount = victim HP delta seeded by the type-5 max-HP broadcast | `AttackerEntityId`, `VictimEntityId`, `Damage`, `ReplayTime`, evidence |
| `Destroyed` | battle events | victim entity id + replay time |
| `BattleStarted` / `BattleEnded` | battle lifecycle | replay clock bounds |

Each `CanonicalEvent` has `Sequence`, `ReplayTime`, `ParticipantId`,
`EntityId`, `ValuesJson`, `Confidence`, `Evidence` — so damage/destroyed
events are joinable to the roster by entity id **and** have replay
timestamps. `ReplayCapability.Damage` is set when damage events decode
(`WotbReplayDecoder.cs:405`).

`BattleStats` (per participant, from battle_results.dat) adds totals:
`DamageDealt`, `Shots`, `HitsDealt`, `PenetrationsDealt`,
`EnemiesDestroyed`, `DamageBlocked`, etc. (`TelemetryModels.cs:147`).

## What is persisted

- `canonical_events` table — every decoded canonical event, including
  Damage/Destroyed, with kind, replay time, participant/entity id,
  values JSON, evidence (`SqliteDecodeRunRepository.cs:608-618`).
- Participants (`entity_id`, `tank_name`, `account_id`, `clan_tag`) and
  position trajectory samples.

## The query path (implemented 2026-08-10)

`IHpGroundTruthProvider` / `SqliteHpGroundTruthProvider` now expose the
Damage/Destroyed `canonical_events` for a session: replay time, victim entity
id, victim participant id, best-effort `damage`/`attackerEntityId` parsed
from `values_json` (null when unparseable — never guessed), plus the session
duration; fail-closed on an unknown/zero-duration session. Registered in
`AddSqliteStorage`. `SqliteTrajectoryGroundTruthProvider` (positions) is
unchanged.

## The memory-side diffing harness

### Implemented (2026-08-10, offline core)

`WotBTreader.Core.Discovery` now carries the pure, synthetic-tested core
(`RecordDiffingTests`, 9 tests):

- `RecordChangeBucketer.Bucket(snapshots)` — time-buckets trusted-reader
  region dumps (full-region bytes + replay clock label) into
  `ByteChangeWindow`s: one per consecutive snapshot pair whose bytes
  differ, with the exclusive/inclusive time span (From, To]. Snapshots must
  be strictly increasing (fail-closed); unchanged pairs produce no window.
- `HpDamageCorrelator.Correlate(windows, damageEvents, targetEntityId,
  matchMode)` — for each window, sums the target entity's damage events
  whose replay time falls in (From, To]; a candidate is a 4-byte-aligned
  int32 whose value drop matches −Σ damage per mode: **Strict** (default)
  requires the drop to equal the summed damage exactly; **Lenient** accepts
  any drop ≥ Σ damage (the destroying hit's overkill, multi-source
  under-sums). Ranked by score (matched / damage windows), then
  **flatness** (fraction of zero-damage control windows in which the field
  was UNCHANGED — separates HP, flat except when hit, from monotonic
  drains that drop every window), then precision (matched / changed
  damage-windows), then offset.Proven by synthetic fixtures: HP-at-+0x48 ranks first across three
damage windows; an unrelated changing counter is never a candidate;
sparse snapshots sum multiple events in one window; other entities' damage
is ignored; a damage window with no HP drop yields no candidates; Lenient
matches the overkill killing blow (HP 500 → 0 vs 150 damage) while still
rejecting a small coincidental drop and subsuming exact matches; a
realistic event mix (Damage + Destroyed + unrelated damage) still ranks
HP first; a monotonic drain (drops ≥ every window's sum) ties HP on score
under Lenient but is demoted by **flatness** (HP unchanged in control
windows, drain keeps dropping — `Correlate_Lenient_DrainingDecoy_RanksBelowHp_OnFlatness`);
a magnitude-mismatched decoy that is flat in control windows still ties
Lenient but is EXCLUDED by the Strict confirmation
(`Correlate_Strict_ExcludesMagnitudeMismatchedDecoy_ConfirmsHp`). **End-to-end compose proof** (`SqliteHpGroundTruthProviderTests`):
seeded `canonical_events` → `IHpGroundTruthProvider` (REAL `values_json`
damage extraction) → `RecordChangeBucketer` → `HpDamageCorrelator` finds
the HP field 2/2 — the two halves compose with the actual data shape.
**Documented limitations:** events outside the observed snapshot
span are observation gaps and do not inflate the denominator; healing (no
in-battle healing in WoTB) is not modeled. The int32-only scan is the
historic default; the int16 candidate pass (added 2026-08-11 — the static
playerHP evidence pins HP as int16 at [entity+0xB8]) is opt-in per call and
on by default for the HP (`decrement`) direction in `hp-diff`.
`Correlate_Int16ModeOn_RanksInt16HpFieldFirst_UnderStrict` proves the
int16 field ranks first under Strict while the coincidental int32 read at
the same offset (health + alive<<16) cannot confirm through the destroy
window.

### Remaining: the live trusted reader (approved-session step)

The correlation core needs only the memory side fed in: a trusted reader
dumping the entity record around the avatar spine (via the walkable
position chain / `entityLookup`) at replay-clock-labeled times, bucketed by
the core above and matched against the damage events this provider returns
(`victim entity id → HP-drop event at replay time T`). No live session has
run; the reader is the next approved-session step.

#### Live session plan (pre-staged for the approval gate)

The L0 seam is now IMPLEMENTED (2026-08-10):

1. **`EntityRecordRegionReadRequest(EntityId, RegionLength, RegionAnchor)` /
   `EntityRecordRegionReadResult(...)`** — shipped end-to-end: the caller
   supplies only the decoded entity id + a bounded region length (≤ 4096
   bytes, enforced fail-closed) + the region anchor; the coordinator owns
   process identity, resolves the entity address via the exact-build resolver
   (`ResolveEntityPositionAddressAsync` under the same lease), requires
   `OfflineReplayVerified` + current authorization, reads the region through
   the guarded reader (`ReadAsync`), labels the dump with the replay clock
   from the G2 same-decoded-clock snapshot (≤ 2 s bound, `EstimatedReplayTime`
   becomes `ReplayTimeSeconds`), and returns ONLY the bytes + replay time —
   never an absolute address. Exposed as `POST
   /api/v1/game/discover/entity-region` (base64 bytes). Verified by 8
   coordinator tests (gate, length clamp, build identity, unresolved entity,
   clock attestation, bytes-only, tank-record anchor deref + fail-closed)
   + 4 web endpoint tests (base64 bytes, failure, anchor forwarding, invalid
   anchor). The remaining session-driver wiring below is what the live
   session consumes.

   **Region anchor (CORRECTED 2026-08-10 — the L1 wiring originally pointed
   at the wrong object; CORRECTED AGAIN 2026-08-11 with static HP
   evidence).** The HP / damage-dealt harness anchors the dump at the
   per-entity TANK record `[entity+0x3C]` (Ghidra-candidate layout),
   NOT the movement ring record the position resolver reads. The ring
   record is 0x38 bytes (position +0x10, velocity +0x28, stride 0x38) — a
   `+0x48` offset there would land 0x10 bytes past its end, i.e. unrelated
   memory. The seam now carries `RegionAnchor: ring-record |
   entity-tank-record | entity-base`; for the tank-record anchor the
   coordinator dereferences `[entity + 0x3C]` itself under the same
   guarded lease (validating the pointer before any read) so the caller
   still only receives bytes. The resolver now also exposes the resolved
   entity base in `Type10EntityPositionAddressResult` to make that
   dereference possible.

   **2026-08-11 static correction — HP lives on the ENTITY BASE, not the
   tank record.** `VerifyPlayerHpChain.java` (hash-bound, 16/16 checks,
   sha256 `1cda5c31…1760307d`, verdict `player-hp-chain-verified`) pins the
   current-health field as a SIGNED int16 at `[entity+0xB8]` on the entity
   base record itself, with the alive byte at `[entity+0xBA]` and the
   healing int16 at `[entity+0x11E]`. The evidence chain: VehicleGameLogic
   vftable slot 1 (0x31b560) is the byte-verified entity getter
   `MOV EAX,[ECX+0x4]; RET`; `set_health`
   (`FUN_016ee450`) reads the OLD value through it
   (`MOVSX EDI,word ptr [EAX+0xb8]`), `set_healingHealth`
   (`FUN_016ee350`) reads `[EAX+0x11e]`, `set_maxHealth`
   (`FUN_016eeb70`) reads `[EAX+0x11c]`, `set_isAlive`
   (`FUN_016ee990`) checks the `[EAX+0xba]` byte and `[EAX+0xb8]` word,
   and `set_gunAnglesPacked` (`FUN_016ee230`) reads the `[EAX+0x7e]`
   word (two 6-bit packed angles); the state-sync writer
   `FUN_0166b9f0` stores the same offsets (int16→`+0xB8`/`+0x11C`/`+0x11E`,
   byte→`+0xBA`, word→`+0x7E`) and the diff-notify twin
   `FUN_01675f60` does the same with property-changed listener dispatch
   (vtable +0x68).

   **The entity-base health block (11.19.0.10, statically verified):**

   | Offset | Width | Field |
   |---|---|---|
   | `+0x7E` | int16 | gun angles packed (2 × 6-bit, `set_gunAnglesPacked`) |
   | `+0xB8` | int16 | current health (`set_health`) |
   | `+0xBA` | byte | alive flag (`set_isAlive`) |
   | `+0x11C` | int16 | max health (`set_maxHealth`) |
   | `+0x11E` | int16 | healing health (`set_healingHealth`) |

   So the HP session anchors at **entity-base** with a region length ≥ 0x120
   and correlates **int16** candidates (`hp-diff --int16 true`, on by
   default for the HP/decrement direction). The tank-record anchor remains
   correct for the facing/rotation work (the +0x2C tail of the transform
   layout).
2. **Session driver** — `scripts/invoke-hp-diffing-session.ps1` runs the
   whole flow: gate → **qualify the victim from the decoded replay**
   (see below — do NOT default to the player's own entity) → print the
   event-bound dump schedule → acquire the bounded series of region dumps
   at replay-clock-labeled times (the GATED seam — the driver exits 3 with
   the contract until the region read lands; pass `-SnapshotsPath` to run
   the verdict against an existing dump file) → `hp-diff
   <snapshots.json> --session <id> --victim <entity> --mode lenient` for
   the verdict (with `-FailOnNoHit` for fail-closed automation). The dump
   schedule is per-hit: the extractor's `--hp-delta` emits a `dump_schedule`
   (a dump just BEFORE and AFTER each damage event, ±0.2 s, so each change
   window captures exactly one hit) plus flat control dumps in the gap
   segments → `RecordChangeBucketer` → `HpDamageCorrelator` (Lenient mode
   first — overkill) → verdict. Since the 2026-08-11 static correction the
   driver defaults to `-RegionAnchor entity-base` (HP lives on the entity
   base record at +0xB8, not the tank record at [entity+0x3C]) with a
   default `-RegionLength 320` (≥ 0x120 covers +0x11E healing), and the
   HP/decrement verdict path passes `--int16 true` automatically.
3. **Verdict contract** — the top candidate offset with score, matched /
   total damage windows, and the matched window list (replay times + deltas
   vs. the provider's events). A candidate is a HIT when it (a) matches ≥ 2
   damage windows with score 1.0 in Lenient mode, (b) has **flatness 1.0**
   (unchanged in every zero-damage control window — the control dumps are
   load-bearing), and (c) **confirms under Strict**: ≥ 2 windows where the
   drop equals the exact damage sum (excludes magnitude-mismatched decoys
   that Lenient admits — e.g. another victim's HP or a heavy drain that is
   flat in control windows; proven by
   `Correlate_Strict_ExcludesMagnitudeMismatchedDecoy_ConfirmsHp`),
   AND the matched offsets agree across the two independent replays (the
   Phase-4 repeatability rule).
4. **Evidence + privacy** — record the session under an OD-RECOVERY id,
   keep `publicProcessAddressesOrRawBytes: false` (raw region bytes are
   session evidence, never published), and publish only the offset + chain
   form through the operator gate if the candidate repeats.

#### Victim selection — verified against decoded replay data (2026-08-10)

The session must track an entity that **actually takes damage**. Verified
from `.data/treader.db` (11.19.0 decode runs): **the player's own entity
(`mrkool1138`) took ZERO damage in both 11.19.0 replays** (Oasis Palms 0
events, Dead Rail 0 events — the viewpoint tank survives unhit), so a
session tracking the player would hand the correlator an all-flat series
and zero windows to match. Qualify the victim before the session with one
command:

```
python scripts/python/replay-delta-extractor.py --session <id> --top-victims 8 --window 10
python scripts/python/replay-delta-extractor.py --session <id> --hp-delta --victim-entity <entity_id> --window 10
```

Require **≥ 2 damage windows** (the verdict contract needs ≥ 2 matched
windows); otherwise pick the next victim by hit count. The `--hp-delta`
output's hit-window list is the event-bound dump schedule. (Numbers
cross-checked against direct SQL on 2026-08-10.)

> **2026-08-10 correction — replay-tick unit:** an earlier draft of this
> plan quoted 10×-too-large times (e.g. "900–1680s of a ~2798s replay").
> The decoded DB stores replay ticks as .NET ticks (10⁷/s —
> `position_samples` max tick ≈ `battle_sessions.duration_ticks` and the
> Oasis Palms battle is 279.9s, not 2798s), but the extractor's
> `TICKS_PER_SECOND` was 10⁶. Fixed to 10⁷; all schedules above are in
> real replay seconds, and the hit-window bucketing now uses true 10s
> windows (verified window-by-window against the raw event ticks). The
> movement-proxy participant ranking (a separate dead-code bug — it only
> compared consecutive samples) was also fixed to scan ~1s-apart pairs.

**Oasis Palms** (session `019fdff7-8dcf-7426-8547-9fb8cc3eb07b`, 11.19.0,
battle ≈ 280s) — victim **3760578** is the strongest candidate: 9 events /
4,028 damage, hits at t = 90.4–167.4s, in six ten-second windows
**90–100, 100–110, 130–140, 140–150, 150–160, 160–170s** (window sums
256 / 1278 / 664 / 386 / 933 / 511 — verified against the raw
events: 90.45, 100.93, 107.81, 136.52, 143.42, 150.31, 156.62, 157.23,
167.42s). The dump series concentrates there, plus 2–3 flat-window
control dumps (e.g. ~30s and ~230s) to confirm the field is otherwise
unchanged. Alternative victims in the same replay: 3760571 (7 hits,
118.8–175.4s), 3760574 (6 hits, 114.3–157.5s), 3760575 (6 hits, late,
245.4–274.0s).

**Dead Rail** (session `019fb86c-c8e7-7004-9df6-a574f5a7835b`, 11.19.0,
battle ≈ 271s) — the second independent replay for the Phase-4
repeatability rule: victim **2549399** — 18 events / 4,647 damage,
hits at t = 114.4–152.4s, in five ten-second windows **110–120,
120–130, 130–140, 140–150, 150–160s**. So the two-replay verdict
contract is fully pre-staged: Oasis Palms victim 3760578 + Dead Rail
victim 2549399, both with ≥ 2 damage windows, schedules above.

The walker resolves **any** entity id through `entityLookup` (the
published chain takes the target id per walk and now exposes
`ResolvedEntityAddress`), so a non-viewpoint enemy is resolvable — the
HP harness only needs the entity base + the `[entity+0x3C]` tank-record
region (Ghidra-candidate layout, test-local until live verification); it
does not depend on the live-verified viewpoint ring-record path.

**Mechanism proven end-to-end offline** (2026-08-10,
`Walk_PublishedTable_EntityBase_AnchorsHpRegionDump_CorrelatorFindsHp`):
walk the published `playerPositionX` chain on full-spine synthetic memory
→ the walker exposes the found entity base → deref `[entity+0x3C]` to the
tank record → dump the 0x100-byte region at replay-clock-labeled times
(HP dropping by the exact damage amounts) → `RecordChangeBucketer` →
`HpDamageCorrelator` (Lenient) ranks the HP int32 at `+0x48` first with
score 1.0 across both damage windows. Every stage of the planned session
flow is now proven on the real published table; only the live read
remains.

**Two-replay rehearsal (2026-08-10, offline evidence):** the full session flow
was rehearsed end-to-end on BOTH qualified replays against their real event
timelines — the extractor's `dump_schedule` times, HP at `+0x48` dropping by
the exact cumulative damage at each real hit tick (step function), flat
control dumps, `RecordChangeBucketer` → `HpDamageCorrelator` (Lenient) →
verdict, via `scripts/invoke-hp-diffing-session.ps1 -SnapshotsPath`:

| Replay | Victim | Verdict | Offset | Score / Flatness | Matched |
|---|---|---|---|---|---|
| Oasis Palms | 3760578 | HIT | `+0x48` | 1.0 / 1.0 | 2/2 |
| Dead Rail | 2549399 | HIT | `+0x48` | 1.0 / 1.0 | 13/13 |

Both verdicts satisfy the contract (score 1.0 + flatness 1.0 + ≥ 2 exact-sum
Strict matches) and **agree on the matched offset `+0x48`** — the Phase-4
two-replay repeatability rule is proven offline in rehearsal; the live
session only replaces the synthetic dumps with the trusted reader's.
Construction note: dumps must bracket the hits at the scheduled ±0.2 s
offsets and NOT land exactly on an event tick — placing a dump at the event
time itself creates a zero-width boundary window whose sum lands in the
wrong bucket (rehearsal hit this; the step-function rebuild fixed it).

**IMPORTANT (2026-08-10 cross-check, SUPERSEDED 2026-08-11): `+0x48` is a
SYNTHETIC FIXTURE offset** — the mechanism-proof test planted the HP int32
at `+0x48` to prove the correlator machinery; it is NOT Ghidra-derived. The
static evidence has now LANDED (2026-08-11): `VerifyPlayerHpChain` pins
current HP as a signed int16 at `[entity+0xB8]` on the entity BASE record
(alive byte +0xBA, healing int16 +0x11E), with the read/write site pair
byte-verified (set_health / set_healingHealth reads, state-sync + diff-notify
writers). The old expectation — an int32 HP inside the transform object at
`[entity+0x3C]` — is refuted: the field is int16 and lives 0x7C bytes past
the transform pointer, on the entity record itself. The live session still
DISCOVERS the offset empirically (dumps the entity-base region, the
correlator ranks the field), but the plan now expects int16 at +0xB8 and the
correlator's int16 pass (`--int16 true`, default for HP) makes that
findable — an int32-only scan would fold health + alive byte + padding into
garbage and miss it.

**Simulation reading:** the extractor's `--hp-delta` survival simulation
at `target=0` measures the flat-window pass rate (3760578 at 10s windows:
11/17 = 0.65 → survival ≈ 0.12 / 0.01 over 5 / 10 rounds). The honest
reading: a single-target rolling delta campaign sheds the true HP field in
any round whose window contains a hit — the per-window
`HpDamageCorrelator` (window damage sum vs. per-window drop) is the right
tool, not the rolling pilot; this is what the session flow already uses.

All offline halves are proven and green; the approval ask is exactly the
scope above (one gated region-read addition + one session), with the
correlation core and ground-truth provider already in place.

## Damage-dealt track (increment direction) — pre-staged 2026-08-10

The scoreboard damage-dealt counter is the mirror image of HP: it RISES by
the exact damage of each event the target DEALT (attacker-side). The
correlator now supports a `DamageCorrelationDirection` (Decrement/HP,
Increment/damage-dealt); the Increment direction keys the event sum on
`AttackerEntityId` and matches `delta == +Σ` (Strict) / `delta >= +Σ`
(Lenient). Ground truth verified from `.data/treader.db` (11.19.0): **the
player's own stat IS a viable target** — unlike HP, the viewpoint entity
landed hits in both replays:

| Replay | Player entity | Dealt events | Damage | Nonzero 10s windows |
|---|---|---|---|---|
| Oasis Palms | 3760577 | 5 | 2184 | 4 (177.8–274.0s) |
| Dead Rail | 2549401 | 5 | 1569 | 4 (154.5–257.9s) |

Tooling (all offline, verified 2026-08-10):

- Extractor: `--damage-dealt [--attacker-entity <id>]` — the increment
  mirror of `--hp-delta` (default target = the session's viewpoint entity),
  emitting the same `dump_schedule` shape + an `hp-diff` command with
  `--direction increment`. The schedule drops 0/unparseable-damage events
  (a dump pair around them would waste two lease-bounded dumps).
- CLI: `hp-diff --direction increment|decrement` (default decrement —
  existing callers unchanged), echoed in the output envelope.
- Driver: `invoke-hp-diffing-session.ps1 -Track damage-dealt` (default
  `hp`) — qualification, schedule print, and the verdict command all
  switch direction automatically.
- Unit proofs: 6 new `RecordDiffingTests` — increment field ranked first
  (score/flatness 1.0), default direction unchanged (increment-only field
  is NOT a candidate), victim-side events ignored under Increment, Strict
  excludes magnitude-mismatched risers, flatness demotes monotonic risers,
  Lenient admits overcap rises while Strict rejects them. Plus 2 CLI tests
  (end-to-end increment HIT at `+0x48`, unknown `--direction` rejected).

**Two-replay rehearsal (2026-08-10, offline evidence):** the full session
flow rehearsed on both replays' real attacker timelines (synthetic region
with the damage-dealt int32 rising by the exact cumulative damage at each
real hit tick, step function):

| Replay | Target | Verdict | Offset | Score / Flatness | Matched |
|---|---|---|---|---|---|
| Oasis Palms | 3760577 | HIT | `+0x48` | 1.0 / 1.0 | 5/5 |
| Dead Rail | 2549401 | HIT | `+0x48` | 1.0 / 1.0 | 5/5 |

Both verdicts satisfy the contract and agree on `+0x48` — the Phase-4
repeatability rule proven for the increment direction too. Re-verified
through the DRIVER after the L1 tank-record anchor correction
(2026-08-10): `invoke-hp-diffing-session.ps1 -Track damage-dealt` with
`-RegionAnchor entity-tank-record` (the default) reproduced both HITs
(5/5, flatness 1.0) on the real attacker timelines, and the no-host
(`-LiveAcquire` without a web host → `rendezvous_unavailable`, fail-closed)
and `-FailOnNoHit` (exit 1) paths both behave.
Construction
note (same trap as HP, caught in rehearsal): trailing control dumps must
carry the step-function value at their time, not the final cumulative —
a control dump after a hit but before the next must show the value as of
that time, or the control window falsely counts as a field change and
flatness drops to 0 (the first Oasis Palms build failed exactly this way).

Same caveat as HP — and the same fixture caution (see the HP rehearsal note
above): `+0x48` is the planted test offset, not a verified location; the
live session discovers whichever int32 actually moves with the target's
scoreboard damage. HP and damage-dealt both rehearsed to the same planted
offset purely because both fixtures used it; a live confirmation of either
would give the transform-object layout its first stat-field anchor.

## Facing/yaw track — packet-derived ground truth (2026-08-10)

**The type-10 position packet's tail carries the entity's rotation.** The
49-byte packet (entityId, spaceId, vehicleId, x/y/z, + 25-byte tail) was
re-scanned from the stored artifacts at decode time (the persisted evidence
is ciphertext; the plaintext tail is only visible during decoding) and the
tail decoded: **yaw float32 at payload +36, pitch +40, roll +44** (all
radians). The yaw is validated 1:1 against the position-derived heading on
BOTH 11.19.0 replays while moving forward (Oasis Palms 144/157 moving
windows within 15°, Dead Rail 109/122), is exactly constant through long
stationary stretches, and stays unchanged during reversals (the tank's
facing, not its velocity — the velocity-vector reading matches only 14% of
moving windows vs 79% for yaw). This corrects the earlier replay-format
claim that the +36–47 triple was a physics velocity with no orientation
field anywhere in the stream.

**Durably persisted:** the decoder now extracts yaw/pitch/roll and stores
them as `position_samples.yaw/pitch/roll` (migration 5, added 2026-08-10;
re-decoded both replays — 2812/2812 and 2784/2784 viewpoint samples carry
yaw). The ground truth is now DB-queryable, exactly like x/y/z.

**What this unlocks for the memory side:** the facing track no longer
suffers the pivot-turn blindness of position-derived heading (the packet IS
the authoritative rotation), and the record-diffing playbook becomes viable:
dump the ring record region at replay-clock-labeled times (the same
`EntityRecordRegionReadRequest` seam the HP/damage-dealt plans need), and
correlate a float32 candidate whose per-window delta matches the packet
`yaw` delta (radians, wrap-aware). Control windows are the stationary
segments — the packet yaw is exactly constant there (proven), so flatness
separates the yaw field from drifting decoys. The ring record is 0x38
bytes (position +0x10/+0x14/+0x18, velocity +0x28); the unaccounted tail
(+0x2C..+0x37) is the first place to look for the rotation floats. The
live read is the remaining input; the offline ground truth is complete.

## Facing correlator — rehearsal proven (2026-08-10)

The `HeadingCorrelator` (Core/Discovery) ranks 4-byte-aligned float32 fields
whose wrap-aware delta matches the packet yaw delta per replay-time window:
TURN windows (expected |delta| > the 0.05 rad match tolerance) form the
score denominator, and stationary CONTROL windows (|delta| ≤ tolerance)
form the flatness denominator —
the packet yaw is exactly constant when stationary, so flatness separates the
yaw field from drifting decoys. The lookup is nearest-sample (the dump's
replay clock lands on the packet the state was sent at), fail-closed outside
the sample span, with ties resolving to the earlier sample. 8 unit tests pin
wrap-across-π, decoy demotion, entity filtering, and fail-closed behavior.

`yaw-diff <snapshots> --session <guid> --victim <entity>` (mirror of `hp-diff`)
ran the full rehearsal on BOTH 11.19.0 replays with the L2 driver's real
dump schedule (`--yaw-dump` emits one dump pair per turn segment): synthetic
ring-record region dumps whose float32 at the predicted yaw offset **+0x2C**
carry the real packet yaw (nearest-sample, the correlator's lookup
semantics), with a constant decoy at +0x20:

| Replay | Verdict | Offset | Score | Flatness | Matched / Controls |
|---|---|---|---|---|---|
| Oasis Palms | HIT | `+0x2C` | 1.0 | 1.0 | 27/27 turns, 0 control changed |
| Dead Rail | HIT | `+0x2C` | 1.0 | 1.0 | 35/35 turns, 0 control changed |

(Decoy note: a decoy that is a CONSTANT OFFSET of the real yaw — e.g. yaw +
1.7 — reproduces every delta and legitimately ties the true field; the
correlator then breaks the tie by offset ascending. A rehearsal decoy must
NOT track yaw to prove discrimination; the live session needs no decoy at
all.)

Construction rule fixed in correlator (2026-08-10, same class as the HP
flatness trap): a window whose expected delta sits in the dead band between
0.02 rad and the 0.05 rad match tolerance is skipped as "unchanged" by the
matcher (observed |delta| ≤ tolerance) yet previously counted in the score
denominator — a perfect field capped at 27/30. The correlator now classifies
such windows as CONTROL windows (|expected| ≤ match tolerance), so the score
denominator holds only provable turns and a true yaw field reaches 1.0/1.0.
Rehearsal windows are still selected well above tolerance (|expected| >
0.1 rad) by the dump-pair picker, but the classifier no longer depends on
that discipline. The memory side is the remaining input: the gated live
region read dumps the ring record at replay-clock-labeled times and the same
correlator confirms whether the rotation floats live in the +0x2C..+0x37 tail.

### L2 live-session plan — facing/yaw (pre-staged for the approval gate, 2026-08-10)

The facing live session consumes the SAME region-read seam as L1/L3/L4
(`EntityRecordRegionReadRequest/Result`, ≤ 4 KB, replay-clock-labeled via
G2) and shares the O5 rehearsed target:

1. **Rehearsed pilot target (new 2026-08-10).** `replay-delta-extractor.py
   --heading-delta` emits the per-window packet-yaw delta series
   (wrap-aware, movement-gated) for the exact participant the live session
   will dump. Two-replay rehearsal:

   | Replay | Windows | Yaw Δ median | Yaw Δ p90 | Yaw Δ max | Seam crossings |
   |---|---|---|---|---|---|
   | Oasis Palms | 1 644 | 0.011° | 24.4° | 47.1° | 0 |
   | Dead Rail | 1 728 | 2.92° | 48.5° | 118.2° | **5** |

   The live driver is `scripts/invoke-facing-session.ps1` (2026-08-10):
   step 1 runs `--yaw-dump` (the dump-pair schedule, one pair per turn
   segment ≥ 0.1 rad), step 2 POSTs `/discover/entity-region` with
   `regionAnchor=ring-record` at every scheduled replay-clock time (plus
   `-ControlTimes`) and fail-closes on `sameDecodedClockProven=false`, step
   3 runs `wotbtreader-cli yaw-diff` with `-FailOnNoHit` support. The
   full-schedule rehearsal on both replays is in the correlator section
   above (27/27 and 35/35, both HIT at `+0x2C`).

   The seam-crossing count is the wrap-awareness evidence: Dead Rail's 5
   crossings are windows where a naive (non-wrap-aware) delta would read
   ~2π wrong, and the correlator's wrap-aware matcher is exactly what
   handles them.
2. **Dump-pair picker rule (mirrors the rehearsal construction rule).**
   TURN windows must have |expected yaw Δ| > 0.1 rad (0.05 rad match
   tolerance × 2) — the O5 p90 column (24–48°) already confirms such
   windows exist on both replays. CONTROL windows are the stationary
   segments: the packet yaw is exactly constant there (proven 1:1), so
   flatness 1.0 separates the yaw field from drifting decoys.
3. **Verdict contract** — same shape as the HP plan: top candidate offset
   with score, matched / total turn windows, flatness over control windows,
   AND repeatability: the same offset must hit on both 11.19.0 replays
   (the ring record is 0x38 bytes; the +0x2C..+0x37 tail is the first
   place to look — the predicted +0x2C rehearsal HIT already scored 1.0/1.0
   on both).
4. **Evidence + privacy** — record under an OD-RECOVERY id,
   `publicProcessAddressesOrRawBytes: false`; publish only the offset via
   the operator gate (P3 facing/yaw publication).

### Type-39 scene-point packet — static + behavioral triage (2026-08-10)

Behavioral evidence (Oasis Palms, both replays consistent): per-frame
(~60 Hz) 28-byte / 7-float32 record; smooth drift; NEVER matches the
viewpoint tank, any entity, the team centroid, or the bounding-box anchor;
NOT a third-person camera (offset from the tank varies 30→507 m and sits
~38 m BELOW the tank — no camera sits there); settles on fixed anchors
(spawn corner at battle start, a victory point at battle end); altitude
stays below terrain (y −4..+15 vs terrain 33–42).

Static pass (`FindScenePointWriter.java`, 2026-08-10): the bit-exact
constant -0.0011081547 (f32 0xBA913F80, present in many packets) has ZERO
hits in the binary — it is computed at runtime, not an immediate, so the
writer is not locatable by that anchor. The Rust oracle (wotbreplay-parser)
also reports type 39 as `Unknown`. **Status: structure fully
characterized, semantics + writer unresolved.** Next anchors (future
passes): the f64 representation of the constant, the event-stream
serializer call sites around the type byte 0x27, or a live capture of the
writer. Do NOT treat type-39 as zone geometry or as a confirmed camera
until one of those lands.

## Overlay frame contract — ReplayFrameSource (2026-08-10)

The replay overlay's data seam is in place: `Core/Overlay` defines
`OverlayCamera` (viewpoint position + yaw/pitch/roll), `OverlayTankState`
(world pos, facing, HP fraction 0..1, alive, team, name/clan/tank/class,
distance), and `OverlayFrame` (time + camera + tanks). `IOverlayFrameSource`
(Application/Storage) + `ReplayFrameSource` (Application/Replay) build frames
from `ISessionQueryRepository.GetProjectionAsync` — nearest-sample per entity
(fail-closed: no sample at/before frame time means the tank is omitted), HP
fraction from the canonical damage events (1.0 when the tank took no damage),
alive from the Destroyed event, camera from the viewpoint entity.

**2026-08-11 — max HP is IN the replay after all (type-5 spawn broadcast).**
`wotbreplay-inspector dump-data` on both replays shows the type-5
full-state broadcast carries `u32 eid @ +0x00` and `u16 currentHP @ +0x33`
(LE), fired 1–3× per tank at spawn. Validated four ways: (1) the author's
value (700) equals `battle_results.hitpoints_left` exactly on both replays
(Churchill I, tank_id 2897, same account); (2) the value is monotonic
non-increasing per tank across its broadcasts (Dead Rail 2549397: 540 → 501
after damage), so the **first broadcast per tank = max HP**; (3) aggregate
bounds hold — total `damage_dealt` ≤ Σ first-broadcast values (8964 ≤ 12140
Oasis, 6227 ≤ 8500 Dead Rail); (4) the same tank_id reads the same value
across replays. So `ReplayFrameSource` can emit a true HP fraction:
maxHP from the tank's first type-5 broadcast, current HP from the damage
ledger, instead of clamping to 1.0. The overlay renders only `OverlayFrame`, so
a future live source behind the same interface is a data-source swap, not a
rewrite. 5 unit tests cover nearest-sample selection, fail-closed omission,
the HP arc + destroy, origin camera, and the missing-session guard.

## Velocity + pitch/roll — offline validation (2026-08-10)

`scripts/python/velocity-pitch-validation.py` validates the rotation axes
against geometry on both replays (viewpoint entity):

| Metric | Oasis Palms | Dead Rail |
|---|---|---|
| Yaw vs motion heading (incl. reversals) | 1634/1634 (100%) | 1307/1307 (100%) |
| Pitch = −slope (multi-second moving windows) | 155/155, residual −0.001 ± 1.3° | 113/113, residual −0.002 ± 0.8° |
| Max speed (finite difference, dt ≥ 50 ms) | 13.0 m/s | 11.0 m/s |
| Roll range (stationary-constant) | [−0.401, +0.264] | [−0.166, +0.088] |

Findings: (1) the packet **pitch is the vertical facing with a flipped sign**
relative to atan2(dY, dH) — pitch ≈ −slope, validated to ~1° — so the ring
record's rotation floats can be correlated with the flipped-sign delta. (2)
Velocity must be computed on dt ≥ 50 ms pairs only: the replay carries
sub-ms duplicate packets whose tiny-dt finite difference fabricates ~22 m/s
spikes (the 13.0/11.0 m/s figures above are the honest top speeds). (3) Roll
is exactly constant when stationary and dynamic during movement — the third
rotation axis, consistent with banking. `--self-test` pins the forward/reversal
and pitch=−slope semantics with a synthetic fixture.

## WorldToScreen projection + overlay-frame preview (2026-08-10)

Phase 1 O1 is in place: `Core/Overlay/WorldToScreen` projects a world point
to viewport pixels given the camera pose (pos + packet yaw/pitch) and a
vertical FOV. Conventions match the decoded telemetry: yaw 0 faces +Z
(yaw ≈ atan2(dx, dz)), camera-space +X right / +Y up / +Z forward, pinhole
perspective with focal = (h/2)/tan(fov/2), screen origin top-left. Two
conventions were pinned by tests: world +X is on the camera's RIGHT when
facing +Z, and the up vector is cross(forward, right) so a pitched-up
camera drops the horizon below center (how a real camera renders). Points
at/behind the camera return null (fail-closed); a camera without rotation
evidence (pre-migration-5 samples) returns null. 9 unit tests.

The `overlay-frame <time> --session <guid> [--fov --width --height]` CLI
command renders one frame through `ReplayFrameSource` + `WorldToScreen`:
viewpoint camera with rotation, every roster tank with name/team/HP/distance
plus projected screen X/Y/depth (or behind-camera), sorted by distance. Two
real-data findings while previewing Oasis Palms: (1) the position stream
carries non-participant entities (a duplicate "self" stream that starts at
the viewpoint's spawn then teleports to origin, plus projectiles/debris) —
the frame source now renders ONLY roster entities, so nameplates never
target non-tanks; (2) `ISessionQueryRepository.GetProjectionAsync` had not
been updated for migration 5 — its position SELECT/reader now carry
yaw/pitch/roll, so frames see the packet rotation. 4 CLI tests + 1 new
frame-source test pin roster filtering and fail-closed omission.

## W2S HUD — projected nameplates over the game window (2026-08-10)

The world-to-screen overlay HUD is wired end-to-end over the loopback host:

- **`OverlayFrameProjector`** (Application/Replay): the single projection
  path (frame + FOV + viewport → camera + projected tanks) shared by the
  CLI `overlay-frame` command and the web host — they can never disagree.
  3 unit tests.
- **Web endpoint `GET /api/v1/sessions/{id}/frame?timeSeconds&fov&width&height`**
  (ReadApiEndpoints): serves the projected frame to HUD clients; validates
  query bounds and maps session failures to 404. 3 endpoint tests
  (projection incl. behind-camera, bad params, not-found).
- **`OverlayFrameResponse`/`OverlayTankResponse`** (ApiContracts): the
  machine contract (world data + screen X/Y/depth + inViewport).
- **`TreaderApiClient.GetOverlayFrameAsync`**: the overlay's frame fetch
  with FOV/viewport query params; 1 test (URL + deserialize).
- **`W2sHudView`** (WPF): renders nameplates (label, team-colored
  blue/red, HP bar green→red by fraction, distance, greyed when dead) at
  the projected pixels. The overlay window is resized to exactly the game
  client rect (existing P/Invoke tracking), so viewport pixels map 1:1.
  `AnchorRect` is a pure, tested clamp (3 tests).
- **MainViewModel + MainWindow**: on every 50 ms playback tick (and on
  scrub), `RefreshOverlayFrameAsync` fetches the frame at the current
  replay time — a generation guard + CTS drops stale responses, failures
  keep the previous frame — and `Nameplates` updates the HUD. Own tank
  (distance < 1), behind-camera, and off-viewport tanks are never drawn.
  2 view-model tests (visible-tank filtering, no-session guard).

The replay overlay now renders the actual 3D view: run the web host
(`serve`) with the replay decoded, select the session, and the HUD draws
nameplates over the game window as the timeline plays. FOV is a view-model
setting (default 90°); the camera is the viewpoint tank's packet pose — the
packet rotation makes this fully offline and engine-agnostic.

## Notes

- Damage events are the highest-value correlation target: HP changes only on
  damage, so measurement windows are event-bound, not continuous (unlike
  position). The event timeline lets discovery pick the replay segment where
  damage happens instead of watching the whole battle.
- Entity-id binding (which entity record is the player/enemies) reuses the
  same join: `CanonicalEvent.EntityId` ↔ `participant.entity_id` ↔ the
  memory resolver's entity ids.

## O3 — Beacon/POI layer (2026-08-10)

The overlay gained persistent world-space POIs ("beacons"):

- **Model**: `OverlayBeacon` (name, x/y/z, HTML color, optional replay-time
  visibility window) in Core/Overlay.
- **Persistence**: `beacons` table (migration 6, keyed `(battle_session_id,
  name)`, upsert on re-add), `IBeaconStore` + `SqliteBeaconStore`.
- **Projection**: `OverlayFrameProjector` now projects beacons with the same
  camera as tanks — one path shared by CLI + web + HUD, so the preview and
  the live HUD can never disagree. Time-tagged beacons are filtered by the
  frame's replay time; behind-camera beacons are never drawn.
- **Placement**: `beacon add <name> <x> <y> <z> --session <guid> [--color]
  [--from <s>] [--until <s>]`, `beacon list`, `beacon remove`. Coordinates are
  decoded-replay world units (read them from `overlay-frame` or position data).
- **HUD**: `W2sHudView` renders colored pins + labels (beacons under
  nameplates); a FOV slider was added to the toolbar, feeding the existing
  `HudFovDegrees` property into the frame request.

Verified end-to-end on the real Oasis Palms session (add → projected in
`overlay-frame` → remove) plus 13 new tests across Application/Web/CLI/
Storage/Overlay.
