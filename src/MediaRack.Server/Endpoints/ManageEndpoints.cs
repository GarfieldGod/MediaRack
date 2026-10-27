using System.Text.Json;
using MediaRack.Models;
using MediaRack.Server.Hosting;
using MediaRack.Server.Jobs;
using MediaRack.Server.Json;
using MediaRack.Store;
using MediaRack.Virtual;

namespace MediaRack.Server.Endpoints;

internal static class ManageEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        MapMeta(app);
        MapMetaBatch(app);
        MapCollections(app);
        MapFavorites(app);
        MapProgress(app);
        MapScan(app);
        MapMaintenance(app);
    }

    // ── 维护 ─────────────────────────────────────────────────

    /// <summary>
    /// 清理缺失条目。<b>位置记录全删，档案保留</b>——扫描永远不会调用它，
    /// 这是用户在管理页显式点的动作（语义见 <see cref="MissingPurge"/>）。
    /// </summary>
    private static void MapMaintenance(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/maintenance/purge-missing",
            (string? source, LibraryAccess lib, ServiceScope scope) =>
            {
                if (!scope.Writable) return Api.ServiceReadOnly(scope.Profile.Name);
                if (!string.IsNullOrWhiteSpace(source) && !scope.CanSee(source))
                {
                    return Api.SourceNotFound(source);
                }

                return lib.With<IResult>(db =>
                {
                    var result = new MissingPurge(db).Run(
                        string.IsNullOrWhiteSpace(source) ? null : source);

                    // 条目删了，合集的投影也得跟着重算：只挂在被删条目上的空合集会一起消失。
                    var rebuilt = result.Deleted > 0 ? new CollectionRebuilder(db).Rebuild() : null;

                    return Results.Ok(new
                    {
                        deleted = result.Deleted,
                        anchorsKept = result.AnchorsKept,
                        orphanFingerprints = result.OrphanFingerprintsRemoved,
                        collections = rebuilt?.Collections ?? 0,
                    });
                });
            });
    }

    // ── 虚拟元数据 ────────────────────────────────────────────

    private static void MapMeta(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/meta/{source}/{key}",
            async (string source, string key, HttpRequest request, LibraryAccess lib, ServiceScope scope) =>
            {
                // 服务级只读优先于一切：公开到公网的服务不该能被改库，这一条与源级 virtualWrite 是两道独立的闸。
                if (!scope.Writable) return Api.ServiceReadOnly(scope.Profile.Name);
                if (!scope.CanSee(source)) return Api.SourceNotFound(source);

                if (!TryReadIfMatch(request, out var expectedRev, out var revError))
                {
                    return Api.BadRequest(revError!);
                }

                MediaPatchDto? dto;
                try
                {
                    dto = await request.ReadFromJsonAsync<MediaPatchDto>();
                }
                catch (JsonException ex)
                {
                    return Api.BadRequest($"请求体不是合法 JSON：{ex.Message}");
                }

                if (dto is null) return Api.BadRequest("缺少请求体");

                return lib.With<IResult>(db =>
                {
                    var config = new SourceRepository(db).Get(source);
                    if (config is null) return Api.SourceNotFound(source);

                    var entry = new MediaRepository(db).Get(source, key);
                    if (entry is null) return Api.MediaNotFound(source, key);

                    // collections 不在 MetaPatch 里（它是关联表不是字段），权限单独判。
                    // 闸门依旧在写入之前，所以整批拒绝时库里什么都不会变。
                    if (dto.Collections is not null &&
                        !MetaPolicy.Allows(config.VirtualWrite, MetaField.Collection))
                    {
                        return Api.Forbidden(
                            $"源 {source} 的虚拟写权限为 {DtoMapper.VirtualWriteName(config.VirtualWrite)}，不允许修改收藏夹归属");
                    }

                    // collection（结构合集归属 override）单独校验格式；权限走 MetaPolicy.Collection 闸。
                    if (dto.Collection is { } col && col.Trim().Length > 0 &&
                        !col.Trim().StartsWith("dir:") && !col.Trim().StartsWith("up:") &&
                        !col.Trim().StartsWith("custom:"))
                    {
                        return Api.BadRequest("collection 必须是 dir:/… 、up:… 或 custom:… 之一，空串 = 恢复自动归类");
                    }
                    if (dto.Collection is not null &&
                        !MetaPolicy.Allows(config.VirtualWrite, MetaField.Collection))
                    {
                        return Api.Forbidden(
                            $"源 {source} 的虚拟写权限为 {DtoMapper.VirtualWriteName(config.VirtualWrite)}，不允许修改合集归属");
                    }

                    var patch = new MetaPatch
                    {
                        TitleDisplay = dto.TitleDisplay,
                        Tags = dto.Tags,
                        Hidden = dto.Hidden,
                        Rotation = dto.Rotation,
                        Collection = dto.Collection,
                    };

                    var repo = new MetaRepository(db);
                    var result = repo.Apply(source, key, patch, expectedRev, actor: "http");

                    if (!result.Ok) return MapWriteFailure(result);

                    // 合集归属立即生效（不等下次扫描重建）：custom 按需建行，
                    // dir:/up: 目标不存在时 override 已存好、留到投影出现那天再落位。
                    if (dto.Collection is { } colKey)
                    {
                        new CollectionRebuilder(db).ApplyOverride(source, [key], colKey.Trim());
                    }

                    // collections = 这个条目属于哪些收藏夹（整体替换语义），与结构合集无关。
                    if (dto.Collections is not null)
                    {
                        var favorites = new FavoriteRepository(db);
                        if (dto.Collections.Any(fid => favorites.Get(fid) is null))
                        {
                            return Api.BadRequest("含不存在的收藏夹 id");
                        }

                        favorites.ReplaceMembership(source, key, dto.Collections);
                    }

                    var meta = repo.Get(source, key) ?? new MediaMeta { SourceId = source, Key = key };
                    return Results.Ok(DtoMapper.ToDto(meta));
                });
            });
    }

    private static IResult MapWriteFailure(WriteResult result) => result.Status switch
    {
        WriteStatus.Forbidden => Api.Forbidden(result.Message ?? "该源不允许此操作"),
        WriteStatus.NotFound => Api.Error(StatusCodes.Status404NotFound, "media_not_found",
            result.Message ?? "条目不存在"),
        WriteStatus.Conflict => Api.Conflict("rev_conflict", result.Message ?? "rev 冲突"),
        _ => Results.Ok(result.Value is null ? null : DtoMapper.ToDto(result.Value)),
    };

    // ── 批量元数据（管理页多选）────────────────────────────────
    //
    // 单条 POST /meta/{source}/{key} 是逐条语义（带 If-Match rev 校验）；
    // 批量操作一次改几十条，rev 校验没有意义（拿到列表那一刻就过期了），
    // 所以这里不复用那条路径：逐条 Apply、不带 expectedRev，失败的 key 单独报出来。

    private static void MapMetaBatch(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/meta/batch",
            async (HttpRequest request, LibraryAccess lib, ServiceScope scope) =>
            {
                if (!scope.Writable) return Api.ServiceReadOnly(scope.Profile.Name);

                MediaBatchPatchDto? dto;
                try
                {
                    dto = await request.ReadFromJsonAsync<MediaBatchPatchDto>();
                }
                catch (JsonException ex)
                {
                    return Api.BadRequest($"请求体不是合法 JSON：{ex.Message}");
                }

                if (dto is null || string.IsNullOrWhiteSpace(dto.Source)) return Api.BadRequest("缺少 source");
                if (dto.Keys is null || dto.Keys.Count == 0) return Api.BadRequest("缺少 keys");
                if (dto.Rotation is { } r && r is not (0 or 90 or 180 or 270))
                {
                    return Api.BadRequest($"rotation 只接受 0/90/180/270，收到 {r}");
                }
                if (dto.Collection is { } c && c.Trim().Length > 0 &&
                    !c.Trim().StartsWith("dir:") && !c.Trim().StartsWith("up:") &&
                    !c.Trim().StartsWith("custom:"))
                {
                    return Api.BadRequest("collection 必须是 dir:/… 、up:… 或 custom:… 之一，空串 = 恢复自动归类");
                }
                if (dto.Rotation is null && dto.Hidden is null && dto.Tags is null && dto.Collection is null)
                {
                    return Api.BadRequest("没有任何要修改的字段");
                }
                if (!scope.CanSee(dto.Source)) return Api.SourceNotFound(dto.Source);

                return lib.With<IResult>(db =>
                {
                    var config = new SourceRepository(db).Get(dto.Source);
                    if (config is null) return Api.SourceNotFound(dto.Source);

                    // 逐字段过闸：某个字段整源禁改就整批拒绝，不做"能改几个改几个"。
                    foreach (var (provided, field) in new[]
                             {
                                 (dto.Rotation is not null, MetaField.Rotation),
                                 (dto.Hidden is not null, MetaField.Hidden),
                                 (dto.Tags is not null, MetaField.Tags),
                                 (dto.Collection is not null, MetaField.Collection),
                             })
                    {
                        if (provided && !MetaPolicy.Allows(config.VirtualWrite, field))
                        {
                            return Api.Forbidden(
                                $"源 {dto.Source} 的虚拟写权限为 {DtoMapper.VirtualWriteName(config.VirtualWrite)}，不允许批量修改{MetaPolicy.Name(field)}");
                        }
                    }

                    var media = new MediaRepository(db);
                    var repo = new MetaRepository(db);
                    var patch = new MetaPatch
                    {
                        Tags = dto.Tags,
                        Hidden = dto.Hidden,
                        Rotation = dto.Rotation,
                        Collection = dto.Collection,
                    };

                    int updated = 0;
                    var failed = new List<string>();
                    var appliedKeys = new List<string>();
                    foreach (var key in dto.Keys)
                    {
                        if (media.Get(dto.Source, key) is null)
                        {
                            failed.Add(key);
                            continue;
                        }

                        if (repo.Apply(dto.Source, key, patch, expectedRev: null, actor: "http-batch").Ok)
                        {
                            updated++;
                            appliedKeys.Add(key);
                        }
                        else
                        {
                            failed.Add(key);
                        }
                    }

                    // 合集归属立即生效（不等下次扫描重建）。
                    if (dto.Collection is { } colKey)
                    {
                        new CollectionRebuilder(db).ApplyOverride(dto.Source, appliedKeys, colKey.Trim());
                    }

                    return Results.Ok(new { updated, failed });
                });
            });
    }

    /// <summary><c>If-Match</c> 可以带引号（HTTP 规范如此）也可以不带，两种都收。</summary>
    private static bool TryReadIfMatch(HttpRequest request, out long? expectedRev, out string? error)
    {
        expectedRev = null;
        error = null;

        var raw = request.Headers.IfMatch.ToString().Trim();
        if (raw.Length == 0) return true;

        raw = raw.Trim('"');
        if (long.TryParse(raw, out var rev) && rev >= 0)
        {
            expectedRev = rev;
            return true;
        }

        error = $"If-Match 必须是整数 rev，收到 {raw}";
        return false;
    }

    // ── 合集（结构层，只读）────────────────────────────────────
    //
    // v4 起合集是媒体库的投影：single 目录 / dual UP 主，源级、平铺、排他。
    // 客户端只读——用户想"整理"东西请用收藏（下一段），那是另一层，多对多、可跨端。

    private static void MapCollections(IEndpointRouteBuilder app)
    {
        // ?source= 只看某个源；不带则返回全部可见源的合集（聚合视图）。
        app.MapGet("/api/v1/collections", (HttpRequest request, LibraryAccess lib, ServiceScope scope) =>
            lib.With(db =>
        {
            var repo = new CollectionRepository(db);
            var source = Api.StrQuery(request, "source");

            IReadOnlyList<CollectionRow> rows;
            if (!string.IsNullOrWhiteSpace(source))
            {
                if (!scope.CanSee(source)) return Api.SourceNotFound(source);
                rows = repo.ListBySource(source);
            }
            else
            {
                // 受限服务不能把别的源的合集名漏出来——名字本身就是信息。
                rows = repo.ListAll().Where(r => scope.CanSee(r.SourceId)).ToList();
            }

            return Results.Ok(rows.Select(ToDto).ToList());
        }));
    }

    private static CollectionDto ToDto(CollectionRow row) =>
        new(row.Id, row.Name, row.SourceId, row.Kind, row.Path, row.ItemCount);

    // ── 收藏（用户层，可写、跨端）──────────────────────────────
    //
    // 三个动作面：收藏夹本身（增删改）、收藏的视频（/library?favorite=<id> 读）、收藏的合集。
    // 收藏是"用户的组织"，与结构合集无关——一个视频可以在多个夹子里，一个夹子可以跨源。

    private static void MapFavorites(IEndpointRouteBuilder app)
    {
        // source 可选：Web 侧栏选中某个源时传它，计数只算该源的收藏；
        // 不传 = 全库（App 的收藏页、聚合视图都按这个口径）。
        app.MapGet("/api/v1/favorites", (HttpRequest request, LibraryAccess lib, ServiceScope scope) =>
            lib.With(db =>
            {
                var sourceId = Api.StrQuery(request, "source");
                if (sourceId is not null && new SourceRepository(db).Get(sourceId) is null)
                    return Api.SourceNotFound(sourceId);
                if (sourceId is not null && !scope.CanSee(sourceId))
                    return Api.SourceNotFound(sourceId);

                // 计数口径：显式 source → 该源；没传 → 服务可见源（门户只有自己那一个，
                // 根服务是 null=全库）。不传 scope 的话门户页与 App 都会看到整台机器的收藏数。
                return Results.Ok(
                    new FavoriteRepository(db).List(sourceId, scope.VisibleSourceIds).Select(ToDto).ToList());
            }));

        app.MapPost("/api/v1/favorites", async (HttpRequest request, LibraryAccess lib, ServiceScope scope) =>
        {
            if (!scope.Writable) return Api.ServiceReadOnly(scope.Profile.Name);

            FavoritePatchDto? dto;
            try
            {
                dto = await request.ReadFromJsonAsync<FavoritePatchDto>();
            }
            catch (JsonException ex)
            {
                return Api.BadRequest($"请求体不是合法 JSON：{ex.Message}");
            }

            if (dto is null || string.IsNullOrWhiteSpace(dto.Name)) return Api.BadRequest("name 不能为空");

            return lib.With<IResult>(db =>
            {
                var repo = new FavoriteRepository(db);
                try
                {
                    var id = repo.Create(dto.Name);
                    return Results.Ok(ToDto(repo.Get(id)!));
                }
                catch (InvalidOperationException ex)
                {
                    return Api.Conflict("favorite_exists", ex.Message);
                }
                catch (ArgumentException ex)
                {
                    return Api.BadRequest(ex.Message);
                }
            });
        });

        app.MapPatch("/api/v1/favorites/{id}", async (string id, HttpRequest request,
            LibraryAccess lib, ServiceScope scope) =>
        {
            if (!scope.Writable) return Api.ServiceReadOnly(scope.Profile.Name);

            FavoritePatchDto? dto;
            try
            {
                dto = await request.ReadFromJsonAsync<FavoritePatchDto>();
            }
            catch (JsonException ex)
            {
                return Api.BadRequest($"请求体不是合法 JSON：{ex.Message}");
            }

            if (dto is null || string.IsNullOrWhiteSpace(dto.Name)) return Api.BadRequest("name 不能为空");

            return lib.With<IResult>(db =>
            {
                var repo = new FavoriteRepository(db);
                if (repo.Get(id) is null) return FavoriteNotFound(id);

                try
                {
                    repo.Rename(id, dto.Name);
                    return Results.Ok(ToDto(repo.Get(id)!));
                }
                catch (InvalidOperationException ex)
                {
                    return Api.Conflict("favorite_exists", ex.Message);
                }
                catch (ArgumentException ex)
                {
                    return Api.BadRequest(ex.Message);
                }
            });
        });

        app.MapDelete("/api/v1/favorites/{id}", (string id, LibraryAccess lib, ServiceScope scope) =>
        {
            if (!scope.Writable) return Api.ServiceReadOnly(scope.Profile.Name);

            return lib.With<IResult>(db =>
            {
                var repo = new FavoriteRepository(db);
                return repo.Delete(id)
                    ? Results.Ok(new { deleted = id })
                    : FavoriteNotFound(id);
            });
        });

        // 收藏 / 取消收藏视频。remove 不需要写权限"源可见"之外的额外条件——
        // 收回自己的收藏动作永远允许（否则被删的源会留下清不掉的收藏）。
        app.MapPost("/api/v1/favorites/{id}/items", async (string id, HttpRequest request,
            LibraryAccess lib, ServiceScope scope) =>
        {
            if (!scope.Writable) return Api.ServiceReadOnly(scope.Profile.Name);

            FavoriteItemsPatchDto? dto;
            try
            {
                dto = await request.ReadFromJsonAsync<FavoriteItemsPatchDto>();
            }
            catch (JsonException ex)
            {
                return Api.BadRequest($"请求体不是合法 JSON：{ex.Message}");
            }

            if (dto is null) return Api.BadRequest("缺少请求体");

            return lib.With<IResult>(db =>
            {
                var repo = new FavoriteRepository(db);
                if (repo.Get(id) is null) return FavoriteNotFound(id);

                // 请求体里的 source 客户端可以瞎写，可见性与存在性在这里判。
                var adds = dto.Add ?? [];
                if (adds.Any(r => !scope.CanSee(r.Source))) return SourceNotVisible();

                var media = new MediaRepository(db);
                var rejected = adds.Where(r => media.Get(r.Source, r.Key) is null).ToList();
                if (rejected.Count > 0)
                {
                    return Api.Error(StatusCodes.Status404NotFound, "media_not_found",
                        $"条目不存于源 {rejected[0].Source}：{rejected[0].Key}");
                }

                var added = adds.Count == 0
                    ? 0
                    : repo.AddItems(id, adds.Select(r => new FavoriteRepository.ItemRef(r.Source, r.Key)).ToList());

                var removed = dto.Remove is { Count: > 0 }
                    ? repo.RemoveItems(id, dto.Remove.Select(r => new FavoriteRepository.ItemRef(r.Source, r.Key)).ToList())
                    : 0;

                return Results.Ok(new { added, removed });
            });
        });

        // 收藏的合集（= 关注的 UP 主 / 文件夹）
        app.MapGet("/api/v1/favorites/{id}/collections", (string id, LibraryAccess lib, ServiceScope scope) =>
            lib.With(db =>
        {
            var repo = new FavoriteRepository(db);
            if (repo.Get(id) is null) return FavoriteNotFound(id);

            var collections = new CollectionRepository(db);
            var rows = repo.ListCollections(id)
                .Select(cid => collections.Get(cid))
                .Where(r => r is not null && scope.CanSee(r.SourceId))
                .Select(r => ToDto(r!))
                .ToList();

            return Results.Ok(rows);
        }));

        app.MapPost("/api/v1/favorites/{id}/collections", async (string id, HttpRequest request,
            LibraryAccess lib, ServiceScope scope) =>
        {
            if (!scope.Writable) return Api.ServiceReadOnly(scope.Profile.Name);

            FavoriteCollectionsPatchDto? dto;
            try
            {
                dto = await request.ReadFromJsonAsync<FavoriteCollectionsPatchDto>();
            }
            catch (JsonException ex)
            {
                return Api.BadRequest($"请求体不是合法 JSON：{ex.Message}");
            }

            if (dto is null) return Api.BadRequest("缺少请求体");

            return lib.With<IResult>(db =>
            {
                var repo = new FavoriteRepository(db);
                if (repo.Get(id) is null) return FavoriteNotFound(id);

                var collections = new CollectionRepository(db);
                var adds = dto.Add ?? [];
                foreach (var cid in adds)
                {
                    var row = collections.Get(cid);
                    if (row is null)
                    {
                        return Api.Error(StatusCodes.Status404NotFound, "collection_not_found",
                            $"合集不存在：{cid}");
                    }

                    if (!scope.CanSee(row.SourceId)) return SourceNotVisible();
                }

                var added = adds.Count == 0 ? 0 : repo.AddCollections(id, adds);
                var removed = dto.Remove is { Count: > 0 } ? repo.RemoveCollections(id, dto.Remove) : 0;
                return Results.Ok(new { added, removed });
            });
        });
    }

    private static FavoriteDto ToDto(FavoriteRepository.FavoriteRow row) =>
        new(row.Id, row.Name, row.ItemCount, row.CollectionCount);

    private static IResult FavoriteNotFound(string id) =>
        Api.Error(StatusCodes.Status404NotFound, "favorite_not_found", $"收藏夹不存在：{id}");

    private static IResult SourceNotVisible() =>
        Api.Error(StatusCodes.Status403Forbidden, "source_not_visible", "请求里包含本服务不可见的源");

    // ── 播放进度 ──────────────────────────────────────────────

    private static void MapProgress(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/progress/{source}/{key}",
            async (string source, string key, HttpRequest request, LibraryAccess lib, ServiceScope scope) =>
            {
                if (!scope.Writable) return Api.ServiceReadOnly(scope.Profile.Name);
                if (!scope.CanSee(source)) return Api.SourceNotFound(source);

                ProgressPatchDto? dto;
                try
                {
                    dto = await request.ReadFromJsonAsync<ProgressPatchDto>();
                }
                catch (JsonException ex)
                {
                    return Api.BadRequest($"请求体不是合法 JSON：{ex.Message}");
                }

                if (dto is null) return Api.BadRequest("缺少请求体");
                if (dto.PosMs < 0) return Api.BadRequest("posMs 不能为负");

                return lib.With<IResult>(db =>
                {
                    var patch = new MetaPatch
                    {
                        // 看完了就把进度归零，否则下次"继续观看"会从片尾开始。
                        PlayPosMs = dto.Completed == true ? 0 : dto.PosMs,
                        MarkedPlayed = true,
                    };

                    var result = new MetaRepository(db).Apply(source, key, patch, actor: "http");
                    return result.Ok
                        ? Results.Ok(DtoMapper.ToDto(result.Value!))
                        : MapWriteFailure(result);
                });
            });
    }

    // ── 扫描 ──────────────────────────────────────────────────

    private static void MapScan(IEndpointRouteBuilder app)
    {
        // 任务列表：给"刷新页面 / 换了个标签页"用——扫描是服务端任务，
        // 客户端不知道 jobId 时也能把它找回来接着显示进度。
        app.MapGet("/api/v1/scan", (ScanJobQueue jobs, ServiceScope scope) =>
            !scope.Writable
                ? Api.ServiceReadOnly(scope.Profile.Name)
                : Results.Ok(jobs.Recent()));

        app.MapPost("/api/v1/scan", async (HttpRequest request, ScanJobQueue jobs, ServiceScope scope) =>
        {
            if (!scope.Writable) return Api.ServiceReadOnly(scope.Profile.Name);

            ScanRequestDto? dto = null;

            // 请求体可以省略：那时表示"把所有已登记的源各扫一遍"，由客户端多次调用。
            if (request.ContentLength is > 0 || request.Headers.TransferEncoding.Count > 0)
            {
                try
                {
                    dto = await request.ReadFromJsonAsync<ScanRequestDto>();
                }
                catch (JsonException ex)
                {
                    return Api.BadRequest($"请求体不是合法 JSON：{ex.Message}");
                }
            }

            dto ??= new ScanRequestDto(null, null, null, null, null);

            if (string.IsNullOrWhiteSpace(dto.SourceId) && string.IsNullOrWhiteSpace(dto.Root))
            {
                return Api.BadRequest("请提供 sourceId（扫描已登记的源）或 root（登记新源）");
            }

            // 重扫某个源要能看见它；带 root 加新源是纯管理动作，已经由上面的 writable 闸管住。
            if (!string.IsNullOrWhiteSpace(dto.SourceId) && !scope.CanSee(dto.SourceId))
            {
                return Api.SourceNotFound(dto.SourceId);
            }

            var job = jobs.Start(dto);
            return Results.Json(new JobAcceptedDto(job.JobId), statusCode: StatusCodes.Status202Accepted);
        });

        app.MapGet("/api/v1/scan/{jobId}", (string jobId, ScanJobQueue jobs) =>
        {
            var job = jobs.Get(jobId);
            return job is null
                ? Api.Error(StatusCodes.Status404NotFound, "job_not_found", $"任务不存在：{jobId}")
                : Results.Ok(job);
        });

        // 取消一个扫描任务。running 会进入 cancelling（扫描/抽帧在下个检查点停下）；
        // 已结束的任务返回 404 之外的 200（幂等，前端不用区分）。
        app.MapPost("/api/v1/scan/{jobId}/cancel", (string jobId, ScanJobQueue jobs, ServiceScope scope) =>
        {
            if (!scope.Writable) return Api.ServiceReadOnly(scope.Profile.Name);

            var job = jobs.Cancel(jobId);
            return job is null
                ? Api.Error(StatusCodes.Status404NotFound, "job_not_found", $"任务不存在或已结束：{jobId}")
                : Results.Ok(job);
        });
    }
}
