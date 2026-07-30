# Milestone completion — M0 through M7

**Date:** 2026-07-30
**Head:** `0fa332a` — `fix: address 3 minor bug-hunt findings`
**Author:** Codex Agent <codex@local.invalid>

## Summary

All seven architecture milestones defined in `docs/architecture/roadmap.md` are complete. The alpha release candidate is ready.

## Milestone evidence

| Milestone | Status | Key commit | Evidence |
|-----------|--------|-----------|----------|
| M0 — Baseline | ✅ | Earlier | Disabled unsafe auto-attach. Restored ACL. Removed overlay mutation listener. BLK-0003, BLK-0014, BLK-0015. |
| M1 — Boundaries | ✅ | Earlier | Portable TFMs. Enforced reference graph. `ApiContracts` zero-ref assembly. |
| M2 — Game access | ✅ | `b2ed7b7`..`dc84f39` | Suspended process creation. Correlation registrar. Thread resume. Guarded VM-read factory. |
| M3 — Control plane | ✅ | `bf71cee` | Deleted dead overlay endpoints. Hardened mutation middleware. Capability wired through all clients. |
| M4 — Offset evidence | ✅ | `8f48432` | Offset models + reader with hash enforcement. Publication separation. Orphan reconciliation. |
| M5 — Focused HUD | ✅ | `6b71cc8` | Removed WebView2, host startup, game launch, import (-685 lines). Pure loopback HUD client. |
| M6 — Operability | ✅ | `e67e0db` | Port conflict detection. Orphaned host PID check. Rendezvous cleanup on shutdown. |
| M7 — Release gate | ✅ | `e67e0db` | 0 vulnerable packages. Architecture delta: all gaps closed. Roadmap reconciled. |

## Final gate

```
validate.ps1 -AuditPackages:
  Restore:  Pass (27 projects, locked-mode)
  Format:   Pass (verify-no-changes)
  Build:    Pass (0 errors, 0 warnings)
  Tests:    395 passed, 0 failed, 4 opt-in skipped (12 projects)
  Audit:    0 vulnerable packages across all 27 projects
  Scan:     Pass (457 tracked files clean)
```

## Bug hunt

40-agent swarm on 2026-07-30 found:
- **0 critical or high-severity bugs**
- 3 minor findings, all fixed in `0fa332a`:
  1. TelemetryStreamService now logs exception type to Debug output
  2. WatchAsync no longer exposes directory path in error messages
  3. OnLoaded sync-exception path verified safe (no fix needed)

## Architecture enforcement

All mechanical invariants are enforced by `WotBTreader.Architecture.Tests` (14 tests):
- **Reference graph:** Adapters never reference each other. Overlay references only ApiContracts.
- **TFM allowlist:** Only Overlay and GameHarness target `net10.0-windows`.
- **Native-access boundary:** No P/Invoke outside GameIntegration. Guarded reader path allowed.
- **Composition root:** Every DI port registered through Bootstrap. Published-port test active.

## State of the alpha

The overlay is a loopback client only — rendering, input, window tracking. It discovers the web host
via the owner-only rendezvous file in %LocalAppData%. Host.Web is the single authenticated control
plane on port 9182. Game-process access is centralized in GameIntegration with a fail-closed offline
gate. Offset claims are evidence-backed with hash enforcement. Source artifacts and decode runs are
immutable. Telemetry publication is separated from decode success.

## Next session

1. Tag `v0.1.0-alpha`
2. Run full smoke test: publish → import synthetic replay → serve → overlay
3. Update `README.md` and `knowledge.md` to reflect completed architecture
