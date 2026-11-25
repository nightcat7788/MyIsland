# -*- coding: utf-8 -*-
# 分析用户期望的监控内容截图
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

w, h, ch, rows = load_png(r"C:\Users\fengl\.workbuddy\clipboard-images\clipboard-2026-11-21T18-41-56-756Z-f9cb6806.png")
print(f"尺寸 {w}x{h}")

def px(x, y):
    o = x*ch
    return rows[y][o], rows[y][o+1], rows[y][o+2]

# 白色文字簇（内容定位）
wmap = collections.Counter()
for yy in range(0, h, 2):
    for xx in range(0, w, 2):
        r, g, b = px(xx, yy)
        if r>170 and g>170 and b>170:
            wmap[(xx//80*80, yy//40*40)] += 1
print("白色内容簇:")
for (x,y),n in sorted(wmap.items(), key=lambda kv:-kv[1])[:16]:
    print(f"  x{x:4d} y{y:3d}: {n}")

# 彩色元素
colors = collections.Counter()
posmap = collections.defaultdict(list)
for yy in range(0, h, 2):
    for xx in range(0, w, 2):
        r, g, b = px(xx, yy)
        mx, mn = max(r,g,b), min(r,g,b)
        if mx-mn > 40 and mx > 90:
            key = "#%02X%02X%02X" % (r//24*24, g//24*24, b//24*24)
            colors[key] += 1
            posmap[key].append((xx, yy))
print("彩色TOP10:")
for c, n in colors.most_common(10):
    pts = posmap[c]
    xs = [p[0] for p in pts]; ys = [p[1] for p in pts]
    print(f"  {c} x{n:4d} x{min(xs)}..{max(xs)} y{min(ys)}..{max(ys)}")
