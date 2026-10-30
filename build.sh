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
# 产物都落在 publish/，**文件夹名与里面的 exe 同名同大小写**（一套四件，一眼能对上）：
#   MediaRack-Server-Desktop-Windows-x64/  MediaRack-Server-Desktop.exe  托盘 + 配置向导 + 服务端同进程
#   MediaRack-Client-Windows-x64/          MediaRack-Client.exe          Avalonia + libvlc（+ libvlc/ 目录）
#   MediaRack-Server-Windows-x64/          MediaRack-Server.exe          无界面服务端：给 NAS / 服务器
#   MediaRack-Client-Android/              MediaRack-Client.apk          手机客户端（debug 签名，可覆盖升级）
# 除客户端外都是**单文件**（客户端必须带 libvlc/），所以只有客户端出 zip，其余直接给 exe/apk。
# 包里不放使用说明.txt：说明文字只在 README 与发布页正文里，避免两份文档互相矛盾。
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

    # 只有客户端需要打 zip：单个 exe 再套一层压缩纯属多一道解压步骤，
    # 发布页上直接给 exe 更像"双击就能用"。
    case "$t" in
        desktop) bash scripts/release/publish.sh --app desktop --output publish/MediaRack-Server-Desktop-Windows-x64 ;;
        client)  bash scripts/release/publish.sh --app client  --zip --output publish/MediaRack-Client-Windows-x64 ;;
        server)  bash scripts/release/publish.sh --app server  --runtime win-x64 --output publish/MediaRack-Server-Windows-x64 ;;
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
echo "  桌面壳：拿 publish/MediaRack-Server-Desktop-Windows-x64/MediaRack-Server-Desktop.exe，"
echo "          单个 exe 双击即用（配置与库在 %LOCALAPPDATA%\\MediaRack）。"
echo "  服务端：拿 publish/MediaRack-Server-Windows-x64/MediaRack-Server.exe，同样单文件。"
echo "  客户端：publish/MediaRack-Client-Windows-x64/ 整个目录一起拷（exe + libvlc/ 缺一不可），"
echo "          发布用那份 zip 就是整个目录。"
echo "  手机  ：adb install -r publish/MediaRack-Client-Android/MediaRack-Client.apk，或拷进手机点开安装。"
