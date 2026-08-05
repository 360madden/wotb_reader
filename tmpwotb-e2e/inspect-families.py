import json
import collections

d = json.load(open('.data/od-049-fresh-result.json'))
print('verdict:', d.get('verdict'))
print('results:', len(d.get('results', [])))
print('resultsByAxis:', d.get('correlate', {}).get('resultsByAxis'))
print('strongByAxis:', d.get('correlate', {}).get('strongByAxis'))
print('familyRefinement:', json.dumps(d.get('familyRefinement')))
print('autoTrace report written:', d.get('completedAtUtc'))

fams = d.get('families', [])
print()
print('families:', len(fams))
# Distinct entity/participant coverage
ents = collections.Counter()
axis_sets = collections.Counter()
complete = 0
for f in fams:
    axes = tuple(sorted(f.get('axesCovered', [])))
    axis_sets[axes] += 1
    if f.get('complete'):
        complete += 1
    for m in f.get('members', []):
        ents[m.get('entityId')] += 1
print('complete families:', complete)
print('axis-set histogram:', dict(axis_sets))
print('member entity histogram (top):', ents.most_common(5))
print()
print('=== top 3 families ===')
for f in fams[:3]:
    print('base=', f.get('baseAddress'), 'span=', f.get('spanBytes'),
          'axes=', f.get('axesCovered'), 'complete=', f.get('complete'))
    for m in f.get('members', []):
        print('   ', m.get('address'), 'off=', m.get('offsetBytes'),
              'axis=', m.get('axis'), 'sign=', m.get('sign'),
              'score=', round(m.get('score', 0), 3),
              'shift=', m.get('shiftSeconds'), 'edge=', m.get('edgeAligned'))
