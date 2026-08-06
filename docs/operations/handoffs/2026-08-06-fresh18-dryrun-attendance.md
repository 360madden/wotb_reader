# Handoff — FRESH18 attendance-latency dry-run (offline prediction)

Date: 2026-08-06 · Author: agent session · Status: **dry-run complete, live pending**

## Why this dry-run exists

FRESH15j (the last live round) staged at the correct *time* (56s marker-elapsed)
but still produced 0 strong viewpoint survivors — all 12 results edge-aligned
at the ±30s sweep edge with best score 0.34. Root cause diagnosed and fixed in
commit `c918694` (FRESH18): the decoded trajectory's tick 0 is **match-begin**,
which lags the Start marker by the ~50s loading + all-players-in-attendance
phase. Two consequences:

1. **Staging scanned the wrong value** — it targeted ground-truth tick 56s while
   the live tank sat at battle tick ~6s, so the true position field never
   matched and decoy floats were staged instead.
2. **The correlate could not align even a correctly staged field** — with
   `baseTicks = (wall − marker)`, the correct alignment needed a **−50s shift**,
   outside the ±30s sweep.

The FRESH15j report persisted only *aggregates* (no raw observation series), so
a literal replay of its series is impossible. This dry-run instead rebuilds the
series the way FRESH18 **will** stage it and scores it through the **real** host
correlate endpoint (TrajectoryCorrelationScorer over HTTP, not a
reimplementation) under both anchors.

## Method

- Session: `019fd74c-8902-7624-ab9a-d0139e799128` (FRESH15j's battle)
- Viewpoint entity: `2549401` (mrkool1138 / GB08_Churchill_I, 2784 decoded samples)
- Series: viewpoint x/y/z sampled every 2s over battle ticks 6s→140s (the
  FRESH18 staging window), wall-stamped at `battleStart + tick`
- Two POSTs to `/api/v1/game/discover/correlate`:
  - **A (OLD bug):** `replayStartWallTimeUtc` = raw marker
  - **B (FRESH18):** `replayStartWallTimeUtc` = marker + 50s attendance

## Results (real scorer)

| Axis | span | OLD anchor (marker) | NEW anchor (battleStart) |
|------|------|--------------------|--------------------------|
| x | 91.2 | **0.574, shift −29.5, band [−30..−29.5]** (edge-pinned) | **1.000, shift 0, band [−0.5..1]** (68/68) |
| z | 12.9 | 0.750, shift −29.5, band [−30..−29.5] | 1.000, shift 0, band [−5..1.5] |
| y | 5.2 | 1.000, shift 20.5, band [20.5..30] (degenerate: tiny span matches anywhere) | 1.000, shift 0, band [−5..30] |

**Prediction confirmed:** under the OLD anchor the discriminating x axis (largest
span, real movement) collapses to 0.574 with its shift band pinned at the sweep
edge — the exact FRESH15j signature. Under the FRESH18 anchor the same series
scores **1.000 at shift 0** with a narrow band, i.e. the corrected anchor makes
the viewpoint series align where the sweep can reach it.

The OLD anchor's y=1.000 is the known degenerate class (span 5.2 — the height
axis barely moves, matches at many shifts; FRESH10/FRESH12 warned this exact
artifact). It is NOT evidence the old anchor worked.

## Expected live behavior (FRESH18)

- `staging entity=2549401 tick_est≈60000000` (6s, not 56s)
- Viewpoint x/z results scoring ~1.0 at shift ≈ 0, **not** edge-aligned
- If the live field is staged, the solo-family gate should arm it and the
  auto-write-trace should produce the first `odwt-*.bin` hit report

## Caveats

- The dry-run series is noise-free (observation value = exact decoded sample),
  so 1.000 is the ceiling; live float32 rounding + staging-tolerance jitter will
  shave it. A live survivor at ≥0.9 with shift near 0 and a non-edge band is the
  success criterion.
- The dry-run assumes the live game plays the replay at exactly 1× from
  match-begin; the ±30s sweep absorbs residual jitter.

## Tooling

`tmpwotb-e2e/dryrun-fresh18-attendance.py` — rebuilds the viewpoint series from
the host DB and scores both anchors against the live host. Requires the host
running (`dotnet run --project src/WotBTreader.Host.Web -c Release --no-build`)
and the rendezvous capability. Rerun after any further tick/anchor changes.
