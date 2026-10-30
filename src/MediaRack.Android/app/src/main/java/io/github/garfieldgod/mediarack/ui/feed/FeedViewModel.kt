package io.github.garfieldgod.mediarack.ui.feed

import android.app.Application
import android.net.Uri
import android.util.Log
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import io.github.garfieldgod.mediarack.MediaRackApp
import io.github.garfieldgod.mediarack.core.db.DownloadEntity
import io.github.garfieldgod.mediarack.core.db.ItemEntity
import io.github.garfieldgod.mediarack.core.db.MarkEntity
import io.github.garfieldgod.mediarack.core.db.ServerEntity
import io.github.garfieldgod.mediarack.core.download.DownloadLayouts
import io.github.garfieldgod.mediarack.core.remote.ApiClient
import io.github.garfieldgod.mediarack.core.remote.ApiException
import io.github.garfieldgod.mediarack.core.remote.Favorite
import io.github.garfieldgod.mediarack.core.remote.ItemRefBody
import io.github.garfieldgod.mediarack.core.remote.MediaPage
import io.github.garfieldgod.mediarack.core.remote.MediaSummary
import io.github.garfieldgod.mediarack.core.remote.NetworkException
import io.github.garfieldgod.mediarack.core.remote.RemoteCollection
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitAll
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import kotlinx.serialization.builtins.ListSerializer
import java.io.File

/** 保存远程源的结果：失败原因交给表单**就地**显示，不再靠底部提示条。 */
sealed interface SaveOutcome {
    data class Ok(val name: String) : SaveOutcome
    data class Fail(val reason: String) : SaveOutcome
}

/** 信息流数据：聚合条目、解析播放地址、收藏/看过/进度标记、统计。 */
class FeedViewModel(app: Application) : AndroidViewModel(app) {
    private val db = (app as MediaRackApp).db
    private val markDao = db.markDao()

    /** 二级页（合集 / 收藏）的离线兜底快照。 */
    private val pageCache = PageCache(File(getApplication<MediaRackApp>().cacheDir, "pagecache"))

    // ── 播放模式（播完暂停 / 循环播放 / 自动切集） ─────────────
    private val prefs = app.getSharedPreferences("player", android.content.Context.MODE_PRIVATE)

    /**
     * 当前播放模式。**落地到磁盘**：这是使用习惯，不是一次性开关——
     * 每次冷启动都回到「播完暂停」的话，选了自动连播的人每开一次 App 都要重新点一遍。
     */
    var playMode: PlayMode
        get() = PlayMode.of(prefs.getInt(KEY_PLAY_MODE, PlayMode.PauseAtEnd.ordinal))
        set(value) = prefs.edit().putInt(KEY_PLAY_MODE, value.ordinal).apply()

    /**
     * 画面适配：false = 适应（Fit，整幅可见）/ true = 铺满（Cover，裁边）。
     * 和播放模式一样是**全局**设置：不跟源、不跟视频，落盘重启保留。
     */
    var coverFit: Boolean
        get() = prefs.getBoolean(KEY_VIDEO_COVER, false)
        set(value) = prefs.edit().putBoolean(KEY_VIDEO_COVER, value).apply()

    // ── 条目流 ──────────────────────────────────────────────

    private val _items = MutableStateFlow<List<FeedItem>>(emptyList())
    val items: StateFlow<List<FeedItem>> = _items

    private val _loading = MutableStateFlow(false)
    val loading: StateFlow<Boolean> = _loading

    private val _message = MutableStateFlow<String?>(null)
    val message: StateFlow<String?> = _message

    private var messageJob: Job? = null

    /**
     * 非阻断提示：自动消失，不需要用户点"知道了"。
     *
     * 以前这里是 AlertDialog——"某源不可达"这种一句话结论也要弹窗+点按钮，
     * 比它提示的问题本身更烦人。真正的阻塞错误（播放失败）在播放器层单独表达。
     */
    private fun postMessage(text: String, ttlMs: Long = 3200) {
        messageJob?.cancel()
        _message.value = text
        messageJob = viewModelScope.launch {
            delay(ttlMs)
            _message.value = null
        }
    }

    fun consumeMessage() {
        messageJob?.cancel()
        _message.value = null
    }

    private val _filter = MutableStateFlow<FeedFilter>(FeedFilter.All)
    val filter: StateFlow<FeedFilter> = _filter

    /**
     * 当前是否处在「某个收藏夹 / 合集的连播」里（null = 普通信息流）。
     * 只影响两件事：顶栏那个可关闭的「连播」胶囊，以及退出时把 items 还原成信息流。
     */
    private val _playQueue = MutableStateFlow<PlayQueue?>(null)
    val playQueue: StateFlow<PlayQueue?> = _playQueue

    /** 全量缓存（未过滤），收藏夹连播与续播跳转从这里找条目。 */
    private var allCache: List<FeedItem> = emptyList()

    val servers = db.serverDao().observeAll()

    /** 本地缓存条目数（抽屉「本地缓存」行的副标题）。 */
    private val _localCount = MutableStateFlow(0)
    val localCount: StateFlow<Int> = _localCount

    fun setFilter(f: FeedFilter) {
        _filter.value = f
        saveLastFilter(f)
        // 切源/换过滤 = 主动离开连播：否则播放页还停在上一个夹子的列表里，
        // 顶栏写着新源、内容却是旧的（两个状态各说一套）。
        _playQueue.value = null
        viewModelScope.launch(Dispatchers.IO) {
            refreshMarkSnapshot()
            _items.value = applyFilter(allCache, f)
        }
    }

    /**
     * 抽屉切源：远程源**先探活再切**，不可达就不切（否则切过去直接进播放页一直重连）。
     * 探活结果带去抖缓存（[REACH_TTL_MS]），翻页时同一源的连续判断不会反复打网络。
     */
    fun selectSource(f: FeedFilter) {
        if (f !is FeedFilter.Server) {
            setFilter(f)
            return
        }
        viewModelScope.launch(Dispatchers.IO) {
            _connecting.value = f.name
            val ok = probeServer(f.serverId)
            _connecting.value = null
            if (!ok) {
                postMessage("「${f.name}」不可达，未切换")
                return@launch
            }
            _filter.value = f
            saveLastFilter(f)
            _playQueue.value = null
            refreshMarkSnapshot()
            _items.value = applyFilter(allCache, f)
        }
    }

    /** 正在探测可达性的源名（UI 显示「正在连接 X…」，避免探活那两秒看起来像卡死）。 */
    private val _connecting = MutableStateFlow<String?>(null)
    val connecting: StateFlow<String?> = _connecting

    // ── 可达性探测 ──────────────────────────────────────────

    /**
     * 探活结果缓存，键 = host:port。
     * 一条配置 = 一个源之后，同一台服务器上会有多条配置——同一端口同一进程，
     * 探通一个其余必然可达，不必每个源各打一遍网络。
     */
    private val reachCache = HashMap<String, Pair<Boolean, Long>>()

    private fun reachKey(baseUrl: String): String {
        val (h, p, _) = ApiClient.splitSourceBaseUrl(baseUrl)
        return if (p.isEmpty()) h else "$h:$p"
    }

    /**
     * 服务器是否可达。单次探测走 health（免鉴权），连接/读取都压到 2~3s——
     * 这里只是决定"要不要切/要不要跳过"，不该让用户等满 5s。
     */
    suspend fun probeServer(serverId: Long, force: Boolean = false): Boolean =
        withContext(Dispatchers.IO) {
            val server = db.serverDao().getById(serverId) ?: return@withContext false
            val now = System.currentTimeMillis()
            val key = reachKey(server.baseUrl)
            reachCache[key]?.let { (ok, at) ->
                if (!force && now - at < REACH_TTL_MS) return@withContext ok
            }
            val ok = runCatching {
                ApiClient(server.baseUrl, server.token, probeHttp).health()
            }.isSuccess
            reachCache[key] = ok to now
            ok
        }

    private val probeHttp = okhttp3.OkHttpClient.Builder()
        .connectTimeout(2, java.util.concurrent.TimeUnit.SECONDS)
        .readTimeout(3, java.util.concurrent.TimeUnit.SECONDS)
        .build()

    // ── 服务器的增 / 改 / 删（抽屉里直接管，不再有独立的源管理页） ──────────

    /**
     * 添加 / 编辑远程源（一条配置 = 一个源）。
     * host/port/源地址 三项拼成 `http://host:port/s/{源地址}`，先探活再入库——
     * 地址打错、令牌不对、源没登记都直接拒绝，不让一个连不上的配置躺进列表。
     *
     * 返回 [SaveOutcome] 而不是"飘一条提示了事"：失败原因要回到**表单内**显示，
     * 用户改个字就能重试。
     */
    suspend fun saveServer(
        existing: ServerEntity?,
        host: String,
        port: String,
        sourcePath: String,
        token: String?,
        name: String?,
    ): SaveOutcome = withContext(Dispatchers.IO) {
        val src = sourcePath.trim()
        if (src.isEmpty()) {
            return@withContext SaveOutcome.Fail("不写源地址不能连\n（新版服务端按源地址 + 令牌放行）")
        }
        // 手填 slug 要符合服务端规则；粘完整路径（/ 开头）则原样接受
        if (!src.startsWith("/") && !ApiClient.SLUG_PATTERN.matches(src)) {
            return@withContext SaveOutcome.Fail("源地址只能用小写字母、数字、- _\n且以字母或数字开头")
        }
        val base = ApiClient.buildSourceBaseUrl(host, port, src)
            ?: return@withContext SaveOutcome.Fail("地址格式不对：$host:$port$src")
        val tok = token?.takeIf { it.isNotBlank() }
        // 提示与默认名里的称呼：名称 > 源地址段 > host:port
        val path = ApiClient.splitSourceBaseUrl(base).third
        val label = name?.takeIf { it.isNotBlank() }
            ?: path.substringAfterLast('/').takeIf { it.isNotEmpty() }
            ?: base.substringAfter("://")
        // 地址/令牌没改就不用再探（只改个名字也得能保存，哪怕服务器当前离线）
        val reachable = existing != null && existing.baseUrl == base && existing.token == tok
        var remoteName: String? = null
        if (!reachable) {
            val client = ApiClient(base, tok, probeHttp)
            val probe = runCatching { client.health() }
            if (probe.isFailure) {
                // 只上屏"连不上的性质"（连接被拒绝/域名解析失败/超时），
                // 不铺 OkHttp 原文：那是两行英文 + IP，排版灾难还看不懂。
                val reason = when (val e = probe.exceptionOrNull()) {
                    // 新版服务端把根路径封给本机管理页：局域网 403 多半是地址里没写源地址段
                    is ApiException ->
                        if (e.httpStatus == 403) "地址缺源地址段\n（形如 http://ip:port/s/movie）" else e.message
                    else ->
                        e?.message?.takeIf { it.isNotBlank() }?.take(24) ?: "无法建立连接"
                }
                // 主动换行而不是让 Text 自己折：「（连接超时）」会被断成「（连接超 / 时）」
                return@withContext SaveOutcome.Fail("连不上「$label」\n（$reason）")
            }
            // auth/state 带令牌发：令牌填错了在这里就能发现，而不是等播放时 401
            val auth = runCatching { client.authState() }.getOrNull()
            if (auth != null && auth.authRequired && !auth.authenticated) {
                return@withContext SaveOutcome.Fail("「$label」令牌不对\n请在电脑端管理页查看或重新扫码")
            }
            // 服务端里的源名做默认名（用户没填名称时，抽屉里显示它）
            remoteName = runCatching { client.sources().firstOrNull()?.name }.getOrNull()
        }
        val finalName = name?.takeIf { it.isNotBlank() }
            ?: remoteName
            ?: existing?.name
            ?: path.substringAfterLast('/').takeIf { it.isNotEmpty() }
            ?: base
        if (existing == null) {
            db.serverDao().insert(
                ServerEntity(
                    name = finalName,
                    baseUrl = base,
                    token = tok,
                    createdAt = System.currentTimeMillis(),
                )
            )
        } else {
            db.serverDao().update(existing.id, finalName, base, tok)
            reachCache.remove(reachKey(existing.baseUrl))
        }
        postMessage(if (existing == null) "已添加「$finalName」" else "已更新「$finalName」")
        buildFeed()
        SaveOutcome.Ok(finalName)
    }

    fun deleteServer(serverId: Long) {
        viewModelScope.launch(Dispatchers.IO) {
            val key = db.serverDao().getById(serverId)?.let { reachKey(it.baseUrl) }
            db.serverDao().deleteById(serverId)
            key?.let { reachCache.remove(it) }
            pageCache.clear(serverId)
            // 删掉的正好是当前过滤源 → 回「全部」
            if ((_filter.value as? FeedFilter.Server)?.serverId == serverId) {
                _filter.value = FeedFilter.All
                saveLastFilter(FeedFilter.All)
            }
            buildFeed()
        }
    }

    /**
     * 组装信息流，两段式避免空转圈：
     * 1) 本地条目立刻上屏（只等本地 DB，毫秒级）；
     * 2) 各服务器并行拉取，到达后增量合并（单台失败不影响其他）。
     *
     * 注意：`_loading` 必须等**远程那一段也结束**才置 false。
     * 否则本地 0 条时（新装/刚清缓存）UI 会先渲染一次"信息流是空的"，
     * 几百毫秒到几秒后被远程结果填上——空态就这样闪一下，看起来像出了错。
     */
    fun buildFeed() {
        // 重建信息流 = 回到普通信息流：连播的列表是二级页给的临时视图，重建后不再存在。
        _playQueue.value = null
        viewModelScope.launch(Dispatchers.IO) {
            _loading.value = true
            try {
                // 重建信息流 = 换一批随机序：种子在这里轮换；marks 快照同步刷新，
                // 冷却的"多久前播过"判定才跟得上最新的观看记录
                feedSeed = kotlin.random.Random.nextLong()
                refreshMarkSnapshot()
                restoreLastFilterOnce()
                val local = localItems()
                allCache = local
                _localCount.value = local.size
                _items.value = applyFilter(local, _filter.value)
                Log.d(TAG, "feed local=${local.size}")
            } catch (e: Exception) {
                Log.e(TAG, "local feed failed", e)
            }
            try {
                val remote = remoteItems()
                if (remote.isNotEmpty()) {
                    allCache = allCache + remote
                    _items.value = applyFilter(allCache, _filter.value)
                    Log.d(TAG, "feed +remote=${remote.size}")
                }
            } catch (e: Exception) {
                Log.e(TAG, "remote feed failed", e)
            } finally {
                _loading.value = false
            }
        }
    }

    private suspend fun localItems(): List<FeedItem> =
        db.sourceDao().getAll().flatMap { s ->
            val posterDir = getApplication<io.github.garfieldgod.mediarack.MediaRackApp>().scanner.posterDir(s.id)
            db.itemDao().listOkBySource(s.id).map { it.toFeedItem(s.name, posterDir) }
        }

    // ── 冷启动恢复上次选中的源 ──────────────────────────────

    /** 本进程是否已恢复过（buildFeed 会被删源/重扫/加源反复触发，只恢复第一次）。 */
    private var filterRestored = false

    /**
     * 冷启动不再强制回「全部」，而是接着上次用的源打开。
     * 「收藏夹连播」不算源、不恢复（它是「我的」页发起的临时队列）。
     *
     * 远程源分两步：先查库确认配置还在（不在 = 源已被删，落回「全部」），
     * 再异步探活——探活不挡本地条目上屏，服务器当前关着就退回「全部」并提示，
     * 不让用户面对一个空信息流还不知道为什么。
     */
    private suspend fun restoreLastFilterOnce() {
        if (filterRestored) return
        filterRestored = true
        val kind = prefs.getString(KEY_LAST_FILTER, null) ?: return
        val restored = when (kind) {
            "local" -> FeedFilter.Local
            "server" -> {
                val server = db.serverDao().getById(prefs.getLong(KEY_LAST_SERVER_ID, -1L))
                if (server == null) {
                    saveLastFilter(null)
                    return
                }
                FeedFilter.Server(server.id, server.name)
            }
            else -> return
        }
        _filter.value = restored
        if (restored !is FeedFilter.Server) return
        viewModelScope.launch(Dispatchers.IO) {
            _connecting.value = restored.name
            val ok = probeServer(restored.serverId)
            _connecting.value = null
            // 用户没在探活期间手动切走才回退；已经切走就别动了
            if (!ok && _filter.value == restored) {
                _filter.value = FeedFilter.All
                saveLastFilter(FeedFilter.All)
                _items.value = applyFilter(allCache, FeedFilter.All)
                postMessage("「${restored.name}」不可达，已回到全部")
            }
        }
    }

    /** 记住本次选中的源（null = 清掉）；收藏夹连播不记。 */
    private fun saveLastFilter(f: FeedFilter?) {
        prefs.edit()
            .putString(KEY_LAST_FILTER, when (f) {
                FeedFilter.Local -> "local"
                is FeedFilter.Server -> "server"
                else -> null
            })
            .putLong(KEY_LAST_SERVER_ID, (f as? FeedFilter.Server)?.serverId ?: -1L)
            .apply()
    }

    /**
     * 各服务器并行拉取；实拉失败用缓存兜底。
     * 最终没进信息流的源合并成**一条**提示——逐台各弹一次的体验比不能连还糟。
     * 401/403 单独点名：那是令牌或源地址不对（服务端升级后老配置全中），删掉重扫才能修，
     * 跟"服务器关机了"是两回事，提示不能混成一锅。
     */
    private suspend fun remoteItems(): List<FeedItem> = coroutineScope {
        val results: List<Pair<String?, List<FeedItem>>> = db.serverDao().getAll().map { server ->
            async {
                val api = ApiClient(server.baseUrl, server.token)
                try {
                    // sort=recommend：服务端按 seed 出随机序，前 120 条就是全库洗牌的头部；
                    // 同一 seed 翻页/重拉都不抖，只有"30 分钟内播过的"会被剔掉
                    val page = withContext(Dispatchers.IO) {
                        api.library(limit = 120, sort = "recommend", seed = feedSeed.toString())
                    }
                    null to page.items.map {
                        it.toFeedItem(server.id, server.name, it.posterUrl?.let { p -> api.mediaUrl(p) })
                    }
                } catch (e: Exception) {
                    Log.w(TAG, "server ${server.name} live fetch failed, trying cache: ${e.message}")
                    val cached = server.lastSummaryJson?.let { json ->
                        runCatching { ApiClient.JSON.decodeFromString(MediaPage.serializer(), json) }.getOrNull()
                    }
                    val failMsg = when {
                        cached != null -> null // 缓存兜底成功就不打扰
                        e is ApiException && (e.httpStatus == 401 || e.httpStatus == 403) ->
                            "「${server.name}」令牌或源地址不对（删除后重新扫码可修复）"
                        else -> "「${server.name}」不可达"
                    }
                    val items = cached?.items?.map {
                        it.toFeedItem(server.id, server.name, it.posterUrl?.let { p -> api.mediaUrl(p) })
                    } ?: emptyList()
                    failMsg to items
                }
            }
        }.awaitAll()

        val offline = results.mapNotNull { it.first }
        if (offline.isNotEmpty()) {
            postMessage(offline.joinToString("、") + "，未加入信息流", 4200)
        }
        results.flatMap { it.second }
    }

    /**
     * 信息流随机种子：每次 [buildFeed] 换一个（重进 = 换一批）；
     * 切过滤/切源只从 allCache 重投影、不换种子——同一会话里来回切，序列不抖。
     * 合集/收藏夹的固定队列（PlayQueue 替换、FeedFilter.Liked）不经过洗牌。
     */
    private var feedSeed: Long = kotlin.random.Random.nextLong()

    /** marks 全量快照缓存：applyFilter 的本地分支按条目查"多久前播过"（冷却用）。 */
    private var markSnapshot: Map<String, MarkEntity> = emptyMap()

    private suspend fun refreshMarkSnapshot() {
        markSnapshot = db.markDao().allSnapshot().associateBy { "${it.scope}|${it.key}" }
    }

    private suspend fun applyFilter(list: List<FeedItem>, f: FeedFilter): List<FeedItem> = when (f) {
        FeedFilter.All -> recommendMix(list)
        FeedFilter.Local -> FeedRecommender.shuffleLocal(
            list.filter { !it.isRemote }, feedSeed, ::localPlayedAt,
        )
        // 单服务器视图：远程拉取时服务端已按 recommend 排好序，这里只过滤
        is FeedFilter.Server -> list.filter { it.serverId == f.serverId }
        FeedFilter.Liked -> {
            val liked = markDao.likedSnapshot().map { "${it.scope}|${it.key}" }.toSet()
            // 固定队列不许被随机序污染：远程是按 recommend 拉回来的，这里按标题
            // 恢复与服务端默认序一致的确定性顺序
            list.filter { liked.contains(it.id) }
                .sortedWith(compareBy({ it.title ?: "" }, { it.key }))
        }
    }

    /** 「全部」视图：本地条目按占比穿插进服务端随机序列（本地占比高时整体退化为本地洗牌）。 */
    private fun recommendMix(list: List<FeedItem>): List<FeedItem> {
        val local = list.filter { !it.isRemote }
        val remote = list.filter { it.isRemote }
        if (local.isEmpty()) return remote
        if (remote.isEmpty()) {
            return FeedRecommender.shuffleLocal(local, feedSeed, ::localPlayedAt)
        }
        val localOrdered = FeedRecommender.shuffleLocal(local, feedSeed, ::localPlayedAt)
        return FeedRecommender.mix(remote, localOrdered, feedSeed)
    }

    /** 本地条目的上次播放时间（marks 里有记录就用它，没有 = 0 从没播过）。 */
    private fun localPlayedAt(item: FeedItem): Long =
        markSnapshot[item.id]?.playedAt ?: 0L

    // ── 播放地址解析 ────────────────────────────────────────

    private val resolved = HashMap<String, Pair<String, String?>>()
    private val resolveMutex = Mutex()

    /**
     * 远程条目的服务端游标：key = "serverId|sourceId|key" → (posMs, lastPlayAt)。
     * mediaDetail 本来就要拉（取直链），游标是搭车拿的，不额外打请求。
     */
    private val serverProgress = HashMap<String, Pair<Long, Long>>()

    /** 结果内存缓存；失败抛异常由调用方提示。 */
    suspend fun resolvePlay(item: FeedItem): Pair<String, String?> = resolveMutex.withLock {
        resolved[item.id]?.let { return it }

        val target = if (!item.isRemote) {
            checkNotNull(item.videoUri) to item.audioUri
        } else {
            localPlayTarget(item) ?: remotePlayTarget(item)
        }
        resolved[item.id] = target
        target
    }

    /** 已下载条目直接播本地文件（本地优先）。 */
    private suspend fun localPlayTarget(item: FeedItem): Pair<String, String?>? {
        val serverId = item.serverId ?: return null
        val row = db.downloadDao().get(serverId, item.key) ?: return null
        if (row.status != DownloadEntity.STATUS_DONE) return null
        val root = DownloadLayouts.downloadsRoot(getApplication())
        return when (row.layout) {
            "dual" -> {
                val (avid, cid) = DownloadLayouts.parseDualKey(item.key) ?: return null
                val itemDir = DownloadLayouts.dualItemDir(root, avid, cid)
                val tagDir = DownloadLayouts.resolveTagDir(itemDir, null) ?: return null
                val video = File(tagDir, "video.m4s")
                val audio = File(tagDir, "audio.m4s")
                if (!video.isFile) return null
                Uri.fromFile(video).toString() to
                    audio.takeIf { it.isFile }?.let { Uri.fromFile(it).toString() }
            }
            else -> {
                val f = row.relPath?.let { DownloadLayouts.singleFile(root, it) } ?: return null
                if (!f.isFile) return null
                Uri.fromFile(f).toString() to null
            }
        }
    }

    private suspend fun remotePlayTarget(item: FeedItem): Pair<String, String?> {
        val server = db.serverDao().getById(item.serverId!!)
            ?: throw Exception("服务器不存在（可能已被删除）")
        val api = ApiClient(server.baseUrl, server.token)
        val detail = withContext(Dispatchers.IO) { api.mediaDetail(item.remoteSourceId!!, item.key) }
        // 跨端续播：详情里顺带拿着服务端游标（另一台设备看过 → 续播点以它为准）
        detail.meta?.let { meta ->
            serverProgress["${item.serverId}|${item.remoteSourceId}|${item.key}"] = meta.playPosMs to meta.lastPlayAt
        }
        val dual = api.dualStreamUrls(detail)
        if (dual != null) return dual
        val single = api.singleStreamUrl(detail)
        if (single != null) return single to null
        throw Exception("该条目不支持直接播放（delivery=${detail.delivery.joinToString("/")}） ")
    }

    /**
     * 直连播不了时的兜底：让服务端把双流转封装成单文件 mp4，等它就绪再返回地址。
     *
     * 只在 ExoPlayer 已经报过一次错之后才走这条路（HEVC 参数集缺失等解析失败），
     * 正常条目不受影响；服务端没有 ffmpeg / 转封装超时返回 null。
     */
    suspend fun remuxPlayable(item: FeedItem): String? {
        val serverId = item.serverId ?: return null
        val source = item.remoteSourceId ?: return null
        return withContext(Dispatchers.IO) {
            val server = db.serverDao().getById(serverId) ?: return@withContext null
            val api = ApiClient(server.baseUrl, server.token)
            val url = api.remuxUrl(source, item.key)
            if (api.remuxReady(url)) url else null
        }
    }

    /** 断点续播：进度 >30s、未标看过、剩余 >20s 才续，否则从头。远程条目另比一次服务端游标。 */
    suspend fun resumePositionMs(item: FeedItem): Long {
        val m = markDao.get(item.scope, item.key)
        val server = if (item.isRemote && item.serverId != null && item.remoteSourceId != null) {
            serverProgress["${item.serverId}|${item.remoteSourceId}|${item.key}"]
        } else {
            null
        }
        return pickResumePosMs(
            localPosMs = m?.positionMs ?: 0,
            localUpdatedAt = m?.updatedAt ?: 0,
            localWatchedAt = m?.watchedAt,
            serverPosMs = server?.first ?: 0,
            serverUpdatedAt = server?.second ?: 0,
            durationMs = m?.durationMs?.takeIf { it > 0 } ?: item.durationMs,
        )
    }

    // ── 合集 ────────────────────────────────────────────────

    /**
     * 同合集条目列表；没有合集信息（远程条目契约未含合集、本地散文件）返回空表——
     * 调用方据此显示空态，而不是把"没有"当"加载中"永远转圈。
     */
    suspend fun collectionItems(item: FeedItem): List<FeedItem> {
        if (item.isRemote) return emptyList()
        val dir = item.collectionDir ?: return emptyList()
        val source = db.sourceDao().getById(item.sourceId)
        val posterDir = getApplication<io.github.garfieldgod.mediarack.MediaRackApp>().scanner.posterDir(item.sourceId)
        return db.itemDao().listByCollection(item.sourceId, dir).map {
            it.toFeedItem(source?.name ?: item.sourceName ?: "本地源", posterDir)
        }
    }

    // ── 首帧缩略图 ──────────────────────────────────────────

    /**
     * 首帧文件路径；失败返回 null（调用方回退封面海报）。
     * 本地条目直接抽帧；远程条目走服务器 thumb 端点；已下载的远程条目有本地文件，同样本地抽。
     */
    suspend fun thumbFile(item: FeedItem): String? {
        val remoteUrl = if (item.isRemote && item.videoUri == null) {
            db.serverDao().getById(item.serverId ?: return null)?.let { s ->
                ApiClient(s.baseUrl, s.token).thumbUrl(item.remoteSourceId ?: return null, item.key)
            }
        } else {
            null
        }
        return io.github.garfieldgod.mediarack.core.thumb.ThumbStore.ensure(
            getApplication(), item.id, item.videoUri, remoteUrl,
        )
    }

    /** 邻页预取（当前页 ±2），让翻页时首帧已经就绪。串行做，不与播放抢 IO。 */
    fun prefetchThumbs(list: List<FeedItem>, center: Int) {
        viewModelScope.launch(Dispatchers.IO) {
            for (i in (center - 2)..(center + 2)) {
                val it = list.getOrNull(i) ?: continue
                runCatching { thumbFile(it) }
            }
        }
    }

    /** 按 id 找条目（续播跳转用）；找不到时清过滤再试一次。 */
    fun findItem(id: String): FeedItem? =
        allCache.find { it.id == id } ?: _items.value.find { it.id == id }

    /**
     * 进入某个集合的**连续播放**：把播放页的 items 整个换成该集合的完整列表，并定位到被点的那条。
     *
     * 旧做法是 [pinItem]（把一条插到信息流最前）：信息流变成"这一条 + 一堆无关视频"，
     * 上滑下一条是别的源的内容，而这个夹子里的第二条根本翻不到——"连续播放某收藏夹"就无从谈起。
     * 换列表之后，上下滑就是在夹子里前后翻，语义与列表页的阅读顺序一致。
     *
     * [allCache] 不动：退出连播 / 重建信息流时靠它还原。
     */
    fun playInQueue(queue: PlayQueue, anchor: FeedItem): String {
        // 锚点必须在队列里，否则 pendingJump 找不到 index，播放页会停在第一条上还以为是跳转失败
        val list = if (queue.items.any { it.id == anchor.id }) queue.items else listOf(anchor) + queue.items
        _playQueue.value = queue.copy(items = list)
        _items.value = list
        return anchor.id
    }

    /** 退出连播：回到当前过滤源的信息流。 */
    fun exitQueue() {
        if (_playQueue.value == null) return
        _playQueue.value = null
        viewModelScope.launch(Dispatchers.IO) {
            _items.value = applyFilter(allCache, _filter.value)
        }
    }

    /**
     * 把信息流之外的条目（合集页 / 收藏夹页点进来的）插到列表最前并返回它的 id，
     * 供 UI 跳到该页播放。
     *
     * 播放器是"按当前 items 的 index 播放"的整体结构，不做注入的话，
     * 合集里的第 200 条根本翻不到。注入的条目在下一次 buildFeed 时会被换掉——
     * 那时用户已经看完了，可以接受。
     *
     * 现在还用的场合只有"拿不到整份列表"的入口（如已下载管理里翻到一条不在信息流的条目）。
     * 收藏夹 / 合集都走 [playInQueue]。
     */
    fun pinItem(item: FeedItem): String {
        val current = _items.value
        if (current.none { it.id == item.id }) {
            _items.value = listOf(item) + current
        }
        allCache = (listOf(item) + allCache).distinctBy { it.id }
        return item.id
    }

    // ── 旋转（用户覆盖角，跟条目走）─────────────────────────

    /**
     * 保存某条目的旋转角（0/90/180/270）。
     *
     * 按 scope 分流：远程条目写服务端 `media_meta.rotation`（跨端、跨设备一致），
     * 本地条目写 Room。**在线才写**——失败时提示并把内存值留在旧角度，
     * 而不是"看起来记住了、下次打开又变回去"。
     */
    suspend fun setRotation(item: FeedItem, rotation: Int): Boolean = withContext(Dispatchers.IO) {
        val sid = item.serverId
        if (sid != null) {
            val server = db.serverDao().getById(sid)
                ?: run {
                    postMessage("服务器配置已删除", 2600)
                    return@withContext false
                }
            val sourceId = item.remoteSourceId
                ?: return@withContext false
            try {
                ApiClient(server.baseUrl, server.token).setRotation(sourceId, item.key, rotation)
            } catch (e: kotlinx.coroutines.CancellationException) {
                throw e
            } catch (e: Exception) {
                postMessage(
                    if (e is NetworkException) "离线状态不能保存旋转（联网后再转一次）"
                    else "旋转保存失败：${e.message}",
                    3200,
                )
                return@withContext false
            }
        } else {
            db.itemDao().setRotation(item.sourceId, item.key, rotation)
        }

        // 内存同步：同一次会话里翻回去必须是新角度（items 已从旧值构造过）
        fun patch(list: List<FeedItem>) =
            list.map { if (it.id == item.id) it.copy(rotation = rotation) else it }
        _items.value = patch(_items.value)
        allCache = patch(allCache)
        true
    }

    // ── 远程合集 / 收藏（服务端为真源）──────────────────────

    /** 合集列表：带上所属服务器 id（一配置=一源，条目得按服务器拉）。失败上屏一句话。 */
    suspend fun loadCollections(): CollectionsLoad = withContext(Dispatchers.IO) {
        val servers = db.serverDao().getAll()
        if (servers.isEmpty()) return@withContext CollectionsLoad(emptyList(), false)

        val all = mutableListOf<Pair<Long, RemoteCollection>>()
        var offline = false
        for (server in servers) {
            try {
                val list = ApiClient(server.baseUrl, server.token).collections()
                pageCache.put(
                    server.id, PageCache.KEY_COLLECTIONS,
                    ApiClient.JSON.encodeToString(ListSerializer(RemoteCollection.serializer()), list),
                )
                list.forEach { all += server.id to it }
            } catch (e: Exception) {
                if (e is kotlinx.coroutines.CancellationException) throw e
                Log.w(TAG, "collections of ${server.name} failed: ${e.message}")
                // 离线兜底：上次的内容照显，页面顶部标注「离线」
                val cached = pageCache.get(server.id, PageCache.KEY_COLLECTIONS)?.let { json ->
                    runCatching {
                        ApiClient.JSON.decodeFromString(ListSerializer(RemoteCollection.serializer()), json)
                    }.getOrNull()
                }
                if (cached != null) {
                    offline = true
                    cached.forEach { all += server.id to it }
                }
            }
        }

        if (all.isEmpty() && offline) postMessage("合集加载失败：服务器不可达，显示上次内容", 3200)
        // 空合集不显示：服务端的合集行是 media 的投影，文件夹搬走后行还在（只是条目缺失），
        // 计数已经是"只算可用条目"，所以 itemCount == 0 就是空壳。Web 端同样在渲染前过滤；
        // 离线快照也一起过滤，免得缓存里的旧空合集一直挂着。
        val visible = all.filter { it.second.itemCount > 0 }
        CollectionsLoad(visible.sortedWith(compareBy({ it.second.sourceId }, { it.second.name })), offline)
    }

    /** 某个合集 / 收藏夹里的条目（分页）。离线时回退缓存快照（[CollectionPageLoad.offline]）。 */
    suspend fun loadCollectionItems(
        serverId: Long,
        collectionId: String? = null,
        favoriteId: String? = null,
        offset: Int = 0,
        limit: Int = 100,
    ): CollectionPageLoad = withContext(Dispatchers.IO) {
        val server = db.serverDao().getById(serverId)
            ?: return@withContext CollectionPageLoad(emptyList(), 0, false)
        val api = ApiClient(server.baseUrl, server.token)
        val cacheKey = PageCache.keyItems(collectionId, favoriteId)
        // 收藏夹按**加入时间**倒序（用户拍板）：服务端默认按标题排，连播就会变成乱序跳。
        // 合集保持默认（标题序）——它是结构层，没有"加入"这回事。
        val sort = if (favoriteId != null) "added" else null
        try {
            val page = api.library(
                offset = offset, limit = limit,
                collectionId = collectionId, favoriteId = favoriteId, sort = sort,
            )
            pageCache.put(serverId, cacheKey, ApiClient.JSON.encodeToString(MediaPage.serializer(), page))
            val items = page.items.map { m ->
                m.toFeedItem(server.id, server.name, m.posterUrl?.let { p -> api.mediaUrl(p) })
            }
            CollectionPageLoad(items, page.total, false)
        } catch (e: Exception) {
            if (e is kotlinx.coroutines.CancellationException) throw e
            Log.w(TAG, "collection items of ${server.name} failed: ${e.message}")
            val cached = pageCache.get(serverId, cacheKey)?.let { json ->
                runCatching { ApiClient.JSON.decodeFromString(MediaPage.serializer(), json) }.getOrNull()
            }
            if (cached != null) {
                val items = cached.items.map { m ->
                    m.toFeedItem(server.id, server.name, m.posterUrl?.let { p -> api.mediaUrl(p) })
                }
                CollectionPageLoad(items, cached.total, true)
            } else {
                // 离线且没有快照：空表 + offline 标记，页面显示「加载失败」而不是「空空如也」
                CollectionPageLoad(emptyList(), 0, true)
            }
        }
    }

    /** 收藏夹列表（跨服务器合并：收藏是用户层，与源无关，但每台服务器各有一份）。 */
    /**
     * 收藏夹列表（一条 = 一个源上的一个夹子）。
     *
     * [onlyServerId] 非空 = 只拉这个源的（收藏是源级的：每个源有自己的
     * 「默认收藏夹」，源选中时「我的」里只看得到这一个源的那份）。
     * null = 全部源都拉（「全部」视图，夹子名由 UI 标注来源）。
     */
    suspend fun loadFavorites(onlyServerId: Long? = null): FavoritesLoad = withContext(Dispatchers.IO) {
        val servers = db.serverDao().getAll().filter { onlyServerId == null || it.id == onlyServerId }
        val out = mutableListOf<Pair<Long, Favorite>>()
        var offline = false
        for (server in servers) {
            try {
                val list = ApiClient(server.baseUrl, server.token).favorites()
                pageCache.put(
                    server.id, PageCache.KEY_FAVORITES,
                    ApiClient.JSON.encodeToString(ListSerializer(Favorite.serializer()), list),
                )
                list.forEach { out += server.id to it }
            } catch (e: Exception) {
                if (e is kotlinx.coroutines.CancellationException) throw e
                Log.w(TAG, "favorites of ${server.name} failed: ${e.message}")
                val cached = pageCache.get(server.id, PageCache.KEY_FAVORITES)?.let { json ->
                    runCatching {
                        ApiClient.JSON.decodeFromString(ListSerializer(Favorite.serializer()), json)
                    }.getOrNull()
                }
                if (cached != null) {
                    offline = true
                    cached.forEach { out += server.id to it }
                }
            }
        }
        FavoritesLoad(out, offline)
    }

    /** 收藏的合集（关注的 UP 主 / 文件夹）。返回 null = 该服务器不可达（与「夹子里真没有」区分）。 */
    suspend fun loadFavoriteCollections(favoriteId: String, serverId: Long): List<RemoteCollection>? =
        withContext(Dispatchers.IO) {
            val server = db.serverDao().getById(serverId) ?: return@withContext null
            runCatching { ApiClient(server.baseUrl, server.token).favoriteCollectionsOf(favoriteId) }
                .onFailure { Log.w(TAG, "favorite collections failed: ${it.message}") }
                .getOrNull()
        }

    /** 某个合集在指定服务器上是否已被关注（任一收藏夹里）——合集页星标的初值。 */
    suspend fun collectionFollowed(serverId: Long, collectionId: String): Boolean = withContext(Dispatchers.IO) {
        val server = db.serverDao().getById(serverId) ?: return@withContext false
        runCatching {
            val api = ApiClient(server.baseUrl, server.token)
            api.favorites().any { fav -> api.favoriteCollectionsOf(fav.id).any { it.id == collectionId } }
        }.getOrDefault(false)
    }

    /**
     * 离线页判定「这条还能不能播」：远程条目看下载表有没有 done 的本地副本。
     * 本地条目恒 true（本来就在磁盘上）。
     */
    suspend fun cachedSubset(items: List<FeedItem>): List<FeedItem> = withContext(Dispatchers.IO) {
        items.filter { item ->
            !item.isRemote ||
                db.downloadDao().get(item.serverId ?: return@filter false, item.key)?.status == DownloadEntity.STATUS_DONE
        }
    }

    /** 给二级页用的轻提示（与信息流同一条通道）。 */
    fun toast(text: String, ttlMs: Long = 2600) {
        postMessage(text, ttlMs)
    }

    // ── 收藏夹管理（新建 / 把视频移到别的夹）─────────────────

    /**
     * 新建收藏夹。返回 null = 成功，否则是给对话框显示的错误文案。
     * 收藏夹归属某台服务器（一配置=一源），所以必须指定 [serverId]。
     */
    suspend fun createFavorite(serverId: Long, name: String): String? = withContext(Dispatchers.IO) {
        val trimmed = name.trim()
        if (trimmed.isEmpty()) return@withContext "名称不能为空"
        val server = db.serverDao().getById(serverId) ?: return@withContext "服务器配置已删除"
        try {
            ApiClient(server.baseUrl, server.token).createFavorite(trimmed)
            postMessage("已创建收藏夹「$trimmed」", 2000)
            null
        } catch (e: kotlinx.coroutines.CancellationException) {
            throw e
        } catch (e: Exception) {
            if (e is NetworkException) "离线状态不能新建收藏夹" else "新建失败：${e.message}"
        }
    }

    /**
     * 把收藏夹里的一条移到另一个夹（[toFavoriteId] = null 表示只移出本夹）。
     *
     * 顺序是**先加后减**：中途失败最坏是"两个夹里都有"，不会把条目弄丢。
     * 用服务端的 `favorites/{id}/items` 增量接口，不用 meta 的整体替换——
     * 整体替换会把这条在**其他**夹里的归属一起抹掉。
     */
    suspend fun moveFavoriteItem(
        item: FeedItem,
        serverId: Long,
        fromFavoriteId: String,
        toFavoriteId: String?,
    ): Boolean = withContext(Dispatchers.IO) {
        val server = db.serverDao().getById(serverId) ?: run {
            postMessage("服务器配置已删除", 2600)
            return@withContext false
        }
        val remoteSource = item.remoteSourceId ?: return@withContext false
        val ref = ItemRefBody(remoteSource, item.key)
        val api = ApiClient(server.baseUrl, server.token)
        try {
            if (toFavoriteId != null) api.favoriteItems(toFavoriteId, add = listOf(ref))
            api.favoriteItems(fromFavoriteId, remove = listOf(ref))
            postMessage(if (toFavoriteId != null) "已移到另一个收藏夹" else "已移出收藏夹", 2000)
            true
        } catch (e: kotlinx.coroutines.CancellationException) {
            throw e
        } catch (e: Exception) {
            postMessage(
                if (e is NetworkException) "离线状态不能移动" else "移动失败：${e.message}",
                3200,
            )
            false
        }
    }

    /**
     * 收藏 / 取消收藏（**在线才写**：离线时按钮不该给出成功的错觉）。
     *
     * 目标夹子：优先用第一个夹子，一个都没有时现建"默认收藏夹"。
     * 服务端收藏是本轮的唯一真源，本地 marks.liked 只做离线展示的镜像。
     */
    suspend fun toggleFavorite(item: FeedItem): Boolean? = withContext(Dispatchers.IO) {
        val serverId = item.serverId ?: run {
            postMessage("本地条目暂不支持收藏", 2600)
            return@withContext null
        }
        val server = db.serverDao().getById(serverId) ?: run {
            postMessage("服务器配置已删除", 2600)
            return@withContext null
        }
        val api = ApiClient(server.baseUrl, server.token)

        val already = markDao.get(item.scope, item.key)?.likedAt != null
        try {
            val favorites = api.favorites()
            val target = favorites.firstOrNull() ?: api.createFavorite("默认收藏夹")
            if (already) {
                api.favoriteItems(target.id, remove = listOf(ItemRefBody(item.remoteSourceId!!, item.key)))
                toggle(MarkField.LIKED, item) // 本地镜像同步取消
                postMessage("已取消收藏", 1800)
                false
            } else {
                api.favoriteItems(target.id, add = listOf(ItemRefBody(item.remoteSourceId!!, item.key)))
                toggle(MarkField.LIKED, item)
                postMessage("已收藏到「${target.name}」", 1800)
                true
            }
        } catch (e: kotlinx.coroutines.CancellationException) {
            throw e
        } catch (e: Exception) {
            postMessage(
                if (e is NetworkException) "离线状态不能收藏\n（本地已缓存的仍可播放）" else "收藏失败：${e.message}",
                3200,
            )
            null
        }
    }

    /**
     * 关注 / 取消关注一个合集（= 收藏的合集）。目标夹子与视频收藏一致：
     * 第一个夹子，没有就现建"默认收藏夹"。在线才写。
     */
    suspend fun toggleCollectionFavorite(serverId: Long, collectionId: String, name: String): Boolean? =
        withContext(Dispatchers.IO) {
            val server = db.serverDao().getById(serverId) ?: return@withContext null
            val api = ApiClient(server.baseUrl, server.token)

            try {
                val favorites = api.favorites()
                val target = favorites.firstOrNull() ?: api.createFavorite("默认收藏夹")
                val already = api.favoriteCollectionsOf(target.id).any { it.id == collectionId }
                if (already) {
                    api.favoriteCollections(target.id, remove = listOf(collectionId))
                    postMessage("已取消关注「$name」", 2000)
                    false
                } else {
                    api.favoriteCollections(target.id, add = listOf(collectionId))
                    postMessage("已关注「$name」", 2000)
                    true
                }
            } catch (e: kotlinx.coroutines.CancellationException) {
                throw e
            } catch (e: Exception) {
                postMessage(
                    if (e is NetworkException) "离线状态不能关注" else "关注失败：${e.message}",
                    3000,
                )
                null
            }
        }

    // ── 标记（收藏 / 看过 / 进度） ──────────────────────────

    val likedIds: Flow<Set<String>> = markDao.observeLiked().map { list ->
        list.map { "${it.scope}|${it.key}" }.toSet()
    }
    val watchedIds: Flow<Set<String>> = markDao.observeWatched().map { list ->
        list.map { "${it.scope}|${it.key}" }.toSet()
    }
    val likedList: Flow<List<MarkEntity>> = markDao.observeLiked()

    /**
     * 正在播的那条（`scope|key`），由播放页在切条目时写入。
     * 只用于把「继续观看」里"就是这条"排除掉——不是播放状态，别拿它当 isPlaying 用。
     */
    private val _nowPlayingId = MutableStateFlow<String?>(null)

    fun setNowPlaying(id: String?) {
        _nowPlayingId.value = id
    }

    /** 「继续观看」：上一次播放的那一条（排除正在播的），最多一条。 */
    val continueList: Flow<List<MarkEntity>> =
        combine(markDao.observeContinue(), _nowPlayingId) { rows, playing ->
            rows.filter { "${it.scope}|${it.key}" != playing }.take(1)
        }

    fun toggleLiked(item: FeedItem) {
        viewModelScope.launch(Dispatchers.IO) { toggle(MarkField.LIKED, item) }
    }

    fun toggleWatched(item: FeedItem) {
        viewModelScope.launch(Dispatchers.IO) { toggle(MarkField.WATCHED, item) }
    }

    private enum class MarkField { LIKED, WATCHED }

    private suspend fun toggle(field: MarkField, item: FeedItem) {
        val now = System.currentTimeMillis()
        val old = markDao.get(item.scope, item.key)
        val base = old ?: MarkEntity(
            scope = item.scope, key = item.key, title = item.title,
            likedAt = null, watchedAt = null, positionMs = 0, durationMs = item.durationMs,
            updatedAt = now,
        )
        val updated = when (field) {
            MarkField.LIKED ->
                base.copy(likedAt = if (base.likedAt == null) now else null, updatedAt = now)
            MarkField.WATCHED ->
                base.copy(watchedAt = if (base.watchedAt == null) now else null, updatedAt = now)
        }
        markDao.upsert(updated)
    }

    /** 上次向服务端上报进度的时间（毫秒）。本地每 4s 存一次，服务端每 ~15s 报一次。 */
    private var lastReportAt = 0L

    /**
     * 播放进度回写（节流由调用方控制，~4s 一次）。在线时顺带把进度上报到服务端（跨端续播）。
     *
     * 同时刷新 `played_at`：这才是「继续观看」的排序依据（见 [continueList]）。
     */
    fun savePosition(item: FeedItem, positionMs: Long, durationMs: Long) {
        viewModelScope.launch(Dispatchers.IO) {
            val now = System.currentTimeMillis()
            if (item.serverId != null && item.remoteSourceId != null && now - lastReportAt > 15_000) {
                lastReportAt = now
                val server = db.serverDao().getById(item.serverId)
                if (server != null) {
                    runCatching {
                        ApiClient(server.baseUrl, server.token)
                            .reportProgress(item.remoteSourceId, item.key, positionMs)
                    }.onFailure { Log.d(TAG, "progress report skipped: ${it.message}") }
                }
            }
            val old = markDao.get(item.scope, item.key)
            markDao.upsert(
                (old ?: MarkEntity(
                    scope = item.scope, key = item.key, title = item.title,
                    likedAt = null, watchedAt = null,
                    positionMs = 0, durationMs = 0, updatedAt = now,
                )).copy(
                    positionMs = positionMs,
                    durationMs = if (durationMs > 0) durationMs else old?.durationMs ?: 0,
                    updatedAt = now,
                    playedAt = now,
                )
            )
        }
    }

    // ── 「我的」页统计 ──────────────────────────────────────

    data class Stats(val liked: Int, val watched: Int, val downloads: Int, val bytes: Long)

    /**
     * 「已下载」= 本地缓存条目（items 表里 status = ok 的行）。
     *
     * 以前这里读的是 `downloadDao().observeDoneCount()`——那是下载任务表，
     * 只统计"从远程源下载下来的"。手机自带的 B 站缓存走扫描器进 items 表，
     * 在下载表里一行都没有，于是"本地缓存 3 条、已下载 0 条"这种自相矛盾的画面。
     * 用户视角只有一个库，两个口径必须合成一个。
     */
    val stats: Flow<Stats> = combine(
        markDao.observeLikedCount(),
        markDao.observeWatchedCount(),
        db.itemDao().observeOkCount(),
        db.itemDao().observeOkBytes(),
    ) { liked, watched, downloads, bytes ->
        Stats(liked, watched, downloads, bytes)
    }

    /** 管理页数据源：本地条目全量（不受当前 filter 影响）。 */
    suspend fun localItemsForManage(): List<FeedItem> = withContext(Dispatchers.IO) {
        localItems().sortedWith(compareBy({ it.sourceName ?: "" }, { it.title }))
    }

    /**
     * 重扫所有本地源：外部丢进目录的文件、或手动整理过的目录能立刻反映出来。
     * 信息流本身没有扫描入口（旧库页已删），管理页的刷新按钮就是唯一入口。
     */
    suspend fun rescanLocal(): Int = withContext(Dispatchers.IO) {
        val app = getApplication<io.github.garfieldgod.mediarack.MediaRackApp>()
        var total = 0
        db.sourceDao().getAll().forEach { source ->
            total += runCatching { app.scanner.scan(source) }.getOrDefault(0)
        }
        buildFeed()
        total
    }

    /**
     * 删除一条本地缓存：磁盘文件 + 海报 + items 行，然后重建信息流。
     *
     * dual（B 站缓存的 `c_<cid>` 目录）整目录删——entry.json / 弹幕 / 封面都在里面，
     * 只删两个 m4s 会留下空壳目录，下次扫描还会当成异常条目收进来。
     * 其它情况只删媒体文件本身，绝不碰父目录（可能是用户自己的文件夹）。
     */
    suspend fun deleteLocalItem(item: FeedItem): String? = withContext(Dispatchers.IO) {
        if (item.sourceId <= 0L) return@withContext "只有本地缓存可以删除"
        val video = item.videoUri?.let { u -> runCatching { File(Uri.parse(u).path!!) }.getOrNull() }
            ?: return@withContext "找不到文件路径"
        val audio = item.audioUri?.let { u -> runCatching { File(Uri.parse(u).path!!) }.getOrNull() }

        val err = runCatching {
            val parent = video.parentFile
            if (audio != null && parent != null && parent.name.startsWith("c_")) {
                if (!parent.deleteRecursively()) error("文件被占用或没有写权限")
            } else {
                video.delete()
                audio?.delete()
            }
            item.posterFile?.let { File(it).delete() }
            db.itemDao().deleteItem(item.sourceId, item.key)
        }.exceptionOrNull()
        if (err != null) return@withContext err.message ?: "删除失败"

        buildFeed()
        null
    }

    // ── 缓存（下载到本地）────────────────────────────────────

    private val downloadRepo: io.github.garfieldgod.mediarack.core.download.DownloadRepository
        get() = getApplication<io.github.garfieldgod.mediarack.MediaRackApp>().downloads

    /** 缓存管理页的队列数据源（正在/待缓存/失败）。 */
    fun observeDownloadQueue() = db.downloadDao().observeActive()

    /** 并发下载数（1-3），落盘。 */
    var downloadConcurrency: Int
        get() = downloadRepo.concurrency
        set(value) { downloadRepo.concurrency = value }

    fun pauseDownload(serverId: Long, key: String) = downloadRepo.pause(serverId, key)
    fun resumeDownload(serverId: Long, key: String) = downloadRepo.resume(serverId, key)
    fun cancelDownload(serverId: Long, key: String) = downloadRepo.cancel(serverId, key)

    /**
     * 播放设置面板的「缓存」：把当前远程条目加入下载队列。
     * 返回 null = 成功（发提示），否则返回错误文案。
     */
    suspend fun cacheItem(item: FeedItem): String? = withContext(Dispatchers.IO) {
        val serverId = item.serverId ?: return@withContext "本地条目无需缓存"
        val server = db.serverDao().getById(serverId) ?: return@withContext "服务器不存在"
        try {
            val api = ApiClient(server.baseUrl, server.token)
            val detail = api.mediaDetail(item.remoteSourceId ?: "", item.key)
            val err = downloadRepo.enqueue(serverId, item.key, detail)
            if (err == null) postMessage("已加入缓存队列：${item.title}")
            err
        } catch (e: Exception) {
            Log.w(TAG, "cacheItem failed: ${e.message}")
            "缓存失败：${e.message ?: "连接不上服务器"}"
        }
    }

    // ── App 内搜索（跟随当前源）──────────────────────────────

    /**
     * 搜索当前范围的条目：
     * - 远程源 → 服务端 `q` 参数（标题/UP主/路径，SQL 层 LIKE）
     * - 本地缓存 / 全部 → 本地条目内存过滤（本地量级小，不值得加 DAO 查询）
     * - 收藏夹连播 → 在已拉到的收藏列表里过滤
     */
    suspend fun search(query: String): List<FeedItem> = withContext(Dispatchers.IO) {
        val q = query.trim()
        if (q.isEmpty()) return@withContext emptyList()

        when (val f = _filter.value) {
            is FeedFilter.Server -> {
                val server = db.serverDao().getById(f.serverId) ?: return@withContext emptyList()
                try {
                    val api = ApiClient(server.baseUrl, server.token)
                    api.library(query = q, limit = 60).items.map {
                        it.toFeedItem(server.id, server.name, it.posterUrl?.let { p -> api.mediaUrl(p) })
                    }
                } catch (e: Exception) {
                    Log.w(TAG, "search server ${server.name} failed: ${e.message}")
                    emptyList()
                }
            }

            FeedFilter.Liked -> markSnapshot.values
                .filter { it.title.contains(q, ignoreCase = true) }
                .mapNotNull { findItem("${it.scope}|${it.key}") }

            else -> {
                val local = localItems().filter { it.title.contains(q, ignoreCase = true) }
                if (f == FeedFilter.Local) {
                    local
                } else {
                    // 「全部」：本地 + 各服务器并行搜
                    val remote = coroutineScope {
                        db.serverDao().getAll().map { server ->
                            async {
                                try {
                                    val api = ApiClient(server.baseUrl, server.token)
                                    api.library(query = q, limit = 40).items.map {
                                        it.toFeedItem(server.id, server.name, it.posterUrl?.let { p -> api.mediaUrl(p) })
                                    }
                                } catch (e: Exception) {
                                    Log.w(TAG, "search server ${server.name} failed: ${e.message}")
                                    emptyList()
                                }
                            }
                        }.awaitAll().flatten()
                    }
                    local + remote
                }
            }
        }
    }

    companion object {
        private const val TAG = "MediaRack"

        /** 可达性结果的有效期：进同一个源反复翻页时不再重复打网络。 */
        private const val REACH_TTL_MS = 20_000L

        /** 播放模式在 SharedPreferences 里的键（值是 [PlayMode] 的序号）。 */
        private const val KEY_PLAY_MODE = "play_mode"

        /** 画面适配（false=适应 / true=铺满）的键，全局设置。 */
        private const val KEY_VIDEO_COVER = "video_cover"

        /** 上次选中的源（冷启动恢复用）：值为 "local" / "server"，其余视为「全部」。 */
        private const val KEY_LAST_FILTER = "last_filter_kind"

        /** 上次选中的远程源 id（配合 [KEY_LAST_FILTER] = "server"）。 */
        private const val KEY_LAST_SERVER_ID = "last_filter_server_id"
    }
}

/** 本地条目 → FeedItem：dual 下载目录的 audio.m4s 同级合成。 */
private fun ItemEntity.toFeedItem(sourceName: String, posterDir: File?): FeedItem {
    val audio = File(File(absPath).parentFile, "audio.m4s").takeIf { it.isFile }
    val poster = posterDir?.let { d -> File(d, "$key.jpg").takeIf { it.isFile }?.absolutePath }
    return FeedItem(
        scope = "local:$sourceId",
        key = key,
        title = title,
        collectionName = collectionDir?.substringAfterLast('/'),
        collectionDir = collectionDir,
        sourceId = sourceId,
        serverId = null,
        remoteSourceId = null,
        layout = if (audio != null) "dual" else "single",
        durationMs = durationMs,
        sizeBytes = size,
        width = width,
        height = height,
        videoUri = Uri.fromFile(File(absPath)).toString(),
        audioUri = audio?.let { Uri.fromFile(it).toString() },
        sourceName = sourceName,
        posterFile = poster,
        posterUrl = null,
        rotation = rotation,
    )
}

/** 服务器聚合条目 → FeedItem：流地址翻到该页时再解析。 */
private fun MediaSummary.toFeedItem(serverId: Long, serverName: String, posterAbs: String?): FeedItem = FeedItem(
    scope = "srv:$serverId",
    key = key,
    title = title,
    collectionName = collectionName.takeIf { it.isNotEmpty() },
    collectionDir = null,
    collectionId = collectionId.takeIf { it.isNotEmpty() },
    collectionKind = collectionKind.takeIf { it.isNotEmpty() },
    sourceId = 0,
    serverId = serverId,
    remoteSourceId = sourceId,
    layout = layout,
    durationMs = durationMs,
    sizeBytes = sizeBytes,
    width = width,
    height = height,
    videoUri = null,
    audioUri = null,
    sourceName = serverName,
    posterFile = null,
    posterUrl = posterAbs,
    rotation = rotation,
)
