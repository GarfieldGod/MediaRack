using MediaRack.Client.Remote;
using MediaRack.Client.Storage;

namespace MediaRack.Client.ViewModels;

/// <summary>
/// 连接页「已保存的源」里的行。
///
/// 单独包一层而不是直接绑 <see cref="ServerEntry"/>：条目是落盘模型，不该挂着
/// "现在连没连着"和三个按钮。命令挂在行上（不用 <c>$parent</c> 往上游爬），
/// 编译期绑定能直接解析。
/// </summary>
public sealed class SavedSource : ViewModelBase
{
    public SavedSource(ServerEntry entry, bool connected, Action<SavedSource> connect,
        Action<SavedSource> edit, Action<SavedSource> delete)
    {
        Entry = entry;
        Connected = connected;
        ConnectCommand = new RelayCommand(() => connect(this), () => !Connected);
        EditCommand = new RelayCommand(() => edit(this));
        DeleteCommand = new RelayCommand(() => delete(this));
    }

    public ServerEntry Entry { get; }

    public string Title => Entry.DisplayName;

    /// <summary>第二行：<c>host:port/s/slug</c>，连着的话后面标一句。</summary>
    public string Subtitle => Connected ? $"{Entry.AddressLine} · 已连接" : Entry.AddressLine;

    public bool Connected { get; }

    public bool CanConnect => !Connected;

    public string ConnectLabel => Connected ? "已连接" : "连接";

    public RelayCommand ConnectCommand { get; }
    public RelayCommand EditCommand { get; }
    public RelayCommand DeleteCommand { get; }
}
