#!/usr/bin/env bash
# 把服务端 / 桌面壳打成自包含单文件。目标：拷到任意机器就能跑，不需要装 .NET 运行时。
#
# 这是发布的**唯一实现**，纯 bash + dotnet CLI，Windows（Git Bash）与 Linux / macOS 通用。
# 根目录的 build.sh 只是固定了"桌面壳 + 出 zip + 输出到 publish/"这组参数的薄壳。
#
# 两个必须保留的 dotnet publish 参数，去掉任意一个都会在运行时炸：
#   IncludeNativeLibrariesForSelfExtract  —— Microsoft.Data.Sqlite 带 e_sqlite3 原生库，
#                                            不塞进单文件的话启动就报找不到 SQLite。
#   不开 PublishTrimmed                   —— ASP.NET Core 的 Minimal API 与 JSON 序列化重度依赖
#                                            反射，裁剪会把它们当死代码删掉，表现为运行期 500。
#
# 平台约束（这条不是脚本能绕过的）：
#   桌面壳 MediaRack.Server.Desktop 是 net10.0-windows + WinForms + WPF，
#   **只能在 Windows 上构建**。在 Linux 上发布桌面壳会得到 NETSDK1100，脚本会提前拦下来。
#   服务端 MediaRack.Server 是 net10.0，无平台依赖，可以在任意平台交叉发布到 win/linux/osx。
#
# 用法：
#   scripts/release/publish.sh                                  -> 服务端，按宿主平台 -> dist/<rid>/
#   scripts/release/publish.sh --app desktop --zip --output publish/MediaRack-win-x64
#   scripts/release/publish.sh --app client  --zip --output publish/MediaRack.Client-win-x64
#   scripts/release/publish.sh --app server --runtime linux-x64 --zip     （交叉发布，Windows 上也能出 Linux 包）
#   scripts/release/publish.sh --help

set -euo pipefail
cd "$(dirname "$0")/../.."
ROOT=$(pwd)

APP=server
RUNTIME=""
OUTPUT=""
DO_ZIP=0

usage() {
    cat <<'EOF'
用法：scripts/release/publish.sh [选项]

参数：
  --app server|desktop|client
                          发布哪个程序（默认 server）
                            server  = mediarack-server（控制台，跨平台）
                            desktop = MediaRack.exe（Windows 托盘壳，仅 Windows）
                            client  = MediaRack.Client（Avalonia 客户端，跨平台）
  --runtime <RID>         目标运行时，如 win-x64 / linux-x64 / linux-arm64 / osx-arm64
                          缺省 = 宿主平台；--app desktop 缺省 win-x64
  --output <目录>         输出目录（缺省 dist/<rid>[-desktop|-client]）
  -z, --zip               同时打出 <输出目录>.zip
  -h, --help              显示本帮助
EOF
}

while [ $# -gt 0 ]; do
    case "$1" in
        --app)      APP=${2:?--app 需要值}; shift 2 ;;
        --app=*)    APP=${1#*=}; shift ;;
        --runtime)  RUNTIME=${2:?--runtime 需要值}; shift 2 ;;
        --runtime=*) RUNTIME=${1#*=}; shift ;;
        --output)   OUTPUT=${2:?--output 需要值}; shift 2 ;;
        --output=*) OUTPUT=${1#*=}; shift ;;
        -z|--zip)   DO_ZIP=1; shift ;;
        -h|--help)  usage; exit 0 ;;
        *) echo "未知参数: $1（--help 看用法）" >&2; exit 2 ;;
    esac
done

# ---- 宿主平台（决定默认 RID）-------------------------------------------------
# uname 在 Git Bash / MSYS / Cygwin 下返回 MINGW64_NT-* / MSYS_NT-* / CYGWIN_NT-*，
# 在 Linux / macOS 下返回 Linux / Darwin。架构只在 Linux/macOS 上需要区分。
host_rid() {
    local os arch
    os=$(uname -s)
    arch=$(uname -m)
    case "$os" in
        MINGW*|MSYS*|CYGWIN*) echo "win-x64" ;;
        Linux)
            case "$arch" in
                x86_64|amd64) echo "linux-x64" ;;
                aarch64|arm64) echo "linux-arm64" ;;
                *) echo "linux-$arch" ;;
            esac ;;
        Darwin)
            case "$arch" in
                arm64) echo "osx-arm64" ;;
                *) echo "osx-x64" ;;
            esac ;;
        *) echo "win-x64" ;;
    esac
}

# ---- 目标程序 ----------------------------------------------------------------
case "$APP" in
    server)
        PROJECT="src/MediaRack.Server/MediaRack.Server.csproj"
        EXE_BASE="mediarack-server"
        DEFAULT_OUT_SUFFIX=""
        ;;
    desktop)
        PROJECT="src/MediaRack.Server.Desktop/MediaRack.Server.Desktop.csproj"
        EXE_BASE="MediaRack"
        DEFAULT_OUT_SUFFIX="-desktop"
        ;;
    client)
        PROJECT="src/MediaRack.Client/MediaRack.Client.csproj"
        EXE_BASE="MediaRack.Client"
        DEFAULT_OUT_SUFFIX="-client"
        ;;
    *)
        echo "不支持的 --app '$APP'（可选 server / desktop / client）" >&2
        exit 2
        ;;
esac

[ -n "$RUNTIME" ] || RUNTIME=$(host_rid)

# Windows 目标带 .exe，Linux/macOS 不带。这不是为了好看：zip 里多一个
# mediarack-server.exe 会让 Linux 用户以为包发错了。
case "$RUNTIME" in
    win-*) EXE="$EXE_BASE.exe" ;;
    *)     EXE="$EXE_BASE" ;;
esac

# ---- 平台约束校验 ------------------------------------------------------------
# 桌面壳的 TFM 是 net10.0-windows。dotnet 自己也会报 NETSDK1100，但那条错误是英文的、
# 而且在几十行构建日志之后才出现；这里提前拦住，直接说清楚"该换个什么命令"。
if [ "$APP" = desktop ]; then
    case "$RUNTIME" in
        win-*) : ;;
        *)
            cat >&2 <<EOF
错误：桌面壳（MediaRack.Server.Desktop）是 net10.0-windows 的 WinForms 程序，
      只能在 Windows 上构建，无法发布到 $RUNTIME。

      在非 Windows 机器上，能发布的是服务端（跨平台）：
        ./scripts/release/publish.sh --app server --runtime $RUNTIME --zip

      想让 Windows 用户用到桌面壳，请在 Windows 上执行：
        ./scripts/release/publish.sh --app desktop --zip
EOF
            exit 1 ;;
    esac
    # 注意：这里必须用 case，不能写 [ "$(host_rid)" != win-* ]。
    # 单方括号的 != 只做字符串比较，右侧的 win-* 不会被当通配符展开，
    # 结果 "win-x64" != "win-*" 恒为真 —— 连 Windows 宿主都会被拦下来（已踩）。
    case "$(host_rid)" in
        win-*) : ;;
        *)
            echo "错误：--app desktop 需要在 Windows 上执行（当前宿主：$(uname -s)）" >&2
            exit 1 ;;
    esac
fi

[ -n "$OUTPUT" ] || OUTPUT="dist/$RUNTIME$DEFAULT_OUT_SUFFIX"

# 转成绝对路径：dotnet publish -o 与 zip 都按**进程**当前目录解析相对路径，
# 脚本中途不会 cd，但把这一层钉死可以避免调用方从别的目录传相对路径时踩坑。
case "$OUTPUT" in
    /*|[A-Za-z]:*) : ;;
    *) OUTPUT="$ROOT/$OUTPUT" ;;
esac

# ---- 打 zip：按可用性降级 ----------------------------------------------------
# 顺序的由来（都实测过）：
#   1. zip        —— Linux 上常见，最快；Git for Windows **不自带**
#   2. pack.cs    —— 只要装了 dotnet 就一定可用（发布本身就要它），file-based app，约 2 秒
# 故意不用 tar -a：GNU tar 不认 zip 格式，会把 tar 内容写进 .zip 文件名，
# 拿到的包 unzip 直接报 "End-of-central-directory signature not found"。
make_zip() {
    local dir=$1 out=$2
    rm -f "$out"
    if command -v zip >/dev/null 2>&1; then
        ( cd "$dir" && zip -qr "$out" . )
    else
        dotnet run "$ROOT/scripts/release/pack.cs" -- "$dir" "$out"
    fi
}

# ---- 客户端：libvlc 只留目标架构 ----------------------------------------------
# VideoLAN.LibVLC.Windows 一个包同时带 win-x64 / win-x86 / win-arm64 三套原生库
# （实测 108 MB + 104 MB + 91 MB）。libvlc 是**内容文件**不是程序集，单文件不会把它
# 塞进 bundle，而是原样铺在 exe 旁边 —— 三套全留着，包体就多背约 195 MB。
#
# 客户端的 PlaybackEngine 只探 `{exe 目录}/libvlc/{当前 RID}/`，别名的目录永远用不到，
# 所以按目标 RID 只留一套是安全的。
#
# **正常情况下这里没有东西可删**：MediaRack.Client.csproj 已经用包自带的
# VlcWindows*Enabled 开关在收集阶段只收目标架构（2026-10-27 改，见那里的注释）。
# 留着这段是兜底——万一包换了版本、开关失效，还能在发布后补救。
# 反过来，这段删不掉时**不要**当成正常：那会把 184 MB 的包当成品发出去。
#
# 保守起见：只有确认 `libvlc/<当前 RID>` 真实存在才动手删，否则原样保留并提示。
# （万一哪天包布局变了，宁可包大一点，也不能删成一个跑不起来的客户端。）
trim_libvlc() {
    local out=$1 rid=$2
    [ -d "$out/libvlc" ] || return 0
    if [ ! -d "$out/libvlc/$rid" ]; then
        echo "提示：$out/libvlc 里没有 $rid 目录，跳过裁剪（包布局可能变了，全部保留）" >&2
        ls -1 "$out/libvlc" >&2
        return 0
    fi

    local before after d name
    before=$(du -sm "$out/libvlc" | cut -f1)
    for d in "$out/libvlc"/*/ ; do
        [ -d "$d" ] || continue
        name=$(basename "$d")
        [ "$name" = "$rid" ] && continue
        echo "  裁剪  libvlc/$name  $(du -sh "$d" | cut -f1)（目标架构是 $rid）"
        # 删不掉不算失败：多留一份用不到的架构只是包大一点，不该让整次发布中断。
        rm -rf "$d" || echo "  警告：删不掉 $d（可能被杀软占用），已保留" >&2
    done
    after=$(du -sm "$out/libvlc" | cut -f1)
    [ "$before" -gt "$after" ] && echo "  libvlc  ${before} MB -> ${after} MB"
    return 0
}

# 使用说明跟着包一起发：拿到 zip 的人不该再去找文档。
# 编码用无 BOM UTF-8 —— Windows 10 之后记事本、VS Code、cat 都认；
# 带 BOM 反而会让 Linux 上的 cat / less 在开头显示一个乱码方块。
write_usage() {
    local exe=$1
    if [ "$APP" = desktop ]; then
        cat > "$OUTPUT/使用说明.txt" <<EOF
MediaRack 使用说明
==================

运行
----
双击 $exe。首次运行弹出配置向导：
  1. 库目录：放数据库、封面、导出产物（默认 %LOCALAPPDATA%\MediaRack）
  2. 服务：端口（默认 8080）与密码（留空 = 不鉴权，仅限可信局域网）
  3. 源文件夹：视频目录，可添加多个
点「保存并启动」后自动扫描一遍源，之后常驻系统托盘。
双击托盘图标，或右键「打开管理页」。

手机配对
--------
管理页里选中源 -> 出示二维码 -> Android 客户端扫码。
二维码用的是浏览器当前地址：用局域网 IP 打开管理页，手机就连局域网。

局域网访问
----------
服务监听 0.0.0.0（所有网卡），但**管理控制台（根路径 /）只接受本机访问**：
别的电脑打开 http://<本机IP>:<端口>/ 会得到 403 loopback_only —— 这是有意设计，
管理页不对外。局域网电脑 / 手机请走库页（带令牌）：
  http://<本机IP>:<端口>/s/<源地址>/
管理页里每个源的「访问方式」列出本机全部地址与配对二维码，复制即可。
首次启动时 Windows 防火墙会弹窗，请勾选「专用网络」并允许。
端口被占用就换一个（托盘右键 -> 配置服务...）。

随身模式
--------
exe 同目录放一个 mediarack.config.json 即可覆盖配置（便携部署）；
不放则使用 %LOCALAPPDATA%\MediaRack\config.json。

ffmpeg（可选）
--------------
浏览器 remux 转封装、封面抽帧、single 源探测需要 ffmpeg：
装进 PATH，或用环境变量 MEDIARACK_FFMPEG / MEDIARACK_FFPROBE 指定。
不装也能扫描、建库、播放（Android 客户端直接解 m4s 双流）。

日志：%LOCALAPPDATA%\MediaRack\logs\desktop.log
EOF
    elif [ "$APP" = client ]; then
        cat > "$OUTPUT/使用说明.txt" <<EOF
MediaRack 客户端使用说明
========================

运行
----
双击 $exe（Linux/macOS 的包没有 .exe 后缀，直接 ./MediaRack.Client）。
首次启动在连接页填服务器地址 —— **填的是源库页地址，不是根地址**：
  http://<服务器IP>:<端口>/s/<源地址>/
（根地址 / 是服务器本机的管理控制台，别的机器打开是 403 loopback_only。）
最省事：把管理页里那个源的配对码（MR1|... 或带 #token= 的地址）粘进连接页，
地址与令牌一次带进来；来源就是手机端扫的那个二维码。

这个目录里必须有的东西
----------------------
  $exe          主程序（托管代码都在里面）
  libvlc/       播放引擎的原生库（约 $LIVLC_SIZE）

libvlc 目录**不能删、不能移走**。它是被本机加载的 dll，塞不进单文件，
所以只能跟 exe 放一起。缺了它程序照常启动、能浏览、能搜索，
只有"播放"不可用，界面上会提示原因。

ffmpeg
------
不需要。客户端自己用 libvlc 合成 video.m4s + audio.m4s 双流播放，
不依赖服务端 remux，也不依赖本机装 ffmpeg。

日志：%LOCALAPPDATA%\MediaRack\logs\client.log
EOF
    else
        cat > "$OUTPUT/使用说明.txt" <<EOF
MediaRack 服务端使用说明
========================

运行
----
chmod +x $exe            # 解压后先做这一步（原因见文末"关于可执行位"）
./$exe --meta /srv/MediaRack --urls http://0.0.0.0:8080 --open
  --meta   库目录（数据库、封面、导出产物）；缺省 ~/.local/share/MediaRack
  --urls   监听地址。写 http://0.0.0.0:8080 才能被局域网访问；
           缺省只绑 localhost:5000，那样只有本机打得开
           （局域网打开的是库页 /s/<源地址>/；根路径 / 是管理台，只给本机，外部请求 403）
  --open   启动后顺手打开浏览器
  --token  访问令牌；配了之后除静态页与健康检查外都要 Bearer 鉴权

管理页（http://localhost:8080）：加源、扫描、搜索、改名、标签、隐藏、收藏夹、播放。

ffmpeg（可选）
--------------
浏览器 remux 转封装、封面抽帧、single 源探测需要 ffmpeg：
装进 PATH，或用环境变量 MEDIARACK_FFMPEG / MEDIARACK_FFPROBE 指定。
不装也能扫描、建库、播放（Android 客户端直接解 m4s 双流）。

关于可执行位
------------
这个包是在 Windows 上交叉发布的，NTFS 没有 unix 权限位、.NET 的 zip 也不写它，
所以解压出来的 mediarack-server 不带 x 位，必须自己 chmod +x 一次。
若你的解压工具保留了权限位（在 Linux 本机发布、或用 unzip），可跳过。
EOF
    fi
}

# ---- 主流程 ------------------------------------------------------------------
echo "发布 $PROJECT"
echo "  运行时    $RUNTIME"
echo "  输出目录  $OUTPUT"
echo "  宿主      $(uname -s) / $(uname -m)"
echo

# dotnet publish -o 不清输出目录：上一次的 dll、pdb 会原样留着，跟单文件产物混在一起，
# 拿到包的人会以为"要连这些 dll 一起拷"。先整个删掉再发——这是构建产物目录，删它没有代价。
#
# 删不掉时不要中断发布：换带时间戳的新目录继续（宁可多一个目录，也不要"一半旧一半新"）。
# 实测这是真会发生的：刚发布的 221 MB exe 还在被 Windows Defender 实时扫描时，
# 紧接着再发一次就会撞上共享冲突，几十秒后又能删了。所以：
#   1. 把 rm 的错误**显示出来**，不吞（吞掉就查不出为什么降级）
#   2. 记下旧目录，等发布跑完再删一次——那时扫描早就结束，绝大多数情况都能删掉
# 先清掉之前降级留下的 `名字-YYYYMMDD-HHMMSS` 目录。
# 为什么放在这里：那些目录当时删不掉，是因为刚生成的 221MB exe 正被 Windows Defender
# 实时扫描（实测要 1~2 分钟才放手），而发布结束时的重试只隔了几秒，赶不上。
# 等到下一次发布时锁早就没了，所以在这里扫一遍，让 publish/ 自动收敛回一个目录。
for stale in "$OUTPUT"-[0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9]-[0-9][0-9][0-9][0-9][0-9][0-9] ; do
    [ -e "$stale" ] || continue
    if rm -rf "$stale"; then
        echo "清理上次降级留下的目录 $stale"
        rm -f "$stale.zip"
    fi
done

STALE=""
if [ -e "$OUTPUT" ]; then
    echo "清理旧产物 $OUTPUT"
    if ! rm -rf "$OUTPUT"; then
        STAMP=$(date +%Y%m%d-%H%M%S)
        STALE="$OUTPUT"
        OUTPUT="$OUTPUT-$STAMP"
        echo "警告：清不掉旧产物，改用新目录 $OUTPUT" >&2
        echo "      常见原因：上一次发布的 MediaRack 还在运行（托盘没退出），" >&2
        echo "      或杀软正在扫描那个 exe（通常 1~2 分钟）。下次发布会自动清掉本目录。" >&2
        rm -rf "$OUTPUT"
    fi
fi

dotnet publish "$PROJECT" \
    -c Release \
    -r "$RUNTIME" \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:EnableCompressionInSingleFile=true \
    -p:DebugType=none \
    -p:IsTransformWebConfigDisabled=true \
    -p:StaticWebAssetsEnabled=false \
    -o "$OUTPUT" \
    --nologo

EXE_PATH="$OUTPUT/$EXE"
if [ ! -f "$EXE_PATH" ]; then
    echo "错误：没有找到可执行文件 $EXE_PATH" >&2
    echo "      目录内容：" >&2
    ls -1 "$OUTPUT" >&2
    exit 1
fi

# 服务端在 Linux 上要可执行。这一步在 Windows 宿主上做不出效果（NTFS 没有 unix 权限位，
# Cygwin 的 chmod 只改模拟属性、不进 zip），所以在 Windows 上交叉发布时它基本是空操作；
# 真正有用的是"在 Linux 本机发布"这条路径。交叉发布的包里必须靠 chmod +x，
# 使用说明里已写明（zip 的 unix 权限位是 0，.NET 不写这一项）。
case "$RUNTIME" in
    win-*) : ;;
    *) chmod +x "$EXE_PATH" 2>/dev/null || true ;;
esac

# 客户端要额外裁掉别的架构的 libvlc（见 trim_libvlc 注释）。
LIVLC_SIZE=""
if [ "$APP" = client ]; then
    echo
    echo "裁剪 libvlc 到目标架构 $RUNTIME"
    trim_libvlc "$OUTPUT" "$RUNTIME"
    if [ -d "$OUTPUT/libvlc" ]; then
        LIVLC_SIZE=$(du -sh "$OUTPUT/libvlc" | cut -f1)
    fi
fi

SIZE=$(du -h "$EXE_PATH" | cut -f1)
echo
echo "完成  $EXE_PATH  $SIZE"
if [ "$APP" = client ]; then
    echo "      整包 $(du -sh "$OUTPUT" | cut -f1)（exe + libvlc $LIVLC_SIZE）"
fi

if [ "$DO_ZIP" = 1 ]; then
    write_usage "$EXE"
    ZIP_PATH="$OUTPUT.zip"
    echo "打包  $ZIP_PATH"
    make_zip "$OUTPUT" "$ZIP_PATH"
    echo "分发包  $ZIP_PATH  $(du -h "$ZIP_PATH" | cut -f1)"
fi

if [ "$APP" = server ]; then
    echo
    echo "第一次运行会在 --meta 指定目录建库，示例："
    echo "  ./$EXE --meta /srv/MediaRack --urls http://0.0.0.0:8080 --open"
    echo "  （--urls 必须写 0.0.0.0 才能被局域网访问；缺省只绑 localhost:5000）"
fi

if [ "$APP" = client ]; then
    echo
    echo "分发时整个 $OUTPUT/ 一起给（exe + libvlc/，缺一不可）。"
    echo "启动后在设置里填服务器地址 http://<服务器IP>:8080。"
fi

# 回到开头那个降级分支：发布这几分钟里 dotnet 已退出、杀软扫描也结束了，
# 现在再删一次，publish/ 下就不会攒一堆带时间戳的目录。清不掉也不影响本次产物。
if [ -n "$STALE" ] && [ -e "$STALE" ]; then
    echo
    if rm -rf "$STALE" 2>/dev/null; then
        echo "已清理降级前的旧目录 $STALE"
    else
        echo "旧目录仍被占用（多半还在被扫描），保留在原处：$STALE" >&2
        echo "  下次发布时会自动清理；想立刻删就先退出正在运行的 MediaRack。" >&2
    fi
fi
