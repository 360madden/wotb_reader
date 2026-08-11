# Resolver-path consolidation plan (module-rooted chain resolution)

**Date:** 2026-08-11 (plan; execution is the subsequent workstream)
**Status:** plan only — no code/doc changes beyond this doc, its roadmap
cross-link, and the planning handoff.

## Strategy (why this exists)

1. **Replays are the live-mode test harness.** The memory track exists for the
   future live-game overlay (Phase 5, policy-gated). Replays are the reliable
   rehearsal ground: deterministic decoded ground truth, repeatable launches,
   a real game process (real ASLR, real phases), and no policy risk. Every
   read developed here transfers to live mode as-is — only the
   `OfflineReplayVerified` gate is replay-specific (live mode gets its own
   policy gate, X1).
2. **The resolver path is the canonical read surface.** Position is published
   and mechanically walkable (`OD-RECOVERY-083/084`); the post-publication
   doc calls the resolver "the authoritative position reader." The legacy
   offset-table observation surface is a frozen artifact (offsets 0, chained
   fields excluded, emits nulls — pinned by
   `ChainedFields_AreExcludedFromObservationReads`). New work goes through
   the resolver path; the legacy surface is never extended.
3. **Module-rooted chain resolution** = the read model: learn the runtime
   module base (ASLR-proof anchor), walk gated pointer hops from
   `moduleBase + RVA` to the target, identity-check every hop, double-read
   the payload region. Addresses are *resolved* at runtime, never assumed.

## The consolidation checklist (execution order)

> Hardware-atomicity proof is **LAST by design** (item 7): it needs the batch
> read-surface design (X2) and the per-frame read discipline to exist first;
> every item before it is a prerequisite or makes the proof cheaper.

### 1. Publish as chains, never legacy offsets
- Every new discovery target follows the G0 pipeline: gate → evidence →
  hash-bound `Type10<X>Layout` → `chains` section → `Verified` → walker-read.
  Offsets stay 0 for chained fields.
- Promotion requirements stay per the workflow (exact executable identity,
  2 launches × 2 replays, harness invariants, static-analysis + GameHarness
  provenance, lead + decoder-auditor approval).

### 2. Chain-walker is the single sanctioned runtime reader
- One walker (`OffsetChainWalker`), one gate discipline (version-pin +
  sha256 + identity gates + byte-identical double-read), one per-target
  layout. The camera endpoint (CAM-005, `Type10CameraPoseLayout`) is the
  reference shape.
- Convention (below) governs every new layout; no parallel read paths.

### 3. Phase tolerance is the standard, not the exception
- Every chain read returns the retryable `ReplaySessionInactive` for the
  pre-battle phase (CAM-008) and callers retry through it (G1 poll pattern).
  Unknown vftables fail closed. Never dereference garbage.
- **✅ AUDITED 2026-08-11 (no gaps found):** Core resolver (PreLogin vftable
  → `ReplaySessionInactive` retry; unknown → `UnsupportedSessionController`
  stop); coordinator `entity-position` + `position-page`/`entity-region`
  both pass the status through (never a terminal error for inactive);
  camera-pose is gate-free by design and reports `AnchorNotFound` during
  pre-battle (honest; the frame endpoint fails closed to the viewpoint, the
  CAM-001 script polls on); the CAM-001 direct walk bails on the PreLogin
  vftable and the round loop keeps polling (`+direct-walk-failed`); the G1
  poll retries explicitly (3 attempts, corrected mode). Standard holds.

### 4. Freeze + deprecate the legacy observation surface
- No code changes (already frozen + test-pinned); add a deprecation note so
  nobody extends it. The observation keeps emitting nulls for chained fields;
  the resolver endpoints are the sanctioned API.
- **✅ DONE 2026-08-11:** deprecation banner added to
  `docs/operations/legacy-observation-surface.md` (frozen, never extended,
  resolver path canonical).

### 5. L1–L4 mapping onto the pipeline
| Track | Anchor | Region / correlate | Published form | Live session cap |
|---|---|---|---|---|
| L1 HP | entity-base | region ≥ 0x120, correlate int16; `VerifyPlayerHpChain` 26/26 rehearsed (`+0xB8` current, `+0x11C` max) | chain (`chains` + layout) | 1 |
| L2 Facing | ring-record | dump vs `position_samples.yaw`; probe `+0x2C..0x37` | chain | 1 |
| L3 Damage | viewpoint counter | `invoke-hp-diffing-session.ps1 -Track damage-dealt` (rehearsal HIT `+0x48` 5/5 both replays) | chain | 1 |
| L4 replayTime | static chain (`GameCore 0x04095c88 → … → [BWServerConnection+0x58]+0x90` Double) | OD-044 interceptor, byte-exact Double; `-ArmSourceOnFirstHit` load-bearing | chain | 1 |

  Each session reuses L0's region dump (multipurpose). NOTE: `+0x48` is the
  synthetic fixture for HP; HP's real location is discovered by the
  record-diffing playbook scan of the dumped region — an honest no-hit widens
  the anchor (entity base / ring record).

### 6. Live-mode alignment (X2/X4)
- Rehearse **batch N-entity reads** on replays (the walker already resolves
  any entity id) so the per-frame live surface is measured before live mode
  needs it.
- **✅ DESIGN DONE 2026-08-11:** `docs/operations/batch-entity-read-design.md`
  — the batch surface (one round trip for ≤ 16 entities, one G2
  clock attestation per batch, per-entity status so an unresolved entity
  never fails the frame, read discipline: gate → resolve-all → read-all →
  one post-read snapshot, and the item-7 atomicity hook: per-entity
  `ConsistentDoubleRead` + a measured verification window, added additively
  when the proof starts). Single-read seam unchanged (back-compat).
- **✅ COORDINATOR + ENDPOINT IMPLEMENTED 2026-08-11:**
  `ReadEntityRegionsAsync` (validation clamps, resolve-all → read-all → one
  post-read G2 snapshot, per-entity statuses in request order) and
  `POST /api/v1/game/discover/entity-regions`, with 7 coordinator + 4 web
  endpoint tests.
- **✅ REHEARSAL FULLY PRE-STAGED 2026-08-11:** driver
  `scripts/invoke-batch-rehearsal.ps1` + cross-check tool
  `scripts/python/batch-rehearsal-crosscheck.py` — qualify the decoded
  roster, batch-dump the full roster per replay time through the gated
  seam (one clock attestation per batch), and match each ring-record
  position triple (+0x10) to the nearest decoded sample. Cross-check proven
  on real decoded data (42/42 PASS, corruption detected; exits 0/1/3). The
  batch response also carries the read-pass measurement (`Measurement`:
  resolve-start → last-read, + snapshot moment) so the rehearsal measures
  the item-7 verification window in the same session.
- **✅ X3 LIVE-ROSTER ENUMERATION IMPLEMENTED + REHEARSAL-WIRED 2026-08-11:**
  `EnumerateEntities` (full-tree walk over the three entity maps + cache,
  deduped, per-tree node bound fails closed, movement-filter vtable gate
  → avatar family) + `EnumerateEntitiesAsync` + `POST
  /discover/entity-roster` (ids only; addresses die inside the
  coordinator). The rehearsal driver now composes BOTH rehearsals in one
  command: `invoke-batch-rehearsal.ps1 -EnumerateLive -LiveAcquire` —
  enumerate → filter → batch-read the ENUMERATED ids → cross-check
  positions, with the enumeration verdict (matched/missing/extra + filter
  precision, `--enumeration` mode, fail-closed on TraversalLimited) closing
  the X3 filter-precision question in the same session. See
  `docs/operations/live-roster-read-design.md` (X3) and
  `docs/operations/live-frame-loop-design.md` (X4). The session itself
  still needs one approved live launch (`OD-RECOVERY-086`;
  `docs/operations/od-recovery-086-evidence-template.md`).
- `LiveFrameSource` consumes the **resolver endpoints**, not the observation
  DTO. Deferred decision (shared-contract proposal when that work starts):
  promote resolver results into the observation contract vs. keep the
  resolver endpoints as the sanctioned API — lean: keep the resolver
  endpoints.

### 7. LAST: hardware-atomicity proof
- Prove the position bytes (and per-target payload regions) are
  hardware-atomic or design the read discipline that makes atomicity
  unnecessary (double-read + verification window, batch surface).
- Explicitly deferred until items 1–6 land; nothing in the checklist before
  it blocks on it.

## Per-target layout convention (`Type10<X>Layout`)

- Lives in `Core/Discovery`, hash-bound: `GameVersion` + `ExecutableSha256`
  of the exact analyzed binary.
- Carries **replay + live variants** for every anchor RVA/vftable (the
  replay variant is the test path; the live variant ships to Phase 5 — this
  is how CAM-002 discovered live-mode facts during a replay session).
- Every hop offset + expected vftable recorded; region offsets + length for
  the double-read payload.
- Published additively in `memory-offsets/11.19.0.10.json` `chains` (offsets
  stay 0); `offset_check.py --check-schema` + fidelity gate enforces draft ↔
  published identity; the walker reads the table directly.

## Decision log

- 2026-08-11: plan adopted; hardware atomicity ordered last; observation
  promotion decision deferred to the X2/X4 proposal; yaw quarantine
  **resolved-by-supersession 2026-08-11** (see ledger) — yaw is a runtime
  chain field on the movement ring record (`+0x2C`), not a static offset;
  the three legacy static candidates were mutually inconsistent and are
  retired; the ring-record `+0x2C` prediction (rehearsed 27/27 + 35/35
  against packet yaw) is the anchor pending the live L2 facing session.
