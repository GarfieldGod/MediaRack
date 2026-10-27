using MediaRack.Models;

namespace MediaRack.Scan;

/// <summary>
/// 判断一个根目录是哪种布局，让 <c>mrack scan</c> 不必手写 <c>--layout</c>。
/// <para>
/// dual 的特征很硬：存在 <c>entry.json</c>，或 <c>&lt;avid&gt;/c_&lt;cid&gt;/entry.json</c>。
/// 找不到就是 single——宁可把双流目录误判成单流吗？不会：双流目录里没有可识别的
/// 单流视频扩展名，误判成 single 只会扫出 0 条，属于可立刻发现的失败。
/// </para>
/// </summary>
public static class LayoutDetector
{
    /// <summary>
    /// 文件夹的最终布局：登记为具体值就用它；auto = 现场识别。
    /// 调用方（扫描）识别完应当把结果写回存储，避免每次扫描重复猜。
    /// </summary>
    public static SourceLayout Resolve(SourceFolder folder) =>
        folder.Layout ?? Detect(folder.Root);

    public static SourceLayout Detect(string root)
    {
        if (!Directory.Exists(root)) return SourceLayout.Single;

        // 根目录直接就是一个条目
        if (File.Exists(Path.Combine(root, "entry.json"))) return SourceLayout.Dual;

        string[] level1;
        try
        {
            level1 = Directory.GetDirectories(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return SourceLayout.Single;
        }

        foreach (var avidDir in level1)
        {
            string[] level2;
            try
            {
                level2 = Directory.GetDirectories(avidDir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var cidDir in level2)
            {
                if (!Path.GetFileName(cidDir).StartsWith("c_", StringComparison.OrdinalIgnoreCase)) continue;
                if (File.Exists(Path.Combine(cidDir, "entry.json"))) return SourceLayout.Dual;
            }
        }

        return SourceLayout.Single;
    }
}
