#!/bin/bash
# 编译 macOS 原生探针（源码 → 可执行文件）。
#
# 【为什么需要这个脚本】仓库里只有探针**源码**（thermal.m / device-battery.swift），
# 没有编译脚本，而 packaging/macos/publish.sh 会硬性校验
# island-thermal / island-devices 等工具存在于发布目录里 —— 也就是说
# 「mac 版能不能打出来」取决于某个人在机器上敲过的命令，没留下可复现的路径。
# 本脚本把两者的编译命令固定下来（与各源码头注释里写的一致），并做产物自检。
#
# 【产物】
#   island-thermal    ← tools/thermal-probe/thermal.m            （CPU/GPU/SSD 温度）
#   island-devices    ← tools/device-battery/device-battery.swift （设备电量）
#
# 用法：bash tools/build-mac-probes.sh [输出目录]
#   输出目录默认 publish/osx-arm64（与 packaging/macos/publish.sh 一致）
#
# ⚠️ 必须在 macOS 上跑（需要 clang / swiftc 与 macOS SDK）。
#    交叉编译不成立：IOKit / Foundation 都是系统框架。
set -euo pipefail
cd "$(dirname "$0")/.."
ROOT="$(pwd)"

OUT="${1:-$ROOT/publish/osx-arm64}"
mkdir -p "$OUT"

echo "==> 编译原生探针 → $OUT"

# ---- island-thermal：IOHID 温度传感器 + SMC（macmon 协议）----
if ! clang -O2 -framework IOKit -framework CoreFoundation \
        -o "$OUT/island-thermal" tools/thermal-probe/thermal.m; then
    echo "[error] island-thermal 编译失败" >&2
    exit 1
fi
echo "[ok] island-thermal"

# ---- island-devices：IOKit 设备电量（蓝牙 HID + 内置电池）----
if ! swiftc -O -o "$OUT/island-devices" tools/device-battery/device-battery.swift; then
    echo "[error] island-devices 编译失败" >&2
    exit 1
fi
echo "[ok] island-devices"

chmod +x "$OUT/island-thermal" "$OUT/island-devices"

# ---- 自检：产物存在且能跑起来（探针只读，不改系统状态）----
for tool in island-thermal island-devices; do
    if [[ ! -x "$OUT/$tool" ]]; then
        echo "[error] 产物缺失或不可执行：$OUT/$tool" >&2
        exit 1
    fi
done

# 冒烟：设备电量探针跑一次（读不到就输出空，不该崩）
if ! "$OUT/island-devices" >/dev/null 2>&1; then
    echo "[warn] island-devices 冒烟运行失败（退出码非 0）—— 运行时再查"
else
    echo "[ok] island-devices 冒烟通过"
fi

echo
echo "==> 完成：$OUT/island-thermal, $OUT/island-devices"
