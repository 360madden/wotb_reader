import os, struct, zlib
from collections import Counter

p = os.path.join(os.environ['TEMP'], 'wotb-screen-peek.png')
data = open(p, 'rb').read()
pos = 8
w = h = None
idat = b''
while pos < len(data):
    length = struct.unpack('>I', data[pos:pos+4])[0]
    ctype = data[pos+4:pos+8]
    chunk = data[pos+8:pos+8+length]
    if ctype == b'IHDR':
        w, h, bitdepth, colortype = struct.unpack('>IIBB', chunk[:10])
    elif ctype == b'IDAT':
        idat += chunk
    elif ctype == b'IEND':
        break
    pos += 12 + length
raw = zlib.decompress(idat)
bpp = 4
stride = w * bpp
def pixel(x, y):
    row = raw[y * (stride + 1) + 1: y * (stride + 1) + 1 + stride]
    o = x * bpp
    return row[o], row[o+1], row[o+2]

def region_stats(name, x0, y0, x1, y1):
    orange = dark = bright = 0
    total = 0
    for y in range(y0, y1, 8):
        for x in range(x0, x1, 8):
            r, g, b = pixel(x, y)
            total += 1
            if r > 150 and g > 60 and b < 100:
                orange += 1
            elif (r + g + b) / 3 > 200:
                bright += 1
            elif (r + g + b) / 3 < 50:
                dark += 1
    print(f'{name:28s} orange={100*orange/total:5.1f}% bright={100*bright/total:5.1f}% dark={100*dark/total:5.1f}%')

print('=== REGION ANALYSIS (1920x1080) ===')
region_stats('top-center (header)', 700, 60, 1220, 160)
region_stats('top-right (menu)', 1500, 60, 1900, 160)
region_stats('center', 600, 400, 1300, 700)
region_stats('bottom-center', 600, 900, 1300, 1050)
region_stats('bottom-right', 1400, 900, 1900, 1050)
region_stats('bottom-left', 100, 900, 500, 1050)
region_stats('left-column', 60, 300, 300, 800)
region_stats('right-column', 1620, 300, 1880, 800)

# Orange pixel bounding box (where is the orange content?)
minx, miny, maxx, maxy = w, h, 0, 0
orange_n = 0
for y in range(0, h, 4):
    for x in range(0, w, 4):
        r, g, b = pixel(x, y)
        if r > 150 and g > 60 and b < 100:
            orange_n += 1
            if x < minx: minx = x
            if x > maxx: maxx = x
            if y < miny: miny = y
            if y > maxy: maxy = y
print(f'\norange pixels sampled: {orange_n}')
if orange_n:
    print(f'orange bbox: x=[{minx},{maxx}] y=[{miny},{maxy}]')
