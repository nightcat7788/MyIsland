#!/bin/bash
# 编译 MyIsland 内置的显示器控制工具（从 LunarCN 源码，MIT 许可）。
# 产物直接进 publish 目录，不再依赖 /Applications/LunarCN.app。
#   ddc-helper       外接屏 DDC/CI 读写（m1ddc 后端）
#   screen-guardian  内建屏关闭的独立守护进程（主进程崩溃也能恢复）
#   island-screens   屏幕枚举（我们自己的 Swift 小工具）
# 用法：bash tools/display-tools/build.sh <输出目录>
set -euo pipefail
cd "$(dirname "$0")"
OUT="${1:?用法: build.sh <输出目录>}"
mkdir -p "$OUT"

if [[ "$(uname -s)" != Darwin || "$(uname -m)" != arm64 ]]; then
    echo '显示器控制工具需要 Apple Silicon Mac 和 Xcode 命令行工具。' >&2
    exit 1
fi

# ddc-helper：ObjC + m1ddc 后端（与 LunarCN build.sh 同参数）
xcrun clang -O2 -Wall -Wextra -fmodules -DMAX_DISPLAYS=32 \
    -target arm64-apple-macos13.0 -I m1ddc/headers \
    m1ddc/sources/i2c.m m1ddc/sources/ioregistry.m DDCHelper.m \
    -framework Foundation -framework IOKit -framework CoreGraphics -framework CoreDisplay \
    -o "$OUT/ddc-helper"

# screen-guardian：Swift，链接 BuiltinDisplayEngine（SkyLight/CoreDisplay 私有 API 调用）
xcrun swiftc -O -swift-version 5 -parse-as-library -target arm64-apple-macosx13.0 \
    BuiltinDisplayEngine.swift ScreenGuardian.swift \
    -framework CoreGraphics -framework IOKit \
    -o "$OUT/screen-guardian"

# app-icon：应用图标探针（应用榜用图标；同目录上一级）
xcrun swiftc -O -target arm64-apple-macos13.0 \
    ../app-icon/app-icon.swift -o "$OUT/app-icon"

# island-devices：设备电量探针（蓝牙/USB 设备 + 内置电池，同目录上一级）
xcrun swiftc -O -target arm64-apple-macos13.0 \
    ../device-battery/device-battery.swift -o "$OUT/island-devices"

# island-hotkey：全局快捷键探针（Carbon；同目录上一级）
xcrun swiftc -O -target arm64-apple-macos13.0 \
    ../hotkey/hotkey.swift -o "$OUT/island-hotkey"

# island-screens：屏幕枚举（我们的工具，同目录上一级）
xcrun swiftc -O -target arm64-apple-macosx13.0 \
    ../screen-list/screens.swift -o "$OUT/island-screens"

# island-thermal：IOHID + SMC 硬件温度探针（同目录上一级）
xcrun clang -O2 -Wall -Wextra -target arm64-apple-macos13.0 \
    ../thermal-probe/thermal.m \
    -framework IOKit -framework CoreFoundation \
    -o "$OUT/island-thermal"

BINS=(
    "$OUT/ddc-helper"
    "$OUT/screen-guardian"
    "$OUT/island-screens"
    "$OUT/app-icon"
    "$OUT/island-devices"
    "$OUT/island-hotkey"
    "$OUT/island-thermal"
)
chmod +x "${BINS[@]}"
codesign --force --sign - "${BINS[@]}"

echo "已生成：ddc-helper / screen-guardian / island-screens / app-icon / island-devices / island-hotkey / island-thermal → $OUT"
