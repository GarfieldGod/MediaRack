# MediaRack

多源视频库与播放服务。统一抽象是**源**：B 站缓存（双流 fMP4）与普通视频文件（单流）都是源，
PC 与手机共用一套库、一套协议。

> 设计（模型 / 不变量 / 接口 / 风险）→ [docs/design.md](docs/design.md)  
> 技术探针结论 → [docs/probes-report.md](docs/probes-report.md)  
> 写代码/注释/文档的规矩 → [docs/agent-rules.md](docs/agent-rules.md)  
> 契约（唯一源）→ [contracts/openapi.v1.yaml](contracts/openapi.v1.yaml)

**三条不可让渡的规则**

1. **磁盘永不可写** —— 源目录只读，改名/收藏/隐藏全在 SQLite 里
2. **源独立** —— 条目以 `(source_id, key)` 标识，去重只发生在「全部视频」聚合视图
3. **离线只读** —— 写操作一律要求在线

## 快速开始

```bash
dotnet build MediaRack.sln

# 环境自检（ffmpeg / 库 / 源）
dotnet run --project src/MediaRack.Cli -- doctor --meta <库目录>

# 扫一个 B 站缓存镜像（双流，带 md5 校验）
dotnet run --project src/MediaRack.Cli -- scan --root <视频目录> --source bili --verify full --meta <库目录>

# 扫一个普通视频目录（单流，子目录自动成为合集）
dotnet run --project src/MediaRack.Cli -- scan --root <视频目录> --meta <库目录>

# 起服务：Web 管理台 + 播放（默认 http://localhost:8080）
dotnet run --project src/MediaRack.Server -- --meta <库目录>
```

- `--layout auto|dual|single`，默认 `auto`：有 `entry.json` 判 dual，否则 single
- `--verify` 三档，**仅对 dual 有效**（单流没有 md5 清单）：

| 值 | 做什么 | 什么时候用 |
|---|---|---|
| `none` | 只查文件存在 | 首次全库建索引，最快 |
| `sample` | 加字节数比对（默认） | 日常增量 |
| `full` | 加 md5 全量比对 | 体检、排障、确认备份完整 |

### 常用 CLI

```bash
# 补缩略图（分批跑，避免一次跑几小时）
mrack posters --limit 200
# 看库
mrack ls --limit 20
mrack db info
# 虚拟管理（只写库，磁盘一个字节都不动）
mrack meta title <key> "我的新标题"
mrack meta tag   <key> --add 风景,4K
mrack meta hide  <key>
mrack collect new 精选
mrack collect add 精选 <key>
# 证明源目录只读（操作前后全量快照 diff 必须为空）
mrack verify-readonly --source bili --hash
```

完整命令：`mrack help`。

## 产物

一条命令出齐四件：

```bash
./build.sh                    # 全部
./build.sh desktop            # 只发某几件：desktop / client / server / android
./build.sh android desktop    # 也可以给多个，按顺序执行
```

| 产物 | 文件 | 给谁用 |
|---|---|---|
| **桌面壳** | `publish/MediaRack-win-x64/MediaRack.exe` + zip（85 MB / 80 MB） | **推荐入口**：托盘 + 配置向导 + 服务端同进程 |
| Windows 客户端 | `publish/MediaRack.Client-win-x64/`（exe + `libvlc/`，zip 91 MB） | 桌面播放器（Avalonia + libvlc） |
| 服务端 | `publish/MediaRack-server-win-x64/mediarack-server.exe` + zip | 无界面：NAS / 服务器 / 只要 HTTP 管理台 |
| Android | `publish/MediaRack-android/MediaRack-<version>.apk` | 手机客户端，`adb install -r` 或拷进手机点开 |

- Windows 三个包都是**自包含单文件 + 扁平 zip**：解压后根目录就是 exe，拷到任意 Windows 电脑双击即可，
  目标机不需要装 .NET
- 客户端必须**整目录**一起用——`libvlc/` 是被本机加载的 dll，塞不进单文件
- APK 是 **debug 签名**，与开发期 adb 装的包同一证书，可 `-r` 覆盖升级（release 需要 keystore，密钥不进仓库）
- 前提：bash（Windows 上用 Git Bash）+ dotnet 10；Android 另需 JDK 21 与 Android SDK
  （在 `src/MediaRack.Android/local.properties` 里写 `sdk.dir`；该文件按机器而异，不入库）

**发布细节**（改脚本前先看）：

- 唯一实现是 `scripts/release/publish.sh`（纯 bash + dotnet CLI）与 `scripts/release/publish-android.sh`（Gradle wrapper），
  根 `build.sh` 只是分发器。仓库里没有 `.cmd` / `.ps1` 构建脚本。
- 客户端三处特殊处理不能随手改回去：`KeepLibVlcOutsideBundle`（libvlc 是 Content，塞进单文件会让播放器
  找不到 `libvlc.dll`）、`TrimPublishOutput`（SkiaSharp/HarfBuzzSharp 的原生 `.pdb` 有 101 MB）、
  `VlcWindows*Enabled`（Windows 包带三套架构共 300 MB，按 RID 只收一套）。
- 跨平台：服务端与客户端任意平台可发（含交叉发布）；**桌面壳只能在 Windows 上发**（WinForms + WPF）。

## 服务端

浏览器打开 `http://localhost:8080` 就能管库：加源、扫描、搜索、改名、标签、隐藏、收藏夹、播放。
页面是编译进程序集的（`wwwroot` 三个文件），发布后**只拷 exe 也能用**。

```bash
mediarack-server.exe --meta <库目录> --urls http://0.0.0.0:8080 --open
```

| 参数 | 说明 |
|---|---|
| `--meta` | 库目录；不指定则用 `%LOCALAPPDATA%\MediaRack` |
| `--urls` | 监听地址；写 `0.0.0.0` 才能被局域网访问，缺省只绑 localhost |
| `--open` | 启动后顺手打开浏览器 |
| `--token` | 访问令牌；配了之后除静态页与健康检查外都要鉴权 |

### 访问边界（容易踩）

| 入口 | 谁可以打开 |
|---|---|
| `/` 管理控制台 | **仅本机**。别的电脑打开会拿到 `403 loopback_only` |
| `/s/<源>/` 单源库页 | 局域网 / 手机可达（带令牌） |

管理台能改库，按设计不对外。**手机和局域网电脑要连的是库页地址**（管理台里每个源的「访问方式」
列出全部地址与二维码）。首次启动在 Windows 防火墙弹窗里勾选「专用网络」。

### 三种播放路线

浏览器 `<video>` 一个标签只能吃一条流，而 B 站缓存是音视频分开的两个文件，所以有三条路：

| mode | 谁用 | 服务端做什么 | 代价 |
|---|---|---|---|
| `direct` | Android / PC 客户端 | 307 到详情，客户端取两条 URL 自己合成 | **零 CPU** |
| `remux` | 浏览器、VLC、投屏 | `ffmpeg -c copy` 转封装成单流 mp4，落 `export/` 复用 | 首次几百 ms 磁盘 IO |
| `transcode` | 弱网 | 重新编码 | 未实现，返回 409 |

### 鉴权

不配令牌 = 不鉴权（局域网自用的默认）。配了之后三路凭据都认：`Authorization: Bearer`（客户端/脚本）、
Cookie（浏览器自己发的图片与视频请求加不了头）、`?token=`（VLC / 投屏只能吃一个 URL）。
页面在配了令牌时自动弹登录框，cookie 30 天，过期静默续期。

## Android 端

```bash
./build.sh android                              # → publish/MediaRack-android/*.apk
adb install -r publish/MediaRack-android/*.apk

# 手动构建（仓库自带 Gradle wrapper，不要求本机装 gradle）
(cd src/MediaRack.Android && ./gradlew assembleDebug)

# 装真实样本进 App 私有目录（内部存储），然后 App 里「添加源」
scripts/devtools/android-push-samples.sh                 # --dry-run 可先看要推哪几个文件
```

- **本地源根是内部存储** `files/sources`。不能用外部 `Android/data`：Android 16 / ColorOS 会把
  adb 创建的文件归属 shell uid，应用自己都看不见
- **远程源**：源页「服务器」Tab → 手输地址或**扫配对二维码**（管理台「配对手机」按钮）→
  浏览聚合库 → single 源直连、**dual 源客户端合成**（`MergingMediaSource`）
- 服务器不可达时条目**变灰不消失**（缓存降级）
- **下载到本地**：条目行尾 ⬇，断点续传；仅 Wi-Fi（流量网络自动挂起 + 常显提示，可会话级放行）；
  落盘布局与 PC 缓存逐字段对齐，目录拷到 PC 可直接当源扫描；播放**本地优先、远程兜底**

## PC 客户端

独立于服务端的桌面播放器：一条 base URL + 一个令牌 = 一台服务端。本质是「横屏版、没有推荐流的 APK」，
热点键鼠化的手势改为一整套快捷键。

```bash
dotnet run --project src/MediaRack.Client
dotnet bin/Debug/net10.0/MediaRack.Client.dll --selftest [baseUrl]   # 协议层自检
dotnet bin/Debug/net10.0/MediaRack.Client.dll --playtest [baseUrl]   # 播放链路自检
```

连接配置在 `%LOCALAPPDATA%\MediaRack\client-servers.json`，日志在 `logs/client.log`。
**不引 Core**：只照契约说话，与 APK 同一套协议。

## 校验

```bash
dotnet test MediaRack.sln                        # 221 项（Core/CLI 117 + Server 104）
python scripts/check/check_contract.py           # 契约自检：27 端点、$ref 全解析（需 pyyaml）
python scripts/check/check_hygiene.py            # 文档与注释卫生：真机路径、内部编号、死链
python scripts/check/e2e_smoke.py --base http://127.0.0.1:8080 --root <视频目录> --source bili
```

卫生检查的规则与清理办法见 [docs/agent-rules.md](docs/agent-rules.md)。

端到端验收会对运行中的服务跑全链路，最后比对源目录 md5。服务端开了令牌时加 `--token`，
脚本会先自证「无凭据确实被拒」。

## 目录结构

```
MediaRack.sln
build.sh                       一键发布（分发器，实现在 scripts/release/）
Directory.Packages.props       NuGet 版本集中在此（CPM），csproj 只写包名
├── src/
│   ├── MediaRack.Core/        模型 / 扫描 / 库 / 虚拟层 / SQLite（含 Schema/）
│   ├── MediaRack.Server/      Minimal API、Range 流、remux、内嵌 Web 页（wwwroot/）
│   ├── MediaRack.Cli/         mrack 运维命令行（直接读库，不经 HTTP）
│   ├── MediaRack.Client/      PC 客户端（Avalonia + libvlc）
│   ├── MediaRack.Server.Desktop/  MediaRack.exe（托盘壳 + 内嵌 Kestrel）
│   └── MediaRack.Android/     Android 客户端（Kotlin + Compose + Room + Media3）
├── tests/                     MediaRack.Tests / MediaRack.Server.Tests
├── contracts/openapi.v1.yaml  三端唯一契约源
├── docs/                      design.md / probes-report.md / agent-rules.md
└── scripts/
    ├── release/               出交付物：publish.sh / publish-android.sh / pack.cs
    ├── check/                 退出码判定：check_contract / check_hygiene / e2e_smoke
    └── devtools/              人工走查：screenshot / click / sendkey / android-push-samples
```

## 状态

三端可用：PC 客户端、Android 客户端、服务端 + Web 管理台。已完成：双流/单流扫描、
播放交付（直读 / remux）、虚拟元数据与收藏、服务端合集、网络源与断网降级、
手动下载到本地（断点续传 + Wi-Fi 闸门）。未做：转码、公网中继。

验收基线：`dotnet build` 0 warning / 0 error，`dotnet test` 221 项全过，契约 27 端点自检通过。
