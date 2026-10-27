using System.Text.Json;
using MediaRack.Models;
using MediaRack.Scan;
using MediaRack.Server.Hosting;
using MediaRack.Server.Jobs;
using MediaRack.Server.Json;
using MediaRack.Store;

namespace MediaRack.Server.Endpoints;

/// <summary>
/// 管理页端点。<b>只在本机根路径下可用</b>——源门户（<c>/s/{slug}</c>）一律 403。
/// <para>
/// 这条限制不是"顺手加的校验"：这些响应里带每个源的明文令牌。如果门户也能调，
/// 那么拿到电影源令牌的电视盒子，就能顺着 <c>/api/v1/services</c> 把音乐源的令牌读走，
/// 源级隔离就白做了。
/// </para>
/// </summary>
internal static class AdminEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/services", ListService);
        app.MapPatch("/api/v1/services", UpdateService);
        app.MapPost("/api/v1/sources", CreateSource);
        app.MapPatch("/api/v1/sources/{id}", UpdateSource);
        app.MapDelete("/api/v1/sources/{id}", DeleteSource);
        app.MapPost("/api/v1/sources/{id}/folders", AddFolder);
        app.MapPatch("/api/v1/sources/{id}/folders/{folderId:long}", UpdateFolder);
        app.MapDelete("/api/v1/sources/{id}/folders/{folderId:long}", RemoveFolder);
    }

    /// <summary>门户请求一律拒绝。返回 null 表示"这是本机管理页"。用 Slug 判而不是 IP：
    /// 门户 profile 一定带 slug、根路径一定不带，比判断远端地址可靠（测试环境没有远端地址）。</summary>
    private static IResult? DenyIfPortal(ServiceScope scope) =>
        scope.Profile.Slug is null ? null : Api.Forbidden("管理端点只能在本机管理页使用");

    // ── 读 ────────────────────────────────────────────────────

    private static IResult ListService(LibraryAccess lib, ServiceScope scope, ServiceCatalog catalog)
    {
        if (DenyIfPortal(scope) is { } denied) return denied;

        var port = scope.Profile.Port;

        var hosts = new List<AdminHostDto> { new("127.0.0.1", "loopback", port) };
        hosts.AddRange(HostCatalog.Local().Select(h => new AdminHostDto(h.Address, KindCode(h.Kind), port)));

        var sources = lib.With(db =>
        {
            var repo = new SourceRepository(db);
            return repo.List().Select(s => ToDto(s, (int)db.CountAvailable(s.Id), (int)db.CountMissing(s.Id), db.TotalMediaBytes(s.Id))).ToList();
        });

        return Results.Ok(new AdminServiceDto(
            Name: scope.Profile.Name,
            Port: port,
            PublicUrl: PublicUrlStore.Read(catalog),
            Hosts: hosts,
            Sources: sources));
    }

    // ── 写服务器（目前只有公网地址）──────────────────────────────

    private static async Task<IResult> UpdateService(
        HttpRequest request, ServiceScope scope, ServiceCatalog catalog)
    {
        if (DenyIfPortal(scope) is { } denied) return denied;

        UpdateServiceDto? dto;
        try
        {
            dto = await request.ReadFromJsonAsync<UpdateServiceDto>();
        }
        catch (JsonException ex)
        {
            return Api.BadRequest($"请求体不是合法 JSON：{ex.Message}");
        }

        if (dto is null) return Api.BadRequest("缺少请求体");

        var path = catalog.ConfigPath;
        if (string.IsNullOrEmpty(path) || path == "<testing>")
        {
            return Api.BadRequest("当前启动没有配置文件，公网地址无处保存；请先创建 config.json");
        }

        MediaRackConfig config;
        try
        {
            config = MediaRackConfig.Load(path);
        }
        catch (Exception ex)
        {
            return Api.BadRequest(ex.Message);
        }

        var url = string.IsNullOrWhiteSpace(dto.PublicUrl) ? null : dto.PublicUrl.Trim();
        if (url is not null && !url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return Api.BadRequest("公网地址要以 http:// 或 https:// 开头");
        }

        (config with { PublicUrl = url }).Save(path);
        return Results.Ok(new { publicUrl = url });
    }

    // ── 建源 ──────────────────────────────────────────────────

    /// <summary>
    /// 建源只登记"身份"：名称 / 源标识 / 令牌。内容靠<b>文件夹列表</b>（多对多、布局跟文件夹走），
    /// 在源详情里逐个添加——建源时只挑一个文件夹的老表单，让人误以为一个源只能有一个根。
    /// </summary>
    private static async Task<IResult> CreateSource(
        HttpRequest request, LibraryAccess lib, ServiceScope scope)
    {
        if (DenyIfPortal(scope) is { } denied) return denied;

        CreateSourceDto? dto;
        try
        {
            dto = await request.ReadFromJsonAsync<CreateSourceDto>();
        }
        catch (JsonException ex)
        {
            return Api.BadRequest($"请求体不是合法 JSON：{ex.Message}");
        }

        if (dto is null) return Api.BadRequest("缺少请求体");

        // 令牌：手写优先，留空就生成。长度不做硬校验——用户有权用短令牌，
        // 前端会给"短令牌能读整个源"的提示，但不拦。
        var token = string.IsNullOrWhiteSpace(dto.Token) ? SourceIdentity.NewToken() : dto.Token.Trim();

        return lib.With<IResult>(db =>
        {
            var repo = new SourceRepository(db);

            // 手写的 slug 撞名要报错（用户明确要这个名字，悄悄改成 source-2 是惊吓）；
            // 自动生成的撞名才走 UniqueSlug 的 -2 / -3 后缀。
            var manualSlug = !string.IsNullOrWhiteSpace(dto.Slug);
            var candidate = manualSlug
                ? dto.Slug!.Trim()
                : SourceIdentity.CandidateSlug(dto.Name ?? "source", dto.Name);

            if (!SourceIdentity.IsValidSlug(candidate))
            {
                return Api.BadRequest(
                    $"源标识只能用 a-z、0-9、- 和 _，且以字母或数字开头（收到：{candidate}）");
            }

            if (manualSlug && repo.IsSlugTaken(candidate))
            {
                return Api.Conflict("slug_taken", $"源标识已被占用：{candidate}");
            }

            var slug = manualSlug ? candidate : repo.UniqueSlug(candidate);

            // id 与 slug 同源；老库里可能有同名的非 ASCII id，撞上就加后缀。
            var id = slug;
            var existingIds = repo.List().Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
            for (var n = 2; existingIds.Contains(id); n++) id = $"{slug}-{n}";

            var name = string.IsNullOrWhiteSpace(dto.Name) ? id : dto.Name.Trim();

            // 新源零文件夹：内容在详情页「文件夹来源」里登记后再扫。
            repo.Upsert(new SourceConfig
            {
                Id = id,
                Name = name,
                Slug = slug,
                Token = token,
                VirtualWrite = VirtualWrite.Full,
            });

            var created = repo.Get(id)!;
            return Results.Ok(new CreateSourceResultDto(ToDto(created, 0, 0, 0), JobId: null));
        });
    }

    // ── 删源 ──────────────────────────────────────────────────

    /// <summary>
    /// 删除源。<b>磁盘文件一行不动</b>，删的是库里的登记。
    /// <para>
    /// <c>?withArchive=false</c>（默认）：只删源登记、文件夹、该源全部条目与合集投影。
    /// 旋转/进度/标题/标签/合集归属（<c>media_meta</c>）与收藏（<c>favorite_item</c>）留着——
    /// 这些键在 <c>(source_id, key)</c> 或内容指纹上，文件搬回来（哪怕重新加源）能自动认回。
    /// </para>
    /// <para>
    /// <c>?withArchive=true</c>：连档案与收藏一起删，删完就真的没有痕迹了。
    /// </para>
    /// </summary>
    private static IResult DeleteSource(
        string id, HttpRequest request, LibraryAccess lib, ServiceScope scope, ScanJobQueue jobs)
    {
        if (DenyIfPortal(scope) is { } denied) return denied;

        var withArchive = Api.BoolQuery(request, "withArchive", false);

        // 先掐掉这个源还没跑完的扫描：否则删除事务提交后，扫描线程下一批 Upsert
        // 会把条目和源行一起写回来，用户看到"删了又长回来"。
        var cancelled = jobs.CancelForSource(id);

        return lib.With<IResult>(db =>
        {
            var result = new SourceRemoval(db).Run(id, withArchive);
            if (result is null) return Api.SourceNotFound(id);

            return Results.Ok(new DeleteSourceResultDto(
                SourceId: result.SourceId,
                MediaRemoved: result.MediaRemoved,
                FoldersRemoved: result.FoldersRemoved,
                CollectionsRemoved: result.CollectionsRemoved,
                MetaRemoved: result.MetaRemoved,
                FavoriteItemsRemoved: result.FavoriteItemsRemoved,
                MetaKept: result.MetaKept,
                FavoriteItemsKept: result.FavoriteItemsKept,
                FingerprintsRemoved: result.FingerprintsRemoved,
                CancelledJobs: cancelled,
                WithArchive: withArchive));
        });
    }

    // ── 源的文件夹列表（v3：多对多，布局跟文件夹走）──────────────

    private static async Task<IResult> AddFolder(
        string id, HttpRequest request, LibraryAccess lib, ServiceScope scope, ScanJobQueue jobs)
    {
        if (DenyIfPortal(scope) is { } denied) return denied;

        CreateFolderDto? dto;
        try
        {
            dto = await request.ReadFromJsonAsync<CreateFolderDto>();
        }
        catch (JsonException ex)
        {
            return Api.BadRequest($"请求体不是合法 JSON：{ex.Message}");
        }

        if (dto is null || string.IsNullOrWhiteSpace(dto.Root)) return Api.BadRequest("请填写文件夹地址");

        string root;
        try
        {
            root = Path.GetFullPath(dto.Root.Trim());
        }
        catch (Exception ex)
        {
            return Api.BadRequest($"路径不合法：{ex.Message}");
        }

        if (!Directory.Exists(root)) return Api.BadRequest($"文件夹不存在：{root}");

        SourceLayout? layout;
        try
        {
            layout = ParseFolderLayout(dto.Layout, root);
        }
        catch (InvalidOperationException ex)
        {
            return Api.BadRequest(ex.Message);
        }

        return lib.With<IResult>(db =>
        {
            var repo = new SourceRepository(db);
            if (repo.Get(id) is null) return Api.SourceNotFound(id);

            var folder = repo.AddFolder(id, root, layout);
            if (folder is null)
            {
                return Api.Conflict("folder_exists", "这个文件夹已经登记在该源里了");
            }

            // auto 布局先识别一次写回，管理页立刻能看见"dual/single"而不是"auto"。
            var resolved = LayoutDetector.Resolve(folder);
            if (folder.Layout is null) repo.SetFolderLayout(id, folder.Id, resolved);

            var job = jobs.Start(new ScanRequestDto(id, null, null, null, null));
            return Results.Ok(new CreateSourceResultDto(
                ToDto(repo.Get(id)!, (int)db.CountAvailable(id), (int)db.CountMissing(id), db.TotalMediaBytes(id)), job.JobId));
        });
    }

    private static async Task<IResult> UpdateFolder(
        string id, long folderId, HttpRequest request, LibraryAccess lib, ServiceScope scope,
        ScanJobQueue jobs)
    {
        if (DenyIfPortal(scope) is { } denied) return denied;

        UpdateFolderDto? dto;
        try
        {
            dto = await request.ReadFromJsonAsync<UpdateFolderDto>();
        }
        catch (JsonException ex)
        {
            return Api.BadRequest($"请求体不是合法 JSON：{ex.Message}");
        }

        if (dto is null || string.IsNullOrWhiteSpace(dto.Layout))
        {
            return Api.BadRequest("请提供 layout（auto / single / dual）");
        }

        return lib.With<IResult>(db =>
        {
            var repo = new SourceRepository(db);
            var folder = repo.GetFolder(id, folderId);
            if (folder is null) return Api.Error(StatusCodes.Status404NotFound, "folder_not_found", "文件夹不存在");

            SourceLayout? layout;
            try
            {
                layout = ParseFolderLayout(dto.Layout, folder.Root);
            }
            catch (InvalidOperationException ex)
            {
                return Api.BadRequest(ex.Message);
            }

            repo.SetFolderLayout(id, folderId, layout);

            // 布局决定条目怎么解释，改了必须重扫。
            var job = jobs.Start(new ScanRequestDto(id, null, null, null, null));
            return Results.Ok(new CreateSourceResultDto(
                ToDto(repo.Get(id)!, (int)db.CountAvailable(id), (int)db.CountMissing(id), db.TotalMediaBytes(id)), job.JobId));
        });
    }

    private static IResult RemoveFolder(
        string id, long folderId, LibraryAccess lib, ServiceScope scope, ScanJobQueue jobs)
    {
        if (DenyIfPortal(scope) is { } denied) return denied;

        return lib.With<IResult>(db =>
        {
            var repo = new SourceRepository(db);
            if (repo.GetFolder(id, folderId) is null)
            {
                return Api.Error(StatusCodes.Status404NotFound, "folder_not_found", "文件夹不存在");
            }

            repo.RemoveFolder(id, folderId);

            // 条目不在这删：重扫后该文件夹下的条目不再出现，MarkMissingExcept 自然清掉。
            // 磁盘文件从头到尾没人碰。
            var job = jobs.Start(new ScanRequestDto(id, null, null, null, null));
            return Results.Ok(new CreateSourceResultDto(
                ToDto(repo.Get(id)!, (int)db.CountAvailable(id), (int)db.CountMissing(id), db.TotalMediaBytes(id)), job.JobId));
        });
    }

    // ── 改源 ──────────────────────────────────────────────────

    private static async Task<IResult> UpdateSource(
        string id, HttpRequest request, LibraryAccess lib, ServiceScope scope)
    {
        if (DenyIfPortal(scope) is { } denied) return denied;

        UpdateSourceDto? dto;
        try
        {
            dto = await request.ReadFromJsonAsync<UpdateSourceDto>();
        }
        catch (JsonException ex)
        {
            return Api.BadRequest($"请求体不是合法 JSON：{ex.Message}");
        }

        if (dto is null) return Api.BadRequest("缺少请求体");

        return lib.With<IResult>(db =>
        {
            var repo = new SourceRepository(db);
            if (repo.Get(id) is null) return Api.SourceNotFound(id);

            if (dto.Name is not null)
            {
                var name = dto.Name.Trim();
                if (name.Length == 0) return Api.BadRequest("名称不能为空");
                repo.SetName(id, name);
            }

            if (dto.Slug is not null)
            {
                var slug = dto.Slug.Trim();
                if (!SourceIdentity.IsValidSlug(slug))
                {
                    return Api.BadRequest(
                        $"源标识只能用 a-z、0-9、- 和 _，且以字母或数字开头（收到：{slug}）");
                }

                if (repo.IsSlugTaken(slug, id))
                {
                    return Api.Conflict("slug_taken", $"源标识已被占用：{slug}");
                }

                // 改名不迁移磁盘、不动 id：id 是 media/meta/collection 的引用前缀，
                // 改它要级联四张表，而用户要改的只是"别人怎么访问我"。
                repo.SetSlug(id, slug);
            }

            if (dto.Token is not null)
            {
                var token = dto.Token.Trim();
                if (token.Length == 0)
                {
                    return Api.BadRequest("令牌不能为空：空令牌等于把整个源公开给任何人");
                }

                repo.SetToken(id, token);
            }

            if (dto.VirtualWrite is not null)
            {
                var level = ParseVirtualWrite(dto.VirtualWrite);
                if (level is null) return Api.BadRequest("访问等级只接受 none / whitelist / full");
                repo.SetVirtualWrite(id, level.Value);
            }

            var updated = repo.Get(id)!;
            return Results.Ok(ToDto(updated, (int)db.CountAvailable(id), (int)db.CountMissing(id), db.TotalMediaBytes(id)));
        });
    }

    // ── 辅助 ──────────────────────────────────────────────────

    private static AdminSourceDto ToDto(SourceConfig source, int itemCount, int missingCount, long totalBytes) => new(
        Id: source.Id,
        Slug: source.Slug ?? string.Empty,
        Name: source.Name,
        Token: source.Token,
        Root: source.Root ?? source.Folders.FirstOrDefault()?.Root ?? string.Empty,
        Layout: source.Layout == SourceLayout.Dual ? "dual" : "single",
        VirtualWrite: source.VirtualWrite switch
        {
            VirtualWrite.None => "none",
            VirtualWrite.Whitelist => "whitelist",
            _ => "full",
        },
        ItemCount: itemCount,
        MissingCount: missingCount,
        TotalBytes: totalBytes,
        LastScanAt: source.LastScanAt,
        // 移动硬盘没插是最常见的情况，管理页要把这一眼看出来。
        Reachable: DtoMapper.Reachable(source),
        Folders: source.Folders.Select(f => new AdminFolderDto(
            Id: f.Id,
            Root: f.Root,
            Layout: f.Layout is null ? "auto" : (f.Layout == SourceLayout.Dual ? "dual" : "single"),
            Reachable: Directory.Exists(f.Root))).ToList());

    private static string KindCode(HostCatalog.Kind kind) => kind switch
    {
        HostCatalog.Kind.Virtual => "virtual",
        HostCatalog.Kind.Lan => "lan",
        _ => "other",
    };

    /// <summary>文件夹布局的解析：auto = null（扫描时现场识别）。</summary>
    private static SourceLayout? ParseFolderLayout(string? value, string root) => value?.ToLowerInvariant() switch
    {
        null or "" or "auto" => null,
        "dual" => SourceLayout.Dual,
        "single" => SourceLayout.Single,
        _ => throw new InvalidOperationException($"layout 只接受 auto / dual / single，收到 {value}"),
    };

    private static VirtualWrite? ParseVirtualWrite(string value) => value.Trim().ToLowerInvariant() switch
    {
        "none" => VirtualWrite.None,
        "whitelist" => VirtualWrite.Whitelist,
        "full" => VirtualWrite.Full,
        _ => null,
    };
}
