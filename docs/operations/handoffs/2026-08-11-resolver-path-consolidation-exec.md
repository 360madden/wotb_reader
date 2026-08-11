# Resolver-path consolidation — items 3 + 4 executed (2026-08-11)

**Status:** ✅ committed and pushed (tree clean, full gate green).

## Item 3 — phase-tolerance audit: ✅ VERIFIED, no gaps

Every chain read was audited for the consolidation standard (retryable
`ReplaySessionInactive` during the pre-battle phase, fail closed on unknown):

| Read | Phase handling | Verdict |
|---|---|---|
| Core resolver (`Type10EntityPositionResolver`) | PreLogin vftable `0x325ad2c` → `ReplaySessionInactive` retry; unknown → `UnsupportedSessionController` stop | ✅ |
| Coordinator `entity-position` | status passed through verbatim — never a terminal error for inactive | ✅ |
| Coordinator `position-page` / `entity-region` | status passed through verbatim | ✅ |
| Camera pose (`ReadCameraPoseAsync`) | gate-free by design; pre-battle → `AnchorNotFound` (honest); frame endpoint fails closed to viewpoint | ✅ |
| CAM-001 direct walk | bails on the PreLogin vftable; round loop continues polling (`+direct-walk-failed`) — retry by polling | ✅ |
| G1 poll (`invoke-g1-live-poll.ps1`) | explicit pre-login retry, 3 attempts, corrected mode only | ✅ |

No code changes needed — the standard already holds everywhere. Audit
recorded in `docs/operations/resolver-path-consolidation.md` item 3.

## Item 4 — legacy observation surface deprecated: ✅ DONE

Deprecation banner added to `docs/operations/legacy-observation-surface.md`
(frozen, never extended, chained fields excluded by design, resolver path
canonical). No code changes — the surface was already frozen +
test-pinned (`ChainedFields_AreExcludedFromObservationReads`).

## Remaining checklist

1. Item 1 (publish-as-chains) — convention documented in the plan; applies
   per new discovery target.
2. Item 2 (single walker) — convention documented; already the de-facto
   shape (position + camera layouts).
3. Item 5 (L1–L4 mapping) — table in the plan; sessions remain
   approval-gated.
4. Item 6 (batch N-entity rehearsal) — next substantive offline work.
5. Item 7 (hardware atomicity) — ordered LAST, not started.
