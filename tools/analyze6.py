# -*- coding: utf-8 -*-
# 解析刘海设计截图 v2
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

w, h, ch, rows = load_png(r"C:\Users\fengl\.workbuddy\clipboard-images\clipboard-2026-11-21T18-09-40-594Z-c7e5a339.png")
print(f"尺寸 {w}x{h}")

def px(x, y):
    o = x*ch
    return rows[y][o], rows[y][o+1], rows[y][o+2]

# 岛体边界与高度
mid_y = h // 2
cur = None; runs = []
for x in range(w):
    r, g, b = px(x, mid_y); lum = (r+g+b)/3
    if lum > 12:
        if cur is None: cur = x
    else:
        if cur is not None and x-cur > 10: runs.append((cur, x))
        cur = None
if cur: runs.append((cur, w))
print(f"非黑段(y={mid_y}): {runs[:6]}")

# 岛体高度（取中心 x 的黑区）
cx = runs[0][0] + 40 if runs else w//2
col = [y for y in range(h) if sum(px(cx, y))/3 < 25]
if col: print(f"岛体 y 范围 x={cx}: {min(col)}..{max(col)} 高{max(col)-min(col)}px")

# 彩色元素
colors = collections.Counter()
posmap = collections.defaultdict(list)
for yy in range(0, h, 2):
    for xx in range(0, w, 2):
        r, g, b = px(xx, yy)
        mx, mn = max(r,g,b), min(r,g,b)
        if mx-mn > 45 and mx > 90:
            key = "#%02X%02X%02X" % (r//24*24, g//24*24, b//24*24)
            colors[key] += 1
            posmap[key].append((xx, yy))
print("彩色TOP12:")
for c, n in colors.most_common(12):
    pts = posmap[c]
    xs = [p[0] for p in pts]; ys = [p[1] for p in pts]
    print(f"  {c} x{n:4d} 区域 x{min(xs)}..{max(xs)} y{min(ys)}..{max(ys)}")

# 横向彩色图标中心
print("横向彩色分布(每60px统计彩色数):")
for x0 in range(0, w-60, 60):
    cnt = sum(1 for yy in range(0,h,3) for xx in range(x0, x0+60, 3)
              if (lambda p: max(p)-min(p)>45 and max(p)>90)(px(xx,yy)))
    if cnt: print(f"  x{x0}: {'█'*min(30,cnt//2)} {cnt}")
