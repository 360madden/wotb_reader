import json

d = json.load(open('.data/od-049-fresh-result.json'))
families = d.get('families', [])

# Find the family at base 0x2387CA48 (the one the dry-run armed)
target = 0x2387CA48
print('=== family at 0x%X ===' % target)
found = None
for f in families:
    if int(f['baseAddress'], 16) == target:
        found = f
        break
if found:
    print('axes=%s complete=%s span=%d' % (found['axesCovered'], found['complete'], found['spanBytes']))
    for m in found['members']:
        print('  %-12s off=%-3d axis=%-2s score=%.3f shift=%s edge=%s' % (
            m['address'], m['offsetBytes'], m['axis'], m['score'],
            m.get('shiftSeconds'), m.get('edgeAligned')))
else:
    print('NOT FOUND')

# Which families are usable (>=2 members, >=1 non-edge)? Show top usable by score.
def usable(f):
    ms = f.get('members') or []
    if len(ms) < 2: return False
    return any(not (m.get('edgeAligned') is True) for m in ms)

def score(f):
    return sum(float(m.get('score') or 0) for m in (f.get('members') or []))

usable_list = [f for f in families if usable(f)]
usable_list.sort(key=score, reverse=True)
print('\n=== usable families by summed score (top 8) ===')
for f in usable_list[:8]:
    ms = f['members']
    print('  base=%-12s axes=%s n=%d sum=%.3f edges=%s' % (
        f['baseAddress'], f['axesCovered'], len(ms), score(f),
        [m.get('edgeAligned') for m in ms]))
print('\nusable count:', len(usable_list), 'of', len(families))

# And what about the real x/z pair from entity 2549405?
print('\n=== family containing 0x29D957DC (real x/z pair) ===')
for f in families:
    if any(m['address'].lower() == '0x29d957dc' for m in (f.get('members') or [])):
        ms = f['members']
        print('  base=%s axes=%s n=%d sum=%.3f' % (f['baseAddress'], f['axesCovered'], len(ms), score(f)))
        for m in ms:
            print('    %-12s off=%d axis=%s score=%.3f edge=%s' % (
                m['address'], m['offsetBytes'], m['axis'], m['score'], m.get('edgeAligned')))
