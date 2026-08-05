import os, struct, zlib

p = os.path.join(os.environ['TEMP'], 'wotb-screen-peek.png')
data = open(p, 'rb').read()
assert data[:8] == b'\x89PNG\r\n\x1a\n', 'not a PNG'

pos = 8
w = h = None
idat = b''
while pos < len(data):
    length = struct.unpack('>I', data[pos:pos+4])[0]
    ctype = data[pos+4:pos+8]
    chunk = data[pos+8:pos+8+length]
    if ctype == b'IHDR':
        w, h, bitdepth, colortype = struct.unpack('>IIBB', chunk[:10])
        print('size:', w, 'x', h, 'bitdepth:', bitdepth, 'colortype:', colortype)
    elif ctype == b'IDAT':
        idat += chunk
    elif ctype == b'IEND':
        break
    pos += 12 + length

raw = zlib.decompress(idat)
# colortype 2 = RGB, 6 = RGBA
bpp = 3 if colortype == 2 else 4
stride = w * bpp

def pixel(x, y):
    # account for filter byte per row
    row = raw[y * (stride + 1) + 1: y * (stride + 1) + 1 + stride]
    o = x * bpp
    return row[o], row[o+1], row[o+2]

lum = {'dark': 0, 'mid': 0, 'bright': 0, 'orange': 0}
for y in range(0, h, 20):
    for x in range(0, w, 20):
        r, g, b = pixel(x, y)
        l = (r + g + b) / 3
        if r > 150 and g > 60 and b < 100:
            lum['orange'] += 1
        elif l < 60:
            lum['dark'] += 1
        elif l < 180:
            lum['mid'] += 1
        else:
            lum['bright'] += 1
total = sum(lum.values())
print('luminance buckets:', {k: round(100 * v / total, 1) for k, v in lum.items()})

def row_stats(y):
    vals = []
    for x in range(0, w, 30):
        r, g, b = pixel(x, y)
        vals.append((r + g + b) / 3)
    return (round(min(vals)), round(max(vals)), round(sum(vals) / len(vals)))

print('row h/2:', row_stats(h // 2), 'row 3h/4:', row_stats(3 * h // 4), 'row h-40:', row_stats(h - 40))
print('top row:', row_stats(40))

from collections import Counter
c = Counter()
for y in range(max(0, h // 2 - 100), min(h, h // 2 + 100), 10):
    for x in range(0, w, 25):
        r, g, b = pixel(x, y)
        c[(r // 64, g // 64, b // 64)] += 1
print('center band color buckets (top 6):', c.most_common(6))
