#!/bin/bash
# 生成 MyIsland 应用图标（方案 A：胶囊岛 + 270° 仪表环 + 三条读数条）。
# 全矢量 CoreGraphics 绘制，每个尺寸独立渲染（不缩放），小尺寸专门加粗线条。
# 用法：bash tools/IconGen/build.sh   → 输出到 branding/
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
OUT="$HERE/../../branding"
WORK="$(mktemp -d)"
xcrun swiftc -O "$HERE/icongen.swift" -o "$WORK/icongen"
(cd "$WORK" && ./icongen)
iconutil -c icns "$WORK/island.iconset" -o "$OUT/island.icns"
rm -rf "$OUT/island.iconset" && cp -R "$WORK/island.iconset" "$OUT/island.iconset"
for s in 16 24 32 48 64 128 256 512 1024; do cp "$WORK/brand-$s.png" "$OUT/island-logo-$s.png"; done
python3 - "$WORK" "$OUT/island-logo.ico" <<'PY'
import struct, sys
work, out = sys.argv[1], sys.argv[2]
imgs = []
for sz in [16, 32, 48, 256]:
    d = open(f'{work}/brand-{sz}.png', 'rb').read()
    w = 0 if sz >= 256 else sz
    imgs.append((w, d))
hdr = struct.pack('<HHH', 0, 1, len(imgs)); offset = 6 + 16 * len(imgs)
entries = b''; body = b''
for w, d in imgs:
    entries += struct.pack('<BBBBHHII', w, w, 0, 0, 1, 32, len(d), offset)
    body += d; offset += len(d)
open(out, 'wb').write(hdr + entries + body)
PY
rm -rf "$WORK"
echo "已生成：branding/island.icns / island.iconset / island-logo-*.png / island-logo.ico"
