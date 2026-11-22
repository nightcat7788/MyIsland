# -*- coding: utf-8 -*-
# 解析刘海截图：横向元素序列 + 高度轮廓
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

w, h, ch, rows = load_png(r"C:\Users\fengl\.workbuddy\clipboard-images\clipboard-2026-11-21T18-03-06-435Z-0fb14ca4.png")
print(f"尺寸 {w}x{h}")

def px(x, y):
    o = x*ch
    return rows[y][o], rows[y][o+1], rows[y][o+2]

# 岛体边界：非黑区
y = h // 2
cur = None; runs = []
for x in range(w):
    r, g, b = px(x, y); lum = (r+g+b)/3
    if lum > 12:
        if cur is None: cur = x
    else:
        if cur is not None and x-cur > 10: runs.append((cur, x))
        cur = None
if cur: runs.append((cur, w))
print(f"非黑段(y={y}): {runs}")

# 彩色元素定位（横向序列）
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
print("彩色TOP10:")
for c, n in colors.most_common(10):
    pts = posmap[c]
    xs = [p[0] for p in pts]; ys = [p[1] for p in pts]
    print(f"  {c} x{n:4d}  区域 x{min(xs)}..{max(xs)} y{min(ys)}..{max(ys)}")

# 白色元素位置（数值）
white_xs = []
for yy in range(0, h, 2):
    for xx in range(0, w, 2):
        r, g, b = px(xx, yy)
        if r > 200 and g > 200 and b > 200:
            white_xs.append(xx)
print(f"白色像素 x范围: {min(white_xs) if white_xs else '-'}..{max(white_xs) if white_xs else '-'} 分布密度:", 
      collections.Counter([x//80*80 for x in white_xs]).most_common(8))
