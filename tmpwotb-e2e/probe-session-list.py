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

page = api('/api/v1/sessions?limit=50')
items = page.get('items', [])
print('total items:', len(items))
for it in items:
    s = it.get('session')
    if s:
        print(s.get('battleSessionId'), s.get('durationTicks'), '|', it.get('decodeRunId', ''))
