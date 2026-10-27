using MediaRack.Models;
using MediaRack.Scan;
using MediaRack.Server.Hosting;

namespace MediaRack.Server.Desktop;

/// <summary>
/// 配置向导。左列服务、右侧该服务的属性、底部源文件夹。
/// <para>
/// 刻意不做成"一个服务一页面"的多步向导：服务与源的对应关系是<b>一张二维表</b>，
/// 分成下一步下一步反而让人记不住自己刚才把哪个源给了哪个服务。
/// </para>
/// </summary>
internal sealed class SetupForm : Form
{
    private readonly TextBox _metaBox = new();
    private readonly ListBox _serviceList = new();
    private readonly ListBox _sourceList = new();

    private readonly TextBox _nameBox = new();
    private readonly NumericUpDown _portBox = new();
    private readonly TextBox _passwordBox = new();
    private readonly CheckBox _writableBox = new();
    private readonly CheckedListBox _visibleChecks = new();

    private readonly List<ServiceDraft> _services = [];
    private readonly List<SourceDraft> _sources = [];

    /// <summary>属性面板回填时置位，避免 TextChanged 把回填当成用户编辑。</summary>
    private bool _loading;

    /// <summary>点"保存并启动"后的结果；取消时为 null。</summary>
    public MediaRackConfig? Result { get; private set; }

    /// <summary>本次是否要顺带扫描源（首次配置时是 true）。</summary>
    public bool ScanAfterSave { get; private set; }

    public SetupForm(MediaRackConfig? existing)
    {
        Text = "MediaRack 配置";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(880, 560);
        MinimumSize = new Size(760, 500);
        AutoScaleMode = AutoScaleMode.Font;
        Font = new Font("Microsoft YaHei UI", 9F);

        BuildLayout();
        LoadExisting(existing);
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(12),
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 68));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 32));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        // ── 库目录 ──
        var metaRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, AutoSize = true };
        metaRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        metaRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        metaRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        metaRow.Controls.Add(Label("库目录："), 0, 0);
        _metaBox.Dock = DockStyle.Fill;
        _metaBox.Margin = new Padding(0, 4, 8, 4);
        metaRow.Controls.Add(_metaBox, 1, 0);

        var browse = new Button { Text = "浏览…", AutoSize = true, Margin = new Padding(0, 2, 0, 2) };
        browse.Click += (_, _) => BrowseMetaRoot();
        metaRow.Controls.Add(browse, 2, 0);

        root.Controls.Add(metaRow, 0, 0);

        // ── 服务 + 属性 ──
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterDistance = 300,
            SplitterWidth = 8,
        };

        split.Panel1.Controls.Add(BuildServicePane());
        split.Panel2.Controls.Add(BuildPropertyPane());
        root.Controls.Add(split, 0, 1);

        // ── 源文件夹 ──
        root.Controls.Add(BuildSourcePane(), 0, 2);

        // ── 按钮 ──
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
        };

        var ok = new Button { Text = "保存并启动", AutoSize = true, Padding = new Padding(10, 4, 10, 4) };
        ok.Click += (_, _) => Save();

        var cancel = new Button { Text = "取消", AutoSize = true, DialogResult = DialogResult.Cancel };
        cancel.Margin = new Padding(8, 3, 0, 3);

        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);
        root.Controls.Add(buttons, 0, 3);

        AcceptButton = ok;
        CancelButton = cancel;
        Controls.Add(root);
    }

    private Control BuildServicePane()
    {
        var box = new GroupBox { Text = "对外服务", Dock = DockStyle.Fill, Padding = new Padding(8) };

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _serviceList.Dock = DockStyle.Fill;
        _serviceList.IntegralHeight = false;
        _serviceList.SelectedIndexChanged += (_, _) => ShowSelectedService();
        layout.Controls.Add(_serviceList, 0, 0);

        var bar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, 6, 0, 0) };

        var add = new Button { Text = "添加", AutoSize = true };
        add.Click += (_, _) => AddService();

        var remove = new Button { Text = "删除", AutoSize = true };
        remove.Click += (_, _) => RemoveService();

        bar.Controls.Add(add);
        bar.Controls.Add(remove);
        layout.Controls.Add(bar, 0, 1);

        box.Controls.Add(layout);
        return box;
    }

    private Control BuildPropertyPane()
    {
        var box = new GroupBox { Text = "服务属性", Dock = DockStyle.Fill, Padding = new Padding(8) };

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 5,
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        for (var i = 0; i < 5; i++) grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        grid.Controls.Add(Label("名称："), 0, 0);
        _nameBox.Dock = DockStyle.Fill;
        _nameBox.TextChanged += (_, _) => Commit("name");
        grid.Controls.Add(_nameBox, 1, 0);

        grid.Controls.Add(Label("端口："), 0, 1);
        _portBox.Minimum = 1024;
        _portBox.Maximum = 65535;
        _portBox.Value = 8080;
        _portBox.Dock = DockStyle.Left;
        _portBox.Width = 120;
        _portBox.ValueChanged += (_, _) => Commit("port");
        grid.Controls.Add(_portBox, 1, 1);

        grid.Controls.Add(Label("密码："), 0, 2);
        _passwordBox.Dock = DockStyle.Fill;
        _passwordBox.PlaceholderText = "留空即完全公开";
        _passwordBox.TextChanged += (_, _) => Commit("password");
        grid.Controls.Add(_passwordBox, 1, 2);

        grid.Controls.Add(Label("允许修改："), 0, 3);
        _writableBox.Text = "允许在这个端口上改名 / 收藏 / 扫描";
        _writableBox.AutoSize = true;
        _writableBox.CheckedChanged += (_, _) => Commit("writable");
        grid.Controls.Add(_writableBox, 1, 3);

        grid.Controls.Add(Label("可见源："), 0, 4);
        _visibleChecks.Dock = DockStyle.Fill;
        _visibleChecks.CheckOnClick = true;
        _visibleChecks.Height = 140;
        _visibleChecks.ItemCheck += (_, _) =>
        {
            // ItemCheck 发生在状态更新之前，要等这一轮消息处理完再读。
            BeginInvoke(new Action(() => Commit("sources")));
        };
        grid.Controls.Add(_visibleChecks, 1, 4);

        box.Controls.Add(grid);

        // 允许修改这一栏在"公开 + 可写"时给出风险提示，用颜色而不用弹窗：
        // 弹窗会在每次勾选时打断，用户很快就会盲点掉。
        _passwordBox.TextChanged += (_, _) => UpdateRiskHint();
        _writableBox.CheckedChanged += (_, _) => UpdateRiskHint();

        return box;
    }

    private void UpdateRiskHint()
    {
        if (_loading) return;

        var risky = _passwordBox.Text.Trim().Length == 0 && _writableBox.Checked;
        _writableBox.ForeColor = risky ? Color.Firebrick : SystemColors.ControlText;
        _writableBox.Text = risky
            ? "允许修改（⚠ 公开且可写：同网络任何人都能改库）"
            : "允许在这个端口上改名 / 收藏 / 扫描";
    }

    private Control BuildSourcePane()
    {
        var box = new GroupBox { Text = "源文件夹", Dock = DockStyle.Fill, Padding = new Padding(8) };

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _sourceList.Dock = DockStyle.Fill;
        _sourceList.IntegralHeight = false;
        layout.Controls.Add(_sourceList, 0, 0);

        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            Margin = new Padding(8, 0, 0, 0),
        };

        var add = new Button { Text = "添加文件夹…", AutoSize = true };
        add.Click += (_, _) => AddSource();

        var remove = new Button { Text = "移除", AutoSize = true };
        remove.Click += (_, _) => RemoveSource();

        bar.Controls.Add(add);
        bar.Controls.Add(remove);
        layout.Controls.Add(bar, 1, 0);

        box.Controls.Add(layout);
        return box;
    }

    private static Label Label(string text) => new()
    {
        Text = text,
        AutoSize = true,
        TextAlign = ContentAlignment.MiddleLeft,
        Margin = new Padding(0, 8, 10, 8),
    };

    // ── 数据 ────────────────────────────────────────────────

    private void LoadExisting(MediaRackConfig? existing)
    {
        var config = existing ?? BuildDefaultConfig();

        _metaBox.Text = string.IsNullOrWhiteSpace(config.MetaRoot)
            ? ServerOptions.DefaultMetaRoot
            : config.MetaRoot;

        foreach (var entry in config.Services)
        {
            _services.Add(new ServiceDraft
            {
                Name = string.IsNullOrWhiteSpace(entry.Name) ? $"服务 {entry.Port}" : entry.Name!,
                Port = entry.Port <= 0 ? 8080 : entry.Port,
                Password = entry.Password,
                Writable = entry.Writable,
                Sources = [.. entry.Sources ?? []],
            });
        }

        if (_services.Count == 0) _services.Add(new ServiceDraft { Name = "全部", Port = 8080 });

        // 已有的可见源名字来自 config，但它们的文件夹路径这次会话里不知道，
        // 先登记成"仅名字"的条目，用户重新添加文件夹时按 id 覆盖即可。
        foreach (var id in _services.SelectMany(s => s.Sources).Distinct(StringComparer.Ordinal))
        {
            _sources.Add(new SourceDraft { Id = id, Name = id, Root = string.Empty });
        }

        RefreshSources();
        RefreshServices();
        _serviceList.SelectedIndex = 0;
    }

    private static MediaRackConfig BuildDefaultConfig() => new()
    {
        MetaRoot = ServerOptions.DefaultMetaRoot,
        Services = [new ServiceConfig { Name = "全部", Port = 8080, Writable = true, Sources = [] }],
    };

    private ServiceDraft? Current =>
        _serviceList.SelectedIndex >= 0 && _serviceList.SelectedIndex < _services.Count
            ? _services[_serviceList.SelectedIndex]
            : null;

    private void RefreshServices()
    {
        var keep = _serviceList.SelectedIndex;

        _serviceList.BeginUpdate();
        _serviceList.Items.Clear();

        foreach (var service in _services)
        {
            var lockMark = string.IsNullOrWhiteSpace(service.Password) ? "公开" : "需密码";
            var writeMark = service.Writable ? "可写" : "只读";
            var scope = service.Sources.Count == 0 ? "全部源" : $"{service.Sources.Count} 个源";
            _serviceList.Items.Add($"{service.Name}   ·   {service.Port}   ·   {lockMark}   ·   {writeMark}   ·   {scope}");
        }

        _serviceList.EndUpdate();

        if (_serviceList.Items.Count > 0)
        {
            _serviceList.SelectedIndex = Math.Clamp(keep, 0, _serviceList.Items.Count - 1);
        }
    }

    private void ShowSelectedService()
    {
        var service = Current;
        if (service is null) return;

        _loading = true;
        _nameBox.Text = service.Name;
        _portBox.Value = Math.Clamp(service.Port, (int)_portBox.Minimum, (int)_portBox.Maximum);
        _passwordBox.Text = service.Password ?? string.Empty;
        _writableBox.Checked = service.Writable;

        RefreshVisibleChecks(service);
        _loading = false;

        UpdateRiskHint();
    }

    private void RefreshVisibleChecks(ServiceDraft service)
    {
        _visibleChecks.BeginUpdate();
        _visibleChecks.Items.Clear();

        foreach (var source in _sources)
        {
            // 空集合的语义是"全部源"，此时顺手把所有框勾上，避免"看着一个没勾却什么都能看"的困惑。
            var visible = service.Sources.Count == 0 || service.Sources.Contains(source.Id);
            _visibleChecks.Items.Add(source.Display, visible);
        }

        _visibleChecks.EndUpdate();
    }

    private void Commit(string field)
    {
        var service = Current;
        if (_loading || service is null) return;

        switch (field)
        {
            case "name":
                service.Name = string.IsNullOrWhiteSpace(_nameBox.Text) ? $"服务 {service.Port}" : _nameBox.Text.Trim();
                break;
            case "port":
                service.Port = (int)_portBox.Value;
                break;
            case "password":
                service.Password = string.IsNullOrWhiteSpace(_passwordBox.Text) ? null : _passwordBox.Text.Trim();
                break;
            case "writable":
                service.Writable = _writableBox.Checked;
                break;
            case "sources":
                service.Sources = ReadCheckedSources();
                break;
        }

        // 列表项要跟着改，但重建列表会打断正在输入的光标，所以只在列表选中项上原地替换。
        var index = _serviceList.SelectedIndex;
        if (index >= 0)
        {
            var lockMark = string.IsNullOrWhiteSpace(service.Password) ? "公开" : "需密码";
            var writeMark = service.Writable ? "可写" : "只读";
            var scope = service.Sources.Count == 0 ? "全部源" : $"{service.Sources.Count} 个源";
            _serviceList.Items[index] =
                $"{service.Name}   ·   {service.Port}   ·   {lockMark}   ·   {writeMark}   ·   {scope}";
        }
    }

    /// <summary>
    /// 读取勾选结果。全勾等于"全部源"（存成空数组），
    /// 这样以后新加的源会自动对这类服务可见——这才是"全部源"该有的语义。
    /// </summary>
    private List<string> ReadCheckedSources()
    {
        var checkedIds = new List<string>();

        for (var i = 0; i < _visibleChecks.Items.Count; i++)
        {
            if (_visibleChecks.GetItemChecked(i)) checkedIds.Add(_sources[i].Id);
        }

        return checkedIds.Count == _sources.Count ? [] : checkedIds;
    }

    private void AddService()
    {
        var port = _services.Count == 0 ? 8080 : _services.Max(s => s.Port) + 1;

        _services.Add(new ServiceDraft
        {
            Name = $"服务 {port}",
            Port = port,
            // 新服务默认要密码：加第二个服务的动机通常就是"这一个别让人随便看"。
            Password = string.Empty,
            Writable = false,
            Sources = [],
        });

        RefreshServices();
        _serviceList.SelectedIndex = _services.Count - 1;
    }

    private void RemoveService()
    {
        if (_services.Count <= 1)
        {
            MessageBox.Show(this, "至少要保留一个服务。", "MediaRack",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var index = _serviceList.SelectedIndex;
        if (index < 0) return;

        // 空串密码的 Draft 表示"用户还没填"，但保存时必须变成 null（= 公开）。
        _services.RemoveAt(index);
        RefreshServices();
    }

    private void BrowseMetaRoot()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "选择库目录（数据库、封面、导出产物都放这里）",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(_metaBox.Text) ? _metaBox.Text : string.Empty,
        };

        if (dialog.ShowDialog(this) == DialogResult.OK) _metaBox.Text = dialog.SelectedPath;
    }

    private void AddSource()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "选择视频所在的文件夹（B 站缓存目录或普通视频目录都可以）",
            UseDescriptionForTitle = true,
        };

        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        var root = dialog.SelectedPath;
        if (_sources.Any(s => string.Equals(s.Root, root, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(this, "这个文件夹已经加过了。", "MediaRack",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var id = MakeSourceId(root);
        var layout = LayoutDetector.Detect(root);

        _sources.Add(new SourceDraft
        {
            Id = id,
            Name = id,
            Root = root,
            Layout = layout,
        });

        RefreshSources();

        // 刻意不动各服务的可见源：
        // 可见源为空表示"全部源"，新加的源天然就属于它；而已经收窄成白名单的服务
        // （比如"家庭"只看 home）不该因为加了个新文件夹就自动扩容。
        // 这里只是把新源刷进属性面板的勾选列表。
        ShowSelectedService();
    }

    /// <summary>源 id 取文件夹名；重名时加序号。它就是接口 URL 里的那一段，所以不能带斜杠。</summary>
    private string MakeSourceId(string root)
    {
        var baseId = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(baseId)) baseId = "source";

        var id = baseId;
        var n = 2;
        while (_sources.Any(s => string.Equals(s.Id, id, StringComparison.Ordinal))) id = $"{baseId}-{n++}";

        return id;
    }

    private void RemoveSource()
    {
        var index = _sourceList.SelectedIndex;
        if (index < 0 || index >= _sources.Count) return;

        var id = _sources[index].Id;
        _sources.RemoveAt(index);

        // 服务里的可见源引用要一起清掉，否则保存后会得到一个指向不存在源的配置。
        foreach (var service in _services) service.Sources.Remove(id);

        RefreshSources();
        ShowSelectedService();
    }

    private void RefreshSources()
    {
        _sourceList.BeginUpdate();
        _sourceList.Items.Clear();

        foreach (var source in _sources)
        {
            var layout = source.Root.Length == 0 ? "" : source.Layout == SourceLayout.Dual ? "  · 双流" : "  · 单流";
            _sourceList.Items.Add(string.IsNullOrEmpty(source.Root)
                ? $"{source.Id}（文件夹需重新指定）"
                : $"{source.Id}{layout}   —   {source.Root}");
        }

        _sourceList.EndUpdate();
    }

    private void Save()
    {
        if (_services.Count == 0)
        {
            MessageBox.Show(this, "至少要有一个服务。", "MediaRack", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var duplicate = _services.GroupBy(s => s.Port).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            MessageBox.Show(this, $"两个服务用了同一个端口 {duplicate.Key}，每个服务必须独立端口。",
                "MediaRack", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(_metaBox.Text))
        {
            MessageBox.Show(this, "请选择库目录。", "MediaRack", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        Result = new MediaRackConfig
        {
            MetaRoot = _metaBox.Text.Trim(),
            Services = _services.Select(s => new ServiceConfig
            {
                Name = s.Name,
                Port = s.Port,
                Password = string.IsNullOrWhiteSpace(s.Password) ? null : s.Password,
                Writable = s.Writable,
                Sources = [.. s.Sources],
            }).ToArray(),
        };

        ScanAfterSave = _sources.Any(s => s.Root.Length > 0);

        // 源文件夹不属于 MediaRackConfig（那是"库"的事，不是"服务"的事），
        // 单独交给调用方去扫描登记。这里只把路径带出去。
        SourceRoots = _sources.Where(s => s.Root.Length > 0).Select(s => s.Root).ToList();

        DialogResult = DialogResult.OK;
        Close();
    }

    /// <summary>用户在向导里添加的源文件夹（保存后由调用方登记并扫描）。</summary>
    public IReadOnlyList<string> SourceRoots { get; private set; } = [];

    private sealed class ServiceDraft
    {
        public required string Name { get; set; }
        public required int Port { get; set; }
        public string? Password { get; set; }
        public bool Writable { get; set; } = true;
        public List<string> Sources { get; set; } = [];
    }

    private sealed class SourceDraft
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public string Root { get; set; } = string.Empty;
        public SourceLayout Layout { get; set; } = SourceLayout.Single;

        public string Display => string.IsNullOrEmpty(Root) ? Id : $"{Id}  ({Root})";
    }
}
