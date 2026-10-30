# AGENTS.md

给 AI 编码代理的操作约定。人类读者看 [README.md](README.md)（怎么跑、怎么发布）与
[docs/design.md](docs/design.md)（当前设计）。本文件只写 Agent 会踩的坑：
三端构建、账目规则、Compose/libvlc 铁律、发布流水线、协作纪律。

## 项目一句话

多源视频库 + 播放器：PC 服务端（ASP.NET Core + WinForms 托盘）、Android 客户端
（Kotlin/Compose + media3）、PC 桌面客户端（Avalonia + libvlc，连远程服务端）。
m4s 双流是唯一真相。条目 key：dual 为 `<avid>_c<cid>`，single 为 `s_<sha1(relPath) 前 16 位>`。

## 三端构建与验证

- `src/` 六模块：Core / Server / Server.Desktop / Cli / Client / Android
- 服务端：`dotnet build src/MediaRack.Server.Desktop/MediaRack.Server.Desktop.csproj`；
  **验收必须 `--no-incremental`** —— 增量构建跳过未改项目，那些项目的警告根本看不见，
  「零警告」的账要用全量重编来对
- Android：`(cd src/MediaRack.Android && ./gradlew assembleDebug)`，
  Gradle 8.10.2 + AGP 8.7.3 + JDK 21，只依赖仓库自带 wrapper；
  生成 wrapper 挂代理时用 `--no-validate-url`（本机代理做 TLS MITM 会 PKIX 失败）
- 桌面客户端：`src/MediaRack.Client/`，纯 HTTP 不引 Core，照仓库根 `openapi.v1.yaml`；
  自检 `--selftest`（协议）、`--vmtest`（主页）、`--playtest`（播放），`--server <url>` 临时连指定端
- 调试端口 8080/8081；**`wwwroot` 是 EmbeddedResource** → 改前端必须 rebuild + 重启
- GUI 进程锁 dll → 验证 UI 前先停客户端进程，build 后必须 grep error 确认
- 跨平台边界：Server.Desktop 是 `net10.0-windows`（WinForms+WPF）不能在 Linux 构建；
  Server 是 `net10.0` 可交叉发布

## 账与扫描

- 指纹 `fp = SHA256(大小 + 首尾 64KB)`；FingerprintSync 命中 → CopyMeta 认领 +
  ClaimFavorites（**只复制，不删除**）。档案层跟内容走，位置层（media）是源级事实
- DDL 在 `src/MediaRack.Core/Store/Schema/`：多个 EmbeddedResource，`LogicalName` 写死成
  `MediaRack.Core.Store.schema.vN.sql` → **移动文件只改 Core.csproj，Schema.cs 一行不动**；
  改完必须 `dotnet test`（build 过不代表资源能读到）
- **聚合/计数必须过 `ServiceScope.VisibleSourceIds`**（漏了门户会显示全库）；
  `?source=` 只是前端参数，不是隔离边界
- 扫描：盘符闸门（每盘 1 / 全局 3）；mtime+size 命中跳 ffprobe；dual 校验 none/sample/full
- media3 必须 ≥1.9.0（1.5.1 的 hvcC bug 播不了 HEVC）；转封装兜底只对 isRemote 生效
- 删源语义：位置层总删、档案层默认保留（查询参数决定），删前先取消该源所有作业
- 继续观看的口径：列名是 `last_play_at`（不是 played_at）且要过滤
  `play_pos_ms > 0`；CTE 投影要显式别名 —— 外层 ORDER BY 看不到没 SELECT 的列

## Compose 铁律（Android）

- `LaunchedEffect` 的 key 不许在 effect 内自清；`scrollToPage` 交给 scope.launch
- `catch (Exception)` 必须单独 rethrow `CancellationException`
- 播放视图必须 TextureView；90/270 旋转先换布局尺寸再绕中心转
- seek 过程中短暂的 BUFFERING 不算换片（等 ready 一次为准）
- `@Upsert` 会抹掉 rotation → 扫描前带上旧值；缩略图组件只缩放不裁剪 →
  列表小格容器必须 `.clip(shape)`
- 连播：`PlayQueue(label, items)` + 换 `_items`；切源或重建信息流先清队列
- 真机坑：adb 造的私有目录 `exists()` 返回 false（push 后 run-as cp）；
  adb 注入不了多点触控；Git Bash 传 Windows 路径给 adb 前 `cygpath -w`、
  设 `MSYS_NO_PATHCONV=1`；查 Room 数据库要连 -wal/-shm；用户正在用手机时勿注入

## 桌面客户端铁律（Avalonia + libvlc）

- `VideoView` 是 NativeControlHost，**永远盖住 Avalonia 图层** → 控制层不能叠在画面上
  （塞进 VideoView.Content 也不渲染），必须走「上信息栏 / 中画面 / 下控制条」分栏
- Avalonia 12 键盘三坑：① 中文输入法把字母键变成 `Key.ImeProcessed`，而 12 里没有
  `ImeProcessedKey` → 必须按 `PhysicalKey` 判定；② 播放页叠在列表上时下面的输入控件仍持
  焦点，冒泡会吃掉字母与方向键 → 快捷键挂 `TopLevel` 且用 `RoutingStrategies.Tunnel`；
  ③ `keybd_event` 只发 VK（scan=0）时 `PhysicalKey` 是 None → 测试发键脚本必须
  `MapVirtualKeyW` 带扫描码
- libvlc 3.x 没有 `MediaPlayer.Crop`：`CropGeometry` + `AspectRatio` 两个都设才真铺满；
  `--aout=dummy` 下 Volume/Mute 不回读 → 静音改「音量归零 + 记原值」
- 取流三级降级：dual（video + `:input-slave=audio`）→ single → 换服务端 remux；
  状态推进用 400ms 轮询，不订阅 VLC 事件；关播放器用**同步** `Stop()`
- 看完判据：尾部 20 秒**且**走过九成（只判尾部会把短视频全判成看完）
- 海报缓存：懒加载 + 并发 6 / LRU 260 / 失败不缓存 / 淘汰不 Dispose；
  列表虚拟化：Avalonia 12 没有 ItemsRepeater → 定宽卡片 + VM 切行 + VirtualizingStackPanel
- 窗口构造阶段 `await` 之后不在 UI 线程，异常会被静默吞 → 回 UI 线程用
  `Dispatcher.UIThread.Post` 并全捕获写日志

## 发布流水线

- 根 `build.sh` 是分发器（desktop / client / server / android，可选做）；
  Windows 三件用 `scripts/release/publish.sh`，APK 用 `publish-android.sh`，
  zip 一律 `scripts/release/pack.cs`（.NET file-based app）—— **绝不用 `tar -a`**
  （GNU tar 产出的不是 zip）
- Windows 包全是自包含单文件 + 扁平 zip；APK 只做 debug 签名（keystore 不进仓库），
  `-r` 可覆盖升级
- libvlc 按 RID 在源头裁剪（按 RuntimeIdentifier 开对应 `VlcWindows*Enabled`），
  Content 里的原生 dll 必须打 `ExcludeFromSingleFile`，否则被塞进 exe →
  运行时找不到 libvlc；发布后清理只是兜底，删不掉必须报错不许静默
- ffmpeg 不随包分发（体积 + GPL）：`MEDIARACK_FFMPEG`/`MEDIARACK_FFPROBE` 指向 PATH，
  缺失按能力降级而非报错：dual 扫描不受影响；single 扫描退化 NullProbe；
  缩略图 404 `thumb_unavailable`；HEVC 单文件浏览器播放 409 `capability_unavailable`；
  `mrack doctor` 打印提示并退出码 1
- NuGet 版本集中在仓库根 `Directory.Packages.props`（CPM）；LibVLCSharp 与
  VideoLAN 原生库**必须同组改**，错配是 EntryPointNotFoundException
- Windows 上 `core.filemode=false` → 首次提交必须
  `git add --chmod=+x src/MediaRack.Android/gradlew`；`.gitattributes` 里
  `gradlew`/`*.sh` 用 eol=lf（CRLF 会把 shebang 变成 `^M`），`*.bat` 用 crlf
- 发布出来的大 exe 正被杀软实时扫描时目录删不掉（约 1~2 分钟窗口）→
  旧产物目录放到**下次发布开始时**清，rm 的错误必须显示
- 单文件产物冒烟：`--urls` 会被 config 的显式配置覆盖，想换端口要么清 config 要么改 config；
  从 shell 用 `&` 起的 GUI/服务进程会随命令结束被回收 → 必须挂后台跑

## 脚本与文档纪律

- `scripts/` 三组，按「谁判定通过」分：`release/` 出交付物、`check/` 靠退出码 0/1、
  `devtools/` 人工看图（仅 Windows，是验收手段本身，换环境重写等于重新踩坑）
- 文档卫生：`python scripts/check/check_hygiene.py`（规则见 docs/agent-rules.md）——
  不写开发机路径、不写私网 IP（示例一律 `192.0.2.x` 段）、不写内部里程碑编号、
  不写变更历史日期、正文不引用不入库目录；一份文档只回答一个问题
- 历史件进归档目录（已被 gitignore），主文档只用一句话指路、不给 markdown 链接；
  `docs/` 内全部入库
- 托盘单实例互斥名不许跟着项目改名 —— 改了新旧版本会同时起两个服务抢同一个端口

## 协作约定

- 先对齐方案再动代码；报告先行（分节 + 表格 + 数据）；事实性判断先实测再下结论
- 改完只做本地 `git commit`，**绝不自动 push**（CI 与 Release 挂在 push 上；
  推送只能由用户本人执行或明确要求时做）
