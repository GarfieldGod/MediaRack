#!/usr/bin/env bash
# 把 Android 端打成可侧载的 APK，落到 publish/MediaRack-Client-Android/MediaRack-Client.apk。
#
# 为什么是 debug 而不是 release：
#   release 需要签名密钥，而密钥不该进仓库（进了仓库等于公开）。没有 signingConfig 时
#   AGP 只会产出 app-release-unsigned.apk —— 那个文件手机装不上（"应用未安装"）。
#   debug 签名是固定的调试证书，同一台机器上每次构建签名一致，所以能覆盖升级自己。
#   将来要上架/长期分发，再把 keystore 路径与口令走环境变量注进来，这里加一个 --release。
#
# 产物名固定（不带版本号），与同批交付物同一套命名：文件夹 MediaRack-Client-Android，
# 里面就是 MediaRack-Client.apk。版本号打在发布页的 tag 上，文件名保持稳定——
# 下载、侧载、CI 上传三处都只需要记一个名字。当前版本仍会打印在构建日志里。
#
# 用法：
#   scripts/release/publish-android.sh              # 增量构建（日常）
#   scripts/release/publish-android.sh --clean      # 先 clean 再构建（改过 gradle 脚本 / 依赖时）
#
# 依赖：JDK 21 + Android SDK（路径在 src/MediaRack.Android/local.properties 的 sdk.dir）。
# 仓库自带 Gradle wrapper（8.10.2 + AGP 8.7.3），不需要本机装 gradle。

set -euo pipefail
cd "$(dirname "$0")/../.."
ROOT=$(pwd)

ANDROID_DIR="src/MediaRack.Android"
OUT_DIR="publish/MediaRack-Client-Android"

CLEAN=0
case "${1:-}" in
    --clean) CLEAN=1 ;;
    "") : ;;
    -h|--help) sed -n '2,20p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "未知参数: $1（--help 看用法）" >&2; exit 2 ;;
esac

[ -f "$ANDROID_DIR/app/build.gradle" ] || { echo "错误：找不到 $ANDROID_DIR/app/build.gradle" >&2; exit 1; }

# 版本号从 build.gradle 读，不在这里写死：写死就会跟 app/build.gradle 漂移。
# 用 sed 而非 grep -P —— BSD grep（macOS）不认 -P。
VERSION_NAME=$(sed -nE "s/.*versionName[[:space:]]+['\"]([^'\"]+)['\"].*/\1/p" "$ANDROID_DIR/app/build.gradle" | head -1)
VERSION_CODE=$(sed -nE "s/.*versionCode[[:space:]]+([0-9]+).*/\1/p" "$ANDROID_DIR/app/build.gradle" | head -1)
[ -n "$VERSION_NAME" ] || VERSION_NAME="unknown"

echo "构建 Android APK"
echo "  版本      $VERSION_NAME (versionCode ${VERSION_CODE:-?})"
echo "  输出目录  $OUT_DIR"
echo

# Windows 的 git 不记录可执行位，clone 下来 gradlew 可能没有 x 位。
chmod +x "$ANDROID_DIR/gradlew" 2>/dev/null || true

(
    cd "$ANDROID_DIR"
    if [ "$CLEAN" = 1 ]; then
        ./gradlew clean --console=plain
    fi
    ./gradlew assembleDebug --console=plain
)

SRC_APK="$ANDROID_DIR/app/build/outputs/apk/debug/app-debug.apk"
[ -f "$SRC_APK" ] || { echo "错误：构建成功但没有找到 $SRC_APK" >&2; exit 1; }

# 清旧产物删不掉时（杀软正在扫描 / 手机上还有进程在读）降级到带时间戳的目录，
# 与 publish.sh 同一套处理：宁可多一个目录，也不要"旧 apk 被当成新 apk 发出去"。
if [ -e "$OUT_DIR" ]; then
    echo
    echo "清理旧产物 $OUT_DIR"
    if ! rm -rf "$OUT_DIR"; then
        STAMP=$(date +%Y%m%d-%H%M%S)
        echo "警告：清不掉 $OUT_DIR，改用 $OUT_DIR-$STAMP" >&2
        OUT_DIR="$OUT_DIR-$STAMP"
    fi
fi
mkdir -p "$OUT_DIR" || { OUT_DIR="$OUT_DIR-$(date +%Y%m%d-%H%M%S)"; mkdir -p "$OUT_DIR"; }

DEST_APK="$OUT_DIR/MediaRack-Client.apk"
cp "$SRC_APK" "$DEST_APK"

echo
echo "完成  $DEST_APK  $(du -h "$DEST_APK" | cut -f1)"
echo
echo "装机（手机与电脑在同一网络 / USB 调试已开）："
echo "  adb install -r \"$DEST_APK\""
echo "  覆盖升级用的是 debug 签名，与开发期 adb 装的包是同一个证书，可直接 -r 覆盖。"
