import json
import os
import glob

# Query the host's trajectory endpoint for the FRESH5 session to verify
# per-axis movement (y stationary? z moving?) and entity speeds.
base = None
rvdir = os.path.join(os.environ.get('LOCALAPPDATA', ''), 'WotBTreader', 'rendezvous')
files = sorted(glob.glob(os.path.join(rvdir, '*')), key=os.path.getmtime, reverse=True)
if not files:
    print('NO_RENDEZVOUS')
    raise SystemExit
rv = json.load(open(files[0]))
base = rv.get('baseUri')
cap = rv.get('capability')
print('rendezvous:', base)

import urllib.request

def api(path):
    req = urllib.request.Request(base + path, headers={'X-WotBTreader-Capability': str(cap)})
    with urllib.request.urlopen(req, timeout=30) as r:
        return json.load(r)

try:
    st = api('/api/v1/game/state')
    print('gate:', st.get('verificationState'))
except Exception as e:
    print('state err:', e)

# newest session
try:
    page = api('/api/v1/sessions?limit=3')
    for it in page.get('items', [])[:3]:
        s = it.get('session')
        if s:
            print('session:', s.get('battleSessionId'), 'dur_ticks:', s.get('durationTicks'))
except Exception as e:
    print('sessions err:', e)
