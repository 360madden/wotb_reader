# G1/G2 live run: first approved session — G2 closed live, G1 honest negative

Date: 2026-08-09
Status: milestone partial — G2 same-decoded-clock proven live; G1 first live
run complete (honest negative); one-command session proven end-to-end
Scope: one approved live session via `scripts/invoke-g1-live-poll.ps1` on the
content-distinct Oasis Palms replay; resolver, read surface, and offset table
untouched

> **CORRECTION (2026-08-09, OD-RECOVERY-080):** the `avatar-helper`
> failures in this session were NOT a pointer race. The guard-page
> interceptor's PAGE_GUARD on the ring-record page failed the poll's own
> reads (ERROR_PARTIAL_COPY 299 at the first armed-page touch). The
> corrected G1 procedure runs the poll un-armed (`-SkipInterceptorArm`);
> the per-read byte-identical branch is the poll's own
> `allConsistentDoubleRead` (proven 24/24 in OD-075/076).

## Session summary

The one-command session ran end-to-end: launcher to `OK
OfflineReplayVerified`, gate-verified position-page resolve (live, 30 ms,
record `0x228659B8` / page `0x22865000`), the guard-page interceptor armed
on the ring-record page (128 module-mapped page writes captured across 803
guard events), the unchanged bounded od-073 poll inside the capture window
(19/24 resolved), and the replay-clock anchor POST at the gate. The wrapper
and all new surfaces worked on first live use after two pre-live fixes (the
state endpoint carries no pid — the wrapper now reads the process list; and
the cold sessions read exceeded the 15 s timeout — the wrapper's API timeout
is now 60 s).

## Evidence

- Launcher: exit 0, `OK OfflineReplayVerified` (cold boot covered by
  `-WindowWaitSeconds 240`; dialog dismissed).
- Position page: record `0x228659B8` / page `0x22865000` (entity 3760577,
  session `019fe7dc-97f9-7ce2-8fa0-ac6d66c53f0e`).
- Interceptor: exit 0, 1 page armed, 803 guard events, 128 hits; top write
  sites `wotblitz.exe+0x1AD2D9D` (71), `wotblitz.exe+0x230E856` (30),
  `nvwgf2um.dll+0x2183FA` (15) — the ring-record page is written by the
  game's own module-mapped copy-loop sites. Read window
  `[18:51:42, 18:52:00]` had 18 hits, liveness 53 before / 56 after →
  verdict `write-observation-observed` (the expected branch: the ring is
  actively rewritten during the reads).
- Poll: `honest-negative-or-inconclusive`, 19/24 resolved (each a
  byte-identical double-read on attempt 1), 5 `ReadFailed` at
  `avatar-helper` after exhausting 3 attempts (attemptCounts 1:19, 3:5);
  the entity stayed in the primary map for all 24 reads — a live
  pointer-race/reallocation pattern, not a despawn. Distinct 19, within-one
  11, within-three 18, `allModuleRooted=true`.
- Clock anchor: `clock_anchor appended sequence=0 uncertainty_s=1` →
  **`sameDecodedClockProven=true`** in the poll aggregate (the 2 s
  coordinator bound).

## Decision

**G2 closed live** — the `CaptureLog` anchor landed in
`replay_clock_segments` and the coordinator computed the flag from real
segments within the bound; the wiring, endpoint, caller, and flag all worked
on first live use. Correlation bound to record: anchor 1 s + gate cadence
1 s.

**G1 stays open** — the write-observation was `observed` and the poll was
19/24, so neither the clean branch nor the 24/24 byte-identical branch
holds. The machinery is proven live (real game-code write sites, correct
attach/arm/exit, correct verdict); the missing piece is a 24/24 positive
poll, which also flips G3 with the prior positive.

**G3 stays open** (no positive poll yet).

**G0 stays gated** (G1 + G3 open; no numeric-offset publication).

## Next

One further approved session re-running `invoke-g1-live-poll.ps1` targeting
a 24/24 positive poll (the `avatar-helper` read-failure pattern is the
known variable — a different battle segment or entity may avoid it). Then
G1's per-read byte-identical branch attests hardware atomicity (the clean
branch is impossible while the ring is actively rewritten), G3 flips with
the prior positive, and the pre-staged G0 review in
`docs/operations/g0-publication-review.md` can run.
