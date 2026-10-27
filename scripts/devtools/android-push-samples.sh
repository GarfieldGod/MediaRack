#!/usr/bin/env bash
# 把真实样本视频装进 App 的**私有内部目录**。
#
# 为什么不直接 adb push 到 /sdcard/Android/data/<pkg>？在 Android 16 / ColorOS（实测 PHY120）上，
# adb（shell uid）在 Android/data 里创建的文件，属主 App 看不见（FUSE 归属问题）——
# File.listFiles() 返回 null。所以先推到 /data/local/tmp，再用 run-as 复制进去（仅 debug 包可用）。
#
# 用法：
#   scripts/devtools/android-push-samples.sh                    # 自动找样本目录与 adb
#   scripts/devtools/android-push-samples.sh ~/Videos/Captures  # 指定样本目录
#   ADB=/path/to/adb scripts/devtools/android-push-samples.sh   # 指定 adb
#
# 跨平台：Windows（Git Bash / MSYS）、Linux、macOS 通用。
# Windows 上 adb.exe 是原生程序，认不得 /c/Users/... 这种 MSYS 路径，
# 所以送进去的本地路径要先经 cygpath -w 换成 C:\... 形式（下面 win_path 做的就是这件事）。

set -euo pipefail
cd "$(dirname "$0")/../.."

PKG=io.github.garfieldgod.mediarack
TARGETS=('demo/clips/A.mp4' 'demo/clips/B.mp4' 'demo/C.mp4')
TMPDIR_DEV=/data/local/tmp/mrack_push

# ---- 参数 -------------------------------------------------------------------
CAP_ARG=""
DRY=0
while [ $# -gt 0 ]; do
    case "$1" in
        -n|--dry-run) DRY=1; shift ;;
        -h|--help) cat <<'EOF'
用法：scripts/devtools/android-push-samples.sh [样本目录] [选项]

  <样本目录>        放 mp4 的目录。缺省依次尝试 ~/Videos/Captures、~/Videos、~/视频、~/Movies
  -n, --dry-run    只打印将要执行的 adb 命令，不真推
  -h, --help       显示本帮助

环境变量：
  ADB=...          指定 adb 路径；缺省按 PATH → ANDROID_HOME → ANDROID_SDK_ROOT → 常见安装位置查找

把 3 个最小的 mp4 装进 App 私有目录 files/sources/（demo/clips/A.mp4、demo/clips/B.mp4、demo/C.mp4）。
走 /data/local/tmp 中转 + run-as，仅对 debug 包有效。
EOF
                   exit 0 ;;
        -*) echo "未知参数：$1（--help 看用法）" >&2; exit 2 ;;
        *)  CAP_ARG=$1; shift ;;
    esac
done

# 所有 adb 调用都过这个函数：--dry-run 时只打印、不执行，
# 用来先确认"要推哪 3 个文件、推到哪"，免得在真机上试错。
run() {
    if [ "$DRY" = 1 ]; then
        printf '  [dry-run]'; printf ' %q' "$@"; printf '\n'
    else
        "$@"
    fi
}

# ---- 定位 adb ---------------------------------------------------------------
# 顺序：显式 $ADB → PATH → ANDROID_HOME / ANDROID_SDK_ROOT → 各平台默认安装位置。
# 不写死某个具体安装目录，是因为那是某台机器的布局，换台电脑就废了。
#
# 环境变量里常见的值是 Windows 形式（ANDROID_HOME=C:\Android\Sdk），
# 反斜杠在 bash 里不是路径分隔符、cygwin 也未必认，所以先用 cygpath 换成 /c/... 再 stat。
to_unix() {
    [ -n "${1:-}" ] || return 0
    if command -v cygpath >/dev/null 2>&1; then cygpath -u "$1"; else printf '%s\n' "$1"; fi
}

SDK_A=$(to_unix "${ANDROID_HOME:-}")
SDK_B=$(to_unix "${ANDROID_SDK_ROOT:-}")
SDK_C=$(to_unix "${LOCALAPPDATA:-}")

find_adb() {
    local c
    for c in \
        "${ADB:-}" \
        "$(command -v adb 2>/dev/null || true)" \
        "$SDK_A/platform-tools/adb" \
        "$SDK_A/platform-tools/adb.exe" \
        "$SDK_B/platform-tools/adb" \
        "$SDK_B/platform-tools/adb.exe" \
        "$SDK_C/Android/Sdk/platform-tools/adb.exe" \
        "$HOME/Android/Sdk/platform-tools/adb" \
        "/c/Application/Android/Sdk/platform-tools/adb.exe"
    do
        [ -n "$c" ] && [ -x "$c" ] && { printf '%s\n' "$c"; return 0; }
    done
    return 1
}

if ! ADB_BIN=$(find_adb); then
    if [ "$DRY" = 1 ]; then
        ADB_BIN="adb"
        echo "提示：--dry-run 下没找到 adb，按占位符 adb 打印命令。" >&2
    else
        cat >&2 <<'EOF'
错误：找不到 adb。

  三种办法任选其一：
    1. 让 adb 在 PATH 里（Android Studio 的 platform-tools 目录）
    2. 设置 ANDROID_HOME 或 ANDROID_SDK_ROOT 指向 SDK 根目录
    3. 显式传：ADB=/path/to/adb scripts/devtools/android-push-samples.sh
EOF
        exit 1
    fi
fi

# ---- 定位样本目录 -----------------------------------------------------------
# 缺省按平台常见位置猜；猜不到就让调用方用第一个参数指定。
find_samples() {
    local d
    for d in "${1:-}" \
             "$HOME/Videos/Captures" \
             "$HOME/Videos" \
             "$HOME/视频" \
             "$HOME/Movies"
    do
        [ -n "$d" ] && [ -d "$d" ] && { printf '%s\n' "$d"; return 0; }
    done
    return 1
}

if ! CAP=$(find_samples "$CAP_ARG"); then
    echo "错误：找不到样本目录。请显式指定，例如：scripts/devtools/android-push-samples.sh ~/Videos/Captures" >&2
    exit 1
fi

# 取最小的 3 个 mp4：样本要能秒推完，不希望挑到几个 GB 的大文件。
# 用 wc -c 取字节数而不是 stat -c/-f —— 那两个在 GNU 与 BSD 上参数不兼容。
# find -print0 + read -d '' 让带空格的文件名也能正确处理。
mapfile -t MP4S < <(
    find "$CAP" -maxdepth 1 -type f -iname '*.mp4' -print0 \
    | while IFS= read -r -d '' f; do printf '%s\t%s\n' "$(wc -c <"$f")" "$f"; done \
    | sort -n | head -3 | cut -f2-
)

if [ "${#MP4S[@]}" -lt 3 ]; then
    echo "错误：$CAP 里只有 ${#MP4S[@]} 个 mp4，需要 3 个。" >&2
    exit 1
fi

# adb.exe 是原生 Windows 程序，收到 /c/Users/... 会当成相对路径。
# Git Bash 有时会自动转换参数，但别指望——显式转换一次最稳。
win_path() {
    if command -v cygpath >/dev/null 2>&1; then
        cygpath -w "$1"
    else
        printf '%s\n' "$1"
    fi
}

echo "adb     $ADB_BIN"
echo "样本目录 $CAP"
echo

run "$ADB_BIN" shell mkdir -p "$TMPDIR_DEV"
for i in 0 1 2; do
    tmp_name="sample_$i.mp4"
    run "$ADB_BIN" push "$(win_path "${MP4S[$i]}")" "$TMPDIR_DEV/$tmp_name"
    dst=${TARGETS[$i]}
    dst_dir=${dst%/*}
    run "$ADB_BIN" shell run-as "$PKG" mkdir -p "files/sources/$dst_dir"
    run "$ADB_BIN" shell run-as "$PKG" cp "$TMPDIR_DEV/$tmp_name" "files/sources/$dst"
done
run "$ADB_BIN" shell rm -r "$TMPDIR_DEV"

echo
echo "--- files/sources on device ---"
run "$ADB_BIN" shell run-as "$PKG" ls -R files/sources
