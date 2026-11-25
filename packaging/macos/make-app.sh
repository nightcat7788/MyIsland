#!/bin/bash
# 把 publish/osx-arm64 产物组装成 MyIsland.app（带图标、可放 /Applications、菜单栏显示）
# 显示器控制工具（ddc-helper / screen-guardian / island-screens）由 dotnet publish 从源码编译，
# 与主程序同目录，不再依赖外部 LunarCN.app。
# 用法：bash packaging/macos/make-app.sh [publish目录] [输出目录]
set -euo pipefail
# 构建号自动递增（原来 plist 写死 19，45 轮修订后仍显示 19）
BUILD_NUM_FILE="$(dirname "$0")/.build-num"
BUILD_NUM=1
[[ -f "$BUILD_NUM_FILE" ]] && BUILD_NUM=$(<"$BUILD_NUM_FILE")
BUILD_NUM=$((BUILD_NUM + 1))
echo "$BUILD_NUM" > "$BUILD_NUM_FILE"
echo "==> 构建号 $BUILD_NUM"
cd "$(dirname "$0")/../.."
ROOT="$(pwd)"
SRC="${1:-$ROOT/publish/osx-arm64}"
OUT="${2:-$SRC}"

APP="$OUT/MyIsland.app"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"

# 显示器控制工具：从源码重新编译（ddc-helper 的退出码约定 + 百分比写入模式
# 是 .NET 侧判断"该 VCP 是否被显示器支持"的依据，必须和主程序版本匹配，
# 不能用旧二进制）。编译失败不阻断打包——用已有二进制兜底。
if bash "$ROOT/tools/display-tools/build.sh" "$SRC" >/dev/null 2>&1; then
    echo "[ok] 显示器控制工具已重新编译"
else
    echo "[warn] 工具重新编译失败，沿用已有二进制（若为旧版，亮度调节可能失效）"
fi

# 可执行文件 + 原生库 + 随包工具（全部放 MacOS/，与单文件发布一致）
cp "$SRC"/Island.Avalonia "$APP/Contents/MacOS/"
# 原生依赖（Skia/Avalonia 等）：缺任何一个都会导致 .app 启动即崩
# （libSkiaSharp.dylib 缺失 → Avalonia 初始化抛 TypeInitializationException）。
# 原来用 `2>/dev/null || true` 静默跳过 —— 打包出跑不起来的 .app 却毫无提示。
shopt -s nullglob
_dylibs=("$SRC"/*.dylib)
shopt -u nullglob
if [ ${#_dylibs[@]} -eq 0 ]; then
    echo "[error] publish 目录里没有任何 .dylib —— 请先完整执行 dotnet publish（不要只增量 build）" >&2
    exit 1
fi
cp "${_dylibs[@]}" "$APP/Contents/MacOS/"
TOOLS=(island-thermal island-screens ddc-helper screen-guardian app-icon island-devices island-hotkey)
for t in "${TOOLS[@]}"; do
    if [ -f "$SRC/$t" ]; then cp "$SRC/$t" "$APP/Contents/MacOS/"; else echo "[warn] 缺少 $t（显示器控制功能不可用）"; fi
done
chmod +x "$APP/Contents/MacOS/"*

cp "$ROOT/packaging/macos/Info.plist" "$APP/Contents/Info.plist"
# 版本号注入（plist 模板里是 __BUILD__ 占位符）。
# 不用 sed -i ''：某些环境（sandbox shim）会吞掉 -i 的空串参数，导致 sed 把脚本名当文件名。
sed "s/__BUILD__/$BUILD_NUM/" "$APP/Contents/Info.plist" > "$APP/Contents/Info.plist.tmp" && mv "$APP/Contents/Info.plist.tmp" "$APP/Contents/Info.plist"
cp "$ROOT/branding/island.icns" "$APP/Contents/Resources/island.icns"
# 第三方许可（LunarCN + m1ddc，均为 MIT，内置其源码须随附许可）
cp "$ROOT/tools/display-tools/NOTICE.txt" "$APP/Contents/Resources/NOTICE.txt"
cat "$ROOT/tools/display-tools/LICENSE.LunarCN.txt" "$ROOT/tools/display-tools/LICENSE.m1ddc.txt" >> "$APP/Contents/Resources/NOTICE.txt"

# 本机临时签名（ad-hoc）：无 Developer ID 也能本机运行；跨机分发需 xattr 去 quarantine。
# --deep 递归签所有嵌套代码（.dylib / 随包工具），否则校验会报子组件未签。
codesign --force --deep --sign - "$APP"
if codesign --verify --deep --strict "$APP" 2>/dev/null; then
    echo "[ok] codesign 校验通过"
else
    echo "[warn] codesign 校验未通过（不影响本机运行）"
fi

echo "已生成：$APP"
echo "安装：cp -R \"$APP\" /Applications/ && open -a MyIsland"
