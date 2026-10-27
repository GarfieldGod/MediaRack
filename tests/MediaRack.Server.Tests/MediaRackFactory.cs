using MediaRack.Media;
using MediaRack.Models;
using MediaRack.Store;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace MediaRack.Server.Tests;

/// <summary>
/// 一套真实的库 + 真实的源目录，跑在真实的 HTTP 管线上。
/// <para>
/// 不用 mock：这个服务端几乎全部逻辑都是"库里的相对路径 → 磁盘上的文件 → 带 Range 推出去"，
/// mock 掉磁盘就什么都没测到。源文件用的是一段固定字节（不是真视频），
/// 因为 Range 与 MIME 的验证不需要能解码的内容——真解码验证在验收里用 ffprobe 做过。
/// </para>
/// </summary>
public class MediaRackFactory : WebApplicationFactory<Program>
{
    // 源与内容键全部固定，断言里直接引用。
    public const string DualA = "a";
    public const string DualB = "b";
    public const string Single = "single";

    public const string KeyOnlyA = "1001_c2002";
    public const string KeyShared = "3003_c4004";
    public const string KeySingle = "s_abc123456789012";

    public const int VideoSize = 4096;
    public const int AudioSize = 1024;
    public const int SingleSize = 8192;

    public string Root { get; } = Path.Combine(
        Path.GetTempPath(), "mrack-srv-" + Guid.NewGuid().ToString("N")[..10]);

    public string MetaRoot => Path.Combine(Root, "meta");

    public string DualRootA => Path.Combine(Root, "src-a");

    public string DualRootB => Path.Combine(Root, "src-b");

    public string SingleRoot => Path.Combine(Root, "src-single");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("MediaRack:MetaRoot", MetaRoot);
        // 预算设成 1 MB：小到一个产物都留不住，方便测淘汰。
        builder.UseSetting("MediaRack:ExportBudgetBytes", (2 * 1024 * 1024).ToString());
        builder.UseEnvironment("Testing");

        if (_token is not null) builder.UseSetting("MediaRack:Token", _token);

        // 多服务配置走这条注入：测试环境不读真实 config.json，只认这个键。
        if (_servicesJson is not null) builder.UseSetting("MediaRack:Services", _servicesJson);
    }

    private readonly string? _token;
    private readonly string? _servicesJson;

    /// <summary>无参构造给 <c>IClassFixture</c> 用——xUnit 不会解析可选参数，有参构造它会当成注入失败。</summary>
    public MediaRackFactory() : this(null, null) { }

    /// <summary>带令牌的变体。protected 是有意的：只给需要鉴权的子类用，xUnit 不会拿它当注入候选。</summary>
    protected MediaRackFactory(string? token) : this(token, null) { }

    /// <summary>
    /// 带服务表的变体。<paramref name="servicesJson"/> 是 <c>MediaRackConfig.services</c> 的 JSON 数组。
    /// </summary>
    protected MediaRackFactory(string? token, string? servicesJson)
    {
        _token = token;
        _servicesJson = servicesJson;

        Directory.CreateDirectory(MetaRoot);
        SeedFiles();
        SeedDatabase();
    }

    /// <summary>dual 源的一个内容目录：<c>&lt;root&gt;/&lt;avid&gt;/c_&lt;cid&gt;/120/{video,audio}.m4s</c>。</summary>
    public string DualDir(string root, string key)
    {
        var parts = key.Split("_c");
        return Path.Combine(root, parts[0], "c_" + parts[1], "120");
    }

    private void SeedFiles()
    {
        foreach (var root in new[] { DualRootA, DualRootB })
        {
            foreach (var key in new[] { KeyOnlyA, KeyShared })
            {
                var dir = DualDir(root, key);
                Directory.CreateDirectory(dir);
                WritePattern(Path.Combine(dir, "video.m4s"), VideoSize);
                WritePattern(Path.Combine(dir, "audio.m4s"), AudioSize);
            }
        }

        var singleFile = Path.Combine(SingleRoot, "片子", "第一集.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(singleFile)!);
        WritePattern(singleFile, SingleSize);

        // 封面：随便一张能被 img 读的字节，只要存在即可。
        var posterDir = Path.Combine(MetaRoot, "poster", DualA);
        Directory.CreateDirectory(posterDir);
        File.WriteAllBytes(Path.Combine(posterDir, KeyOnlyA + ".jpg"), [0xFF, 0xD8, 0xFF, 0xD9]);
    }

    /// <summary>每个文件的字节按位置可预测，这样"Range 返回的是不是正确那一段"能被断言。</summary>
    private static void WritePattern(string path, int size)
    {
        var bytes = new byte[size];
        for (var i = 0; i < size; i++) bytes[i] = (byte)(i % 251);
        File.WriteAllBytes(path, bytes);
    }

    private void SeedDatabase()
    {
        using var db = LibraryDatabase.Open(MetaRoot);
        db.Migrate();

        var sources = new SourceRepository(db);
        sources.Upsert(new SourceConfig
        {
            Id = DualA,
            Name = "源 A",
            Layout = SourceLayout.Dual,
            Root = DualRootA,
        });
        sources.Upsert(new SourceConfig
        {
            Id = DualB,
            Name = "源 B",
            Layout = SourceLayout.Dual,
            Root = DualRootB,
        });
        sources.Upsert(new SourceConfig
        {
            Id = Single,
            Name = "单流源",
            Layout = SourceLayout.Single,
            Root = SingleRoot,
            // 只读档：用来验证 403 与"被拒时库里不留记录"。
            VirtualWrite = VirtualWrite.None,
        });

        var media = new MediaRepository(db);

        media.Upsert(DualA, [DualEntry(DualA, KeyOnlyA, "只在 A 的视频", posterRel: $"poster/{DualA}/{KeyOnlyA}.jpg")]);
        media.Upsert(DualA, [DualEntry(DualA, KeyShared, "两个源都有的视频")]);
        media.Upsert(DualB, [DualEntry(DualB, KeyShared, "两个源都有的视频")]);

        media.Upsert(Single, [new MediaEntry
        {
            SourceId = Single,
            Key = KeySingle,
            RelPath = "片子/第一集.mp4",
            RelDir = "片子",
            Layout = SourceLayout.Single,
            TitleRaw = "第一集",
            DurationMs = 60_000,
            Width = 1920,
            Height = 1080,
            SizeBytes = SingleSize,
            Status = MediaStatus.Ok,
            Tracks = [new MediaTrack { Role = TrackRole.Video, SizeBytes = SingleSize }],
        }]);

        // 合集是 media 的投影（v4）：重建一次，测 collection 过滤。
        new CollectionRebuilder(db).Rebuild();
    }

    private static MediaEntry DualEntry(
        string sourceId, string key, string title, string? posterRel = null) => new()
    {
        SourceId = sourceId,
        Key = key,
        RelPath = key.Split("_c")[0] + "/c_" + key.Split("_c")[1],
        RelDir = key.Split("_c")[0],
        Layout = SourceLayout.Dual,
        TitleRaw = title,
        DurationMs = 12_000,
        Width = 2160,
        Height = 3840,
        Codec = "hevc",
        TagCode = 120,
        SizeBytes = VideoSize + AudioSize,
        PosterRel = posterRel,
        Status = MediaStatus.Ok,
        HasDanmaku = true,
        Tracks =
        [
            new MediaTrack
            {
                Role = TrackRole.Video,
                CodecId = 12,
                SizeBytes = VideoSize,
                Md5 = "d41d8cd98f00b204e9800998ecf8427e",
            },
            new MediaTrack
            {
                Role = TrackRole.Audio,
                CodecId = 0,
                SizeBytes = AudioSize,
            },
        ],
    };

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (!disposing) return;

        try
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 临时目录删不掉不该让测试失败。
        }

        SqliteConnectionPool.Clear();
    }
}

internal static class SqliteConnectionPool
{
    public static void Clear() => Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
}
