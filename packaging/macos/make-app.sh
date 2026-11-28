#!/bin/bash
# 把 publish/osx-arm64 产物组装成 MyIsland.app（带图标、可放 /Applications、菜单栏显示）
# 用法：bash packaging/macos/make-app.sh [publish目录] [输出目录]
set -euo pipefail
cd "$(dirname "$0")/../.."
ROOT="$(pwd)"
SRC="${1:-$ROOT/publish/osx-arm64}"
OUT="${2:-$SRC}"

if [[ ! -f "$SRC/Island.Avalonia" ]]; then
    echo "[error] 未找到主程序 $SRC/Island.Avalonia —— 请先执行 bash packaging/macos/publish.sh" >&2
    exit 1
fi

VERSION=$(grep -oE '<Version>[^<]+' "$ROOT/Directory.Build.props" | head -n1 | sed 's/<Version>//')
[[ -n "$VERSION" ]] || VERSION="1.1.9"

# 构建号自动递增（确认主程序存在后再写，避免失败构建污染状态）
BUILD_NUM_FILE="$ROOT/packaging/macos/.build-num"
BUILD_NUM=1
[[ -f "$BUILD_NUM_FILE" ]] && BUILD_NUM=$(<"$BUILD_NUM_FILE")
BUILD_NUM=$((BUILD_NUM + 1))
echo "$BUILD_NUM" > "$BUILD_NUM_FILE"
echo "==> 版本 $VERSION · 构建号 $BUILD_NUM"

APP="$OUT/MyIsland.app"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"

# 显示器与温度原生工具：强制从源码重新编译，保证协议一致
bash "$ROOT/tools/display-tools/build.sh" "$SRC"
echo "[ok] 原生辅助工具已从源码重新编译"

cp "$SRC"/Island.Avalonia "$APP/Contents/MacOS/"
shopt -s nullglob
_dylibs=("$SRC"/*.dylib)
shopt -u nullglob
if [ ${#_dylibs[@]} -eq 0 ]; then
    echo "[error] publish 目录里没有任何 .dylib —— 请先完整执行 dotnet publish" >&2
    exit 1
fi
cp "${_dylibs[@]}" "$APP/Contents/MacOS/"

TOOLS=(island-thermal island-screens ddc-helper screen-guardian app-icon island-devices island-hotkey)
for t in "${TOOLS[@]}"; do
    if [ -f "$SRC/$t" ]; then
        cp "$SRC/$t" "$APP/Contents/MacOS/"
    else
        echo "[error] 缺少原生工具 $t" >&2
        exit 1
    fi
done
chmod +x "$APP/Contents/MacOS/"*

cp "$ROOT/packaging/macos/Info.plist" "$APP/Contents/Info.plist"
sed -e "s/__BUILD__/$BUILD_NUM/" \
    -e "s|<key>CFBundleShortVersionString</key><string>[^<]*</string>|<key>CFBundleShortVersionString</key><string>$VERSION</string>|" \
    "$APP/Contents/Info.plist" > "$APP/Contents/Info.plist.tmp" && mv "$APP/Contents/Info.plist.tmp" "$APP/Contents/Info.plist"

cp "$ROOT/branding/island.icns" "$APP/Contents/Resources/island.icns"
cp "$ROOT/THIRD-PARTY-NOTICES.txt" "$APP/Contents/Resources/THIRD-PARTY-NOTICES.txt"
{
    cat "$ROOT/tools/display-tools/NOTICE.txt"
    printf "\n--- LunarCN License (MIT) ---\n"
    cat "$ROOT/tools/display-tools/LICENSE.LunarCN.txt"
    printf "\n--- m1ddc License (MIT) ---\n"
    cat "$ROOT/tools/display-tools/LICENSE.m1ddc.txt"
    printf "\n"
} > "$APP/Contents/Resources/NOTICE.txt"

codesign --force --deep --sign - "$APP"
codesign --verify --deep --strict "$APP"
echo "[ok] codesign 校验通过"

echo "已生成：$APP"
echo "安装：cp -R \"$APP\" /Applications/ && open -a MyIsland"
