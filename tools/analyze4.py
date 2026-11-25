# -*- coding: utf-8 -*-
# 解析新截图：布局结构 + 模块识别
import struct, zlib, collections

def load_png(path):
    f = open(path, "rb"); data = f.read(); f.close()
    pos = 8; idat = b""
    while pos < len(data):
        ln = struct.unpack(">I", data[pos:pos+4])[0]
        ct = data[pos+4:pos+8]; chunk = data[pos+8:pos+8+ln]
        if ct == b"IHDR":
            w, h, bd, cct = struct.unpack(">IIBB", chunk[:10])
        elif ct == b"IDAT":
            idat += chunk
        elif ct == b"IEND":
            break
        pos += 12 + ln
    raw = zlib.decompress(idat)
    ch = {0:1, 2:3, 6:4}[cct]; stride = w * ch
    def paeth(a, b, c):
        p = a+b-c; pa,pb,pc = abs(p-a),abs(p-b),abs(p-c)
        return a if pa<=pb and pa<=pc else (b if pb<=pc else c)
    prev = bytearray(stride); rows = []; pos = 0
    for y in range(h):
        ft = raw[pos]; pos += 1
        line = bytearray(raw[pos:pos+stride]); pos += stride
        if ft == 1:
            for i in range(ch, stride): line[i] = (line[i]+line[i-ch]) & 255
        elif ft == 2:
            for i in range(stride): line[i] = (line[i]+prev[i]) & 255
        elif ft == 3:
            for i in range(stride):
                a = line[i-ch] if i >= ch else 0
                line[i] = (line[i]+(a+prev[i])//2) & 255
        elif ft == 4:
            for i in range(stride):
                a = line[i-ch] if i >= ch else 0
                line[i] = (line[i]+paeth(a, prev[i], prev[i-ch] if i>=ch else 0)) & 255
        prev = line; rows.append(line)
    return w, h, ch, rows

w, h, ch, rows = load_png(r"C:\Users\fengl\.workbuddy\clipboard-images\clipboard-2026-11-21T17-57-17-390Z-a15d112a.png")
print(f"尺寸 {w}x{h}")

def px(x, y):
    o = x*ch
    return rows[y][o], rows[y][o+1], rows[y][o+2]

# 网格主色 10x6
print("网格主色:")
for gy in range(6):
    row = []
    for gx in range(10):
        x0, x1 = int(w*gx/10), int(w*(gx+1)/10)
        y0, y1 = int(h*gy/6), int(h*(gy+1)/6)
        rs = gs = bs = n = 0
        for yy in range(y0, y1, 5):
            for xx in range(x0, x1, 7):
                r, g, b = px(xx, yy)
                rs += r; gs += g; bs += b; n += 1
        r, g, b = rs//n, gs//n, bs//n
        if r > 200 and g > 200 and b > 200: t = "白"
        elif r < 30 and g < 30 and b < 30: t = "黑"
        elif abs(r-g) < 22 and abs(g-b) < 22: t = f"灰{r:02X}"
        elif b > r and b > g: t = f"蓝#{r:02X}{g:02X}{b:02X}"
        elif g > r and g > b: t = f"绿青#{r:02X}{g:02X}{b:02X}"
        elif r > 180 and g > 120 and b < 140: t = f"橙#{r:02X}{g:02X}{b:02X}"
        elif r > g and r > b: t = f"红粉#{r:02X}{g:02X}{b:02X}"
        else: t = f"#{r:02X}{g:02X}{b:02X}"
        row.append(t)
    print(f"  y{gy}: " + " | ".join(row))

# 彩色TOP
colors = collections.Counter()
for y in range(0, h, 5):
    for x in range(0, w, 5):
        r, g, b = px(x, y)
        mx, mn = max(r,g,b), min(r,g,b)
        if mx-mn > 40 and mx > 90:
            colors["#%02X%02X%02X" % (r//24*24, g//24*24, b//24*24)] += 1
print("彩色TOP12:", colors.most_common(12))

# 卡片底色带
rowdensity = []
for y in range(0, h, 4):
    cnt = sum(1 for x in range(0, w, 16) if 16 <= sum(px(x,y))/3 <= 85)
    rowdensity.append((y, cnt))
bands = []
inband = None
for y, c in rowdensity:
    if c > 8 and inband is None: inband = y
    elif c <= 8 and inband is not None:
        if y - inband > 18: bands.append((inband, y))
        inband = None
if inband is not None: bands.append((inband, h))
print("卡片行带:", bands)
