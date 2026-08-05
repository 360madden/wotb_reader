import glob, os, re, time
logs = sorted(glob.glob(os.path.join(os.environ['LOCALAPPDATA'], 'wotblitz', 'DAVAProject', 'blitz-logs_*.txt')), key=os.path.getmtime, reverse=True)
log = logs[0]
d = open(log, 'r', encoding='utf-8', errors='replace').read()
print('log:', os.path.basename(log))
print('size:', len(d), 'mtime:', time.strftime('%H:%M:%S', time.localtime(os.path.getmtime(log))))
lines = d.split('\n')
hits = [ln for ln in lines if re.search(r'Start replay event|LoadGameScene|onLeaveWorld.*isPlayer: 1|become hidden|OnBackground', ln)]
print('markers:', len(hits))
for ln in hits[-8:]:
    print('  ', ln[:100])
