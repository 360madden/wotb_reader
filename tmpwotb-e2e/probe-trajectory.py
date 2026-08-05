import json
import os
import glob
import urllib.request

rvdir = os.path.join(os.environ.get('LOCALAPPDATA', ''), 'WotBTreader', 'rendezvous')
files = sorted(glob.glob(os.path.join(rvdir, '*')), key=os.path.getmtime, reverse=True)
rv = json.load(open(files[0]))
base = rv.get('baseUri')
cap = rv.get('capability')

def api(path):
    req = urllib.request.Request(base + path, headers={'X-WotBTreader-Capability': str(cap)})
    with urllib.request.urlopen(req, timeout=30) as r:
        return json.load(r)

# The FRESH5 report session
sid = '019fd306-ddc4-7bdf-a9d9-4c4126d57c01'
try:
    t = api('/api/v1/game/discover/trajectory/' + sid)
    print('duration_ticks:', t.get('durationTicks'))
    print('entities:', len(t.get('entities', [])))
    for e in t.get('entities', []):
        xs = [s['x'] for s in e['samples']]
        ys = [s['y'] for s in e['samples']]
        zs = [s['z'] for s in e['samples']]
        n = len(xs)
        if n == 0:
            continue
        span = lambda v: max(v) - min(v)
        print('entity', e['entityId'], 'viewpoint=', e.get('isViewpoint'),
              'samples=', n, 'tick_first=', e['samples'][0]['replayTimeTicks'],
              'tick_last=', e['samples'][-1]['replayTimeTicks'],
              'xspan=%.1f yspan=%.1f zspan=%.1f' % (span(xs), span(ys), span(zs)))
except Exception as ex:
    print('trajectory err:', ex)
