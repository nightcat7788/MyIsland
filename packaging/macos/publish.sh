#!/bin/bash
# Mac 版统一发布脚本：干净发布 + 产物完整性校验。
#
# 【为什么必须删干净再 publish】
# .NET 8 的增量 publish 会**先清空输出目录**再拷贝新产物，
# 结果是**第二次 publish 后 native dylib（libSkiaSharp 等）全部消失**，
# 打出来的 .app 一启动就崩（TypeInitializationException: libSkiaSharp 缺失）。
# 这个坑踩过两次，做法就是：每次 publish 前 rm -rf 输出目录，强制完整发布。
#
# 用法：bash packaging/macos/publish.sh [输出目录]
set -euo pipefail
cd "$(dirname "$0")/../.."

OUT="${1:-publish/osx-arm64}"
ROOT="$(pwd)"
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
DOTNET="$DOTNET_ROOT/dotnet"

echo "==> 清理输出目录（避免增量 publish 丢 dylib）"
rm -rf "$OUT"

echo "==> 完整 publish"
"$DOTNET" publish src/Island.Avalonia/Island.Avalonia.csproj \
    -c Release -r osx-arm64 --self-contained -p:PublishSingleFile=true \
    -o "$OUT" --nologo

echo "==> 校验产物"
fail=0
# native 依赖：Avalonia/Skia/HarfBuzz 等，缺任何一个 .app 都起不来
dylibs=$(ls "$OUT"/*.dylib 2>/dev/null | wc -l | tr -d ' ')
if [[ "$dylibs" -lt 5 ]]; then
    echo "[error] 只找到 $dylibs 个 .dylib（应至少 5 个）—— .app 会启动即崩" >&2
    fail=1
fi
# 原生工具：屏幕枚举 / DDC / 设备电量 / 应用图标 / 热键
for tool in ddc-helper screen-guardian island-screens island-devices island-hotkey app-icon; do
    if [[ ! -x "$OUT/$tool" ]]; then
        echo "[error] 缺少原生工具 $tool" >&2
        fail=1
    fi
done
if [[ ! -f "$OUT/Island.Avalonia" ]]; then
    echo "[error] 缺少主程序 Island.Avalonia" >&2
    fail=1
fi
if [[ "$fail" -ne 0 ]]; then
    echo "发布产物校验失败。" >&2
    exit 1
fi

echo "[ok] 发布产物完整：$dylibs 个 dylib / 6 个原生工具 / 主程序"
