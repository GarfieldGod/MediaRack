using MediaRack.Models;
using MediaRack.Store;
using MediaRack.Virtual;

namespace MediaRack.Cli;

/// <summary>
/// <c>mrack meta</c> — 虚拟元数据的读写。<b>全部只改数据库，磁盘上的文件一个字节都不动。</b>
/// </summary>
internal static class MetaCommand
{
    public const string HelpText =
        """
        mrack meta — 查看与修改虚拟元数据（只写库，磁盘永不变）

        用法：
          mrack meta show   <key> [--source <id>]
          mrack meta title  <key> [<新标题>] [--source <id>] [--rev <n>]
          mrack meta tag    <key> [--add a,b] [--remove c] [--set a,b] [--clear] [--source <id>]
          mrack meta hide   <key> [--unhide] [--source <id>]
          mrack meta rotate <key> <0|90|180|270> [--source <id>]
          mrack meta pos    <key> <毫秒> [--played] [--source <id>]

        选项：
          --source <id>   条目所在的源；只有一个源含该 key 时可省略
          --rev <n>       乐观锁：与库里的 rev 不一致就拒绝（用于"不好重做"的操作）
          --played        同时记一次播放（play_count + 1，刷新最近播放时间）

        说明：
          标题传空串（""）表示清除覆盖、恢复源端标题。
          被源写权限拒绝时退出码 3，rev 冲突时退出码 4。
          whitelist 档只放行 标签 / 收藏夹 / 隐藏 / 旋转 / 进度，标题锁定。
        """;

    public static int Run(string[] args, string metaTarget)
    {
        var options = Options.Parse(args);
        if (options.Flag("help") || options.Positional.Count == 0)
        {
            Console.WriteLine(HelpText);
            return options.Flag("help") ? 0 : 2;
        }

        var sub = options.Positional[0];
        var key = options.Positional.Count > 1 ? options.Positional[1] : string.Empty;

        using var db = LibraryDatabase.Open(metaTarget, create: false);
        var (sourceId, mediaKey) = TargetResolver.Resolve(db, options.Get("source"), key);
        var meta = new MetaRepository(db);

        switch (sub)
        {
            case "show":
                return Show(meta, sourceId, mediaKey);

            case "title":
            {
                var title = options.Positional.Count > 2 ? options.Positional[2] : null;
                if (title is null) throw new CliException("缺少新标题；要清除覆盖请传空串 \"\"");
                return Emit(meta.Apply(sourceId, mediaKey, new MetaPatch { TitleDisplay = title },
                    ParseRev(options), "cli"), "标题");
            }

            case "tag":
            {
                var add = TargetResolver.SplitList(options.Get("add"));
                var remove = TargetResolver.SplitList(options.Get("remove"));
                var set = options.Flag("set") ? TargetResolver.SplitList(options.Get("set")) : null;

                if (!options.Flag("set") && !options.Flag("clear") && add.Count == 0 && remove.Count == 0)
                {
                    throw new CliException("至少给一个 --add / --remove / --set / --clear");
                }

                var patch = new MetaPatch
                {
                    Tags = set,
                    TagsAdd = add.Count > 0 ? add : null,
                    TagsRemove = remove.Count > 0 ? remove : null,
                    ClearTags = options.Flag("clear"),
                };

                return Emit(meta.Apply(sourceId, mediaKey, patch, ParseRev(options), "cli"), "标签");
            }

            case "hide":
                return Emit(
                    meta.Apply(sourceId, mediaKey, new MetaPatch { Hidden = !options.Flag("unhide") },
                        ParseRev(options), "cli"),
                    "隐藏");

            case "rotate":
            {
                var raw = options.Positional.Count > 2 ? options.Positional[2] : null;
                if (!int.TryParse(raw, out var degrees))
                {
                    throw new CliException("缺少旋转角（0 / 90 / 180 / 270）");
                }

                return Emit(meta.Apply(sourceId, mediaKey, new MetaPatch { Rotation = degrees },
                    ParseRev(options), "cli"), "旋转");
            }

            case "pos":
            {
                var raw = options.Positional.Count > 2 ? options.Positional[2] : null;
                if (!long.TryParse(raw, out var ms))
                {
                    throw new CliException("缺少进度（毫秒）");
                }

                return Emit(meta.Apply(sourceId, mediaKey, new MetaPatch
                {
                    PlayPosMs = ms,
                    MarkedPlayed = options.Flag("played"),
                }, ParseRev(options), "cli"), "播放进度");
            }

            default:
                throw new CliException($"未知子命令：meta {sub}");
        }
    }

    private static int Show(MetaRepository meta, string sourceId, string key)
    {
        var resolved = meta.Resolve(sourceId, key);
        if (resolved is null) throw new CliException($"条目不存在：{sourceId}/{key}");

        var m = resolved.Meta;

        Console.WriteLine($"条目      {sourceId}/{key}");
        Console.WriteLine($"源端标题  {resolved.TitleRaw ?? "（无）"}");
        Console.WriteLine($"展示标题  {resolved.Title ?? "（无）"}" + (resolved.TitleOverridden ? "  ← 已覆盖" : "  （跟随源端）"));
        Console.WriteLine($"标签      {(m.Tags.Count == 0 ? "（无）" : string.Join(", ", m.Tags))}");
        Console.WriteLine($"隐藏      {(m.Hidden ? "是" : "否")}");
        Console.WriteLine($"旋转      {m.Rotation}°");
        Console.WriteLine($"播放      {FormatPosition(m.PlayPosMs)}，共 {m.PlayCount} 次" +
                          (m.LastPlayAt is { } at ? $"，最近 {FormatTime(at)}" : string.Empty));
        Console.WriteLine($"rev       {m.Rev}" +
                          (m.UpdatedAt is { } updated
                              ? $"   最后写入 {FormatTime(updated)}" + (m.UpdatedBy is { } by ? $" by {by}" : string.Empty)
                              : "   （尚未写入，全部跟随源端）"));

        return 0;
    }

    private static int Emit(WriteResult result, string what)
    {
        if (result.Ok)
        {
            var rev = result.Value?.Rev ?? 0;
            Console.WriteLine($"已写入    {what}    rev {rev}");
            return 0;
        }

        Console.Error.WriteLine(result.Status switch
        {
            WriteStatus.Forbidden => $"拒绝：{result.Message}",
            WriteStatus.Conflict => $"冲突：{result.Message}",
            _ => $"失败：{result.Message}",
        });

        return ExitCodeOf(result.Status);
    }

    internal static int ExitCodeOf(WriteStatus status) => status switch
    {
        WriteStatus.Applied => 0,
        WriteStatus.Forbidden => 3,
        WriteStatus.Conflict => 4,
        _ => 1,
    };

    private static long? ParseRev(Options options) =>
        long.TryParse(options.Get("rev"), out var rev) ? rev : null;

    internal static string FormatPosition(long ms)
    {
        if (ms <= 0) return "从头";
        var span = TimeSpan.FromMilliseconds(ms);
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:D2}:{span.Seconds:D2}"
            : $"{span.Minutes}:{span.Seconds:D2}";
    }

    internal static string FormatTime(long unixMs) =>
        DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
}
