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

# report session id (read from the report file itself)
report = json.load(open('.data/od-049-fresh-result.json'))
rid = report.get('battleSessionId')
print('report session:', rid)
print('report anchor:', report.get('replayStartWallTimeUtc'))

for sid in [rid, '019fd306-ddbe-7847-abc6-11c4b19b41a8']:
    try:
        t = api('/api/v1/game/discover/trajectory/' + sid)
        print('OK trajectory for', sid, 'dur_ticks:', t.get('durationTicks'), 'entities:', len(t.get('entities', [])))
        for e in t.get('entities', []):
            xs = [s['x'] for s in e['samples']]
            ys = [s['y'] for s in e['samples']]
            zs = [s['z'] for s in e['samples']]
            if not xs:
                continue
            span = lambda v: max(v) - min(v)
            print('  entity', e['entityId'], 'vp=', e.get('isViewpoint'), 'n=', len(xs),
                  't0=', e['samples'][0]['replayTimeTicks'], 'tN=', e['samples'][-1]['replayTimeTicks'],
                  'xspan=%.1f yspan=%.1f zspan=%.1f' % (span(xs), span(ys), span(zs)))
    except Exception as ex:
        print('ERR', sid, ex)
