# Item-7 Branch B step 2 — `ConsistentDoubleRead` shared contract (APPLIED)

> **STATUS: APPLIED 2026-08-14 — owner-approved.** This is the shared-contract
> change deferred by the item-7 plan
> (`docs/operations/item7-hardware-atomicity-proof-plan.md`, Branch B step 2).
> Branch B step 1 (the double-read discipline) is DONE: the batch region span
> and the entity-base span are each read TWICE per attempt with a bounded
> retry (`layout.MaxAttempts`) and fail-closed exhaustion
> (`region-unstable-snapshot` / `entity-base-unstable-snapshot`); the per-span
> `SequenceEqual` is the stability witness (the ring record's leading time
> field sits inside the span). `ConsistentDoubleRead` still travels **false**
> on batch items before this apply; it is now claimable and the per-entity
> span measurement fields are exposed. Nothing here touches the resolver,
> the read surface, the offset table, or any runtime read offset.

## 1. What is proposed

**Flip `ConsistentDoubleRead` to computed-true on the batch region item** when
the double-read witness passed — i.e. the delivered `RegionBytes` are a
`SequenceEqual`-matched pair (possibly after bounded retries); false when the
item failed closed (never a silent single read). The camera pose path already
does exactly this (`GameSessionCoordinator` ~3494: `ConsistentDoubleRead:
true` only when the pose pair matches — the CAM-013-verified precedent), so
the proposal extends the existing claimable-witness pattern to the batch
surface rather than inventing a new flag.

**Add per-entity span measurement fields** (the "per-entity span measurement
fields" the plan promised):

| Field | Type | Semantics |
|---|---|---|
| `RegionReadAttempts` | int | Ring-region attempts until the stable pair (1..`layout.MaxAttempts`); surfaces the retry count today hidden in the loop. |
| `RegionTearObserved` | bool | A region pair mismatched at least once before settling (tear observed + retried) — honest, never hidden. |
| `EntityBaseTearObserved` | bool | Same witness honesty for the entity-base span (`EntityBaseAttempts` already exists; this adds the tear flag). |

## 2. Semantics (what the flag does NOT claim)

`ConsistentDoubleRead: true` means: **"the delivered bytes are a
stability-witnessed snapshot"** — two reads under the same guarded lease were
byte-identical, so a ring advance or mid-write that occurred between them
would have retried (bounded) or failed the item (fail-closed). It does NOT
claim:

- **Hardware atomicity** — `HardwareAtomicReadProven` stays **false**,
  never claimed (Branch A's static write-size proof is a separate gate; the
  witness only bounds the sub-microsecond window, per Branch B's design).
- **No tear ever happened** — `RegionTearObserved: true` explicitly records
  that a tear was seen and settled. The flag is about the DELIVERED bytes,
  not the absence of torn attempts.
- **Single-read surfaces** — the single `/discover/entity-region` path has no
  witness and keeps `ConsistentDoubleRead: false` (honest-negative). Only the
  batch item (witnessed) and the camera pose (witnessed) can be true.

## 3. Contract surface changes (apply scope)

1. `src/WotBTreader.Application/Game/GameSessionContracts.cs` —
   `EntityRegionReadResultItem`: `ConsistentDoubleRead` set from the ring
   witness; add `RegionReadAttempts`, `RegionTearObserved`,
   `EntityBaseTearObserved`.
2. `src/WotBTreader.GameIntegration/Session/GameSessionCoordinator.cs` —
   batch loop (~2495): track attempts + tear, set the fields; exhaustion
   paths keep `ConsistentDoubleRead: false` (already item failures).
3. `src/WotBTreader.ApiContracts/OffsetDiscoveryContracts.cs` + `src/
   WotBTreader.Host.Web/Endpoints/GameApiEndpoints.cs` — forward the new
   fields on the batch response item (~1145).
4. Tests — `GameSessionCoordinatorTests.cs`:
   - `:1726` (batch item asserts false) flips to assert the witness semantics:
     stable pair → true + `RegionReadAttempts 1` + `RegionTearObserved
     false`; torn-then-settled (the existing `RegionTearRetriesAndSucceeds`)
     → true + attempts > 1 + `RegionTearObserved true`; exhausted
     (`RegionAlwaysTornFailsRegionOnly`) → item failure, flag false.
   - `:2616` (single-read asserts false) STAYS false — pins the honest
     single-read boundary.
   - `:2536`/`:3155` (camera pose true) unchanged — the precedent.
5. Docs — `docs/operations/batch-entity-read-design.md` (flag semantics),
   `docs/operations/item7-hardware-atomicity-proof-plan.md` (step 2 → applied),
   `docs/operations/offset-promotion-checklist.md` (the "requires it false"
   clause — the plan's contract-change section says the checklist's clause is
   updated in the same proposal), `offline/` pack if it mentions the flag.

## 4. Consumers audit (why the flip is behavior-neutral)

- The live-frame assembly reads the region BYTES and ignores the flag — the
  flip changes reporting only, not the frame's render decisions.
- No consumer branches on `ConsistentDoubleRead: false` on the batch item
  today (verified: only tests + DTO forwarding reference it on the batch
  surface).
- The flag is evidence/reporting; the overlay never receives it (loopback
  client renders bytes, not provenance).

## 5. Owner decision points

1. Approve the flag semantics as "stability-witnessed snapshot" (not
   hardware-atomicity) — accept the camera-pose precedent as the pattern.
2. Approve the three measurement fields (naming/surface).
3. Approve keeping the SINGLE-read surface at false (no witness) — the
   honest boundary.
4. Approve the checklist clause update in the same change.

## 6. Applied result and remaining live gate

The application contract, coordinator, API DTO/endpoint mapping, and focused
tests were applied on 2026-08-14. Stable pairs report true; torn-then-settled
pairs report attempts greater than one plus `RegionTearObserved=true`; an
exhausted pair fails the item with the flag false; entity-base tears are
reported independently. The single-read surface remains false.

The same day's two-replay live cluster measured six batch passes and twelve
camera probes, but it ran immediately BEFORE this apply: the artifacts prove
the delivered batch items resolved without unstable-snapshot exhaustion, yet
cannot prove that no transient retry occurred because the new tear fields did
not exist on their wire shape. Therefore `HardwareAtomicReadProven` remains
false and one post-apply Phase-4 two-replay batch pass is still required.

## 7. NOT in scope

- `HardwareAtomicReadProven` (stays false — Branch A's separate gate).
- The single-read surface, the resolver, the read surface, the offset table.
- The L3 damage-dealt lane (own plan + seam).
