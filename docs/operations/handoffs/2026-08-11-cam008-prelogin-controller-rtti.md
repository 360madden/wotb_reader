# CAM-008 — the CAM-003 "session variant" is PreLoginController (RTTI-verified)

- Date: 2026-08-11
- Status: committed; static identification + resolver fix, unit-tested
- Supersedes: the CAM-003 "third session-controller variant with a
  different layout" hypothesis — REFRAMED

## The finding

RTTI resolution against the installed 11.19.0.10 exe (read-only PE parse,
COL → type descriptor, the same technique that identified GameCamera):

| vftable RVA | Class |
|---|---|
| `0x325ad2c` (CAM-003 "variant") | **`.?AVPreLoginController@@`** |
| `0x323d9bc` (resolver-expected) | `.?AVSessionController@@` |
| `0x323d61c` | `.?AVAppController@@` |
| `0x326dd0c` | `.?AVReplayCameraController@@` (known) |
| `0x32dafa0` | `.?AVGameCamera@@` (known) |

So the CAM-003 phase drift is **not** a mystery session-controller layout:
the app's session slot holds a **PreLoginController until replay playback
actually starts**. The resolver's `0x323d9bc` gate was CORRECT to reject —
the game simply was not in a battle session during those reads. That
explains, with one fact:

- why `/discover/entity-position` returned `UnsupportedSessionController`
  in those launches (reads landed in the pre-login/lobby window),
- why `[session+0x118]` (account controller) read as garbage in the v6
  direct walk (PreLoginController has no session account controller there),
- why od-073's 24/24 (08-09) vs 0/12 (08-11) differed (launch timing vs
  the 55 s calibration window, not a binary/DLC change),
- why `replayStarted=False` at the gate correlated with the failures.

## The fix

1. **Core layout** (`Type10EntityPositionResolver`): added
   `PreLoginControllerVtableRva = 0x0325ad2c` (hash-bound, validated != 0).
2. **Resolver gate**: when the session-slot vftable equals the PreLogin
   vftable, return the retryable **`ReplaySessionInactive`**
   (`session-controller-vtable`) instead of the terminal
   `UnsupportedSessionController`. Callers now WAIT for playback instead of
   failing the build; any other unknown vftable still fails closed.
3. **CAM-001 direct walk** (`invoke-camera-state-verify.ps1`): reads the
   session vftable after the `[app+0x124]` hop and bails cleanly on
   PreLogin instead of dereferencing garbage.
4. **Test**: `Resolve_PreLoginSessionController_ReportsReplayInactiveInsteadOfUnsupported`
   (36 resolver tests green).

## Impact

- The entity-position read is now honest about the pre-login phase: a
  session poll that lands early reports `ReplaySessionInactive` (retryable)
  rather than a confusing hard failure — and the launch-flow fix is
  *timing* (wait for playback), not layout mapping.
- The camera track is unaffected (CAM-006/007 are session-controller-gate-
  free by design), which is exactly why it succeeded in both phases.

## Verified

- RTTI names above resolved from the on-disk exe (image base 0x400000).
- `dotnet build WotBTreader.sln -c Release`: 0 warnings, 0 errors.
- Core resolver tests: 36 passed (1 new).
- CAM-001 script parse-check OK (PS 5.1).

## Next steps

- The `verify-camera-projection.py` live session (CAM-007) is unaffected
  and is still the next live gate.
- Optionally: teach the G1 poll wrapper to treat `ReplaySessionInactive`
  as "wait and retry" so a poll that starts early doesn't fail the run.
