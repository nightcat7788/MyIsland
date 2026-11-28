#!/bin/bash
# Mac 版统一发布脚本：干净发布 + 产物完整性校验。
# 用法：bash packaging/macos/publish.sh [输出目录]
set -euo pipefail
cd "$(dirname "$0")/../.."

OUT="${1:-publish/osx-arm64}"
ROOT="$(pwd)"
if [[ -n "${DOTNET_ROOT:-}" && -x "$DOTNET_ROOT/dotnet" ]]; then
    DOTNET="$DOTNET_ROOT/dotnet"
elif command -v dotnet >/dev/null 2>&1; then
    DOTNET="$(command -v dotnet)"
else
    DOTNET="$HOME/.dotnet/dotnet"
fi

echo "==> 清理输出目录（避免增量 publish 丢 dylib）"
rm -rf "$OUT"

echo "==> 完整 publish"
"$DOTNET" publish src/Island.Avalonia/Island.Avalonia.csproj \
    -c Release -r osx-arm64 --self-contained -p:PublishSingleFile=true \
    -o "$OUT" --nologo

echo "==> 校验产物"
fail=0
dylibs=$(ls "$OUT"/*.dylib 2>/dev/null | wc -l | tr -d ' ')
if [[ "$dylibs" -lt 5 ]]; then
    echo "[error] 只找到 $dylibs 个 .dylib（应至少 5 个）—— .app 会启动即崩" >&2
    fail=1
fi
for tool in ddc-helper screen-guardian island-screens island-devices island-hotkey app-icon island-thermal; do
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

echo "[ok] 发布产物完整：$dylibs 个 dylib / 7 个原生工具 / 主程序"
