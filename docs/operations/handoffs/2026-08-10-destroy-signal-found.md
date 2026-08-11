# Handoff: destroy signal found — type-10 marker → `Destroyed` events

**Date:** 2026-08-10 · **Status:** Complete (offline) · **Scope:** replay
decoder + HUD death path; no live memory work

## What was found

The destroy signal is a **type-10 position packet with the per-entity
constant (payload +24..+35) zeroed AND the status flags byte (+48)
cleared** — the same 49-byte layout as a normal sample, but the constant
field zeroes at the death instant. Verified on both 11.19 replays:

- **15/15 destroyed tanks** have exactly one *first* marker; **0/13
  survivors** have any (perfect classifier, no false negatives/positives).
- The position stream **freezes at the marker** — the death instant. The
  wreck then re-broadcasts the frozen position for tens of seconds and can
  re-carry the marker byte pattern (Dead Rail 2549399 showed 3 markers),
  so **only the first marker per entity is a death**.
- Ruled out en route: type 4's entity markers (fire mid-battle for entities
  that keep streaming), amt=0 direct-damage events (last *damage* events,
  not a kill), the type-7 status stream (states toggle constantly, no
  transition at death times), and subtype-38 entity-method packets (fire on
  the effect entity).

## What changed

- `EventPacketDecoders.TryReadPosition` now flags `IsDestroyMarker` on
  `PositionObservation` (zeroed constant 24..35 + byte 48 == 0).
- `WotbReplayDecoder.BuildEvents` emits one `CanonicalEventKind.Destroyed`
  per roster entity from the **first** marker only (dedup by entity);
  non-roster entities (viewpoint, debris) are ignored even though they can
  carry the byte pattern.
- `SyntheticReplayFactory.CreatePositionPayload` now writes a non-zero
  per-entity constant and flags=1 for normal samples (it previously left
  both zeroed, which would have made every synthetic packet a marker), with
  a `destroyMarker` option; `CreateReplay`/`CreateEventStream` gained
  `includeDestroyMarker`.
- New test `DestroyMarkerEmitsSingleDestroyedEventPerRosterEntity`: two
  markers for entity 100 → one Destroyed at t=3.0; marker for non-roster
  999 → ignored.

## Verified

- Full suite green (all 13 test projects, ~890 tests, 0 warnings).
- Two-replay rehearsal on real data (`--data-root .data` imports):
  - Oasis Palms: 8 Destroyed events — 3760569@95.22, 3760574@161.81,
    3760573@172.12, 3760571@179.83, 3760568@186.52, 3760567@186.82,
    3760570@207.23, 3760576@245.10 — exactly the death instants.
  - Dead Rail: 7 Destroyed events — 2549395@109.09, 2549399@156.49,
    2549396@164.49, 2549398@175.99, 2549404@192.28, 2549400@215.48,
    2549407@232.08 (2549399 deduped from 3 markers to 1).
- CLI `overlay-frame --json` on the re-decoded Oasis session:
  - `alive=false` lands at the right frames (3760569 dead by t=100,
    3760574 by t=165, six dead by t=190; 3760570 alive until 207.23).
  - Death pip renders: `Destroyed` pip for 3760567 at t=187–188 with
    screen coords (1112,576); out-of-viewport deaths (3760576) correctly
    drop the pip while still flagging `alive=false`.

## Consequence

The HUD's `Alive` flag and death pips now run on **real replays**, not just
synthetic fixtures — the gap documented in the previous handoff
(2026-08-10 packet-inventory) is closed. Docs updated:
`offline/replay-format.md` (destroy marker row + resolved finding) and
`docs/operations/product-roadmap.md` (V3 row).
