# Handoff — 2026-08-09: Second G1/G2 live session (OD-RECOVERY-079)

## Session summary

Ran the one-command chain (`scripts/invoke-g1-live-poll.ps1` on the Oasis
Palms replay, `-WindowWaitSeconds 240`) end-to-end a second time. Everything
held except the one known open variable:

- Launcher → `OK OfflineReplayVerified` (gate OK)
- Position-page resolve live: record `0x262CF6A8` / page `0x262CF000`
- Guard-page interceptor armed on the ring-record page (799 guard events,
  129 module-mapped page writes)
- Unchanged bounded od-073 poll inside the capture window
- Clock anchor POST at the gate → `sameDecodedClockProven=true`

## Evidence (all in `.data/diagnostics/g1-live-20260809-165135/`)

- `g1-evidence.json` — verdict `write-observation-observed`, 18 in-window /
  53 before / 57 after, `pollSucceeded=true`, `pollExit=0`, `reportExit=0`
- `interceptor-report.json` — 129 hits; dominant sites
  `wotblitz.exe+0x1AD2D9D` (85) and `+0x230E856` (39), plus `+0xF75F19` (3)
  and two `nvwgf2um.dll` sites (1 each)
- `od073-poll.json` — `honest-negative-or-inconclusive`, resolved **22/24**
  (up from 19/24 in OD-RECOVERY-078), 22 distinct triples, 12 within one
  world unit, 21 within three, `allModuleRooted=true`,
  `sameDecodedClockProven=true`

## What changed vs the first session

| Field | OD-RECOVERY-078 (run 1) | OD-RECOVERY-079 (run 2) |
|---|---|---|
| Resolved reads | 19/24 | **22/24** |
| Failed reads | 5 @ `avatar-helper` | 2 @ `avatar-helper` |
| Read window hits | 18 | 18 |
| Liveness before / after | 53 / 56 | 53 / 57 |
| Total page writes | 128 | 129 |
| Dominant write sites | +0x1AD2D9D (71), +0x230E856 (30) | +0x1AD2D9D (85), +0x230E856 (39) |
| G2 flag | `true` | `true` (re-confirmed) |

## Decision

- **G2: closed live** (second independent confirmation).
- **G1: still open.** The poll improved to 22/24 but the acceptance is a
  24/24 positive (per-read byte-identical branch; the clean branch is
  impossible while the ring is actively rewritten). The 2 failures are the
  same `avatar-helper` pointer-race pattern — battle-segment dependent.
- **G3: still open** (needs a positive poll + prior; this run was not
  `stable-resolver-positive`).
- **G0: stays gated.** No offset-table change, no resolver change, no read
  broadening.

## Next

One more approved session with the identical command, targeting 24/24. The
read-failure pattern is the only variable; a different battle segment or
entity may avoid it. All managed processes were stopped after the session
(0 remaining). No product code changed this session.
