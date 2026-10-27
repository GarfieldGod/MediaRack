using System.Reflection;

namespace MediaRack.Store;

/// <summary>
/// schema 定义的唯一来源：DDL 以 <c>Store/Schema/schema*.sql</c> 为源，编译期嵌入程序集，
/// 因此代码里不存在"另一份 DDL"，不可能与源码漂移。
/// </summary>
internal static class Schema
{
    public const int CurrentVersion = 7;

    private const string V1Resource = "MediaRack.Core.Store.schema.v1.sql";
    private const string V2Resource = "MediaRack.Core.Store.schema.v2.sql";
    private const string V3Resource = "MediaRack.Core.Store.schema.v3.sql";
    private const string V4Resource = "MediaRack.Core.Store.schema.v4.sql";
    private const string V5Resource = "MediaRack.Core.Store.schema.v5.sql";
    private const string V6Resource = "MediaRack.Core.Store.schema.v6.sql";
    private const string V7Resource = "MediaRack.Core.Store.schema.v7.sql";

    public static string Load(int version) => version switch
    {
        1 => ReadResource(V1Resource),
        2 => ReadResource(V2Resource),
        3 => ReadResource(V3Resource),
        4 => ReadResource(V4Resource),
        5 => ReadResource(V5Resource),
        6 => ReadResource(V6Resource),
        7 => ReadResource(V7Resource),
        _ => throw new ArgumentOutOfRangeException(nameof(version), version, "未知的 schema 版本"),
    };

    private static string ReadResource(string logicalName)
    {
        var asm = typeof(Schema).Assembly;
        using var stream = asm.GetManifestResourceStream(logicalName)
            ?? throw new InvalidOperationException(
                $"嵌入资源缺失：{logicalName}。检查 MediaRack.Core.csproj 的 EmbeddedResource 配置。");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
