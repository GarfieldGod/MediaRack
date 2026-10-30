using MediaRack.Models;
using MediaRack.Store;
using MediaRack.Virtual;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MediaRack.Server.Tests;

/// <summary>
/// <c>sort=recommend</c> 的排序语义：播过的最多只占 1/10 名额，其余全给没播过的；
/// 同一 seed 稳定（客户端翻页靠它），换 seed 换一批。
/// <para>
/// 每个用例一个独立库，池子大小可以精确控制——共享 <c>MediaRackFactory</c> 的话，
/// 几条用例往同一个源里塞数据，总数和比例就对不上了。
/// </para>
/// </summary>
public sealed class RecommendOrderTests : IDisposable
{
    private const string Source = "rec";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "mfav-recommend-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        // 只清本条连接串的池：ClearAllPools 是进程级的，会波及其他并行跑的用例
        using (var conn = new SqliteConnection($"Data Source={LibraryDatabase.ResolvePath(_root)}"))
            SqliteConnection.ClearPool(conn);

        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录删不掉不影响结论，交给系统的临时清理
        }
    }

    [Fact]
    public void 随机序_同seed稳定_不同seed不同()
    {
        Seed(unseen: 540, played: 60);

        Assert.Equal(Order("seed-a"), Order("seed-a"));
        Assert.NotEqual(Order("seed-a"), Order("seed-b"));
    }

    [Fact]
    public void 随机序_一页里播过的只占十分之一()
    {
        Seed(unseen: 540, played: 60); // 600 条，播过 60

        var keys = Order("seed-a", limit: 100);
        var played = PlayedKeys(unseen: 540, played: 60);

        // 名额按全序总量算，所以要在"分页截断"上看比例；整份列出来必然是池子的原始配比
        Assert.Equal(100, keys.Count);
        Assert.Equal(10, keys.Count(played.Contains));
    }

    [Fact]
    public void 随机序_播过的均匀分散不聚堆()
    {
        Seed(unseen: 540, played: 60);

        var keys = Order("seed-a", limit: 100);
        var played = PlayedKeys(unseen: 540, played: 60);

        // 名额按等分区间取位，任意 20 条窗口里至少有 1 条播过的——
        // 不会出现"前半页全新片、后半页全是旧片"的断层
        for (var start = 0; start + 20 <= keys.Count; start++)
        {
            Assert.True(
                keys.Skip(start).Take(20).Any(played.Contains),
                $"第 {start} 条起 20 条全是没播过的");
        }
    }

    [Fact]
    public void 随机序_没播过的耗尽时剩余槽位给播过的()
    {
        Seed(unseen: 5, played: 40);

        var keys = Order("seed-a", limit: 20);
        var played = PlayedKeys(unseen: 5, played: 40);

        Assert.Equal(20, keys.Count);
        // 5 条没播过的会被优先用掉，之后只能全给播过的
        Assert.Equal(5, keys.Take(10).Count(k => !played.Contains(k)));
    }

    private List<string> Order(string seed, int limit = 100)
    {
        using var db = LibraryDatabase.Open(_root);
        return new MediaRepository(db)
            .ListSummaries(new SummaryQuery(SourceId: Source, Sort: "recommend", Seed: seed, Limit: limit))
            .Items.Select(i => i.Key).ToList();
    }

    /// <summary>与 <see cref="Seed"/> 同一套 key 生成规则：前 <paramref name="unseen"/> 条没播过，其后播过。</summary>
    private static HashSet<string> PlayedKeys(int unseen, int played) =>
        Enumerable.Range(unseen, played).Select(i => Key(i)).ToHashSet(StringComparer.Ordinal);

    private static string Key(int i) => $"{9000 + i}_c{5000 + i}";

    /// <summary>
    /// 造 <paramref name="unseen"/> 条没播过的 + <paramref name="played"/> 条播过的。
    /// 播过的把 <c>last_play_at</c> 推到 2 小时前——<see cref="MetaPatch"/> 只能记成"现在"，
    /// 留在 30 分钟冷却里会被整批排除，排序就测不到了。
    /// </summary>
    private void Seed(int unseen, int played)
    {
        Directory.CreateDirectory(_root);

        using (var db = LibraryDatabase.Open(_root))
        {
            db.Migrate();
            new SourceRepository(db).Upsert(new SourceConfig
            {
                Id = Source,
                Name = Source,
                Layout = SourceLayout.Dual,
                Root = _root,
                Folders = [new SourceFolder(1, 0, _root, SourceLayout.Dual)],
            });

            var entries = Enumerable.Range(0, unseen + played)
                .Select(i => Entry(Key(i), $"随机序样本 {i}"))
                .ToList();
            new MediaRepository(db).Upsert(Source, entries);

            var meta = new MetaRepository(db);
            foreach (var entry in entries.Skip(unseen))
                meta.Apply(Source, entry.Key, new MetaPatch { MarkedPlayed = true });
        }

        // 连接池已释放，另开一条连接改时间戳（LibraryDatabase.Connection 是 internal）
        using var conn = new SqliteConnection($"Data Source={LibraryDatabase.ResolvePath(_root)}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE media_meta SET last_play_at = $t WHERE source_id = $s;";
        cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.AddHours(-2).ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$s", Source);
        cmd.ExecuteNonQuery();
    }

    private static MediaEntry Entry(string key, string title)
    {
        var parts = key.Split("_c");
        return new MediaEntry
        {
            SourceId = Source,
            Key = key,
            RelPath = parts[0] + "/c_" + parts[1],
            RelDir = parts[0],
            Layout = SourceLayout.Dual,
            TitleRaw = title,
            DurationMs = 12_000,
            Width = 2160,
            Height = 3840,
            Codec = "hevc",
            TagCode = 120,
            SizeBytes = 4096,
            Status = MediaStatus.Ok,
            Tracks = [new MediaTrack { Role = TrackRole.Video, SizeBytes = 4096 }],
        };
    }
}
