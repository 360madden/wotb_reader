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

# cluster orange pixels into 40x40 cells
cells = Counter()
for y in range(0, h, 4):
    for x in range(0, w, 4):
        r, g, b = pixel(x, y)
        if r > 150 and g > 60 and b < 100:
            cells[(x // 80, y // 80)] += 1
print('orange cells (col,row):count, top 20:')
for (cx, cy), n in cells.most_common(20):
    print(f'  cell=({cx},{cy}) px={n*4}x{4} approx_x={cx*80}-{cx*80+80} y={cy*80}-{cy*80+80}')
