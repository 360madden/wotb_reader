# FRESH22 — first armed trace completes, family-no-hit; FRESH23 span-first selection fix

Date: 2026-08-06
Status: **fixed + validated offline; FRESH23 = consensus-class arming**

## FRESH22 live outcome

`od-049-autoloop -AttachSmokeOnFirstRound -StageViewpointOnly` (50 rounds):

- Launch stack clean (resize 640×360, click round 1, marker 16:55:12, gate,
  staging tick 5.7s, full 3000 staged).
- Smoke passed first try; 50/50 rounds, zero 401s; 566 addresses scored.
- **`family_solo_emitted axis=z score=0.94 band=27.5s`** — the FRESH22
  band-floor/span-floor fix worked; the solo family was emitted (FRESH21
  skipped it).
- **`auto_write_trace INVOKING … trace_s=25`** — the trace fired end-to-end:
  gate `OfflineReplayVerified`, liveness ok, x64dbg attached (pid 0xC8C0),
  `scriptload+scriptrun` injected, memory BP armed on `0x228B9050`, window
  ran 16:59:12–16:59:37, `released_detach`.
- **`family-verdict=family-no-hit hit_members=0`** — zero writes to the armed
  address in a live 25s window. `trace-complete`, exit 0. Game stopped after
  the campaign.

## Diagnosis (evidence-backed, not a timing artifact)

- The decoded replay (medvedkovo session 019fb86c, entity 2549401) shows the
  viewpoint tank **alive and moving through the ENTIRE battle**: last movement
  t=266s of 279s; z goes 23.3 → −10.1 → −28.3 at t≈190–240s — exactly the
  trace window. A per-frame position field MUST be written while the tank
  drives.
- Therefore zero writes = the armed address is **not** the per-frame field.
- **Root cause: the solo selection picked a partial-window copy.** The tiebreak
  was score-desc: `0x228B9050` (span 75.5, score 0.94) beat the **consensus
  class — ~20 z addresses at span EXACTLY 275.4, score 0.92, shift 0** (the
  same synchronized-copy signature as FRESH21's 29). A span-75.5 address
  tracked the series only part-way (a copy updated on a slow/partial cadence);
  it correlates well within its active window but is not written every frame.
- The FRESH22-armed address ranks **41st** under span-first selection.

## Fix (FRESH23, `od-048-monitor-correlate-session.ps1`)

1. **Span-first selection**: solo candidates sort by (span desc, score desc,
   band asc). A full-trajectory field carries the axis's full span; a static
   value, a partial-window copy, and a low-information y all lose. This is the
   pivot's "movement span" qualification made the primary discriminator.
2. **Arm the top-N consensus addresses** (new `-AutoTraceMaxSoloMembers`,
   default 4 = DR0-DR3 cap): the real per-frame field is one of MANY
   synchronized copies, so arm 4 at once instead of gambling on one.

## Validation (all green)

| Check | Result |
|---|---|
| Parse pwsh 7 + PS 5.1 | clean |
| PSSA hygiene gate | PASSED (66 pre-existing advisories) |
| ASCII | clean |
| Selection simulation on the REAL FRESH22 result | 47 candidates pass floors; **top-4 = the span-275.4 consensus class** (0x22B89A10, 0x22B4EED0, 0x22D568D0, 0x238CC890 @ 0.92, shift 0); FRESH22's armed partial now rank 41 |
| 4-member write-trace DryRun | `family_members_armed=4 unarmed=0 dr_limit=4` — 4× `bpm addr,1,w` memory BPs + per-address log/savedata generated |

## FRESH23 live checklist

1. Pre-flight: no stray game/host/debugger; replay present; fresh host publish.
2. Launch `od-049-autoloop -AttachSmokeOnFirstRound -StageViewpointOnly`.
3. Watch: `family_solo_emitted … members=4 … span=275.x` → `auto_write_trace`
   → **first `odwt-*.bin` hit report** (writer RIP/RVA, base register,
   displacement, nearby-object dump) — any of the 4 consensus addresses
   written per-frame by the position update fires it.
4. If STILL family-no-hit: the WOW64 resume-freeze class (window frozen, not
   no-write) becomes the suspect — add a post-window liveness re-read to
   distinguish frozen-window from genuinely-not-written.
