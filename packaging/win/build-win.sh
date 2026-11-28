#!/bin/bash
# Windows 分发物打包（在 macOS 上交叉构建）。
#
# 【能建什么 / 不能建什么】—— 如实说明，不含糊：
#   ✅ 便携版 zip      publish/win-x64 + packaging/win-portable 的 4 个附加文件
#   ✅ 单文件版 exe    PublishSingleFile + 压缩，自包含
#   ❌ 安装包 Setup.exe 需要 Inno Setup 的 ISCC.exe（Windows 专用编译器，无 macOS 版本）
#                       → 必须在 Windows 机器上执行：iscc installer\MyIsland.iss
#
# 【重要：这不是正式发布门禁的替代】
#   项目自己的发布流程 dev.ps1 规定「先跑 Island.Tests，失败则中止发布」。
#   测试工程目标框架是 net8.0-windows（依赖 WindowsDesktop 运行时），在 Mac 上编译得过、跑不起来。
#   所以本脚本产出的是「与源码同源、编译 0 警告、通过 P0 危险条目检查」的包，
#   但**未经正式测试门禁**，也**未在 Windows 上运行验证**。
#   能在 Mac 上做的替代验证见 tests 目录说明（Settings / MonitorModel 两组跨平台断言）。
#
# 用法：bash packaging/win/build-win.sh
set -euo pipefail
cd "$(dirname "$0")/../.."
ROOT="$(pwd)"
if [[ -n "${DOTNET_ROOT:-}" && -x "$DOTNET_ROOT/dotnet" ]]; then
    DOTNET="$DOTNET_ROOT/dotnet"
elif command -v dotnet >/dev/null 2>&1; then
    DOTNET="$(command -v dotnet)"
else
    DOTNET="$HOME/.dotnet/dotnet"
fi

VERSION=$(grep -oE '<Version>[^<]+' Directory.Build.props | head -n1 | sed 's/<Version>//')
[[ -n "$VERSION" ]] || { echo "[error] 无法从 Directory.Build.props 读取版本号" >&2; exit 1; }
OUT="$ROOT/publish/win-dist"
PUB="$ROOT/publish/win-x64"
EXTRA="$ROOT/packaging/win-portable"

echo "==> 版本 $VERSION"

# ── 1. 便携版依赖的完整 publish ───────────────────────────────────────────
# 每次先删干净：.NET 增量 publish 会先清空输出目录，第二次可能丢文件（mac 脚本踩过同一个坑）。
echo "==> [1/3] publish（框架依赖目录，供便携版）"
rm -rf "$PUB"
"$DOTNET" publish src/Island.App/Island.App.csproj -c Release -r win-x64 --self-contained true -o "$PUB" --nologo

# ── 2. P0 回归：危险 / 探针条目必须为 0 ──────────────────────────────────
# 2026-11-23 三次 0x1EA 蓝屏的元凶是 inpoutx64.dll（一被加载就装未签名内核驱动）。
# 源码树 tools/inpout/ 里仍有这个 dll，任何一步漏了排除都可能把它带进分发物。
check_clean() {
    local dir="$1" label="$2"
    local bad
    bad=$(find "$dir" \( -iname 'inpout*' -o -iname 'lhmprobe*' -o -iname '*.sys' \) | wc -l | tr -d ' ')
    if [[ "$bad" -ne 0 ]]; then
        echo "[error] $label 含 $bad 个危险/探针条目，中止：" >&2
        find "$dir" \( -iname 'inpout*' -o -iname 'lhmprobe*' -o -iname '*.sys' \) >&2
        exit 1
    fi
}
check_clean "$PUB" "publish/win-x64"
echo "[ok] publish/win-x64 无 inpout / lhmprobe / .sys"

# ── 3. 便携版 zip ─────────────────────────────────────────────────────────
# 排除 *.pdb（调试符号，用户机器不需要）与 createdump.exe 之外的探针类文件。
echo "==> [2/3] 便携版 zip"
rm -rf "$OUT" && mkdir -p "$OUT"
STAGE=$(mktemp -d)
trap 'rm -rf "$STAGE"' EXIT
# 用 rsync 的 --exclude 精确剔除（cp 做不到排除，find -exec 太啰嗦）
rsync -a --exclude='*.pdb' --exclude='inpout*' --exclude='lhmprobe*' --exclude='LhmProbe*' "$PUB/" "$STAGE/"
cp "$EXTRA/README.txt" "$EXTRA/run.ps1" "$EXTRA/MyIsland图标.png" "$EXTRA/启动 MyIsland.bat" "$ROOT/THIRD-PARTY-NOTICES.txt" "$STAGE/"
check_clean "$STAGE" "便携版暂存目录"
PORTABLE="MyIsland-$VERSION-portable.zip"
# 【不要用 macOS 自带的 zip 命令】它写入 UTF-8 文件名却不置 zip 规范的 UTF-8 标志位
# （通用位标志 0x800）→ Windows 解压工具不知道该按 UTF-8 解，回退到本地代码页（GBK）
# → 「启动 MyIsland.bat」「MyIsland图标.png」解压出来是乱码文件名。
# 11-23 的旧包是用 Windows 工具打的，标志位是置上的；必须保持一致。
# Python 的 zipfile 对非 ASCII 名会自动置位，用它与具体平台无关。
STAGE="$STAGE" OUT_ZIP="$OUT/$PORTABLE" python3 - <<'PY'
import os, zipfile
stage, out = os.environ["STAGE"], os.environ["OUT_ZIP"]
with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
    for root, _, files in os.walk(stage):
        for f in sorted(files):
            p = os.path.join(root, f)
            z.write(p, os.path.relpath(p, stage))
PY
# 自检：非 ASCII 文件名必须带 UTF-8 标志位，否则中止（不让乱码包流出去）
python3 - "$OUT/$PORTABLE" <<'PY'
import sys, zipfile
bad = [i.filename for i in zipfile.ZipFile(sys.argv[1]).infolist()
       if not i.filename.isascii() and not (i.flag_bits & 0x800)]
if bad:
    sys.exit(f"[error] 以下条目的文件名未置 UTF-8 标志位，Windows 上会乱码：{bad}")
PY
echo "[ok] 中文文件名 UTF-8 标志位：已置位"
# 注意：变量名后紧跟中文全角标点时必须用 ${VAR} —— 在 set -u 下，bash 会把
# 多字节的「（」误读成变量名的一部分，报 "unbound variable"。
echo "[ok] ${PORTABLE}（$(unzip -Z1 "$OUT/$PORTABLE" | wc -l | tr -d ' ') 项）"

# ── 4. 单文件版 exe ───────────────────────────────────────────────────────
# 单独 publish 到临时目录：PublishSingleFile 会把运行时与所有程序集打进一个 exe，
# 与便携版的目录布局互斥，不能共用 publish/win-x64。
echo "==> [3/3] 单文件版 exe"
SINGLE=$(mktemp -d)
trap 'rm -rf "$STAGE" "$SINGLE"' EXIT
"$DOTNET" publish src/Island.App/Island.App.csproj -c Release -r win-x64 --self-contained true \
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true -o "$SINGLE" --nologo
check_clean "$SINGLE" "单文件版输出"
ONE="MyIsland-$VERSION.exe"
cp "$SINGLE/Island.App.exe" "$OUT/$ONE"
cp "$ROOT/THIRD-PARTY-NOTICES.txt" "$OUT/THIRD-PARTY-NOTICES.txt"
echo "[ok] ${ONE}（$(du -h "$OUT/$ONE" | cut -f1)）"

# ── 5. 汇总 ───────────────────────────────────────────────────────────────
echo
echo "==> 完成。输出：publish/win-dist/"
ls -la "$OUT" | awk 'NR>3 {printf "    %10d  %s\n", $5, $9}'
echo
echo "[!] 未构建：MyIsland-Setup-$VERSION.exe（需在 Windows 上运行 iscc installer/MyIsland.iss）"
echo "[!] 以上产物未经正式测试门禁（Island.Tests 需 Windows 运行时），也未在 Windows 上运行验证。"
