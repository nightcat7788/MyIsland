#!/bin/bash
# ==============================================================================
# macOS 侧收尾自检 —— 把计划 C1 的三项「人工核对」变成「一条命令出一份报告」
#
# 【为什么有这个脚本】
#   计划 C1 原本是三条人工核对项（清单上的复选框，但没人能保证每次都真核过）：
#     ① Feature 报告第 0 字节是不是报告 ID；
#     ② thermal: cpu source 是否指向 PMU tdie；
#     ③ 设备页读不到电量时是否显示「未知」而不是 0%。
#   本脚本把三条变成可重复执行、能出报告、能进版本库的一条命令。
#   「没法核对」绝不会被写成「通过」—— 缺工具链就如实写缺什么。
#
# 【怎么用】（必须在 macOS 上跑：原生探针是给 macOS 编译的）
#   bash tools/mac-selfcheck.sh
#   bash tools/mac-selfcheck.sh --out-dir publish/osx-arm64 --report docs/verification/mac-2026-11-26.md
#   bash tools/mac-selfcheck.sh --no-build     # 复用已有探针产物，不重新编译
#
# 【产物】
#   报告：docs/verification/mac-<日期>.md（同一天重跑覆盖同一个文件 ⇒ 幂等，不留垃圾文件）
#
# 【退出码】
#   0 = 三项全部「已核对且通过」
#   2 = 至少一项「已核对但不通过」
#   3 = 至少一项「没法核对」（缺工具链 / 非 macOS / 本机没有可核对的样本）
#   1 = 参数错误
#
# 【兼容】bash 3.2（macOS 自带）—— 不用关联数组 / 大写转换 / mapfile 等 bash 4+ 语法。
# 【路径】全部由脚本自身位置推导，不写死任何本机路径。
# ==============================================================================
set -u
set -o pipefail

# ---------- 脚本自身位置 → 仓库根（不写死路径） ----------
SCRIPT_PATH="$0"
case "$SCRIPT_PATH" in
    /*) ;;
    *) SCRIPT_PATH="$PWD/$SCRIPT_PATH" ;;
esac
SCRIPT_DIR="$(cd "$(dirname "$SCRIPT_PATH")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

OUT_DIR=""
REPORT=""
DO_BUILD=1

usage() {
    cat <<'USAGE'
用法：bash tools/mac-selfcheck.sh [选项]

  --out-dir DIR   探针产物目录（默认 <仓库根>/publish/osx-arm64，与 packaging/macos/publish.sh 一致）
  --report PATH   报告输出路径（默认 <仓库根>/docs/verification/mac-<日期>.md）
  --no-build      不重新编译探针，复用 --out-dir 里已有的产物
  -h, --help      显示本帮助

退出码：0 = 三项全部核对通过；2 = 有「已核对但不通过」；3 = 有「没法核对」；1 = 参数错误
USAGE
}

while [ $# -gt 0 ]; do
    case "$1" in
        --out-dir)
            [ $# -ge 2 ] || { echo "缺少 --out-dir 的参数" >&2; exit 1; }
            OUT_DIR="$2"; shift 2 ;;
        --report)
            [ $# -ge 2 ] || { echo "缺少 --report 的参数" >&2; exit 1; }
            REPORT="$2"; shift 2 ;;
        --no-build)
            DO_BUILD=0; shift ;;
        -h|--help)
            usage; exit 0 ;;
        *)
            echo "未知参数：$1" >&2
            usage >&2
            exit 1 ;;
    esac
done

[ -n "$OUT_DIR" ] || OUT_DIR="$ROOT/publish/osx-arm64"
if [ -z "$REPORT" ]; then
    REPORT="$ROOT/docs/verification/mac-$(date +%Y-%m-%d).md"
fi

cd "$ROOT" || exit 1

TMP_ROOT="/tmp"
if printenv TMPDIR >/dev/null 2>&1; then
    TMP_ROOT="$(printenv TMPDIR)"
fi
WORK="$(mktemp -d "$TMP_ROOT/mac-selfcheck.XXXXXX")" || exit 1
trap 'rm -rf "$WORK"' EXIT INT TERM

# ==============================================================================
# 0. 预检：平台 / 工具链
# ==============================================================================
UNAME_S="$(uname -s 2>/dev/null || echo unknown)"
UNAME_A="$(uname -a 2>/dev/null || echo unknown)"
IS_MAC=0
if [ "$UNAME_S" = "Darwin" ]; then IS_MAC=1; fi

HAVE_CLANG=0
if command -v clang >/dev/null 2>&1; then HAVE_CLANG=1; fi
HAVE_SWIFTC=0
if command -v swiftc >/dev/null 2>&1; then HAVE_SWIFTC=1; fi

XCODE_PATH=""
if [ "$IS_MAC" -eq 1 ]; then
    XCODE_PATH="$(xcode-select -p 2>/dev/null || true)"
fi
CLANG_VER=""
if [ "$HAVE_CLANG" -eq 1 ]; then
    CLANG_VER="$(clang --version 2>/dev/null | head -n 1)"
fi
SWIFTC_VER=""
if [ "$HAVE_SWIFTC" -eq 1 ]; then
    SWIFTC_VER="$(swiftc --version 2>&1 | head -n 1)"
fi

# ==============================================================================
# 1. 编译原生探针（复用 tools/build-mac-probes.sh）
# ==============================================================================
BUILD_LOG="$WORK/build.log"
BUILD_STATE="未执行"
BUILD_RC=0
if [ "$IS_MAC" -eq 0 ]; then
    BUILD_STATE="未执行（非 macOS 平台，uname -s = $UNAME_S）"
    printf '%s\n' "本机不是 macOS（uname -s = $UNAME_S），mac 原生探针无法编译、也无法运行。" > "$BUILD_LOG"
elif [ "$DO_BUILD" -eq 0 ]; then
    BUILD_STATE="跳过（--no-build）"
    printf '%s\n' "按 --no-build 跳过编译，复用 $OUT_DIR 里已有的产物。" > "$BUILD_LOG"
else
    if bash "$ROOT/tools/build-mac-probes.sh" "$OUT_DIR" > "$BUILD_LOG" 2>&1; then
        BUILD_STATE="成功"
    else
        BUILD_RC=$?
        BUILD_STATE="失败（退出码 $BUILD_RC）"
    fi
fi

# ==============================================================================
# 2. 跑探针
# ==============================================================================
DEV_BIN="$OUT_DIR/island-devices"
TH_BIN="$OUT_DIR/island-thermal"
DEV_OUT="$WORK/devices.out"; DEV_ERR="$WORK/devices.err"
TH_OUT="$WORK/thermal.out";  TH_ERR="$WORK/thermal.err"
: > "$DEV_OUT"; : > "$DEV_ERR"; : > "$TH_OUT"; : > "$TH_ERR"

DEV_STATE="未运行"; DEV_RC=127
if [ -x "$DEV_BIN" ]; then
    "$DEV_BIN" > "$DEV_OUT" 2> "$DEV_ERR"
    DEV_RC=$?
    DEV_STATE="已运行（退出码 $DEV_RC）"
else
    DEV_STATE="未运行（探针不存在或不可执行：$DEV_BIN）"
fi

TH_STATE="未运行"; TH_RC=127
if [ -x "$TH_BIN" ]; then
    "$TH_BIN" > "$TH_OUT" 2> "$TH_ERR"
    TH_RC=$?
    TH_STATE="已运行（退出码 $TH_RC）"
else
    TH_STATE="未运行（探针不存在或不可执行：$TH_BIN）"
fi

# ==============================================================================
# 3. 核对 ①：Feature 报告第 0 字节是不是报告 ID
# ==============================================================================
# 口径（实现与报告里写的一致）：
#   island-devices 的 HIDFTR 行格式：
#     HIDFTR<TAB>vid<TAB>pid<TAB>报告ID(十进制)<TAB>字节hex(空格分隔)
#   判据：第 5 字段的第 1 个字节 == printf '%02X' (报告ID & 0xFF)。
#   依据：C# 侧 HidReportDescriptor.ReadValue 用 startBit = 8 + BitOffset，
#         即假定 report[0] 是报告 ID 字节；探针 device-battery.swift 的
#         readFeatureReport() 手工把报告 ID 补在第 0 字节。两者必须一致，
#         否则所有 HID 字段整体错位。
C1_STATE="没法核对"
C1_ACTUAL=""
C1_DETAIL="$WORK/c1.detail"
: > "$C1_DETAIL"

if [ ! -x "$DEV_BIN" ]; then
    C1_ACTUAL="没有可运行的 island-devices 探针"
    printf '%s\n' "- 没法核对：探针不可运行（$DEV_BIN）。" >> "$C1_DETAIL"
elif [ ! -s "$DEV_OUT" ]; then
    C1_ACTUAL="探针输出为空（没有任何设备行）"
    printf '%s\n' "- 没法核对：探针 stdout 为空，没有样本可判。" >> "$C1_DETAIL"
else
    awk -F'\t' '$1=="HIDFTR" && NF>=5 { print $4 "\t" $5 }' "$DEV_OUT" > "$WORK/ftr.txt"
    ftr_total=$(wc -l < "$WORK/ftr.txt" | tr -d ' ')
    c1_ok=0
    c1_bad=0
    while IFS=$'\t' read -r c1_rid c1_bytes; do
        [ -n "$c1_rid" ] || continue
        case "$c1_rid" in
            ''|*[!0-9]*)
                c1_bad=$((c1_bad + 1))
                printf '%s\n' "- 报告 ID 字段不是十进制整数：\`$c1_rid\`" >> "$C1_DETAIL"
                continue ;;
        esac
        c1_first=$(printf '%s\n' "$c1_bytes" | cut -d' ' -f1)
        if [ -z "$c1_first" ]; then
            c1_bad=$((c1_bad + 1))
            printf '%s\n' "- 报告 ID=$c1_rid 的 HIDFTR 行没有字节内容" >> "$C1_DETAIL"
            continue
        fi
        c1_first_upper=$(printf '%s' "$c1_first" | tr '[:lower:]' '[:upper:]')
        c1_expect=$(printf '%02X' "$(( c1_rid % 256 ))")
        if [ "$c1_first_upper" = "$c1_expect" ]; then
            c1_ok=$((c1_ok + 1))
        else
            c1_bad=$((c1_bad + 1))
            printf '%s\n' "- 报告 ID=$c1_rid：期望第 0 字节 = \`$c1_expect\`，实测 = \`$c1_first_upper\`" >> "$C1_DETAIL"
        fi
    done < "$WORK/ftr.txt"

    if [ "$ftr_total" -eq 0 ]; then
        C1_STATE="没法核对"
        C1_ACTUAL="没有 Feature 报告样本（HIDFTR 行 0 条）"
        printf '%s\n' "- 没法核对：本机没有支持 Feature 报告的 HID 设备，探针一条 HIDFTR 行都没输出。" >> "$C1_DETAIL"
    elif [ "$c1_bad" -gt 0 ]; then
        C1_STATE="不通过"
        C1_ACTUAL="$c1_bad / $ftr_total 条 HIDFTR 行的第 0 字节与报告 ID 不一致"
        printf '%s\n' "- 一致 $c1_ok 条 / 不一致 $c1_bad 条（样本总数 $ftr_total）。" >> "$C1_DETAIL"
    else
        C1_STATE="通过"
        C1_ACTUAL="$ftr_total 条 HIDFTR 行全部一致（第 0 字节 == 报告 ID）"
        printf '%s\n' "- 一致 $c1_ok 条 / 不一致 $c1_bad 条，样本总数 $ftr_total。" >> "$C1_DETAIL"
    fi
fi

# ==============================================================================
# 4. 核对 ②：thermal: cpu source 是否指向 PMU tdie
# ==============================================================================
# 口径：island-thermal 的 stderr 有一行 "thermal: cpu source = <传感器名> <读数>"。
#   期望传感器名以 "PMU tdie" 开头 —— 这正是探针 thermal.m 里 CPU 优先级 1 的规则
#   （cpu_priority(): name_starts_ci(name, "PMU tdie") → 1）。
#   实测是别的来源（pACC / eACC / SMC Tp* 平均）或 (none) ⇒ 不通过，并打印实测值。
C2_STATE="没法核对"
C2_ACTUAL=""
C2_DETAIL="$WORK/c2.detail"
: > "$C2_DETAIL"

C2_SRC_LINE=""
if [ -x "$TH_BIN" ]; then
    C2_SRC_LINE="$(awk '/thermal: cpu source/ { print; exit }' "$TH_ERR")"
fi
C2_STDOUT="$(head -n 1 "$TH_OUT" 2>/dev/null)"

if [ ! -x "$TH_BIN" ]; then
    C2_ACTUAL="没有可运行的 island-thermal 探针"
    printf '%s\n' "- 没法核对：探针不可运行（$TH_BIN）。" >> "$C2_DETAIL"
elif [ -z "$C2_SRC_LINE" ]; then
    C2_ACTUAL="探针 stderr 里没有 thermal: cpu source 行"
    printf '%s\n' "- 没法核对：stderr 里没有 thermal: cpu source 行（探针版本不对 / 编译产物不是本仓库的）。" >> "$C2_DETAIL"
else
    C2_SOURCE="$(printf '%s\n' "$C2_SRC_LINE" | sed -e 's/^.*thermal: cpu source = //' -e 's/ [-0-9.]*$//')"
    C2_LOWER="$(printf '%s' "$C2_SOURCE" | tr '[:upper:]' '[:lower:]')"
    printf '%s\n' "- 原始行：\`$C2_SRC_LINE\`" >> "$C2_DETAIL"
    printf '%s\n' "- 解析出的传感器名：\`$C2_SOURCE\`" >> "$C2_DETAIL"
    if [ -n "$C2_STDOUT" ]; then
        printf '%s\n' "- 探针 stdout：\`$C2_STDOUT\`" >> "$C2_DETAIL"
    fi
    case "$C2_LOWER" in
        "pmu tdie"*)
            C2_STATE="通过"
            C2_ACTUAL="$C2_SOURCE"
            ;;
        *)
            C2_STATE="不通过"
            C2_ACTUAL="$C2_SOURCE"
            if [ "$C2_SOURCE" = "(none)" ]; then
                printf '%s\n' "- 说明：探针一个 CPU 传感器都没选中（IOHID 无候选 + SMC 兜底也没有值）。" >> "$C2_DETAIL"
            else
                printf '%s\n' "- 说明：选中的不是 PMU tdie（可能是 pACC / eACC / SMC Tp* 兜底 —— 口径要求 PMU tdie）。" >> "$C2_DETAIL"
            fi
            ;;
    esac
fi

# ==============================================================================
# 5. 核对 ③：设备页读不到电量时显示「未知」而不是 0%
#    拆成三段，各自如实标注怎么验的：
#    ③-a 静态：核对源码（读不到分支写 LevelText="未知"；渲染优先用 LevelText）
#    ③-b 运行时：核对探针输出（输入侧没有假 0%；未读到电量的设备确实没有百分比）
#    ③-c 人工：画面确认（脚本不能替人看屏幕）—— 报告里留勾选位
# ==============================================================================
BAT_CS="$ROOT/src/Island.Avalonia/DeviceBattery.cs"
CAN_CS="$ROOT/src/Island.Avalonia/IslandCanvas.cs"

S3_STATE="没法核对"
S3_DETAIL="$WORK/c3a.detail"
: > "$S3_DETAIL"

if [ ! -f "$BAT_CS" ] || [ ! -f "$CAN_CS" ]; then
    printf '%s\n' "- 没法核对：源码不在仓库里（$BAT_CS / $CAN_CS）。" >> "$S3_DETAIL"
else
    s3_miss=0
    if ! grep -q 'LevelText = "未知"' "$BAT_CS"; then
        s3_miss=$((s3_miss + 1))
        printf '%s\n' "- 不满足：DeviceBattery.cs 里找不到 \`LevelText = \"未知\"\`。" >> "$S3_DETAIL"
    fi
    if ! grep -q 'unknown = dev.LevelText is not null' "$CAN_CS"; then
        s3_miss=$((s3_miss + 1))
        printf '%s\n' "- 不满足：IslandCanvas.cs 里找不到 \`unknown = dev.LevelText is not null\`。" >> "$S3_DETAIL"
    fi
    if ! grep -q 'RenderKit.Text(dev.LevelText!' "$CAN_CS"; then
        s3_miss=$((s3_miss + 1))
        printf '%s\n' "- 不满足：IslandCanvas.cs 里找不到 \`RenderKit.Text(dev.LevelText!\`（未知分支画的是文字，不是百分比）。" >> "$S3_DETAIL"
    fi
    if [ "$s3_miss" -eq 0 ]; then
        S3_STATE="通过"
        printf '%s\n' "- 三条字面量全部命中：DeviceBattery.cs 写「未知」、IslandCanvas.cs 先判 LevelText 非空再画文字。" >> "$S3_DETAIL"
    else
        S3_STATE="不通过"
        printf '%s\n' "- 有 $s3_miss 条口径不满足（上面逐条列出）。" >> "$S3_DETAIL"
    fi
fi

R3_STATE="没法核对"
R3_DETAIL="$WORK/c3b.detail"
: > "$R3_DETAIL"

if [ ! -x "$DEV_BIN" ]; then
    printf '%s\n' "- 没法核对：探针不可运行。" >> "$R3_DETAIL"
elif [ ! -s "$DEV_OUT" ]; then
    printf '%s\n' "- 没法核对：探针输出为空，没有样本。" >> "$R3_DETAIL"
else
    r3_bad=$(awk -F'\t' '$1!="HIDDEV" && $1!="HIDFTR" && NF>=5 { if ($3 !~ /^[0-9]+$/ || ($3+0) > 100) print $0 }' "$DEV_OUT")
    r3_battery=$(awk -F'\t' '$1!="HIDDEV" && $1!="HIDFTR" && NF>=5 { c++ } END { print c+0 }' "$DEV_OUT")
    r3_hiddev=$(awk -F'\t' '$1=="HIDDEV" { c++ } END { print c+0 }' "$DEV_OUT")
    if [ -n "$r3_bad" ]; then
        R3_STATE="不通过"
        printf '%s\n' "- 不通过：探针输出了非法电量值（不是 0..100 的整数）：" >> "$R3_DETAIL"
        printf '%s\n' "$r3_bad" | head -n 10 | sed 's/^/  · /' >> "$R3_DETAIL"
    elif [ "$r3_hiddev" -eq 0 ]; then
        R3_STATE="没法核对"
        printf '%s\n' "- 没法核对：本机没有「识别到但读不到电量」的 HID 设备样本（HIDDEV 行 0 条），运行时这一段无从判。" >> "$R3_DETAIL"
    else
        R3_STATE="通过"
        printf '%s\n' "- 电量行 $r3_battery 条，电量字段全部是 0..100 的整数（探针不编造占位值）。" >> "$R3_DETAIL"
        printf '%s\n' "- HIDDEV 行 $r3_hiddev 条 = 被识别为 HID 但**探针没给出电量百分比**的设备；它们在应用里走 LevelText=「未知」分支或干脆不显示，两种都不是 0%。" >> "$R3_DETAIL"
        printf '%s\n' "- 这些设备（产品名 / vid / pid）：" >> "$R3_DETAIL"
        awk -F'\t' '$1=="HIDDEV" { printf "  · %s (vid %s / pid %s)\n", ($6=="" ? "(无名)" : $6), $2, $3 }' "$DEV_OUT" | head -n 20 >> "$R3_DETAIL"
    fi
fi

# ③ 合并：静态与运行时两段都通过才算通过
C3_STATE="没法核对"
if [ "$S3_STATE" = "不通过" ] || [ "$R3_STATE" = "不通过" ]; then
    C3_STATE="不通过"
elif [ "$S3_STATE" = "通过" ] && [ "$R3_STATE" = "通过" ]; then
    C3_STATE="通过"
else
    C3_STATE="没法核对"
fi

C3_ACTUAL="静态(源码)=$S3_STATE；运行时(探针输出)=$R3_STATE"

# ==============================================================================
# 6. 总判定 + 退出码
# ==============================================================================
has_fail=0
has_unknown=0
for st in "$C1_STATE" "$C2_STATE" "$C3_STATE"; do
    case "$st" in
        不通过) has_fail=1 ;;
        没法核对) has_unknown=1 ;;
    esac
done
if [ "$has_fail" -eq 1 ]; then
    EXIT_CODE=2
    OVERALL="不通过"
elif [ "$has_unknown" -eq 1 ]; then
    EXIT_CODE=3
    OVERALL="没法核对"
else
    EXIT_CODE=0
    OVERALL="通过"
fi

# ==============================================================================
# 7. 出报告
# ==============================================================================
mkdir -p "$(dirname "$REPORT")" || exit 1

{
cat <<'HEADER'
# macOS 侧自检报告（计划 C1）

> 本文件由 `tools/mac-selfcheck.sh` 自动生成 —— **不要手改**；重跑脚本会覆盖同名文件。
> 判定口径（怎么判、判什么值）写在下面每一节里；「实测」由脚本填，
> 最后一节「人工回填」留给跑报告的人补。

HEADER
printf '%s\n' ""
printf '%s\n' "| 项 | 值 |"
printf '%s\n' "|---|---|"
printf '%s\n' "| 执行时间 | $(date '+%Y-%m-%d %H:%M:%S %z') |"
printf '%s\n' "| 主机 | \`$UNAME_A\` |"
if [ "$IS_MAC" -eq 1 ]; then
    printf '%s\n' "| 系统版本 | \`$(sw_vers 2>/dev/null | tr '\n' ' ')\` |"
else
    printf '%s\n' "| 系统版本 | 非 macOS（uname -s = \`$UNAME_S\`）|"
fi
printf '%s\n' "| 仓库根 | \`$ROOT\` |"
printf '%s\n' "| 探针产物目录 | \`$OUT_DIR\` |"
printf '%s\n' "| **总体结论** | **$OVERALL**（退出码 $EXIT_CODE）|"
printf '%s\n' ""

cat <<'SECTION1'
## 0. 工具链与产物

| 项 | 状态 |
|---|---|
SECTION1
if [ "$HAVE_CLANG" -eq 1 ]; then
    printf '%s\n' "| clang | 有 —— \`$CLANG_VER\` |"
else
    printf '%s\n' "| clang | **缺** |"
fi
if [ "$HAVE_SWIFTC" -eq 1 ]; then
    printf '%s\n' "| swiftc | 有 —— \`$SWIFTC_VER\` |"
else
    printf '%s\n' "| swiftc | **缺** |"
fi
if [ -n "$XCODE_PATH" ]; then
    printf '%s\n' "| Xcode CLT | \`$XCODE_PATH\` |"
else
    printf '%s\n' "| Xcode CLT | **缺**（xcode-select -p 无输出）|"
fi
printf '%s\n' "| 探针编译 | $BUILD_STATE |"
printf '%s\n' "| island-devices | $DEV_STATE |"
printf '%s\n' "| island-thermal | $TH_STATE |"
printf '%s\n' ""
printf '%s\n' "> 缺什么就写什么：「没法核对」永远不会被写成「通过」。"
printf '%s\n' ""

cat <<'SECTION2'
## 1. 核对项总表

| # | 核对项 | 期望值 | 实测值 | 结论 |
|---|---|---|---|---|
SECTION2
printf '%s\n' "| ① | Feature 报告第 0 字节是不是报告 ID | 第 0 字节 == 报告 ID 的十六进制 | $C1_ACTUAL | **$C1_STATE** |"
printf '%s\n' "| ② | thermal: cpu source 是否指向 PMU tdie | 传感器名以 \`PMU tdie\` 开头 | $C2_ACTUAL | **$C2_STATE** |"
printf '%s\n' "| ③ | 设备页读不到电量时显示「未知」而不是 0% | 静态 + 运行时两段都通过 | $C3_ACTUAL | **$C3_STATE** |"
printf '%s\n' ""
printf '%s\n' "③ 拆两段的说明：**③-a 静态**核对源码、**③-b 运行时**核对探针输出；两段都通过才算 ③ 通过。"
printf '%s\n' "**③-c 画面确认**必须由人来做（脚本不能替人看屏幕），勾选位见 §3。"
printf '%s\n' ""

cat <<'SECTION3'
## 2. 逐项证据

### ① Feature 报告第 0 字节是不是报告 ID

**口径**：取 `island-devices` 输出的每一行 `HIDFTR`，行格式为

    HIDFTR<TAB>vid<TAB>pid<TAB>报告ID(十进制)<TAB>字节hex(空格分隔)

判据 = 第 5 字段的第 1 个字节 == `printf '%02X' (报告ID & 0xFF)`。
不一致时**同时打印期望值与实测值**（见下）。

**为什么这么判**：

- 探针 `tools/device-battery/device-battery.swift` 的 `readFeatureReport()` 手工把报告 ID 补在第 0 字节
  （macOS 的 `IOHIDDeviceGetReport` 把报告 ID 当独立参数，返回缓冲区里**不含**报告 ID）。
- C# 侧 `src/Island.App/Services/Peripheral/HidReportDescriptor.cs` 的 `ReadValue()` 用
  `startBit = 8 + field.BitOffset`，即假定 `report[0]` 就是报告 ID 字节。
- 两者必须一致，否则所有 HID 字段整体错位（读出来的百分比全是错的）。

**实测**：

SECTION3
cat "$C1_DETAIL"
printf '%s\n' ""

cat <<'SECTION3B'
### ② thermal: cpu source 是否指向 PMU tdie

**口径**：`island-thermal` 的 **stderr** 有一行

    thermal: cpu source = <传感器名> <读数>

判据 = 解析出的传感器名以 `PMU tdie` 开头（这正是探针 `tools/thermal-probe/thermal.m` 里
`cpu_priority()` 的优先级 1 规则：`name_starts_ci(name, "PMU tdie")` → 1）。
选中别的来源（`pACC MTR Temp Sensor` / `eACC MTR Temp Sensor` / `SMC Tp* 平均`）或 `(none)`
都判**不通过**，并打印实测值。

**实测**：

SECTION3B
cat "$C2_DETAIL"
printf '%s\n' ""

cat <<'SECTION4'
### ③ 设备页读不到电量时显示「未知」而不是 0%

这一条**不是探针能测的**（它属于应用渲染路径），所以拆成三段，各自注明怎么验的。

#### ③-a 静态：核对源码

| 判据 | 文件 |
|---|---|
| 读不到电量的分支写 `LevelText = "未知"` | `src/Island.Avalonia/DeviceBattery.cs` |
| 渲染时先判 `dev.LevelText is not null` | `src/Island.Avalonia/IslandCanvas.cs` |
| 未知分支画的是文字 `RenderKit.Text(dev.LevelText!, ...)` 而不是 `$"{dev.Percent}%"` | `src/Island.Avalonia/IslandCanvas.cs` |

**实测（$S3_STATE）**：

SECTION4
cat "$S3_DETAIL"
printf '%s\n' ""

cat <<'SECTION5'
#### ③-b 运行时：核对探针输出

**口径**：

1. `island-devices` 每条电量行的电量字段必须是 **0..100 的整数**
   （探针不编造占位值 —— `device-battery.swift` 的注释就是「取不到电量的设备不输出（绝不编造数值）」）；
2. `HIDDEV` 行 = 被识别为 HID 但**探针没给出电量百分比**的设备集合 —— 它们在应用里走
   `LevelText = "未知"` 分支（L3 协议对上了但没读到 / L4 只识别不读数）或干脆不显示，
   **两种都不是 0%**；
3. 一条 `HIDDEV` 都没有 ⇒ 运行时这段**没法核对**（本机没有样本）。

**这一段证明的是「输入侧没有假读数」，不直接观察画出来的字形** ——
字形由 ③-a 的源码核对 + ③-c 的人工画面确认共同保证。

**实测（$R3_STATE）**：

SECTION5
cat "$R3_DETAIL"
printf '%s\n' ""

cat <<'SECTION6'
#### ③-c 人工：画面确认（脚本替不了人）

- [ ] 打开应用 → 展开面板 → 设备页，**读不到电量的设备显示的是「未知」两个字，不是 0%**
- [ ] 截图存到 `docs/verification/`（文件名带日期），并在提交信息里写明

SECTION6
cat <<'SECTION7'
## 3. 原始探针输出

### island-thermal stdout

SECTION7
sed 's/^/    /' "$TH_OUT" 2>/dev/null | head -n 40
printf '%s\n' ""
cat <<'SECTION8'
### island-thermal stderr

SECTION8
sed 's/^/    /' "$TH_ERR" 2>/dev/null | head -n 40
printf '%s\n' ""
cat <<'SECTION9'
### island-devices stdout（最多 200 行）

SECTION9
sed 's/^/    /' "$DEV_OUT" 2>/dev/null | head -n 200
printf '%s\n' ""
cat <<'SECTION10'
### island-devices stderr

SECTION10
sed 's/^/    /' "$DEV_ERR" 2>/dev/null | head -n 40
printf '%s\n' ""
cat <<'SECTION11'
### 探针编译日志（尾部 40 行）

SECTION11
sed 's/^/    /' "$BUILD_LOG" 2>/dev/null | tail -n 40
printf '%s\n' ""

cat <<'SECTION12'
## 4. 人工回填（跑报告的人补）

| 核对项 | 期望值 | 实测值 | 结论 | 人工确认 |
|---|---|---|---|---|
| ① Feature 报告第 0 字节是不是报告 ID | 第 0 字节 == 报告 ID 十六进制 | 见 §1 | 见 §1 | ☐ |
| ② thermal: cpu source 是否指向 PMU tdie | 传感器名以 `PMU tdie` 开头 | 见 §1 | 见 §1 | ☐ |
| ③ 设备页读不到电量显示「未知」 | 静态 + 运行时两段都通过 | 见 §1 | 见 §1 | ☐ |

SECTION12
cat <<'SECTION13'
## 5. 回填指引（跑完这三步，C1 才能勾）

1. **把这份报告提交进版本库**（它是这次核对的唯一证据）：

       git add docs/verification/mac-<日期>.md
       git commit -m "docs(verification): mac 侧三项自检报告（<日期>）"

2. **把实测值抄回任务报告**：
   `.superpowers/sdd/2026-11-26-project-completion-roadmap/task-12-mac-selfcheck-report.md`
   的「Mac 实测回填」一节（`.superpowers/` 在仓库根下）。
   要抄的是：三项的**实测值**、**结论**、**报告文件路径**。

3. **再勾计划里的 C1**：只有当三项都是「已核对且通过」、
   且 §2 里 ③-c 的画面确认也勾上了，C1 才能勾。
   只要有一项是「不通过」或「没法核对」，**就如实留着没勾**，把缺什么写进任务报告。

SECTION13
} > "$REPORT"

# ==============================================================================
# 8. 控制台摘要
# ==============================================================================
echo "==> mac 侧自检完成"
echo "    报告：$REPORT"
echo "    ① $C1_STATE —— $C1_ACTUAL"
echo "    ② $C2_STATE —— $C2_ACTUAL"
echo "    ③ $C3_STATE —— $C3_ACTUAL"
echo "    总体：$OVERALL（退出码 $EXIT_CODE）"
exit "$EXIT_CODE"
