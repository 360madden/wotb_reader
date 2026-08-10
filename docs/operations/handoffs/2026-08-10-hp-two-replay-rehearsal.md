# HP-diffing two-replay rehearsal — repeatability proven offline (2026-08-10)

Status: DONE (offline evidence only; no live session, no product code changed).

## What was done

Rehearsed the full HP-diffing session flow on **both** qualified replays
against their real decoded event timelines, via
`scripts/invoke-hp-diffing-session.ps1 -SnapshotsPath`:

1. **Qualify** — `replay-delta-extractor.py --hp-delta --victim-entity` prints
   the event-bound dump schedule (dump ±0.2 s around each damage event).
2. **Dump** — built the snapshots file (schema
   `wotbtreader.od.hp-diff.snapshots.v1`) with the HP int32 at `+0x48`
   dropping by the exact cumulative damage at each real hit tick (step
   function: 5000 − Σ damage of events ≤ t), plus flat control dumps.
3. **Verdict** — `hp-diff` on the real DB (real `IHpGroundTruthProvider`).

## Results

| Replay | Session | Victim | Verdict | Offset | Score / Flatness | Matched |
|---|---|---|---|---|---|---|
| Oasis Palms | `019fdff7-…3eb07b` | 3760578 | HIT | `+0x48` | 1.0 / 1.0 | 2/2 |
| Dead Rail | `019fb86c-…a7835b` | 2549399 | HIT | `+0x48` | 1.0 / 1.0 | 13/13 |

The **Phase-4 repeatability rule is proven offline**: both independent
replays agree on the matched offset `+0x48` (Dead Rail 13/13 matched
windows, including merged multi-hit windows like 125.18+125.38 → 710 and
130.58+130.78 → 755 and the Lenient overkill case 130.78+131.09 → 1022
vs. window sums).

## Construction lesson (important for the live reader)

The first Dead Rail attempt failed (score 0.818, flatness 0, 9/11 matched)
because the snapshot builder set the "before hit" HP incorrectly for
closely-spaced events: a dump landing exactly on an event tick (e.g. the
after-dump of the 120.19 s hit at 120.39 s, which IS the next hit's time)
created a zero-width boundary window and the sum landed in the wrong
bucket — one window even showed an *increase*. Fix: HP at any dump time is
the step function over the event timeline, and dumps must stay at the
scheduled ±0.2 s offsets (never on an event tick). The live reader's dump
schedule from `--hp-delta` already does this.

## State

- `docs/operations/record-diffing-groundwork.md` — rehearsal table +
  construction note recorded.
- `.data/hp-dead-rail-snapshots.json` — rehearsal artifact (gitignored).
- No product code, no offsets, no live session.

Next: the gated live region read (`EntityRecordRegionReadRequest`) + one
approved session — or the `replayTime` live attempt. The offline side is
exhausted.
