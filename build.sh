#!/usr/bin/env bash
# 一键构建 MediaRack 全部交付物。
#
#   ./build.sh                    # 全部：桌面壳 + Windows 客户端 + 服务端 + Android APK
#   ./build.sh desktop            # 只发桌面壳
#   ./build.sh client             # 只发 Windows 客户端
#   ./build.sh server             # 只发服务端（win-x64）
#   ./build.sh android            # 只发 Android APK
#   ./build.sh android desktop    # 可以给多个，按给出的顺序执行
#
# 产物都落在 publish/：
#   MediaRack-win-x64/          MediaRack.exe          桌面壳：托盘 + 配置向导 + 服务端跑在同一进程
#   MediaRack.Client-win-x64/   MediaRack.Client.exe   Windows 客户端：Avalonia + libvlc（+ libvlc/ 目录）
#   MediaRack-server-win-x64/   mediarack-server.exe   无界面服务端：给 NAS / 服务器 / 只跑服务的人
#   MediaRack-android/          MediaRack-<ver>.apk    Android 客户端（debug 签名，可侧载覆盖升级）
#   同名的 .zip 是拷贝分发用的（APK 本身已经是压缩包，不套 zip）。
#
# 实现不在这里：
#   桌面 / 客户端 / 服务端 -> scripts/release/publish.sh（唯一实现，纯 bash + dotnet CLI，跨平台）
#   Android                -> scripts/release/publish-android.sh（Gradle wrapper 8.10.2 + AGP 8.7.3）
# 本文件只固定"发哪几样 + 落到 publish/ 哪个目录"这层，免得两处参数各写一遍之后悄悄漂移。
#
# 平台约束：桌面壳是 net10.0-windows 的 WinForms + WPF，只能在 Windows 上构建；
# 客户端与服务端跨平台。只发服务端到 Linux 用（任意平台都能交叉发布）：
#   ./scripts/release/publish.sh --app server --runtime linux-x64 --zip

set -euo pipefail
cd "$(dirname "$0")"

TARGETS=("$@")
if [ ${#TARGETS[@]} -eq 0 ]; then
    TARGETS=(desktop client server android)
fi

for t in "${TARGETS[@]}"; do
    case "$t" in
        desktop|client|server|android) : ;;
        *)
            echo "未知目标：$t" >&2
            echo "可选：desktop / client / server / android（不给参数 = 全部）" >&2
            exit 2 ;;
    esac
done

for t in "${TARGETS[@]}"; do
    case "$t" in
        desktop) echo "▶ 桌面壳（托盘 + 服务端同进程）" ;;
        client)  echo "▶ Windows 客户端（Avalonia + libvlc）" ;;
        server)  echo "▶ 服务端（无界面，win-x64）" ;;
        android) echo "▶ Android APK" ;;
    esac
    echo "────────────────────────────────────────────────────"

    case "$t" in
        desktop) bash scripts/release/publish.sh --app desktop --zip --output publish/MediaRack-win-x64 ;;
        client)  bash scripts/release/publish.sh --app client  --zip --output publish/MediaRack.Client-win-x64 ;;
        server)  bash scripts/release/publish.sh --app server  --zip --runtime win-x64 --output publish/MediaRack-server-win-x64 ;;
        android) bash scripts/release/publish-android.sh ;;
    esac

    echo
done

# 汇总按 publish/ 里**实际存在**的东西列，不写死路径：
# publish.sh 在旧目录删不掉时会降级到带时间戳的新目录，写死的路径就成了假消息。
echo "════════════════════════════════════════════════════"
echo "publish/ 现有交付物："
FOUND=0
for f in publish/*.zip publish/*/*.exe publish/*/*.apk; do
    [ -e "$f" ] || continue
    FOUND=1
    printf "  %-46s %s\n" "$f" "$(du -h "$f" | cut -f1)"
done
[ "$FOUND" = 1 ] || echo "  （空）"

echo
echo "用法："
echo "  Windows：把 MediaRack-win-x64.zip 解压到任意电脑，双击 MediaRack.exe；"
echo "           客户端解压后整个目录一起用（exe + libvlc/ 缺一不可）。"
echo "  手机  ：adb install -r publish/MediaRack-android/*.apk，或把 apk 拷进手机点开安装。"
