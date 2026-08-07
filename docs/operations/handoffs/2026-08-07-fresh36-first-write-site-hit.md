# OD-RECOVERY / FRESH36 — FIRST REAL WRITE-SITE HIT REPORT FROM THE GAME (2026-08-07)

**Campaign milestone:** the C# guard-page interceptor captured **51 real writes**
to the armed viewpoint x-family addresses during a **live battle** — the first
`odwt-*.family.json` hit report with real game write-site RIPs. The
FRESH34–35 timing fixes (log-derived real battle window, fire-by deadline,
playback estimate, staging-anchor contract) all held; no new bug surfaced.

## Evidence chain

**Run:** `od-049-autoloop.ps1 -AttachSmokeOnFirstRound -StageViewpointOnly`,
git `da48c92`, interceptor publish fresh, host Release build fresh, stale host
killed pre-flight. Session `019fdaa1-e6a4-71ad-bb0a-d0604a366837`
(GB08_Churchill_I, viewpoint entity 2549401, decoded 271.4s).

**Timeline (UTC):**

| Event | Time |
|---|---|
| Start replay event | 05:11:28 |
| LoadGameScene ends (real scene) | 05:11:32 |
| Staging gate (elapsed_s=0 → wait 55s) | 05:11:35 |
| fire-by deadline | 05:13:02.7 |
| Interceptor invoked (report name `od-048-autotrace-20260807-011305.json`) | ~05:13:05 |
| Deaths (onLeaveWorld ×4) | 05:13:20–22 |
| Battle end (last log write) | 05:13:22 |
| Trace window (25s) | ~05:13:05–30 |

The trace window **overlapped the live battle** (deaths at 05:13:20–22 fell
inside it); `windowValuesChanged=true` proves the armed values moved during
the window (a frozen result screen rewrites the same value → `changed=false`,
the FRESH34 signature).

**M1 correlate:** `verdict=evidence-strong`, `addresses_scored=554`,
`total_samples=7202`, 68 strong survivors, 1 solo family emitted
(`axis=x members=4 score=1 span=77.3 band=1.5s` — structurally excluded from
real families → solo path). `stoppedReason=fire-by-deadline` after **14
rounds** (the FRESH35 fire-by stopped sampling early enough for the
correlate+verdict+launch+trace to land inside the battle).

**Hit report** (`.data/od-048-autotrace-20260807-011305.json.family.json`):
`verdict=family-hit hitsTotal=51 hitMembers=4 armedCount=4
windowLiveness=running windowValuesChanged=true interceptorGuardEvents=50
interceptorPagesArmed=1` (all four addresses live on one 4KB page).

| Armed address | Axis | Hits | Write-site RIPs |
|---|---|---|---|
| 0x3D525BE8 | x | 4 | 0x01005F19, 0x01331878, 0x01B62D9D, 0x0239E856 |
| 0x3D525CC0 | x | 4 | same 4 |
| 0x3D525C98 | x | 4 | same 4 |
| 0x3D525C20 | x | 3 | same 4 (minus one) |

All four addresses are written by the **same 4-instruction code-site set** —
a coherent per-frame transform/position update pattern, not random noise.
RIPs are in the 0x01000000–0x0239FFFF range (game/dll code space; exact
module bases not captured this round).

## Root-cause closure (FRESH34 → FRESH36)

- FRESH34's 0 hits = the trace fired on the frozen result screen, ~40s after
  the real end: the battle-end model assumed 1×+50s attendance while the game
  plays ~2×; AND `Test-BlitzBattleEnded`'s local-date parse skipped every log
  line during the live run (date-rollover). Both fixed offline (`38f5e91` +
  `b5849a5` + `da48c92`), no live session spent.
- The FRESH36 measured playback was ~2.47× (271.4 decoded / 110s wall) vs the
  2.0 default estimate — the estimate errs *late* (predicted end 05:13:47 vs
  real 05:13:20), yet the fire-by still landed the trace inside the battle.
  The `blitzLog.measuredPlaybackSpeed` stays null this round (the loop's
  silence-speed snap needs >20s post-activity silence, which the fire-by stop
  precedes); the post-hoc derived speed (2.47×) is the feedback value for the
  next `-PlaybackSpeedEstimate`.

## Next step (M2 tail)

Resolve which module owns the 4 RIPs → module-relative RVAs → the writing
instruction + member displacement → the probable position/transform object
base → sibling-coordinate local read → resolver classification (pointer path /
object relationship / code signature). Requires a module-base snapshot (the
guarded read API or a system-informer capture) at trace time — build offline,
then one more live round.

## Files

- `.data/od-048-autotrace-20260807-011305.json.family.json` (hit report)
- `.data/od-049-autoloop-result.json` (M1 report, `blitzLog` block)
- `.data/od-049-fresh36.log` (driver log)
- `$LOCALAPPDATA/wotblitz/DAVAProject/blitz-logs_20260807001113.txt` (game log)
