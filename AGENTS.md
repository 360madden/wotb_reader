# WotB Treader — agent entry

Windows-first .NET 10 modular monolith for **offline** WoTB replay telemetry.
No runtime AI, cloud, Python, Node.js, Rust, Electron, or containers.

## Where we are now (2026-08-11)

- **Active workstream (2026-08-11): overlay + camera track.** See
  `docs/operations/product-roadmap.md` (forward plan), the newest files in
  `docs/operations/handoffs/` (HP Phase-4 closed, 2026-08-11), and the
  ledger. The W2S camera
  is live-verified: the fixed member-path walks and both identity gates
  pass (ReplayCameraController `base+0x326dd0c` / GameCamera
  `base+0x32dafa0`), and the live pose lives on the **GameCamera**
  (position `+0x38`, yaw cos/sin `+0x50/+0x54`, pitch `+0x58`, basis
  `+0x80..0xB0`). **CAM-010 (2026-08-11): CAM-004's verdict is SUPERSEDED
  — GameCamera posA `+0x38` is stored (x, z, y) with world Y/Z swapped
  (yz-swap puts it 2.1–3.6 m from the decoded viewpoint tank, sub-meter,
  on v7b+v7c; CAM-004's "23.57 m third-person offset" was the
  `√2·|tank.z − tank.y|` artifact of the un-swapped read, not a chase
  eye). **CAM-011/012 (2026-08-11): orientation LOCKED and the W2S seam
  shipped** — posA yz-swapped IS the render eye, the basis rows are the
  camera's world axes (forward = −row1, up = row2), and the
  `LiveFrameProjector.BuildCamera` consumption is validated by the CAM-007
  screen cross-check at ship time. **CAM-013 (2026-08-11): the CAM-007
  live session VERIFIES the W2S seam (`w2sProjectionVerified: true`)** —
  two launches / three captures (`cam001-v7-aggregate-091/091b/092`)
  reproduce the chase signature (identity gates 3/3, basis coherent, eye
  2–3 m from the tank, yaw tank-following, memory pitch level), and the
  earlier "non-chase honest-negative" was a hull-center measurement
  artifact: the WoTB chase camera aims at the TURRET-LEVEL AIM POINT
  (~1.9 m above the hull center), where pitch-to-aim ≈ 0° ≈ the memory
  pitch. Re-verdict with that target: 091/091b/092/v7c verified (all
  chase), only the v7b battle-intro cinematic (+88° pitch) stays
  non-chase. Validator-only change (no read surface / offsets).
  **X4-E2E (2026-08-11): the live overlay render pass is VERIFIED
  end-to-end** — a mid-battle `GET /live/frame?sessionId=` capture on the
  CAM-013 seam served the full 7v7 roster at battle start with **14/14
  exact decoded-name joins** (enemy ids included — the X2b own-team-only
  bound is superseded; the movement-filter family is time-varying and the
  loop's per-tick re-enumeration catches the full roster at t=0), 13/15
  real L1 HP, and the chase camera geometry (eye 1.9 m, pitch level =
  aim point, viewpoint tank below-center); the CAM-003 controller flip
  between auto-loop battles re-demonstrated the fail-closed 409.
  **Refinements live-verified 2026-08-12:** the live frame now carries a
  real replay clock (the endpoint forwards the session id into the
  discover request so the batch's ONE G2 snapshot runs; capture read
  115.5 s mid-battle — and a pre-existing launcher anchor-date bug was
  found + fixed: the anchor date came from the blitz-log FILENAME (local
  date) while the marker's leading time is UTC — a local-evening launch
  writes its marker to the previous day's file, rolling the estimate 24 h
  (86517 s); the launcher now rolls the date bounded to within 10 min of
  UTC now), and the own-nameplate  suppression
  marker (`OwnEntityId` = the decoded viewpoint participant's entity id)
  was verified live on both launches with exact joins + real HP, and the
  own-tank OFF-viewport edge marker shipped (clamped chevron back at the
  hull, `OwnMarkerItem`/`OwnMarkerMath`, 8 tests, fail-closed).
  **Known caveat
  (CAM-003):** the session-controller vftable FLIPS between launches
  (`base+0x325ad2c` — resolver's hard-coded gates reject it and
  `/discover/entity-position` + `/position-page` return
  `UnsupportedSessionController` — vs `base+0x323d9bc`, where they
  resolve). Mitigation: the CAM-001 v6 gate-free direct walk. The 08-09
  offset-promotion history below is complete and retained.
- **Offset-promotion history (2026-08-09):** offset discovery — module-rooted player-position
  polling. Continuous polling is positive in **two distinct 11.19.0 replays**
  (Dead Rail 24/24; Oasis Palms 24/24, stable-resolver-positive) — cross-replay
  repeatability is proven. **All offline promotion-gate work is done
  (2026-08-09):** G3 runner wiring (`-PriorResultPaths`), G2 clock wiring +
  append endpoint + gate-anchor caller, and the G1 offline write-observation
  mechanism test (OD-RECOVERY-077) plus the position-page capability
  (`POST /discover/position-page`, diagnostic-only, same traversal, poll path
  untouched). **Two G1/G2 live sessions done (2026-08-09):** the one-command
  `scripts/invoke-g1-live-poll.ps1` chain runs end-to-end and **G2 is closed
  live** — `sameDecodedClockProven=true` in the poll aggregate both sessions
  (CaptureLog anchor, 1 s within the 2 s bound). G1's read failures are
  **root-caused (OD-RECOVERY-080): the guard-page interceptor's PAGE_GUARD on
  the ring-record page failed the poll's own reads at the avatar-helper vtable
  hop (ERROR_PARTIAL_COPY 299) — the 19/24 and 22/24 were harness artifacts,
  not a pointer race**; the corrected procedure is
  `invoke-g1-live-poll.ps1 -SkipInterceptorArm`, and the unchanged poll
  un-armed already delivered **24/24 twice (OD-075/076)** with
  `allConsistentDoubleRead=true`. **Third live session done (2026-08-09,
  OD-RECOVERY-081): the corrected un-armed poll resolved 24/24 (all attempt-1,
  byte-identical double-reads, `allConsistentDoubleRead=true`) and re-proved
  G2 (`sameDecodedClockProven=true`), but the verdict came back
  honest-negative — root cause: a verdict-contract conflict. The positive
  verdict's `-not $anySameDecodedClock` clause was written when the
  coordinator hardcoded that flag false (pre-G2); the G2 wiring made it true
  whenever the clock anchor lands, so a working G2 made the G1 positive
  verdict UNREACHABLE by construction. Fixed in the poll (schema v4): the
  same-clock proof is orthogonal evidence reported separately and no longer
  disqualifies the verdict. **Fourth live session (2026-08-09,
  OD-RECOVERY-082): G1 and G3 CLOSED.** The stored v4 aggregate is 24/24
  `stable-resolver-positive` with `allConsistentDoubleRead=true` (G1, per-read
  byte-identical branch); G2 re-confirmed a 4th time
  (`sameDecodedClockProven=true`); G3 closed on the positive verdict +
  directly-validated OD-075/076 priors (the stored
  `stableRootLiveRepeatabilityProven` field is false only from a mechanical
  `-PriorResultPaths` comma-binding bug, fixed in the wrapper — not an
  evidence deficiency). **G0 publication review executed — verdict
  PROMOTE-READY (conditional):** executable identity exact (`1cda5c31...`
  re-measured), RVA chain verified hop-by-hop vs the resolver layout, field
  identity (playerPositionX/Y/Z float32 at record `+0x10`; velocity not
  promoted; playerYaw untouched), repeatability attested, read-only gates
  PASS. **G0 publication applied (2026-08-10, OD-RECOVERY-083,
  operator-approved):** the table is no longer frozen —
  `playerPositionX/Y/Z` are `Verified` via the module-rooted position-ring
  chain (additive `chains` section in `schema.json` + `11.19.0.10.json`;
  `offsets` stay 0 by design — the runtime computes `moduleBase + offset`
  and the ring record is battle-scoped heap), evidence appended (4 launches
  / 2 replays), approvals set, `numericOffsetPublication: true`, all
  post-edit gates green (`offset_check.py` chains-validated 3 fields,
  `validate.ps1` exit 0). NOT promoted at G0: velocity, playerYaw, replayTime,
  playerHP, cameraPitch, aliveTankCount (playerHP + playerYaw were published
  under G1 2026-08-12, OD-RECOVERY-092 — see below). Resolver + read surface untouched;
  the legacy observation path still emits position nulls (chained fields
  excluded — pinned by `ChainedFields_AreExcludedFromObservationReads`).**
  See
  `docs/operations/offset-promotion-checklist.md` (and ledger
  `OD-RECOVERY-078/079/080/081/082/083`).
- **Resolver-path consolidation (2026-08-11):** the 7-item checklist
  (`docs/operations/resolver-path-consolidation.md`) is executed — items 1–4
done (chains-only publication, single sanctioned walker, phase tolerance
audited, legacy observation surface frozen + deprecated); item 6 fully
staged: the **batch N-entity read surface** (`/discover/entity-regions`, up
to 16 entities, ONE clock attestation per batch, per-entity statuses,
read-pass measurement) — design `docs/operations/batch-entity-read-design.md`,
coordinator + endpoint + tests shipped, rehearsal driver
`scripts/invoke-batch-rehearsal.ps1` + cross-check tool
`scripts/python/batch-rehearsal-crosscheck.py` ready (proven 42/42 on real
decoded data); the yaw quarantine is **resolved-by-supersession** (yaw is a
runtime chain field at ring-record `+0x30` — live-verified 2026-08-11 by
OD-RECOVERY-088 as part of a rotation triple roll `+0x28` / pitch `+0x2C` /
yaw `+0x30`, the rehearsal's +0x2C prediction corrected; not a static
offset). **OD-RECOVERY-089 closed 2026-08-11: L2 facing Phase-4 repeat HIT**
— Dead Rail agrees at `+0x30` (56/56, score 1.0, flatness 1.0;
`twoReplayRepeatability = true`). The at-session verdict was an honest
negative from a matcher limitation: the G2 replay-clock LABEL skew is
per-dump variable and OPPOSITE in sign per replay (Oasis memory lags
+4.8 s; Dead Rail leads −2.5 s, spread 5.6 s) — fixed additively by the
per-dump bounded bidirectional lag path (`yaw-diff --per-dump-lag
--memory-lead-seconds`), which re-verdicts the same dumps to HIT on both
replays. **OD-RECOVERY-091 closed 2026-08-11: L1 HP Phase-4 repeat HIT** —
Dead Rail agrees at entity-base `+0xB8` (score 1.0, flatness 1.0, Strict
4/4 exact sums; `twoReplayRepeatability = true`; victim re-scoped to
team-1 2549395 — the planned 2549399 is team 2 and can never resolve, the
resolver's entity-map trees hold the player's own team only). The
at-session verdict was an honest negative from the SAME matcher limitation
class: Dead Rail's memory clock LEADS the decoded clock (~2.5 s), invisible
to the one-directional attribution — fixed additively by the bounded
lead-side window (`hp-diff --lag-lead-seconds`, default 0 = unchanged, 3
tests), which re-verdicts the same dumps to HIT and leaves Oasis
regression-free. **G1 publication applies LANDED 2026-08-12
(OD-RECOVERY-092, operator-approved): `playerHP` (entity-base `[entity+0xB8]`
signed int16 — OD-RECOVERY-087/091, `twoReplayRepeatability = true`) and
`playerYaw` (ring-record `+0x30` float32 — OD-RECOVERY-088/089,
`twoReplayRepeatability = true`) are now `Verified` via module-rooted
`chains`** (5 chained fields total; both `offsets` stay 0 by design;
yaw resolves-by-supersession the quarantined static candidate; post-edit
gates green — `offset_check.py` chains-validated 5 fields + fidelity 5/5,
`validate.ps1` exit 0). **Rotation-triple reconciliation DONE (2026-08-12,
offline):** `yaw-diff --field pitch|roll` (new selector over the decoded
`position_samples.pitch/roll` columns) re-verdicts the SAME immutable
OD-088/089 dumps — pitch `+0x2C` and roll `+0x28` now AGREE on both replays
(Oasis 48/48 + Dead Rail 56/56 each, score 1.0 / flatness 1.0,
`twoReplayRepeatability = true` for the full triple roll `+0x28` / pitch
`+0x2C` / yaw `+0x30`); methodology lesson: the Oasis full-region roll
verdict matched `0x60` = the NEXT ring entry's `+0x28` (stride 0x38,
byte-near-identical sibling) — record-span (0x38) trimming removes the
decoy and both replays agree at `+0x28`. **Record-span option SHIPPED
(2026-08-12):** `yaw-diff --record-span <bytes>` (default 0 = full region)
trims snapshots to the single-record span so ring-sibling decoys are
excluded automatically — re-verdicting the SAME full 256-byte OD-088/089
dumps with `--record-span 56` reproduces the trimmed verdicts in one
command (Oasis roll `+0x28` 48/48 incl. the former `0x60` decoy, pitch
`+0x2C`, yaw `+0x30`; Dead Rail roll `+0x28` 56/56; 3 new tests), and
`invoke-facing-session.ps1 -RecordSpan 56` threads it through the facing
driver (verified on the full OD-088 dumps → `+0x30` 48/48 in one command).
Pitch/roll publication chains are
pre-staged to mirror yaw (position hops 1..11 + recordOffset 0x2C / 0x28),
and the publication package is PRE-STAGED (2026-08-12): optional
`playerPitch`/`playerRoll` offset slots + chains/fieldValidation enums in
`schema.json`, draft chains in the walkable draft, and the operator-facing
spec at `docs/operations/g1-pitch-roll-publication-draft.md` (apply =
table edit + pack doc, nothing else). **L3 static candidate search DONE (2026-08-12,
hash-bound `1cda5c31…`):** the Avatar object (entity-factory case 1, vtable
`0x36752a4`, 0x128 bytes) carries a contiguous uint32 battle-stats block at
`+0x118/0x11c/0x120/0x124` (property indices 0xA–0xD, property-change
dispatcher `FUN_01670de0`); all 13 `+0xB8` word-store sites classified —
the two vehicle state decoders (Vehicle vtable `0x36755d0` slots 12/13,
dispatch `+0x30`/`+0x34`) are the victim-HP write path, the rest are
unrelated-family constructors; damage-dealt is NOT on the entity record
(consistent with OD-RECOVERY-090); new tooling `FindVtableDispatch.java` /
`FindDispatchCallers.java` / `DumpRawWindow.java`; evidence
`.build/ghidra-evidence-l3-damage/`; **reachability CORRECTED
2026-08-12** — the camera chain's `avatarAddress` anchors
`AvatarControllerReplay` (vftable `0x3677e8c`), a DIFFERENT object from
this Avatar; live reach is the established gated vftable AOB scan targeted
at `moduleBase + 0x032752a4`; **the session seam is PRE-STAGED
(2026-08-12):** `/discover/entity-region` gained the `avatar-stats` anchor
(gated AOB scan + identity re-gate + quad read, `avatarCandidateIndex` /
`avatarCandidateCount`, fail-closed `AvatarAnchorNotFound` /
`AvatarIdentityMismatch`) and `invoke-hp-diffing-session.ps1 -RegionAnchor
avatar-stats` dumps every scan candidate per tick (per-candidate snapshots
files, own-counter discrimination at scoring time; 6 new tests); **the
protocol is REHEARSED offline (2026-08-12) —
`scripts/invoke-avatar-stats-rehearsal.ps1` on the real decoded session
`019fa44a-b226-…` proves the increment verdict discriminates the own
counter (score 1.0 / flatness 1.0 / 7/7, offset 0x0) from flat candidates
and the PHASE-4 two-replay simulation passes (Dead Rail 5/5 at the same
offset 0x0 — offsets agree across both replays)**;
live increment-correlation session on the Avatar uint32 quad is the next L3
step (approved launches, plan
`docs/operations/l3-damage-dealt-avatar-family-plan.md`). **L3 LIVE ATTEMPT
2026-08-12 RECORDED (inconclusive + env-blocked):** sweep 1 reached
OfflineReplayVerified (monitor-2 placement verified live) but the first
`avatar-stats` probe 400'd mid-battle with the status swallowed — most
likely `avatar-scan-not-found` (no entity-Avatar instance in the gated
scan; INCONCLUSIVE); sweeps 2-3 were environment-blocked (second monitor
detached mid-session; game landed in ReplayList + ErrorDialog, gate never
flipped). **Sweep 4 RESOLVED THE LANE LIVE (2026-08-12, OD-RECOVERY-093):**
the playback mechanism changed and was re-proven — with the Wargaming Game
Center running + authenticated, the launcher (window move/resize + orange
Watch Offline click) reaches `OfflineReplayVerified` 3/3 when given the
**top-level original replay in the game's replays folder**
(`20260802_1615__…_Churchill_I_….wotbreplay`, the OD-075/076 ground truth);
file-association alone stalls at `LoginOnReplayDialog`. **Error 126
ROOT-CAUSED 2026-08-12 (OD-RECOVERY-094): the discriminator is the replay's
CLIENT VERSION, not its location** — the `.data/launch` Copperfield copy
was an **11.18.0** replay and the 11.19.0.10 game refuses that family
("Replay Error code: 126"); the 11.19.0 `savanna-…` copy in the same
folder plays fine. The old "slow clicks / sync-dim" attribution in the
2026-08-02 specs was wrong (corrected). The launcher now runs a read-only
pre-flight probe (`$cli probe <path> --json`; new CLI `probe` command
reporting `gameVersion`/`installedGameVersion`/`compatible`) and fails
fast with `FAILED_replay_client_version_mismatch` BEFORE the
import/launch dance.
The avatar-stats seam **resolved live on every probe across the whole
battle** (`status='Resolved' candidates=1` — the entity-factory Avatar
instance IS reachable via the gated vftable scan; sweep 1's
`avatar-scan-not-found` was the wrong replay + wrong session id), and the
first real increment-correlation verdict ran: session `019ff5f1`, 20 dumps
spanning the damage windows, **top candidate offset 0x0 with score 1.0 and
3/3 EXACT damage sums (152/170/1) but flatness 0.333 (changed in control
windows) → honest-negative** under the strict Phase-4 contract
(`twoReplayRepeatability` not claimed). Driver fixes shipped with the run:
dump-stage end-of-replay skip + `AvatarAnchorNotFound`/`GateNotSatisfied`
teardown statuses (battle-end teardown no longer discards captured dumps)
and removal of a redundant pre-loop verdict on the suffix-less base path.
**OD-RECOVERY-095 (2026-08-12): the honest-negative is ROOT-CAUSED and the
SAME dumps RE-VERDICT to HIT — the avatar-stats quad `+0x0` IS the
damage-dealt counter.** All 6 decoded own-attacker damage events
(134/152/144/151/170/1, sum 752) map 1:1 to d0 increments; the two
"control-window" changes (+144/+151) were real damage events whose memory
writes LAG the decoded clock by +2.3–4.1 s (OD-087 lag class), invisible to
the at-session lag-0 default (the driver gated lag args behind
`-not $IsIncrement`). Re-verdict with the bounded lag path
(`hp-diff --lag-tolerance` 5 or 12/4): offset 0x0, 5/5 exact sums
(152/144/151/170/1), flatness 1.0, Strict 5/5 → **HIT**; d0 final 752 =
decoded `damageDealt`, d2 final 126 = decoded `damageAssisted1` (the quad
IS the battle-stats block). Driver fixed: lag args now pass on BOTH
directions. **L3 Phase-4 CLOSED (2026-08-12, OD-RECOVERY-096): the Dead
Rail live avatar-stats capture re-verdicts HIT at offset 0x0 (9/9 exact
sums 146/162/145/162/140/178/181/171/168, flatness 1.0, Strict >= 2; final
d0 1598 = decoded `damageDealt`) — offsets agree with Oasis (0x0) →
`twoReplayRepeatability = true`; quad layout refined to `[damageDealt,
damageBlocked, damageAssisted1, damageAssisted2]` (Dead Rail d1 140 =
`damageBlocked`, d3 228 = `damageAssisted2`); the L3 damage-dealt lane is
CLOSED.** Driver fixes shipped with the session: the PS 5.1 `break :label`
from a nested loop does NOT exit the labeled foreach (flag + guard pattern;
the labeled break let the schedule continue after teardown and throw on the
next probe); `AvatarIdentityMismatch` added to the teardown + definitive
lists (observed at battle end); probe status check reordered before the
informational print (StrictMode missing-member hazard); `[ordered]@{}`
int-key index assignment → plain hashtable (the real root cause of the
2026-08-12 `ArgumentOutOfRangeException` after the write phase, previously
misattributed); diagnostic trap; and the deadlock-free chain pattern
(Start-Process -RedirectStandardOutput + log polling — no `*>`
handle-inheritance wait, which hung the earlier Dead Rail chain while the
replay played out unwatched). **Item 7
(hardware-atomicity proof) stays LAST by
design** — its execution plan is pre-staged
(`docs/operations/item7-hardware-atomicity-proof-plan.md`), and **Branch A's
sub-proofs landed 2026-08-11 (hash-bound): HP — the health setters
`FUN_0166b9f0`/`FUN_01675f60` write `+0xB8` (current HP) and `+0x11E`
(healing) as single 16-bit stores, zero 64/128-bit stores to either field
anywhere in the binary, and the 360 dword/53 byte `+0xB8` stores belong to
other object families (bounded live by OD-087/091's byte-exact reads);
position + rotation — the ring writer `FUN_0270df40` (avatar-helper vtable
slot 2, chain-anchored on the 40-check type-10 semantic chain) writes x,y at
record+0x10 as one 8-byte MOVQ and z at +0x18 as a 4-byte store, roll+pitch
at +0x28/+0x2C as one 8-byte MOVQ and yaw at +0x30 as a 4-byte store
(stride 0x38 == resolver `RingRecordSize`), with the monotonic-time guard
and index-advance-before-fill handoff (double-read discipline covers the
sub-microsecond window; zero live tears). **Branch B step 1 landed
2026-08-11 (offline):** the batch region span and the entity-base span are
each read TWICE per attempt with a bounded retry and fail-closed
`region-unstable-snapshot`/`entity-base-unstable-snapshot` exhaustion; the
per-span `SequenceEqual` is the stability witness (the ring record's leading
time field sits inside the span); `ConsistentDoubleRead` stays false — the
flag flip + per-entity span measurement fields are the owner-gated
shared-contract proposal **DRAFTED 2026-08-12** at
`docs/operations/item7-branch-b-step2-consistent-double-read-proposal.md`
(computed-true on the witnessed batch item + `RegionReadAttempts` /
`RegionTearObserved` / `EntityBaseTearObserved`, camera-pose precedent,
single-read surface stays honest-false, owner review required)**.
- **BLK-0026 resolved and validated (2026-08-09):** root cause was a launcher
  regression — .NET `Set-Acl` threw `PrivilegeNotHeldException` on the
  persisted owner-only marker ACL, mapped by the catch-all to
  `FAILED_unexpected` before the gate (every launch after the ACL code landed).
  Fixed with `icacls` in both owner-only ACL functions; the launcher now
  reaches `OfflineReplayVerified` and exactly one unchanged bounded OD-075
  poll returned positive on the content-distinct replay (24/24 resolved,
  stable-resolver-positive) — **  cross-replay continuous polling is now proven
  across two distinct 11.19.0 replays** (Dead Rail + Oasis Palms, ledger
  `OD-RECOVERY-076`). See `docs/operations/blocker-log.md` BLK-0026 and the
  2026-08-09 resolution handoff. At the time (2026-08-09) still unproved:
  hardware atomicity, same-decoded-clock proof, numeric-offset publication,
  promotion. **Superseded 2026-08-10** by the operator-approved G0
  publication (below); hardware atomicity remains unproved.
- **Last verified gate:** 2026-08-12 — 1045 tests passed, 3 local opt-in skips,
  0 warnings, 0 errors.
- **Refresh from:** the newest file in `docs/operations/handoffs/`,
  `docs/operations/product-roadmap.md` (forward plan / workstreams),
  `docs/operations/offset-discovery-ledger.md` (Next planned session row),
  `docs/operations/blocker-log.md` (open blockers),
  `docs/operations/offset-promotion-checklist.md` (offset-gate status).

## Session ritual (start and end every task)

**Start**

1. Read the newest handoff in `docs/operations/handoffs/`; for offset work,
   also the ledger's *Next planned session* row.
2. `git status` + current branch; note uncommitted work that isn't yours —
   never discard, overwrite, stash, or commit it.
3. Load only what the task needs. `offline/README.md` re-orients fast and
   links to canonical docs instead of duplicating them.

**End**

1. Verify: typecheck → focused tests → full `scripts/validate.ps1` gate for
   milestone work.
2. Record: append a handoff + ledger entry where the workstream requires;
   blockers append to `docs/operations/blocker-log.md` (immutable UTC).
3. Report: what changed, what was verified, what remains. Leave the tree clean
   of stray files.

## Project owner context

- The project owner identifies as a junior developer at Wargaming.net. This is
  user-provided background for agents working in the repository; the repository
  remains a personal project unless official status is documented separately.
  Canonical wording: [`docs/project-context.md`](docs/project-context.md).
- Offline discovery pack: [`offline/README.md`](offline/README.md) — a
  focused, self-contained repo index (repo map, entry points, API surface,
  glossary, commands, replay format, offset discovery, data flow,
  memory-offset evidence, file-tree snapshot). Run
  `python scripts/python/offline_check.py --refresh` after editing the pack;
  the gate and CI run `--check-fresh` and fail if `offline/file-tree.md` is
  stale, so regenerate it in the same change that adds, renames, or removes
  files.

## Commands (exact)

| Use | Command |
|---|---|
| Restore | `dotnet restore WotBTreader.sln --locked-mode` — SDK pinned to 10.0.302 by `global.json`; package versions central in `Directory.Packages.props` with committed lock files |
| Build | `dotnet build WotBTreader.sln -c Release` |
| One test project | `dotnet test tests/WotBTreader.Core.Tests -c Release` |
| Focused test | add `--filter "FullyQualifiedName~SomeTest"` |
| Full gate (milestone) | `scripts/validate.ps1` — locked restore → `dotnet format --verify-no-changes` → Release build → all tests → `scan-repository.ps1` (secret + ignore-policy) → PSScriptAnalyzer hygiene (`install-psscriptanalyzer.ps1` + `invoke-scriptanalyzer.ps1`) → offline pack (`offline_check.py --check-fresh`) → offset-table schema + chains validation (`offset_check.py --check-schema`); add `-AuditPackages` for the transitive vulnerability audit |

Warnings are errors (`TreatWarningsAsErrors`) and `NuGetAuditMode=all` fails
restore on vulnerable transitive packages — fix with a central pin, never
suppress (BLK-0002). Tests are MSTest 4 on Microsoft.Testing.Platform; a few
installed-game tests are local opt-in and skip by default.

## Hard constraints (always)

| Rule | Why | If violated |
|---|---|---|
| Bot status may be inferred from a name; player names and bot status are public Wargaming statistics | those facts are public data, not private | privacy boundary misapplied to names |
| Never log raw replay bytes, tokens, full paths, account IDs, chat, screenshots | privacy trust boundary | privacy scan / review fails; trust broken |
| Never modify/redistribute the WotB install or game-derived assets | install is read-only, per project policy | repository scan fails; policy broken |
| `Core` has no project refs; `Application` → `Core` only; overlay is a loopback web client (no parser/storage refs) | mechanically enforced boundary | `Architecture.Tests` fails the build |
| Evidence-first decode: unknown stays unknown; reprocess = new immutable decode run | no fabrication; append-only evidence | invented semantics or mutated runs |
| Pickle = data only; never execute opcodes / import Python | decode safety | arbitrary code execution risk |
| Focused diffs; no destructive git; no secrets in repo | repo hygiene | lost work or leaked secrets |
| Commits as `Codex Agent <codex@local.invalid>` unless user says otherwise | repo convention | inconsistent history |
| Push only when the user asks; never force-push | remote safety | destructive remote history |
| Lead stages/commits; subagents must not. Propose shared-contract changes before editing them | ownership and review | unreviewed contract drift |
| Milestone: format/analyzers/build/tests pass; handoff via `scripts/validate.ps1` | release gate | unreviewable milestone |
| CI: synthetic fixtures only; private-game tests are opt-in local | fixture policy | private data reaches CI artifacts |
| Blockers/handoffs: append only, immutable UTC | audit trail | history rewritten |

## Architecture (enforced by `tests/WotBTreader.Architecture.Tests`)

- `Core`: no project refs. `Application` → `Core` only. Adapters (`Replays`,
  `CaptureLogs`, `GameIntegration`, `Storage.Sqlite`) → `Application`+`Core`,
  never each other. `Bootstrap` is the only composition root; hosts reference
  `Bootstrap`.
- **The overlay (WPF) is a transparent, borderless, topmost HUD** that sits on
  top of WoT Blitz during replay playback — a loopback web client with no
  parser/storage refs. It is **NOT** a generic session viewer. Full design
  spec: [`docs/architecture/overview.md`](docs/architecture/overview.md).
- Only `Overlay` and `tools/GameHarness` target `net10.0-windows`; everything
  else stays portable `net10.0` (BLK-0003). Every new DI port must be added to
  the published-port list in `CompositionRootTests`, or the solution compiles
  and tests green yet no host starts (BLK-0013). Diagram, evidence lifecycle,
  and loopback trust boundary: `docs/architecture/overview.md`.

## Task decision tree

| Task | Load | Allowed | STOP if |
|---|---|---|---|
| Plan / design sharpening before acting | skill `grill-me` (user-invoked, `.agents/skills/`) | interview until shared understanding; agent may fetch facts but never answer decisions | implementing before the user confirms the understanding is shared |
| Replay format / decode internals | [`offline/replay-format.md`](offline/replay-format.md) | read-only analysis; decode as data | executing pickle opcodes / importing Python |
| Telemetry data flow (decode → UI / comparison) | [`offline/data-flow.md`](offline/data-flow.md) | trace pipelines | mutating immutable decode runs |
| Offset / memory evidence | [`offline/memory-offsets.md`](offline/memory-offsets.md), [`offline/offset-discovery.md`](offline/offset-discovery.md), ledger | static/synthetic proof; bounded gated polls per ledger plan | promoting offsets or editing `memory-offsets/11.19.0.10.json` without proof; live polls while BLK-0026 is open |
| Game internals research | [`research/README.md`](research/README.md) (index) | research | touching the game install |
| Architecture / project refs | [`docs/architecture/overview.md`](docs/architecture/overview.md), `tests/WotBTreader.Architecture.Tests/` | refactor within boundaries | violating the reference graph |
| Replay / binary / harness tools | [`offline/replay-format.md`](offline/replay-format.md); Codex `decoder_auditor` | audits | shipping dynamic decoder DLLs |
| Loopback / mutation / privacy audit | Codex `security_auditor` (read-only) | read-only review | mutating shared contracts |
| Validate / commit / handoff | this file's *Definition of done + commit checklist*; `scripts/validate.ps1` | per checklist | pushing without being asked |
| UI / DTO / smoke / docs glue | Codex `implementer_glue` (fast) | bounded mechanical units | changing shared contracts without proposal |
| Prove work after a unit | Codex `verifier` (fast) | verification | staging or committing |
| Human setup | [`README.md`](README.md) | — | — |

## Repo gotchas (each has bitten before)

| Symptom | Root cause | Fix |
|---|---|---|
| `.gitignore` unanchored patterns match **case-insensitively on Windows** and hide real source folders | `*.sqlite`, `diagnostics/`, `dist/` unanchored (BLK-0005, BLK-0012) | add explicit `!` unignore rules for paths that collide with runtime-data patterns; `scan-repository.ps1` fails validation if any ignored file exists under `src`, `tests`, `tools/src`, `scripts`, or `docs` |
| `validate.ps1` reports success after a failed phase | `$ErrorActionPreference='Stop'` does not catch non-zero native exit codes (BLK-0006) | route every native command through `Invoke-CheckedNative` |
| `offline_check.py --refresh` misses a newly added file | `--refresh` reads `git ls-files`; an untracked file is invisible until staged, and the gate's `--check-fresh` compares against the index when it runs — refresh → gate → add silently drops the new file (bit the batch-rehearsal handoff, 2026-08-11) | `git add` new files FIRST, then `--refresh`, then the gate |
| A `.ps1` silently corrupts at runtime | PowerShell 5.1 reads BOM-less UTF-8 as ANSI (an em-dash's trailing byte `0x94` maps to `"` and terminates a string literal early) | keep every `.ps1` ASCII-only; the PSScriptAnalyzer gate (`scripts/invoke-scriptanalyzer.ps1`) and custom rules (`tools/psscriptanalyzer-custom-rules.psm1`) enforce it — they ban `[double]::IsFinite` and `??`/`&&`/`||`; type custom-rule parameters with a **concrete AST node** (`[ScriptBlockAst]`, never `[Ast]` — the analyzer matches by type-name substring and silently skips `[Ast]`); pass `-IncludeDefaultRules` (custom paths replace defaults); run `powershell -File scripts/invoke-scriptanalyzer.ps1 -SelfTest` after touching the rules module |
| cmd wrapper misbehaves | delayed expansion + `!` in filenames, unquoted `%~dp0` in nested `cmd /c`, whitespace input → arithmetic crash, env var leaking | full catalogue and review checklist in [`docs/operations/cmd-wrapper-gotchas.md`](docs/operations/cmd-wrapper-gotchas.md); route any non-trivial cmd/batch review through a thinker agent with the actual file contents — never rely on manual reading |
| .NET commands time out | basher default 30s is far too short | minimums: build 300s, full test suite 300s, single test project 120s, publish 180s; never run interactive `.cmd` wrappers (import, everything, serve) through basher — they expect a TTY or spawn windows; use direct `dotnet build` / `dotnet test` / `dotnet publish`; verify prerequisites (CLI built, packages restored) before running dependents |
| PS `& script *> log` never returns | the launched host/game grandchildren inherit the `*>` redirect file handle, so PowerShell's redirect-wait never sees EOF (bit the 2026-08-12 Dead Rail chain: launcher exited, log complete, chain stuck forever, replay finished unwatched) | never block on `& launcher *> log` when the script spawns long-lived children; start it, poll the log/state you need, and let the child own its handle — the launcher's own log file is enough |
| Fixture leak | private replays, captures, DBs, screenshots reach the repo | synthetic fixtures only in CI; private data stays in ignored paths; full sanitization process: [`docs/testing/fixture-policy.md`](docs/testing/fixture-policy.md) |

## Definition of done + commit checklist

**Done =** code changes typecheck and pass focused tests · docs/handoffs
updated where the workstream requires · milestone work passes the full
`scripts/validate.ps1` gate · no stray files left behind.

**Commit:** review `git diff` and recent `git log` style → stage only related
files (never broad `git add -A`) → conventional-commit message → author
`Codex Agent <codex@local.invalid>` unless the user says otherwise → push
**only when asked**, never force-push.

## Delegation index (Codex · OpenCode · Grok)

| Agent | Use for | Config / notes |
|---|---|---|
| `explorer` (built-in) | broad read-heavy searches / multi-file rounds | Codex built-in; prefer read-heavy parallel work |
| `decoder_auditor` | replay/binary/decoder audits | `.codex/agents/decoder_auditor.toml` |
| `security_auditor` | loopback/mutation/privacy audit (read-only) | `.codex/agents/security_auditor.toml` |
| `implementer_glue` | UI/DTO/smoke/docs glue (fast) | `.codex/agents/implementer_glue.toml` |
| `verifier` | prove work after a unit (fast) | `.codex/agents/verifier.toml` |
| `deepseek-glue` | one bounded mechanical implementation unit | `.opencode/agents/*.md`; DeepSeek V4 Flash at its reliable default reasoning — request `max` only for a hard second opinion |
| `free-reviewer` | bounded read-only second opinion | pinned to an explicit OpenRouter free model, not the variable `openrouter/free` route |
| `grok-glue` | one bounded mechanical unit; every non-trivial `.cmd`/`.bat` review | `.grok/agents/*.md`; owner's grok.com subscription login, not an API key |
| `grok-reviewer` | dependable read-only second opinion | same |

**Cross-cutting rules**

- Subagents must not stage, commit, or push.
- Codex concurrency is capped at three threads (`.codex/config.toml`); keep
  overlapping write units sequential.
- `opencode.json` pins this repository's default OpenCode model to DeepSeek V4
  Flash so an earlier session choice cannot silently select the random free
  router.
- Grok: invoke headlessly with `--no-subagents --disable-web-search
  --no-memory`. For `grok-reviewer` also pass `--permission-mode dontAsk
  --allow Read --allow Grep --deny Edit --deny "Bash(*)"`. For `grok-glue` use
  `--permission-mode dontAsk` with explicit allows for `Read`, `Grep`, `Edit`,
  `Bash(dotnet *)`, `Bash(git status *)`, and `Bash(git diff *)`; leave every
  other shell command denied. Never run `grok-glue` concurrently with another
  writer on the same worktree.
- Lead model keeps: replay/binary/decoder decisions, loopback/mutation/privacy
  review, and shared-contract changes.

## Model stance (one line)

Hard decoder/security/contract decisions stay on the lead model; Codex subagents (default `gpt-5.6-terra`), OpenCode DeepSeek V4 Flash, and Grok handle glue, explore, verify. Details: `.codex/config.toml`, `.opencode/agents/*.md`, `.grok/agents/*.md`.

## Last verified

- 2026-08-12 — full gate green: 1045 tests passed, 3 local opt-in skips,
  0 warnings, 0 errors (fresh run).
- 2026-08-09 — AGENTS.md restructured to the table-driven layout; hard
  constraints trimmed (offline-only + Cheat Engine bullets removed), ADR 0002
  amended, README/knowledge.md reconciled.
- 2026-08-09 — Cursor references removed (subscription ended); delegation
  index now covers Codex, OpenCode, and Grok only.
