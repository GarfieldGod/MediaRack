using System.Diagnostics;
using MediaRack.Library;
using MediaRack.Media;
using MediaRack.Models;
using MediaRack.Scan;
using MediaRack.Server.Hosting;
using MediaRack.Store;
using Microsoft.Data.Sqlite;

namespace MediaRack.Server.Desktop;

/// <summary>
/// 托盘本身。没有主窗口——这是刻意的：服务在跑的时候占一块屏幕没有意义，
/// 需要管理时点一下就开浏览器。
/// </summary>
internal sealed class TrayApp : ApplicationContext
{
    private readonly NotifyIcon _icon;
    private readonly EventWaitHandle _showEvent;
    private readonly ServerRunner _server = new();
    private readonly CancellationTokenSource _shutdown = new();

    private MediaRackConfig? _config;
    private bool _initialized;
    private bool _busy;

    public TrayApp(EventWaitHandle showEvent)
    {
        _showEvent = showEvent;
        _icon = new NotifyIcon
        {
            Icon = AppIcon.Create(),
            Visible = true,
            Text = "MediaRack",
            ContextMenuStrip = BuildMenu(),
        };

        // 双击图标 = 打开管理页，与 Steam 那种"点一下回到界面"的习惯一致。
        _icon.DoubleClick += (_, _) => OpenBrowser();

        Application.Idle += OnFirstIdle;
        ListenForSecondInstance();
    }

    private void OnFirstIdle(object? sender, EventArgs e)
    {
        if (_initialized) return;

        _initialized = true;
        Application.Idle -= OnFirstIdle;

        // 放到 Idle 里做：构造函数里同步等 HTTP 管线启动会把消息循环卡住，
        // 托盘图标要等半天才出现，看起来像程序没启动。
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        _config = LoadConfig();

        if (_config is null)
        {
            // 首次运行：先问配置，用户点了取消就退出，不要留一个什么都不做的托盘图标。
            if (!ShowSetup(forceNew: true))
            {
                ExitThread();
                return;
            }
        }

        await StartServerAsync();

        // 首次配置时刚加的源还没扫过；库里是空的，直接扫一遍比让用户自己点一次更省事。
        var pending = _pendingScanRoots;
        _pendingScanRoots = null;

        if (pending is { Count: > 0 }) await ScanAsync(pending);
    }

    private List<string>? _pendingScanRoots;

    // ── 菜单 ────────────────────────────────────────────────

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();

        var open = new ToolStripMenuItem("打开管理页", null, (_, _) => OpenBrowser());
        open.Font = new Font(open.Font, FontStyle.Bold);

        var copy = new ToolStripMenuItem("复制地址", null, (_, _) => CopyUrl());
        // 二维码不再是"整机一张"：每个源有自己的地址和令牌，码在管理页里按源出。
        var pair = new ToolStripMenuItem("配对手机（到管理页选源扫码）", null, (_, _) => OpenBrowser());

        var addSource = new ToolStripMenuItem("添加源文件夹并扫描…", null, (_, _) => _ = AddSourceAsync());
        var rescan = new ToolStripMenuItem("重新扫描全部源", null, (_, _) => _ = RescanAllAsync());

        var setup = new ToolStripMenuItem("配置服务…", null, (_, _) => ShowSetup(forceNew: false));
        var openMeta = new ToolStripMenuItem("打开库目录", null, (_, _) => OpenFolder(MetaRoot));
        var openConfig = new ToolStripMenuItem("打开配置文件", null, (_, _) => OpenFile(MediaRackConfig.UserPath));
        var openLog = new ToolStripMenuItem("打开日志", null, (_, _) => OpenFile(ServerRunner.LogPath));

        // CheckOnClick + 注册表同步状态：菜单弹出时刷新，防止用户手动改过注册表后菜单撒谎。
        var autoStart = new ToolStripMenuItem("开机自启", null, (_, _) => ToggleAutoStart())
        {
            CheckOnClick = true,
            Checked = SafeIsAutoStart(),
        };

        var exit = new ToolStripMenuItem("退出", null, (_, _) => ExitApplication());

        menu.Items.Add(open);
        menu.Items.Add(pair);
        menu.Items.Add(copy);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(addSource);
        menu.Items.Add(rescan);
        menu.Items.Add(setup);
        menu.Items.Add(openMeta);
        menu.Items.Add(openConfig);
        menu.Items.Add(openLog);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(autoStart);
        menu.Items.Add(exit);

        // 打开菜单时刷新一次，避免显示上一分钟的状态。
        menu.Opening += (_, _) =>
        {
            UpdateTray();
            autoStart.Checked = SafeIsAutoStart();
        };
        return menu;
    }

    /// <summary>注册表读取理论上不抛，但 Explorer 重启等边缘态不值得让它炸托盘。</summary>
    private static bool SafeIsAutoStart()
    {
        try { return AutoStart.IsEnabled(); }
        catch { return false; }
    }

    private void ToggleAutoStart()
    {
        try
        {
            AutoStart.SetEnabled(!AutoStart.IsEnabled());
        }
        catch (Exception ex)
        {
            MessageBox.Show($"修改开机自启失败：{ex.Message}", "MediaRack",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void UpdateTray()
    {
        if (_server.Running)
        {
            var ports = string.Join(" / ", _server.Services.Select(s => s.Port));
            _icon.Text = Truncate($"MediaRack · 运行中 · {ports}");
        }
        else
        {
            _icon.Text = _busy ? "MediaRack · 扫描中" : "MediaRack · 已停止";
        }
    }

    /// <summary>NotifyIcon.Text 上限 63 字符，超了会抛异常。</summary>
    private static string Truncate(string text) => text.Length <= 62 ? text : text[..62];

    // ── 服务 ────────────────────────────────────────────────

    private string MetaRoot =>
        string.IsNullOrWhiteSpace(_config?.MetaRoot) ? ServerOptions.DefaultMetaRoot : _config!.MetaRoot;

    private async Task StartServerAsync()
    {
        if (_config is null) return;

        var ok = await _server.StartAsync(_config);

        if (!ok)
        {
            UpdateTray();

            var hint = _server.LastError?.Contains("address already in use", StringComparison.OrdinalIgnoreCase) == true
                ? "\n\n端口已被占用：可能另一个 MediaRack 正在运行，或别的程序占了这个端口。"
                : string.Empty;

            MessageBox.Show(
                $"服务启动失败：{_server.LastError}{hint}",
                "MediaRack", MessageBoxButtons.OK, MessageBoxIcon.Error);

            return;
        }

        UpdateTray();

        if (_config.Services.Any(s => s.Writable && string.IsNullOrWhiteSpace(s.Password)))
        {
            _icon.ShowBalloonTip(4000, "MediaRack 正在运行",
                "有一个公开且可写的端口。只在可信网络里这样用；要暴露到公网请给它设密码。",
                ToolTipIcon.Warning);
        }
    }

    // ── 扫描 ────────────────────────────────────────────────

    private async Task AddSourceAsync()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "选择视频所在的文件夹",
            UseDescriptionForTitle = true,
        };

        if (dialog.ShowDialog() != DialogResult.OK) return;

        await ScanAsync([dialog.SelectedPath]);
    }

    private async Task RescanAllAsync()
    {
        if (!Directory.Exists(MetaRoot))
        {
            MessageBox.Show("库目录还不存在，先添加一个源文件夹。", "MediaRack",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        List<string> roots;
        using (var db = LibraryDatabase.Open(MetaRoot, create: false))
        {
            roots = new SourceRepository(db).List()
                .Select(s => s.Root)
                .OfType<string>()
                .ToList();
        }

        if (roots.Count == 0)
        {
            _icon.ShowBalloonTip(3000, "MediaRack", "还没有登记任何源。", ToolTipIcon.Info);
            return;
        }

        await ScanAsync(roots);
    }

    /// <summary>
    /// 扫描若干文件夹。整个过程在后台线程，托盘不阻塞——
    /// 首次扫 5000 条要几分钟，卡住界面等于程序假死。
    /// </summary>
    private async Task ScanAsync(IReadOnlyList<string> roots)
    {
        if (_busy) return;

        _busy = true;
        UpdateTray();

        var written = 0;
        string? error = null;

        try
        {
            await Task.Run(() =>
            {
                Directory.CreateDirectory(MetaRoot);

                using var db = LibraryDatabase.Open(MetaRoot);
                db.Migrate();

                var scanner = new ScanService(db, MetaRoot);
                var sources = new SourceRepository(db);

                foreach (var root in roots)
                {
                    if (!Directory.Exists(root)) continue;

                    var id = MakeSourceId(sources, root);

                    var source = new SourceConfig
                    {
                        Id = id,
                        Name = id,
                        Layout = LayoutDetector.Detect(root),
                        Root = Path.GetFullPath(root),
                    };

                    var outcome = scanner.Run(
                        source,
                        new ScanOptions(Verify: VerifyMode.Sample, Posters: new PosterOptions()),
                        ct: _shutdown.Token);

                    written += outcome.Written;
                }
            }, _shutdown.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            error = ex.GetBaseException().Message;
        }
        finally
        {
            _busy = false;
            UpdateTray();
        }

        if (error is not null)
        {
            MessageBox.Show($"扫描失败：{error}", "MediaRack", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        _icon.ShowBalloonTip(3000, "扫描完成",
            $"新增或更新 {written} 条。手机配对请打开管理页，选这个源扫二维码。", ToolTipIcon.Info);
    }

    /// <summary>源 id 取文件夹名，重名加序号。它出现在接口 URL 里，所以不能含分隔符。</summary>
    private static string MakeSourceId(SourceRepository sources, string root)
    {
        var baseId = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(baseId)) baseId = "source";

        var existing = sources.List().Select(s => s.Id).ToHashSet(StringComparer.Ordinal);

        var id = baseId;
        var n = 2;
        while (existing.Contains(id)) id = $"{baseId}-{n++}";

        return id;
    }

    // ── 配置 ────────────────────────────────────────────────

    private MediaRackConfig? LoadConfig()
    {
        try
        {
            var path = MediaRackConfig.Locate([]);
            return MediaRackConfig.Load(path);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"配置文件读不动：{ex.Message}\n\n将改用默认配置启动。",
                "MediaRack", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return MediaRackConfig.Default;
        }
    }

    private bool ShowSetup(bool forceNew)
    {
        // 没有配置文件时第一次调用 LoadConfig 会拿到 Default（8080 单服务），
        // 那个值当"已有配置"显示会让用户以为自己配过了，所以 forceNew 时传 null。
        using var form = new SetupForm(forceNew ? null : _config);

        if (form.ShowDialog() != DialogResult.OK) return false;

        _config = form.Result!;
        _pendingScanRoots = form.ScanAfterSave ? [.. form.SourceRoots] : null;

        try
        {
            _config.Save(MediaRackConfig.UserPath);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"配置保存失败：{ex.Message}", "MediaRack",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }

        if (!forceNew)
        {
            // 改配置必须重启服务，端口与服务表只在启动时读一次。
            _ = RestartAsync();
        }

        return true;
    }

    private async Task RestartAsync()
    {
        await _server.StartAsync(_config!);
        UpdateTray();
    }

    // ── 外部动作 ─────────────────────────────────────────────

    private void OpenBrowser()
    {
        var url = _server.PrimaryUrl;
        if (url is null)
        {
            _icon.ShowBalloonTip(3000, "MediaRack", "服务没在运行，先检查配置。", ToolTipIcon.Warning);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"打不开浏览器：{ex.Message}\n\n请手动访问 {url}", "MediaRack",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void CopyUrl()
    {
        var url = _server.PrimaryUrl;
        if (url is null) return;

        try
        {
            Clipboard.SetText(url);
            _icon.ShowBalloonTip(2000, "MediaRack", $"已复制：{url}", ToolTipIcon.Info);
        }
        catch (Exception)
        {
            // 剪贴板被别的进程占用时会抛，重试没有意义。
        }
    }

    private static void OpenFolder(string path)
    {
        if (!Directory.Exists(path))
        {
            MessageBox.Show($"目录不存在：{path}", "MediaRack", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private static void OpenFile(string path)
    {
        if (!File.Exists(path))
        {
            MessageBox.Show($"文件不存在：{path}", "MediaRack", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    // ── 生命周期 ─────────────────────────────────────────────

    /// <summary>第二个实例启动时置位这个事件，我们来开浏览器——用户看到的就像"点一下就打开了"。</summary>
    private void ListenForSecondInstance()
    {
        _ = Task.Run(() =>
        {
            while (!_shutdown.IsCancellationRequested)
            {
                try
                {
                    if (!_showEvent.WaitOne(500)) continue;
                    OpenBrowser();
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
            }
        });
    }

    private void ExitApplication()
    {
        _shutdown.Cancel();
        _icon.Visible = false;

        // 用 Task.Run 把 StopAsync 从 UI 线程挪开：Application.Exit 会等 UI 线程，
        // 同步等 Kestrel 停止可能超时（5s）。
        Task.Run(async () =>
        {
            await _server.StopAsync();
            SqliteConnection.ClearAllPools();
        }).Wait(TimeSpan.FromSeconds(6));

        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _shutdown.Cancel();
            _icon.Visible = false;
            _icon.Dispose();
            _shutdown.Dispose();
        }

        base.Dispose(disposing);
    }
}
