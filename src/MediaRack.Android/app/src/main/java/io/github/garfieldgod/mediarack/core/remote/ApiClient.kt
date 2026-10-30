package io.github.garfieldgod.mediarack.core.remote

import kotlinx.serialization.json.Json
import okhttp3.HttpUrl.Companion.toHttpUrl
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import java.io.File
import java.io.IOException
import java.util.concurrent.TimeUnit

/** HTTP 层错误：code 给程序判断，message 给人看（契约 Error schema，可直接上屏）。 */
class ApiException(
    val httpStatus: Int,
    val code: String,
    override val message: String,
) : Exception(message)

/** 网络层失败（DNS/超时/连接拒绝等）。message 是一句中文结论，不含 OkHttp 原文。 */
class NetworkException(cause: IOException) : Exception(networkFailureReason(cause), cause)

/**
 * IOException → 一句中文结论。
 *
 * OkHttp 的原文形如 `Failed to connect to /192.0.2.10:8080`、`Unable to resolve host
 * "x": No address associated with hostname`——半中半英、长到必然换行，直接塞进提示框里
 * 就是"无法连接服务器：100.x.x.x 换行 Unable to resolve..."这种灾难排版。
 * 提示只需要"连不上的性质"，原始异常留在 cause 里给日志。
 */
internal fun networkFailureReason(e: IOException): String = when (e) {
    is java.net.UnknownHostException -> "域名解析失败"
    is java.net.NoRouteToHostException -> "网络不可达"
    is java.net.ConnectException -> "连接被拒绝"
    is java.net.SocketTimeoutException ->
        if (e.message?.contains("connect", ignoreCase = true) == true) "连接超时" else "读取超时"
    is javax.net.ssl.SSLException -> "证书校验失败"
    is java.io.InterruptedIOException -> "读取超时"
    else -> "无法建立连接"
}

/**
 * MediaRack 契约客户端（health / auth / sources / library / media 详情）。
 *
 * - 手写 OkHttp，不引 Retrofit（端点少、只读、播放走媒体 URL）
 * - 鉴权：API 请求带 `Authorization: Bearer`；流与海报是播放器/图片加载器自己发请求，
 *   加不了 header，统一用 `?token=`（契约三路凭据等价）
 * - url 字段是相对路径时按 base 拼接；流/海报 URL 一律经 [mediaUrl] 补 token
 */
class ApiClient(
    baseUrl: String,
    private val token: String?,
    client: OkHttpClient? = null,
) {
    companion object {
        val JSON = Json { ignoreUnknownKeys = true; coerceInputValues = true }

        /**
         * 配对码（新版服务端「配对二维码」）：`MR1|{"token":"x","urls":[...]}`——
         * 一张码打包全部候选地址（Tailscale 优先 / 内网 / 手填公网），扫码端并发探活自动选。
         */
        @kotlinx.serialization.Serializable
        data class PairingPayload(val token: String? = null, val urls: List<String> = emptyList())

        private const val PAIRING_PREFIX = "MR1|"

        /**
         * 解析配对二维码内容，统一为 (候选 base 列表, token)。
         *
         * - `MR1|{json}`：配对码，多候选地址
         * - `http://<ip>:<port>/s/{源地址}#token=xxx`：单地址码 / 老版主路径（令牌跟源走）
         * - 兼容 `?token=` 形式与无令牌纯地址
         *
         * 返回 null 表示无法解析。
         */
        fun parsePairingPayload(text: String): PairingPayload? {
            val s = text.trim()
            if (s.isEmpty()) return null

            if (s.startsWith(PAIRING_PREFIX)) {
                return runCatching {
                    val p = JSON.decodeFromString<PairingPayload>(s.substring(PAIRING_PREFIX.length))
                    val urls = p.urls.mapNotNull { normalizeBaseUrl(it) }.distinct()
                    if (urls.isEmpty()) null else PairingPayload(p.token, urls)
                }.getOrNull()
            }

            val hashMatch = Regex("^(https?://[^#\\s]+)#token=([A-Za-z0-9._-]+)$").find(s)
            if (hashMatch != null) {
                val base = normalizeBaseUrl(hashMatch.groupValues[1]) ?: return null
                return PairingPayload(token = hashMatch.groupValues[2], urls = listOf(base))
            }
            val queryMatch = Regex("^(https?://[^\\s]+)[?&]token=([A-Za-z0-9._-]+)$").find(s)
            if (queryMatch != null) {
                val base = normalizeBaseUrl(queryMatch.groupValues[1]) ?: return null
                return PairingPayload(token = queryMatch.groupValues[2], urls = listOf(base))
            }
            val base = normalizeBaseUrl(s) ?: return null
            return PairingPayload(token = null, urls = listOf(base))
        }

        /**
         * 并发探活候选地址（health 免鉴权），**按候选顺序**返回第一个可达的——
         * 顺序即服务端给的优先级（Tailscale 在前）。
         * 全部不可达返回 null。超时统一 [timeoutMs]，不让人等满 OkHttp 默认值。
         */
        fun pickReachableBaseUrl(
            urls: List<String>,
            timeoutMs: Long = 2_500,
            client: OkHttpClient? = null,
        ): String? {
            if (urls.isEmpty()) return null
            val http = client ?: OkHttpClient.Builder()
                .connectTimeout(timeoutMs, java.util.concurrent.TimeUnit.MILLISECONDS)
                .readTimeout(timeoutMs, java.util.concurrent.TimeUnit.MILLISECONDS)
                .build()

            val results = urls.map { url ->
                url to java.util.concurrent.CompletableFuture.supplyAsync {
                    runCatching {
                        http.newCall(Request.Builder().url("$url/api/v1/health").build())
                            .execute().use { it.isSuccessful }
                    }.getOrDefault(false)
                }
            }
            // 每个探活自身有超时兜底；这里再兜一层，保证函数总能在有限时间内返回
            results.forEach { (_, f) -> runCatching { f.get(timeoutMs + 1_000, java.util.concurrent.TimeUnit.MILLISECONDS) } }
            return results.firstOrNull { (_, f) -> f.getNow(false) }?.first
        }

        /** 规范化用户输入：去尾斜杠；无 scheme 补 http://。返回 null 表示格式不合法。 */
        fun normalizeBaseUrl(input: String): String? {
            var s = input.trim()
            if (s.isEmpty()) return null
            if (!s.startsWith("http://") && !s.startsWith("https://")) s = "http://$s"
            s = s.trimEnd('/')
            return try {
                val u = s.toHttpUrl()
                // base 只允许 host[:port][/路径前缀]，不允许 query/fragment
                if (u.querySize == 0 && u.fragment == null) s else null
            } catch (_: IllegalArgumentException) {
                null
            }
        }

        /** 服务端源地址（slug）规则（docs/design.md §11）：小写字母/数字开头，可含 - _，最长 32 位。 */
        val SLUG_PATTERN = Regex("^[a-z0-9][a-z0-9_-]{0,31}$")

        /**
         * 拼源门户 base URL：`http://host:port/s/{slug}`。
         *
         * 服务端新版只有 `/s/{源地址}` 一个对外入口（根路径 = 管理页，局域网 403），
         * 所以"一条配置 = 一个源"：不写源地址就不能连。
         * 源地址一栏允许直接粘完整路径（以 / 开头，如 `/s/movie` 或反代前缀 `/mr/s/movie`），
         * 此时按原样拼接、不再套 slug 规则。
         */
        fun buildSourceBaseUrl(host: String, port: String, sourcePath: String): String? {
            val h = host.trim()
            val p = port.trim()
            val s = sourcePath.trim()
            if (h.isEmpty() || p.isEmpty() || s.isEmpty()) return null
            val path = if (s.startsWith("/")) s.trimEnd('/') else "/s/$s"
            return normalizeBaseUrl("$h:$p$path")
        }

        /**
         * 拆 base URL → (host, port, 源地址路径，带前导 `/`)。
         * 无路径前缀（老版本配置）时 source 为空串——调用方据此提示重新扫码。
         */
        fun splitSourceBaseUrl(baseUrl: String?): Triple<String, String, String> {
            if (baseUrl.isNullOrEmpty()) return Triple("", "", "")
            val noScheme = baseUrl.substringAfter("://", baseUrl)
            val hostPart = noScheme.substringBefore('/')
            val path = noScheme.substringAfter('/', "")
            return Triple(
                hostPart.substringBefore(':'),
                hostPart.substringAfter(':', ""),
                if (path.isEmpty()) "" else "/$path",
            )
        }
    }

    private val base = baseUrl.trimEnd('/')
    private val http = client ?: OkHttpClient.Builder()
        .connectTimeout(5, TimeUnit.SECONDS)
        .readTimeout(20, TimeUnit.SECONDS)
        .build()

    /** 下载专用：大文件单次 read 可能超过普通请求的读超时，放宽到 60s（仍是单次 read 限时，不卡总时长）。 */
    private val downloadHttp by lazy {
        http.newBuilder().readTimeout(60, TimeUnit.SECONDS).build()
    }

    private fun apiGet(path: String): Request.Builder {
        val b = Request.Builder().url("$base$path")
        if (!token.isNullOrEmpty()) b.header("Authorization", "Bearer $token")
        return b
    }

    private inline fun <reified T : Any> apiWrite(method: String, path: String, body: T): Request.Builder {
        val json = JSON.encodeToString(kotlinx.serialization.serializer<T>(), body)
        return apiGet(path).method(
            method,
            okhttp3.RequestBody.create("application/json".toMediaType(), json),
        )
    }

    private fun apiPost(path: String): Request.Builder = apiGet(path).method("POST", "{}".toRequestBody())

    private inline fun <reified T : Any> apiPost(path: String, body: T): Request.Builder = apiWrite("POST", path, body)

    private inline fun <reified T : Any> apiPatch(path: String, body: T): Request.Builder = apiWrite("PATCH", path, body)

    private fun apiDelete(path: String): Request.Builder = apiGet(path).method("DELETE", "".toRequestBody())

    private inline fun <reified T> execute(builder: Request.Builder): T {
        val resp = try {
            http.newCall(builder.build()).execute()
        } catch (e: IOException) {
            throw NetworkException(e)
        }
        resp.use {
            val body = it.body?.string() ?: ""
            if (!it.isSuccessful) {
                val err = runCatching { JSON.decodeFromString<ErrorBody>(body) }.getOrNull()
                throw ApiException(
                    httpStatus = it.code,
                    code = err?.code ?: "http_${it.code}",
                    message = err?.message ?: "HTTP ${it.code}",
                )
            }
            return JSON.decodeFromString(body)
        }
    }

    /** 不关心响应体的写操作（DELETE / 进度上报）：只看状态码。 */
    private fun executeVoid(builder: Request.Builder) {
        val resp = try {
            http.newCall(builder.build()).execute()
        } catch (e: IOException) {
            throw NetworkException(e)
        }
        resp.use {
            if (!it.isSuccessful) {
                val body = it.body?.string() ?: ""
                val err = runCatching { JSON.decodeFromString<ErrorBody>(body) }.getOrNull()
                throw ApiException(
                    httpStatus = it.code,
                    code = err?.code ?: "http_${it.code}",
                    message = err?.message ?: "HTTP ${it.code}",
                )
            }
        }
    }

    @kotlinx.serialization.Serializable
    private data class ErrorBody(val code: String, val message: String)

    /** 免鉴权探活；用于添加服务器时验证可达性。 */
    fun health(): Health = execute(apiGet("/api/v1/health"))

    /** 免鉴权；确认令牌是否有效。**带 Bearer 发**：令牌填错了这里就能看出来（authenticated=false），不用等到播放时 401。 */
    fun authState(): AuthState = execute(apiGet("/api/v1/auth/state"))

    fun sources(): List<SourceInfo> {
        val page = execute<SourcesResponse>(apiGet("/api/v1/sources"))
        return page.sources
    }

    @kotlinx.serialization.Serializable
    private data class SourcesResponse(val sources: List<SourceInfo>)

    /** 聚合列表（跨源去重）。[collectionId] / [favoriteId] 二选一，都是 SQL 层过滤。 */
    fun library(
        offset: Int = 0,
        limit: Int = 60,
        query: String? = null,
        collectionId: String? = null,
        favoriteId: String? = null,
        /** 排序：null = 服务端默认（标题）；`added` = 按收藏夹加入时间倒序；`recommend` = 随机序（seed 决定，不带 seed 时退回默认序）；播过的最多占 1/10 名额。 */
        sort: String? = null,
        /** sort=recommend 的确定性种子：会话内复用同一 seed 翻页稳定，换 seed 即换一批。 */
        seed: String? = null,
    ): MediaPage {
        val sb = StringBuilder("/api/v1/library?offset=").append(offset).append("&limit=").append(limit)
        if (!query.isNullOrEmpty()) sb.append("&q=").append(enc(query))
        if (!collectionId.isNullOrEmpty()) sb.append("&collection=").append(enc(collectionId))
        if (!favoriteId.isNullOrEmpty()) sb.append("&favorite=").append(enc(favoriteId))
        if (!sort.isNullOrEmpty()) sb.append("&sort=").append(enc(sort))
        if (!seed.isNullOrEmpty()) sb.append("&seed=").append(enc(seed))
        return execute(apiGet(sb.toString()))
    }

    // ── 合集（结构层，只读）─────────────────────────────────

    /** 合集列表。[sourceId] 为 null 时返回全部可见源。 */
    fun collections(sourceId: String? = null): List<RemoteCollection> {
        val q = if (sourceId.isNullOrEmpty()) "" else "?source=" + enc(sourceId)
        return execute(apiGet("/api/v1/collections$q"))
    }

    // ── 收藏（用户层，跨端）─────────────────────────────────

    fun favorites(): List<Favorite> = execute(apiGet("/api/v1/favorites"))

    fun createFavorite(name: String): Favorite =
        execute(apiPost("/api/v1/favorites", NamePatch(name)))

    fun renameFavorite(id: String, name: String): Favorite =
        execute(apiPatch("/api/v1/favorites/${enc(id)}", NamePatch(name)))

    fun deleteFavorite(id: String) {
        executeVoid(apiDelete("/api/v1/favorites/${enc(id)}"))
    }

    fun favoriteItems(id: String, add: List<ItemRefBody>? = null, remove: List<ItemRefBody>? = null): ChangeCount =
        execute(apiPost("/api/v1/favorites/${enc(id)}/items", FavoriteItemsPatch(add, remove)))

    fun favoriteCollections(id: String, add: List<String>? = null, remove: List<String>? = null): ChangeCount =
        execute(apiPost("/api/v1/favorites/${enc(id)}/collections", FavoriteCollectionsPatch(add, remove)))

    fun favoriteCollectionsOf(id: String): List<RemoteCollection> =
        execute(apiGet("/api/v1/favorites/${enc(id)}/collections"))

    /** 播放进度上报（跨端续播）。游标在服务端，与本地 marks 各存一份。 */
    fun reportProgress(source: String, key: String, posMs: Long, completed: Boolean? = null) {
        executeVoid(apiPost("/api/v1/progress/${enc(source)}/${enc(key)}", ProgressPatch(posMs, completed)))
    }

    /**
     * 写入用户覆盖的旋转角（0/90/180/270）。写入 `media_meta.rotation`，与容器自带的
     * rotation 元数据正交（后者解码器已自动应用）。
     */
    fun setRotation(source: String, key: String, rotation: Int) {
        executeVoid(
            apiPost("/api/v1/meta/${enc(source)}/${enc(key)}", MediaPatchBody(rotation = rotation))
        )
    }

    fun mediaDetail(source: String, key: String): MediaDetail =
        execute(apiGet("/api/v1/media/$source/$key"))

    /** 首帧缩略图地址（直连媒体路径，`?token=` 鉴权）。服务器首次请求会现抽第 0 帧并缓存。 */
    fun thumbUrl(source: String, key: String): String =
        mediaUrl("/media/${enc(source)}/${enc(key)}/thumb.jpg")

    private fun enc(v: String): String = java.net.URLEncoder.encode(v, "UTF-8")

    /** 拼绝对流/海报地址并补 token（供 ExoPlayer / 图片加载使用）。 */
    fun mediaUrl(pathOrUrl: String): String {
        val url = if (pathOrUrl.startsWith("http://") || pathOrUrl.startsWith("https://")) {
            pathOrUrl
        } else {
            base + (if (pathOrUrl.startsWith("/")) pathOrUrl else "/$pathOrUrl")
        }
        if (token.isNullOrEmpty()) return url
        return url + (if (url.contains('?')) "&" else "?") + "token=" + java.net.URLEncoder.encode(token, "UTF-8")
    }

    /** 从详情里挑播放地址：只处理单流（single 的 /file）。 */
    fun singleStreamUrl(detail: MediaDetail): String? {
        if (detail.layout != "single") return null
        if (!detail.delivery.contains("direct")) return null
        return detail.tracks.firstOrNull { it.role == "video" }?.url?.let { mediaUrl(it) }
            ?: detail.tracks.firstOrNull()?.url?.let { mediaUrl(it) }
    }

    /**
     * 双流地址：dual + delivery 含 direct，video/audio 两条 m4s 各自补 token。
     * 返回 (videoUrl, audioUrl)；任一条件不满足返回 null。
     */
    fun dualStreamUrls(detail: MediaDetail): Pair<String, String>? {
        if (detail.layout != "dual") return null
        if (!detail.delivery.contains("direct")) return null
        val video = detail.tracks.firstOrNull { it.role == "video" }?.url ?: return null
        val audio = detail.tracks.firstOrNull { it.role == "audio" }?.url ?: return null
        return mediaUrl(video) to mediaUrl(audio)
    }

    /**
     * 直连播不了时的兜底地址：服务端用 ffmpeg 把双流合成一个 mp4（含音轨）。
     *
     * 双流直连要求客户端自己解析 m4s，而 B 站缓存的 HEVC m4s 普遍把参数集放在
     * 分片里（hvcC 的 numOfArrays=0），ExoPlayer 解析时直接越界崩（见 FeedScreen
     * 播放失败日志）。服务端转封装会把参数集写回 hvcC，播出来就正常了。
     */
    fun remuxUrl(source: String, key: String): String =
        mediaUrl("/api/v1/export/${enc(source)}/${enc(key)}.mp4")

    /**
     * 等转封装就绪：202 = 服务端正在做，200/206 = 可以直接播。
     *
     * 只探一个字节（Range 0-0），不把整个文件拉下来。阻塞式，必须在 IO 线程调。
     */
    fun remuxReady(url: String, timeoutMs: Long = 90_000): Boolean {
        val deadline = System.currentTimeMillis() + timeoutMs
        while (System.currentTimeMillis() < deadline) {
            val code = runCatching {
                http.newCall(Request.Builder().url(url).header("Range", "bytes=0-0").build())
                    .execute().use { it.code }
            }.getOrDefault(0)

            when (code) {
                200, 206 -> return true
                202 -> Thread.sleep(600)
                else -> return false
            }
        }
        return false
    }

    /** 聚合页序列化（缓存：存原始契约形状，posterUrl 保持相对路径）。 */
    fun encodePage(page: MediaPage): String = JSON.encodeToString(MediaPage.serializer(), page)

    /** 反序列化缓存页；损坏/过期的 JSON 返回 null（缓存坏了当没有缓存）。不依赖实例状态。 */
    fun parseCachedPage(json: String): MediaPage? =
        runCatching { JSON.decodeFromString(MediaPage.serializer(), json) }.getOrNull()

    /**
     * 流式下载到文件（断点续传核心）。
     *
     * - `resumeFrom > 0` 时带 `Range: bytes=<n>-`：服务器回 206 → 追加写入；
     *   回 200（不支持 / 忽略 Range）→ 截断重下
     * - 总字节数优先取 Content-Range 的完整长度（`bytes 5-9/10` 的 10），退化 Content-Length
     * - [onProgress](done, total)：done 含已续传部分；total <= 0 表示未知
     * - 协程取消经 OkHttp call 中断表现为 IOException，调用方用 `ensureActive` 区分
     */
    fun downloadToFile(
        url: String,
        dest: File,
        resumeFrom: Long = 0,
        onProgress: (done: Long, total: Long) -> Unit = { _, _ -> },
    ) {
        val request = Request.Builder().url(url).apply {
            if (resumeFrom > 0) header("Range", "bytes=$resumeFrom-")
        }.build()

        downloadHttp.newCall(request).execute().use { resp ->
            if (!resp.isSuccessful) throw IOException("HTTP ${resp.code}")
            val body = resp.body ?: throw IOException("HTTP ${resp.code}：响应体为空")

            val appending = resp.code == 206 && resumeFrom > 0
            var total = -1L
            resp.header("Content-Range")?.let { cr ->
                // 形如 "bytes 5-9/10"，取 "/" 后的完整长度
                val idx = cr.lastIndexOf('/')
                if (idx >= 0) total = cr.substring(idx + 1).toLongOrNull() ?: -1L
            }
            if (total < 0) total = body.contentLength()

            var done = if (appending) resumeFrom else 0L
            if (total > 0) onProgress(done, total)

            java.io.FileOutputStream(dest, appending).use { out ->
                body.byteStream().use { input ->
                    val buf = ByteArray(64 * 1024)
                    while (true) {
                        val n = input.read(buf)
                        if (n < 0) break
                        out.write(buf, 0, n)
                        done += n
                        onProgress(done, if (total > 0) total else done)
                    }
                    out.flush()
                }
            }
        }
    }
}
