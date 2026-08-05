import json
import collections

d = json.load(open('.data/od-049-fresh-result.json'))
res = d.get('results', [])

# Axis distribution
axes = collections.Counter(r.get('axis') for r in res)
print('axis distribution:', dict(axes))
ents = collections.Counter(r.get('entityId') for r in res)
print('entity count:', len(ents), 'top entities:', ents.most_common(5))
shifts = collections.Counter(r.get('shiftSeconds') for r in res)
print('shift distribution:', dict(shifts))
scores = [r.get('score') for r in res]
print('score min/max:', min(scores), max(scores))
print('matchCount min/max:', min(r.get('matchCount') for r in res), max(r.get('matchCount') for r in res))

# Same-entity address proximity check: are x/y/z siblings within a window of the base?
print()
print('=== same-entity grouped addresses ===')
by_ent = collections.defaultdict(list)
for r in res:
    by_ent[r.get('entityId')].append((r.get('address'), r.get('axis'), r.get('offsetBytes', None)))
for eid, items in sorted(by_ent.items(), key=lambda kv: -len(kv[1]))[:5]:
    print('entity', eid, 'count', len(items))
    for it in sorted(items):
        print('   ', it)
print()
print('=== strongSurvivors ===')
ss = d.get('strongSurvivors')
print(type(ss), len(ss) if ss else 0)
if ss:
    print(json.dumps(ss[0], indent=1)[:500])
    # axis distribution of survivors
    if isinstance(ss[0], dict):
        sax = collections.Counter(s.get('axis') for s in ss)
        print('survivor axes:', dict(sax))
        sa = [s.get('address') for s in ss]
        print('survivor addr sample:', sa[:6])
print()
print('=== familyRefinement ===')
print(json.dumps(d.get('familyRefinement'), indent=1)[:800])
print()
print('=== suspectEdgeAligned ===')
print(json.dumps(d.get('suspectEdgeAligned'), indent=1)[:400])
