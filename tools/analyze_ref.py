# -*- coding: utf-8 -*-
# 深度解析 compact-monitor（刘海系统读数）—— 我们的直接对标
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

def px(rows, ch, x, y):
    o = x*ch
    return rows[y][o], rows[y][o+1], rows[y][o+2]

# ---- compact-monitor.png：刘海系统读数 ----
w, h, ch, rows = load_png(r"C:\Users\fengl\WorkBuddy\2026-11-21-13-46-35\MyIsland\branding\ref\compact-monitor.png")
print(f"=== compact-monitor {w}x{h} ===")
def P(x, y): return px(rows, ch, x, y)

# 岛体边界（非壁纸：壁纸一般是浅色渐变）
mid = h//2
cur=None; runs=[]
for x in range(w):
    r,g,b = P(x,mid); lum=(r+g+b)/3
    if lum < 80:  # 岛体偏深
        if cur is None: cur=x
    else:
        if cur is not None and x-cur>15: runs.append((cur,x))
        cur=None
if cur: runs.append((cur,w))
island = max(runs, key=lambda t: t[1]-t[0]) if runs else (0,w)
print(f"岛体段: {runs[:4]} → 主岛 {island}")
x0, x1 = island
# 岛高
yc = []
for y in range(h):
    r,g,b = P((x0+x1)//2, y)
    if (r+g+b)/3 < 80: yc.append(y)
if yc: print(f"岛体 y: {min(yc)}..{max(yc)} 高{max(yc)-min(yc)}px")

# 彩色元素与位置（岛内）
colors = collections.Counter()
posmap = collections.defaultdict(list)
for y in range(min(yc), max(yc), 2):
    for x in range(x0, x1, 2):
        r, g, b = P(x, y)
        mx, mn = max(r,g,b), min(r,g,b)
        if mx-mn > 40 and mx > 90:
            key = "#%02X%02X%02X" % (r//24*24, g//24*24, b//24*24)
            colors[key] += 1
            posmap[key].append((x, y))
print("彩色TOP10:")
for c, n in colors.most_common(10):
    pts = posmap[c]
    xs = [p[0] for p in pts]; ys = [p[1] for p in pts]
    print(f"  {c} x{n:4d} x{min(xs)}..{max(xs)} y{min(ys)}..{max(ys)}")

# 白色数值簇横向分布
wcols = collections.Counter()
for y in range(min(yc), max(yc), 2):
    for x in range(x0, x1, 2):
        r, g, b = P(x, y)
        if r>190 and g>190 and b>190:
            wcols[x//40*40] += 1
print("白色簇:", sorted(wcols.items()))

# 核心柱检测：竖直短条的列分布
print("\n竖直元素扫描（连续同色高度>8px 的列数）:")
colbars = []
for x in range(x0+10, x1-10):
    cnt = 0
    for y in range(min(yc), max(yc)-4):
        r1,g1,b1 = P(x,y); r2,g2,b2 = P(x,y+3)
        if (r1+g1+b1)/3 < 90 and (r2+g2+b2)/3 < 90 and (max(r1,g1,b1)-min(r1,g1,b1) > 25 or (r1+g1+b1)/3 > 120):
            cnt += 1
    colbars.append((x, cnt))
dense = [x for x,c in colbars if c > 6]
if dense:
    groups=[]; cur=dense[0]
    for i in range(1,len(dense)):
        if dense[i]-dense[i-1] <= 3: continue
        groups.append((cur, dense[i-1])); cur=dense[i]
    groups.append((cur, dense[-1]))
    print("竖条组:", groups[:12])
