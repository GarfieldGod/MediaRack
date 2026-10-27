package io.github.garfieldgod.mediarack

import io.github.garfieldgod.mediarack.core.remote.ApiClient
import io.github.garfieldgod.mediarack.core.remote.ApiException
import io.github.garfieldgod.mediarack.core.remote.ItemRefBody
import okhttp3.OkHttpClient
import okhttp3.mockwebserver.MockResponse
import okhttp3.mockwebserver.MockWebServer
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test

/**
 * 契约客户端行为验证（MockWebServer，纯 JVM）。
 * JSON fixture 按契约 openapi.v1.yaml 的形状手写，字段与真实服务端一致。
 */
class ApiClientTest {
    private lateinit var server: MockWebServer
    private lateinit var client: ApiClient

    @Before
    fun setUp() {
        server = MockWebServer()
        server.start()
        client = ApiClient(server.url("/").toString().trimEnd('/'), "s3cret", OkHttpClient())
    }

    @After
    fun tearDown() {
        server.shutdown()
    }

    @Test
    fun `normalizeBaseUrl_补 scheme 去尾斜杠`() {
        assertEquals("http://192.0.2.10:8080", ApiClient.normalizeBaseUrl("192.0.2.10:8080"))
        assertEquals("http://1.2.3.4:8080", ApiClient.normalizeBaseUrl("http://1.2.3.4:8080/"))
        assertEquals("http://1.2.3.4:8080", ApiClient.normalizeBaseUrl(" http://1.2.3.4:8080 "))
        assertNull(ApiClient.normalizeBaseUrl(""))
        assertNull(ApiClient.normalizeBaseUrl("http://a?x=1"))
    }

    @Test
    fun `parsePairingPayload_主路径 hash token`() {
        val p = ApiClient.parsePairingPayload("http://192.0.2.10:8080/#token=s3cret")!!
        assertEquals(listOf("http://192.0.2.10:8080"), p.urls)
        assertEquals("s3cret", p.token)
    }

    @Test
    fun `parsePairingPayload_新版源门户格式_带路径前缀`() {
        val p = ApiClient.parsePairingPayload("http://192.0.2.20:8080/s/movie#token=s3cret")!!
        assertEquals(listOf("http://192.0.2.20:8080/s/movie"), p.urls)
        assertEquals("s3cret", p.token)
    }

    @Test
    fun `parsePairingPayload_配对码_多候选地址`() {
        val p = ApiClient.parsePairingPayload(
            """MR1|{"token":"s3cret","urls":[""" +
                """"http://192.0.2.5:8080/s/bili",""" +
                """"http://192.0.2.20:8080/s/bili",""" +
                """"http://home.example.com:8080/s/bili"]}""",
        )!!
        assertEquals(
            listOf(
                "http://192.0.2.5:8080/s/bili",
                "http://192.0.2.20:8080/s/bili",
                "http://home.example.com:8080/s/bili",
            ),
            p.urls,
        )
        assertEquals("s3cret", p.token)
    }

    @Test
    fun `parsePairingPayload_配对码_垃圾地址被过滤_全坏返回null`() {
        // 非法 URL 会被 normalizeBaseUrl 过滤掉
        val p = ApiClient.parsePairingPayload("""MR1|{"urls":["not a url"]}""")
        assertNull(p)
    }

    @Test
    fun `pickReachableBaseUrl_按候选顺序选第一个可达的`() {
        val bad = MockWebServer().apply { start() }
        val good1 = MockWebServer().apply { start() }
        val good2 = MockWebServer().apply { start() }
        good1.enqueue(MockResponse().setBody("""{"status":"ok"}"""))
        good2.enqueue(MockResponse().setBody("""{"status":"ok"}"""))
        bad.shutdown()

        val urls = listOf(
            bad.url("/s/bili").toString().trimEnd('/'),   // 探活失败
            good1.url("/s/bili").toString().trimEnd('/'), // 第一个可达
            good2.url("/s/bili").toString().trimEnd('/'),
        )
        assertEquals(urls[1], ApiClient.pickReachableBaseUrl(urls, client = OkHttpClient()))
        good1.shutdown()
        good2.shutdown()
    }

    @Test
    fun `pickReachableBaseUrl_全部不可达返回null`() {
        val bad = MockWebServer().apply { start() }
        bad.enqueue(MockResponse().setResponseCode(404))
        assertEquals(null, ApiClient.pickReachableBaseUrl(listOf(bad.url("/").toString().trimEnd('/')), client = OkHttpClient()))
        bad.shutdown()
    }

    @Test
    fun `buildSourceBaseUrl_slug 与完整路径两种写法`() {
        assertEquals(
            "http://192.0.2.20:8080/s/movie",
            ApiClient.buildSourceBaseUrl("192.0.2.20", "8080", "movie"),
        )
        // 扫码/编辑回填得到的是完整路径，原样接受
        assertEquals(
            "http://192.0.2.20:8080/s/movie",
            ApiClient.buildSourceBaseUrl("192.0.2.20", "8080", "/s/movie"),
        )
        // 反代前缀也放行
        assertEquals(
            "http://a.b:8080/mr/s/movie",
            ApiClient.buildSourceBaseUrl("a.b", "8080", "/mr/s/movie"),
        )
        // 缺任何一项都不成配置（不写源地址不能连）
        assertNull(ApiClient.buildSourceBaseUrl("192.0.2.20", "8080", ""))
        assertNull(ApiClient.buildSourceBaseUrl("", "8080", "movie"))
        assertNull(ApiClient.buildSourceBaseUrl("192.0.2.20", "", "movie"))
    }

    @Test
    fun `splitSourceBaseUrl_带路径与老版无路径`() {
        assertEquals(
            Triple("192.0.2.20", "8080", "/s/movie"),
            ApiClient.splitSourceBaseUrl("http://192.0.2.20:8080/s/movie"),
        )
        // 老版本配置：无路径 → source 为空串，调用方据此提示重新扫码
        assertEquals(
            Triple("1.2.3.4", "8080", ""),
            ApiClient.splitSourceBaseUrl("http://1.2.3.4:8080"),
        )
        assertEquals(Triple("", "", ""), ApiClient.splitSourceBaseUrl(null))
    }

    @Test
    fun `slug规则_与服务端 schema 一致`() {
        assertTrue(ApiClient.SLUG_PATTERN.matches("movie"))
        assertTrue(ApiClient.SLUG_PATTERN.matches("m-1_2"))
        // 大写、空格、斜杠、点都不行
        org.junit.Assert.assertFalse(ApiClient.SLUG_PATTERN.matches("Movie"))
        org.junit.Assert.assertFalse(ApiClient.SLUG_PATTERN.matches("a b"))
        org.junit.Assert.assertFalse(ApiClient.SLUG_PATTERN.matches("s/movie"))
        org.junit.Assert.assertFalse(ApiClient.SLUG_PATTERN.matches("-abc"))
    }

    @Test
    fun `parsePairingPayload_无令牌纯地址`() {
        val p = ApiClient.parsePairingPayload("1.2.3.4:8080")!!
        assertEquals(listOf("http://1.2.3.4:8080"), p.urls)
        assertNull(p.token)
        assertNull(ApiClient.parsePairingPayload("not a url"))
    }

    // ── 合集 / 收藏 / 进度（v4 服务端）────────────────────────

    @Test
    fun `collections_解析平铺合集与kind`() {
        server.enqueue(
            MockResponse().setBody(
                "[" +
                    "{\"id\":\"up:bili:100\",\"name\":\"阿婆主A\",\"sourceId\":\"bili\"," +
                    "\"kind\":\"up\",\"path\":\"100\",\"itemCount\":12}," +
                    "{\"id\":\"dir:rec:/Videos\",\"name\":\"Videos\",\"sourceId\":\"rec\"," +
                    "\"kind\":\"folder\",\"path\":\"Videos\",\"itemCount\":5}]",
            ),
        )

        val rows = client.collections()
        assertEquals(2, rows.size)
        assertEquals("up", rows[0].kind)
        assertEquals(12, rows[0].itemCount)
        assertEquals("Videos", rows[1].path)
        assertEquals("/api/v1/collections", server.takeRequest().path)
    }

    @Test
    fun `collections_带 source 过滤`() {
        server.enqueue(MockResponse().setBody("[]"))
        client.collections(sourceId = "rec")
        assertEquals("/api/v1/collections?source=rec", server.takeRequest().path)
    }

    @Test
    fun `favoriteItems_POST 带 add 与 remove`() {
        server.enqueue(MockResponse().setBody("{\"added\":1,\"removed\":0}"))

        val changed = client.favoriteItems("fav:default", add = listOf(ItemRefBody("rec", "s_abc")))

        assertEquals(1, changed.added)
        val req = server.takeRequest()
        assertEquals("POST", req.method)
        assertEquals("/api/v1/favorites/fav%3Adefault/items", req.path)
        val body = req.body.readUtf8()
        assertTrue(body.contains("\"source\":\"rec\""))
        assertTrue(body.contains("\"key\":\"s_abc\""))
    }

    @Test
    fun `favorites_解析收藏夹计数`() {
        server.enqueue(
            MockResponse().setBody(
                "[{\"id\":\"fav:default\",\"name\":\"默认收藏夹\",\"itemCount\":7,\"collectionCount\":2}]",
            ),
        )

        val list = client.favorites()
        assertEquals(7, list[0].itemCount)
        assertEquals(2, list[0].collectionCount)
    }

    @Test
    fun `library_带 favorite 参数走 SQL 层过滤`() {
        server.enqueue(MockResponse().setBody("{\"total\":0,\"items\":[]}"))
        client.library(favoriteId = "fav:default")
        assertEquals("/api/v1/library?offset=0&limit=60&favorite=fav%3Adefault", server.takeRequest().path)
    }

    @Test
    fun `reportProgress_POST 到 progress 端点`() {
        server.enqueue(MockResponse().setBody("{\"playPosMs\":1000}"))
        client.reportProgress("rec", "s_abc", 92_000)

        val req = server.takeRequest()
        assertEquals("POST", req.method)
        assertEquals("/api/v1/progress/rec/s_abc", req.path)
        assertTrue(req.body.readUtf8().contains("92000"))
    }

    @Test
    fun `health_解析并附带 Bearer`() {
        server.enqueue(
            MockResponse().setBody(
                """{"status":"ok","version":"1.0","schemaVersion":1,
                   "sources":[{"id":"rec","reachable":true,"itemCount":111}],
                   "capabilities":{"ffmpeg":true}}"""
            )
        )
        val health = client.health()
        assertEquals("ok", health.status)
        assertEquals(1, health.sources.size)
        assertEquals(111, health.sources[0].itemCount)
        assertEquals("Bearer s3cret", server.takeRequest().getHeader("Authorization"))
    }

    @Test
    fun `authState_带 Bearer 发_探活即可验令牌`() {
        server.enqueue(MockResponse().setBody("""{"authRequired":true,"authenticated":true}"""))
        val auth = client.authState()
        assertTrue(auth.authRequired)
        assertTrue(auth.authenticated)
        assertEquals("Bearer s3cret", server.takeRequest().getHeader("Authorization"))
    }

    @Test
    fun `library_解析聚合页_忽略未知字段`() {
        server.enqueue(
            MockResponse().setBody(
                """{"total":118,"nextOffset":2,"items":[
                   {"key":"s_abc","sourceId":"rec","layout":"single","title":"测试 01",
                    "durationMs":12000,"width":2560,"height":1376,"sizeBytes":15600000,
                    "posterUrl":"/poster/rec/s_abc.jpg","status":"ok",
                    "sources":["rec"],"preferred":"rec",
                    "someFutureField":123}
                   ]}"""
            )
        )
        val page = client.library(0, 60)
        assertEquals(118, page.total)
        assertEquals(2, page.nextOffset)
        val item = page.items[0]
        assertEquals("s_abc", item.key)
        assertEquals("rec", item.sourceId)
        assertEquals(2560, item.width)
        assertEquals(listOf("rec"), item.sources)
        // 请求路径与分页参数
        val path = server.takeRequest().path!!
        assertTrue(path.startsWith("/api/v1/library?offset=0&limit=60"))
    }

    @Test
    fun `404_映射为 ApiException 的契约 code`() {
        server.enqueue(
            MockResponse().setResponseCode(404).setBody(
                """{"code":"source_not_found","message":"源不存在或不可见"}"""
            )
        )
        val e = assertThrows(ApiException::class.java) { client.library() }
        assertEquals(404, e.httpStatus)
        assertEquals("source_not_found", e.code)
        assertEquals("源不存在或不可见", e.message)
    }

    @Test
    fun `mediaUrl_相对路径拼 base 并补 token`() {
        val c = ApiClient("http://h:8080", "s3cret", OkHttpClient())
        assertEquals(
            "http://h:8080/poster/rec/s_abc.jpg?token=s3cret",
            c.mediaUrl("/poster/rec/s_abc.jpg"),
        )
        assertEquals(
            "http://h:8080/media/rec/s_abc/file?token=s3cret",
            c.mediaUrl("/media/rec/s_abc/file"),
        )
    }

    @Test
    fun `mediaUrl_源门户 base 带路径前缀_端点相对路径不受影响`() {
        // 服务端门户剥前缀后端点看到的仍是 /api/v1/... /media/...；
        // 客户端把前缀留在 base 里，拼接天然正确
        val mockBase = server.url("/s/movie").toString().trimEnd('/')
        val c = ApiClient(mockBase, "s3cret", OkHttpClient())
        assertEquals(
            "$mockBase/media/rec/s_abc/file?token=s3cret",
            c.mediaUrl("/media/rec/s_abc/file"),
        )
        server.enqueue(MockResponse().setBody("""{"total":0,"items":[]}"""))
        c.library()
        assertEquals("/s/movie/api/v1/library?offset=0&limit=60", server.takeRequest().path!!)
    }

    @Test
    fun `mediaUrl_已带 query 用 连接`() {
        val c = ApiClient("http://h:8080", "t", OkHttpClient())
        assertEquals("http://h:8080/x?a=1&token=t", c.mediaUrl("/x?a=1"))
        assertEquals("http://h:8080/x", ApiClient("http://h:8080", null, OkHttpClient()).mediaUrl("/x"))
    }

    @Test
    fun `singleStreamUrl_只认 single 且 delivery 含 direct`() {
        val c = ApiClient("http://h:8080", "s3cret", OkHttpClient())
        val single = io.github.garfieldgod.mediarack.core.remote.MediaDetail(
            key = "s_abc", sourceId = "rec", layout = "single", title = "t", status = "ok",
            delivery = listOf("direct", "remux"),
            tracks = listOf(
                io.github.garfieldgod.mediarack.core.remote.Track(role = "video", url = "/media/rec/s_abc/file")
            ),
        )
        assertEquals("http://h:8080/media/rec/s_abc/file?token=s3cret", c.singleStreamUrl(single))

        val dual = single.copy(layout = "dual")
        assertNull(client.singleStreamUrl(dual))

        val noDirect = single.copy(delivery = listOf("remux"))
        assertNull(client.singleStreamUrl(noDirect))
    }

    @Test
    fun `dualStreamUrl_dual_direct 返回视频音频两条补 token`() {
        val c = ApiClient("http://h:8080", "s3cret", OkHttpClient())
        val dual = io.github.garfieldgod.mediarack.core.remote.MediaDetail(
            key = "1_c2", sourceId = "bili", layout = "dual", title = "t", status = "ok",
            delivery = listOf("direct"),
            tracks = listOf(
                io.github.garfieldgod.mediarack.core.remote.Track(role = "video", url = "/media/bili/1_c2/video.m4s"),
                io.github.garfieldgod.mediarack.core.remote.Track(role = "audio", url = "/media/bili/1_c2/audio.m4s"),
            ),
        )
        val (video, audio) = c.dualStreamUrls(dual)!!
        assertEquals("http://h:8080/media/bili/1_c2/video.m4s?token=s3cret", video)
        assertEquals("http://h:8080/media/bili/1_c2/audio.m4s?token=s3cret", audio)
    }

    @Test
    fun `dualStreamUrl_条件不满足返回 null`() {
        val c = ApiClient("http://h:8080", "s3cret", OkHttpClient())
        val base = io.github.garfieldgod.mediarack.core.remote.MediaDetail(
            key = "1_c2", sourceId = "bili", layout = "dual", title = "t", status = "ok",
            delivery = listOf("direct"),
            tracks = listOf(
                io.github.garfieldgod.mediarack.core.remote.Track(role = "video", url = "/v.m4s"),
                io.github.garfieldgod.mediarack.core.remote.Track(role = "audio", url = "/a.m4s"),
            ),
        )
        // single 不是双流
        assertNull(c.dualStreamUrls(base.copy(layout = "single")))
        // 没有 direct（比如 4K 需要 remux 的设备）
        assertNull(c.dualStreamUrls(base.copy(delivery = listOf("remux"))))
        // 缺音频轨
        assertNull(
            c.dualStreamUrls(
                base.copy(tracks = listOf(io.github.garfieldgod.mediarack.core.remote.Track(role = "video", url = "/v.m4s")))
            )
        )
    }

    @Test
    fun `缓存页_round trip_保持契约形状`() {
        val c = ApiClient("http://h:8080", null, OkHttpClient())
        val page = io.github.garfieldgod.mediarack.core.remote.MediaPage(
            total = 2,
            items = listOf(
                io.github.garfieldgod.mediarack.core.remote.MediaSummary(
                    key = "s_abc", sourceId = "rec", layout = "single", title = "t",
                    durationMs = 12000, posterUrl = "/poster/rec/s_abc.jpg", status = "ok",
                )
            ),
            nextOffset = null,
        )
        val restored = c.parseCachedPage(c.encodePage(page))!!
        assertEquals(page.total, restored.total)
        assertEquals(page.items[0].key, restored.items[0].key)
        // 缓存里 posterUrl 保持相对路径（渲染时再按当前 base 补 token）
        assertEquals("/poster/rec/s_abc.jpg", restored.items[0].posterUrl)
        // 坏 JSON 安全失败
        assertNull(c.parseCachedPage("not json"))
    }
}
