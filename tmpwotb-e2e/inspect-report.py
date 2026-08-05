import json

d = json.load(open('.data/od-049-fresh-result.json'))
print('TOP KEYS:', list(d.keys()))
print()
for k in ('verdict', 'anchor', 'completedAtUtc', 'replayStartWallTimeUtc'):
    print(k, '=', d.get(k))
print()
res = d.get('results', [])
print('results:', len(res))
if res:
    print('first result keys:', list(res[0].keys()))
    print('first result:', json.dumps(res[0], indent=1)[:600])
print()
fam = d.get('families', [])
print('families:', len(fam))
if fam:
    print('first family:', json.dumps(fam[0], indent=1)[:600])
print()
# survivor addresses
surs = [r for r in res if r.get('survivor') or r.get('verdict') == 'strong']
print('survivor-tagged results:', len(surs))
# distribution of result addresses
addrs = [r.get('address') for r in res]
print('sample addresses:', addrs[:8])
print('addr len histogram:', {n: sum(1 for a in addrs if a and len(a) == n) for n in sorted(set(len(a) for a in addrs if a))})
