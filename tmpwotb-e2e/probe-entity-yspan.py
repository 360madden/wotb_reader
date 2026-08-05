import json, os, sys, urllib.request

# Read the FRESH6 report to get the strong survivor entity ids
d = json.load(open('.data/od-049-fresh-result.json'))
strong = [r for r in d.get('results', []) if r.get('score', 0) >= 0.6]
entities = {}
for r in strong:
    entities.setdefault(r.get('entityId'), []).append(r)

print('=== strong survivor entity distribution ===')
for eid, rows in sorted(entities.items(), key=lambda kv: -len(kv[1])):
    print('  entity=%s participant=%s:' % (eid, rows[0].get('participantId')))
    for r in rows:
        print('    %-12s axis=%s score=%.2f shift=%s edge=%s samples=%d span=%s' % (
            r['address'], r['axis'], r.get('score', 0), r.get('shiftSeconds'),
            r.get('edgeAligned'), r.get('totalSamples'), r.get('span')))

# Session id + replay start
sid = d.get('battleSessionId')
rsw = d.get('replayStartWallTimeUtc')
print('\nsession:', sid)
print('replayStartWallTimeUtc:', rsw)

# Hit the host for ground truth per-entity movement
base = 'http://127.0.0.1:9182'
try:
    req = urllib.request.urlopen(base + '/discover/trajectory/' + sid + '?maxObservations=1200&maxEntities=8', timeout=15)
    gt = json.load(req)
    print('\n=== ground truth (first %d entities) ===' % len(gt))
    for ent in gt:
        name = ent.get('name') or ent.get('entityId')
        xs = ent.get('x') or []
        ys = ent.get('y') or []
        zs = ent.get('z') or []
        def span(vals):
            vs = [v for v in vals if v is not None]
            return (max(vs) - min(vs)) if len(vs) >= 2 else 0.0
        print('  %-24s n=%3d xspan=%7.1f yspan=%6.1f zspan=%7.1f firstTick=%s' % (
            name, len(xs), span(xs), span(ys), span(zs), ent.get('firstTick')))
except Exception as e:
    print('GT fetch failed:', e)
