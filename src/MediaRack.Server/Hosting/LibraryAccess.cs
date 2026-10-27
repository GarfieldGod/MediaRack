using MediaRack.Models;
using MediaRack.Store;

namespace MediaRack.Server.Hosting;

/// <summary>
/// 库连接工厂。
/// <para>
/// 每个请求开一个新连接，而不是全进程共享一个——服务端要同时处理"读列表"和"扫描在写库"，
/// 共享单连接就必须全程加锁，扫描期间连列表都刷不出来。SQLite 打开本地文件是微秒级开销，
/// 而 WAL 模式下多读一写是它的设计目标，正好对得上。
/// </para>
/// <para>
/// 写事务撞写事务由 <c>busy_timeout</c> 兜底（见 <see cref="LibraryDatabase.Open"/>）。
/// </para>
/// </summary>
public sealed class LibraryAccess(ServerOptions options)
{
    public string MetaRoot => options.MetaRoot;

    /// <summary>启动时调一次：建目录、建库、迁移。之后所有连接都可以假定库已存在。</summary>
    public void Initialize()
    {
        Directory.CreateDirectory(options.MetaRoot);

        using var db = LibraryDatabase.Open(options.MetaRoot);
        db.Migrate();
    }

    public LibraryDatabase Open() => LibraryDatabase.Open(options.MetaRoot, create: false);

    /// <summary>在自动释放的连接上跑一段查询。</summary>
    public T With<T>(Func<LibraryDatabase, T> work)
    {
        using var db = Open();
        return work(db);
    }

    /// <summary>在自动释放的连接上跑一段写入。</summary>
    public void Write(Action<LibraryDatabase> work)
    {
        using var db = Open();
        work(db);
    }

    public SourceConfig? FindSource(string id) => With(db => new SourceRepository(db).Get(id));
}
