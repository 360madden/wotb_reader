#!/usr/bin/env python3
"""Offline dry-run of the FRESH18 attendance-latency fix against the FRESH15j
session (019fd74c-8902-7624-ab9a-d0139e799128, viewpoint 2549401).

The FRESH15j report persisted only aggregates (no raw series), so a literal
replay of its observations is impossible. Instead this rebuilds the series the
way FRESH18 WILL stage it - the viewpoint entity's decoded position at
battle-tick cadence, wall-stamped at (battleStart + tick) - and scores it
through the REAL host correlate endpoint under BOTH anchors:

  A) OLD bug: replayStartWallTimeUtc = marker (Start replay event). The wall
     stamps map to ticks +50s ahead of the true battle tick, needing a -50s
     shift the +/-30s sweep cannot reach -> predict edge-aligned / weak.
  B) FRESH18: replayStartWallTimeUtc = marker + attendance -> baseTicks already
     equals the true battle tick -> predict strong score, shift ~0, narrow
     band, NOT edge-aligned.

Uses the real TrajectoryCorrelationScorer over HTTP, not a reimplementation.
"""
import json
import os
import sqlite3
import sys
import time
import urllib.request

SESSION = '019fd74c-8902-7624-ab9a-d0139e799128'
VIEWPOINT = 2549401
MARKER = '2026-08-06T13:39:21.0000000Z'  # from the FRESH15j report
ATTENDANCE = 50.0
TOL = 6.0
MAX_SHIFT = 30
MIN_SPAN = 0.5


def load_viewpoint(db_path):
    con = sqlite3.connect(db_path)
    rows = con.execute(
        "SELECT replay_time_ticks, raw_x, raw_y, raw_z FROM position_samples "
        "WHERE battle_session_id=? AND entity_id=? ORDER BY replay_time_ticks",
        (SESSION, VIEWPOINT)).fetchall()
    ticks = [r[0] for r in rows]
    xs = [r[1] for r in rows]
    ys = [r[2] for r in rows]
    zs = [r[3] for r in rows]
    print(f'dryrun: viewpoint_samples={len(rows)} tick_range={ticks[0]}..{ticks[-1]}')
    return ticks, xs, ys, zs


def nearest(ticks, target):
    """Binary search nearest tick index (pure Python, no pwsh perf trap)."""
    lo, hi = 0, len(ticks) - 1
    while lo < hi:
        mid = (lo + hi) // 2
        if ticks[mid] < target:
            lo = mid + 1
        else:
            hi = mid
    return lo


def build_series(ticks, xs, ys, zs, battle_start_iso, start_s=6.0, end_s=140.0, cadence_s=2.0):
    start_tick = int(start_s * 10_000_000)
    end_tick = int(end_s * 10_000_000)
    # battle_start as seconds since epoch for wall stamp math
    import datetime
    bs = datetime.datetime.fromisoformat(battle_start_iso.replace('Z', '+00:00'))
    out = {}
    for axis, vals in (('x', xs), ('y', ys), ('z', zs)):
        samples = []
        t = start_tick
        while t <= end_tick:
            idx = nearest(ticks, t)
            wall = bs + datetime.timedelta(seconds=t / 10_000_000)
            samples.append({'wallTimeUtc': wall.isoformat().replace('+00:00', 'Z'), 'value': vals[idx]})
            t += int(cadence_s * 10_000_000)
        # deterministic pseudo-address per axis
        addr = f'0x{0x50000000 + (ord(axis) & 0xFF):08X}'
        out[addr] = samples
        print(f'dryrun: series_axis={axis} addr={addr} samples={len(samples)}')
    return out


def correlate(base, cap, series, anchor):
    body = {
        'groundTruthSessionId': SESSION,
        'replayStartWallTimeUtc': anchor,
        'tolerancePerAxis': TOL,
        'maxTimeShiftSeconds': MAX_SHIFT,
        'minMovingSpan': MIN_SPAN,
        'observations': [{'Address': a, 'Samples': s} for a, s in series.items()],
    }
    req = urllib.request.Request(
        base + '/api/v1/game/discover/correlate',
        data=json.dumps(body).encode(), method='POST',
        headers={'X-WotBTreader-Capability': cap, 'Content-Type': 'application/json'})
    t0 = time.time()
    with urllib.request.urlopen(req, timeout=120) as r:
        d = json.loads(r.read())
    print(f'dryrun: correlate_ok elapsed={time.time()-t0:.1f}s addresses_scored={d.get("addressesScored")}')
    return d


def main():
    db = os.path.expandvars(r'%LOCALAPPDATA%\WotBTreader\treader.db')
    if not os.path.exists(db):
        print('FAIL: host DB not found at', db)
        return 1
    rv_path = os.path.expandvars(r'%LOCALAPPDATA%\WotBTreader\rendezvous\web.json')
    rv = json.load(open(rv_path))
    base, cap = rv['baseUri'], rv['capability']

    ticks, xs, ys, zs = load_viewpoint(db)

    import datetime
    marker = datetime.datetime.fromisoformat(MARKER.replace('Z', '+00:00'))
    battle_start = marker + datetime.timedelta(seconds=ATTENDANCE)
    series = build_series(ticks, xs, ys, zs, battle_start.isoformat().replace('+00:00', 'Z'))

    # A) OLD (bug): anchor at the raw marker.
    print('\ndryrun: === ANCHOR_MARKER(OLD_BUG) ===')
    old = correlate(base, cap, series, MARKER)
    # B) FRESH18: anchor at battleStart.
    new_anchor = battle_start.isoformat().replace('+00:00', 'Z')
    print('\ndryrun: === ANCHOR_BATTLESTART(FRESH18) ===')
    new = correlate(base, cap, series, new_anchor)

    best = {'old': (0.0, None), 'new': (0.0, None)}
    for label, resp in (('old', old), ('new', new)):
        for r in resp.get('results', []):
            score = float(r.get('score', 0))
            if score > best[label][0]:
                best[label] = (score, r)
    print('\ndryrun: PREDICTION old_best=%.3f new_best=%.3f' % (best['old'][0], best['new'][0]))
    for label in ('old', 'new'):
        r = best[label][1]
        if r:
            print('dryrun: %s best addr=%s axis=%s score=%.3f shift=%s band=[%s..%s] edge=%s span=%.1f' % (
                label.upper(), r.get('address'), r.get('axis'), float(r.get('score', 0)),
                r.get('shiftSeconds'), r.get('shiftBandMinSeconds'), r.get('shiftBandMaxSeconds'),
                r.get('edgeAligned'), float(r.get('span', 0))))
    if best['new'][0] >= 0.9 and best['new'][0] > best['old'][0]:
        print('dryrun: VERDICT FRESH18_ANCHOR_CONFIRMED - corrected anchor scores the viewpoint series >=0.9')
        return 0
    print('dryrun: VERDICT UNEXPECTED - review scores above')
    return 2


if __name__ == '__main__':
    sys.exit(main())
