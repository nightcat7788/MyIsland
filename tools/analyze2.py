# -*- coding: utf-8 -*-
# 分析两张截图的 UI 布局与配色，输出结构化对比
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

def analyze(path, label):
    w, h, ch, rows = load_png(path)
    print(f"\n===== {label} ({w}x{h}) =====")
    # 找 UI 主体（非纯壁纸）：统计亮度分布
    lum_hist = collections.Counter()
    for y in range(0, h, 6):
        for x in range(0, w, 6):
            r, g, b = px(rows, ch, x, y)
            lum_hist[int((r+g+b)/3)//32] += 1
    print("亮度分布(0-7):", dict(lum_hist))
    # 彩色像素聚类
    colors = collections.Counter()
    for y in range(0, h, 6):
        for x in range(0, w, 6):
            r, g, b = px(rows, ch, x, y)
            mx, mn = max(r,g,b), min(r,g,b)
            if mx - mn > 45 and mx > 90:
                colors["#%02X%02X%02X" % (r//24*24, g//24*24, b//24*24)] += 1
    print("彩色TOP:", colors.most_common(10))
    # 卡片检测：亮度 20-75 的区域（卡片底色）行分布
    rowdensity = []
    for y in range(0, h, 4):
        cnt = sum(1 for x in range(0, w, 16)
                  if 18 <= sum(px(rows, ch, x, y))/3 <= 80)
        rowdensity.append((y, cnt))
    bands = []
    inband = None
    for y, c in rowdensity:
        if c > 10 and inband is None: inband = y
        elif c <= 10 and inband is not None:
            if y - inband > 20: bands.append((inband, y))
            inband = None
    if inband is not None: bands.append((inband, h))
    print("卡片行带:", bands)
    # 每个带内的竖直分栏
    for (y0, y1) in bands[:4]:
        gaps = []; ing = None
        for x in range(0, w, 3):
            lum = sum(sum(px(rows, ch, x, yy))/3 for yy in range(y0+6, y1-6, max(6,(y1-y0)//8))) / max(1, len(range(y0+6, y1-6, max(6,(y1-y0)//8))))
            if lum < 16:
                if ing is None: ing = x
            else:
                if ing is not None and x-ing >= 10: gaps.append((ing, x))
                ing = None
        # 卡片区段 = 带范围减去缝
        segs = []
        prev = 0
        for (a, b) in gaps:
            if a - prev > 60: segs.append((prev, a))
            prev = b
        if w - prev > 60: segs.append((prev, w))
        print(f"  带 y{y0}..{y1} 卡片列: {segs[:8]}")
    return w, h, ch, rows

w1 = analyze(r"C:\Users\fengl\.workbuddy\clipboard-images\clipboard-2026-11-21T17-36-06-825Z-90a91ca5.png", "截图1")
w2 = analyze(r"C:\Users\fengl\.workbuddy\clipboard-images\clipboard-2026-11-21T17-36-06-826Z-a89ba033.png", "截图2")
