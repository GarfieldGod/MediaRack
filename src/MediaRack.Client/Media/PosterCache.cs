using Avalonia.Media.Imaging;

namespace MediaRack.Client.Media;

/// <summary>
/// 海报/封面缓存。
///
/// 为什么不用绑个 URL 让控件自己加载：海报墙一屏就有几十张，翻页会累积到几百张。
/// 没有并发上限的话，双击滚动能瞬间打出上百个请求，服务端 ffmpeg 抽帧（/media/…/thumb.jpg）
/// 直接把 CPU 打满。这里统一收口：最多 6 个并发下载、按 LRU 保 260 张、失败不缓存（下次重试）。
///
/// 淘汰时<b>不</b> Dispose：卡片还挂在界面上时销毁 Bitmap 会让渲染读到已释放的 native 内存。
/// 交给 GC —— Avalonia 的 Bitmap 有终结器，引用一断就会自己释放。
/// </summary>
public sealed class PosterCache : IDisposable
{
    private const int MaxConcurrent = 6;
    private const int Capacity = 260;

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(MaxConcurrent, MaxConcurrent);
    private readonly object _lock = new();

    // 用 ConcurrentDictionary + LinkedList 实现简单 LRU；
    // 全部命中路径都在 UI 线程，加锁成本可以忽略。
    private readonly Dictionary<string, Bitmap> _cache = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _recent = new();

    public PosterCache()
    {
        _http = new HttpClient(new SocketsHttpHandler
        {
            MaxConnectionsPerServer = MaxConcurrent,
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
        })
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
    }

    /// <summary>已缓存张数（自检 / 诊断用）。</summary>
    public int Count
    {
        get { lock (_lock) return _cache.Count; }
    }

    /// <summary>
    /// 取一张图。404（尚无封面）/ 超时 / 解码失败都返回 null —— 卡片显示占位块，不是错误。
    /// </summary>
    public async Task<Bitmap?> GetAsync(string url, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(url, out var hit))
            {
                _recent.Remove(url);
                _recent.AddFirst(url);
                return hit;
            }
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // 排队期间可能已被别的卡片加载完
            lock (_lock)
            {
                if (_cache.TryGetValue(url, out var raced))
                {
                    _recent.Remove(url);
                    _recent.AddFirst(url);
                    return raced;
                }
            }

            var bytes = await _http.GetByteArrayAsync(url, ct).ConfigureAwait(false);
            if (bytes.Length == 0) return null;

            using var ms = new MemoryStream(bytes, writable: false);
            var bmp = new Bitmap(ms);

            lock (_lock)
            {
                _cache[url] = bmp;
                _recent.AddFirst(url);
                while (_cache.Count > Capacity && _recent.Last is { } oldest)
                {
                    _recent.RemoveLast();
                    _cache.Remove(oldest.Value);
                }
            }
            return bmp;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException
                                      or IOException or ArgumentException or NotSupportedException)
        {
            // 失败不入缓存：服务端封面可能是懒生成的，下次打开就有
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
        lock (_lock)
        {
            foreach (var b in _cache.Values) b.Dispose();
            _cache.Clear();
            _recent.Clear();
        }
    }
}
