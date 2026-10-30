# Agent 准则

给所有在这个仓库里写代码、写注释、写文档的人（含 AI）。

一句话：**换个人拿到这份仓库，能不能看懂、能不能跑。** 做不到就是缺陷，不是"以后再说"。

## 六条硬规则

| # | 规则 | 常见犯法 | 怎么改 |
|---|---|---|---|
| 1 | 不写开发机路径 | `<盘符>:<目录>\...`、用户主目录、工具链安装路径 | 写相对路径或占位符（`<库目录>`、`<视频目录>`） |
| 2 | 不写真实网络地址 | 家里网段的具体 IP、虚拟组网地址、真机端口 | 示例一律用文档保留段 `192.0.2.x` |
| 3 | 不提别的私有项目 | 注释里"和 X 项目同样处理" | 删掉。读者不认识 X，这句话对他零信息 |
| 4 | 不引用不入库的目录 | 指向归档目录、探针目录、AI 记忆目录 | 读者拿不到 = 没写。删掉，或把内容搬进仓库 |
| 5 | 不写内部编号 | 里程碑编号、`§` 章节号不说是哪份文档、文件名带编号 | 换成功能描述；`§` 必须带 `docs/design.md` |
| 6 | 不写变更历史 | "某年某月删了 X，包体降到 Y MB" | git 已经记了。要留就写"为什么现在这样" |

规则 5 的两种形态最容易被忽略：

- 里程碑编号 → 直接说功能："下载到本地"、"断网降级"，不写阶段号
- 章节号 → 写成 `docs/design.md §10`，不要只写 `§18`（读者不知道是哪个文件的第 18 节，而且旧编号早就失效）

## 文档与注释的附加要求

- **只留要点。** 一个事实说一遍。不要"首先/其次/综上所述"。
- **数字必须实测。** warning 数、测试数、端点数、包体积——写之前跑一遍。
  这条曾经撒过三次谎（端点数、测试数、构建警告数），每次都是"看起来像真的"。

## 犯了以后怎么清

1. `python scripts/check/check_hygiene.py` 列出全部问题（`-q` 只看错误，`--strict` 连警告一起卡）
2. 按上表逐条改。同一批问题用一个一次性脚本批量替换，别手点——手点必漏
3. 动了文件名的，**全仓 grep 引用**：README、其它文档、脚本、代码注释
4. 三件验证：`dotnet build --no-incremental`（0 warning）、`dotnet test`（全过）、`python scripts/check/check_contract.py`
   —— **必须带 `--no-incremental`**：增量构建会跳过没改的项目，那些项目里的警告看不见，会让"0 warning"变成假账
5. 重跑卫生检查，退出码 0 才算完

批量替换脚本的两个坑：Windows 下按 UTF-8 读写并保留原换行；替换前后都要核对命中数（0 命中说明模式写错了，不是"已经干净了"）。

## 检查器

```bash
python scripts/check/check_hygiene.py             # 全仓；有错误退出码 1
python scripts/check/check_hygiene.py -q          # 只打印错误
python scripts/check/check_hygiene.py --strict    # 警告也算失败
```

六类规则：

| 规则 | 抓什么 |
|---|---|
| `leak` | 用户目录、工具链路径、私有项目名、私网/虚拟网地址 |
| `ignored` | 正文里出现不入库的目录 |
| `milestone` | 里程碑编号（XML 只看注释区，矢量图坐标不算） |
| `section` | `§` 章节号没带 `docs/design.md` |
| `archived` | 引用已归档/已删除的文档 |
| `link` | md 相对链接指向不存在的文件 |

私有项目名清单在脚本顶部 `PRIVATE_NAMES`，换项目时改那里。

**豁免**：行尾写 `hygiene:ignore`。只认三种理由——

1. 检查器自己的规则表（必须写出要拦什么）
2. 文档里演示错误写法
3. 面向用户的示例文案故意用常见家庭地址（`ServerEditDialog.kt` 的服务器地址输入框）

## 验收工具

`scripts/` 按「谁判定通过」分三组。换机器后先读这里，别重写：

| 目录 | 判据 | 内容 |
|---|---|---|
| `release/` | 产出 `publish/` 下的交付物 | `publish.sh`（服务端/桌面壳/客户端）、`publish-android.sh`（APK）、`pack.cs`（系统没 `zip` 时的兜底） |
| `check/` | 退出码 0/1，可进 CI | `check_contract.py`、`check_hygiene.py`、`e2e_smoke.py` |
| `devtools/` | 跑完人工看一眼才知道对不对 | `screenshot.py`、`click.py`、`sendkey.py`（Windows 界面）、`android-push-samples.sh`（推样本进真机） |

`devtools/` 只支持 Windows（ctypes 直接调 GDI / `SetCursorPos` / `keybd_event`）。
界面改动单测断言不到时用它：截图 → `click.py --rel` 按比例点坐标 → 再截一张比对。
**别按截图像素量坐标**——截图在聊天窗口里被缩放过，量三次错两次；比例（x/图宽）不受缩放影响。

### 先用无头自检，别一上来就点界面

界面逻辑的判据几乎都在 ViewModel 里，客户端为此留了自检入口——**跑它们比模拟鼠标键盘快一个数量级**：

| 入口 | 覆盖 |
|---|---|
| `--selftest [baseUrl]` | 协议层（ApiClient 全链路） |
| `--vmtest [baseUrl]` | 主页（切源 / 合集 / 搜索 / 续页 / 海报） |
| `--playtest [baseUrl]` | 播放链路（起播 / 双流合成 / 进度写回） |
| `--connecttest <源门户地址> [令牌]` | 连接页（一个地址框的解析容错 / 只认门户地址 / 只能拿到一条源） |
| `--shelltest` | 外壳骨架（空态 / 抽屉 / 连接弹层 / 我的往返），**不联网不开窗** |

`--shelltest` 跑在 UI 线程上同步执行，所以不受"没有消息循环 → `Dispatcher.Post` 的活不执行"影响；
反过来说，它只能断言**结构**（谁是谁、开没开），断言不了网络回来的数据——那部分归 `--vmtest`。

模拟输入有三个反复踩的坑，全和被测代码无关：窗口最小化时 `GetWindowRect` 返回 -32000 哨兵值
（坐标全在屏幕外）；非活动窗口的第一次点击只激活窗口、不传给控件；Alt 抖动抢前台会让 Avalonia
把键盘焦点从 TextBox 移到窗口本身，于是分步调用（点一次、再敲一次）会丢字。
真需要点界面时，**一条命令里做完「激活 → 点 → 清空 → 输入」**，别拆成多次调用。

**看整屏截图的办法（2026-10-30）**：`Add-Type`（PrintWindow）与 `WScript.Shell`（AppActivate）
都被安全策略拦掉，能用的只有 `ffmpeg -f gdigrab -i desktop`——它抓的是屏幕，所以窗口必须真的
在最前面。窗口被挡住时抓出来是全黑（DWM 合成，`-i title=` 也一样）。要稳定复现，就临时在窗口
`Opened` 里设 `Topmost + Position + 固定宽高`，截完把这段临时钩子删掉。

`android-push-samples.sh` 之所以要 `run-as` 而不是 `adb push` 到 `Android/data`：
新系统上 adb（shell uid）建的文件属主不对，应用自己 `File.listFiles()` 返回 null。

## 交付物命名（2026-10-30 定，四处必须一致）

**文件夹名 = 里面那个 exe 的名字**，大小写与连字符都一样：

| 文件夹 | 产物 |
|---|---|
| `MediaRack-Server-Desktop-Windows-x64` | `MediaRack-Server-Desktop.exe` |
| `MediaRack-Client-Windows-x64` | `MediaRack-Client.exe`（+ `libvlc/`） |
| `MediaRack-Server-Windows-x64` | `MediaRack-Server.exe` |
| `MediaRack-Client-Android` | `MediaRack-Client.apk` |

这套名字由四处共同决定，改一处就得改四处：csproj 的 `<AssemblyName>`、`build.sh` 传给
`--output` 的目录、`.github/workflows/release.yml` 里的附件路径、README 的产物表。

- **改 `<AssemblyName>` 时记得 Desktop 的 `TrimPublishOutput`**：它按名字删
  `MediaRack-Server.runtimeconfig.json` / `.deps.json`，名字对不上就会把这两个文件留在发布目录里。
- **单文件是硬要求**：服务端与桌面壳目录里只能有一个 exe。往这两个项目加 `Content` 之前先问
  「这会不会让一个 exe 变成两个」——`appsettings.json` 就是因此被删的（值已进代码默认值）。
- **包里不放使用说明文件**：`使用说明.txt` 已移除，说明文字只写 README 与发布页正文。
  同一个说明写两遍，迟早互相矛盾。
- 客户端是唯一必须打 zip 的（`libvlc/` 塞不进单文件）；其余三项发布时直接给 exe / apk。

## CI（`.github/workflows/release.yml`）

分两档：**推 main 只构建 + 存 artifact（30 天），打 `v*` tag 才发 Release**，tag 名即版本号。
每推一次就发一个版本会把发布页淹掉，也让发版失去「显式动作」的含义。

- 发版流程：`git tag v1.0.0 && git push origin v1.0.0`。tag 只标记「这次打包是给人用的」，
  **不要拿它去统一三端的 `<Version>`**（client / server / android 各走各的，硬凑一个语义化版本等于撒谎）。
- `发布 Release` 那一步带 `if: startsWith(github.ref, 'refs/tags/v')`，`workflow_dispatch` 手跑只出 artifact。
- artifact 下载下来必然是 zip 包装 ⇒ 客户端那个 zip 会套两层。这是 artifact 机制决定的，
  要干净的单文件就走 tag → Release。

- 只有一个 job，跑在 `windows-latest`：桌面壳是 net10.0-windows，必然要一台 Windows 机器，
  其余三件没必要再拆去别的 runner 传 300 MB 产物。
- **构建步骤直接调 `./build.sh`**，不要在 workflow 里另写一套 `dotnet publish` 参数——
  否则"本地发得出来、CI 发不出来"这种分叉迟早出现。
- 产物路径是硬编码核对的（`校验产物` 那一步）：`publish.sh` 在目录删不掉时会降级到
  `<名字>-<时间戳>/`，CI 上真发生了就得当场失败，而不是把上一次的附件当好包发出去。
- 客户端 zip 会检查里面有没有 `libvlc/`：没有就是个"能开不能播"的包，直接拒绝发布。

## 不算错，别乱改

- 「本机」在产品语义里是对的：只对本机开放的管理页、被本机进程加载的 dll
- `.gitignore` 里必须写目录名——它本身就是规则
- 代码注释里的示例路径（如 `<盘符>:\视频`）不是泄漏，检查器只对文档报盘符路径
- 框架类名和私有项目名撞车时，改项目名，不改代码

## 提交前

入库范围见 [`../.gitignore`](../.gitignore)：构建产物、发布包、运行时数据、归档目录、AI 记忆目录都不入库。

犹豫某文件该不该提交时，问一句：**删掉之后能用一条命令重建吗？** 能 → 不入库。
