import json, re

d = json.load(open('.data/od-049-fresh-result.json'))

# Strong survivors from the correlate section
corr = d.get('correlate', {})
strong = d.get('strongSurvivors') or []
print('strongSurvivors key:', type(strong).__name__, strong if isinstance(strong, list) and len(strong) <= 3 else '(list len %d)' % len(strong) if isinstance(strong, list) else strong)

# results shape: find axis/address/score/shift fields
results = d.get('results', [])
print('results sample keys:', list(results[0].keys()) if results else 'n/a')
print('results:', len(results))

# Dump results with score >= 0.6 or non-edge, sorted by address
thr = 0.6
strong_rows = [r for r in results if r.get('score', 0) >= thr]
print('\n=== results with score >= %.1f (%d) ===' % (thr, len(strong_rows)))
for r in sorted(strong_rows, key=lambda r: (int(r.get('address','0x0'),16))):
    print('  %-12s axis=%-2s score=%.3f shift=%-6s band=%s sign=%s' % (
        r.get('address'), r.get('axis'), r.get('score', 0), r.get('shift'), r.get('band'), r.get('sign')))

# Group strong rows by axis and check geometric proximity between axes
by_axis = {}
for r in strong_rows:
    by_axis.setdefault(r.get('axis'), []).append(int(r.get('address','0x0'), 16))
print('\n=== strong by axis ===')
for ax, addrs in by_axis.items():
    print('  axis=%s n=%d addrs=%s' % (ax, len(addrs), [hex(a) for a in sorted(addrs)]))

# Cross-axis proximity: is any y/z address within 16 bytes of an x address?
if 'x' in by_axis and any(a in by_axis for a in ('y','z')):
    xs = sorted(by_axis['x'])
    print('\n=== cross-axis proximity (within 16 bytes) ===')
    for ax in ('y','z'):
        if ax not in by_axis: continue
        for a in sorted(by_axis[ax]):
            near = [x for x in xs if abs(x - a) <= 16]
            print('  axis=%s %s -> nearest x: %s' % (ax, hex(a), [hex(x) for x in near] or 'NONE'))
