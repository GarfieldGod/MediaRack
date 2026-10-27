package io.github.garfieldgod.mediarack.core.download

import android.app.Application
import android.net.ConnectivityManager
import android.net.NetworkCapabilities
import io.github.garfieldgod.mediarack.core.db.AppDatabase
import io.github.garfieldgod.mediarack.core.db.DownloadEntity
import io.github.garfieldgod.mediarack.core.db.SourceEntity
import io.github.garfieldgod.mediarack.core.remote.ApiClient
import io.github.garfieldgod.mediarack.core.remote.MediaDetail
import io.github.garfieldgod.mediarack.core.remote.MediaSummary
import io.github.garfieldgod.mediarack.core.scan.LocalSourceScanner
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.delay
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.withLock
import java.io.File
import java.io.FileOutputStream

/**
 * 下载仓库：手动「下载到本地」的调度中枢。
 *
 * - **复用本地源管线**：下载 = 按 PC 兼容布局写到 files/downloads（该目录注册为本地源
 *   「下载」），完成后重扫——离线播放、合集分组、海报全部走本地源扫描的既有代码
 * - **断点续传**：半成品 `<key>/` 目录下的 .part 文件放 staging 区，重试/重启后从 part 长度续传（HTTP Range）
 * - **网络闸门**：仅 Wi-Fi / 以太网 / 非计量网络可下载；流量网络挂起队列并亮横幅，
 *   横幅提供「本次允许流量」会话级放行（不持久化）
 * - **串行队列**：一次一个任务；进程重启自愈（downloading → queued 重跑，part 还在就续传）
 */
class DownloadRepository(
    private val app: Application,
    private val db: AppDatabase,
    private val scanner: LocalSourceScanner,
) {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val prefs = app.getSharedPreferences("player", android.content.Context.MODE_PRIVATE)

    private val _pausedForNetwork = MutableStateFlow(false)
    val pausedForNetwork: StateFlow<Boolean> = _pausedForNetwork

    /** 本次会话放行流量网络（UI 开关）。 */
    val allowCellular = MutableStateFlow(false)

    /** 横幅 / 通知共用的进行中状态。 */
    data class Ui(
        val activeTitle: String? = null,
        val activeStatus: String? = null,
        val bytesDone: Long = 0,
        val bytesTotal: Long = 0,
        val pendingCount: Int = 0,
    ) {
        val idle: Boolean get() = pendingCount == 0
        val percent: Int
            get() = if (bytesTotal > 0) ((bytesDone * 100) / bytesTotal).toInt().coerceIn(0, 100) else -1
    }

    private val _ui = MutableStateFlow(Ui())
    val ui: StateFlow<Ui> = _ui

    init {
        scope.launch {
            kotlinx.coroutines.flow.combine(
                db.downloadDao().observePending(),
                _pausedForNetwork,
            ) { pending, paused ->
                val active = pending.firstOrNull { it.status == DownloadEntity.STATUS_DOWNLOADING }
                    ?: pending.firstOrNull()
                Ui(
                    activeTitle = active?.title,
                    activeStatus = active?.status,
                    bytesDone = active?.bytesDone ?: 0,
                    bytesTotal = active?.bytesTotal ?: 0,
                    pendingCount = pending.size,
                ).let { if (paused && it.activeStatus == null) it.copy(activeStatus = DownloadEntity.STATUS_PAUSED) else it }
            }.collect { _ui.value = it }
        }
    }

    @Volatile
    private var downloadsSourceId: Long? = null

    /** 当前在跑的下载任务（暂停/取消用它命中在途协程），键 `serverId|key`。 */
    private val activeJobs = java.util.concurrent.ConcurrentHashMap<String, kotlinx.coroutines.Job>()

    /** 并发下载的 lane 协程；数量跟着 [concurrency] 走。 */
    private val lanes = mutableListOf<Job>()

    /**
     * 并发下载数（1-3）。默认 1 = 原来的串行；改动即时生效——
     * 调大补 lane、调小只收多余 lane（**正在跑的不打断**，只是不再领新任务）。
     */
    var concurrency: Int
        get() = prefs.getInt(KEY_CONCURRENCY, 1)
        set(value) {
            prefs.edit().putInt(KEY_CONCURRENCY, value.coerceIn(1, 3)).apply()
            startLanes()
        }

    // ── 「下载」本地源 ──────────────────────────────────────────

    /** files/downloads 注册为本地源（幂等）；海报按它的 sourceId 落位。 */
    suspend fun ensureSource(): SourceEntity {
        val root = DownloadLayouts.downloadsRoot(app).apply { mkdirs() }
        db.sourceDao().getByRootPath(root.absolutePath)?.let {
            downloadsSourceId = it.id
            return it
        }
        val id = db.sourceDao().insert(
            SourceEntity(name = "下载", rootPath = root.absolutePath, createdAt = System.currentTimeMillis())
        )
        downloadsSourceId = id
        return db.sourceDao().getById(id) ?: error("下载源入库失败")
    }

    private suspend fun sourceIdBlocking(): Long = downloadsSourceId ?: ensureSource().id

    /** 进程重启自愈：卡在 downloading 的放回队列；有待办则拉起 lane。返回是否有待办。 */
    suspend fun recover(): Boolean {
        db.downloadDao().recoverDownloading()
        val hasPending = db.downloadDao().nextQueued() != null
        if (hasPending) {
            startLanes()
            DownloadService.start(app)
        }
        return hasPending
    }

    // ── 任务入口（ViewModel 调用）────────────────────────────────

    /**
     * 入队下载。返回错误消息，null = 成功。
     * 详情由 ViewModel 拉（要先给用户看得见的报错），这里只构造任务行。
     */
    suspend fun enqueue(serverId: Long, key: String, detail: MediaDetail): String? {
        val existing = db.downloadDao().get(serverId, key)
        if (existing != null) {
            return when (existing.status) {
                DownloadEntity.STATUS_DONE -> "该条目已下载"
                DownloadEntity.STATUS_FAILED -> null // 允许重下（upsert 覆盖）
                else -> null // queued/downloading/paused：已在队列，不重复入队
            }
        }

        val row = buildRow(serverId, key, detail)
            ?: return "该条目暂不支持下载（single 缺 relPath，或 key 不是 dual 形态）"

        ensureSource() // 海报要按下载源的 sourceId 落位，必须先有源
        db.downloadDao().upsert(row)
        startLanes()
        DownloadService.start(app)
        return null
    }

    private fun buildRow(serverId: Long, key: String, detail: MediaDetail): DownloadEntity? {
        val now = System.currentTimeMillis()
        return when (detail.layout) {
            "dual" -> {
                if (DownloadLayouts.parseDualKey(detail.key) == null) return null
                val video = detail.tracks.firstOrNull { it.role == "video" }
                val audio = detail.tracks.firstOrNull { it.role == "audio" }
                if (video?.url == null) return null
                DownloadEntity(
                    serverId = serverId, key = key, sourceId = detail.sourceId,
                    title = detail.title, layout = "dual", relPath = null,
                    status = DownloadEntity.STATUS_QUEUED,
                    bytesDone = 0,
                    bytesTotal = (video.sizeBytes ?: 0L) + (audio?.sizeBytes ?: 0L),
                    error = null, updatedAt = now,
                )
            }
            "single" -> {
                if (detail.relPath.isNullOrEmpty()) return null
                DownloadEntity(
                    serverId = serverId, key = key, sourceId = detail.sourceId,
                    title = detail.title, layout = "single", relPath = detail.relPath,
                    status = DownloadEntity.STATUS_QUEUED,
                    bytesDone = 0,
                    bytesTotal = detail.tracks.firstOrNull()?.sizeBytes ?: detail.sizeBytes,
                    error = null, updatedAt = now,
                )
            }
            else -> null
        }
    }

    fun pause(serverId: Long, key: String) {
        scope.launch {
            db.downloadDao().setStatus(
                serverId, key, DownloadEntity.STATUS_PAUSED, null, System.currentTimeMillis()
            )
            activeJobs["$serverId|$key"]?.cancel()
        }
    }

    fun resume(serverId: Long, key: String) {
        scope.launch {
            db.downloadDao().setStatus(
                serverId, key, DownloadEntity.STATUS_QUEUED, null, System.currentTimeMillis()
            )
            startLanes()
        }
    }

    /** 取消：删任务行与半成品，不碰已完成文件（删除已下载用 [deleteDownload]）。 */
    fun cancel(serverId: Long, key: String) {
        scope.launch {
            activeJobs["$serverId|$key"]?.cancel()
            db.downloadDao().delete(serverId, key)
            DownloadLayouts.stagingDir(app, key).deleteRecursively()
        }
    }

    /** 删除已下载：删磁盘文件 + 海报 + 任务行，重扫自愈。 */
    fun deleteDownload(serverId: Long, key: String) {
        scope.launch {
            val row = db.downloadDao().get(serverId, key) ?: return@launch
            val root = DownloadLayouts.downloadsRoot(app)
            when (row.layout) {
                "dual" -> DownloadLayouts.parseDualKey(key)?.let { (avid, cid) ->
                    DownloadLayouts.dualItemDir(root, avid, cid).deleteRecursively()
                    // avid 目录空了顺手收掉
                    File(root, avid).takeIf { it.isDirectory && it.listFiles().isNullOrEmpty() }?.delete()
                }
                else -> row.relPath?.let { DownloadLayouts.singleFile(root, it).delete() }
            }
            File(scanner.posterDir(sourceIdBlocking()), "$key.jpg").delete()
            db.downloadDao().delete(serverId, key)
            rescanDownloads()
        }
    }

    // ── lanes：并发工作协程 ────────────────────────────────────

    private fun startLanes() {
        val want = concurrency
        while (lanes.size < want) {
            lanes += scope.launch { laneLoop() }
        }
        if (lanes.size > want) {
            // 只收多余 lane，正在跑的下载不打断（它挂在 app 级 scope 上，不属于任何 lane）
            lanes.drop(want).forEach { it.cancel() }
            lanes.subList(want, lanes.size).clear()
        }
    }

    /** 原子领任务：锁内把最早的 queued 置为 downloading，两个 lane 不会抢到同一条。 */
    private val claimMutex = kotlinx.coroutines.sync.Mutex()

    private suspend fun claimNext(): DownloadEntity? = claimMutex.withLock {
        val row = db.downloadDao().nextQueued() ?: return@withLock null
        db.downloadDao().setStatus(
            row.serverId, row.key, DownloadEntity.STATUS_DOWNLOADING, null, System.currentTimeMillis()
        )
        row
    }

    private suspend fun laneLoop() {
        try {
            while (true) {
                val row = claimNext() ?: break

                // 网络闸门：不满足就挂起，横幅亮「等待 Wi-Fi」；放行后自动续跑
                while (!networkAllowed()) {
                    _pausedForNetwork.value = true
                    db.downloadDao().setStatus(
                        row.serverId, row.key, DownloadEntity.STATUS_PAUSED,
                        "等待 Wi-Fi（当前为流量网络或无网络）", System.currentTimeMillis(),
                    )
                    delay(3000)
                }
                _pausedForNetwork.value = false

                // 子协程挂 app 级 scope：lane 被收回（并发数调小）时正在跑的下载不受影响；
                // 暂停/取消通过 activeJobs 命中这个子协程
                val id = "${row.serverId}|${row.key}"
                val child = scope.launch {
                    try {
                        runOne(row)
                    } catch (e: CancellationException) {
                        throw e
                    } catch (e: Exception) {
                        db.downloadDao().setStatus(
                            row.serverId, row.key, DownloadEntity.STATUS_FAILED,
                            e.message ?: "下载失败", System.currentTimeMillis(),
                        )
                    }
                }
                activeJobs[id] = child
                child.join()
                activeJobs.remove(id)
            }
        } finally {
            _pausedForNetwork.value = false
            lanes.removeAll { it.isCancelled || it.isCompleted }
        }
    }

    /** 下载进度内存缓冲：下载回调是同步的，DB 回写由独立节流协程做。 */
    private class Progress {
        @Volatile var done: Long = 0
        @Volatile var total: Long = 0
    }

    private suspend fun runOne(row: DownloadEntity) {
        val server = db.serverDao().getById(row.serverId)
            ?: throw Exception("服务器不存在（可能已被删除）")
        val api = ApiClient(server.baseUrl, server.token)
        val detail = api.mediaDetail(row.sourceId, row.key)

        db.downloadDao().setStatus(
            row.serverId, row.key, DownloadEntity.STATUS_DOWNLOADING, null, System.currentTimeMillis()
        )
        val staging = DownloadLayouts.stagingDir(app, row.key).apply { mkdirs() }

        // 进度上报：500ms 节流写库，UI（横幅/通知/行内百分比）都从这来
        val progress = Progress()
        val reporter = scope.launch {
            while (isActive) {
                db.downloadDao().setProgress(
                    row.serverId, row.key, progress.done, progress.total, System.currentTimeMillis()
                )
                delay(500)
            }
        }
        try {
            when (row.layout) {
                "dual" -> {
                    val (avid, cid) = DownloadLayouts.parseDualKey(row.key)
                        ?: throw Exception("key 不是 dual 形态：${row.key}")
                    val (videoUrl, audioUrl) = api.dualStreamUrls(detail)
                        ?: throw Exception("服务器未提供双流直链（delivery=${detail.delivery.joinToString("/")})")
                    val videoTrack = detail.tracks.firstOrNull { it.role == "video" }
                    val audioTrack = detail.tracks.firstOrNull { it.role == "audio" }
                    val videoPart = File(staging, "video.m4s.part")
                    val audioPart = File(staging, "audio.m4s.part")
                    val videoSize = videoTrack?.sizeBytes ?: 0L

                    fetchWithResume(api, videoUrl, videoPart) { d, t ->
                        progress.done = d; progress.total = t
                    }
                    checkSize(videoPart, videoSize)

                    if (audioUrl != null) {
                        fetchWithResume(api, audioUrl, audioPart) { d, t ->
                            // 音频阶段的进度基线 = 视频已完成的字节数
                            progress.done = videoPart.length() + d
                            progress.total = videoSize + t
                        }
                        checkSize(audioPart, audioTrack?.sizeBytes ?: 0L)
                    }

                    val itemDir = DownloadLayouts.dualItemDir(DownloadLayouts.downloadsRoot(app), avid, cid)
                    val tagDir = File(itemDir, detail.tagCode?.toString() ?: "0").apply { mkdirs() }
                    val videoLen = videoPart.length()
                    val audioLen = audioPart.takeIf { it.isFile }?.length() ?: 0L
                    videoPart.moveTo(File(tagDir, "video.m4s"))
                    audioPart.takeIf { it.isFile }?.moveTo(File(tagDir, "audio.m4s"))
                    DownloadLayouts.writeEntryJson(
                        itemDir,
                        DownloadLayouts.EntrySpec(
                            title = detail.title, avid = avid, cid = cid, typeTag = tagDir.name,
                            durationMs = detail.durationMs,
                            totalBytes = maxOf(detail.sizeBytes, videoLen + audioLen),
                            hasAudio = audioUrl != null,
                            upName = detail.upName, width = detail.width, height = detail.height,
                        ),
                    )
                }

                "single" -> {
                    val relPath = row.relPath ?: throw Exception("缺少相对路径，无法落盘")
                    val url = api.singleStreamUrl(detail)
                        ?: throw Exception("服务器未提供单流直链（delivery=${detail.delivery.joinToString("/")})")
                    val part = File(staging, "file.part")
                    fetchWithResume(api, url, part) { d, t ->
                        progress.done = d; progress.total = t
                    }
                    checkSize(part, detail.tracks.firstOrNull()?.sizeBytes ?: 0L)

                    val target = DownloadLayouts.singleFile(DownloadLayouts.downloadsRoot(app), relPath)
                    target.parentFile?.mkdirs()
                    part.moveTo(target)
                }

                else -> throw Exception("未知布局：${row.layout}")
            }
        } finally {
            reporter.cancel()
        }

        staging.deleteRecursively()
        fetchPoster(api, detail, row.key)
        rescanDownloads()

        // 服务端对该条目的用户覆盖（旋转等）不能因为"缓存了一份"就丢：
        // 重扫的 @Upsert 是文件现状视角，落库后把 media_meta 的 rotation 补回条目上，
        // 播放页/缩略图才和原条目表现一致（手机端 Room 的 items.rotation 是本地份）。
        detail.meta?.rotation?.takeIf { it != 0 }?.let { rot ->
            runCatching { db.itemDao().setRotation(sourceIdBlocking(), row.key, rot) }
        }

        db.downloadDao().setStatus(
            row.serverId, row.key, DownloadEntity.STATUS_DONE, null, System.currentTimeMillis()
        )
    }

    /** 带重试的续传下载：IOException 自动从 part 长度续传，最多 4 次。 */
    private suspend fun fetchWithResume(api: ApiClient, url: String, part: File, onProgress: (Long, Long) -> Unit) {
        var attempts = 0
        while (true) {
            try {
                currentCoroutineContext().ensureActive()
                api.downloadToFile(url, part, part.length(), onProgress)
                return
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                currentCoroutineContext().ensureActive() // 取消伪装成 IOException 时按取消处理
                attempts++
                if (attempts >= 4) throw Exception("下载失败（已重试 $attempts 次）：${e.message}", e)
                delay(2000L * attempts)
            }
        }
    }

    /** 网络闸门：Wi-Fi / 以太网 / 非计量网络；会话放行优先。 */
    private fun networkAllowed(): Boolean {
        if (allowCellular.value) return true
        val cm = app.getSystemService(ConnectivityManager::class.java) ?: return false
        val net = cm.activeNetwork ?: return false
        val caps = cm.getNetworkCapabilities(net) ?: return false
        return caps.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) ||
            caps.hasTransport(NetworkCapabilities.TRANSPORT_ETHERNET) ||
            caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_METERED)
    }

    private fun checkSize(part: File, expected: Long) {
        if (expected <= 0) return
        val actual = part.length()
        if (actual != expected) {
            part.delete() // 服务器文件变了，半成品不可信，重下
            throw Exception("文件大小不符（期望 $expected，实际 $actual）")
        }
    }

    private companion object {
        /** 并发数在 SharedPreferences 里的键（与播放器设置同一个文件）。 */
        const val KEY_CONCURRENCY = "download_concurrency"
    }

    private fun File.moveTo(target: File) {
        if (renameTo(target)) return
        inputStream().use { input -> target.outputStream().use { input.copyTo(it) } }
        delete()
    }

    /** 服务端缩略图搬到下载源的海报目录，重扫时 ensurePoster 直接命中。失败不阻塞。 */
    private suspend fun fetchPoster(api: ApiClient, detail: MediaDetail, key: String) {
        val url = detail.posterUrl ?: return
        try {
            val posterDir = scanner.posterDir(sourceIdBlocking())
            val target = File(posterDir, "$key.jpg")
            if (target.isFile && target.length() > 0) return
            posterDir.mkdirs()
            api.downloadToFile(api.mediaUrl(url), target, 0)
        } catch (_: Exception) {
            // 海报失败不影响下载完成；重扫时 ensurePoster 兜底
        }
    }

    private suspend fun rescanDownloads() {
        try {
            scanner.scan(ensureSource())
        } catch (_: Exception) {
            // 重扫失败不回滚下载结果
        }
    }
}
