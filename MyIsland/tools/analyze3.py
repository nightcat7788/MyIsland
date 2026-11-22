# -*- coding: utf-8 -*-
# 解析 BrimDeck 截图：结构化输出布局/卡片/配色
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

w, h, ch, rows = load_png(r"C:\Users\fengl\.workbuddy\clipboard-images\clipboard-2026-11-21T17-54-24-356Z-751f233b.png")
print(f"尺寸 {w}x{h}")

def px(x, y):
    o = x*ch
    return rows[y][o], rows[y][o+1], rows[y][o+2]

# 行密度（非黑）
print("行密度(中心 60% 区域):")
for y in range(0, h, 24):
    cnt = sum(1 for x in range(int(w*0.2), int(w*0.8), 16) if sum(px(x,y))/3 > 14)
    print(f"  y{y:4d}: {'█'*int(cnt/3)} {cnt}")

# 彩色聚类
colors = collections.Counter()
for y in range(0, h, 5):
    for x in range(0, w, 5):
        r, g, b = px(x, y)
        mx, mn = max(r,g,b), min(r,g,b)
        if mx-mn > 40 and mx > 90:
            colors["#%02X%02X%02X" % (r//24*24, g//24*24, b//24*24)] += 1
print("彩色TOP12:", colors.most_common(12))
