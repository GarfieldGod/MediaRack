using MediaRack.Models;

namespace MediaRack.Virtual;

/// <summary>
/// 虚拟写权限的判定。<b>只管"服务端视野能不能改"，与磁盘毫无关系</b>——磁盘永不可写没有开关。
/// </summary>
public static class MetaPolicy
{
    /// <summary>
    /// 某档位是否允许写某个字段。
    /// <para>
    /// <c>whitelist</c> 放行 tags / collection / hidden / rotation / play_pos：
    /// 这几项都是"我怎么看"，不是"原始信息是什么"。标题与 UP 名属于原始信息，锁定。
    /// </para>
    /// </summary>
    public static bool Allows(VirtualWrite level, MetaField field) => level switch
    {
        VirtualWrite.Full => true,
        VirtualWrite.Whitelist => field is MetaField.Tags or MetaField.Collection
            or MetaField.Hidden or MetaField.Rotation or MetaField.PlayPos,
        _ => false,
    };

    /// <summary>补丁实际触碰到的字段集合。</summary>
    public static IReadOnlyList<MetaField> FieldsOf(MetaPatch patch)
    {
        var fields = new List<MetaField>(4);
        if (patch.TitleDisplay is not null) fields.Add(MetaField.Title);
        if (patch.Tags is not null || patch.TagsAdd is not null || patch.TagsRemove is not null || patch.ClearTags)
        {
            fields.Add(MetaField.Tags);
        }

        if (patch.Hidden is not null) fields.Add(MetaField.Hidden);
        if (patch.Rotation is not null) fields.Add(MetaField.Rotation);
        if (patch.PlayPosMs is not null || patch.MarkedPlayed) fields.Add(MetaField.PlayPos);
        return fields;
    }

    public static string Name(MetaField field) => field switch
    {
        MetaField.Title => "标题",
        MetaField.Tags => "标签",
        MetaField.Collection => "收藏夹",
        MetaField.Hidden => "隐藏",
        MetaField.Rotation => "旋转",
        _ => "播放进度",
    };

    public static string Name(VirtualWrite level) => level switch
    {
        VirtualWrite.None => "none",
        VirtualWrite.Whitelist => "whitelist",
        _ => "full",
    };
}
