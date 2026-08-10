# Handoff — 2026-08-10: L1 region anchor corrected (tank record) + Dead Rail repeatability rehearsals

**Branch:** `main` — gate green after, tree clean.

## The cross-check found a real seam bug

The ring-record cross-check (does the +0x48 HP candidate sit inside the same
record the +0x38 position chain reads?) answered **no** — and that exposed a
bug in the L0 seam's wiring:

- The position resolver reads the **movement ring record**: `helper + 0x08 +
  index·0x38`, position at `+0x10/+0x14/+0x18`, velocity at `+0x28..+0x30`.
  The record is exactly 0x38 bytes.
- The HP / damage-dealt harness targets the **per-entity tank record** at
  `[entity + 0x3C]` (Ghidra-candidate layout, proven in
  `Walk_PublishedTable_EntityBase_AnchorsHpRegionDump_CorrelatorFindsHp`):
  walk → entity base → deref `[entity+0x3C]` → dump → correlate `+0x48`.

The L0 seam read from `resolved.RecordAddress` (the ring record). A `+0x48`
offset there would land **0x10 bytes past the ring record's end** — unrelated
memory. The L1 live session as first wired would have dumped the wrong object
and produced a confusing no-hit.

## The fix

- **Resolver** (`Type10EntityPositionResolver`): `Type10EntityPositionAddressResult`
  now also carries the resolved **entity base** (`EntityAddress`), threaded
  through `AttemptResult`.
- **Seam** (`EntityRecordRegionReadRequest`): new `RegionAnchor`
  (`ring-record` default | `entity-tank-record`). For the tank-record anchor
  the coordinator dereferences `[entity + 0x3C]` **itself** under the same
  guarded lease (`ResolveTankRecordAddressAsync` — validates the pointer read
  and rejects null / misaligned pointers before any region read). Only bytes
  leave; the tank-record address never escapes.
- **Web**: `POST /api/v1/game/discover/entity-region` accepts
  `regionAnchor`; unknown values fail closed (`invalid_anchor`, 400).
- **Driver** (`invoke-hp-diffing-session.ps1`): defaults to
  `-RegionAnchor entity-tank-record` (HP and damage-dealt live in the tank
  record) and sends it in the seam body.

## Tests

- Coordinator: 271/271 (2 new — tank-record anchor derefs `[entity+0x3C]`
  then reads at the tank record; missing entity base fails closed with zero
  reads).
- Web: 132/132 (2 new — anchor forwarding; invalid anchor fails closed).
- Full solution builds 0 warnings / 0 errors.

## Dead Rail repeatability rehearsals (offline)

The second independent replay is now fully rehearsed for BOTH tracks with the
physically-correct dump shape (real sub-centisecond event ticks, dumps at the
schedule's ±0.2 s offsets):

| Track | Replay | Session | Target | Verdict | Offset | Score/Flatness | Matched |
|---|---|---|---|---|---|---|---|
| HP (decrement) | Dead Rail | `019fecb0-…a7835b` | 2549399 | HIT | `+0x48` | 1.0 / 1.0 | 14/14 |
| Damage-dealt (increment) | Dead Rail | `019fecb0-…a7835b` | 2549401 | HIT | `+0x48` | 1.0 / 1.0 | 5/5 |

Combined with the earlier Oasis rehearsals, both replays agree on `+0x48` for
both directions — the Phase-4 two-replay repeatability rule holds at the
synthetic level for every live-planned track. (Note: this confirms the
correlator machinery on real event timelines; whether the in-memory HP /
damage-dealt counter actually lives at tank-record `+0x48` is exactly what
the gated live region read discovers.)

**Follow-up cross-check (same day): `+0x48` is a SYNTHETIC FIXTURE, not a
verified candidate.** Tracing the claim to its source shows the mechanism
proof planted HP at `+0x48` to prove the correlator; the only static
evidence is `[entity+0x3C]` as the TRANSFORM OBJECT (getter
`FUN_00d29ea0 = return [ECX+0x3C]`, position `+0x1C/20/24`, matrix
`+0x60..0x9C`, per-frame rotation `+0x38..0x5C` — FRESH43). `+0x48` would
land inside that rotation block, an unlikely HP home. The live L1 session
must be framed as DISCOVERY: dump the transform-object region and let the
correlator rank whichever int32 actually drops with damage; an honest
no-hit widens the anchor. Docs corrected accordingly (roadmap L1/P2 +
groundwork rehearsal notes).

## Files changed

- `src/WotBTreader.Core/Discovery/Type10EntityPositionResolver.cs` —
  `EntityAddress` in the address result + plumbing.
- `src/WotBTreader.Application/Game/GameSessionContracts.cs` —
  `EntityRecordRegionAnchor` + request field + `EntityTankRecordOffset` const.
- `src/WotBTreader.GameIntegration/Session/GameSessionCoordinator.cs` —
  anchor switch + `ResolveTankRecordAddressAsync` (guarded deref).
- `src/WotBTreader.ApiContracts/OffsetDiscoveryContracts.cs` —
  `RegionAnchor` on the request DTO.
- `src/WotBTreader.Host.Web/Endpoints/GameApiEndpoints.cs` — anchor parse +
  forwarding (fail-closed on unknown values).
- `scripts/invoke-hp-diffing-session.ps1` — `-RegionAnchor` param, defaults
  to `entity-tank-record`, sent in the seam body.
- Tests: coordinator (2 new), web (2 new).
- Docs: roadmap L0 row, record-diffing groundwork live-plan section.

## Next

The L1 live session is code-ready with the corrected anchor. L2 (facing)
keeps the default `ring-record` anchor (its yaw candidate is inside the ring
record at `+0x2C`) — the seam serves both. Still gated on operator approval;
nothing here touches the live game.
