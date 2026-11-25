# -*- coding: utf-8 -*-
# 分析用户当前实际显示（确认是哪个视图）
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

w, h, ch, rows = load_png(r"C:\Users\fengl\.workbuddy\clipboard-images\clipboard-2026-11-21T18-39-04-460Z-a1f6db89.png")
print(f"尺寸 {w}x{h}")

def px(x, y):
    o = x*ch
    return rows[y][o], rows[y][o+1], rows[y][o+2]

# 白色文字位置（判断内容）
wmap = collections.Counter()
for yy in range(0, h, 2):
    for xx in range(0, w, 2):
        r, g, b = px(xx, yy)
        if r>180 and g>180 and b>180:
            wmap[(xx//60*60, yy//30*30)] += 1
print("白色文字簇:")
for (x,y),n in sorted(wmap.items()):
    print(f"  x{x} y{y}: {n}")
# emoji 彩色（天气图标特征）
colors = collections.Counter()
for yy in range(0, h, 2):
    for xx in range(0, w, 2):
        r, g, b = px(xx, yy)
        mx, mn = max(r,g,b), min(r,g,b)
        if mx-mn > 40 and mx > 90:
            colors["#%02X%02X%02X" % (r//24*24, g//24*24, b//24*24)] += 1
print("彩色TOP6:", colors.most_common(6))
